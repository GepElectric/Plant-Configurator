using System.IO;
using CwcConfigurator.Domain;
using YamlDotNet.Serialization;

namespace CwcConfigurator.Services;

/// <summary>
/// Parses the two YAML files produced by WinCC Unified Openness text-list export
/// (<c>TextLists.hmi.yml</c> = lists -&gt; entries -&gt; text reference, and
/// <c>TextLists.TextLibrary.hmi.yml</c> = reference -&gt; text per language) into the
/// app's <see cref="DescriptionTextListData"/>.
/// </summary>
public sealed class TextListYamlImporter
{
    public DescriptionTextListData Import(string textListsYmlPath, string libraryYmlPath)
    {
        var data = new DescriptionTextListData { SourcePath = textListsYmlPath };
        var library = ParseLibrary(libraryYmlPath);
        ApplyLists(textListsYmlPath, library, data);
        return data;
    }

    // "<LibraryName>.<EntryKey>" -> { language -> text }
    private static Dictionary<string, Dictionary<string, string>> ParseLibrary(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) || !TryMap(Deserialize(path), "TextLibraries", out var libraries))
        {
            return result;
        }

        foreach (var library in libraries)
        {
            var libraryName = library.Key?.ToString() ?? "";
            if (library.Value is not IDictionary<object, object> libraryMap)
            {
                continue;
            }

            var languages = ToStringList(libraryMap, "Languages");
            if (!TryMap(libraryMap, "Entries", out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var entryKey = entry.Key?.ToString() ?? "";
                if (entry.Value is not IDictionary<object, object> entryMap)
                {
                    continue;
                }

                var texts = ToStringList(entryMap, "Text");
                var byLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < languages.Count && i < texts.Count; i++)
                {
                    byLanguage[languages[i]] = texts[i];
                }

                result[libraryName + "." + entryKey] = byLanguage;
            }
        }

        return result;
    }

    private static void ApplyLists(string path, Dictionary<string, Dictionary<string, string>> library, DescriptionTextListData data)
    {
        if (!File.Exists(path) || !TryMap(Deserialize(path), "TextListContainers", out var containers))
        {
            return;
        }

        foreach (var container in containers)
        {
            if (container.Value is not IDictionary<object, object> containerMap
                || !TryMap(containerMap, "ResourceLists", out var lists))
            {
                continue;
            }

            foreach (var list in lists)
            {
                var listName = list.Key?.ToString() ?? "";
                if (list.Value is not IDictionary<object, object> listMap
                    || !TryMap(listMap, "Entries", out var entries))
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.Value is not IDictionary<object, object> entryMap
                        || !entryMap.TryGetValue("Value", out var rawValue)
                        || !int.TryParse(rawValue?.ToString(), out var id))
                    {
                        continue;
                    }

                    var textRef = entryMap.TryGetValue("Text", out var rawRef) ? rawRef?.ToString() ?? "" : "";
                    if (library.TryGetValue(textRef, out var byLanguage))
                    {
                        foreach (var pair in byLanguage)
                        {
                            data.SetText(listName, id, pair.Key, pair.Value);
                        }
                    }
                }
            }
        }
    }

    private static object? Deserialize(string path)
    {
        using var reader = new StreamReader(path);
        return new DeserializerBuilder().Build().Deserialize(reader);
    }

    private static bool TryMap(object? node, string key, out IDictionary<object, object> map)
    {
        if (node is IDictionary<object, object> dictionary
            && dictionary.TryGetValue(key, out var value)
            && value is IDictionary<object, object> inner)
        {
            map = inner;
            return true;
        }

        map = new Dictionary<object, object>();
        return false;
    }

    private static List<string> ToStringList(IDictionary<object, object> map, string key)
    {
        var result = new List<string>();
        if (map.TryGetValue(key, out var value) && value is IList<object> sequence)
        {
            foreach (var item in sequence)
            {
                result.Add(item?.ToString() ?? "");
            }
        }

        return result;
    }
}
