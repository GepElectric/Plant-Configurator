using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CwcConfigurator.Services.AlarmTesting;

/// <summary>
/// Reads live alarms from a local WinCC Unified runtime via Open Pipe
/// (<c>\\.\pipe\HmiRuntime</c>, "expert" JSON syntax).
///
/// This is the same alarm stream the SCADA operator sees: the runtime has
/// already decoded the PLC <c>Program_Alarm</c> messages off the S7+ channel,
/// so we get fully resolved alarms (text, state, time) without touching the
/// encrypted wire protocol or an OPC UA licence.
///
/// Protocol (from the Unified Openness OpenPipe SDK samples):
///   subscribe : {"Message":"SubscribeAlarm","Params":{"SystemNames":[..],"Filter":"","LanguageId":1033},"ClientCookie":".."}
///   push      : {"Message":"NotifySubscribeAlarm","Params":{"Alarms":[ {..fields..} ]},"ClientCookie":".."}
///   one-shot  : {"Message":"ReadAlarm",...}  -> {"Message":"NotifyReadAlarm","Params":{"Alarms":[..]}}
///
/// Messages are newline-delimited UTF-8 JSON. The pipe is full-duplex, so the
/// reader loop runs while we send subscribe/unsubscribe from the UI thread.
/// </summary>
public sealed class AlarmTesterOpenPipeClient : IDisposable
{
    public const string DefaultPipeName = "HmiRuntime";
    public const string DefaultSystemName = "RUNTIME_1";
    public const int LanguageEnUs = 1033;
    public const int LanguageHrHr = 1050;

    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Alarms pushed by the runtime, each stamped with local receive time so the
    // dialog can pick out only what arrived after a signal was toggled.
    private readonly ConcurrentQueue<PlcAlarmEvent> _buffer = new();

    // Debug aid: every alarm the runtime pushes is appended here as raw JSON so
    // we can confirm exactly which field carries the number / text / area.
    private static readonly string RawLogPath = Path.Combine(
        Path.GetTempPath(), "PlantConfigurator_openpipe_alarms.log");

    // One-shot requests (WriteTag, BrowseTags) awaiting their response by cookie.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private string _subscribeCookie = "";
    private string[] _systemNames = [];
    private volatile bool _subscribed;
    private volatile string _lastError = "";

    public AlarmTesterOpenPipeClient(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe?.IsConnected == true;
    public bool IsSubscribed => _subscribed;
    public string LastError => _lastError;

    /// <summary>Raised on the reader thread for every alarm the runtime pushes.</summary>
    public event Action<PlcAlarmEvent>? AlarmReceived;

    public async Task ConnectAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        Disconnect();

        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutMs, cancellationToken);
        _pipe = pipe;

        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token));
    }

    /// <summary>
    /// Sends a SubscribeAlarm and waits for the first response. The first
    /// NotifySubscribeAlarm carries the currently-active alarms (initial
    /// snapshot); subsequent ones are live changes. Throws on ErrorSubscribeAlarm.
    /// </summary>
    public async Task SubscribeAsync(
        IReadOnlyCollection<string> systemNames,
        string filter,
        int languageId,
        int responseTimeoutMs,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _subscribeCookie = "almSub_" + Guid.NewGuid().ToString("N")[..8];
        _systemNames = systemNames.ToArray();
        _subscribed = false;
        _lastError = "";

        var command = new
        {
            Message = "SubscribeAlarm",
            Params = new
            {
                SystemNames = systemNames,
                Filter = filter ?? "",
                LanguageId = languageId
            },
            ClientCookie = _subscribeCookie
        };

        await SendAsync(command, cancellationToken);

        // Wait until the reader flips the flag (first Notify) or records an error.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeoutMs);
        try
        {
            while (!_subscribed)
            {
                if (!string.IsNullOrEmpty(_lastError))
                {
                    throw new InvalidOperationException($"OpenPipe subscribe rejected: {_lastError}");
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WinCC runtime did not confirm the alarm subscription in time.");
        }
    }

    public async Task UnsubscribeAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || string.IsNullOrEmpty(_subscribeCookie))
        {
            _subscribed = false;
            return;
        }

        var command = new
        {
            Message = "UnsubscribeAlarm",
            ClientCookie = _subscribeCookie
        };

        try
        {
            await SendAsync(command, cancellationToken);
        }
        catch
        {
            // Best-effort; we are tearing down anyway.
        }

        _subscribed = false;
    }

    /// <summary>Writes a single tag value (used to send the alarm-reset command).</summary>
    public async Task WriteTagAsync(string tagName, string value, int responseTimeoutMs, CancellationToken cancellationToken)
    {
        EnsureConnected();

        var cookie = "write_" + Guid.NewGuid().ToString("N")[..8];
        var command = new
        {
            Message = "WriteTag",
            Params = new { Tags = new[] { new { Name = tagName, Value = value } } },
            ClientCookie = cookie
        };

        var response = await SendRequestAsync(command, cookie, responseTimeoutMs, cancellationToken);

        // NotifyWriteTag carries a per-tag ErrorCode; surface a failed write.
        if (response.TryGetProperty("Params", out var paramsProp)
            && paramsProp.TryGetProperty("Tags", out var tags)
            && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.TryGetProperty("ErrorCode", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
                {
                    var description = tag.TryGetProperty("ErrorDescription", out var desc) ? desc.GetString() : null;
                    throw new InvalidOperationException($"WriteTag '{tagName}' failed: {description ?? code.GetInt32().ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }
    }

    /// <summary>Returns tag names matching <paramref name="filter"/> (e.g. "*START_STOP*").</summary>
    public async Task<List<string>> BrowseTagsAsync(string filter, int responseTimeoutMs, CancellationToken cancellationToken)
    {
        EnsureConnected();

        var cookie = "browse_" + Guid.NewGuid().ToString("N")[..8];
        var command = new
        {
            Message = "BrowseTags",
            Params = new
            {
                LanguageId = LanguageEnUs,
                Filter = filter ?? "",
                Attributes = new[] { "Name" },
                PageSize = 1000,
                SystemNames = _systemNames
            },
            ClientCookie = cookie
        };

        var response = await SendRequestAsync(command, cookie, responseTimeoutMs, cancellationToken);

        var names = new List<string>();
        if (response.TryGetProperty("Params", out var paramsProp)
            && paramsProp.TryGetProperty("Tags", out var tags)
            && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var name = GetString(tag, "Name");
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<JsonElement> SendRequestAsync(object command, string cookie, int responseTimeoutMs, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[cookie] = tcs;

        try
        {
            await SendAsync(command, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeoutMs);
            await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WinCC runtime did not answer the OpenPipe request in time.");
        }
        finally
        {
            _pending.TryRemove(cookie, out _);
        }
    }

    /// <summary>Drops every buffered alarm. Call right before toggling a signal.</summary>
    public void ClearBuffer()
    {
        while (_buffer.TryDequeue(out _))
        {
        }
    }

    /// <summary>Returns buffered alarms whose receive time is at/after <paramref name="sinceUtc"/>.</summary>
    public List<PlcAlarmEvent> AlarmsReceivedSince(DateTimeOffset sinceUtc)
    {
        return _buffer
            .Where(alarm => alarm.ReceivedUtc >= sinceUtc)
            .OrderBy(alarm => alarm.ReceivedUtc)
            .ToList();
    }

    public void Disconnect()
    {
        _subscribed = false;

        try
        {
            _readerCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // ignore
        }

        _pipe = null;
        _readerCts = null;
        _readerTask = null;

        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        _pending.Clear();
        ClearBuffer();
    }

    public void Dispose()
    {
        Disconnect();
        _writeLock.Dispose();
    }

    private async Task SendAsync(object command, CancellationToken cancellationToken)
    {
        var pipe = EnsureConnected();
        var json = JsonSerializer.Serialize(command) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(bytes, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            return;
        }

        var readBuffer = new byte[64 * 1024];
        var pending = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await pipe.ReadAsync(readBuffer, cancellationToken);
                if (read <= 0)
                {
                    break; // pipe closed by runtime
                }

                pending.Append(Encoding.UTF8.GetString(readBuffer, 0, read));
                DispatchCompleteLines(pending);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    private void DispatchCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            var text = pending.ToString();
            var newlineIndex = text.IndexOf('\n');
            if (newlineIndex < 0)
            {
                break;
            }

            var line = text[..newlineIndex].Trim();
            pending.Remove(0, newlineIndex + 1);

            if (line.Length == 0)
            {
                continue;
            }

            HandleLine(line);
        }
    }

    private void HandleLine(string line)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // ignore non-JSON noise
        }

        var message = root.TryGetProperty("Message", out var messageProp)
            ? messageProp.GetString() ?? ""
            : "";

        switch (message)
        {
            case "NotifySubscribeAlarm":
            case "NotifyReadAlarm":
                _subscribed = true;
                EmitAlarms(root);
                return;

            case "ErrorSubscribeAlarm":
            case "ErrorReadAlarm":
            case "ErrorUnsubscribeAlarm":
                _lastError = root.TryGetProperty("ErrorDescription", out var desc)
                    ? desc.GetString() ?? message
                    : message;
                return;
        }

        // One-shot request responses (WriteTag / BrowseTags) are matched by cookie.
        var cookie = root.TryGetProperty("ClientCookie", out var cookieProp) ? cookieProp.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(cookie) && _pending.TryRemove(cookie, out var tcs))
        {
            if (message.StartsWith("Error", StringComparison.Ordinal))
            {
                var errorText = root.TryGetProperty("ErrorDescription", out var errDesc) ? errDesc.GetString() ?? message : message;
                tcs.TrySetException(new InvalidOperationException($"OpenPipe: {errorText}"));
            }
            else
            {
                tcs.TrySetResult(root);
            }
        }
    }

    private void EmitAlarms(JsonElement root)
    {
        if (!root.TryGetProperty("Params", out var paramsProp)
            || !paramsProp.TryGetProperty("Alarms", out var alarmsProp)
            || alarmsProp.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var receivedUtc = DateTimeOffset.UtcNow;
        foreach (var alarmElement in alarmsProp.EnumerateArray())
        {
            LogRaw(alarmElement);
            var alarm = MapAlarm(alarmElement, receivedUtc);
            _buffer.Enqueue(alarm);
            AlarmReceived?.Invoke(alarm);
        }
    }

    private static void LogRaw(JsonElement alarmElement)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {alarmElement.GetRawText()}{Environment.NewLine}";
            File.AppendAllText(RawLogPath, line);
        }
        catch
        {
            // diagnostics only — never let logging break the reader
        }
    }

    private static PlcAlarmEvent MapAlarm(JsonElement element, DateTimeOffset receivedUtc)
    {
        var parameters = ReadParameters(element);

        // Layout of this project's central alarm block (verified against the
        // runtime): for program alarms (class *_ALM) AlarmText1 is the message,
        // AlarmText2 the cubicle/system, AlarmText8 the PLC alarm number, AlarmText9
        // the alarm class. EventText also holds the system string (not the message).
        var alarmText = FirstNonEmpty(GetString(element, "AlarmText1"), GetString(element, "EventText"));
        var alarmSystem = FirstNonEmpty(GetString(element, "AlarmText2"), GetString(element, "EventText"));

        var stateText = GetString(element, "StateText");
        var notificationReason = GetString(element, "NotificationReason");
        var status = string.IsNullOrEmpty(stateText) ? notificationReason : stateText;

        var timestamp = ParseTimestamp(GetString(element, "RaiseTime"))
                        ?? ParseTimestamp(GetString(element, "ModificationTime"));

        return new PlcAlarmEvent
        {
            Id = FirstNonEmpty(GetString(element, "InstanceID"), GetString(element, "ID")),
            AlarmNumber = ResolveAlarmNumber(element, parameters),
            Name = GetString(element, "Name"),
            Status = status,
            State = stateText,
            Timestamp = timestamp,
            ReceivedUtc = receivedUtc,
            Producer = FirstNonEmpty(GetString(element, "Origin"), GetString(element, "SourceType")),
            SourceType = GetString(element, "SourceType"),
            Origin = GetString(element, "Origin"),
            AlarmClassName = GetString(element, "AlarmClassName"),
            AlarmSystem = alarmSystem,
            Parameters = parameters,
            AcknowledgementState = GetString(element, "StateMachine"),
            AlarmText = alarmText,
            InfoText = GetString(element, "InfoText"),
            Source = "OpenPipe"
        };
    }

    private static List<string> ReadParameters(JsonElement element)
    {
        if (!element.TryGetProperty("AlarmParameterValues", out var prop)
            || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return prop.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString())
            .ToList();
    }

    private static int ResolveAlarmNumber(JsonElement element, IReadOnlyList<string> parameters)
    {
        // Program-alarm number lives in AlarmText8 for this project; the first
        // alarm parameter is the same value. Fall back to the first integer-like
        // parameter so the match still works if the slot layout differs.
        if (TryParseInt(GetString(element, "AlarmText8"), out var fromSlot))
        {
            return fromSlot;
        }

        foreach (var parameter in parameters)
        {
            if (TryParseInt(parameter, out var fromParam))
            {
                return fromParam;
            }
        }

        return 0;
    }

    private static bool TryParseInt(string value, out int parsed) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return "";
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? "",
            JsonValueKind.Number => prop.ToString(),
            _ => ""
        };
    }

    private static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    private static DateTimeOffset? ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private NamedPipeClientStream EnsureConnected()
    {
        if (_pipe?.IsConnected != true)
        {
            throw new InvalidOperationException("Not connected to the WinCC runtime pipe.");
        }

        return _pipe;
    }
}
