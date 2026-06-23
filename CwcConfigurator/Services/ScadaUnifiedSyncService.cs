using System.IO;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ScadaUnifiedPullResult
{
    public ScadaDatabase Database { get; init; } = ScadaDatabase.CreateDefault();
    public string Message { get; init; } = "";
}

/// <summary>
/// Round-trips the SCADA navigation/config script (<c>AUX_FUNCTIONS._SET_INTERNAL_TAG</c>)
/// between the app and the live WinCC Unified project over the Openness REST service.
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

    public async Task<ScadaUnifiedPullResult> PullAsync(string configuredServerPath, CancellationToken cancellationToken = default)
    {
        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, cancellationToken);

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

    public async Task<string> PushAsync(ScadaDatabase database, string configuredServerPath, CancellationToken cancellationToken = default)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, cancellationToken);

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

    private static async Task<(int ProcessId, UnifiedDeviceInfo Device)> ResolveScadaDeviceAsync(
        UnifiedJsConnectorRuntime runtime,
        string configuredServerPath,
        CancellationToken cancellationToken)
    {
        var processes = await runtime.StartAndFetchProcessesAsync(configuredServerPath, cancellationToken);
        if (processes.Count == 0)
        {
            throw new InvalidOperationException("No running TIA Portal / WinCC Unified instance was found. Open the SCADA project first.");
        }

        foreach (var process in processes)
        {
            var devices = await runtime.GetUnifiedDevicesAsync(process.ProcessId, cancellationToken);
            var device = devices.FirstOrDefault(item =>
                item.GlobalModules.Any(module => module.Equals(ModuleName, StringComparison.OrdinalIgnoreCase)));
            if (device is not null)
            {
                return (process.ProcessId, device);
            }
        }

        throw new InvalidOperationException(
            $"No WinCC Unified device exposing the '{ModuleName}' script module was found in any open TIA project.");
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
