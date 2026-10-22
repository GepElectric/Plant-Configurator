using System.IO;
using System.Text.Json;

namespace CwcConfigurator.Services.Simulation;

public sealed class SimulatorConnectionSettings
{
    public string IpAddress { get; set; } = "";
    public short Rack { get; set; }
    public short Slot { get; set; } = 1;
    public int DbNumber { get; set; } = SimulatorLayout.DefaultDbNumber;
    /// <summary>PLC constants of the simulator DB layout (<see cref="SimulatorLayout.ConstantNames"/>); null = proposal.</summary>
    public int[]? Constants { get; set; }
    public int DevicesDbNumber { get; set; } = DeviceLayout.DefaultDbNumber;
    /// <summary>Devices in the devices DB, in DB order; null = the standard unit list.</summary>
    public List<DeviceDefinition>? Devices { get; set; }
}

/// <summary>Connection settings per project + PLC database, kept in %LocalAppData%\PlantConfigurator\simulator.json.</summary>
public static class SimulatorSettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantConfigurator", "simulator.json");

    public static SimulatorConnectionSettings Load(string key)
    {
        var all = ReadAll();
        return all.TryGetValue(key, out var settings) ? settings : new SimulatorConnectionSettings();
    }

    public static void Save(string key, SimulatorConnectionSettings settings)
    {
        try
        {
            var all = ReadAll();
            all[key] = settings;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ModelsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantConfigurator", "simulator-models.json");

    private const string LastProjectKey = "#last-project";

    /// <summary>Project opened last in the Plant Simulator (null = none).</summary>
    public static string? LoadLastProject() => LoadModelPath(LastProjectKey);

    public static void SaveLastProject(string projectPath) => SaveModelPath(LastProjectKey, projectPath);

    /// <summary>The model file last used with a project (null = none remembered).</summary>
    public static string? LoadModelPath(string projectKey)
    {
        try
        {
            var all = File.Exists(ModelsPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ModelsPath)) : null;
            return all is not null && all.TryGetValue(projectKey, out var path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SaveModelPath(string projectKey, string path)
    {
        try
        {
            var all = File.Exists(ModelsPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ModelsPath)) ?? [] : [];
            all[projectKey] = path;
            Directory.CreateDirectory(Path.GetDirectoryName(ModelsPath)!);
            File.WriteAllText(ModelsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, SimulatorConnectionSettings> ReadAll()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, SimulatorConnectionSettings>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
