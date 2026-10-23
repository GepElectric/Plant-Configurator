using System.IO;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.Services;

/// <summary>One mapped WinCC Unified device as a push target, with the managed description
/// lists actually found on it (so the user pushes only where the lists exist).</summary>
public sealed class ScadaPushTarget
{
    public string DeviceName { get; init; } = "";
    public string ListsFile { get; init; } = "";
    public string LibraryFile { get; init; } = "";
    public List<string> FoundLists { get; } = new();
    public bool Selected { get; set; }

    public string Summary => FoundLists.Count == 0
        ? "no managed description lists found"
        : $"found: {string.Join(", ", FoundLists)}";
}

/// <summary>
/// Orchestrates pushing descriptions to WinCC Unified: scan each mapped device (export its
/// text lists, report which managed lists it has), then surgically update ONLY those lists'
/// texts and re-import — leaving every other list untouched. A backup of each device's
/// exported library is kept before import.
/// </summary>
public sealed class ScadaTextListPushService
{
    private readonly ITiaSessionService _session;
    private readonly TextListYamlWriter _writer = new();

    public ScadaTextListPushService(ITiaSessionService session) => _session = session;

    public async Task<IReadOnlyList<ScadaPushTarget>> ScanAsync(IReadOnlyList<string> deviceNames, IReadOnlyCollection<string> managedListNames)
    {
        var managed = new HashSet<string>(managedListNames, StringComparer.OrdinalIgnoreCase);
        var targets = new List<ScadaPushTarget>();
        foreach (var device in deviceNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = NewWorkspace();
            var export = await _session.ExportTextListsAsync(folder, device);
            var listsFile = FindFile(export.Files, "TextLists.hmi.yml");
            var libraryFile = FindFile(export.Files, "TextLists.TextLibrary.hmi.yml");

            var target = new ScadaPushTarget
            {
                DeviceName = device,
                ListsFile = listsFile ?? "",
                LibraryFile = libraryFile ?? ""
            };

            if (listsFile is not null)
            {
                foreach (var name in TextListYamlWriter.ListNamesPresent(listsFile))
                {
                    if (managed.Contains(name))
                    {
                        target.FoundLists.Add(name);
                    }
                }
            }

            target.Selected = target.FoundLists.Count > 0;
            targets.Add(target);
        }

        return targets;
    }

    /// <param name="typesDone">Lists already released as a new library-type version in this push (types are project-wide,
    /// so they are versioned once and every device is updated); filled by this call.</param>
    public async Task<string> PushAsync(ScadaPushTarget target, DescriptionTextListData appData, IReadOnlyCollection<string> managedListNames,
        HashSet<string>? typesDone = null)
    {
        typesDone ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(target.ListsFile) || string.IsNullOrWhiteSpace(target.LibraryFile)
            || !File.Exists(target.ListsFile) || !File.Exists(target.LibraryFile))
        {
            return $"{target.DeviceName}: no exported text lists to update (skipped).";
        }

        // Backup the exported pair before we touch anything.
        File.Copy(target.LibraryFile, target.LibraryFile + ".bak", overwrite: true);
        File.Copy(target.ListsFile, target.ListsFile + ".bak", overwrite: true);

        // Surgically rewrite ONLY the managed lists' texts back into the (in-place) library file.
        var result = _writer.UpdateLibrary(target.ListsFile, target.LibraryFile, appData, managedListNames, target.LibraryFile);

        var importFolder = Path.GetDirectoryName(target.ListsFile) ?? "";
        var changed = result.UpdatedLists.Where(list => !typesDone.Contains(list)).ToList();
        if (result.UpdatedLists.Count == 0)
        {
            return $"{target.DeviceName}: texts already up to date — nothing pushed.";
        }

        var lines = new List<string>();

        // TIA V21+: description text lists are library types (Types/SCADA_TEXT_LIST/<PLC>_...). Their texts change by
        // a new released version of the type, which updates the instances on every device.
        var notTypes = new List<string>();
        if (changed.Count > 0)
        {
            var types = await _session.UpdateTextListTypesAsync(importFolder, changed, "Texts from Plant Configurator");
            foreach (var updated in types.Updated)
            {
                lines.Add($"library type {updated} released");
            }

            notTypes.AddRange(types.NotTypes);
            lines.AddRange(types.Warnings);
            foreach (var list in changed.Except(types.NotTypes, StringComparer.OrdinalIgnoreCase))
            {
                typesDone.Add(list);
            }
        }

        // Text lists that are not library types: import the export folder into the device as before.
        if (notTypes.Count > 0)
        {
            var import = await _session.ImportTextListsAsync(importFolder, target.DeviceName);
            lines.Add(import.Success
                ? $"imported into the device: {string.Join(", ", notTypes)}"
                : $"import FAILED ({string.Join("; ", import.Warnings)}). Backup: {Path.GetFileName(target.LibraryFile)}.bak");
        }

        return $"{target.DeviceName}: {result.UpdatedEntries} changed text(s) in {result.UpdatedLists.Count} list(s). {string.Join(" · ", lines)}";
    }

    /// <summary>
    /// TIA V21+: description text lists that are library types (Types/SCADA_TEXT_LIST/...) are pushed through
    /// "Export/Import project texts": the released texts are exported and compared; only the changed types get a new
    /// in-work version, whose texts are exported, changed and imported, then released (every device follows).
    /// Lists that are not library types are returned in <paramref name="notTypes"/> for the device push.
    /// </summary>
    /// <param name="chooseOtherLanguage">Asked when TIA has more than one language: (TIA languages, app English, app other
    /// language) → the TIA language that gets the other-language texts, "" for English only, null to cancel.</param>
    public async Task<string> PushLibraryTypesAsync(DescriptionTextListData appData, IReadOnlyCollection<string> managedListNames, List<string> notTypes,
        Func<IReadOnlyList<string>, string, string, string?> chooseOtherLanguage)
    {
        var names = managedListNames.Where(name => appData.Lists.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            return "No description texts to push.";
        }

        var folder = NewWorkspace();
        var lines = new List<string>();
        var current = Path.Combine(folder, "released.xlsx");
        var export = await _session.ExportTextListTypeTextsAsync(current, names, inWork: false);
        notTypes.AddRange(export.NotTypes);
        lines.AddRange(export.Warnings);
        if (export.Updated.Count == 0)
        {
            return string.Join(Environment.NewLine, lines);
        }

        // English always goes to the English (or editing-language) column; the other language only where the user says.
        var (english, other) = DescriptionTextListService.GetLanguagePair(appData);
        var tiaLanguages = ProjectTextsWorkbook.Languages(current);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [tiaLanguages.FirstOrDefault(language => language.Equals(english, StringComparison.OrdinalIgnoreCase)) ?? tiaLanguages[0]] = english
        };
        if (tiaLanguages.Count > 1 && !other.Equals(english, StringComparison.OrdinalIgnoreCase))
        {
            var target = chooseOtherLanguage(tiaLanguages, english, other);
            if (target is null)
            {
                return "Push cancelled.";
            }

            if (target.Length > 0)
            {
                sources[target] = other;
            }
        }

        var languageNote = string.Join(", ", sources.Select(source => $"{source.Value} → TIA {source.Key}"));
        var changes = ProjectTextsWorkbook.Apply(current, appData, sources, save: false);
        var changed = changes.Where(change => change.Value > 0).Select(change => change.Key).ToList();
        if (changed.Count == 0)
        {
            lines.Insert(0, $"Library text lists already up to date ({export.Updated.Count} checked; {languageNote}) — nothing pushed.");
            return string.Join(Environment.NewLine, lines);
        }

        var edit = Path.Combine(folder, "in_work.xlsx");
        var opened = await _session.ExportTextListTypeTextsAsync(edit, changed, inWork: true);
        lines.AddRange(opened.Warnings);
        if (opened.Updated.Count == 0)
        {
            return string.Join(Environment.NewLine, lines);
        }

        string? input = edit;
        var count = 0;
        try
        {
            count = ProjectTextsWorkbook.Apply(edit, appData, sources, save: true).Values.Sum();
        }
        catch (Exception ex)
        {
            lines.Add("Changing the exported texts failed: " + ex.Message);
            input = null; // discard the in-work versions
        }

        var finished = await _session.ImportTextListTypeTextsAsync(input, opened.Updated, "Texts from Plant Configurator");
        if (finished.Updated.Count > 0)
        {
            lines.Insert(0, $"{count} changed text(s) ({languageNote}) — released: {string.Join(", ", finished.Updated)}.");
        }

        lines.AddRange(finished.Warnings);
        return string.Join(Environment.NewLine, lines);
    }

    private static string NewWorkspace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PlantConfigurator", "TextListsPush", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string? FindFile(IEnumerable<string> files, string fileName)
    {
        return files.FirstOrDefault(file => Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }
}
