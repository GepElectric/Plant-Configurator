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
