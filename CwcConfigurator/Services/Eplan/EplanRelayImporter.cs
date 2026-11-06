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

/// <summary>
/// A connection point of a PLC card on the schematics (-A611 terminal 1 "DI0", address I0.0): the end of a path
/// (input) or its start (output), until it is connected to the DI / DO of the simulator.
/// </summary>
public sealed record EplanPlcPoint(string Card, string Terminal, int Port, bool Output, string Address);

/// <summary>Relay-logic sheets and selector switches built from EPLAN pages, with what could not be read.</summary>
public sealed class EplanRelayImport
{
    /// <summary>PLC card points used on the sheets, by their tag on the sheets ("-A611:DI0", "-A631:DQ0").</summary>
    public Dictionary<string, EplanPlcPoint> PlcPoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RelaySheet> Sheets { get; } = [];
    public List<RelaySwitch> Switches { get; } = [];

    /// <summary>Operator devices of the whole project for a cubicle door: push buttons, mushrooms, switches, lamps.</summary>
    public List<CubicleItem> CubicleDevices { get; } = [];

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
/// O59 = connection definition point (on a wire, ignored), O70 = potential point (P150 name, e.g. -L2230VAC, -N0VAC).
/// </remarks>
public static class EplanRelayImporter
{
    private const double PinLength = 8.0; // assumed height of a contact / coil symbol; only the order matters
    private const double Tolerance = 0.01;

    /// <summary>Pin of a symbol: index into A185, offset from the origin, directions it connects to (U, D, L, R).</summary>
    private sealed record PinDef(int Index, double Dx, double Dy, string Directions);

    private enum SymbolKind { Contact, Selector, Coil, MeasuringRelay, TwoPinContact, Through }

    private sealed record SymbolDef(SymbolKind Kind, PinDef[] Pins);

    /// <summary>Known symbols (library/number/variant). Contacts: A185 = NC;COM;NO.</summary>
    private static readonly Dictionary<string, SymbolDef> Symbols = new(StringComparer.Ordinal)
    {
        ["1/1125/2"] = new(SymbolKind.Contact, [new(1, 0, 0, "U"), new(2, 0, -PinLength, "D"), new(0, 4, -PinLength, "D")]),
        ["5/35/0"] = new(SymbolKind.Selector, [new(0, 0, 0, "U"), new(1, -4, -PinLength, "D"), new(2, 4, -PinLength, "D")]),
        // Mirrored changeover contact: common on the right, NO below it, NC on the left (A185 = NC;COM;NO).
        ["1/1125/6"] = new(SymbolKind.Contact, [new(1, 4, 0, "U"), new(2, 4, -PinLength, "D"), new(0, 0, -PinLength, "D")]),
        // Key / selector switch with the common in the middle (A185 e.g. 1;2;4).
        ["5/32/0"] = new(SymbolKind.Selector, [new(0, 4, 0, "U"), new(1, 0, -PinLength, "D"), new(2, 8, -PinLength, "D")]),
        ["1/1110/0"] = new(SymbolKind.MeasuringRelay, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D"), new(2, 8, 0, "U"), new(3, 8, -PinLength, "D")]),
        // Push buttons / emergency stop: one contact, two terminals one above the other (A185 e.g. 13;14 or 21;22).
        ["1/35/0"] = new(SymbolKind.TwoPinContact, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")]),
        ["1/36/0"] = new(SymbolKind.TwoPinContact, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")]),
        ["1/150/2"] = new(SymbolKind.TwoPinContact, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")]),
        // Auxiliary contact of a breaker (A185 97;98).
        ["1/1/6"] = new(SymbolKind.TwoPinContact, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")]),
        // Diode module in a lamp circuit (RSD, e.g. lamp test): current from the top terminal to the bottom one.
        ["1/3613/0"] = new(SymbolKind.Through, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")]),
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
            var coils = devices.Count(device => device.Attribute("A1381")?.Value is "200" or "900" or "901");
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
        var parts = ReadParts(path, cancellationToken);
        ForEachPage(path, cancellationToken, (index, page) =>
        {
            if (pages.Contains(index))
            {
                ImportPage(page, import);
            }

            // Selector switches of the whole project (also on pages not imported, e.g. wired straight to PLC inputs),
            // so every switch is there for the relay logic and the cubicle doors.
            CollectSwitches(page, import, parts);
            CollectOperatorDevices(page, import, parts);
        });

        // Selector switches on the cubicle too; an illuminated push button is one device (button + its lamp).
        foreach (var selector in import.Switches.Where(selector => !import.CubicleDevices.Any(item => item.Tag.Equals(selector.Tag, StringComparison.OrdinalIgnoreCase))))
        {
            import.CubicleDevices.Add(new CubicleItem { Kind = CubicleItemKind.Switch, Tag = selector.Tag, Text = selector.Text, Color = "Black" });
        }

        return import;
    }

    /// <summary>Lamp symbols (function category 900 signal lamp, 901 horn / buzzer).</summary>
    private static bool IsLamp(XElement item) => item.Attribute("A1381")?.Value is "900" or "901";

    /// <summary>Tag of the relay-logic "coil" of a lamp: the cubicle lamp is lit by it ("-H104:LAMP").</summary>
    public static string LampTag(string tag) => tag.Trim() + ":LAMP";

    /// <summary>Push buttons / mushrooms (category 600 with a push-button symbol or part) and lamps of a page, for a cubicle door.</summary>
    private static void CollectOperatorDevices(XElement page, EplanRelayImport import, IReadOnlyDictionary<string, string> parts)
    {
        // Path function texts (the text above a current path, e.g. "Warning"): what a device on that path is for.
        var pathTexts = page.Elements("O83")
            .Select(text => (Position: (text.Attribute("A501")?.Value ?? "").Split('/'), Text: Text(text.Attribute("A511")?.Value).Replace("\r", "").Replace('\n', ' ').Trim()))
            .Where(text => text.Position.Length == 2 && text.Text.Length > 0
                           && double.TryParse(text.Position[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(text => (X: double.Parse(text.Position[0], NumberStyles.Float, CultureInfo.InvariantCulture), text.Text))
            .ToList();
        string PathText(XElement item) => Position(item) is { } at
            ? pathTexts.Where(text => Math.Abs(text.X - at.X) <= 20).OrderBy(text => Math.Abs(text.X - at.X)).Select(text => text.Text).FirstOrDefault() ?? ""
            : "";

        foreach (var item in page.Elements("O17"))
        {
            var symbol = $"{item.Attribute("A1261")?.Value}/{item.Attribute("A1262")?.Value}/{item.Attribute("A1263")?.Value}";
            var category = item.Attribute("A1381")?.Value ?? "";
            var tag = Tag(item);
            if (tag.Trim('-').Length == 0)
            {
                continue;
            }

            var p11 = item.Element("P11");
            var text = FirstNonEmpty(Text(p11?.Attribute("P20011")?.Value), category == "600" ? Text(p11?.Attribute("P20027")?.Value) : "", PathText(item));
            var existing = import.CubicleDevices.FirstOrDefault(device => device.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            var part = PartText(item, parts);
            var buttonPart = Has(part, "pushbutton", "push button", "push-button", "mushroom", "emergency stop") && !Has(part, "push-in");
            if (category == "600" && !SwitchSymbols.Contains(symbol)
                && ((Symbols.TryGetValue(symbol, out var def) && def.Kind == SymbolKind.TwoPinContact) || buttonPart))
            {
                var mushroom = symbol == "1/150/2" || Has(part, "mushroom", "emergency stop") || Has(text, "EMERGENCY", "ESD");
                if (existing is null)
                {
                    import.CubicleDevices.Add(new CubicleItem
                    {
                        Kind = mushroom ? CubicleItemKind.EmergencyStop : CubicleItemKind.PushButton,
                        Tag = tag,
                        Text = text,
                        Color = mushroom ? "Red" : LampColor(part) ?? "Green",
                        Contact = mushroom || symbol == "1/36/0" ? "NC" : "NO"
                    });
                }
                else if (existing.IsLamp)
                {
                    // Illuminated push button: the lamp part was found first.
                    existing.Kind = mushroom ? CubicleItemKind.EmergencyStop : CubicleItemKind.PushButton;
                    existing.Contact = mushroom || symbol == "1/36/0" ? "NC" : "NO";
                    existing.Text = existing.Text.Length > 0 ? existing.Text : text;
                }
            }
            else if (IsLamp(item))
            {
                var color = LampColor(Text(p11?.Attribute("P20025")?.Value)) ?? LampColor(part);
                if (existing is null)
                {
                    import.CubicleDevices.Add(new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = tag, Text = FirstNonEmpty(Text(p11?.Attribute("P20011")?.Value), PathText(item)), Color = color ?? "White", Source = LampTag(tag) });
                }
                else if (!existing.IsSwitch)
                {
                    existing.Source = LampTag(tag); // illuminated push button
                    existing.Color = color ?? existing.Color;
                }
            }
        }
    }

    private static string? LampColor(string text)
    {
        var name = text.Trim().ToUpperInvariant();
        return name.Contains("GREEN") || name.Contains("ZELEN") ? "Green"
            : name.Contains("RED") || name.Contains("CRVEN") ? "Red"
            : name.Contains("YELLOW") || name.Contains("AMBER") || name.Contains("ŽUT") || name.Contains("ORANGE") ? "Yellow"
            : name.Contains("BLUE") || name.Contains("PLAV") ? "Blue"
            : name.Contains("WHITE") || name.Contains("BIJEL") ? "White"
            : null;
    }

    /// <summary>
    /// The parts of the project (the device tag list): part number → description and colour, e.g. SE.XB4BVB3 →
    /// "Pilot light", "Color: Green".
    /// </summary>
    private static Dictionary<string, string> ReadParts(string path, CancellationToken cancellationToken)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(path, settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "O117")
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = (XElement)XNode.ReadFrom(reader);
                var number = part.Attribute("A2041")?.Value ?? "";
                var p11 = part.Element("P11");
                if (number.Length > 0)
                {
                    parts[number] = $"{Text(p11?.Attribute("P22004")?.Value)} {Text(p11?.Attribute("P22009")?.Value)}".Replace('\n', ' ').Trim();
                }
            }
            else
            {
                reader.Read();
            }
        }

        return parts;
    }

    /// <summary>Descriptions of the parts of a device (P20100_1, P20100_2… part numbers), joined.</summary>
    private static string PartText(XElement item, IReadOnlyDictionary<string, string> parts)
    {
        var p11 = item.Element("P11");
        return p11 is null ? "" : string.Join(" ", p11.Attributes().Where(attribute => attribute.Name.LocalName.StartsWith("P20100_", StringComparison.Ordinal))
            .Select(attribute => parts.GetValueOrDefault(attribute.Value, "")).Where(text => text.Length > 0));
    }

    /// <summary>Port of a PLC output point: "DQ0" / "DO3" / "Q0.2" → 0 / 3 / 2; null for anything else.</summary>
    private static int? PlcOutputPort(string compact)
    {
        var match = System.Text.RegularExpressions.Regex.Match(compact, @"^(?:DQ|DO|Q\d+\.)(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Card name as in the hardware tables: "-A611" → "A611", "2-A611" → "A611".</summary>
    private static string CardName(string tag) => tag.Split('-').Last().Trim();

    private static bool Has(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selector switches (function category 600 with a switch symbol or a selector / cam switch part): tag, text and position names.</summary>
    private static void CollectSwitches(XElement page, EplanRelayImport import, IReadOnlyDictionary<string, string> parts)
    {
        foreach (var item in page.Elements("O17").Where(item => item.Attribute("A1381")?.Value == "600"))
        {
            var symbol = $"{item.Attribute("A1261")?.Value}/{item.Attribute("A1262")?.Value}/{item.Attribute("A1263")?.Value}";
            var part = PartText(item, parts);
            if (!SwitchSymbols.Contains(symbol) && !(Has(part, "selector switch", "cam switch") && !Has(part, "push")))
            {
                continue;
            }

            var tag = Tag(item);
            var p11 = item.Element("P11");
            var text = FirstNonEmpty(Text(p11?.Attribute("P20011")?.Value), Text(p11?.Attribute("P20027")?.Value));
            var positions = Enumerable.Range(10, 6).Select(i => Text(p11?.Attribute($"P20901_{i}")?.Value)).Where(name => name.Length > 0).ToList();
            var existing = import.Switches.FirstOrDefault(selector => selector.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                import.Switches.Add(new RelaySwitch
                {
                    Tag = tag,
                    Text = text,
                    Positions = positions.Count >= 2 ? string.Join("/", positions) : "0/1",
                    Position = positions.Count >= 2 ? positions[0] : "0"
                });
                continue;
            }

            // The same switch drawn on several pages (contact blocks): keep the first text and named positions found.
            if (existing.Text.Length == 0)
            {
                existing.Text = text;
            }

            if (existing.Positions == "0/1" && positions.Count >= 2)
            {
                existing.Positions = string.Join("/", positions);
                existing.Position = positions[0];
            }
        }
    }

    /// <summary>Symbols of selector switches (maintained positions), whatever their contacts are wired to.</summary>
    private static readonly HashSet<string> SwitchSymbols = new(StringComparer.Ordinal) { "5/35/0", "5/32/0" };

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
        public bool PlusTerminal { get; init; }

        /// <summary>A terminal: terminals of a strip drawn side by side are not wired to each other.</summary>
        public bool Terminal { get; init; }

        /// <summary>Output point of a PLC card ("-A631:DQ0"): supplies what is wired to it.</summary>
        public string PlcOutput { get; init; } = "";
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
                    // Terminal: the wire passes through. A "+" strip (-X44+) supplies the page when it has no supply potential.
                    pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "LRUD", Owner = null, PlusTerminal = Tag(item).TrimEnd().EndsWith('+'), Terminal = true });
                    break;
                case "O17" when category == "300":
                {
                    // Connection point of a device drawn as a black box (frequency converter, PLC…): its +24V output
                    // supplies the page, its digital inputs (DI 1…) end paths like coils ("-TM2:DI1").
                    var point = FirstNonEmpty(item.Attribute("A186")?.Value?.Trim() ?? "", item.Element("P11")?.Attribute("P20400")?.Value?.Trim() ?? "");
                    var compact = point.Replace(" ", "").ToUpperInvariant();
                    if (compact is "+24V" or "24V" or "+24" or "P24" or "+24VDC" or "L+")
                    {
                        pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "UD", Owner = null, Potential = "+" });
                    }
                    else if (PlcOutputPort(compact) is { } outputPort)
                    {
                        // Output of a PLC card (DQ0, DO1, Q0.2…): it supplies the coils wired to it.
                        var marker = $"{Tag(item)}:{compact}";
                        import.PlcPoints[marker] = new EplanPlcPoint(CardName(Tag(item)), item.Attribute("A185")?.Value?.Trim() ?? "", outputPort, true,
                            item.Element("P11")?.Attribute("P20400")?.Value?.Trim() ?? "");
                        pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "UD", Owner = null, PlcOutput = marker });
                    }
                    else if (compact.StartsWith("DI", StringComparison.Ordinal) && compact.Length > 2 && char.IsDigit(compact[2]))
                    {
                        if (item.Element("P11")?.Attribute("P20400")?.Value is { Length: > 0 } address && address.TrimStart().StartsWith('I'))
                        {
                            // Input of a PLC card (DI0 at I0.0): a PLC input object at the end of the path.
                            import.PlcPoints[$"{Tag(item)}:{compact}"] = new EplanPlcPoint(CardName(Tag(item)), item.Attribute("A185")?.Value?.Trim() ?? "",
                                int.TryParse(compact[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inputPort) ? inputPort : 0, false, address.Trim());
                        }

                        var function = Text(item.Element("P11")?.Attribute("P20011")?.Value);
                        var input = new Device
                        {
                            Id = item.Attribute("A1")?.Value ?? Guid.NewGuid().ToString("N"),
                            Tag = $"{Tag(item)}:{compact}",
                            Symbol = new SymbolDef(SymbolKind.Coil, [new(0, 0, 0, "UD"), new(1, 0, -PinLength, "")]),
                            Points = [point, ""],
                            Text = function.Length > 0 ? $"{point}\n{function}" : point,
                            X = at.X
                        };
                        foreach (var pinDef in input.Symbol.Pins)
                        {
                            var pin = new Pin { X = at.X + pinDef.Dx, Y = at.Y + pinDef.Dy, Directions = pinDef.Directions, Owner = input, Index = pinDef.Index };
                            input.Pins[pinDef.Index] = pin;
                            pins.Add(pin);
                        }

                        devices.Add(input);
                    }
                    else
                    {
                        pins.Add(new Pin { X = at.X, Y = at.Y, Directions = "", Owner = null }); // DGND, DI COM…: the return
                    }

                    break;
                }
                case "O17":
                    var tag = Tag(item);
                    var lamp = IsLamp(item);
                    var def = lamp
                        ? new SymbolDef(SymbolKind.Coil, [new(0, 0, 0, "U"), new(1, 0, -PinLength, "D")])
                        : Symbols.GetValueOrDefault(symbol)
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
                        Tag = lamp ? LampTag(tag) : tag,
                        Symbol = def,
                        Points = (item.Attribute("A185")?.Value ?? "").Split(';'),
                        Text = lamp
                            ? FirstNonEmpty(Text(p11?.Attribute("P20011")?.Value), "LAMP " + Text(p11?.Attribute("P20025")?.Value)).Trim()
                            : FirstNonEmpty(Text(p11?.Attribute("P20027")?.Value), Text(p11?.Attribute("P20011")?.Value)),
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
                    // Connection definition point: it lies on a wire (vertical or horizontal — not exported), never
                    // joins wires, so the wire simply runs through it.
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
                if (Nearest(pins, i, direction) is { } j && pins[j].Directions.Contains(Opposite(direction))
                    && !(direction is 'L' or 'R' && pins[i].Terminal && pins[j].Terminal))
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
            supply = pins.Where(pin => pin.PlusTerminal && connected.Contains(pin)).Select(pin => pin.Net).Distinct().ToList();
        }
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
                case SymbolKind.TwoPinContact:
                    if (connected.Contains(device.Pins[0]) && connected.Contains(device.Pins[1]))
                    {
                        edges.Add(new Edge(device.Pins[0].Net, device.Pins[1].Net, new Leaf(device, 0, 1)));
                    }

                    break;
                case SymbolKind.Through:
                    // Diode (lamp test): current only from its top to its bottom, like a closed contact without a tag.
                    if (connected.Contains(device.Pins[0]) && connected.Contains(device.Pins[1]))
                    {
                        edges.Add(new Edge(device.Pins[0].Net, device.Pins[1].Net, new Series([])));
                    }

                    break;
                case SymbolKind.Coil:
                    // An unwired input of a black box (spare DI) is not a path.
                    if (!device.Tag.Contains(':') || connected.Contains(device.Pins[0]))
                    {
                        coils.Add((device, device.Pins[0].Net, device.Pins[1].Net));
                    }

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
        var plcOutputs = pins.Where(pin => pin.PlcOutput.Length > 0 && connected.Contains(pin)).Select(pin => (pin.Net, pin.PlcOutput)).Distinct().ToList();
        foreach (var (coil, a1, _) in coils.OrderBy(coil => coil.Device.X))
        {
            var expr = supply.Select(net => Reduce(edges, net, a1)).FirstOrDefault(found => found is not null);
            List<string> signatures;
            List<RelayElement> elements;
            if ((expr is null || expr is Series { Items.Count: 0 } && !supply.Contains(a1))
                && plcOutputs.Select(output => (output.PlcOutput, Expr: Reduce(edges, output.Net, a1))).FirstOrDefault(found => found.Expr is not null) is { Expr: { } fromPlc } plc)
            {
                // Fed by a PLC output: the output object first, then the contacts between it and the coil.
                var series = Flatten(fromPlc);
                signatures = [plc.PlcOutput, .. series.Select(Signature)];
                elements = [new RelayElement { Tag = plc.PlcOutput, Contact = "NO", Text = "PLC OUTPUT" }, .. series.Select(item => ToElement(item, import))];
            }
            else if (expr is null || expr is Series { Items.Count: 0 } && !supply.Contains(a1))
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
                // The switch's own position names (as first read in the project), so the contacts of every page match.
                var known = import.Switches.FirstOrDefault(item => item.Tag.Equals(device.Tag, StringComparison.OrdinalIgnoreCase))?.PositionList();
                var positions = known is { Count: >= 2 } ? known
                    : device.Positions.Count >= 2 ? device.Positions
                    : [device.Points.ElementAtOrDefault(1) ?? "1", "0", device.Points.ElementAtOrDefault(2) ?? "2"];
                var position = leaf.UsedPin == 1 ? positions[0] : positions[^1];
                if (!import.Switches.Any(item => item.Tag.Equals(device.Tag, StringComparison.OrdinalIgnoreCase)))
                {
                    // (normally already collected from the whole project)
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
            case Leaf { Device.Symbol.Kind: SymbolKind.TwoPinContact } leaf:
            {
                // IEC terminal numbers: x1-x2 normally closed, x3-x4 normally open.
                var device = leaf.Device;
                var first = Point(device, 0);
                var second = Point(device, 1);
                var closed = second.EndsWith('2') || second.EndsWith('1');
                return new RelayElement
                {
                    Tag = device.Tag,
                    Contact = closed ? "NC" : "NO",
                    Pins = closed ? $"{first}/{second}/" : $"{first}//{second}",
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

        // A coil whose whole path is shared hangs from the end of it (a branch without contacts of its own).
        return count;
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
