using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class UpdateAndBugReportTests
{
    [Theory]
    [InlineData("[mandatory]\n\nFixes", true)]
    [InlineData("Fixes DB ALM [MANDATORY]", true)]
    [InlineData("Fixes DB ALM", false)]
    [InlineData(null, false)]
    public void MandatoryMarkerInReleaseNotes(string? notes, bool mandatory)
    {
        Assert.Equal(mandatory, AppUpdateService.IsMandatory(notes));
    }

    [Fact]
    public void CleanNotes_RemovesMarker()
    {
        Assert.Equal("Fixes", AppUpdateService.CleanNotes("[mandatory]\n\nFixes"));
    }

    [Fact]
    public async Task Submit_UploadsAttachmentsThenCreatesIssueWithLinks()
    {
        var handler = new FakeGitHub();
        var service = new BugReportService("test-token", handler);
        var report = CreateReport();

        var result = await service.SubmitAsync(report);

        Assert.Equal(12, result.IssueNumber);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-token", request.Authorization));
        Assert.StartsWith("PUT repos/GepElectric/Plant-Configurator-Bugs/contents/reports/", handler.Requests[0].Line);
        Assert.EndsWith("_Branimir/screenshot.png", handler.Requests[0].Line);
        Assert.EndsWith("_Branimir/project.plantcfg", handler.Requests[1].Line);
        Assert.Equal("POST repos/GepElectric/Plant-Configurator-Bugs/issues", handler.Requests[2].Line);

        var issue = JsonNode.Parse(handler.Requests[2].Body)!;
        Assert.Equal("Filter resets", issue["title"]!.GetValue<string>());
        var body = issue["body"]!.GetValue<string>();
        Assert.Contains("![screenshot.png](https://raw.example/screenshot.png)", body);
        Assert.Contains("- [project.plantcfg](https://raw.example/project.plantcfg)", body);
        Assert.Contains("| App version | 1.2.3 |", body);
        Assert.Contains("| Reported by | Branimir |", body);
    }

    [Fact]
    public async Task Submit_WithoutToken_Throws()
    {
        var service = new BugReportService(null, new FakeGitHub());
        Assert.False(service.CanSubmit);
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => service.SubmitAsync(CreateReport()));
    }

    [Fact]
    public void SaveZip_ContainsReportAndAttachments()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bugzip_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var path = BugReportService.SaveZip(CreateReport(), folder);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(new[] { "project.plantcfg", "report.md", "screenshot.png" }, zip.Entries.Select(entry => entry.FullName).OrderBy(name => name));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void NewIssueUrl_IsEscaped()
    {
        var url = BugReportService.BuildNewIssueUrl(CreateReport());
        Assert.StartsWith("https://github.com/GepElectric/Plant-Configurator-Bugs/issues/new?labels=bug&title=Filter%20resets&body=", url);
    }

    private static BugReport CreateReport()
    {
        var report = new BugReport { Title = "Filter resets", Description = "After two edits the filter resets.", Reporter = "Branimir" };
        report.Environment.Add(new KeyValuePair<string, string>("App version", "1.2.3"));
        report.Attachments.Add(new BugReportAttachment("screenshot.png", [1, 2, 3]));
        report.Attachments.Add(new BugReportAttachment("project.plantcfg", Encoding.UTF8.GetBytes("{}")));
        return report;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(string Line, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add(($"{request.Method} {path}", body, request.Headers.Authorization?.ToString()));
            var json = request.Method == HttpMethod.Put
                ? $"{{\"content\":{{\"download_url\":\"https://raw.example/{Path.GetFileName(path)}\"}}}}"
                : "{\"number\":12,\"html_url\":\"https://github.com/GepElectric/Plant-Configurator-Bugs/issues/12\"}";
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
