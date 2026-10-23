using System.IO;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.Services;

public sealed class ScadaUnifiedPullResult
{
    public ScadaDatabase Database { get; init; } = ScadaDatabase.CreateDefault();
    public string Message { get; init; } = "";
}

/// <summary>
/// Round-trips the SCADA navigation/config script (<c>AUX_FUNCTIONS._SET_INTERNAL_TAG</c>)
/// between the app and the live WinCC Unified project: through the attached TIA session (TIA Openness,
/// <see cref="PullFromTiaAsync"/> / <see cref="PushToTiaAsync"/>) or over the Openness REST service (JS Connector).
///
/// Pull: export the module → isolate the owned function body → parse it with
/// <see cref="ScadaConfigFileService"/>. Push: regenerate the body from the app, then
/// re-export the live module to capture a fresh header/footer, splice only the owned body
/// back in, and import the module — so the global definition area, the protected marker
/// and every other function survive untouched.
/// </summary>
public sealed class ScadaUnifiedSyncService
{
    public const string ModuleName = "AUX_FUNCTIONS";

    private readonly ScadaConfigFileService _scadaService = new();

    public async Task<ScadaUnifiedPullResult> PullAsync(string configuredServerPath, string? deviceName = null, CancellationToken cancellationToken = default)
    {
        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, deviceName, cancellationToken);

        var rootPath = CreateWorkspace();
        var modulePath = await runtime.ExportScriptModuleAsync(processId, device, ModuleName, rootPath, cancellationToken);

        var module = ScadaInternalTagModule.SplitFile(modulePath);
        var bodyPath = Path.Combine(rootPath, "_set_internal_tag_body.js");
        File.WriteAllText(bodyPath, module.Body);

        var database = _scadaService.Import(bodyPath);
        return new ScadaUnifiedPullResult
        {
            Database = database,
            Message = $"Pulled {ModuleName}.{ScadaInternalTagModule.DefaultFunctionName} from WinCC Unified device '{device.DisplayName}/{device.DeviceName}'."
        };
    }

    public async Task<string> PushAsync(ScadaDatabase database, string configuredServerPath, string? deviceName = null, CancellationToken cancellationToken = default)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, deviceName, cancellationToken);

        var rootPath = CreateWorkspace();

        // Re-export the live module first so we splice onto its current header/footer
        // (global definition area, protected marker, sibling functions) rather than a
        // possibly stale copy.
        var modulePath = await runtime.ExportScriptModuleAsync(processId, device, ModuleName, rootPath, cancellationToken);
        var module = ScadaInternalTagModule.SplitFile(modulePath);

        // Back up the exact module we are about to overwrite, for rollback.
        File.Copy(modulePath, modulePath + ".bak", overwrite: true);

        var bodyPath = Path.Combine(rootPath, "_set_internal_tag_body.js");
        _scadaService.Export(database, bodyPath);
        var newBody = File.ReadAllText(bodyPath);

        File.WriteAllText(modulePath, module.Rewrap(newBody));

        var serverResponse = await runtime.ImportScriptModuleAsync(processId, device, ModuleName, rootPath, cancellationToken);
        return $"Pushed {ModuleName}.{ScadaInternalTagModule.DefaultFunctionName} to WinCC Unified device '{device.DisplayName}/{device.DeviceName}'. Backup: {modulePath}.bak{Environment.NewLine}{serverResponse}";
    }

    /// <summary>
    /// Pull through the attached TIA project: exports <c>AUX_FUNCTIONS</c> of the mapped Unified device, reads the
    /// <c>_SET_INTERNAL_TAG</c> body into a SCADA database and reads the device's screens.
    /// </summary>
    /// <param name="withGraphicLists">Geppert SCADA: also pull the icon graphic lists with their images.</param>
    public async Task<ScadaUnifiedPullResult> PullFromTiaAsync(ITiaSessionService session, string deviceName, bool withGraphicLists = true)
    {
        var rootPath = CreateWorkspace();
        var modulePath = await ExportModuleAsync(session, deviceName, rootPath);
        var module = ScadaInternalTagModule.SplitFile(modulePath);
        var bodyPath = Path.Combine(rootPath, "_set_internal_tag_body.js");
        File.WriteAllText(bodyPath, module.Body);

        var database = _scadaService.Import(bodyPath);
        var screens = await session.GetUnifiedScreensAsync(deviceName);
        database.TiaScreens = screens.Screens.ToList();
        var graphics = "";
        if (withGraphicLists)
        {
            try
            {
                database.GraphicLists = await PullGraphicListsAsync(session, deviceName);
                graphics = $", graphic lists {string.Join(", ", database.GraphicLists.Select(list => $"{list.Name} ({list.Entries.Count})"))}";
            }
            catch (Exception ex)
            {
                graphics = $" (graphic lists not read: {ex.Message})";
            }
        }

        return new ScadaUnifiedPullResult
        {
            Database = database,
            Message = $"Pulled {ModuleName}.{ScadaInternalTagModule.DefaultFunctionName} and {database.TiaScreens.Count} screen(s){graphics} from '{deviceName}'."
        };
    }

    /// <summary>Graphic lists MAIN_NAV_BUTTONS, TOP_NAV_ICONS and TREND_ICONS of the device, with the images of their graphics.</summary>
    public async Task<List<ScadaGraphicList>> PullGraphicListsAsync(ITiaSessionService session, string deviceName)
    {
        if (session.CurrentSession is null)
        {
            throw new InvalidOperationException("Attach to the TIA Portal project (V21) in 'TIA Sync' first.");
        }

        var folder = Path.Combine(CreateWorkspace(), "graphic_lists");
        var export = await session.ExportUnifiedGraphicsAsync(folder, deviceName);
        if (!export.Success)
        {
            throw new InvalidOperationException(string.Join(" ", export.Warnings.DefaultIfEmpty("the graphic lists could not be exported.")));
        }

        return ScadaGraphicLists.Read(folder);
    }

    /// <summary>
    /// Push through the attached TIA project: re-exports the live module, replaces only the
    /// <c>_SET_INTERNAL_TAG</c> body and imports the module back (a copy of the replaced module is kept).
    /// The TIA project is not saved.
    /// </summary>
    public async Task<string> PushToTiaAsync(ITiaSessionService session, ScadaDatabase database, string deviceName)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        var rootPath = CreateWorkspace();
        var moduleFolder = Path.Combine(rootPath, "module");
        var modulePath = await ExportModuleAsync(session, deviceName, moduleFolder);
        var module = ScadaInternalTagModule.SplitFile(modulePath);

        // Backup outside the import folder, so only the module itself is imported.
        var backupPath = Path.Combine(rootPath, Path.GetFileName(modulePath) + ".bak");
        File.Copy(modulePath, backupPath, overwrite: true);

        var bodyPath = Path.Combine(rootPath, "_set_internal_tag_body.js");
        _scadaService.Export(database, bodyPath);
        File.WriteAllText(modulePath, module.Rewrap(File.ReadAllText(bodyPath)));

        var import = await session.ImportUnifiedScriptAsync(moduleFolder, deviceName);
        if (!import.Success)
        {
            throw new InvalidOperationException($"Push to '{deviceName}' failed: {string.Join(" ", import.Warnings)}");
        }

        return $"Pushed {ModuleName}.{ScadaInternalTagModule.DefaultFunctionName} to '{deviceName}' (TIA project not saved). Backup: {backupPath}";
    }

    private static async Task<string> ExportModuleAsync(ITiaSessionService session, string deviceName, string folder)
    {
        if (session.CurrentSession is null)
        {
            throw new InvalidOperationException("Attach to the TIA Portal project (V21) in 'TIA Sync' first.");
        }

        Directory.CreateDirectory(folder);
        var export = await session.ExportUnifiedScriptAsync(folder, deviceName, ModuleName);
        var modulePath = export.Files.FirstOrDefault(file => file.EndsWith(".hmi.js", StringComparison.OrdinalIgnoreCase));
        if (!export.Success || modulePath is null)
        {
            throw new InvalidOperationException(
                $"Could not export {ModuleName} from '{deviceName}': {string.Join(" ", export.Warnings.DefaultIfEmpty("no script file was exported."))}");
        }

        return modulePath;
    }

    private static async Task<(int ProcessId, UnifiedDeviceInfo Device)> ResolveScadaDeviceAsync(
        UnifiedJsConnectorRuntime runtime,
        string configuredServerPath,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        var candidates = await ListScadaDevicesAsync(runtime, configuredServerPath, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No WinCC Unified device exposing the '{ModuleName}' script module was found in any open TIA project.");
        }

        // The mapped device name is the HMI device name (e.g. "SCADA_PC") = HmiSoftware.Name,
        // which the REST service returns as DeviceName (DisplayName is the friendly PC name).
        var chosen = !string.IsNullOrWhiteSpace(deviceName)
            ? candidates.FirstOrDefault(candidate =>
                candidate.Device.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            : default;

        if (chosen == default)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                throw new InvalidOperationException(
                    $"WinCC Unified device '{deviceName}' was not found. Available: {string.Join(", ", candidates.Select(c => c.Device.DeviceName))}.");
            }

            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one WinCC Unified device exposes '{ModuleName}': {string.Join(", ", candidates.Select(c => c.Device.DeviceName))}. Map the SCADA database to a device first.");
            }

            chosen = candidates[0];
        }

        return (chosen.ProcessId, chosen.Device);
    }

    private static async Task<List<(int ProcessId, UnifiedDeviceInfo Device)>> ListScadaDevicesAsync(
        UnifiedJsConnectorRuntime runtime,
        string configuredServerPath,
        CancellationToken cancellationToken)
    {
        var processes = await runtime.StartAndFetchProcessesAsync(configuredServerPath, cancellationToken);
        if (processes.Count == 0)
        {
            throw new InvalidOperationException("No running TIA Portal / WinCC Unified instance was found. Open the SCADA project first.");
        }

        var result = new List<(int ProcessId, UnifiedDeviceInfo Device)>();
        foreach (var process in processes)
        {
            var devices = await runtime.GetUnifiedDevicesAsync(process.ProcessId, cancellationToken);
            foreach (var device in devices.Where(item =>
                         item.GlobalModules.Any(module => module.Equals(ModuleName, StringComparison.OrdinalIgnoreCase))))
            {
                result.Add((process.ProcessId, device));
            }
        }

        return result;
    }

    private static string CreateWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PlantConfigurator",
            "ScadaUnified",
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(root);
        return root;
    }
}
