using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaTraceStudio;

public static class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidDataException("Prazna datoteka.");
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
    public static void Save<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, Serialize(value)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static StudioProject Load(string path)
    {
        var project = Deserialize<StudioProject>(File.ReadAllText(path));
        if (project.Version != 1) throw new InvalidDataException("Nepodržana verzija projekta.");
        PlotService.Validate(project); return project;
    }
    public static StudioPreset Preset(StudioProject p) => new()
    {
        Options = Clone(p.Options),
        Signals = p.Data.Signals.Select(s => new SignalPreset(s.Source, s.Name, s.Color, s.Unit, s.Visible, s.Factor, s.Offset, s.Width, s.Panel, s.Axis, s.Style, s.PercentMin, s.PercentMax)).ToList()
    };
    public static int ApplyPreset(StudioProject project, StudioPreset preset)
    {
        double start = project.Options.Start, end = project.Options.End;
        project.Options = Clone(preset.Options); project.Options.Start = start; project.Options.End = end;
        project.Options.Zero = 0; project.Options.Cursors = false;
        int count = 0;
        foreach (var signal in project.Data.Signals)
        {
            var s = preset.Signals.FirstOrDefault(s => s.Source == signal.Source);
            if (s == null) continue;
            signal.Name = s.Name; signal.Color = s.Color; signal.Unit = s.Unit; signal.Visible = s.Visible;
            signal.Factor = s.Factor; signal.Offset = s.Offset; signal.Width = s.Width; signal.Panel = s.Panel; signal.Axis = s.Axis; signal.Style = s.Style; count++;
            signal.PercentMin = s.PercentMin; signal.PercentMax = s.PercentMax;
        }
        return count;
    }
}
