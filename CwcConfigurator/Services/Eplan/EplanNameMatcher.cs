using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Eplan;

/// <summary>
/// Compares a signal name from the schematic with a signal name of the PLC program. The designer and the PLC
/// programmer rarely write the same words, so names are reduced to canonical words first:
/// device tags in brackets, KKS codes ("0AKA01 -") and locations ("+1MKA10") are dropped, "D.E." becomes "DE",
/// abbreviations and synonyms of the project dictionary are applied ("CB" → "CIRCUIT BREAKER", "FASTER" → "UP"),
/// plurals are cut and small typos ("ATUOMATIC", "PTOTECTION") are tolerated. Pairs the user linked by hand
/// (learned names) are always exact.
/// </summary>
public sealed class EplanNameMatcher
{
    public const double Exact = 1.0;

    private static readonly Regex Bracketed = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Kks = new(@"(?<![A-Z0-9])\d[A-Z]{3}\d{2}(?![A-Z0-9])", RegexOptions.Compiled);
    private static readonly Regex Location = new(@"\+\S+", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex SingleLetters = new(@"\b(?:[A-Z] ){1,3}[A-Z]\b", RegexOptions.Compiled);
    private static readonly Regex BracketTags = new(@"\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>A word in at least this share of the DB names is generic (CLOSED, ON, ALARM, CB …).</summary>
    private const double GenericShare = 0.03;
    /// <summary>A word in at most this share of the DB names names a system or device (LOU, HPU, BYPASS, DEFLECTOR …).</summary>
    private const double DistinctiveShare = 0.01;

    private readonly List<(string[] From, string[] To)> _rules;
    private readonly HashSet<(string, string)> _learned;
    private readonly Dictionary<string, int>? _documentFrequency;
    private readonly int _corpusSize;

    public EplanNameMatcher(IEnumerable<EplanSynonym>? synonyms = null, IEnumerable<EplanLearnedName>? learned = null)
    {
        _rules = (synonyms ?? EplanSynonym.Defaults())
            .Select(s => (From: Plain(s.Words).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray(),
                          To: Plain(s.Means).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray()))
            .Where(r => r.From.Length > 0)
            .OrderByDescending(r => r.From.Length)      // longest phrase first
            .ToList();
        _learned = (learned ?? []).Select(l => (Key(l.SchematicName), Key(l.DbName))).ToHashSet();
    }

    private EplanNameMatcher(EplanNameMatcher source, IEnumerable<string> corpus)
    {
        _rules = source._rules;
        _learned = source._learned;
        _documentFrequency = new Dictionary<string, int>();
        foreach (var name in corpus)
        {
            _corpusSize++;
            foreach (var word in Canonical(name).Where(w => !IsIdentifier(w)).Distinct())
            {
                _documentFrequency[word] = _documentFrequency.GetValueOrDefault(word) + 1;
            }
        }
    }

    /// <summary>
    /// Same matcher that knows the DB names of one table: words frequent there (CLOSED, ON, CB) weigh little,
    /// rare words that name a system or device (LOU, HPU, BYPASS) weigh a lot and must not be missing.
    /// </summary>
    public EplanNameMatcher WithCorpus(IEnumerable<string> dbNames) => new(this, dbNames);

    /// <summary>Key used to remember a hand-made link: the plain upper-case text without punctuation.</summary>
    public static string Key(string name) => Plain(name);

    /// <summary>0 … 1; 1 = exact (same canonical words, or a learned pair). Different numbers always give 0.</summary>
    public double Similarity(string schematicName, string schematicConnectedTo, string dbName)
    {
        if (_learned.Contains((Key(schematicName), Key(dbName))))
        {
            return Exact;
        }

        var a = Canonical(schematicName);
        var b = Canonical(dbName);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        // numbers and single letters identify one of several equal things: PUMP 1 / PUMP 2, VALVE A / VALVE B, WINDING U / V
        var numbersA = a.Where(IsIdentifier).ToHashSet();
        var numbersB = b.Where(IsIdentifier).ToHashSet();
        if (numbersA.Count > 0 && numbersB.Count > 0 && !numbersA.SetEquals(numbersB))
        {
            return 0;       // HPU PUMP 1 is never HPU PUMP 2
        }

        // different KKS systems are different equipment (=0AKA01 medium voltage is never =1MKV10 lubrication)
        var kksA = KksCodes(schematicName);
        var kksB = KksCodes(dbName);
        if (kksA.Count > 0 && kksB.Count > 0 && !kksA.Overlaps(kksB))
        {
            return 0;
        }

        var wordsA = a.Where(w => !IsIdentifier(w)).Distinct().ToList();
        var wordsB = b.Where(w => !IsIdentifier(w)).Distinct().ToList();
        var (pairs, restA, restB, typos) = Pair(wordsA, wordsB);
        if (restA.Count == 0 && restB.Count == 0 && numbersA.SetEquals(numbersB) && pairs.Count > 0)
        {
            return typos == 0 ? Exact : 0.95;   // same words; a typo still needs a look
        }

        // a system/device word on both sides that does not match: LOU UNIT CB CLOSED is not DEFLECTOR CB CLOSED
        if (restA.Any(IsDistinctive) && restB.Any(IsDistinctive))
        {
            return Math.Min(0.5, WeightedDice(pairs, restA, restB));
        }

        var score = WeightedDice(pairs, restA, restB);
        var shorter = Math.Min(wordsA.Count, wordsB.Count);
        var extra = wordsA.Count >= wordsB.Count ? restA : restB;
        if (shorter >= 2 && pairs.Count == shorter && extra.All(IsGeneric))
        {
            score = Math.Max(score, 0.75);      // the longer name only adds generic words
        }

        if (_documentFrequency is not null)
        {
            // every unmatched word that is not generic (HEATER, BYPASS…) makes the link less sure
            score -= 0.05 * restA.Concat(restB).Count(w => !IsGeneric(w));
        }

        if (numbersA.Count != numbersB.Count)
        {
            score -= 0.15;                       // a number on one side only
        }

        var device = Plain(schematicConnectedTo.Split(':')[0]);
        var schematicPlain = Plain(schematicName);
        var tagHit = BracketTags.Matches(dbName).SelectMany(m => m.Groups[1].Value.Split(',')).Select(Plain)
            .Where(t => t.Length > 0).Any(t => t == device || schematicPlain.Contains(t, StringComparison.Ordinal));
        return Math.Clamp(score + (tagHit ? 0.25 : 0), 0, 0.99);
    }

    /// <summary>Weighted Dice: a pair counts with the smaller weight of its two spellings on both sides.</summary>
    private double WeightedDice(List<(string A, string B)> pairs, List<string> restA, List<string> restB)
    {
        var matched = pairs.Sum(p => 2 * Math.Min(Weight(p.A), Weight(p.B)));
        var total = matched + restA.Sum(Weight) + restB.Sum(Weight);
        return total <= 0 ? 0 : matched / total;
    }

    /// <summary>Rare words weigh more (inverse document frequency); without a corpus every word weighs 1.</summary>
    private double Weight(string word)
    {
        if (_documentFrequency is null || _corpusSize == 0)
        {
            return 1;
        }

        var df = _documentFrequency.GetValueOrDefault(word);
        return 0.2 + Math.Log((_corpusSize + 1.0) / (df + 1.0)) / Math.Log(_corpusSize + 1.0);
    }

    private double Share(string word) =>
        _documentFrequency is null || _corpusSize == 0 ? double.NaN : (double)_documentFrequency.GetValueOrDefault(word) / _corpusSize;

    /// <summary>Without a corpus nothing is known, so every word may be an add-on (old behaviour).</summary>
    private bool IsGeneric(string word) => double.IsNaN(Share(word)) || Share(word) >= GenericShare;

    private bool IsDistinctive(string word) => !double.IsNaN(Share(word)) && Share(word) <= DistinctiveShare;

    private static HashSet<string> KksCodes(string name) =>
        Kks.Matches(StripDiacritics(name).ToUpperInvariant()).Select(m => m.Value).ToHashSet();

    /// <summary>Canonical words of a name (see class remarks).</summary>
    public List<string> Canonical(string name)
    {
        var text = Bracketed.Replace(StripDiacritics(name).ToUpperInvariant(), " ");
        text = Location.Replace(text, " ");
        text = Kks.Replace(text, " ");
        text = NonWord.Replace(text, " ").Trim();
        text = SingleLetters.Replace(text, m => m.Value.Replace(" ", ""));      // D E -> DE, N D E -> NDE
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToList();     // "MCBs" -> "MCB" before the dictionary

        var result = new List<string>();
        for (var i = 0; i < words.Count;)
        {
            var rule = _rules.FirstOrDefault(r => i + r.From.Length <= words.Count && r.From.SequenceEqual(words.Skip(i).Take(r.From.Length)));
            if (rule.From is not null)
            {
                result.AddRange(rule.To);
                i += rule.From.Length;
            }
            else
            {
                result.Add(words[i]);
                i++;
            }
        }

        return result;
    }

    /// <summary>Greedy word pairing; equal words first, then words within a small edit distance (typos).</summary>
    private static (List<(string A, string B)> Pairs, List<string> RestA, List<string> RestB, int Typos) Pair(List<string> a, List<string> b)
    {
        var free = b.ToList();
        var matched = new List<(string A, string B)>();
        var rest = new List<string>();
        foreach (var word in a)
        {
            if (free.Remove(word))
            {
                matched.Add((word, word));
            }
            else
            {
                rest.Add(word);
            }
        }

        var typos = 0;
        var restA = new List<string>();
        foreach (var word in rest)
        {
            var near = free.FirstOrDefault(other => IsTypo(word, other));
            if (near is not null)
            {
                free.Remove(near);
                matched.Add((word, near));
                typos++;
            }
            else
            {
                restA.Add(word);
            }
        }

        return (matched, restA, free, typos);
    }

    private static bool IsTypo(string a, string b)
    {
        var shorter = Math.Min(a.Length, b.Length);
        if (shorter < 5 || a[0] != b[0])
        {
            return false;
        }

        var allowed = shorter >= 8 ? 2 : 1;
        return Math.Abs(a.Length - b.Length) <= allowed && Distance(a, b) <= allowed;
    }

    /// <summary>Optimal string alignment distance (Levenshtein with transpositions).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private static string Stem(string word) =>
        word.Length > 3 && word.EndsWith('S') && !word.EndsWith("SS", StringComparison.Ordinal) ? word[..^1] : word;

    private static bool IsIdentifier(string word) => word.All(char.IsDigit) || word.Length == 1;

    private static string Plain(string text) => NonWord.Replace(StripDiacritics(text ?? "").ToUpperInvariant(), " ").Trim();

    private static string StripDiacritics(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(ch);
            }
        }

        return plain.ToString().Normalize(NormalizationForm.FormC);
    }
}
