using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TiaTraceStudio;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
    protected void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public class Signal : Observable
{
    bool nameOverride, percentFromProject = true;
    public bool NameOverride { get => nameOverride; set => Set(ref nameOverride, value); }
    /// <summary>0 % / 100 % come from the unit's rated values in the plant definition (default) instead of this graph.</summary>
    public bool PercentFromProject { get => percentFromProject; set => Set(ref percentFromProject, value); }
    public ExtremaLabelOptions MinimumLabel { get; set; } = new();
    public ExtremaLabelOptions MaximumLabel { get; set; } = new();
    public ExtremaLabelOptions ConstantLabel { get; set; } = new();
    public string Source { get; set; } = "";
    public double[] Values { get; set; } = [];
    string name = "", color = "#2563EB", unit = "", axis = "Lijeva", style = "Puna";
    bool visible = true; double factor = 1, offset, width = 2; int panel = 1;
    double? percentMin, percentMax;
    public string Name { get => name; set { Set(ref name, value); Notify(nameof(DisplayName)); } }
    public double? PercentMin { get => percentMin; set => Set(ref percentMin, value); }
    public double? PercentMax { get => percentMax; set => Set(ref percentMax, value); }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Source : Name;
    public string Color { get => color; set => Set(ref color, value); }
    public string Unit { get => unit; set => Set(ref unit, value); }
    public bool Visible { get => visible; set => Set(ref visible, value); }
    public double Factor { get => factor; set => Set(ref factor, value); }
    public double Offset { get => offset; set => Set(ref offset, value); }
    public double Width { get => width; set => Set(ref width, value); }
    public int Panel { get => panel; set => Set(ref panel, value); }
    public string Axis { get => axis; set => Set(ref axis, value); }
    public string Style { get => style; set => Set(ref style, value); }
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Unit) ? DisplayName : $"{DisplayName} [{Unit}]";
    public double[] Scaled() => Values.Select(x => x * Factor + Offset).ToArray();
}

public class TraceData
{
    public string FileName { get; set; } = "";
    public string TimeHeader { get; set; } = "";
    public double[] Time { get; set; } = [];
    public List<Signal> Signals { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class ChartOptions : Observable
{
    public string TimeUnit { get; set; } = "s";
    [JsonIgnore] public double TimeScale => TimeUnit switch { "min" => 60, "h" => 3600, "d" => 86400, _ => 1 };
    public double ToAxisTime(double seconds) => (seconds - Zero) / TimeScale;
    public double FromAxisTime(double displayed) => displayed * TimeScale + Zero;
    public string TimeAxisLabel { get; set; } = "Time";
    public const string UnitsMode = "Izvorne jedinice · više Y-osi";
    public const string PercentMode = "Normalizirano · %";
    public string DisplayMode { get; set; } = UnitsMode;
    public bool ShowExtrema { get; set; }
    public string Title { get; set; } = "Trace · Commissioning";
    public string Subtitle { get; set; } = "";
    public string LeftLabel { get; set; } = "Vrijednost";
    public string RightLabel { get; set; } = "Vrijednost";
    public double Start { get; set; }
    public double End { get; set; } = 100;
    public double Zero { get; set; }
    public double? YMin { get; set; }
    public double? YMax { get; set; }
    public double? RightMin { get; set; }
    public double? RightMax { get; set; }
    public bool Grid { get; set; } = true;
    public bool Legend { get; set; } = true;
    /// <summary>Word report: statistics table (min, max, mean, start, end) below this recording's graph.</summary>
    public bool ReportStatistics { get; set; } = true;
    bool cursors;
    double cursorA, cursorB = 10;
    public bool Cursors { get => cursors; set => Set(ref cursors, value); }
    bool zeroCursorVisible, hCursors; double zeroCursor, hCursorA, hCursorB = 1;
    /// <summary>Horizontal cursors A and B on the left Y axis of the first panel (axis units).</summary>
    public bool HCursors { get => hCursors; set => Set(ref hCursors, value); }
    public double HCursorA { get => hCursorA; set => Set(ref hCursorA, value); }
    public double HCursorB { get => hCursorB; set => Set(ref hCursorB, value); }
    /// <summary>Vertical cursor on the screen used to choose time zero (seconds from the first sample).</summary>
    public bool ZeroCursorVisible { get => zeroCursorVisible; set => Set(ref zeroCursorVisible, value); }
    public double ZeroCursor { get => zeroCursor; set => Set(ref zeroCursor, value); }
    public double CursorA { get => cursorA; set => Set(ref cursorA, value); }
    public double CursorB { get => cursorB; set => Set(ref cursorB, value); }
    public float FontSize { get; set; } = 14;
    public int ExportWidth { get; set; } = 3000;
    public int ExportHeight { get; set; } = 1800;
    public int Quality { get; set; } = 95;
}

public class ExtremaLabelOptions : Observable
{
    // Position within the available label area, independent of image dimensions and DPI.
    public double? X { get; set; }
    public double? Y { get; set; }
    bool hidden;
    public bool Hidden { get => hidden; set => Set(ref hidden, value); }
    [JsonIgnore] public bool IsManual => X.HasValue && Y.HasValue;
    public void ResetPosition() { X = null; Y = null; }
}

public class TraceNote
{
    public double Time { get; set; }
    public double Value { get; set; }
    public string Text { get; set; } = "Događaj";
    public int Panel { get; set; } = 1;
}

public class StudioProject
{
    public int Version { get; set; } = 1;
    public TraceData Data { get; set; } = new();
    public ChartOptions Options { get; set; } = new();
    public List<TraceNote> Notes { get; set; } = [];
}

public record SignalPreset(string Source, string Name, string Color, string Unit, bool Visible,
    double Factor, double Offset, double Width, int Panel, string Axis, string Style,
    double? PercentMin = null, double? PercentMax = null);
public class StudioPreset
{
    public ChartOptions Options { get; set; } = new();
    public List<SignalPreset> Signals { get; set; } = [];
}
public record StatisticsRow(string Signal, int Count, double Minimum, double Maximum, double Mean,
    double StdDev, double? AtA, double? AtB, double? Delta);
