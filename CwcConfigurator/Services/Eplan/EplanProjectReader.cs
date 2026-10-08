using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CwcConfigurator.Services.Eplan;

/// <summary>
/// Reads PLC hardware (stations, modules, channels) from an EPLAN Electric P8 PXF export (.epj).
/// No EPLAN installation is needed: the export is plain XML and is streamed page by page.
/// </summary>
/// <remarks>
/// Property ids used (EPLAN P8 2.9): P1100 function (=), P1200 location (+), P20013/P20014 device tag letters/number,
/// P20012 device tag prefix, P20400 PLC address, P20407 channel, P20901_21/22 part number/type of a PLC card,
/// P11011 page description, P11012 page name. A185/A186 on a function are connection point designations/descriptions.
/// Wires are drawn by EPLAN's autoconnect and are not exported, so "connected to" is derived from the
/// device placed in the same path (column) as the PLC terminal.
/// </remarks>
public static class EplanProjectReader
{
    private const double PathWidth = 48.0;     // A3 schematic: 8 paths of 48 mm starting at x = 20 mm
    private const double PathOrigin = 20.0;

    private static readonly Regex MultiLanguagePart = new(@"([a-z]{2}_[A-Z]{2}|\?\?_\?\?)@(.*?)(?:;|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex MainChannelDescription = new(@"^(?:DI|DQ|I|M|Q)(\d+)\+?$", RegexOptions.Compiled);
    private static readonly Regex ChannelNumber = new(@"^(?:IN|OUT)(\d+)$", RegexOptions.Compiled);

    public static EplanProject Read(string path, CancellationToken cancellationToken = default)
    {
        var pages = new List<EplanPage>();
        var cards = new Dictionary<(string Fn, string Dt), Card>();
        var points = new List<Point>();
        var projectName = "";

        var settings = new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using (var reader = XmlReader.Create(path, settings))
        {
            reader.MoveToContent();
            projectName = reader.GetAttribute("Name") ?? "";
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "O4")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = (XElement)XNode.ReadFrom(reader);
                    ReadPage(page, pages.Count + 1, pages, cards, points);
                }
                else
                {
                    reader.Read();
                }
            }
        }

        var globalPage = pages.ToDictionary(p => (p.Function, p.Name), p => p.GlobalNumber);
        return new EplanProject
        {
            SourcePath = path,
            ProjectName = projectName,
            Pages = pages,
            Stations = BuildStations(cards, points, globalPage)
        };
    }

    // ---------------------------------------------------------------- page scan

    private sealed class Card
    {
        public string Fn = "";
        public string Dt = "";
        public string PartNumber = "";
        public string Type = "";
        public (int GlobalPage, double Row, double X)? ConfigPosition;   // where the card is drawn on PLC CONFIGURATION
    }

    private sealed class Point
    {
        public string Fn = "";
        public string Dt = "";
        public string Terminal = "";
        public string Description = "";
        public string Address = "";
        public string Channel = "";
        public string Signal = "";
        public string ConnectedTo = "";
        public string? Group;
        public string Page = "";
        public string PageFn = "";
        public int Column;
        public bool IsOverview;
    }

    private static void ReadPage(XElement page, int globalNumber, List<EplanPage> pages,
        Dictionary<(string, string), Card> cards, List<Point> points)
    {
        var p49 = page.Element("P49");
        var pageFn = p49?.Attribute("P1100")?.Value ?? "";
        var pageName = p49?.Attribute("P11012")?.Value ?? "";
        var description = Text(page.Element("P11")?.Attribute("P11011")?.Value);
        pages.Add(new EplanPage { GlobalNumber = globalNumber, Function = pageFn, Name = pageName, Description = description });

        var upper = description.ToUpperInvariant();
        var isOverview = upper.Contains("OVERVIEW");
        var isConfig = upper.Contains("CONFIGURATION");

        var texts = page.Elements("O83")
            .Select(o => (Pos: ParseXy(o.Attribute("A501")?.Value), Text: Text(o.Attribute("A511")?.Value)))
            .Where(t => t.Pos is not null)
            .Select(t => (X: t.Pos!.Value.X, t.Text))
            .ToList();

        var functions = page.Descendants("O17")
            .Select(o => (Element: o, Pos: Position(o)))
            .ToList();
        var placed = functions.Where(f => f.Pos is not null).Select(f => (f.Element, Pos: f.Pos!.Value)).ToList();

        foreach (var (o, pos) in functions)
        {
            var p11 = o.Element("P11");
            var p150 = o.Element("P150");
            var fn = p150?.Attribute("P1100")?.Value ?? pageFn;
            var dt = (p150?.Attribute("P20013")?.Value ?? "") + (p150?.Attribute("P20014")?.Value ?? "");

            var partNumber = Text(p11?.Attribute("P20901_21")?.Value);
            if (partNumber.StartsWith("6ES7", StringComparison.Ordinal))
            {
                if (!cards.TryGetValue((fn, dt), out var card))
                {
                    card = new Card { Fn = fn, Dt = dt, PartNumber = partNumber, Type = Text(p11?.Attribute("P20901_22")?.Value) };
                    cards[(fn, dt)] = card;
                }

                if (isConfig && pos is not null)
                {
                    card.ConfigPosition = (globalNumber, Math.Floor(pos.Value.Y / 100), pos.Value.X);
                }
            }

            if (isConfig || (p11?.Attribute("P20400") is null && p11?.Attribute("P20407") is null))
            {
                continue;       // rack picture on PLC CONFIGURATION carries no channel data
            }

            var parent = o.Parent;
            points.Add(new Point
            {
                Fn = fn,
                Dt = dt,
                Terminal = o.Attribute("A185")?.Value ?? "",
                Description = o.Attribute("A186")?.Value ?? "",
                Address = p11?.Attribute("P20400")?.Value ?? "",
                Channel = p11?.Attribute("P20407")?.Value ?? "",
                Signal = pos is null ? "" : SignalText(texts, pos.Value.X),
                ConnectedTo = pos is null || isOverview ? "" : ConnectedDevice(o, pos.Value, placed, pageFn),
                Group = parent?.Name.LocalName == "O26" ? parent.Attribute("A1")?.Value : null,
                Page = pageName,
                PageFn = pageFn,
                Column = pos is null ? 0 : PathOf(pos.Value.X),
                IsOverview = isOverview
            });
        }
    }

    /// <summary>Path (column) text above the terminal; AI/AO groups share one text placed at the group's first terminal.</summary>
    private static string SignalText(List<(double X, string Text)> texts, double x)
    {
        var near = texts.Where(t => Math.Abs(t.X - x) <= 20).OrderBy(t => Math.Abs(t.X - x)).FirstOrDefault();
        if (near.Text is not null)
        {
            return near.Text;
        }

        var left = texts.Where(t => t.X <= x + 2 && x - t.X < 30).OrderByDescending(t => t.X).FirstOrDefault();
        return left.Text ?? "";
    }

    /// <summary>Nearest device in the same path above the PLC terminal (inputs), otherwise below (outputs).</summary>
    private static string ConnectedDevice(XElement self, (double X, double Y) pos,
        List<(XElement Element, (double X, double Y) Pos)> placed, string pageFn)
    {
        var sameColumn = placed
            .Where(f => f.Element != self && Math.Abs(f.Pos.X - pos.X) < 0.6)
            .Where(f =>
            {
                var letter = f.Element.Element("P150")?.Attribute("P20013")?.Value;
                return !string.IsNullOrEmpty(letter) && letter != "A";
            })
            .ToList();

        var above = sameColumn.Where(f => f.Pos.Y > pos.Y).OrderBy(f => f.Pos.Y).FirstOrDefault();
        var below = sameColumn.Where(f => f.Pos.Y < pos.Y).OrderByDescending(f => f.Pos.Y).FirstOrDefault();
        var neighbour = above.Element ?? below.Element;
        if (neighbour is null)
        {
            return "";
        }

        var pin = PinOf(neighbour);
        return DeviceTagText(neighbour, pageFn) + (pin.Length > 0 ? ":" + pin : "");
    }

    private static string DeviceTagText(XElement o, string pageFn)
    {
        var p150 = o.Element("P150");
        if (p150 is null)
        {
            return "";
        }

        var tag = "-" + (p150.Attribute("P20012")?.Value ?? "") + (p150.Attribute("P20013")?.Value ?? "") + (p150.Attribute("P20014")?.Value ?? "");
        var fn = p150.Attribute("P1100")?.Value;
        return !string.IsNullOrEmpty(fn) && fn != pageFn ? "=" + fn + tag : tag;
    }

    /// <summary>Connection point of the neighbour facing the PLC terminal, as EPLAN shows it (e.g. 14 on a change-over contact).</summary>
    private static string PinOf(XElement o)
    {
        var terminalNumber = o.Element("P11")?.Attribute("P20030")?.Value;
        if (!string.IsNullOrEmpty(terminalNumber))
        {
            return terminalNumber;
        }

        var designations = o.Attribute("A185")?.Value ?? "";
        return designations switch
        {
            "12;11;14" => "14",
            "14;13" => "14",
            "13;14" => "13",
            _ => designations.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""
        };
    }

    // ---------------------------------------------------------------- assembly

    private static List<EplanStation> BuildStations(Dictionary<(string, string), Card> cards, List<Point> points,
        Dictionary<(string, string), int> globalPage)
    {
        // PLC terminals inside a channel macro may carry a foreign device tag; attach them to the card of their macro
        var groupDt = new Dictionary<string, string>();
        foreach (var p in points.Where(p => p.Group is not null && cards.ContainsKey((p.Fn, p.Dt))))
        {
            groupDt[p.Group!] = p.Dt;
        }

        foreach (var p in points.Where(p => !cards.ContainsKey((p.Fn, p.Dt)) && p.Group is not null && groupDt.ContainsKey(p.Group)))
        {
            p.Dt = groupDt[p.Group!];
            p.Description = "";     // auxiliary terminal of a channel (M-, IC+, UV...) – never a main channel
        }

        var placedCards = cards.Values.Where(c => c.ConfigPosition is not null).ToList();
        var stations = new List<EplanStation>();
        foreach (var fnGroup in placedCards.GroupBy(c => c.Fn).OrderBy(g => g.Min(c => c.ConfigPosition!.Value.GlobalPage)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = fnGroup.OrderBy(c => c.ConfigPosition!.Value.GlobalPage).ThenBy(c => c.ConfigPosition!.Value.Row).ThenBy(c => c.ConfigPosition!.Value.X).ToList();
            var head = ordered.FirstOrDefault(c => KindOf(c.Type) is null);
            var modules = ordered
                .Where(c => KindOf(c.Type) is not null)
                .Select(c => BuildModule(c, KindOf(c.Type)!.Value, points, globalPage))
                .ToList();
            if (head is null && modules.Count == 0)
            {
                continue;
            }

            stations.Add(new EplanStation
            {
                Function = fnGroup.Key,
                HeadDeviceTag = head?.Dt ?? "",
                HeadPartNumber = head?.PartNumber ?? "",
                HeadType = head?.Type ?? "",
                IsCpu = head?.Type.Contains("CPU", StringComparison.OrdinalIgnoreCase) == true,
                Modules = modules
            });
        }

        return stations;
    }

    private static EplanModule BuildModule(Card card, EplanModuleKind kind, List<Point> points, Dictionary<(string, string), int> globalPage)
    {
        var own = points.Where(p => p.Fn == card.Fn && p.Dt == card.Dt).ToList();
        var channels = new List<EplanChannel>();

        foreach (var terminal in own.GroupBy(p => p.Terminal))
        {
            var reps = terminal.ToList();
            var description = reps.Select(p => p.Description).FirstOrDefault(d => d.Length > 0) ?? "";
            int? port = null;
            var main = MainChannelDescription.Match(description);
            if (main.Success)
            {
                port = int.Parse(main.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (kind == EplanModuleKind.Counter)
            {
                var ch = reps.Select(p => ChannelNumber.Match(p.Channel)).FirstOrDefault(m => m.Success);
                if (ch is null || description is "M" or "L+")
                {
                    continue;
                }
            }

            var isAnalog = kind is EplanModuleKind.AnalogInput or EplanModuleKind.RtdInput or EplanModuleKind.AnalogOutput;
            if (kind != EplanModuleKind.Counter && port is null)
            {
                continue;       // supply (L+/M) and auxiliary terminals are not channels
            }

            if (isAnalog && !description.EndsWith('+'))
            {
                continue;       // one row per analog channel: the + terminal
            }

            var schematic = reps.FirstOrDefault(p => !p.IsOverview);
            var overview = reps.FirstOrDefault(p => p.IsOverview);
            channels.Add(new EplanChannel
            {
                Port = kind == EplanModuleKind.Counter ? null : port,
                Terminal = terminal.Key,
                ConnectionDescription = description,
                Address = schematic?.Address is { Length: > 0 } a ? a : overview?.Address ?? "",
                SignalName = reps.Where(p => !p.IsOverview).Select(p => p.Signal).FirstOrDefault(s => s.Length > 0) ?? "",
                ConnectedTo = reps.Select(p => p.ConnectedTo).FirstOrDefault(s => s.Length > 0) ?? "",
                Schematic = Ref(schematic, globalPage),
                Overview = Ref(overview, globalPage)
            });
        }

        return new EplanModule
        {
            Function = card.Fn,
            DeviceTag = card.Dt,
            PartNumber = card.PartNumber,
            Type = card.Type,
            Kind = kind,
            Channels = channels
                .OrderBy(c => c.Port ?? int.MaxValue)
                .ThenBy(c => int.TryParse(c.Terminal, out var t) ? t : int.MaxValue)
                .ToList()
        };
    }

    private static EplanPageRef? Ref(Point? p, Dictionary<(string, string), int> globalPage)
    {
        if (p is null)
        {
            return null;
        }

        return new EplanPageRef
        {
            Function = p.PageFn,
            Page = p.Page,
            Column = p.Column,
            GlobalPage = globalPage.TryGetValue((p.PageFn, p.Page), out var g) ? g : 0
        };
    }

    /// <summary>Module kind from the card type text; null for CPUs, interface modules and anything else.</summary>
    public static EplanModuleKind? KindOf(string type)
    {
        var t = type.Trim().ToUpperInvariant();
        if (t.StartsWith("DI", StringComparison.Ordinal)) return EplanModuleKind.DigitalInput;
        if (t.StartsWith("DQ", StringComparison.Ordinal)) return EplanModuleKind.DigitalOutput;
        if (t.StartsWith("AI", StringComparison.Ordinal)) return t.Contains("RTD") ? EplanModuleKind.RtdInput : EplanModuleKind.AnalogInput;
        if (t.StartsWith("AQ", StringComparison.Ordinal)) return EplanModuleKind.AnalogOutput;
        if (t.Contains("COUNT")) return EplanModuleKind.Counter;
        return null;
    }

    // ---------------------------------------------------------------- helpers

    private static int PathOf(double x) => Math.Clamp((int)Math.Floor((x - PathOrigin) / PathWidth) + 1, 1, 8);

    private static (double X, double Y)? Position(XElement o) => ParseXy(o.Element("S40x1201")?.Attribute("A762")?.Value);

    private static (double X, double Y)? ParseXy(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        return (x, y);
    }

    /// <summary>EPLAN multilingual string ("hr_HR@..;en_US@..;") to plain text: hr_HR, then neutral, then en_US.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var parts = new Dictionary<string, string>();
        foreach (Match m in MultiLanguagePart.Matches(value))
        {
            parts.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }

        var text = parts.Count == 0
            ? value
            : new[] { "hr_HR", "??_??", "en_US" }.Select(k => parts.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v))
              ?? parts.Values.First();
        return Whitespace.Replace(text, " ").Trim();
    }
}
