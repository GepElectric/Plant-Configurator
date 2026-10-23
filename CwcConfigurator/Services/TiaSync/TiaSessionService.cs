using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaSessionService : ITiaSessionService, IDisposable
{
    private static readonly TimeSpan HelperResponseTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // One helper process per installed TIA version ("V18"/"V21"). Each helper build
    // binds only its own Openness assemblies, so process listing queries every
    // installed version and an attached session pins the active version — all later
    // commands route to the helper that owns the attached process.
    private readonly Dictionary<string, Process> _helpers = new(StringComparer.OrdinalIgnoreCase);
    private int _nextRequestId;
    private bool _disposed;
    private string? _activeVersion;

    public TiaAttachedSession? CurrentSession { get; private set; }

    public async Task<IList<TiaPortalProcessInfo>> GetRunningPortalProcessesAsync()
    {
        var result = new List<TiaPortalProcessInfo>();
        foreach (var version in TiaApiLocator.GetInstalledVersions())
        {
            try
            {
                var response = await SendAsync<TiaProcessListResponse>(version, "GetProcesses", null);
                foreach (var process in response.Processes)
                {
                    if (string.IsNullOrWhiteSpace(process.Version))
                    {
                        process.Version = version;
                    }

                    result.Add(process);
                }
            }
            catch
            {
                // A version whose helper cannot answer (its Openness runtime is
                // unavailable, etc.) must not block discovery of the other version.
            }
        }

        return result;
    }

    public async Task<TiaAttachedSession> AttachToRunningPortalAsync(int processId, string portalVersion)
    {
        var version = NormalizeVersion(portalVersion);
        var response = await SendAsync<TiaAttachedSession>(version, "Attach", new { ProcessId = processId });
        _activeVersion = version;
        CurrentSession = response;
        return response;
    }

    public async Task DisconnectAsync()
    {
        var version = _activeVersion;
        CurrentSession = null;
        _activeVersion = null;
        if (version is null
            || !_helpers.TryGetValue(version, out var helper)
            || helper.HasExited)
        {
            return;
        }

        await SendAsync<object>(version, "Disconnect", null);
    }

    public Task<TiaSourceTransferResult> ExportSourcesAsync(IEnumerable<TiaPlcSourceExportRequest> plcs, string outputFolder)
    {
        return SendAsync<TiaSourceTransferResult>(RequireActiveVersion(), "ExportSources", new
        {
            OutputFolder = outputFolder,
            Plcs = plcs.ToList()
        });
    }

    public Task<TiaSourceTransferResult> ImportSourcesAsync(IEnumerable<TiaPlcSourceImportRequest> plcs)
    {
        return SendAsync<TiaSourceTransferResult>(RequireActiveVersion(), "ImportSources", new
        {
            Plcs = plcs.ToList()
        });
    }

    public Task<TiaCrossReferenceResult> GetCrossReferencesAsync(TiaCrossReferenceRequest request)
    {
        return SendAsync<TiaCrossReferenceResult>(RequireActiveVersion(), "GetCrossReferences", request);
    }

    public Task<TiaCrossReferenceBatchResult> GetCrossReferencesBatchAsync(TiaCrossReferenceBatchRequest request)
    {
        return SendAsync<TiaCrossReferenceBatchResult>(RequireActiveVersion(), "GetCrossReferencesBatch", request);
    }

    public Task<TiaCrossReferenceOpenResult> OpenCrossReferenceAsync(TiaCrossReferenceOpenRequest request)
    {
        return SendAsync<TiaCrossReferenceOpenResult>(RequireActiveVersion(), "OpenCrossReference", request);
    }

    public Task<TiaPlcControlResult> ExecutePlcControlAsync(TiaPlcControlRequest request)
    {
        return SendAsync<TiaPlcControlResult>(RequireActiveVersion(), "ExecutePlcControl", request);
    }

    public Task<TiaProjectTextsImportResult> ImportProjectTextsAsync(string workbookPath)
    {
        return SendAsync<TiaProjectTextsImportResult>(RequireActiveVersion(), "ImportProjectTexts", new
        {
            WorkbookPath = workbookPath
        });
    }

    public Task<TiaUnifiedScreensResult> GetUnifiedScreensAsync(string deviceName)
    {
        return SendAsync<TiaUnifiedScreensResult>(RequireActiveVersion(), "GetUnifiedScreens", new
        {
            DeviceName = deviceName ?? ""
        });
    }

    public Task<TiaTextListExportResult> ExportTextListsAsync(string outputFolder, string deviceName)
    {
        return SendAsync<TiaTextListExportResult>(RequireActiveVersion(), "ExportTextLists", new
        {
            OutputFolder = outputFolder,
            DeviceName = deviceName ?? ""
        });
    }

    public Task<TiaTextListImportResult> ImportTextListsAsync(string inputFolder, string deviceName)
    {
        return SendAsync<TiaTextListImportResult>(RequireActiveVersion(), "ImportTextLists", new
        {
            InputFolder = inputFolder,
            DeviceName = deviceName ?? ""
        });
    }

    public Task<TiaUnifiedDevicesResult> GetUnifiedDevicesAsync()
    {
        return SendAsync<TiaUnifiedDevicesResult>(RequireActiveVersion(), "GetUnifiedDevices", null);
    }

    public Task<TiaUnifiedScriptResult> ExportUnifiedScriptAsync(string outputFolder, string deviceName, string moduleName)
    {
        return SendAsync<TiaUnifiedScriptResult>(RequireActiveVersion(), "ExportUnifiedScript", new
        {
            OutputFolder = outputFolder,
            DeviceName = deviceName ?? "",
            ModuleName = moduleName
        });
    }

    public Task<TiaUnifiedScriptResult> ImportUnifiedScriptAsync(string inputFolder, string deviceName)
    {
        return SendAsync<TiaUnifiedScriptResult>(RequireActiveVersion(), "ImportUnifiedScript", new
        {
            InputFolder = inputFolder,
            DeviceName = deviceName ?? ""
        });
    }

    public void AbortHelper()
    {
        CurrentSession = null;
        _activeVersion = null;
        foreach (var helper in _helpers.Values)
        {
            TryKill(helper);
        }

        _helpers.Clear();
    }

    private string RequireActiveVersion()
    {
        return _activeVersion
               ?? throw new InvalidOperationException("Attach to TIA Portal first.");
    }

    private static string NormalizeVersion(string version)
    {
        var value = (version ?? "").Trim();
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Defensive fallback when a caller did not carry the process version.
        return TiaApiLocator.GetInstalledVersions().FirstOrDefault() ?? "";
    }

    private async Task<T> SendAsync<T>(string version, string command, object? payload)
    {
        await _gate.WaitAsync();
        try
        {
            var helper = EnsureHelperProcess(version);
            var request = new HelperRequest
            {
                Id = ++_nextRequestId,
                Command = command,
                Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload)
            };

            var line = JsonSerializer.Serialize(request);
            await helper.StandardInput.WriteLineAsync(line);
            await helper.StandardInput.FlushAsync();

            var readTask = helper.StandardOutput.ReadLineAsync();
            var completedTask = await Task.WhenAny(readTask, Task.Delay(HelperResponseTimeout));
            if (completedTask != readTask)
            {
                KillHelper(version);
                throw new TimeoutException($"TIA helper ({version}) did not respond within {HelperResponseTimeout.TotalMinutes:0} minute(s). Attach to TIA Portal again before retrying.");
            }

            var responseLine = await readTask;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                var error = await helper.StandardError.ReadToEndAsync();
                KillHelper(version);
                throw new InvalidOperationException("TIA helper stopped unexpectedly." + FormatHelperError(error));
            }

            var response = JsonSerializer.Deserialize<HelperResponse>(responseLine)
                           ?? throw new InvalidOperationException("TIA helper returned an invalid response.");
            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? "TIA helper command failed.");
            }

            if (typeof(T) == typeof(object))
            {
                return (T)new object();
            }

            if (response.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                throw new InvalidOperationException("TIA helper returned an empty response.");
            }

            return response.Payload.Deserialize<T>()
                   ?? throw new InvalidOperationException("TIA helper returned an invalid payload.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process EnsureHelperProcess(string version)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TiaSessionService));
        }

        if (_helpers.TryGetValue(version, out var existing))
        {
            if (!existing.HasExited)
            {
                return existing;
            }

            TryKill(existing);
            _helpers.Remove(version);
        }

        var helperPath = ResolveHelperPath(version);
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["CWC_TIA_PORTAL_VERSION"] = version;

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Could not start the TIA helper process for {version}.");
        _helpers[version] = process;
        return process;
    }

    private static string ResolveHelperPath(string version)
    {
        var baseDir = AppContext.BaseDirectory;
        var devProject = version.Equals("V21", StringComparison.OrdinalIgnoreCase)
            ? "CwcConfigurator.TiaHelper.V21"
            : "CwcConfigurator.TiaHelper";
        var candidates = new[]
        {
            Path.Combine(baseDir, "TiaHelper", version, "CwcConfigurator.TiaHelper.exe"),
            Path.Combine(baseDir, "TiaHelper", version, "win-x64", "CwcConfigurator.TiaHelper.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", devProject, "bin", "Debug", "net48", "CwcConfigurator.TiaHelper.exe"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"TIA helper executable for {version} was not found.", candidates[0]);
    }

    private void KillHelper(string version)
    {
        if (_helpers.TryGetValue(version, out var helper))
        {
            TryKill(helper);
            _helpers.Remove(version);
        }

        if (string.Equals(_activeVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            _activeVersion = null;
            CurrentSession = null;
        }
    }

    private static void TryKill(Process? helper)
    {
        try
        {
            if (helper is { HasExited: false })
            {
                helper.Kill();
            }
        }
        catch
        {
        }

        try
        {
            helper?.Dispose();
        }
        catch
        {
        }
    }

    private static string FormatHelperError(string error)
    {
        return string.IsNullOrWhiteSpace(error) ? "" : Environment.NewLine + error.Trim();
    }

    public void Dispose()
    {
        _disposed = true;
        CurrentSession = null;
        _activeVersion = null;
        foreach (var helper in _helpers.Values)
        {
            TryKill(helper);
        }

        _helpers.Clear();
        _gate.Dispose();
    }

    private sealed class TiaProcessListResponse
    {
        public List<TiaPortalProcessInfo> Processes { get; set; } = [];
    }

    private sealed class HelperRequest
    {
        public int Id { get; set; }
        public string Command { get; set; } = "";
        public JsonElement? Payload { get; set; }
    }

    private sealed class HelperResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public JsonElement Payload { get; set; }
    }
}
