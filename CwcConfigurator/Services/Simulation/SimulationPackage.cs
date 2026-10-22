using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// A self-contained simulation (".plantsim", a zip): a copy of the Plant Configurator project (the database with all
/// signals, scaling and devices), the plant model and the PLC connection settings. Opening it needs nothing else,
/// so a simulation can be continued on another PC or by someone else.
/// </summary>
public static class SimulationPackage
{
    public const string Extension = ".plantsim";
    private const string ProjectEntry = "project.plantcfg";
    private const string ModelEntry = "model.simulator.yaml";
    private const string ConnectionsEntry = "connections.json";
    private const string InfoEntry = "package.json";

    public sealed class Content
    {
        public required byte[] Project { get; init; }
        public required Dictionary<string, ProcessModel> Models { get; init; }
        public required Dictionary<string, SimulatorConnectionSettings> Connections { get; init; }
        public string ProjectName { get; init; } = "";
        public string SourceProject { get; init; } = "";
        public string Saved { get; init; } = "";
    }

    public static bool IsPackage(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>"&lt;project&gt;.plantsim" next to a .plantcfg.</summary>
    public static string PathFor(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath) ?? "", Path.GetFileNameWithoutExtension(projectPath) + Extension);

    public static void Save(string path, Content content)
    {
        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Write(zip, ProjectEntry, content.Project);
            Write(zip, ModelEntry, Encoding.UTF8.GetBytes(ProcessModelStore.ToYaml(content.Models)));
            Write(zip, ConnectionsEntry, JsonSerializer.SerializeToUtf8Bytes(content.Connections, new JsonSerializerOptions { WriteIndented = true }));
            Write(zip, InfoEntry, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Format = 1,
                content.ProjectName,
                content.SourceProject,
                Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Application = "Plant Simulator " + AppUpdateService.AssemblyVersion
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        File.Move(temp, path, overwrite: true);
    }

    public static Content Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var project = Read(zip, ProjectEntry) ?? throw new InvalidDataException($"{Path.GetFileName(path)} does not contain a project.");
        var model = Read(zip, ModelEntry);
        var connections = Read(zip, ConnectionsEntry);
        var info = Read(zip, InfoEntry);
        var infoJson = info is null ? default : JsonDocument.Parse(info).RootElement;
        return new Content
        {
            Project = project,
            Models = model is null ? new(StringComparer.OrdinalIgnoreCase) : ProcessModelStore.FromYaml(Encoding.UTF8.GetString(model)),
            Connections = new Dictionary<string, SimulatorConnectionSettings>(
                connections is null ? [] : JsonSerializer.Deserialize<Dictionary<string, SimulatorConnectionSettings>>(connections) ?? [],
                StringComparer.OrdinalIgnoreCase),
            ProjectName = Text(infoJson, "ProjectName"),
            SourceProject = Text(infoJson, "SourceProject"),
            Saved = Text(infoJson, "Saved")
        };
    }

    /// <summary>Writes the embedded project to a temporary .plantcfg so the normal project loader can read it.</summary>
    public static string ExtractProject(byte[] project, string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PlantSimulator");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}_{Guid.NewGuid():N}.plantcfg");
        File.WriteAllBytes(path, project);
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(data);
    }

    private static byte[]? Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
