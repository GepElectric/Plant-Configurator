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
