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

    private readonly List<(string[] From, string[] To)> _rules;
    private readonly HashSet<(string, string)> _learned;

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

        var wordsA = a.Where(w => !IsIdentifier(w)).Distinct().ToList();
        var wordsB = b.Where(w => !IsIdentifier(w)).Distinct().ToList();
        var (matched, typos) = Pair(wordsA, wordsB);
        if (matched == wordsA.Count && matched == wordsB.Count && numbersA.SetEquals(numbersB) && matched > 0)
        {
            return typos == 0 ? Exact : 0.95;   // same words; a typo still needs a look
        }

        var total = wordsA.Count + wordsB.Count;
        var score = total == 0 ? 0 : 2.0 * matched / total;
        var shorter = Math.Min(wordsA.Count, wordsB.Count);
        if (shorter >= 2 && matched == shorter)
        {
            score = Math.Max(score, 0.75);      // every word of the shorter name is in the longer one
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
    private static (int Matched, int Typos) Pair(List<string> a, List<string> b)
    {
        var free = b.ToList();
        var matched = 0;
        var typos = 0;
        var rest = new List<string>();
        foreach (var word in a)
        {
            if (free.Remove(word))
            {
                matched++;
            }
            else
            {
                rest.Add(word);
            }
        }

        foreach (var word in rest)
        {
            var near = free.FirstOrDefault(other => IsTypo(word, other));
            if (near is not null)
            {
                free.Remove(near);
                matched++;
                typos++;
            }
        }

        return (matched, typos);
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
