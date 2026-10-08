using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Eplan;

public enum AutomapMatch
{
    None,
    Likely,
    Exact
}

/// <summary>A signal of a DB table that a hardware row can point to with its ID.</summary>
public sealed record AutomapCandidate(int Id, string Name, string PlcStructure = "", string PlcName = "");

/// <summary>One hardware row with EPLAN data and the DB signal proposed for it.</summary>
public sealed class AutomapProposal
{
    public required EplanModuleKind Kind { get; init; }
    public required object Row { get; init; }
    public int ModuleNumber { get; init; }
    public string ModuleName { get; init; } = "";
    public int? Port { get; init; }
    public string EplanSignal { get; init; } = "";
    public string EplanConnectedTo { get; init; } = "";
    public int CurrentId { get; init; }
    public AutomapCandidate? Proposed { get; set; }
    public AutomapMatch Match { get; set; }
    public double Score { get; set; }
    /// <summary>Why a proposal was weakened or dropped (duplicate, already used...).</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// Automap: gives hardware rows the ID of the DB signal whose English name matches the signal name in the
/// EPLAN schematic. Only hardware IDs are proposed; DB tables are never changed and nothing is created.
/// DI → DB DI, DO → DB DO, AI/RTD/COUNTER → DB MEAS, AO → DB AO.
/// </summary>
public static class EplanIdAutomap
{
    public const double LikelyThreshold = 0.6;

    private static readonly HashSet<string> Spare = ["SPARE", "RESERVE", "REZERVA"];

    public static IReadOnlyList<AutomapCandidate> Candidates(PlcDatabase database, EplanModuleKind kind)
    {
        var catalog = database.DbCatalog;
        IEnumerable<AutomapCandidate> list = kind switch
        {
            EplanModuleKind.DigitalInput => catalog.DiRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish, r.PlcStructure, r.PlcName)),
            EplanModuleKind.DigitalOutput => catalog.DoRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish, r.PlcStructure, r.PlcName)),
            EplanModuleKind.AnalogOutput => catalog.AoRows.Select(r => new AutomapCandidate(r.AoNumber, r.NameEnglish, r.AoSystemName, r.AoPlcName)),
            _ => catalog.MeasRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish, r.PlcStructure, r.PlcName))
        };
        return list.Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name)).ToList();
    }

    public static List<AutomapProposal> Propose(PlcDatabase database, EplanNameMatcher? matcher = null)
    {
        matcher ??= new EplanNameMatcher();
        var proposals = new List<AutomapProposal>();
        foreach (var kind in Enum.GetValues<EplanModuleKind>())
        {
            var candidates = Candidates(database, kind);
            var tableMatcher = matcher.WithCorpus(candidates.Select(c => c.Name));
            var rows = Rows(database, kind);
            var group = rows.Select(r => Proposal(kind, r)).Where(p => p.EplanSignal.Length > 0).ToList();
            foreach (var p in group)
            {
                if (IsSpare(p.EplanSignal))
                {
                    p.Note = "spare in the schematic";
                    continue;
                }

                // the device the terminal is wired to (-2K26, -H101) outranks the wording: a DB signal that names
                // the same device wins over one with a nicer name but no device
                var best = candidates
                    .Select(c =>
                    {
                        var score = tableMatcher.Similarity(p.EplanSignal, p.EplanConnectedTo, c.Name);
                        // same relay/lamp even when the PLC name is a leftover of a template ("Nozzle 4 close (-2K42)")
                        var wired = EplanNameMatcher.DeviceTagHit(p.EplanConnectedTo, c.Name) && !tableMatcher.Contradicts(p.EplanSignal, c.Name);
                        return (Candidate: c, Score: wired ? Math.Max(score, LikelyThreshold) : score, Wired: wired, Raw: score);
                    })
                    .OrderByDescending(x => x.Score + (x.Wired ? 0.5 : 0))
                    .ThenByDescending(x => x.Raw)       // two signals on the same device: the closer name first
                    .Take(2)
                    .ToList();
                if (best.Count == 0 || best[0].Score < LikelyThreshold)
                {
                    continue;
                }

                var top = best[0];
                p.Proposed = top.Candidate;
                p.Score = top.Score + (top.Wired ? 0.5 : 0);
                p.Match = top.Score >= 1.0 || (top.Wired && top.Score >= 0.85) ? AutomapMatch.Exact : AutomapMatch.Likely;
                if (top.Wired && top.Score < 0.85)
                {
                    p.Note = $"same device {p.EplanConnectedTo.Split(':')[0]}, names differ";
                }

                if (top.Wired && best.Count > 1 && best[1].Wired)
                {
                    p.Match = AutomapMatch.Likely;      // two PLC signals name the same device: a person decides
                    p.Note = $"device {p.EplanConnectedTo.Split(':')[0]} also on ID {best[1].Candidate.Id} '{best[1].Candidate.Name}'";
                }
                else if (best.Count > 1 && best[1].Score + (best[1].Wired ? 0.5 : 0) >= p.Score - 1e-9 && p.Match == AutomapMatch.Exact)
                {
                    p.Match = AutomapMatch.Likely;
                    p.Note = $"same name also on ID {best[1].Candidate.Id}";
                }
            }

            // one DB signal belongs to one hardware channel: the best match keeps it
            foreach (var clash in group.Where(p => p.Proposed is not null).GroupBy(p => p.Proposed!.Id).Where(g => g.Count() > 1))
            {
                var ordered = clash.OrderByDescending(p => p.Score).ToList();
                foreach (var loser in ordered.Skip(1))
                {
                    loser.Note = $"ID {loser.Proposed!.Id} fits -{ordered[0].ModuleName} port {ordered[0].Port} better";
                    loser.Proposed = null;
                    loser.Match = AutomapMatch.None;
                }

                if (ordered.Count > 1 && Math.Abs(ordered[0].Score - ordered[1].Score) < 1e-9)
                {
                    ordered[0].Match = AutomapMatch.Likely;
                    ordered[0].Note = "same signal name on more than one channel";
                }
            }

            proposals.AddRange(group);
        }

        return proposals;
    }

    /// <summary>Writes the IDs of the chosen proposals into the hardware rows. Returns how many rows changed.</summary>
    public static int Apply(IEnumerable<AutomapProposal> chosen)
    {
        var changed = 0;
        foreach (var p in chosen.Where(p => p.Proposed is not null))
        {
            var id = p.Proposed!.Id;
            switch (p.Row)
            {
                case DigitalSignalConfig d when d.Id != id: d.Id = id; changed++; break;
                case AnalogSignalConfig a when a.Id != id: a.Id = id; changed++; break;
                case CounterConfig k when k.Id != id: k.Id = id; changed++; break;
                case AnalogOutputConfig o when o.Id != id: o.Id = id; changed++; break;
            }
        }

        return changed;
    }

    /// <summary>Similarity with the default dictionary (see <see cref="EplanNameMatcher"/>).</summary>
    public static double Similarity(string eplanSignal, string eplanConnectedTo, string dbName) =>
        new EplanNameMatcher().Similarity(eplanSignal, eplanConnectedTo, dbName);

    /// <summary>
    /// Remembers the links the user confirmed although the names differ (likely or typed by hand), so the same
    /// schematic name is exact next time – the designer's "ABC" is the programmer's "BCA".
    /// </summary>
    public static int Learn(EplanImportConfig config, IEnumerable<AutomapProposal> applied)
    {
        var added = 0;
        foreach (var p in applied.Where(p => p.Proposed is not null && p.Match != AutomapMatch.Exact))
        {
            var schematic = EplanNameMatcher.Key(p.EplanSignal);
            var db = EplanNameMatcher.Key(p.Proposed!.Name);
            if (schematic.Length == 0 || config.LearnedNames.Any(l => EplanNameMatcher.Key(l.SchematicName) == schematic && EplanNameMatcher.Key(l.DbName) == db))
            {
                continue;
            }

            config.LearnedNames.Add(new EplanLearnedName { SchematicName = p.EplanSignal, DbName = p.Proposed.Name });
            added++;
        }

        return added;
    }

    private static bool IsSpare(string signal) => Spare.Contains(EplanNameMatcher.Key(signal));

    private static IEnumerable<object> Rows(PlcDatabase db, EplanModuleKind kind) => kind switch
    {
        EplanModuleKind.DigitalInput => db.DigitalInputs,
        EplanModuleKind.DigitalOutput => db.DigitalOutputs,
        EplanModuleKind.AnalogInput => db.AnalogInputs,
        EplanModuleKind.RtdInput => db.RtdInputs,
        EplanModuleKind.Counter => db.Counters,
        _ => db.AnalogOutputs
    };

    private static AutomapProposal Proposal(EplanModuleKind kind, object row) => row switch
    {
        DigitalSignalConfig d => new AutomapProposal { Kind = kind, Row = row, ModuleNumber = d.ModuleNumber, ModuleName = d.ModuleName, Port = d.Port, EplanSignal = d.EplanSignalName, EplanConnectedTo = d.EplanConnectedTo, CurrentId = d.Id },
        AnalogSignalConfig a => new AutomapProposal { Kind = kind, Row = row, ModuleNumber = a.ModuleNumber, ModuleName = a.ModuleName, Port = a.Port, EplanSignal = a.EplanSignalName, EplanConnectedTo = a.EplanConnectedTo, CurrentId = a.Id },
        CounterConfig k => new AutomapProposal { Kind = kind, Row = row, ModuleNumber = k.ModuleNumber, ModuleName = k.ModuleName, EplanSignal = k.EplanSignalName, EplanConnectedTo = k.EplanConnectedTo, CurrentId = k.Id },
        AnalogOutputConfig o => new AutomapProposal { Kind = kind, Row = row, ModuleNumber = o.ModuleNumber, ModuleName = o.ModuleName, Port = o.Port, EplanSignal = o.EplanSignalName, EplanConnectedTo = o.EplanConnectedTo, CurrentId = o.Id },
        _ => throw new ArgumentException("Unknown hardware row type.", nameof(row))
    };
}
