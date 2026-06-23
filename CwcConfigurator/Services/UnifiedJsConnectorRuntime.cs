using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace CwcConfigurator.Services;

public sealed class UnifiedJsConnectorRuntime : IDisposable
{
    private const string ServerExecutableName = "Siemens.Simatic.Hmi.Openness.RestService.exe";
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://localhost:7979/") };
    private Process? _process;

    public string ResolveServerPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var appBase = AppContext.BaseDirectory;
        var packagedPath = Path.Combine(appBase, "Tools", "WinCCUnifiedJsConnector", "server", ServerExecutableName);
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlantConfigurator",
            "WinCCUnifiedJsConnector");
        if (Directory.Exists(localAppData))
        {
            var importedPath = Directory
                .EnumerateFiles(localAppData, ServerExecutableName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (importedPath is not null)
            {
                return importedPath;
            }
        }

        throw new FileNotFoundException("WinCC Unified JS Connector REST server was not found in Plant Configurator.");
    }

    public async Task<IReadOnlyList<UnifiedTiaProcessInfo>> StartAndFetchProcessesAsync(string configuredPath, CancellationToken cancellationToken = default)
    {
        if (!await IsHealthyAsync(cancellationToken))
        {
            Start(ResolveServerPath(configuredPath));
            await WaitUntilHealthyAsync(cancellationToken);
        }

        return await FetchProcessesAsync(cancellationToken);
    }

    private void Start(string serverPath)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                WorkingDirectory = Path.GetDirectoryName(serverPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        _process.Start();
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("WinCC Unified JS Connector REST server did not answer on http://localhost:7979.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("tiaProcesses", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<UnifiedTiaProcessInfo>> FetchProcessesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("tiaProcesses", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        var processes = new List<UnifiedTiaProcessInfo>();
        if (!document.RootElement.TryGetProperty("tiaProcesses", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return processes;
        }

        foreach (var item in array.EnumerateArray())
        {
            processes.Add(new UnifiedTiaProcessInfo(
                GetInt(item, "Id", "ProcessId"),
                GetString(item, "ProjectName", "Name"),
                GetString(item, "ProjectPath", "Path"),
                GetString(item, "PortalVersion", "Version")));
        }

        return processes;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property))
            {
                return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString();
            }
        }

        return "";
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// Lists the WinCC Unified devices of a TIA process together with their global script
    /// modules (e.g. <c>AUX_FUNCTIONS</c>). Requires the REST server to be running, which
    /// <see cref="StartAndFetchProcessesAsync"/> guarantees.
    /// </summary>
    public async Task<IReadOnlyList<UnifiedDeviceInfo>> GetUnifiedDevicesAsync(int processId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"unifiedDevices?processId={processId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        var devices = new List<UnifiedDeviceInfo>();
        if (!document.RootElement.TryGetProperty("unifiedDevices", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return devices;
        }

        foreach (var item in array.EnumerateArray())
        {
            var modules = new List<string>();
            if (item.TryGetProperty("GlobalModuleList", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var module in list.EnumerateArray())
                {
                    if (module.ValueKind == JsonValueKind.String)
                    {
                        modules.Add(module.GetString() ?? "");
                    }
                }
            }

            devices.Add(new UnifiedDeviceInfo(
                GetString(item, "DisplayName"),
                GetString(item, "Name", "DeviceName"),
                modules));
        }

        return devices;
    }

    /// <summary>
    /// Exports a single global script module to disk and returns the path of the generated
    /// <c>.hmi.js</c> file. Read-only with respect to the TIA project.
    /// </summary>
    public async Task<string> ExportScriptModuleAsync(int processId, UnifiedDeviceInfo device, string moduleName, string rootPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(rootPath);
        using var content = BuildModuleRequest(device, moduleName, rootPath);
        using var response = await _httpClient.PostAsync($"export/unifiedDevices?processId={processId}", content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"WinCC Unified export failed ({(int)response.StatusCode}). {responseJson}");
        }

        var path = GetScriptModulePath(rootPath, device.DisplayName, moduleName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Exported script module was not found after export. Server response: {responseJson}", path);
        }

        return path;
    }

    /// <summary>
    /// Imports the (edited) module sitting under <paramref name="rootPath"/> back into the
    /// running WinCC Unified project. The edited <c>.hmi.js</c> must already be in place.
    /// </summary>
    public async Task<string> ImportScriptModuleAsync(int processId, UnifiedDeviceInfo device, string moduleName, string rootPath, CancellationToken cancellationToken = default)
    {
        var path = GetScriptModulePath(rootPath, device.DisplayName, moduleName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Edited script module to import was not found.", path);
        }

        using var content = BuildModuleRequest(device, moduleName, rootPath);
        using var response = await _httpClient.PostAsync($"import/unifiedDevices?processId={processId}", content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"WinCC Unified import failed ({(int)response.StatusCode}). {responseJson}");
        }

        return responseJson;
    }

    /// <summary>Disk layout the REST server uses for an exported/imported module.</summary>
    public static string GetScriptModulePath(string rootPath, string displayName, string moduleName)
    {
        return Path.Combine(rootPath, displayName, "Scripts", moduleName, moduleName + ".hmi.js");
    }

    private static StringContent BuildModuleRequest(UnifiedDeviceInfo device, string moduleName, string rootPath)
    {
        var body = new
        {
            RootPath = rootPath,
            Entities = new[]
            {
                new
                {
                    DisplayName = device.DisplayName,
                    DeviceName = device.DeviceName,
                    GlobalModuleList = new[] { moduleName }
                }
            }
        };

        return new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }
    }
}

public sealed record UnifiedTiaProcessInfo(int ProcessId, string ProjectName, string ProjectPath, string PortalVersion);

public sealed record UnifiedDeviceInfo(string DisplayName, string DeviceName, IReadOnlyList<string> GlobalModules);
