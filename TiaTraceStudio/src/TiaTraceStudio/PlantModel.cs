using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TiaTraceStudio;

public static class Turbines
{
    public const string Pelton = "Pelton", Francis = "Francis", Kaplan = "Kaplan", CrossFlow = "Cross Flow";
    public static readonly string[] All = [Pelton, Francis, Kaplan, CrossFlow];
}

public static class TestPhases
{
    public const string Dry = "1 · Dry test", Wet = "2 · Wet test", Grid = "3 · Testovi na mreži", Common = "Zajednički testovi", User = "Korisnička snimanja";
    public static readonly string[] All = [Dry, Wet, Grid];
}

public class PlantDefinition
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public List<UnitDefinition> Units { get; set; } = [];
    public List<PlcConnection> Plcs { get; set; } = [];
    /// <summary>User-built groups of the tree (e.g. Intake, Common systems), each with its own PLC / database.</summary>
    public List<CustomGroup> CustomGroups { get; set; } = [];
    /// <summary>Plants with several units: load rejections of all units together, recorded from all PLCs at once.</summary>
    public string CommonLoadRejectionLevels { get; set; } = "25, 50, 75, 100";
    /// <summary>Names the user gave to generated tree nodes (unit, phase, group), by tree key.</summary>
    public Dictionary<string, string> TreeNames { get; set; } = [];
    /// <summary>Word report: chosen company template and the texts replaced in it, per template.</summary>
    public ReportSettings Report { get; set; } = new();
    public PlcConnection? PlcForUnit(int unit) => Plcs.FirstOrDefault(p => p.UnitNumbers().Contains(unit));
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Unesi naziv postrojenja.");
        if (Units.Count is < 1 or > 12) throw new InvalidDataException("Postrojenje mora imati 1–12 agregata.");
        foreach (var unit in Units) unit.Validate();
        if (Units.Count > 1) UnitDefinition.ParseLevels(CommonLoadRejectionLevels ?? "", "Zajednički testovi");
    }
}

public class UnitDefinition : Observable
{
    int number = 1, nozzles = 2, deflectors = 1, sections = 1;
    string turbine = Turbines.Pelton, loadLevels = "25, 50, 75, 100";
    bool miv = true, bypass = true, excitation = true;
    bool speedControl = true, powerControl = true, levelControl = true, openingControl, voltageControl = true, reactiveControl;
    public int Number { get => number; set { Set(ref number, value); Notify(nameof(Summary)); } }
    public string Turbine { get => turbine; set { Set(ref turbine, value); Notify(nameof(Summary)); } }
    public int Nozzles { get => nozzles; set { Set(ref nozzles, value); Notify(nameof(Summary)); } }
    public int Deflectors { get => deflectors; set { Set(ref deflectors, value); Notify(nameof(Summary)); } }
    public int GuideVaneSections { get => sections; set { Set(ref sections, value); Notify(nameof(Summary)); } }
    public bool HasMiv { get => miv; set => Set(ref miv, value); }
    public bool HasBypass { get => bypass; set => Set(ref bypass, value); }
    public bool HasExcitation { get => excitation; set => Set(ref excitation, value); }
    bool vibration;
    /// <summary>Unit has vibration measurement (MEAS 40–43), recorded with load rejections.</summary>
    public bool HasVibration { get => vibration; set => Set(ref vibration, value); }
    public bool SpeedControl { get => speedControl; set => Set(ref speedControl, value); }
    public bool PowerControl { get => powerControl; set => Set(ref powerControl, value); }
    public bool LevelControl { get => levelControl; set => Set(ref levelControl, value); }
    public bool OpeningControl { get => openingControl; set => Set(ref openingControl, value); }
    public bool VoltageControl { get => voltageControl; set => Set(ref voltageControl, value); }
    public bool ReactiveControl { get => reactiveControl; set => Set(ref reactiveControl, value); }
    public string LoadRejectionLevels { get => loadLevels; set => Set(ref loadLevels, value); }

    /// <summary>Rated values of this unit's measurements: the 0 % and 100 % of the normalized graph.</summary>
    public List<NominalRange> NominalRanges { get; set; } = [];
    public NominalRange? NominalFor(string tag) => NominalRanges.FirstOrDefault(r => TagMatcher.Matches(r.Tag, tag) && r.Min.HasValue && r.Max.HasValue);
    [JsonIgnore] public string Title => $"Agregat {Number}";
    [JsonIgnore] public string Summary => Turbine switch
    {
        Turbines.Pelton => $"{Title} · Pelton · {Nozzles} mlazn. · {Deflectors} defl.",
        Turbines.CrossFlow => $"{Title} · Cross Flow · {GuideVaneSections} sekc.",
        _ => $"{Title} · {Turbine}"
    };
    public int[] LoadLevels() => ParseLevels(LoadRejectionLevels, Title);
    public static int[] ParseLevels(string text, string owner)
    {
        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        var levels = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim().TrimEnd('%'), out int level) || level is < 1 or > 120)
                throw new InvalidDataException($"{owner}: razine odbacivanja tereta unesi kao cijele postotke 1–120, npr. 25, 50, 75, 100.");
            if (!levels.Contains(level)) levels.Add(level);
        }
        return levels.Order().ToArray();
    }
    public void Validate()
    {
        if (!Turbines.All.Contains(Turbine)) throw new InvalidDataException($"{Title}: odaberi tip turbine.");
        if (Turbine == Turbines.Pelton && (Nozzles is < 1 or > 6 || Deflectors is < 0 or > 6)) throw new InvalidDataException($"{Title}: Pelton ima 1–6 mlaznica i 0–6 deflektora.");
        if (Turbine == Turbines.CrossFlow && GuideVaneSections is < 1 or > 3) throw new InvalidDataException($"{Title}: Cross Flow ima 1–3 sekcije privodnog aparata.");
        NominalRanges ??= [];
        foreach (var range in NominalRanges)
        {
            if (string.IsNullOrWhiteSpace(range.Tag)) throw new InvalidDataException($"{Title}: nazivna vrijednost bez taga.");
            if (range.Min.HasValue != range.Max.HasValue || range.Min.HasValue && !(double.IsFinite(range.Min!.Value) && double.IsFinite(range.Max!.Value) && range.Min < range.Max))
                throw new InvalidDataException($"{Title}: za '{(range.Name.Length > 0 ? range.Name : range.Tag)}' unesi 0 % i 100 %, s 0 % manjim od 100 %.");
        }
        LoadLevels();
    }
    /// <summary>Sub-groups the user added to the unit's tree, as "phase/path" (kept even while empty).</summary>
    public List<string> Folders { get; set; } = [];

    public UnitDefinition CopyAs(int number)
    {
        var copy = ProjectStore.Clone(this); copy.Number = number; return copy;
    }
}

public class ReportSettings
{
    /// <summary>"Geppert", "PrimeVolt" or "" (basic report without a template).</summary>
    public string Company { get; set; } = "Geppert";
    /// <summary>Per company: text in the template → text in this project's report (header, cover, signatures).</summary>
    public Dictionary<string, Dictionary<string, string>> Replacements { get; set; } = [];
    /// <summary>Required document data of the report (document number, project, title, author, customer, section, date).</summary>
    public Dictionary<string, string> Fields { get; set; } = [];
}

/// <summary>A group of the tree that is not a unit; the user builds its sub-groups and recordings.</summary>
public class CustomGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>PLC (IP and Plant Configurator database) its recordings use by default.</summary>
    public string PlcId { get; set; } = "";
    /// <summary>Sub-group paths ("Zapornica", "Zapornica/Hidraulika"), kept even while empty.</summary>
    public List<string> Folders { get; set; } = [];
}

/// <summary>Rated range of one measurement of a unit (normalized graph: Min = 0 %, Max = 100 %).</summary>
public class NominalRange : Observable
{
    string tag = "", name = "", unit = ""; double? min, max;
    public string Tag { get => tag; set => Set(ref tag, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string Unit { get => unit; set => Set(ref unit, value); }
    public double? Min { get => min; set => Set(ref min, value); }
    public double? Max { get => max; set => Set(ref max, value); }
}

/// <summary>One expected recording (or manual entry) in the commissioning tree.</summary>
public class TestSlot
{
    public string Id { get; set; } = "";
    public int Unit { get; set; }
    public string Phase { get; set; } = "";
    /// <summary>Tree path below the phase, separated by '/'. Empty places the test directly under the phase.</summary>
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Name given by the user; kept when the tree is regenerated from the plant definition.</summary>
    public string? CustomTitle { get; set; }
    public string Kind { get; set; } = "";
    public int Index { get; set; }
    public bool Manual { get; set; }
    public bool Reviewed { get; set; }
    public bool NotApplicable { get; set; }
    /// <summary>Removed from the tree by the user; generated tests stay hidden after the plant definition changes.</summary>
    public bool Deleted { get; set; }
    public string Note { get; set; } = "";
    public double? OpeningTime { get; set; }
    public double? ClosingTime { get; set; }
    /// <summary>Default PLC of a user-defined recording (tests of a unit use the unit's PLC).</summary>
    public string PlcId { get; set; } = "";
    /// <summary>User recordings placed in a user-built group (empty = "Korisnička snimanja").</summary>
    public string GroupId { get; set; } = "";
    /// <summary>Measurements chosen for this test; null = the catalogue's default list for the test kind.</summary>
    public List<SlotChannel>? Channels { get; set; }
    /// <summary>Tag list of version 1.7 user recordings, migrated to <see cref="Channels"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Tags { get; set; }
    [JsonIgnore] public bool IsUser => Kind == PlantPlanner.UserKind;
    [JsonIgnore] public bool IsCommon => Kind.StartsWith("COMMON_", StringComparison.Ordinal);
    /// <summary>No longer produced by the plant definition, kept because it holds recordings or entries.</summary>
    public bool Retired { get; set; }
    [JsonIgnore] public bool HasManualValues => OpeningTime.HasValue || ClosingTime.HasValue;
    /// <summary>Opening and closing times are entered: MIV / bypass, and the opening/closing test of every turbine valve.</summary>
    [JsonIgnore] public bool HasTimes => Manual || Kind.EndsWith("_OPEN_CLOSE", StringComparison.Ordinal);
    [JsonIgnore] public string PhaseName => Phase.Contains(" · ") ? Phase[(Phase.IndexOf(" · ") + 3)..] : Phase;
    [JsonIgnore] public string Path => string.Join(" · ", new[] { Unit > 0 ? $"Agregat {Unit}" : "", PhaseName, Group.Replace("/", " · "), Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
    /// <summary>Short chart title: the actuator or group plus the test, without phase and tree folders.</summary>
    [JsonIgnore] public string ChartTitle
    {
        get
        {
            string group = Group.Split('/').Last();
            return string.Join(" · ", new[] { Unit > 0 ? $"Agregat {Unit}" : "", Title.StartsWith(group, StringComparison.OrdinalIgnoreCase) ? "" : group, Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }
}

/// <summary>One measurement of a test: the tag and the PLC it is read from (empty = the test's default PLC).</summary>
public sealed record SlotChannel(string Tag, string PlcId = "");

public class TestKind
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Help { get; set; } = "";
    /// <summary>Tags to record. {n} is replaced by the valve number / load level, {u} by the unit number.</summary>
    public List<string> Signals { get; set; } = [];
}

public class TestCatalog
{
    public int Version { get; set; } = 2;
    public List<TestKind> Kinds { get; set; } = [];
    public TestKind? Find(string key) => Kinds.FirstOrDefault(k => k.Key == key);
    /// <summary>
    /// Catalogue default tags of a test; {u} is the given unit (common tests are expanded per unit),
    /// {valves} all turbine valves of the unit and {vibrations} its vibration measurements (if it has them).
    /// </summary>
    public List<string> ExpectedTags(TestSlot slot, int? unit = null, UnitDefinition? definition = null)
    {
        if (slot.IsUser) return [];
        var kind = Find(slot.Kind);
        // Common tests reuse the per-unit list of their base test until they get their own tags.
        if (slot.IsCommon && (kind == null || kind.Signals.Count == 0)) kind = Find(slot.Kind["COMMON_".Length..]);
        var result = new List<string>();
        foreach (var raw in kind?.Signals.Where(s => !string.IsNullOrWhiteSpace(s)) ?? [])
        {
            string tag = raw.Trim();
            if (tag.Equals("{valves}", StringComparison.OrdinalIgnoreCase)) { if (definition != null) result.AddRange(PlantPlanner.ValveTags(definition)); continue; }
            if (tag.Equals("{vibrations}", StringComparison.OrdinalIgnoreCase)) { if (definition?.HasVibration == true) result.AddRange(PlantPlanner.VibrationTags); continue; }
            result.Add(tag.Replace("{n}", slot.Index.ToString()).Replace("{u}", (unit ?? slot.Unit).ToString()));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public string HelpFor(TestSlot slot) => slot.IsUser
        ? "Korisničko snimanje: slobodan popis mjerenja s odabranog PLC-a (npr. Common ili Intake).\n\nU prozoru snimanja dodaj mjerenja iz Plant Configuratora ili upiši tag i adresu. Popis se pamti za sljedeće snimanje. Snimka se može i učitati iz TIA trace CSV-a."
        : (Find(slot.Kind)?.Help ?? "").Replace("{n}", slot.Index.ToString()).Replace("{u}", slot.Unit.ToString());

    static string GlobalPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaTraceStudio", "test-catalog.json");
    public static TestCatalog LoadGlobal()
    {
        try { if (File.Exists(GlobalPath)) return ProjectStore.Deserialize<TestCatalog>(File.ReadAllText(GlobalPath)).WithDefaults(); }
        catch { /* a damaged default catalogue must not block new projects */ }
        return Default();
    }
    public void SaveGlobal() { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GlobalPath)!); ProjectStore.Save(GlobalPath, this); }
    /// <summary>Adds test kinds introduced by newer versions without touching edited ones.</summary>
    public TestCatalog WithDefaults()
    {
        Kinds ??= [];
        foreach (var kind in Default().Kinds) if (Find(kind.Key) == null) Kinds.Add(kind);
        if (Version < 2)
        {
            // Version 2 brings default measurements for HPU, start-up, stops and load rejection; lists the user edited stay.
            foreach (var fresh in Default().Kinds.Where(k => k.Key is "HPU_PRESSURE_DROP" or "STARTUP" or "BEARING_RUN" or "STOP_NSD" or "STOP_QSD" or "STOP_ESD" or "LOAD_REJECTION"))
                if (Find(fresh.Key) is TestKind old && old.Signals.All(t => t is "DI#23" or "DI#24")) old.Signals = fresh.Signals.ToList();
            Version = 2;
        }
        // Generator breaker state is part of every load rejection (DI 23 / 24 in every PLC).
        if (Find("LOAD_REJECTION") is TestKind rejection && !rejection.Signals.Any(t => t.StartsWith("DI#", StringComparison.OrdinalIgnoreCase))) rejection.Signals.AddRange(["DI#23", "DI#24"]);
        foreach (var kind in Kinds) { kind.Signals ??= []; kind.Help ??= ""; }
        return this;
    }

    public static TestCatalog Default()
    {
        var catalog = new TestCatalog();
        void Add(string key, string title, string help, params string[] signals) => catalog.Kinds.Add(new() { Key = key, Title = title, Help = help, Signals = signals.ToList() });
        foreach (var valve in PlantPlanner.ValveTypes)
        {
            string? element = valve.Key switch
            {
                "NOZZLE" => "PELTON_NOZZLE[{n}]", "DEFLECTOR" => "PELTON_DEFLECTOR[{n}]", "WICKET_GATE" => "FRANCIS_DISTRIBUTOR",
                "KAPLAN_GATE" => "KAPLAN_DISTRIBUTOR", "RUNNER" => "KAPLAN_RUNNER", "CF_SECTION" => "CROSS_FLOW_VALVE_{n}", _ => null
            };
            var tags = new List<string>();
            if (valve.Key == "NOZZLE") tags.Add("MEAS.TURBINE_AND_HPU.NOZZLE_{n}.VAL");
            if (element != null) tags.AddRange([$"REG_INTF.{element}.VALUES.SCALED_REFERENCE", $"REG_INTF.{element}.VALUES.OUT", $"REG_INTF.{element}.VALUES.SCALED_MEAS"]);
            string[] valveTags = [.. tags];
            Add(valve.Key + "_OPEN_CLOSE", $"{valve.Generic} – brzina otvaranja / zatvaranja",
                $"Cilj: izmjeriti vrijeme punog otvaranja i zatvaranja ({valve.Generic.ToLowerInvariant()}) i usporediti ga s projektnim vremenom.\n\n" +
                "Priprema:\n• Dry test – MIV zatvoren, nema vode u turbini.\n• HPU u radu na nazivnom tlaku.\n• Ventil u ručnom / test režimu.\n\n" +
                "Koraci:\n1. Pokreni trace (okidač na promjenu reference ili ručno).\n2. Zadaj otvaranje 0 → 100 % najvećom brzinom i pričekaj krajnji položaj.\n3. Zadaj zatvaranje 100 → 0 %.\n4. Ponovi 2–3 puta zbog ponovljivosti.\n\n" +
                "Što očitati: vrijeme otvaranja i zatvaranja (kursori A/B, npr. 0–100 % ili 10–90 % prema specifikaciji), linearnost hoda, prebačaj na krajnjim položajima, pad tlaka HPU tijekom hoda.\n\nPreporučeni takt: 100 ms (snimanje s PLC-a) ili brže TIA traceom.", valveTags);
            Add(valve.Key + "_STEP", $"{valve.Generic} – odziv na step reference",
                "Cilj: provjeriti odziv pozicijske regulacije na skokovitu promjenu reference.\n\n" +
                "Koraci:\n1. Postavi radnu točku, npr. 50 %.\n2. Pokreni trace i zadaj step +5 %, pričekaj smirenje, zatim −5 %.\n3. Ponovi s većim stepom (npr. ±10 %) i u drugim radnim točkama (npr. 20 % i 80 %).\n\n" +
                "Što očitati: vrijeme porasta, prebačaj, vrijeme smirenja i statičku grešku. Snimi referencu, stvarni položaj i izlaz regulatora.\n\nPreporučeni takt: 100 ms (snimanje s PLC-a) ili brže TIA traceom.", valveTags);
            Add(valve.Key + "_RAMP", $"{valve.Generic} – odziv na rampu reference",
                "Cilj: provjeriti praćenje reference pri rampi.\n\n" +
                "Koraci:\n1. Pokreni trace.\n2. Zadaj rampu reference 0 → 100 % i 100 → 0 % zadanom brzinom (npr. sporo i nazivno).\n\n" +
                "Što očitati: kašnjenje i grešku praćenja između reference i položaja (u alatu Izvedeni signali dodaj A − B), te ima li zastoja ili titranja.\n\nPreporučeni takt: 100 ms.", valveTags);
        }
        Add("HPU_PRESSURE_DROP", "HPU – pad tlaka pri ESD tlaku",
            "Cilj: provjeriti može li HPU (akumulator) sigurno zatvoriti sve ventile kad je na minimalnom (ESD) tlaku.\n\n" +
            "Priprema:\n• Svi turbinski ventili otvoreni.\n• Pumpe HPU blokirane, tlak spušten na ESD razinu.\n\n" +
            "Koraci:\n1. Pokreni trace.\n2. Zadaj zatvaranje svih ventila (ESD naredba) bez dopune pumpama.\n3. Snimaj do potpunog zatvaranja i smirenja tlaka.\n\n" +
            "Što očitati: početni i završni tlak, ukupni pad tlaka, jesu li svi ventili potpuno zatvoreni i za koje vrijeme. Završni tlak mora ostati iznad minimalnog dopuštenog.",
            "MEAS#13", "{valves}");
        Add("MIV_TIMES", "MIV – vrijeme otvaranja i zatvaranja",
            "MIV najčešće nema mjerenje položaja.\n\nIzmjeri vrijeme otvaranja i zatvaranja (štoperica ili vrijeme između krajnjih sklopki) i upiši ga u polja ispod. Ako postoji snimka, učitaj je u ovaj test.");
        Add("BYPASS_TIMES", "Bypass – vrijeme otvaranja i zatvaranja",
            "Bypass ventil najčešće nema mjerenje položaja.\n\nIzmjeri vrijeme otvaranja i zatvaranja (štoperica ili vrijeme između krajnjih sklopki) i upiši ga u polja ispod. Ako postoji snimka, učitaj je u ovaj test.");
        Add("STARTUP", "Zalet",
            "Cilj: snimiti zalet od mirovanja do nazivne brzine u praznom hodu.\n\n" +
            "Priprema: MIV otvoren, pomoćni sustavi (hlađenje, podmazivanje) u radu.\n\n" +
            "Koraci:\n1. Pokreni trace prije naredbe start.\n2. Pokreni start sekvencu.\n3. Snimaj dok brzina ne bude stabilna na nazivnoj vrijednosti.\n\n" +
            "Što očitati: vrijeme zaleta, prebačaj brzine, otvor ventila tijekom zaleta i stabilnost u praznom hodu.",
            "{valves}", "MEAS#178", "MEAS#171");
        Add("BEARING_RUN", "Bearing run test",
            "Cilj: prije sinkronizacije provjeriti stabilizaciju temperatura ležajeva.\n\n" +
            "Koraci:\n1. Agregat radi u praznom hodu na nazivnoj brzini.\n2. Snimaj dugotrajno (long-term trace, sporiji takt npr. 1–10 s).\n3. Test traje dok se temperature ne stabiliziraju (npr. promjena manja od 1 K/h).\n\n" +
            "Što očitati: temperature ležajeva i ulja, vibracije, krajnje stabilne vrijednosti u odnosu na granice alarma.\n\n" +
            "Zadana mjerenja: turbinski ventili, brzina generatora i temperature ležajeva turbine (MEAS 12, 23) i generatora (MEAS 30–35, 45). Ležajeve kojih nema ukloni u Uredi mjerenja.",
            "{valves}", "MEAS#178", "MEAS#12", "MEAS#23", "MEAS#30", "MEAS#31", "MEAS#32", "MEAS#33", "MEAS#34", "MEAS#35", "MEAS#45");
        Add("FIRST_EXCITATION", "Prva uzbuda",
            "Cilj: snimiti prvo uzbuđivanje generatora u praznom hodu.\n\n" +
            "Koraci:\n1. Agregat na nazivnoj brzini.\n2. Pokreni trace i uključi uzbudu.\n3. Napon podiži postupno (npr. 30 / 60 / 100 %) prema programu ispitivanja.\n\n" +
            "Što očitati: napon generatora, struju i napon uzbude, frekvenciju te prebačaj napona.");
        foreach (var (key, name, where) in PlantPlanner.Controllers)
        {
            string regulator = key switch { "SPEED" => "SPEED", "VOLTAGE" => "EXC_VOLTAGE", "POWER" => "ACTIVE_POWER", "LEVEL" => "WATER_LEVEL", "OPENING" => "OPENNESS", _ => "EXC_REACTIVE_POWER" };
            string[] controllerTags = [$"REG_INTF.{regulator}.VALUES.SCALED_REFERENCE", $"REG_INTF.{regulator}.VALUES.OUT", $"REG_INTF.{regulator}.VALUES.SCALED_MEAS"];
            Add($"CTRL_{key}_STEP", $"Regulacija – {name.ToLowerInvariant()} – step",
                $"Cilj: provjeriti odziv regulacije ({name.ToLowerInvariant()}) na skok reference.\n\n" +
                $"Uvjeti: {where}.\n\n" +
                "Koraci:\n1. Agregat stabilan u radnoj točki.\n2. Pokreni trace i zadaj mali step reference (npr. ±2 %), pričekaj smirenje.\n3. Ponovi s većim stepom prema programu ispitivanja.\n\n" +
                "Što očitati: vrijeme porasta, prebačaj, vrijeme smirenja i stabilnost. Snimi referencu, stvarnu vrijednost i izlaz regulatora.", controllerTags);
            Add($"CTRL_{key}_RAMP", $"Regulacija – {name.ToLowerInvariant()} – rampa",
                $"Cilj: provjeriti praćenje rampe reference (regulacija: {name.ToLowerInvariant()}).\n\n" +
                $"Uvjeti: {where}.\n\n" +
                "Koraci:\n1. Pokreni trace.\n2. Zadaj rampu reference gore pa dolje zadanom brzinom.\n\n" +
                "Što očitati: grešku praćenja (Izvedeni signali → A − B), kašnjenje i eventualno titranje.", controllerTags);
        }
        Add("SYNC", "Sinkronizacija",
            "Cilj: snimiti sinkronizaciju i uklop generatorskog prekidača.\n\n" +
            "Koraci:\n1. Agregat u praznom hodu s uzbudom.\n2. Pokreni trace i zadaj sinkronizaciju.\n3. Snimaj do stabilnog rada na mreži.\n\n" +
            "Što očitati: razliku frekvencije, napona i faze u trenutku uklopa, naredbu i status prekidača te udar snage nakon uklopa.");
        Add("STOP_NSD", "Zaustavljanje NSD",
            "Normalno zaustavljanje (NSD).\n\nKoraci:\n1. Pokreni trace.\n2. Zadaj normalno zaustavljanje: rasterećenje, isklop prekidača, zatvaranje ventila, kočenje.\n3. Snimaj do mirovanja.\n\n" +
            "Što očitati: redoslijed i vremena koraka sekvence, brzinu, otvor ventila, snagu i vrijeme do mirovanja.", "{valves}", "MEAS#168", "MEAS#178", "MEAS#171", "MEAS#17", "MEAS#18");
        Add("STOP_QSD", "Zaustavljanje QSD",
            "Brzo zaustavljanje (QSD).\n\nKoraci:\n1. Pokreni trace.\n2. Izazovi QSD (simulacijom kvara ili naredbom).\n3. Snimaj do mirovanja.\n\n" +
            "Što očitati: vrijeme zatvaranja ventila, porast brzine, tlak u cjevovodu i vrijeme do mirovanja.", "{valves}", "MEAS#168", "MEAS#178", "MEAS#171", "MEAS#17", "MEAS#18");
        Add("STOP_ESD", "Zaustavljanje ESD",
            "Zaustavljanje u nuždi (ESD).\n\nKoraci:\n1. Pokreni trace.\n2. Aktiviraj ESD (tipkalo u nuždi ili zaštita).\n3. Snimaj do mirovanja.\n\n" +
            "Što očitati: trenutak isklopa prekidača, brzo zatvaranje ventila / deflektora i MIV-a, najveći porast brzine i tlaka te vrijeme do mirovanja.", "{valves}", "MEAS#168", "MEAS#178", "MEAS#171", "MEAS#17", "MEAS#18");
        Add("LOAD_REJECTION", "Odbacivanje tereta",
            "Cilj: odbacivanje tereta pri {n} % snage.\n\n" +
            "Priprema: agregat stabilno radi na {n} % snage. Kreni od manjih snaga prema većima i nakon svakog testa provjeri rezultate.\n\n" +
            "Koraci:\n1. Pokreni snimanje (takt 100 ms).\n2. Isključi generatorski prekidač.\n3. Snimaj do stabilizacije u praznom hodu ili do zaustavljanja.\n\n" +
            "Što očitati: najveći porast brzine, najveći tlak u cjevovodu, napon generatora i vremena zatvaranja ventila. Usporedi s dopuštenim vrijednostima.\n\n" +
            "Stanje generatorskog prekidača (DI 23 uklopljen, DI 24 isklopljen) snima se uvijek, kao referenca trenutka odbacivanja. Vibracije se snimaju ako ih agregat ima.",
            "{valves}", "MEAS#168", "MEAS#171", "MEAS#178", "MEAS#17", "MEAS#18", "{vibrations}", "DI#23", "DI#24");
        Add("COMMON_LOAD_REJECTION", "Odbacivanje tereta – svi agregati",
            "Cilj: istovremeno odbacivanje tereta svih agregata pri {n} % snage.\n\n" +
            "Priprema: svi agregati stabilno rade na {n} % snage. Provjeri da su dovršena pojedinačna odbacivanja tereta na toj razini.\n\n" +
            "Koraci:\n1. Pokreni snimanje sa svih PLC-ova (Snimaj s PLC-a snima sve agregate istodobno, s jednom vremenskom osi).\n2. Isključi generatorske prekidače (ili odvod) prema programu ispitivanja.\n3. Snimaj do stabilizacije u praznom hodu ili do zaustavljanja.\n\n" +
            "Što očitati: najveći porast brzine svakog agregata, najveći tlak u zajedničkom cjevovodu, međusobni utjecaj agregata i vremena zatvaranja.\n\n" +
            "Mjerenja: zadano se za svaki agregat uzima popis iz pojedinačnog odbacivanja tereta (uključujući stanje generatorskog prekidača DI 23 / DI 24); uredi ga po potrebi.");
        Add("HEAT_RUN", "Heat run test",
            "Cilj: na mreži, pri nazivnoj snazi, snimiti stabilizaciju svih temperatura.\n\n" +
            "Koraci:\n1. Agregat radi na nazivnoj snazi.\n2. Snimaj dugotrajno sporim taktom (npr. 10 s).\n3. Test traje dok se temperature ne stabiliziraju (npr. promjena manja od 1 K/h), obično nekoliko sati.\n\n" +
            "Što očitati: temperature namota statora, ležajeva, ulja i rashladne vode te snagu i jalovu snagu tijekom testa.");
        return catalog;
    }
}

public static class PlantPlanner
{
    /// <summary>Position measurements of the unit's turbine valves (Plant Configurator MEAS IDs).</summary>
    public static IEnumerable<string> ValveTags(UnitDefinition unit) => unit.Turbine switch
    {
        Turbines.Pelton => Enumerable.Range(1, Math.Clamp(unit.Nozzles, 0, 6)).Select(i => $"MEAS#{3 + i}").Concat(unit.Deflectors > 0 ? ["MEAS#3"] : []),
        Turbines.Francis => ["MEAS#0"],
        Turbines.Kaplan => ["MEAS#1", "MEAS#2"],
        _ => unit.GuideVaneSections > 1 ? ["MEAS#10", "MEAS#11"] : ["MEAS#10"]
    };
    public static readonly string[] VibrationTags = ["MEAS#40", "MEAS#41", "MEAS#42", "MEAS#43"];

    public const string UserKind = "USER";
    public static TestSlot NewUserSlot(string title, string plcId) => new() { Id = "USER/" + Guid.NewGuid().ToString("N")[..8], Unit = 0, Phase = TestPhases.User, Title = title.Trim(), Kind = UserKind, PlcId = plcId, Channels = [] };

    /// <summary>
    /// Gives every unit a PLC and links PLCs to Plant Configurator databases: "Unit n" databases by unit number,
    /// other databases (Common, Intake …) by name, adding a PLC entry for each database not used yet.
    /// </summary>
    public static void LinkPlcs(PlantDefinition plant, PlantConfigLink? link)
    {
        foreach (var unit in plant.Units.Where(u => plant.PlcForUnit(u.Number) == null))
            plant.Plcs.Add(new PlcConnection { Name = $"PLC agregata {unit.Number}", Units = unit.Number.ToString() });
        if (link == null) return;
        foreach (var plc in plant.Plcs.Where(p => link.Get(p.CatalogId) == null))
        {
            var units = plc.UnitNumbers();
            var match = link.Plcs.FirstOrDefault(c => string.Equals(c.Name, plc.Name, StringComparison.OrdinalIgnoreCase))
                ?? (units.Length > 0 ? link.Plcs.FirstOrDefault(c => PlantConfigLink.UnitNumber(c.Name) == units[0]) : null)
                ?? (link.Plcs.Count == 1 && units.Length > 0 ? link.Plcs[0] : null);
            if (match != null) plc.CatalogId = match.Id;
        }
        foreach (var catalog in link.Plcs.Where(c => plant.Plcs.All(p => p.CatalogId != c.Id) && PlantConfigLink.UnitNumber(c.Name) == null))
            plant.Plcs.Add(new PlcConnection { Name = catalog.Name, CatalogId = catalog.Id });
    }
    public sealed record ValveType(string Key, string Generic);
    public static readonly ValveType[] ValveTypes =
    [
        new("NOZZLE", "Mlaznica"), new("DEFLECTOR", "Deflektor"), new("WICKET_GATE", "Privodni aparat"), new("KAPLAN_GATE", "Privodni aparat (Kaplan)"),
        new("RUNNER", "Lopatice rotora"), new("CF_SECTION", "Sekcija privodnog aparata")
    ];
    public static readonly (string Key, string Name, string Where)[] Controllers =
    [
        ("SPEED", "Brzina", "prazni hod, nazivna brzina"), ("VOLTAGE", "Napon", "prazni hod s uzbudom"),
        ("POWER", "Snaga", "agregat na mreži"), ("LEVEL", "Razina", "agregat na mreži, regulacija razine aktivna"),
        ("OPENING", "Otvor", "agregat na mreži"), ("REACTIVE", "Jalova snaga", "agregat na mreži")
    ];

    static IEnumerable<(string Key, int Index, string Name)> Valves(UnitDefinition unit)
    {
        switch (unit.Turbine)
        {
            case Turbines.Pelton:
                for (int i = 1; i <= unit.Nozzles; i++) yield return ("NOZZLE", i, $"Mlaznica {i}");
                for (int i = 1; i <= unit.Deflectors; i++) yield return ("DEFLECTOR", i, $"Deflektor {i}");
                break;
            case Turbines.Francis: yield return ("WICKET_GATE", 1, "Privodni aparat"); break;
            case Turbines.Kaplan: yield return ("KAPLAN_GATE", 1, "Privodni aparat"); yield return ("RUNNER", 1, "Lopatice rotora"); break;
            case Turbines.CrossFlow:
                if (unit.GuideVaneSections == 1) yield return ("CF_SECTION", 1, "Privodni aparat");
                else for (int i = 1; i <= unit.GuideVaneSections; i++) yield return ("CF_SECTION", i, $"Privodni aparat – sekcija {i}");
                break;
        }
    }

    public static List<TestSlot> Generate(PlantDefinition plant)
    {
        var slots = new List<TestSlot>();
        foreach (var unit in plant.Units)
        {
            int u = unit.Number;
            void Add(string phase, string group, string title, string kind, int index = 0, bool manual = false) =>
                slots.Add(new() { Id = $"U{u}/{kind}/{index}", Unit = u, Phase = phase, Group = group, Title = title, Kind = kind, Index = index, Manual = manual });
            bool Has(string key) => key switch
            {
                "SPEED" => unit.SpeedControl, "VOLTAGE" => unit.VoltageControl && unit.HasExcitation, "POWER" => unit.PowerControl,
                "LEVEL" => unit.LevelControl, "OPENING" => unit.OpeningControl, "REACTIVE" => unit.ReactiveControl && unit.HasExcitation, _ => false
            };
            void AddControllers(string phase, params string[] keys)
            {
                foreach (var c in PlantPlanner.Controllers.Where(c => keys.Contains(c.Key) && Has(c.Key)))
                {
                    Add(phase, "Regulacije", $"{c.Name} – step", $"CTRL_{c.Key}_STEP");
                    Add(phase, "Regulacije", $"{c.Name} – rampa", $"CTRL_{c.Key}_RAMP");
                }
            }

            foreach (var valve in Valves(unit))
            {
                string group = "Turbinski ventili/" + valve.Name;
                Add(TestPhases.Dry, group, "Brzina otvaranja / zatvaranja", valve.Key + "_OPEN_CLOSE", valve.Index);
                Add(TestPhases.Dry, group, "Odziv na step reference", valve.Key + "_STEP", valve.Index);
                Add(TestPhases.Dry, group, "Odziv na rampu reference", valve.Key + "_RAMP", valve.Index);
            }
            Add(TestPhases.Dry, "", "HPU – pad tlaka (svi ventili otvoreni, ESD tlak)", "HPU_PRESSURE_DROP");
            if (unit.HasMiv) Add(TestPhases.Dry, "Glavni ventili", "MIV – vrijeme otvaranja i zatvaranja", "MIV_TIMES", manual: true);
            if (unit.HasBypass) Add(TestPhases.Dry, "Glavni ventili", "Bypass – vrijeme otvaranja i zatvaranja", "BYPASS_TIMES", manual: true);

            Add(TestPhases.Wet, "", "Zalet", "STARTUP");
            Add(TestPhases.Wet, "", "Bearing run test", "BEARING_RUN");
            if (unit.HasExcitation) Add(TestPhases.Wet, "", "Prva uzbuda", "FIRST_EXCITATION");
            AddControllers(TestPhases.Wet, "SPEED", "VOLTAGE");
            Add(TestPhases.Wet, "", "Sinkronizacija", "SYNC");
            Add(TestPhases.Wet, "Zaustavljanja", "NSD – normalno zaustavljanje", "STOP_NSD");
            Add(TestPhases.Wet, "Zaustavljanja", "QSD – brzo zaustavljanje", "STOP_QSD");
            Add(TestPhases.Wet, "Zaustavljanja", "ESD – zaustavljanje u nuždi", "STOP_ESD");

            AddControllers(TestPhases.Grid, "POWER", "LEVEL", "OPENING", "REACTIVE");
            foreach (int level in unit.LoadLevels()) Add(TestPhases.Grid, "Odbacivanje tereta", $"Odbacivanje tereta {level} %", "LOAD_REJECTION", level);
            Add(TestPhases.Grid, "", "Heat run test", "HEAT_RUN");
        }
        if (plant.Units.Count > 1)
            foreach (int level in UnitDefinition.ParseLevels(plant.CommonLoadRejectionLevels ?? "", "Zajednički testovi"))
                slots.Add(new() { Id = $"C/COMMON_LOAD_REJECTION/{level}", Unit = 0, Phase = TestPhases.Common, Group = "Odbacivanje tereta – svi agregati", Title = $"Odbacivanje tereta {level} % – svi agregati", Kind = "COMMON_LOAD_REJECTION", Index = level });
        return slots;
    }

    /// <summary>
    /// Regenerates the tree after the plant definition changed. Entries, notes and statuses of
    /// matching tests are kept; tests that disappeared stay (marked retired) while they hold data.
    /// </summary>
    public static List<TestSlot> Merge(List<TestSlot> existing, List<TestSlot> generated, Func<string, bool> hasRecordings)
    {
        var old = existing.Where(s => !s.IsUser).ToDictionary(s => s.Id);
        foreach (var slot in generated)
        {
            if (!old.Remove(slot.Id, out var previous)) continue;
            slot.Reviewed = previous.Reviewed; slot.NotApplicable = previous.NotApplicable; slot.Note = previous.Note; slot.Deleted = previous.Deleted;
            slot.OpeningTime = previous.OpeningTime; slot.ClosingTime = previous.ClosingTime;
            slot.Channels = previous.Channels;
            if (!string.IsNullOrWhiteSpace(previous.CustomTitle)) { slot.CustomTitle = previous.CustomTitle; slot.Title = previous.CustomTitle; }
        }
        foreach (var slot in old.Values.Where(s => hasRecordings(s.Id) || s.HasManualValues || !string.IsNullOrWhiteSpace(s.Note)))
        { slot.Retired = true; generated.Add(slot); }
        generated.AddRange(existing.Where(s => s.IsUser));
        return generated;
    }
}

public static class TagMatcher
{
    static readonly Regex Address = new(@"\s*\[%[^\]]*\]\s*$", RegexOptions.Compiled);
    /// <summary>Strips quotes and the trailing absolute address TIA adds to trace column headers.</summary>
    public static string Normalize(string tag) => Address.Replace(tag.Trim().Trim('"'), "").Trim().Trim('"');
    public static bool Matches(string expected, string source) => string.Equals(Normalize(expected), Normalize(source), StringComparison.OrdinalIgnoreCase);
    public static List<(string Tag, bool Found)> Check(IEnumerable<string> expected, IEnumerable<string> sources)
    {
        var present = sources.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Select(tag => (tag, present.Contains(Normalize(tag)))).ToList();
    }
}
