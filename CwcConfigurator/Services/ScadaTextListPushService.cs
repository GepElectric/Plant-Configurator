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

    public async Task<string> PushAsync(ScadaPushTarget target, DescriptionTextListData appData, IReadOnlyCollection<string> managedListNames)
    {
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

        // Import the export folder (original lists file + updated library file) back into the device.
        var importFolder = Path.GetDirectoryName(target.ListsFile) ?? "";
        var import = await _session.ImportTextListsAsync(importFolder, target.DeviceName);

        var status = import.Success
            ? $"{target.DeviceName}: updated {result.UpdatedEntries} text(s) in {result.UpdatedLists.Count} list(s) [{string.Join(", ", result.UpdatedLists)}]."
            : $"{target.DeviceName}: import FAILED ({string.Join("; ", import.Warnings)}). Backup: {Path.GetFileName(target.LibraryFile)}.bak";
        return status;
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
