using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CwcConfigurator.Services.AlarmTesting;

public sealed class PlcWebApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private HttpClient _client;
    private string? _token;
    private int _requestId;

    public PlcWebApiClient()
    {
        _client = CreateClient();
    }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_token);

    public Task LoginAsync(string baseAddress, string user, string password, CancellationToken cancellationToken)
    {
        _client.Dispose();
        _client = CreateClient();
        _client.BaseAddress = new Uri(NormalizeBaseAddress(baseAddress));
        _token = null;
        return LoginCoreAsync(user, password, cancellationToken);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    private async Task LoginCoreAsync(string user, string password, CancellationToken cancellationToken)
    {
        var response = await SendAsync<LoginResult>("Api.Login", new { user, password }, cancellationToken);
        _token = response.Token;
    }

    public async Task<List<PlcAlarmEvent>> BrowseAlarmsAsync(int count, string language, CancellationToken cancellationToken)
    {
        var response = await SendAsync<AlarmBrowseResult>(
            "Alarms.Browse",
            new { count, language },
            cancellationToken);

        return response.Entries.Select(item => new PlcAlarmEvent
        {
            Id = item.Id ?? "",
            AlarmNumber = item.AlarmNumber,
            Status = item.Status ?? "",
            Timestamp = ParseTimestamp(item.Timestamp),
            Producer = item.Producer ?? "",
            AcknowledgementState = item.Acknowledgement?.State ?? "",
            AlarmText = item.AlarmText ?? "",
            InfoText = item.InfoText ?? ""
        }).ToList();
    }

    public async Task AcknowledgeAsync(IEnumerable<string> alarmIds, CancellationToken cancellationToken)
    {
        var ids = alarmIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await SendAsync<JsonElement>("Alarms.Acknowledge", new { ids }, cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task<T> SendAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var request = new JsonRpcRequest(++_requestId, method, parameters);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/jsonrpc")
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(_token))
        {
            message.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        }

        using var response = await _client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var envelope = await JsonSerializer.DeserializeAsync<JsonRpcResponse<T>>(stream, JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("PLC Web API returned an empty response.");

        if (envelope.Error is not null)
        {
            throw new InvalidOperationException($"{method}: {envelope.Error.Message} ({envelope.Error.Code})");
        }

        return envelope.Result;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeBaseAddress(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private sealed record JsonRpcRequest(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Params)
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc => "2.0";
    }

    private sealed class JsonRpcResponse<T>
    {
        [JsonPropertyName("result")]
        public T Result { get; init; } = default!;

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; init; }
    }

    private sealed class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = "";
    }

    private sealed class LoginResult
    {
        [JsonPropertyName("token")]
        public string Token { get; init; } = "";
    }

    private sealed class AlarmBrowseResult
    {
        [JsonPropertyName("entries")]
        public List<AlarmEntry> Entries { get; init; } = [];
    }

    private sealed class AlarmEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("alarm_number")]
        public int AlarmNumber { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; init; }

        [JsonPropertyName("producer")]
        public string? Producer { get; init; }

        [JsonPropertyName("acknowledgement")]
        public AlarmAcknowledgement? Acknowledgement { get; init; }

        [JsonPropertyName("alarm_text")]
        public string? AlarmText { get; init; }

        [JsonPropertyName("info_text")]
        public string? InfoText { get; init; }
    }

    private sealed class AlarmAcknowledgement
    {
        [JsonPropertyName("state")]
        public string? State { get; init; }
    }
}
