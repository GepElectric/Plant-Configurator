using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Eplan;

/// <summary>A schematic page of an EPLAN export with its relay devices (for choosing what to import).</summary>
public sealed record EplanSchematicPage(int Index, string Function, string Location, string Name, string Description, int Coils, int Contacts)
{
    public string Title => $"={Function}+{Location}/{Name}  {Description}".Trim();
}

/// <summary>Relay-logic sheets and selector switches built from EPLAN pages, with what could not be read.</summary>
public sealed class EplanRelayImport
{
    public List<RelaySheet> Sheets { get; } = [];
    public List<RelaySwitch> Switches { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Builds relay logic from the schematic pages of an EPLAN Electric P8 PXF export (.epj). The export has no wires:
/// EPLAN draws them by autoconnect, so they are rebuilt the same way — every connection point connects to the nearest
/// point straight in its direction (up/down/left/right) that faces it. Junctions (T-nodes, corners), connection
/// definition points, terminals and potential points join their wires. The pins of a symbol are not exported either:
/// they come from <see cref="Symbols"/> (offsets from the symbol origin, in mm). Each relay coil's path from the supply
/// potential (L…) to its A1 is reduced to series / parallel contacts; paths sharing their start become branches.
/// </summary>
/// <remarks>
/// O17 = function (P150: P20012 prefix, P20013 letters, P20014 number; A1261/A1262/A1263 symbol library/number/variant;
/// A1381 function category: 200 coil, 203 changeover contact, 600 selector switch, 100 terminal; A182 = 1 main function;
/// A185 = connection point designations; P11/P20011 function text, P20027 coil text, P20901_10.. switch positions),
/// S40x1201/A762 = "x/y" position in mm (y up), O42 = junction (SPECIAL 0/64, 0/66, 0/67, 0/72 T-nodes, 0/70 corners),
/// O59 = connection definition point, O70 = potential point (P150 name, e.g. -L2230VAC, -N0VAC).
/// </remarks>
public static class EplanRelayImporter
{
    private const double PinLength = 8.0; // assumed height of a contact / coil symbol; only the order matters
    private const double Tolerance = 0.01;

    /// <summary>Pin of a symbol: index into A185, offset from the origin, directions it connects to (U, D, L, R).</summary>
    private sealed record PinDef(int Index, double Dx, double Dy, string Directions);

    private enum SymbolKind { Contact, Selector, Coil, MeasuringRelay }

    private sealed record SymbolDef(SymbolKind Kind, PinDef[] Pins);

    /// <summary>Known symbols (library/number/variant). Contacts: A185 = NC;COM;NO.</summary>
    private static readonly Dictionary<string, SymbolDef> Symbols = new(StringComparer.Ordinal)
    {
        ["1/1125/2"] = new(SymbolKind.Contact, [new(1, 0, 0, "U"), new(2, 0, -PinLength, "D"), new(0, 4, -PinLength, "D")]),
        ["5/35/0"] = new(SymbolKind.Selector, [new(0, 0, 0, "U"), new(1, -4, -PinLength, "D"), new(2, 4, -PinLength, "D")]),
        ["1/1110/0"] = new(SymbolKind.MeasuringRelay, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D"), new(2, 8, 0, "U"), new(3, 8, -PinLength, "D")]),
    };

    private static readonly string[] CoilSymbolNumbers = ["27", "20"];

    // ---------------------------------------------------------------- page list

    public static List<EplanSchematicPage> ListPages(string path, CancellationToken cancellationToken = default)
    {
        var result = new List<EplanSchematicPage>();
        ForEachPage(path, cancellationToken, (index, page) =>
        {
            var (function, location, name, description) = PageInfo(page);
            var devices = page.Elements("O17").ToList();
            var coils = devices.Count(device => device.Attribute("A1381")?.Value == "200");
            var contacts = devices.Count(device => device.Attribute("A1381")?.Value is "203" or "201" or "202" or "600");
            if (coils + contacts > 0)
            {
                result.Add(new EplanSchematicPage(index, function, location, name, description, coils, contacts));
            }
        });
        return result;
    }

    // ---------------------------------------------------------------- import

    public static EplanRelayImport Import(string path, IReadOnlySet<int> pages, CancellationToken cancellationToken = default)
    {
        var import = new EplanRelayImport();
        ForEachPage(path, cancellationToken, (index, page) =>
        {
            if (pages.Contains(index))
            {
                ImportPage(page, import);
            }
        });
        return import;
    }

    private static void ForEachPage(string path, CancellationToken cancellationToken, Action<int, XElement> action)
    {
        var settings = new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(path, settings);
        reader.MoveToContent();
        var index = 0;
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "O4")
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = (XElement)XNode.ReadFrom(reader);
                action(++index, page);
            }
            else
            {
                reader.Read();
            }
        }
    }

    private static (string Function, string Location, string Name, string Description) PageInfo(XElement page)
    {
        var p49 = page.Element("P49");
        var p11 = page.Element("P11");
        return (p49?.Attribute("P1100")?.Value ?? "", p49?.Attribute("P1200")?.Value ?? "", p49?.Attribute("P11012")?.Value ?? "",
            Text(p11?.Attribute("P11011")?.Value));
    }

    // ---------------------------------------------------------------- one page

    private sealed class Pin
    {
        public required double X { get; init; }
        public required double Y { get; init; }
        public required string Directions { get; init; }
        public required Device? Owner { get; init; }
        public int Index { get; init; } = -1;
        public string Potential { get; init; } = "";
        public int Net { get; set; }
    }

    private sealed class Device
    {
        public required string Id { get; init; }
        public required string Tag { get; init; }
        public required SymbolDef Symbol { get; init; }
        public required string[] Points { get; init; }
        public required string Text { get; init; }
        public required double X { get; init; }
        public List<string> Positions { get; init; } = [];
        public Dictionary<int, Pin> Pins { get; } = [];
    }

    /// <summary>A contact (or switch contact, or coil) between two nets.</summary>
    private sealed record Edge(int From, int To, Expr Expr);

    private abstract record Expr;

    private sealed record Leaf(Device Device, int CommonPin, int UsedPin) : Expr;

    private sealed record Series(List<Expr> Items) : Expr;

    private sealed record Parallel(List<Expr> Items) : Expr;

    private static void ImportPage(XElement page, EplanRelayImport import)
    {
        var (_, _, pageName, description) = PageInfo(page);
        var pins = new List<Pin>();
        var devices = new List<Device>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in page.Elements())
        {
            if (Position(item) is not { } at)
            {
                continue;
            }

            var symbol = $"{item.Attribute("A1261")?.Value}/{item.Attribute("A1262")?.Value}/{item.Attribute("A1263")?.Value}";
            var category = item.Attribute("A1381")?.Value ?? "";
            switch (item.Name.LocalName)
            {
                case "O17" when category == "100":
                    pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "LRUD", Owner = null }); // terminal: wire passes through
                    break;
                case "O17":
                    var tag = Tag(item);
                    var def = Symbols.GetValueOrDefault(symbol)
                              ?? (category == "200" && CoilSymbolNumbers.Contains(item.Attribute("A1262")?.Value) && item.Attribute("A1263")?.Value == "0"
                                  ? new SymbolDef(SymbolKind.Coil, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")])
                                  : null);
                    if (def is null)
                    {
                        // Unknown symbol (PLC card, black box…): its wires end here — a wire must not run through it.
                        pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "", Owner = null });
                        pins.Add(new Pin { X = at.X, Y = at.Y - PinLength, Directions = "", Owner = null });
                        if (category is "200" or "203" or "201" or "202" or "600" && unknown.Add(symbol))
                        {
                            import.Warnings.Add($"Page {pageName}: {tag} — symbol {symbol} is not known yet; the wires through it are not connected.");
                        }

                        continue;
                    }

                    var p11 = item.Element("P11");
                    var device = new Device
                    {
                        Id = item.Attribute("A1")?.Value ?? Guid.NewGuid().ToString("N"),
                        Tag = tag,
                        Symbol = def,
                        Points = (item.Attribute("A185")?.Value ?? "").Split(';'),
                        Text = FirstNonEmpty(Text(p11?.Attribute("P20027")?.Value), Text(p11?.Attribute("P20011")?.Value)),
                        X = at.X,
                        Positions = Enumerable.Range(10, 6).Select(i => Text(p11?.Attribute($"P20901_{i}")?.Value)).Where(text => text.Length > 0).ToList()
                    };
                    foreach (var pinDef in def.Pins)
                    {
                        var pin = new Pin { X = at.X + pinDef.Dx, Y = at.Y + pinDef.Dy, Directions = pinDef.Directions, Owner = device, Index = pinDef.Index };
                        device.Pins[pinDef.Index] = pin;
                        pins.Add(pin);
                    }

                    devices.Add(device);
                    break;
                case "O59":
                    pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "UD", Owner = null });
                    break;
                case "O70":
                    pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "LR", Owner = null, Potential = item.Element("P150")?.Attribute("P20013")?.Value ?? "" });
                    break;
                case "O42" when item.Attribute("A1261")?.Value == "0":
                    var directions = item.Attribute("A1262")?.Value switch
                    {
                        "64" => "LRD",
                        "66" => "UDR",
                        "67" => "UDL",
                        "72" => "LRU",
                        "70" => (int.TryParse(item.Attribute("A1263")?.Value, out var variant) ? variant : 0) switch { 1 => "UR", 2 => "UL", 3 => "LD", _ => "RD" },
                        _ => ""
                    };
                    if (directions.Length > 0)
                    {
                        pins.Add(new Pin { X = at.X, Y = at.Y, Directions = directions, Owner = null });
                    }

                    break;
            }
        }

        // Autoconnect, then nets (union-find); junctions and terminals are single pins, so their wires join.
        var parent = Enumerable.Range(0, pins.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var connected = new HashSet<Pin>();
        for (var i = 0; i < pins.Count; i++)
        {
            foreach (var direction in pins[i].Directions)
            {
                if (Nearest(pins, i, direction) is { } j && pins[j].Directions.Contains(Opposite(direction)))
                {
                    parent[Find(i)] = Find(j);
                    connected.Add(pins[i]);
                    connected.Add(pins[j]);
                }
            }
        }

        foreach (var (pin, i) in pins.Select((pin, i) => (pin, i)))
        {
            pin.Net = Find(i);
        }

        var supply = pins.Where(pin => IsSupply(pin.Potential)).Select(pin => pin.Net).Distinct().ToList();
        if (supply.Count == 0)
        {
            import.Warnings.Add($"Page {pageName}: no supply potential point (L, L+…) on the page — the coils get a contact without a source (e.g. a PLC output).");
        }

        // Contacts as edges top → bottom; a changeover contributes the sides that are wired.
        var edges = new List<Edge>();
        var coils = new List<(Device Device, int A1, int A2)>();
        foreach (var device in devices)
        {
            switch (device.Symbol.Kind)
            {
                case SymbolKind.Contact:
                    foreach (var used in new[] { 2, 0 })
                    {
                        if (device.Pins.TryGetValue(1, out var common) && device.Pins.TryGetValue(used, out var output)
                            && connected.Contains(common) && connected.Contains(output))
                        {
                            edges.Add(new Edge(common.Net, output.Net, new Leaf(device, 1, used)));
                        }
                    }

                    break;
                case SymbolKind.Selector:
                    foreach (var used in new[] { 1, 2 })
                    {
                        if (device.Pins.TryGetValue(0, out var common) && device.Pins.TryGetValue(used, out var output) && connected.Contains(output))
                        {
                            edges.Add(new Edge(common.Net, output.Net, new Leaf(device, 0, used)));
                        }
                    }

                    break;
                case SymbolKind.Coil:
                    coils.Add((device, device.Pins[0].Net, device.Pins[1].Net));
                    break;
                case SymbolKind.MeasuringRelay:
                    coils.Add((device, device.Pins[2].Net, device.Pins[3].Net));
                    break;
            }
        }

        var sheet = new RelaySheet
        {
            Name = description.Length > 0 ? description.Replace('\n', ' ') : "Page " + pageName,
            Page = int.TryParse(pageName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0
        };
        var built = new List<(List<RelayElement> Elements, List<string> Signatures)>();
        foreach (var (coil, a1, _) in coils.OrderBy(coil => coil.Device.X))
        {
            var expr = supply.Select(net => Reduce(edges, net, a1)).FirstOrDefault(found => found is not null);
            List<string> signatures;
            List<RelayElement> elements;
            if (expr is null || expr is Series { Items.Count: 0 } && !supply.Contains(a1))
            {
                // Fed by something not readable from the page (PLC output, other page, unknown symbol):
                // one contact without a source, to be connected by hand.
                import.Warnings.Add($"Page {pageName}: coil {coil.Tag} — its supply is not readable from the page (PLC output, other page or unknown symbol); a contact without a source was put in its path.");
                signatures = [];
                elements = [new RelayElement { Tag = "", Contact = "NO", Text = "SUPPLY ?\n(set the source)" }];
            }
            else
            {
                var series = Flatten(expr);
                signatures = series.Select(Signature).ToList();
                elements = series.Select(item => ToElement(item, import)).ToList();
            }
            var circuit = new RelayCircuit { Coil = new RelayCoil { Tag = coil.Tag, Text = coil.Text } };

            // A path that starts like one already built branches off it below the last shared contact.
            var best = built.Select(other => (other, shared: SharedPrefix(other.Signatures, signatures))).OrderByDescending(pair => pair.shared).FirstOrDefault();
            if (best.shared > 0)
            {
                circuit.Feed = best.other.Elements[best.shared - 1].Id;
                circuit.Elements = elements.Skip(best.shared).ToList();
            }
            else
            {
                circuit.Elements = elements;
            }

            sheet.Circuits.Add(circuit);
            if (signatures.Count > 0)
            {
                built.Add((elements, signatures));
            }
        }

        if (sheet.Circuits.Count > 0)
        {
            import.Sheets.Add(sheet);
        }
    }

    /// <summary>Series / parallel reduction of the contacts between the supply net and a coil's A1.</summary>
    private static Expr? Reduce(List<Edge> all, int source, int target)
    {
        // Only contacts on some path source → target.
        var forward = Reach(all, source, edge => edge.From, edge => edge.To);
        var backward = Reach(all, target, edge => edge.To, edge => edge.From);
        var edges = all.Where(edge => forward.Contains(edge.From) && forward.Contains(edge.To) && backward.Contains(edge.From) && backward.Contains(edge.To)).ToList();
        if (source == target)
        {
            return new Series([]);
        }

        var changed = true;
        while (changed && edges.Count > 1)
        {
            changed = false;
            foreach (var group in edges.GroupBy(edge => (edge.From, edge.To)).Where(group => group.Count() > 1))
            {
                edges.RemoveAll(group.Contains);
                edges.Add(new Edge(group.Key.From, group.Key.To, new Parallel(group.Select(edge => edge.Expr).ToList())));
                changed = true;
                break;
            }

            if (changed)
            {
                continue;
            }

            foreach (var node in edges.SelectMany(edge => new[] { edge.From, edge.To }).Distinct())
            {
                if (node == source || node == target)
                {
                    continue;
                }

                var incoming = edges.Where(edge => edge.To == node).ToList();
                var outgoing = edges.Where(edge => edge.From == node).ToList();
                if (incoming.Count == 1 && outgoing.Count == 1)
                {
                    edges.Remove(incoming[0]);
                    edges.Remove(outgoing[0]);
                    edges.Add(new Edge(incoming[0].From, outgoing[0].To, new Series([incoming[0].Expr, outgoing[0].Expr])));
                    changed = true;
                    break;
                }
            }
        }

        return edges.Count == 1 && edges[0].From == source && edges[0].To == target ? edges[0].Expr : null;
    }

    private static HashSet<int> Reach(List<Edge> edges, int start, Func<Edge, int> from, Func<Edge, int> to)
    {
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>([start]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var edge in edges.Where(edge => from(edge) == node))
            {
                if (seen.Add(to(edge)))
                {
                    queue.Enqueue(to(edge));
                }
            }
        }

        return seen;
    }

    private static List<Expr> Flatten(Expr expr) => expr switch
    {
        Series series => series.Items.SelectMany(Flatten).ToList(),
        _ => [expr]
    };

    private static RelayElement ToElement(Expr expr, EplanRelayImport import)
    {
        switch (expr)
        {
            case Parallel parallel:
                return new RelayElement
                {
                    Kind = RelayElementKind.Parallel,
                    Branches = Branches(parallel).Select(item => Flatten(item).Select(inner => ToElement(inner, import)).ToList()).ToList()
                };
            case Leaf { Device.Symbol.Kind: SymbolKind.Selector } leaf:
            {
                var device = leaf.Device;
                var positions = device.Positions.Count >= 2 ? device.Positions : [device.Points.ElementAtOrDefault(1) ?? "1", "0", device.Points.ElementAtOrDefault(2) ?? "2"];
                var position = leaf.UsedPin == 1 ? positions[0] : positions[^1];
                if (!import.Switches.Any(item => item.Tag.Equals(device.Tag, StringComparison.OrdinalIgnoreCase)))
                {
                    import.Switches.Add(new RelaySwitch { Tag = device.Tag, Text = device.Text, Positions = string.Join("/", positions), Position = positions[0] });
                }

                var other = leaf.UsedPin == 1 ? 2 : 1;
                return new RelayElement
                {
                    Tag = device.Tag,
                    Contact = "NO",
                    Position = position,
                    Pins = $"{Point(device, 0)}/{Point(device, other)}/{Point(device, leaf.UsedPin)}",
                    Text = device.Text
                };
            }
            case Leaf leaf:
            {
                var device = leaf.Device;
                return new RelayElement
                {
                    Tag = device.Tag,
                    Contact = leaf.UsedPin == 0 ? "NC" : "NO",
                    Pins = $"{Point(device, 1)}/{Point(device, 0)}/{Point(device, 2)}",
                    Text = device.Text
                };
            }
            default:
                return new RelayElement { Kind = RelayElementKind.Parallel, Branches = [Flatten(expr).Select(inner => ToElement(inner, import)).ToList()] };
        }
    }

    /// <summary>Branches of a parallel group; a parallel group directly inside another adds its branches.</summary>
    private static IEnumerable<Expr> Branches(Parallel parallel) =>
        parallel.Items.SelectMany(item => item is Parallel inner ? Branches(inner) : [item]);

    private static string Signature(Expr expr) => expr switch
    {
        Leaf leaf => $"{leaf.Device.Id}:{leaf.CommonPin}-{leaf.UsedPin}",
        Parallel parallel => "(" + string.Join("|", Branches(parallel).Select(Signature).Order(StringComparer.Ordinal)) + ")",
        Series series => string.Join(">", series.Items.Select(Signature)),
        _ => ""
    };

    private static int SharedPrefix(List<string> a, List<string> b)
    {
        var count = 0;
        while (count < a.Count && count < b.Count && a[count] == b[count])
        {
            count++;
        }

        // Keep at least one element of its own on the branch when the whole path is shared.
        return Math.Max(0, Math.Min(count, b.Count - 1));
    }

    private static int? Nearest(List<Pin> pins, int index, char direction)
    {
        var a = pins[index];
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var j = 0; j < pins.Count; j++)
        {
            if (j == index)
            {
                continue;
            }

            var b = pins[j];
            double distance;
            if (direction is 'U' or 'D')
            {
                if (Math.Abs(b.X - a.X) > Tolerance)
                {
                    continue;
                }

                distance = direction == 'U' ? b.Y - a.Y : a.Y - b.Y;
            }
            else
            {
                if (Math.Abs(b.Y - a.Y) > Tolerance)
                {
                    continue;
                }

                distance = direction == 'R' ? b.X - a.X : a.X - b.X;
            }

            if (distance > Tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = j;
            }
        }

        return best;
    }

    private static char Opposite(char direction) => direction switch { 'U' => 'D', 'D' => 'U', 'L' => 'R', _ => 'L' };

    private static (double X, double Y)? Position(XElement item)
    {
        var text = item.Element("S40x1201")?.Attribute("A762")?.Value;
        if (text is null)
        {
            return null;
        }

        var parts = text.Split('/');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;
    }

    /// <summary>Device tag from P150: "2-K03", "-K101", "-X60L".</summary>
    private static string Tag(XElement item)
    {
        var tag = item.Element("P150");
        return tag is null ? "" : $"{tag.Attribute("P20012")?.Value}-{tag.Attribute("P20013")?.Value}{tag.Attribute("P20014")?.Value}".Replace("\n", "");
    }

    /// <summary>
    /// Supply potential by its letters (P20013): L, L1…L3, L+ or + (e.g. -L2230VAC, 11-L+110VDC); L-, N and M are the
    /// return.
    /// </summary>
    private static bool IsSupply(string letters)
    {
        var name = letters.Trim();
        return (name.StartsWith('L') && !name.StartsWith("L-", StringComparison.Ordinal)) || name.StartsWith('+');
    }

    private static string Point(Device device, int index) => device.Points.ElementAtOrDefault(index) ?? "";

    /// <summary>Multi-language EPLAN text "en_US@…;hr_HR@…;" → English, else the first language.</summary>
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (!value.Contains('@'))
        {
            return value.Trim();
        }

        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('@', 2))
            .Where(part => part.Length == 2)
            .ToList();
        return (parts.FirstOrDefault(part => part[0] == "en_US") ?? parts.FirstOrDefault())?[1].Trim() ?? "";
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? "";
}
