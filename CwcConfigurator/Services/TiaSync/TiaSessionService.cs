using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaSessionService : ITiaSessionService, IDisposable
{
    private static readonly TimeSpan HelperResponseTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _helperProcess;
    private int _nextRequestId;
    private bool _disposed;

    public TiaAttachedSession? CurrentSession { get; private set; }

    public async Task<IList<TiaPortalProcessInfo>> GetRunningPortalProcessesAsync()
    {
        var response = await SendAsync<TiaProcessListResponse>("GetProcesses", null);
        return response.Processes;
    }

    public async Task<TiaAttachedSession> AttachToRunningPortalAsync(int processId)
    {
        var response = await SendAsync<TiaAttachedSession>("Attach", new { ProcessId = processId });
        CurrentSession = response;
        return response;
    }

    public async Task DisconnectAsync()
    {
        if (_helperProcess is null || _helperProcess.HasExited)
        {
            CurrentSession = null;
            return;
        }

        await SendAsync<object>("Disconnect", null);
        CurrentSession = null;
    }

    public Task<TiaSourceTransferResult> ExportSourcesAsync(IEnumerable<TiaPlcSourceExportRequest> plcs, string outputFolder)
    {
        return SendAsync<TiaSourceTransferResult>("ExportSources", new
        {
            OutputFolder = outputFolder,
            Plcs = plcs.ToList()
        });
    }

    public Task<TiaSourceTransferResult> ImportSourcesAsync(IEnumerable<TiaPlcSourceImportRequest> plcs)
    {
        return SendAsync<TiaSourceTransferResult>("ImportSources", new
        {
            Plcs = plcs.ToList()
        });
    }

    public Task<TiaCrossReferenceResult> GetCrossReferencesAsync(TiaCrossReferenceRequest request)
    {
        return SendAsync<TiaCrossReferenceResult>("GetCrossReferences", request);
    }

    public Task<TiaCrossReferenceBatchResult> GetCrossReferencesBatchAsync(TiaCrossReferenceBatchRequest request)
    {
        return SendAsync<TiaCrossReferenceBatchResult>("GetCrossReferencesBatch", request);
    }

    public Task<TiaCrossReferenceOpenResult> OpenCrossReferenceAsync(TiaCrossReferenceOpenRequest request)
    {
        return SendAsync<TiaCrossReferenceOpenResult>("OpenCrossReference", request);
    }

    public Task<TiaPlcControlResult> ExecutePlcControlAsync(TiaPlcControlRequest request)
    {
        return SendAsync<TiaPlcControlResult>("ExecutePlcControl", request);
    }

    public Task<TiaProjectTextsImportResult> ImportProjectTextsAsync(string workbookPath)
    {
        return SendAsync<TiaProjectTextsImportResult>("ImportProjectTexts", new
        {
            WorkbookPath = workbookPath
        });
    }

    public void AbortHelper()
    {
        CurrentSession = null;
        try
        {
            if (_helperProcess is { HasExited: false })
            {
                _helperProcess.Kill();
            }
        }
        catch
        {
        }

        try
        {
            _helperProcess?.Dispose();
        }
        catch
        {
        }

        _helperProcess = null;
    }

    private async Task<T> SendAsync<T>(string command, object? payload)
    {
        await _gate.WaitAsync();
        try
        {
            var helper = EnsureHelperProcess();
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
                AbortHelper();
                throw new TimeoutException($"TIA helper did not respond within {HelperResponseTimeout.TotalMinutes:0} minute(s). Attach to TIA Portal again before retrying.");
            }

            var responseLine = await readTask;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                var error = await helper.StandardError.ReadToEndAsync();
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

    private Process EnsureHelperProcess()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TiaSessionService));
        }

        if (_helperProcess is { HasExited: false })
        {
            return _helperProcess;
        }

        var helperPath = ResolveHelperPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["CWC_TIA_PORTAL_VERSION"] = TiaApiLocator.GetPreferredVersionLabel();

        _helperProcess = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("Could not start the TIA helper process.");
        return _helperProcess;
    }

    private static string ResolveHelperPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "TiaHelper", "CwcConfigurator.TiaHelper.exe"),
            Path.Combine(baseDir, "TiaHelper", "win-x64", "CwcConfigurator.TiaHelper.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "CwcConfigurator.TiaHelper", "bin", "Debug", "net48", "CwcConfigurator.TiaHelper.exe"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("TIA helper executable was not found.", candidates[0]);
    }

    private static string FormatHelperError(string error)
    {
        return string.IsNullOrWhiteSpace(error) ? "" : Environment.NewLine + error.Trim();
    }

    public void Dispose()
    {
        _disposed = true;
        CurrentSession = null;
        try
        {
            if (_helperProcess is { HasExited: false })
            {
                _helperProcess.Kill();
            }
        }
        catch
        {
        }

        _helperProcess?.Dispose();
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
