using System.IO;
using System.Text.Json;

namespace CwcConfigurator.Services;

/// <summary>
/// Persists window placement and layout preferences between sessions
/// (same %AppData% folder as the recent-projects list).
/// </summary>
public sealed class UiStateService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlantConfigurator",
        "ui-state.json");

    public UiState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new UiState();
            }

            return JsonSerializer.Deserialize<UiState>(File.ReadAllText(_path)) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    public void Save(UiState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing UI-state persistence must never break app shutdown.
        }
    }
}

public sealed class UiState
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double PlcPaneWidth { get; set; }
}
