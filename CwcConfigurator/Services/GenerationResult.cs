namespace CwcConfigurator.Services;

public sealed class GenerationResult
{
    public List<string> CreatedFiles { get; } = [];
    public List<ProjectIssue> Issues { get; } = [];

    public bool Success => Issues.All(issue => issue.Severity != "ERROR");

    public void AddFile(string path)
    {
        CreatedFiles.Add(path);
    }

    public void AddIssue(ProjectIssue issue)
    {
        Issues.Add(issue);
    }
}
