using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Eplan;

public enum AutomapMatch
{
    None,
    Likely,
    Exact
}

/// <summary>A signal of a DB table that a hardware row can point to with its ID.</summary>
public sealed record AutomapCandidate(int Id, string Name);

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

    private static readonly Regex NonWord = new(@"[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex Bracketed = new(@"\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly HashSet<string> Spare = ["SPARE", "RESERVE", "REZERVA"];

    public static IReadOnlyList<AutomapCandidate> Candidates(PlcDatabase database, EplanModuleKind kind)
    {
        var catalog = database.DbCatalog;
        IEnumerable<AutomapCandidate> list = kind switch
        {
            EplanModuleKind.DigitalInput => catalog.DiRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish)),
            EplanModuleKind.DigitalOutput => catalog.DoRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish)),
            EplanModuleKind.AnalogOutput => catalog.AoRows.Select(r => new AutomapCandidate(r.AoNumber, r.NameEnglish)),
            _ => catalog.MeasRows.Select(r => new AutomapCandidate(r.Id, r.NameEnglish))
        };
        return list.Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name)).ToList();
    }

    public static List<AutomapProposal> Propose(PlcDatabase database)
    {
        var proposals = new List<AutomapProposal>();
        foreach (var kind in Enum.GetValues<EplanModuleKind>())
        {
            var candidates = Candidates(database, kind);
            var rows = Rows(database, kind);
            var group = rows.Select(r => Proposal(kind, r)).Where(p => p.EplanSignal.Length > 0).ToList();
            foreach (var p in group)
            {
                if (IsSpare(p.EplanSignal))
                {
                    p.Note = "spare in the schematic";
                    continue;
                }

                var best = candidates
                    .Select(c => (Candidate: c, Score: Similarity(p.EplanSignal, p.EplanConnectedTo, c.Name)))
                    .OrderByDescending(x => x.Score)
                    .Take(2)
                    .ToList();
                if (best.Count == 0 || best[0].Score < LikelyThreshold)
                {
                    continue;
                }

                p.Proposed = best[0].Candidate;
                p.Score = best[0].Score;
                p.Match = best[0].Score >= 1.0 ? AutomapMatch.Exact : AutomapMatch.Likely;
                if (best.Count > 1 && best[1].Score >= best[0].Score - 1e-9 && p.Match == AutomapMatch.Exact)
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

    /// <summary>
    /// 1.0 when the names are equal ignoring case, punctuation and a device tag in brackets ("ESD RESET" =
    /// "ESD reset (-2K02)"); otherwise the word overlap (Dice) of the names without brackets, raised when a bracketed
    /// tag of the DB name is the EPLAN connected device or appears in the EPLAN text. Different numbers
    /// ("PUMP 1" / "PUMP 2") rule a candidate out.
    /// </summary>
    public static double Similarity(string eplanSignal, string eplanConnectedTo, string dbName)
    {
        var a = Normalize(Bracketed.Replace(eplanSignal, " "));
        var b = Normalize(Bracketed.Replace(dbName, " "));
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b || Normalize(eplanSignal) == Normalize(dbName))
        {
            return 1.0;
        }

        var wordsA = Words(a);
        var wordsB = Words(b);
        var numbersA = wordsA.Where(IsNumber).ToHashSet();
        var numbersB = wordsB.Where(IsNumber).ToHashSet();
        if (numbersA.Count > 0 && numbersB.Count > 0 && !numbersA.SetEquals(numbersB))
        {
            return 0;       // HPU PUMP 1 is never HPU PUMP 2
        }

        var common = wordsA.Intersect(wordsB).Count();
        var dice = 2.0 * common / (wordsA.Count + wordsB.Count);
        if (numbersA.Count != numbersB.Count)
        {
            dice -= 0.15;   // a number on one side only
        }

        var device = Normalize(eplanConnectedTo.Split(':')[0]);
        var fullA = Normalize(eplanSignal);
        var tagHit = Bracketed.Matches(dbName).Select(m => Normalize(m.Groups[1].Value)).Where(t => t.Length > 0)
            .Any(t => t == device || fullA.Contains(t, StringComparison.Ordinal));
        return Math.Clamp(dice + (tagHit ? 0.25 : 0), 0, 0.99);
    }

    private static bool IsNumber(string word) => word.All(char.IsDigit);

    private static string Normalize(string text)
    {
        var plain = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(ch);
            }
        }

        return NonWord.Replace(plain.ToString().ToUpperInvariant(), " ").Trim();
    }

    private static HashSet<string> Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static bool IsSpare(string signal) => Spare.Contains(Normalize(signal));

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
