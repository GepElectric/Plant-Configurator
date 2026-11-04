using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CwcConfigurator.Services.Eplan;

/// <summary>Power devices read from EPLAN with what feeds each one, plus the feeder chains for overview sheets.</summary>
public sealed class EplanPowerImport
{
    public List<PowerDevice> Devices { get; } = [];

    /// <summary>Each feeder from the L1 busbar: the devices in order (key, tag) and the load at its end ("-M3 11 kW").</summary>
    public List<(string Page, List<(string Key, string Tag)> Chain, string Load)> Feeders { get; } = [];

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Reads the power (three-phase) circuits of an EPLAN PXF export: the same autoconnect rebuild as the control
/// circuits (<see cref="EplanRelayImporter"/>), with the pin geometry of the power symbols (3-pole breakers and motor
/// protection switches, contactor main poles, motors…), terminals that connect only in their own directions (so the
/// phases are not shorted), potentials and interruption points that join the pages by name, and frequency converters
/// as black boxes (L1 → T1/U). From the L1 busbar every breaker, contactor and motor is traced to the device that feeds
/// it; terminals, cables and control contacts in between pass through.
/// </summary>
public static class EplanPowerImporter
{
    private const double H = 8;          // pole length (mm); only the order matters
    private const double Tolerance = 0.01;

    private enum Kind { Pole, Load, Changeover, Selector }

    private sealed record Pin(int Index, double Dx, double Dy, string Directions);

    private static Pin[] Poles(int count, double pitch = 8) =>
        Enumerable.Range(0, count).SelectMany(i => new[] { new Pin(2 * i, i * pitch, 0, "U"), new Pin(2 * i + 1, i * pitch, -H, "D") }).ToArray();

    private static Pin[] Ups(params double[] dx) => dx.Select((x, i) => new Pin(i, x, 0, "U")).ToArray();

    /// <summary>Pin geometry of the symbols (library/number/variant), A185 index → offset (mm) and direction.</summary>
    private static readonly Dictionary<string, (Kind Kind, Pin[] Pins)> Symbols = new(StringComparer.Ordinal)
    {
        ["1/1547/0"] = (Kind.Pole, Poles(3)), ["1/174/0"] = (Kind.Pole, Poles(3)), ["1/97/0"] = (Kind.Pole, Poles(3)), ["1/1525/0"] = (Kind.Pole, Poles(3)),
        ["1/0/0"] = (Kind.Pole, Poles(1)), ["1/1/0"] = (Kind.Pole, Poles(1)), ["1/1/6"] = (Kind.Pole, Poles(1)),
        ["1/1340/0"] = (Kind.Pole, Poles(2)), ["6/1340/0"] = (Kind.Pole, Poles(2)), ["6/1339/0"] = (Kind.Pole, Poles(2)), ["1/1137/4"] = (Kind.Pole, Poles(2)),
        ["1/1142/0"] = (Kind.Pole, Poles(4)),
        ["1/1220/0"] = (Kind.Pole, Poles(1)), ["5/14/0"] = (Kind.Pole, Poles(1)), ["1/49/0"] = (Kind.Load, Poles(1)),
        ["1/307/0"] = (Kind.Load, Ups(0, 8, 16, 24)),
        ["19/3615/0"] = (Kind.Load, [new(0, 3, 0, "U"), new(1, 8, 0, "U"), new(2, 13, 0, "U"), new(3, -2, 0, "U"), new(4, 23, 0, "U"), new(5, 28, 0, "U")]),
        ["5/2/0"] = (Kind.Load, [new(0, 4, 0, "U"), new(1, 12, 0, "U"), new(2, 20, 0, "U")]),
        ["1/1462/0"] = (Kind.Load, Ups(0, 8, 16, 24, 32)), ["1/1464/0"] = (Kind.Load, Ups(0, 8, 16)),
        ["5/15/0"] = (Kind.Load, [new(0, 0, 0, "U"), new(1, 0, -8, "D"), new(2, 8, -8, "D")]),
        ["19/46/0"] = (Kind.Load, [new(0, 0, 0, "U"), new(1, 0, -8, "D")]),
        ["1/1190/0"] = (Kind.Load, [new(0, 0, 0, "U"), new(1, 0, -8, "D"), new(2, 8, 0, "R")]),
        ["5/27/0"] = (Kind.Load, Poles(1)), ["5/16/0"] = (Kind.Load, Poles(1)),
        ["1/1125/2"] = (Kind.Changeover, [new(1, 0, 0, "U"), new(2, 0, -8, "D"), new(0, 4, -8, "D")]),
        ["5/35/0"] = (Kind.Selector, [new(0, 0, 0, "U"), new(1, -4, -8, "D"), new(2, 4, -8, "D")]),
        ["1/192/2"] = (Kind.Load, Ups(0, 16, 24, 8)),
    };

    /// <summary>Terminal symbols: the directions they connect (most only up/down — a strip must not short the phases).</summary>
    private static readonly Dictionary<string, string> Terminals = new(StringComparer.Ordinal)
    {
        ["1/1117"] = "UD", ["1/1351"] = "UD", ["1/1355"] = "UDLR", ["1/1353/0"] = "UDL", ["1/1353/2"] = "UDR", ["1/1353/4"] = "UDR", ["5/22/6"] = "UDR"
    };

    private sealed class Point
    {
        public required double X { get; init; }
        public required double Y { get; init; }
        public required string Directions { get; init; }
        public string Name { get; init; } = "";       // potential / interruption point name
        public Device? Owner { get; init; }
        public int Index { get; init; } = -1;
        public string BlackBox { get; init; } = "";
        public string BoxPoint { get; init; } = "";
        public string BoxVariant { get; init; } = "";
        public bool Used { get; set; }
        public string Net { get; set; } = "";
    }

    private sealed class Device
    {
        public required string Key { get; init; }      // function + location + tag (two -Q1 can be different devices)
        public required string Tag { get; init; }
        public required string Category { get; init; }
        public required string Symbol { get; init; }
        public required Kind Kind { get; init; }
        public required string[] Points { get; init; }
        public required string Text { get; init; }
        public required string Page { get; init; }
        public Dictionary<int, Point> Pins { get; } = [];
    }

    private sealed record Edge(string From, string To, Device? Device);

    /// <summary>Pages with power devices (breakers, motor protection, contactor main poles, motors).</summary>
    public static List<EplanSchematicPage> ListPages(string path, CancellationToken cancellationToken = default)
    {
        var result = new List<EplanSchematicPage>();
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
                index++;
                var devices = page.Descendants("O17").Count(item =>
                    item.Attribute("A1381")?.Value is "501" or "502" or "504" or "506" or "1000"
                    || (item.Attribute("A1381")?.Value == "201" && item.Attribute("A1262")?.Value == "0"));
                if (devices > 0)
                {
                    var p49 = page.Element("P49");
                    result.Add(new EplanSchematicPage(index, p49?.Attribute("P1100")?.Value ?? "", p49?.Attribute("P1200")?.Value ?? "",
                        p49?.Attribute("P11012")?.Value ?? "", Text(page.Element("P11")?.Attribute("P11011")?.Value), 0, devices));
                }
            }
            else
            {
                reader.Read();
            }
        }

        return result;
    }

    public static EplanPowerImport Import(string path, IReadOnlySet<int> pages, CancellationToken cancellationToken = default)
    {
        var import = new EplanPowerImport();
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string node)
        {
            parent.TryAdd(node, node);
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }

            return node;
        }

        void Union(string a, string b) => parent[Find(a)] = Find(b);

        var edges = new List<Edge>();
        var loads = new List<(Device Device, string Net)>();
        var devices = new List<Device>();
        var settings = new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
        using (var reader = XmlReader.Create(path, settings))
        {
            reader.MoveToContent();
            var index = 0;
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "O4")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = (XElement)XNode.ReadFrom(reader);
                    if (pages.Contains(++index))
                    {
                        ReadPage(page, edges, loads, devices, Find, Union);
                    }
                }
                else
                {
                    reader.Read();
                }
            }
        }

        // Trace from the L1 busbar (all pages joined by the potential / interruption name "L1").
        var source = Find("NAME:L1");
        var graph = new Dictionary<string, List<(string To, Device? Device)>>();
        foreach (var edge in edges)
        {
            var (a, b) = (Find(edge.From), Find(edge.To));
            graph.TryAdd(a, []);
            graph.TryAdd(b, []);
            graph[a].Add((b, edge.Device));
            graph[b].Add((a, edge.Device));
        }

        // Supply potentials besides the L1 busbar (single phase L2230VAC, DC L+110VDC / L+24VDC…) and the rails that
        // end a trace (L2, L3, N, PE, L-…, M).
        var names = parent.Keys.Where(key => key.StartsWith("NAME:", StringComparison.Ordinal)).Select(key => key[5..]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rails = names.Where(IsReturn).Select(name => Find("NAME:" + name)).ToHashSet();
        var supplies = names.Where(name => IsSupplyName(name) && Find("NAME:" + name) != source).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

        Dictionary<string, (string From, Device? Device)?> Trace(string start, HashSet<string> visited, Func<string, bool> blocked)
        {
            var steps = new Dictionary<string, (string From, Device? Device)?> { [start] = null };
            var pending = new Queue<string>([start]);
            while (pending.Count > 0)
            {
                var node = pending.Dequeue();
                foreach (var (to, device) in graph.GetValueOrDefault(node) ?? [])
                {
                    if (!steps.ContainsKey(to) && !visited.Contains(to) && !blocked(to))
                    {
                        steps[to] = (node, device);
                        pending.Enqueue(to);
                    }
                }
            }

            return steps;
        }

        List<Device> ChainTo(Dictionary<string, (string From, Device? Device)?> steps, string node)
        {
            var path = new List<Device>();
            while (steps.TryGetValue(node, out var step) && step is { } value)
            {
                if (value.Device is not null && (path.Count == 0 || path[^1] != value.Device))
                {
                    path.Add(value.Device);
                }

                node = value.From;
            }

            path.Reverse();
            return path;
        }

        var previous = Trace(source, [], node => IsRail(node, source, Find));
        List<Device> PathTo(string node) => ChainTo(previous, node);

        // Power devices on the paths and what feeds them; motors at the end of their path.
        var added = new Dictionary<string, PowerDevice>(StringComparer.OrdinalIgnoreCase);
        void Add(Device device, Device? upstream, string supply)
        {
            if (added.ContainsKey(device.Key) || PowerKind(device) is not { } kind)
            {
                return;
            }

            var power = new PowerDevice(device.Tag, kind, device.Text, upstream is null ? "" : upstream.Key, device.Page, Incomer: supply.Length == 0 && device.Category == "506",
                Key: device.Key, Source: supply);
            added[device.Key] = power;
            import.Devices.Add(power);
        }

        void AddReached(Dictionary<string, (string From, Device? Device)?> steps, string supply)
        {
            foreach (var node in steps.Keys.Where(node => steps[node] is not null).ToList())
            {
                var chain = ChainTo(steps, node).Where(device => PowerKind(device) is not null).ToList();
                for (var i = 0; i < chain.Count; i++)
                {
                    Add(chain[i], i == 0 ? null : chain[i - 1], supply);
                }
            }

            foreach (var (device, net) in loads.Where(load => load.Device.Category == "1000"))
            {
                var node = Find(net);
                if (steps.ContainsKey(node))
                {
                    Add(device, ChainTo(steps, node).Where(item => PowerKind(item) is not null).LastOrDefault(), supply);
                }
            }
        }

        AddReached(previous, "");

        // The other supplies, each on its own busbar; what L1 reached already is not traced again.
        var visited = previous.Keys.ToHashSet();
        foreach (var supply in supplies)
        {
            var start = Find("NAME:" + supply);
            if (visited.Contains(start))
            {
                continue;
            }

            var steps = Trace(start, visited, node => node != start && (rails.Contains(node) || supplies.Any(other => Find("NAME:" + other) == node)));
            AddReached(steps, Bare(supply));
            visited.UnionWith(steps.Keys);
        }

        // Breakers no supply reaches (fed from a page not imported, an unknown symbol…): drawn anyway.
        foreach (var device in devices.Where(device => PowerKind(device) is PowerDeviceKind.Breaker or PowerDeviceKind.MotorProtection))
        {
            if (!added.ContainsKey(device.Key))
            {
                Add(device, null, "?");
            }
        }

        // Device tags shared by two devices (e.g. a local -Q1 and the main -Q1) get their location in the text.
        foreach (var duplicate in import.Devices.GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            import.Warnings.Add($"{duplicate.Key} is used by {duplicate.Count()} power devices (different function / location) — named {duplicate.Key}, {duplicate.Key}_2… in the simulator.");
        }

        // Feeder chains for the overview sheets: every load reached from L1, with the power devices on its way.
        foreach (var (device, net) in loads)
        {
            var node = Find(net);
            if (!previous.ContainsKey(node) || node == source)
            {
                continue;
            }

            var chain = PathTo(node).Select(item => (item.Key, item.Tag)).ToList();
            var load = $"{device.Tag} {device.Text}".Trim();
            if (!import.Feeders.Any(feeder => feeder.Load == load))
            {
                import.Feeders.Add((device.Page, chain, load));
            }
        }

        return import;
    }

    /// <summary>Potential name without its page / group prefix: "11-L+110VDC" → "L+110VDC".</summary>
    private static string Bare(string name)
    {
        var dash = name.IndexOf('-');
        return dash > 0 && name[..dash].All(c => char.IsDigit(c) || c == '.') ? name[(dash + 1)..] : name;
    }

    /// <summary>A supply potential: L+…, +…, a single-phase L…VAC (not the L2 / L3 phases of the busbar).</summary>
    private static bool IsSupplyName(string name)
    {
        var bare = Bare(name).Replace(" ", "").ToUpperInvariant();
        return bare.StartsWith("L+", StringComparison.Ordinal) || bare.StartsWith('+') || bare.EndsWith("L+", StringComparison.Ordinal)
               || (bare.StartsWith('L') && bare.Contains("VAC", StringComparison.Ordinal) && bare is not ("L1" or "L2" or "L3"));
    }

    /// <summary>A return / other phase: N…, PE, M, L-…, L2, L3.</summary>
    private static bool IsReturn(string name)
    {
        var bare = Bare(name).Replace(" ", "").ToUpperInvariant();
        return bare is "L2" or "L3" or "PE" or "M" || bare.StartsWith('N') || bare.StartsWith("L-", StringComparison.Ordinal) || bare.EndsWith("L-", StringComparison.Ordinal)
               || bare.StartsWith('-');
    }

    private static bool IsRail(string node, string source, Func<string, string> find) =>
        node != source && (node == find("NAME:L2") || node == find("NAME:L3") || node == find("NAME:N") || node == find("NAME:PE"));

    /// <summary>What a device is in the simulator, or null when it only passes the current (terminal, control contact…).</summary>
    private static PowerDeviceKind? PowerKind(Device device) => device.Category switch
    {
        "501" or "504" or "506" => PowerDeviceKind.Breaker,
        "502" => PowerDeviceKind.MotorProtection,
        "201" when device.Symbol == "1/0/0" => PowerDeviceKind.Contactor,
        "201" when device.Symbol == "1/1/0" && device.Points.All(point => point.Length == 0) => PowerDeviceKind.Breaker, // local isolator
        "600" when device.Symbol == "1/1525/0" => PowerDeviceKind.Breaker,
        "1000" => PowerDeviceKind.Motor,
        _ => null
    };

    private static void ReadPage(XElement page, List<Edge> edges, List<(Device, string)> loads, List<Device> devices,
        Func<string, string> find, Action<string, string> union)
    {
        var pageName = page.Element("P49")?.Attribute("P11012")?.Value ?? "";
        var points = new List<Point>();
        var pageDevices = new List<Device>();

        // Positioned objects, also those inside groups (O26).
        foreach (var item in page.Descendants().Where(item => item.Name.LocalName is "O17" or "O42" or "O70"))
        {
            if (Position(item) is not { } at)
            {
                continue;
            }

            var lib = item.Attribute("A1261")?.Value ?? "";
            var number = item.Attribute("A1262")?.Value ?? "";
            var variant = item.Attribute("A1263")?.Value ?? "";
            var symbol = $"{lib}/{number}/{variant}";
            var category = item.Attribute("A1381")?.Value ?? "";
            var tag = Tag(item);
            switch (item.Name.LocalName)
            {
                case "O17" when category == "100":
                    points.Add(new Point { X = at.X, Y = at.Y, Directions = Terminals.GetValueOrDefault(symbol) ?? Terminals.GetValueOrDefault($"{lib}/{number}") ?? "UD" });
                    break;
                case "O17" when category == "1304":
                    // Potential (names the busbar, e.g. L1): 0/30 T-nodes, 0/38 rail ends.
                    var potential = number == "30" ? variant switch { "0" => "LRU", "2" => "LRD", _ => "LRUD" }
                        : number == "38" ? variant switch { "0" => "U", "1" => "L", "2" => "D", "3" => "R", _ => "LRUD" } : "LRUD";
                    points.Add(new Point { X = at.X, Y = at.Y, Directions = potential, Name = tag.TrimStart('-') });
                    break;
                case "O17" when category == "300":
                    points.Add(new Point
                    {
                        X = at.X, Y = at.Y, Directions = variant switch { "0" => "U", "1" => "L", "2" => "D", "3" => "R", _ => "UDLR" },
                        BlackBox = tag, BoxPoint = item.Attribute("A185")?.Value ?? "", BoxVariant = variant
                    });
                    break;
                case "O17":
                    if (!Symbols.TryGetValue(symbol, out var def))
                    {
                        continue;
                    }

                    var p11 = item.Element("P11");
                    var device = new Device
                    {
                        Key = $"{item.Element("P150")?.Attribute("P1100")?.Value}+{item.Element("P150")?.Attribute("P1200")?.Value}{tag}",
                        Tag = tag,
                        Category = category,
                        Symbol = symbol,
                        Kind = def.Kind,
                        Points = (item.Attribute("A185")?.Value ?? "").Split(';'),
                        Text = FirstNonEmpty(Text(p11?.Attribute("P20011")?.Value), Text(p11?.Attribute("P20027")?.Value)).Replace("\r", "").Replace('\n', ' '),
                        Page = pageName
                    };
                    foreach (var pin in def.Pins)
                    {
                        var point = new Point { X = at.X + pin.Dx, Y = at.Y + pin.Dy, Directions = pin.Directions, Owner = device, Index = pin.Index };
                        device.Pins[pin.Index] = point;
                        points.Add(point);
                    }

                    pageDevices.Add(device);
                    break;
                case "O70":
                    // Interruption point: joins pages by its name.
                    points.Add(new Point { X = at.X, Y = at.Y, Directions = variant is "1" or "3" or "5" or "7" ? "UD" : "LR", Name = tag.TrimStart('-') });
                    break;
                case "O42" when lib == "0":
                    var directions = number switch
                    {
                        "64" => "LRD", "66" => "UDR", "67" => "UDL", "72" => "LRU",
                        "70" => variant switch { "1" => "UR", "2" => "UL", "3" => "LD", _ => "RD" },
                        _ => ""
                    };
                    if (directions.Length > 0)
                    {
                        points.Add(new Point { X = at.X, Y = at.Y, Directions = directions });
                    }

                    break;
            }
        }

        // Autoconnect on the page; coincident points join too.
        var parent = Enumerable.Range(0, points.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                if (Math.Abs(points[i].X - points[j].X) < Tolerance && Math.Abs(points[i].Y - points[j].Y) < Tolerance)
                {
                    parent[Find(i)] = Find(j);
                    points[i].Used = points[j].Used = true;
                }
            }

            foreach (var direction in points[i].Directions)
            {
                if (Nearest(points, i, direction) is { } j && points[j].Directions.Contains(Opposite(direction)))
                {
                    parent[Find(i)] = Find(j);
                    points[i].Used = points[j].Used = true;
                }
            }
        }

        for (var i = 0; i < points.Count; i++)
        {
            points[i].Net = $"{pageName}:{Find(i)}";
            find(points[i].Net);
            if (points[i].Name.Length > 0)
            {
                union(points[i].Net, "NAME:" + points[i].Name);
            }
        }

        foreach (var device in pageDevices)
        {
            devices.Add(device);
            switch (device.Kind)
            {
                case Kind.Pole:
                    for (var i = 0; i + 1 < device.Pins.Count; i += 2)
                    {
                        edges.Add(new Edge(device.Pins[i].Net, device.Pins[i + 1].Net, device));
                    }

                    break;
                case Kind.Changeover:
                    foreach (var used in new[] { 2, 0 }.Where(used => device.Pins[used].Used))
                    {
                        edges.Add(new Edge(device.Pins[1].Net, device.Pins[used].Net, device));
                    }

                    break;
                case Kind.Selector:
                    foreach (var used in new[] { 1, 2 })
                    {
                        edges.Add(new Edge(device.Pins[0].Net, device.Pins[used].Net, device));
                    }

                    break;
                default:
                    loads.AddRange(device.Pins.Values.Select(pin => (device, pin.Net)));
                    break;
            }
        }

        // Black boxes (frequency converters): inputs L1/L2/L3 → outputs T1/T2/T3 in order.
        foreach (var box in points.Where(point => point.BlackBox.Length > 0).GroupBy(point => point.BlackBox))
        {
            var inputs = box.Where(point => point.BoxVariant == "0" && !point.BoxPoint.Contains("PE")).OrderBy(point => point.X).ToList();
            var outputs = box.Where(point => point.BoxVariant == "2" && point.BoxPoint.StartsWith('T')).OrderBy(point => point.X).ToList();
            foreach (var (input, output) in inputs.Zip(outputs))
            {
                edges.Add(new Edge(input.Net, output.Net, null));
            }
        }
    }

    private static int? Nearest(List<Point> points, int index, char direction)
    {
        var a = points[index];
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var j = 0; j < points.Count; j++)
        {
            if (j == index)
            {
                continue;
            }

            var b = points[j];
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
        var parts = text?.Split('/');
        return parts is { Length: 2 }
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;
    }

    private static string Tag(XElement item)
    {
        var tag = item.Element("P150");
        return tag is null ? "" : $"{tag.Attribute("P20012")?.Value}-{tag.Attribute("P20013")?.Value}{tag.Attribute("P20014")?.Value}".Replace("\n", "");
    }

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

        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('@', 2)).Where(part => part.Length == 2).ToList();
        return ((parts.FirstOrDefault(part => part[0] == "en_US") ?? parts.FirstOrDefault())?[1] ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? "";
}
