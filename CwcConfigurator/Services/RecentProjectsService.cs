using System.IO;
using System.Text.Json;

namespace CwcConfigurator.Services;

public sealed class RecentProjectsService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlantConfigurator",
        "recent-projects.json");

    public IReadOnlyList<RecentProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(_path))?
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Add(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var list = Load()
            .Where(item => !item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(new RecentProjectEntry(Path.GetFileNameWithoutExtension(fullPath), fullPath, DateTime.Now))
            .Take(12)
            .ToList();
        Save(list);
    }

    public void Remove(string path)
    {
        Save(Load().Where(item => !item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private void Save(IReadOnlyList<RecentProjectEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record RecentProjectEntry(string Name, string Path, DateTime LastOpened);
