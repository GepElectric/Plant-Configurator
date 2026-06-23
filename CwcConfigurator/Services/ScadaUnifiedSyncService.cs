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

    /// <summary>
    /// Display names of every WinCC Unified device that exposes the <see cref="ModuleName"/>
    /// module, across all running TIA processes — the candidates for Pull/Push.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListScadaDeviceNamesAsync(string configuredServerPath, CancellationToken cancellationToken = default)
    {
        using var runtime = new UnifiedJsConnectorRuntime();
        var candidates = await ListScadaDevicesAsync(runtime, configuredServerPath, cancellationToken);
        return candidates
            .Select(candidate => candidate.Device.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ScadaUnifiedPullResult> PullAsync(string configuredServerPath, string? preferredDeviceDisplayName = null, CancellationToken cancellationToken = default)
    {
        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, preferredDeviceDisplayName, cancellationToken);

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

    public async Task<string> PushAsync(ScadaDatabase database, string configuredServerPath, string? preferredDeviceDisplayName = null, CancellationToken cancellationToken = default)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        using var runtime = new UnifiedJsConnectorRuntime();
        var (processId, device) = await ResolveScadaDeviceAsync(runtime, configuredServerPath, preferredDeviceDisplayName, cancellationToken);

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
        string? preferredDeviceDisplayName,
        CancellationToken cancellationToken)
    {
        var candidates = await ListScadaDevicesAsync(runtime, configuredServerPath, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No WinCC Unified device exposing the '{ModuleName}' script module was found in any open TIA project.");
        }

        var chosen = !string.IsNullOrWhiteSpace(preferredDeviceDisplayName)
            ? candidates.FirstOrDefault(candidate =>
                candidate.Device.DisplayName.Equals(preferredDeviceDisplayName, StringComparison.OrdinalIgnoreCase))
            : default;

        if (chosen == default)
        {
            // No (or unmatched) preference: fall back to the only/first candidate, but make a
            // multi-device project explicit so the user picks instead of silently guessing.
            if (!string.IsNullOrWhiteSpace(preferredDeviceDisplayName))
            {
                throw new InvalidOperationException(
                    $"WinCC Unified device '{preferredDeviceDisplayName}' was not found. Available: {string.Join(", ", candidates.Select(c => c.Device.DisplayName))}.");
            }

            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one WinCC Unified device exposes '{ModuleName}': {string.Join(", ", candidates.Select(c => c.Device.DisplayName))}. Select the SCADA device first.");
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
