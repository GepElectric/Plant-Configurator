using System.IO;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class DescriptionTemplateService
{
    public int MergeFromCsv(DescriptionTemplateCatalog catalog, string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        catalog.Templates ??= [];
        var changed = 0;
        foreach (var record in ReadCsv(path).Skip(1))
        {
            if (record.Count < 4)
            {
                continue;
            }

            var key = record[0].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var existing = catalog.Templates.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new DescriptionTemplateEntry { Key = key };
                catalog.Templates.Add(existing);
                changed++;
            }

            changed += SetIfDifferent(existing.Scope, NormalizeScope(record[1]), value => existing.Scope = value);
            changed += SetIfDifferent(existing.Notes, record[2], value => existing.Notes = value);
            changed += SetIfDifferent(existing.TextEnglish, record[3], value => existing.TextEnglish = value);
            if (record.Count > 4)
            {
                changed += SetIfDifferent(existing.TextOther, record[4], value => existing.TextOther = value);
            }
        }

        catalog.Templates = catalog.Templates
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return changed;
    }

    public int ApplyTemplates(CwcProject project)
    {
        var changed = 0;
        foreach (var database in project.PlcDatabases)
        {
            changed += ApplyRows(database.DbCatalog.AlarmRows, project.DescriptionTemplates, "ALM");
            changed += ApplyRows(database.DbCatalog.ParameterRows, project.DescriptionTemplates, "PAR");
        }

        changed += ApplyRows(project.DbCatalog.AlarmRows, project.DescriptionTemplates, "ALM");
        changed += ApplyRows(project.DbCatalog.ParameterRows, project.DescriptionTemplates, "PAR");
        return changed;
    }

    private static int ApplyRows(IEnumerable<DbStringRow> rows, DescriptionTemplateCatalog catalog, string scope)
    {
        var templates = catalog.ForScope(scope).ToList();
        var changed = 0;
        foreach (var row in rows)
        {
            var template = !string.IsNullOrWhiteSpace(row.DescriptionTemplateKey)
                ? FindTemplateByKey(row.DescriptionTemplateKey, templates)
                : FindMatchingTemplate(row, templates);
            if (template is null)
            {
                if (!string.IsNullOrWhiteSpace(row.DescriptionTemplateKey))
                {
                    row.DescriptionTemplateKey = "";
                    changed++;
                }

                continue;
            }

            changed += SetIfDifferent(row.DescriptionTemplateKey, template.Key, value => row.DescriptionTemplateKey = value);
            changed += SetIfDifferent(row.DescriptionEnglish, template.Key, value => row.DescriptionEnglish = value);
            changed += SetIfDifferent(row.DescriptionOther, template.Key, value => row.DescriptionOther = value);
        }

        return changed;
    }

    public string ResolveDescription(DbStringRow row, DescriptionTemplateCatalog catalog, string scope, string languageCode)
    {
        var useEnglish = languageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase);
        var fallback = useEnglish ? row.DescriptionEnglish : row.DescriptionOther;
        return ResolveTemplateText(fallback, row.DescriptionTemplateKey, catalog, scope, languageCode);
    }

    public string ResolveTemplateText(string text, string templateKey, DescriptionTemplateCatalog catalog, string scope, string languageCode)
    {
        var template = FindTemplateByKeyOrText(text, templateKey, catalog.ForScope(scope));
        if (template is null)
        {
            return text ?? "";
        }

        if (!languageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(template.TextOther))
        {
            return template.TextOther;
        }

        return !string.IsNullOrWhiteSpace(template.TextEnglish)
            ? template.TextEnglish
            : text ?? "";
    }

    private static DescriptionTemplateEntry? FindMatchingTemplate(DbStringRow row, IEnumerable<DescriptionTemplateEntry> templates)
    {
        foreach (var template in templates)
        {
            if (KeyMatches(row.DescriptionTemplateKey, template.Key)
                || KeyMatches(row.DescriptionEnglish, template.Key)
                || KeyMatches(row.DescriptionOther, template.Key)
                || DescriptionPairMatchesTemplate(row, template))
            {
                return template;
            }
        }

        return null;
    }

    private static bool DescriptionPairMatchesTemplate(DbStringRow row, DescriptionTemplateEntry template)
    {
        var hasEnglish = !string.IsNullOrWhiteSpace(row.DescriptionEnglish);
        var hasOther = !string.IsNullOrWhiteSpace(row.DescriptionOther);
        var templateHasEnglish = !string.IsNullOrWhiteSpace(template.TextEnglish);
        var templateHasOther = !string.IsNullOrWhiteSpace(template.TextOther);

        var englishMatchesEnglish = TextMatches(row.DescriptionEnglish, template.TextEnglish);
        var otherMatchesOther = TextMatches(row.DescriptionOther, template.TextOther);
        var englishMatchesOther = TextMatches(row.DescriptionEnglish, template.TextOther);
        var otherMatchesEnglish = TextMatches(row.DescriptionOther, template.TextEnglish);

        if (hasEnglish && hasOther && templateHasEnglish && templateHasOther)
        {
            return (englishMatchesEnglish && otherMatchesOther)
                   || (englishMatchesOther && otherMatchesEnglish);
        }

        if (hasEnglish && hasOther && templateHasEnglish)
        {
            return englishMatchesEnglish && otherMatchesEnglish;
        }

        if (hasEnglish && hasOther && templateHasOther)
        {
            return englishMatchesOther && otherMatchesOther;
        }

        return hasEnglish && !hasOther && (englishMatchesEnglish || englishMatchesOther)
               || hasOther && !hasEnglish && (otherMatchesOther || otherMatchesEnglish);
    }

    private static DescriptionTemplateEntry? FindTemplateByKey(string key, IEnumerable<DescriptionTemplateEntry> templates)
    {
        return templates.FirstOrDefault(template => KeyMatches(key, template.Key));
    }

    private static DescriptionTemplateEntry? FindTemplateByKeyOrText(string text, string templateKey, IEnumerable<DescriptionTemplateEntry> templates)
    {
        foreach (var template in templates)
        {
            if (KeyMatches(templateKey, template.Key)
                || KeyMatches(text, template.Key)
                || TextMatches(text, template.TextEnglish)
                || TextMatches(text, template.TextOther))
            {
                return template;
            }
        }

        return null;
    }

    private static bool KeyMatches(string current, string key)
    {
        return !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(key)
            && current.Trim().Equals(key.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TextMatches(string current, string template)
    {
        return !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(template)
            && NormalizeTemplateText(current).Equals(NormalizeTemplateText(template), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTemplateText(string text)
    {
        return string.Join(" ", (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int SetIfDifferent(string current, string incoming, Action<string> set)
    {
        if (string.Equals(current ?? "", incoming ?? "", StringComparison.Ordinal))
        {
            return 0;
        }

        set(incoming ?? "");
        return 1;
    }

    private static string NormalizeScope(string scope)
    {
        return scope.Trim().Equals("ALM", StringComparison.OrdinalIgnoreCase)
            ? "ALM"
            : scope.Trim().Equals("PAR", StringComparison.OrdinalIgnoreCase)
                ? "PAR"
                : "Both";
    }

    private static IEnumerable<List<string>> ReadCsv(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            yield return ParseCsvLine(line);
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }
}
