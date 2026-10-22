using System.IO;

namespace TiaTraceStudio;

public sealed class SignalNameLibrary
{
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SignalScaling> Scaling { get; set; } = new(StringComparer.Ordinal);
    public static SignalNameLibrary Load(string path) => File.Exists(path) ? ProjectStore.Deserialize<SignalNameLibrary>(File.ReadAllText(path)) : new();
    public void Save(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); ProjectStore.Save(path, this); }
    public void Apply(StudioProject project, bool onlyEmpty = false)
    {
        foreach (var signal in project.Data.Signals)
        {
            if (!signal.NameOverride && (!onlyEmpty || string.IsNullOrWhiteSpace(signal.Name)) && Names.TryGetValue(signal.Source, out var name)) signal.Name = name;
            if (!signal.NameOverride && Scaling.TryGetValue(signal.Source, out var scaling)) scaling.Apply(signal);
        }
    }
    public int Share(Signal source, IEnumerable<StudioProject> projects, bool includeOverrides = false)
    {
        if (string.IsNullOrWhiteSpace(source.Source)) throw new InvalidDataException("Nedostaje izvorni tag signala.");
        var scaling = new SignalScaling(source.Unit, source.Factor, source.Offset);
        scaling.Validate();
        Scaling[source.Source] = scaling;
        if (!string.IsNullOrWhiteSpace(source.Name)) Names[source.Source] = source.Name.Trim();
        int count = 0;
        foreach (var signal in projects.SelectMany(p => p.Data.Signals).Where(s => s.Source == source.Source && (includeOverrides || !s.NameOverride)))
        {
            if (Names.TryGetValue(source.Source, out var name)) signal.Name = name;
            scaling.Apply(signal);
            if (includeOverrides) signal.NameOverride = false;
            count++;
        }
        return count;
    }
    public int Rename(string source, string name, IEnumerable<StudioProject> projects, bool includeOverrides = false)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Unesi naziv signala prije spremanja u bazu.");
        Names[source] = name.Trim(); int count = 0;
        foreach (var signal in projects.SelectMany(p => p.Data.Signals).Where(s => s.Source == source && (includeOverrides || !s.NameOverride)))
        { signal.Name = name.Trim(); if (includeOverrides) signal.NameOverride = false; count++; }
        return count;
    }
}

public sealed record SignalScaling(string Unit, double Factor, double Offset)
{
    public void Apply(Signal signal) { signal.Unit = Unit; signal.Factor = Factor; signal.Offset = Offset; }
    public void Validate()
    {
        if (Unit == null || !double.IsFinite(Factor) || !double.IsFinite(Offset)) throw new InvalidDataException("Neispravna jedinica ili scaling u bazi signala.");
    }
}

public sealed class TraceSession
{
    public int Version { get; set; } = 3;
    public int ActiveIndex { get; set; }
    public PlantDefinition? Plant { get; set; }
    public List<TestSlot> Slots { get; set; } = [];
    public TestCatalog? Catalog { get; set; }
    public PlantConfigLink? PlantConfig { get; set; }
    public Dictionary<string, TagAddress> TagAddresses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StudioProject> Projects { get; set; } = [];
    public SignalNameLibrary NameLibrary { get; set; } = new();
    public List<TraceView> Views { get; set; } = [];
    public static TraceSession Load(string path)
    {
        if (path.EndsWith(".tiatrace", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = new TraceSession { Projects = [ProjectStore.Load(path)] }; legacy.SeedNames(); return legacy;
        }
        var session = ProjectStore.Deserialize<TraceSession>(File.ReadAllText(path));
        if (session.Version is not (1 or 2 or 3)) throw new InvalidDataException("Neispravan ili nepodržan projekt.");
        foreach (var project in session.Projects) { if (project.Version != 1) throw new InvalidDataException("Nepodržana verzija projekta."); PlotService.Validate(project); }
        if (session.NameLibrary?.Names == null || session.NameLibrary.Names.Any(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value))) throw new InvalidDataException("Neispravna baza naziva projekta.");
        if (session.NameLibrary.Scaling == null || session.NameLibrary.Scaling.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null)) throw new InvalidDataException("Neispravna baza scalinga projekta.");
        foreach (var scaling in session.NameLibrary.Scaling.Values) scaling.Validate();
        if (session.Views == null || session.Views.Any(v => !double.IsFinite(v.Left) || !double.IsFinite(v.Right) || v.Left >= v.Right)) throw new InvalidDataException("Neispravan vremenski pogled projekta.");
        if (session.Version == 1) session.SeedNames();
        session.SeedMissingScaling();
        session.Slots ??= [];
        if (session.Slots.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id)) || session.Slots.Select(s => s.Id).Distinct().Count() != session.Slots.Count) throw new InvalidDataException("Neispravno stablo testova projekta.");
        session.Plant?.Validate();
        foreach (var slot in session.Slots.Where(s => s.Tags != null))
        { slot.Channels ??= slot.Tags!.Select(t => new SlotChannel(t)).ToList(); slot.Tags = null; }
        session.PlantConfig?.Validate();
        session.TagAddresses = new(session.TagAddresses ?? [], StringComparer.OrdinalIgnoreCase);
        session.Catalog = session.Plant == null ? session.Catalog : (session.Catalog ?? TestCatalog.Default()).WithDefaults();
        session.Version = 3;
        return session;
    }
    void SeedNames()
    {
        foreach (var signal in Projects.SelectMany(p => p.Data.Signals))
        {
            if (!signal.NameOverride && !string.IsNullOrWhiteSpace(signal.Source) && !string.IsNullOrWhiteSpace(signal.Name)) NameLibrary.Names.TryAdd(signal.Source, signal.Name);
        }
        SeedMissingScaling();
    }
    void SeedMissingScaling()
    {
        // Older projects stored units and scaling only in individual recordings.
        // Prefer configured recordings over freshly imported CSV defaults, while
        // preserving explicit library entries and local exceptions.
        foreach (var group in Projects.SelectMany(p => p.Data.Signals)
                     .Where(s => !s.NameOverride && !string.IsNullOrWhiteSpace(s.Source))
                     .GroupBy(s => s.Source, StringComparer.Ordinal))
        {
            if (NameLibrary.Scaling.ContainsKey(group.Key)) continue;
            var source = group.FirstOrDefault(s => !HasDefaultScaling(s)) ?? group.First();
            var scaling = new SignalScaling(source.Unit, source.Factor, source.Offset);
            NameLibrary.Scaling.Add(group.Key, scaling);
            foreach (var signal in group.Where(HasDefaultScaling)) scaling.Apply(signal);
        }
    }
    static bool HasDefaultScaling(Signal signal) => string.IsNullOrWhiteSpace(signal.Unit) && signal.Factor == 1 && signal.Offset == 0;
}
public record TraceView(double Left, double Right, int SelectedSignal = 0, string? SlotId = null);
