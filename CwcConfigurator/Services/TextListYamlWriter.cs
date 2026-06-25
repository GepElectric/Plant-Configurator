using System.IO;
using CwcConfigurator.Domain;
using YamlDotNet.Serialization;

namespace CwcConfigurator.Services;

public sealed class TextListPushResult
{
    public List<string> UpdatedLists { get; } = new();
    public int UpdatedEntries { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Surgically updates a WinCC Unified text-list export so that ONLY the managed description
/// lists carry the app's texts. It edits text values inside the library YAML in place
/// (deserialize → change the referenced texts → reserialize, preserving the file header) and
/// leaves the lists YAML and every other list / library entry untouched. This is the
/// text-list equivalent of the <c>_SET_INTERNAL_TAG</c> splice: a merge, never a replace.
/// </summary>
public sealed class TextListYamlWriter
{
    /// <summary>Names of text lists present in the exported SCADA (used by the push selector).</summary>
    public static IReadOnlyList<string> ListNamesPresent(string textListsYmlPath)
    {
        return ParseListRefs(textListsYmlPath, null).Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public TextListPushResult UpdateLibrary(
        string textListsYmlPath,
        string libraryYmlPath,
        DescriptionTextListData appData,
        IReadOnlyCollection<string> managedListNames,
        string outLibraryPath)
    {
        var result = new TextListPushResult();
        var managed = new HashSet<string>(managedListNames, StringComparer.OrdinalIgnoreCase);
        var listRefs = ParseListRefs(textListsYmlPath, managed);

        if (Deserialize(libraryYmlPath) is not IDictionary<object, object> root
            || !TryGetMap(root, "TextLibraries", out var libraries))
        {
            result.Warnings.Add("Library YAML has no TextLibraries section.");
            return result;
        }

        var libs = new Dictionary<string, (List<string> Languages, IDictionary<object, object> Entries)>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (library.Value is IDictionary<object, object> map && TryGetMap(map, "Entries", out var entries))
            {
                libs[library.Key?.ToString() ?? ""] = (ToStringList(map, "Languages"), entries);
            }
        }

        foreach (var (listName, idToRef) in listRefs)
        {
            var listUpdated = false;
            foreach (var (id, textRef) in idToRef)
            {
                if (!TrySplitRef(textRef, libs.Keys, out var libName, out var entryKey))
                {
                    continue;
                }

                var lib = libs[libName];
                if (!lib.Entries.TryGetValue(entryKey, out var entryObj)
                    || entryObj is not IDictionary<object, object> entryMap
                    || !entryMap.TryGetValue("Text", out var textObj)
                    || textObj is not IList<object> textSeq)
                {
                    continue;
                }

                for (var i = 0; i < lib.Languages.Count && i < textSeq.Count; i++)
                {
                    var language = lib.Languages[i];
                    if (appData.Lists.TryGetValue(listName, out var byLanguage)
                        && byLanguage.TryGetValue(language, out var byId)
                        && byId.TryGetValue(id, out var appText)
                        && !string.Equals(textSeq[i]?.ToString(), appText, StringComparison.Ordinal))
                    {
                        textSeq[i] = appText ?? "";
                        result.UpdatedEntries++;
                        listUpdated = true;
                    }
                }
            }

            if (listUpdated)
            {
                result.UpdatedLists.Add(listName);
            }
        }

        Serialize(root, outLibraryPath, ReadLeadingComments(libraryYmlPath));
        return result;
    }

    // managed == null -> all lists; otherwise only the named ones. Result: list -> (id -> textRef).
    private static Dictionary<string, Dictionary<int, string>> ParseListRefs(string textListsYmlPath, ISet<string>? managed)
    {
        var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        if (Deserialize(textListsYmlPath) is not IDictionary<object, object> root
            || !TryGetMap(root, "TextListContainers", out var containers))
        {
            return result;
        }

        foreach (var container in containers)
        {
            if (container.Value is not IDictionary<object, object> containerMap
                || !TryGetMap(containerMap, "ResourceLists", out var lists))
            {
                continue;
            }

            foreach (var list in lists)
            {
                var listName = list.Key?.ToString() ?? "";
                if (managed != null && !managed.Contains(listName))
                {
                    continue;
                }

                if (list.Value is not IDictionary<object, object> listMap || !TryGetMap(listMap, "Entries", out var entries))
                {
                    continue;
                }

                var idMap = new Dictionary<int, string>();
                foreach (var entry in entries)
                {
                    if (entry.Value is IDictionary<object, object> entryMap
                        && entryMap.TryGetValue("Value", out var rawValue)
                        && int.TryParse(rawValue?.ToString(), out var id)
                        && entryMap.TryGetValue("Text", out var rawRef))
                    {
                        idMap[id] = rawRef?.ToString() ?? "";
                    }
                }

                result[listName] = idMap;
            }
        }

        return result;
    }

    private static bool TrySplitRef(string textRef, IEnumerable<string> libraryNames, out string libraryName, out string entryKey)
    {
        foreach (var name in libraryNames)
        {
            if (!string.IsNullOrEmpty(name) && textRef.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            {
                libraryName = name;
                entryKey = textRef.Substring(name.Length + 1);
                return true;
            }
        }

        libraryName = "";
        entryKey = "";
        return false;
    }

    private static object? Deserialize(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path);
        return new DeserializerBuilder().Build().Deserialize(reader);
    }

    private static void Serialize(object root, string path, string leadingComments)
    {
        var serializer = new SerializerBuilder().Build();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, false);
        if (!string.IsNullOrEmpty(leadingComments))
        {
            writer.Write(leadingComments);
        }

        serializer.Serialize(writer, root);
    }

    // Preserves the leading "#..." header lines (e.g. "#Version: 2.0") that the deserializer drops.
    private static string ReadLeadingComments(string path)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                builder.AppendLine(line);
            }
            else if (line.Trim().Length == 0)
            {
                builder.AppendLine(line);
            }
            else
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static bool TryGetMap(IDictionary<object, object> node, string key, out IDictionary<object, object> map)
    {
        if (node.TryGetValue(key, out var value) && value is IDictionary<object, object> inner)
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
