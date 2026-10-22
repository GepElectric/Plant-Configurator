using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CwcConfigurator.Services;

public sealed record BugReportAttachment(string FileName, byte[] Content);

public sealed class BugReport
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Reporter { get; set; } = "";
    public List<KeyValuePair<string, string>> Environment { get; } = [];
    public List<BugReportAttachment> Attachments { get; } = [];
}

public sealed record BugReportResult(int IssueNumber, string IssueUrl);

/// <summary>
/// Sends bug reports from Help → Report a bug as GitHub issues. Attachments are committed to the
/// bug repository (reports/&lt;date&gt;-&lt;user&gt;/...) and linked from the issue, because the GitHub API
/// cannot attach files to an issue. The token only needs Issues and Contents access to that repository;
/// it is kept out of the main repository so it can never change the source code or the releases.
/// </summary>
public sealed class BugReportService
{
    public const string Owner = "GepElectric";
    public const string Repository = "Plant-Configurator-Bugs";
    public const string RepositoryUrl = "https://github.com/" + Owner + "/" + Repository;
    public const string TokenFileName = "bugreport.token";
    public const string TokenEnvironmentVariable = "PLANTCONFIGURATOR_BUGREPORT_TOKEN";
    public const long MaxAttachmentBytes = 50L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    private readonly HttpClient _http;
    private readonly string? _token;

    public BugReportService(string? token, HttpMessageHandler? handler = null)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://api.github.com/");
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlantConfigurator", AppUpdateService.AssemblyVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    public bool CanSubmit => _token is not null;

    /// <summary>Token from the environment variable, else from bugreport.token next to the exe.</summary>
    public static string? LoadToken(string? baseDirectory = null)
    {
        var fromEnvironment = System.Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, TokenFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<BugReportResult> SubmitAsync(BugReport report, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_token is null)
        {
            throw new InvalidOperationException("Bug reporting is not configured (no GitHub token).");
        }

        var folder = BuildFolderName(report, DateTime.Now);
        var links = new List<(string FileName, string Url)>();
        foreach (var attachment in WithUniqueNames(report.Attachments))
        {
            progress?.Report($"Uploading {attachment.FileName}...");
            var url = await UploadAsync($"{folder}/{attachment.FileName}", attachment.Content, report.Title, cancellationToken).ConfigureAwait(false);
            links.Add((attachment.FileName, url));
        }

        progress?.Report("Creating issue...");
        var payload = new JsonObject
        {
            ["title"] = report.Title.Trim(),
            ["body"] = BuildIssueBody(report, links),
            ["labels"] = new JsonArray("bug")
        };
        using var response = await _http.PostAsync($"repos/{Owner}/{Repository}/issues", JsonContent(payload), cancellationToken).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, "create issue", cancellationToken).ConfigureAwait(false);
        return new BugReportResult(json["number"]?.GetValue<int>() ?? 0, json["html_url"]?.GetValue<string>() ?? RepositoryUrl + "/issues");
    }

    public static string BuildIssueBody(BugReport report, IReadOnlyList<(string FileName, string Url)> links)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Description").AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(report.Description) ? "_(no description)_" : report.Description.Trim()).AppendLine();
        builder.AppendLine("## Environment").AppendLine();
        builder.AppendLine("| | |").AppendLine("|---|---|");
        if (!string.IsNullOrWhiteSpace(report.Reporter))
        {
            builder.AppendLine($"| Reported by | {EscapeCell(report.Reporter)} |");
        }

        foreach (var (key, value) in report.Environment)
        {
            builder.AppendLine($"| {EscapeCell(key)} | {EscapeCell(value)} |");
        }

        if (links.Count > 0)
        {
            builder.AppendLine().AppendLine("## Attachments").AppendLine();
            foreach (var (fileName, url) in links.Where(link => IsImage(link.FileName)))
            {
                builder.AppendLine($"![{fileName}]({url})").AppendLine();
            }

            foreach (var (fileName, url) in links)
            {
                builder.AppendLine($"- [{fileName}]({url})");
            }
        }

        var body = builder.ToString();
        // GitHub rejects issue bodies above 65536 characters.
        return body.Length <= 60000 ? body : body[..60000] + System.Environment.NewLine + "_(truncated)_";
    }

    /// <summary>Fallback when sending is not possible: everything in one zip the user can send by hand.</summary>
    public static string SaveZip(BugReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"PlantConfigurator_bug_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var attachments = WithUniqueNames(report.Attachments).ToList();
        var markdown = $"# {report.Title}{System.Environment.NewLine}{System.Environment.NewLine}" +
                       BuildIssueBody(report, attachments.Select(item => (item.FileName, item.FileName)).ToList());
        using (var writer = new StreamWriter(zip.CreateEntry("report.md").Open(), new UTF8Encoding(false)))
        {
            writer.Write(markdown);
        }

        foreach (var attachment in attachments)
        {
            using var stream = zip.CreateEntry(attachment.FileName).Open();
            stream.Write(attachment.Content);
        }

        return path;
    }

    /// <summary>Pre-filled "new issue" page for the browser fallback (text only; files are dragged in by hand).</summary>
    public static string BuildNewIssueUrl(BugReport report)
    {
        var body = BuildIssueBody(report, []);
        if (body.Length > 5000)
        {
            body = body[..5000] + System.Environment.NewLine + "_(truncated)_";
        }

        return $"{RepositoryUrl}/issues/new?labels=bug&title={Uri.EscapeDataString(report.Title)}&body={Uri.EscapeDataString(body)}";
    }

    public static string BuildFolderName(BugReport report, DateTime timestamp)
    {
        var who = SafeName(string.IsNullOrWhiteSpace(report.Reporter) ? "user" : report.Reporter);
        return $"reports/{timestamp:yyyy-MM-dd_HHmmss}_{who}";
    }

    public static string SafeName(string value)
    {
        var chars = value.Trim().Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray();
        var name = new string(chars).Trim('_', '.');
        return string.IsNullOrEmpty(name) ? "file" : name.Length > 80 ? name[..80] : name;
    }

    private static IEnumerable<BugReportAttachment> WithUniqueNames(IEnumerable<BugReportAttachment> attachments)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in attachments)
        {
            var name = SafeName(attachment.FileName);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var index = 2; !used.Add(name); index++)
            {
                name = $"{stem}_{index}{extension}";
            }

            yield return attachment with { FileName = name };
        }
    }

    private async Task<string> UploadAsync(string path, byte[] content, string title, CancellationToken cancellationToken)
    {
        var escapedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var payload = new JsonObject
        {
            ["message"] = $"Bug report attachment: {title}".Trim(),
            ["content"] = Convert.ToBase64String(content)
        };
        using var response = await _http.PutAsync($"repos/{Owner}/{Repository}/contents/{escapedPath}", JsonContent(payload), cancellationToken).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, "upload " + Path.GetFileName(path), cancellationToken).ConfigureAwait(false);
        return json["content"]?["download_url"]?.GetValue<string>()
               ?? json["content"]?["html_url"]?.GetValue<string>()
               ?? $"{RepositoryUrl}/blob/main/{escapedPath}";
    }

    private static StringContent JsonContent(JsonNode payload)
    {
        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException)
            {
            }

            throw new HttpRequestException($"GitHub could not {action}: {(int)response.StatusCode} {message}");
        }

        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static bool IsImage(string fileName)
    {
        return ImageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
    }

    private static string EscapeCell(string value)
    {
        return value.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
    }
}
