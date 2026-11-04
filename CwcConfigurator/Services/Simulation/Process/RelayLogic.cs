using System.Globalization;
using YamlDotNet.Serialization;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// A relay-logic sheet drawn like a circuit diagram page: current paths side by side between the supply rail (L+) at
/// the top and the return rail (M) at the bottom. Each path is a series of contacts (with parallel branches) that
/// energises a relay coil at its bottom.
/// </summary>
public sealed class RelaySheet
{
    public string Name { get; set; } = "";

    /// <summary>Page number used in cross references ("/37.4" = page 37, path 4); 0 = the sheet's position.</summary>
    public int Page { get; set; }

    public List<RelayCircuit> Circuits { get; set; } = [];
}

/// <summary>One current path: contacts in series (top to bottom) and the coil it energises.</summary>
public sealed class RelayCircuit
{
    /// <summary>
    /// Where the path takes its supply: "" = the L+ rail, otherwise the <see cref="RelayElement.Id"/> of a contact
    /// (or parallel group) of another path — the path branches off below that element, like a split after a switch.
    /// </summary>
    public string Feed { get; set; } = "";

    public List<RelayElement> Elements { get; set; } = [];

    public RelayCoil Coil { get; set; } = new();
}

/// <summary>
/// A selector switch with two or three maintained positions (e.g. "LOCAL/0/REMOTE"), operated by clicking it in the
/// relay-logic view. Its contacts (same tag) close in the positions given on each contact, like a switching table.
/// </summary>
public sealed class RelaySwitch
{
    public string Tag { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Position names separated by '/', e.g. "MAN/0/AUTO" or "OFF/ON".</summary>
    public string Positions { get; set; } = "0/1";

    /// <summary>Current position (one of <see cref="Positions"/>); saved with the model.</summary>
    public string Position { get; set; } = "";

    public List<string> PositionList() => Positions.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The current position, or the first one when it is not set.</summary>
    [YamlIgnore]
    public string Current => PositionList() is { Count: > 0 } list
        ? list.FirstOrDefault(item => item.Equals(Position.Trim(), StringComparison.OrdinalIgnoreCase)) ?? list[0]
        : "";
}

public static class RelayElementKind
{
    public const string Contact = "Contact";
    public const string Parallel = "Parallel";
}

/// <summary>
/// A contact or a group of parallel branches. A contact is an auxiliary contact of a relay on a relay sheet (by its
/// tag, e.g. -K107) or of anything else given by <see cref="Source"/>: a signal ("DO:188", "DI:23"), an object port
/// ("@Mcb1.Closed", "@Common/Reservoir.High") or a constant ("=true").
/// </summary>
public sealed class RelayElement
{
    /// <summary>Identity of the element, so other paths can branch off below it (<see cref="RelayCircuit.Feed"/>).</summary>
    public string Id { get; set; } = NewId();

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];

    public string Kind { get; set; } = RelayElementKind.Contact;

    public string Tag { get; set; } = "";

    /// <summary>Used when the tag is not a relay coil of this PLC: "DO:188", "@Object.Port", "=true".</summary>
    public string Source { get; set; } = "";

    /// <summary>"NO" (closes when the source is on, terminals 11-14) or "NC" (opens, terminals 11-12).</summary>
    public string Contact { get; set; } = "NO";

    /// <summary>Terminal numbers "common/NC/NO" of the changeover contact, e.g. "11/12/14" or "21/22/24".</summary>
    public string Pins { get; set; } = "11/12/14";

    /// <summary>Function text (lines separated by '\n'); empty = the text of the relay coil.</summary>
    public string Text { get; set; } = "";

    /// <summary>Contact of a selector switch: the positions in which it is operated, e.g. "AUTO" or "MAN,AUTO".</summary>
    public string Position { get; set; } = "";

    /// <summary>Parallel branches, each a series of elements (Kind = Parallel).</summary>
    public List<List<RelayElement>> Branches { get; set; } = [];

    [YamlIgnore]
    public bool IsParallel => Kind.Equals(RelayElementKind.Parallel, StringComparison.OrdinalIgnoreCase);

    [YamlIgnore]
    public bool IsNormallyClosed => Contact.Equals("NC", StringComparison.OrdinalIgnoreCase);

    public (string Common, string Nc, string No) Terminals()
    {
        var parts = (Pins ?? "").Split('/', StringSplitOptions.TrimEntries);
        return (parts.ElementAtOrDefault(0) ?? "11", parts.ElementAtOrDefault(1) ?? "12", parts.ElementAtOrDefault(2) ?? "14");
    }
}

public static class RelayTimer
{
    public const string None = "";
    public const string OnDelay = "OnDelay";
    public const string OffDelay = "OffDelay";
}

/// <summary>The relay coil at the bottom of a current path. Its state drives the contacts with the same tag and the
/// <see cref="Outputs"/> signals (e.g. "DI:13, DI:46" — the relay's contact wired to PLC inputs).</summary>
public sealed class RelayCoil
{
    public string Tag { get; set; } = "";

    public string Text { get; set; } = "";

    public string Outputs { get; set; } = "";

    /// <summary>"" (normal relay), "OnDelay" or "OffDelay" (time relay).</summary>
    public string Timer { get; set; } = RelayTimer.None;

    /// <summary>Delay of a time relay in seconds.</summary>
    public double Time { get; set; }
}

/// <summary>
/// Runs the relay sheets of all PLCs once per simulation step, after the plant objects: contacts read the relay
/// coils of their PLC, signals, object ports or constants; the paths are solved repeatedly until the relays settle
/// (self-holding, interlocks), time relays count with the step time, and the coils write their output signals.
/// </summary>
public sealed class RelayLogicRunner
{
    private const int MaxSettlePasses = 10;

    private readonly List<CoilState> _coils = [];
    private readonly Dictionary<string, CoilState> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RelayElement, (bool PoweredIn, bool Closed)> _elements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, RelaySwitch> _switches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CubicleItem> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Plc, CubicleItem Item, List<(string Position, List<PortBinding> Targets)> Outputs)> _cubicleOutputs = [];
    private readonly List<(string Plc, CubicleItem Lamp)> _lamps = [];
    private readonly Dictionary<CubicleItem, bool> _lampStates = new(ReferenceEqualityComparer.Instance);

    private sealed class CoilState
    {
        public required string Plc { get; init; }
        public required RelaySheet Sheet { get; init; }
        public required RelayCircuit Circuit { get; init; }
        public required string Location { get; init; }
        public List<PortBinding> Outputs { get; } = [];
        public bool On { get; set; }
        public bool Powered { get; set; }
        public double Elapsed { get; set; }
    }

    public RelayLogicRunner(IReadOnlyDictionary<string, ProcessModel> models, RelayLogicRunner? previous, List<string> problems)
    {
        foreach (var (plc, model) in models)
        {
            // Cubicle devices: push buttons operate their contacts, buttons and switches drive PLC inputs, lamps show a source.
            foreach (var item in model.Cubicles.SelectMany(cubicle => cubicle.Items))
            {
                if (item.IsButton && item.Tag.Trim().Length > 0 && !_buttons.TryAdd(Key(plc, item.Tag), item))
                {
                    problems.Add($"{plc}: push button {item.Tag} is on the cubicles more than once.");
                }

                if ((item.IsLamp || item.IsButton) && item.Source.Trim().Length > 0)
                {
                    _lamps.Add((plc, item));
                }

                if ((item.IsButton || item.IsSwitch) && item.Outputs.Trim().Length > 0)
                {
                    var outputs = item.IsSwitch
                        ? item.OutputsByPosition().Select(pair => (pair.Key, Targets(pair.Value))).ToList()
                        : [("", Targets(item.Outputs))];
                    _cubicleOutputs.Add((plc, item, outputs));
                }

                List<PortBinding> Targets(string text)
                {
                    var targets = new List<PortBinding>();
                    foreach (var target in PortBinding.ParseList(text))
                    {
                        if (target.Kind != BindingKind.Signal || target.SignalKind is SimulatorSignalKind.DO or SimulatorSignalKind.AO)
                        {
                            problems.Add($"{plc}: {item.Tag} output '{target}' must be a PLC input (DI…).");
                            continue;
                        }

                        targets.Add(target);
                    }

                    return targets;
                }
            }

            foreach (var item in model.RelaySwitches.Where(item => item.Tag.Trim().Length > 0))
            {
                if (!_switches.TryAdd(Key(plc, item.Tag), item))
                {
                    problems.Add($"{plc}: switch {item.Tag} is defined more than once.");
                }
            }

            for (var sheetIndex = 0; sheetIndex < model.RelaySheets.Count; sheetIndex++)
            {
                var sheet = model.RelaySheets[sheetIndex];
                for (var path = 0; path < sheet.Circuits.Count; path++)
                {
                    var circuit = sheet.Circuits[path];
                    var state = new CoilState { Plc = plc, Sheet = sheet, Circuit = circuit, Location = Location(sheet, sheetIndex, path) };
                    foreach (var target in PortBinding.ParseList(circuit.Coil.Outputs))
                    {
                        if (target.Kind != BindingKind.Signal || target.SignalKind is SimulatorSignalKind.DO or SimulatorSignalKind.AO)
                        {
                            problems.Add($"{plc}/{sheet.Name} {Display(circuit.Coil.Tag, state.Location)}: output '{target}' must be a PLC input (DI, AI…).");
                            continue;
                        }

                        state.Outputs.Add(target);
                    }

                    var key = Key(plc, circuit.Coil.Tag);
                    if (circuit.Coil.Tag.Trim().Length > 0 && !_byTag.TryAdd(key, state))
                    {
                        problems.Add($"{plc}: relay {circuit.Coil.Tag} has more than one coil ({_byTag[key].Location} and {state.Location}).");
                    }

                    if (previous?._coils.FirstOrDefault(old => old.Circuit == circuit || (circuit.Coil.Tag.Trim().Length > 0 && Key(old.Plc, old.Circuit.Coil.Tag) == key)) is { } old)
                    {
                        state.On = old.On;
                        state.Elapsed = old.Elapsed;
                    }

                    _coils.Add(state);
                }
            }
        }

        // A path fed from a contact of another path is solved after it (unknown feeds are reported).
        var owner = new Dictionary<string, CoilState>(StringComparer.OrdinalIgnoreCase);
        foreach (var coil in _coils)
        {
            foreach (var element in AllElements(coil.Circuit.Elements))
            {
                owner.TryAdd(coil.Plc + "/" + element.Id, coil);
            }
        }

        var ordered = new List<CoilState>();
        var visiting = new HashSet<CoilState>();
        void Visit(CoilState coil)
        {
            if (!visiting.Add(coil))
            {
                return;
            }

            if (coil.Circuit.Feed.Length > 0)
            {
                if (owner.TryGetValue(coil.Plc + "/" + coil.Circuit.Feed, out var feeder) && feeder != coil)
                {
                    Visit(feeder);
                }
                else
                {
                    problems.Add($"{coil.Plc}/{coil.Sheet.Name} {Display(coil.Circuit.Coil.Tag, coil.Location)}: the contact this path branches off from no longer exists.");
                }
            }

            ordered.Add(coil);
        }

        foreach (var coil in _coils.ToList())
        {
            Visit(coil);
        }

        _coils.Clear();
        _coils.AddRange(ordered);
    }

    /// <summary>Cross-reference location of a path: "/page.path".</summary>
    public static string Location(RelaySheet sheet, int sheetIndex, int pathIndex) =>
        "/" + (sheet.Page > 0 ? sheet.Page : sheetIndex + 1).ToString(CultureInfo.InvariantCulture) + "." + (pathIndex + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Location of the coil of a relay ("/37.4"), or "" when the tag is not a relay of this PLC.</summary>
    public string CoilLocation(string plc, string tag) => _byTag.TryGetValue(Key(plc, tag), out var coil) ? coil.Location : "";

    /// <summary>Function text of the coil of a relay, or "".</summary>
    public string CoilText(string plc, string tag) => _byTag.TryGetValue(Key(plc, tag), out var coil) ? coil.Circuit.Coil.Text : "";

    public bool IsRelay(string plc, string tag) => _byTag.ContainsKey(Key(plc, tag));

    /// <summary>The cubicle push button with this tag, or null.</summary>
    public CubicleItem? ButtonOf(string plc, string tag) => _buttons.GetValueOrDefault(Key(plc, tag));

    /// <summary>The selector switch with this tag, or null.</summary>
    public RelaySwitch? SwitchOf(string plc, string tag) => _switches.GetValueOrDefault(Key(plc, tag));

    /// <summary>Relay picked up (coil energised, after its time delay).</summary>
    public bool IsOn(string plc, string tag) => _byTag.TryGetValue(Key(plc, tag), out var coil) && coil.On;

    public bool IsOn(RelayCircuit circuit) => _coils.FirstOrDefault(coil => coil.Circuit == circuit)?.On == true;

    /// <summary>Current reaches the coil of the path (a time relay may not have picked up yet).</summary>
    public bool IsPowered(RelayCircuit circuit) => _coils.FirstOrDefault(coil => coil.Circuit == circuit)?.Powered == true;

    /// <summary>Last state of a contact: current reaches its common terminal / the contact is closed.</summary>
    public (bool PoweredIn, bool Closed) StateOf(RelayElement element) => _elements.TryGetValue(element, out var state) ? state : (false, false);

    /// <summary>Contacts of other paths and sheets that use a relay: (location of the path, NO/NC, pins).</summary>
    public List<(string Location, RelayElement Contact)> ContactsOf(string plc, string tag)
    {
        var result = new List<(string, RelayElement)>();
        foreach (var coil in _coils.Where(coil => coil.Plc.Equals(plc, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var contact in Contacts(coil.Circuit.Elements).Where(contact => contact.Tag.Trim().Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                result.Add((coil.Location, contact));
            }
        }

        return result;
    }

    /// <summary>Lamp lit (its source is on), from the last step.</summary>
    public bool IsLampOn(CubicleItem lamp) => _lampStates.TryGetValue(lamp, out var on) && on;

    public void Step(double dt, Func<string, ISignalIo?> io, ProcessEngine engine)
    {
        StepCubicles(io, engine);
        if (_coils.Count == 0)
        {
            return;
        }

        // Normal relays follow their path at once; repeat until nothing changes (a relay contact in another path).
        for (var pass = 0; pass < MaxSettlePasses; pass++)
        {
            var changed = false;
            foreach (var coil in _coils)
            {
                coil.Powered = Solve(coil.Plc, coil.Circuit.Elements, FeedPowered(coil), io(coil.Plc), engine);
                if (coil.Circuit.Coil.Timer.Length == 0 && coil.On != coil.Powered)
                {
                    coil.On = coil.Powered;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        foreach (var coil in _coils.Where(coil => coil.Circuit.Coil.Timer.Length > 0))
        {
            var delay = Math.Max(0, coil.Circuit.Coil.Time);
            if (coil.Circuit.Coil.Timer.Equals(RelayTimer.OffDelay, StringComparison.OrdinalIgnoreCase))
            {
                coil.Elapsed = coil.Powered ? 0 : coil.Elapsed + dt;
                coil.On = coil.Powered || (coil.On && coil.Elapsed < delay);
            }
            else
            {
                coil.Elapsed = coil.Powered ? coil.Elapsed + dt : 0;
                coil.On = coil.Powered && coil.Elapsed >= delay;
            }
        }

        foreach (var coil in _coils)
        {
            var plcIo = io(coil.Plc);
            foreach (var target in coil.Outputs)
            {
                plcIo?.Write(target.SignalKind, target.SignalId, coil.On ? 1 : 0);
            }
        }
    }

    /// <summary>Push buttons and switches write their PLC inputs; lamps read their source (after the relays of the last step).</summary>
    private void StepCubicles(Func<string, ISignalIo?> io, ProcessEngine engine)
    {
        foreach (var (plc, item, outputs) in _cubicleOutputs)
        {
            var plcIo = io(plc);
            if (plcIo is null)
            {
                continue;
            }

            foreach (var (position, targets) in outputs)
            {
                var on = item.IsSwitch
                    ? _switches.TryGetValue(Key(plc, item.Tag), out var selector) && selector.Current.Equals(position, StringComparison.OrdinalIgnoreCase)
                    : item.Contact.Equals("NC", StringComparison.OrdinalIgnoreCase) ? !item.Pressed : item.Pressed;
                foreach (var target in targets)
                {
                    plcIo.Write(target.SignalKind, target.SignalId, on ? 1 : 0);
                }
            }
        }

        foreach (var (plc, lamp) in _lamps)
        {
            var source = lamp.Source.Trim();
            var probe = source.StartsWith('-') ? new RelayElement { Tag = source } : new RelayElement { Source = source };
            _lampStates[lamp] = SourceOn(plc, probe, io(plc), engine);
        }
    }

    public void Reset()
    {
        foreach (var coil in _coils)
        {
            coil.On = false;
            coil.Powered = false;
            coil.Elapsed = 0;
        }

        _elements.Clear();
    }

    /// <summary>Current at the start of a path: the L+ rail, or the output of the element it branches off from.</summary>
    private bool FeedPowered(CoilState coil)
    {
        if (coil.Circuit.Feed.Length == 0)
        {
            return true;
        }

        var feed = _elements.Keys.FirstOrDefault(element => element.Id.Equals(coil.Circuit.Feed, StringComparison.OrdinalIgnoreCase));
        return feed is not null && _elements[feed] is { PoweredIn: true, Closed: true };
    }

    /// <summary>Contacts and parallel groups of a series, nested ones included.</summary>
    public static IEnumerable<RelayElement> AllElements(IEnumerable<RelayElement> series)
    {
        foreach (var element in series)
        {
            yield return element;
            foreach (var nested in element.Branches.SelectMany(AllElements))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Current through a series of elements: in at the top, out at the bottom.</summary>
    private bool Solve(string plc, List<RelayElement> series, bool poweredIn, ISignalIo? io, ProcessEngine engine)
    {
        var powered = poweredIn;
        foreach (var element in series)
        {
            if (element.IsParallel)
            {
                var any = false;
                foreach (var branch in element.Branches)
                {
                    any |= Solve(plc, branch, powered, io, engine);
                }

                _elements[element] = (powered, any);
                powered = any;
                continue;
            }

            var closed = element.IsNormallyClosed ? !SourceOn(plc, element, io, engine) : SourceOn(plc, element, io, engine);
            _elements[element] = (powered, closed);
            powered &= closed;
        }

        return powered;
    }

    private bool SourceOn(string plc, RelayElement contact, ISignalIo? io, ProcessEngine engine)
    {
        if (contact.Tag.Trim().Length > 0 && _byTag.TryGetValue(Key(plc, contact.Tag), out var coil))
        {
            return coil.On;
        }

        if (contact.Tag.Trim().Length > 0 && _buttons.TryGetValue(Key(plc, contact.Tag), out var button))
        {
            return button.Pressed; // operated while held down on the cubicle
        }

        if (contact.Tag.Trim().Length > 0 && _switches.TryGetValue(Key(plc, contact.Tag), out var selector))
        {
            // Operated in the positions listed on the contact (switching table).
            var current = selector.Current;
            return contact.Position.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(position => position.Equals(current, StringComparison.OrdinalIgnoreCase));
        }

        var binding = PortBinding.Parse(contact.Source);
        switch (binding.Kind)
        {
            case BindingKind.Constant:
                return binding.Constant >= 0.5;
            case BindingKind.Signal:
                return io is not null && io.TryRead(binding.SignalKind, binding.SignalId, out var value) && value >= 0.5;
            case BindingKind.Link:
                var name = binding.LinkObject.Contains('/') ? binding.LinkObject.Split('/', 2) : [plc, binding.LinkObject];
                return engine.Find(name[0], name[1])?.Value(binding.LinkPort) >= 0.5;
            default:
                return false;
        }
    }

    private static IEnumerable<RelayElement> Contacts(IEnumerable<RelayElement> series)
    {
        foreach (var element in series)
        {
            if (element.IsParallel)
            {
                foreach (var contact in element.Branches.SelectMany(Contacts))
                {
                    yield return contact;
                }
            }
            else
            {
                yield return element;
            }
        }
    }

    private static string Key(string plc, string tag) => plc + "/" + tag.Trim();

    private static string Display(string tag, string location) => tag.Trim().Length > 0 ? tag : "path " + location;
}

/// <summary>
/// Copies a relay-logic sheet for the same circuit with other devices and signals. Rules "old=new", one per line:
/// tags, sources and coil outputs are replaced as whole items ("-K107=-K207", "DI:64=DI:84") or by a prefix with a
/// trailing '*' ("-K1*=-K2*" makes -K107 → -K207); texts and the sheet name get every occurrence replaced
/// ("GATE 1=GATE 2").
/// </summary>
public static class RelaySheetCopy
{
    public static List<(string Old, string New)> ParseRules(string text) =>
        (text ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains('='))
            .Select(line => (Old: line[..line.IndexOf('=')].Trim(), New: line[(line.IndexOf('=') + 1)..].Trim()))
            .Where(rule => rule.Old.Length > 0)
            .ToList();

    public static RelaySheet Copy(RelaySheet source, IReadOnlyList<(string Old, string New)> rules)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copy = new RelaySheet
        {
            Name = ReplaceText(source.Name, rules),
            Page = 0,
            Circuits = source.Circuits.Select(circuit => CopyCircuit(circuit, rules, ids)).ToList()
        };

        // A path branching off a contact of the copied sheet branches off the copy of that contact.
        foreach (var circuit in copy.Circuits.Where(circuit => circuit.Feed.Length > 0))
        {
            circuit.Feed = ids.TryGetValue(circuit.Feed, out var id) ? id : circuit.Feed;
        }

        return copy;
    }

    private static RelayCircuit CopyCircuit(RelayCircuit circuit, IReadOnlyList<(string Old, string New)> rules, Dictionary<string, string> ids) => new()
        {
            Feed = circuit.Feed,
            Elements = CopySeries(circuit.Elements, rules, ids),
            Coil = new RelayCoil
            {
                Tag = ReplaceItem(circuit.Coil.Tag, rules),
                Text = ReplaceText(circuit.Coil.Text, rules),
                Outputs = ReplaceList(circuit.Coil.Outputs, rules),
                Timer = circuit.Coil.Timer,
                Time = circuit.Coil.Time
            }
        };

    private static List<RelayElement> CopySeries(List<RelayElement> series, IReadOnlyList<(string Old, string New)> rules, Dictionary<string, string> ids) =>
        series.Select(element => new RelayElement
        {
            Id = ids[element.Id] = RelayElement.NewId(),
            Kind = element.Kind,
            Tag = ReplaceItem(element.Tag, rules),
            Source = ReplaceList(element.Source, rules),
            Contact = element.Contact,
            Pins = element.Pins,
            Position = element.Position,
            Text = ReplaceText(element.Text, rules),
            Branches = element.Branches.Select(branch => CopySeries(branch, rules, ids)).ToList()
        }).ToList();

    /// <summary>A whole item: exact rule first, then the longest matching prefix rule ("-K1*").</summary>
    public static string ReplaceItem(string value, IReadOnlyList<(string Old, string New)> rules)
    {
        var item = (value ?? "").Trim();
        if (item.Length == 0)
        {
            return value ?? "";
        }

        foreach (var (old, @new) in rules.Where(rule => !rule.Old.EndsWith('*')))
        {
            if (item.Equals(old, StringComparison.OrdinalIgnoreCase))
            {
                return @new;
            }
        }

        foreach (var (old, @new) in rules.Where(rule => rule.Old.EndsWith('*')).OrderByDescending(rule => rule.Old.Length))
        {
            var prefix = old[..^1];
            if (prefix.Length > 0 && item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return @new.TrimEnd('*') + item[prefix.Length..];
            }
        }

        return item;
    }

    private static string ReplaceList(string value, IReadOnlyList<(string Old, string New)> rules) =>
        string.Join(", ", (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => ReplaceItem(item, rules)));

    private static string ReplaceText(string value, IReadOnlyList<(string Old, string New)> rules)
    {
        var text = value ?? "";
        foreach (var (old, @new) in rules.Where(rule => !rule.Old.EndsWith('*')))
        {
            text = text.Replace(old, @new, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
