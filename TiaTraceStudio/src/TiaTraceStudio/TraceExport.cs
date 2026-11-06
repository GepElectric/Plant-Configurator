using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace TiaTraceStudio;

/// <summary>
/// A recording out of the studio: CSV (time and every signal in its scaled units, the list separator and decimal
/// sign of this PC, so Excel opens it directly) and an Excel chart on the clipboard — pasted into Word it stays an
/// editable chart with its data.
/// </summary>
public static class TraceExport
{
    const int MaxChartRows = 4000;

    /// <summary>Time on the graph's axis (its zero and unit) and the scaled value of every signal.</summary>
    public static string ToCsv(StudioProject project, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string separator = culture.TextInfo.ListSeparator is { Length: > 0 } list ? list : ";";
        var data = project.Data;
        var signals = data.Signals.ToList();
        var columns = signals.Select(signal => signal.Scaled()).ToList();
        var text = new StringBuilder();
        text.AppendLine(string.Join(separator, new[] { TimeHeader(project) }.Concat(signals.Select(signal => signal.Label)).Select(field => Quote(field, separator))));
        for (int row = 0; row < data.Time.Length; row++)
        {
            text.Append(project.Options.ToAxisTime(data.Time[row]).ToString("0.######", culture));
            foreach (var column in columns)
            {
                text.Append(separator);
                if (row < column.Length && double.IsFinite(column[row])) text.Append(column[row].ToString("0.######", culture));
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    public static void SaveCsv(StudioProject project, string path) =>
        File.WriteAllText(path, ToCsv(project), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    static string TimeHeader(StudioProject project)
    {
        var options = project.Options;
        return string.IsNullOrWhiteSpace(options.TimeAxisLabel) ? $"Time [{options.TimeUnit}]" : $"{options.TimeAxisLabel.Trim()} [{options.TimeUnit}]";
    }

    static string Quote(string field, string separator) =>
        field.Contains(separator) || field.Contains('"') ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    // Excel stays open in the background while its chart is on the clipboard (closing it would take the chart away).
    static dynamic? excel;
    static dynamic? workbook;

    /// <summary>
    /// Puts an Excel line chart (XY) of the visible signals on the clipboard, with the data in its workbook; pasted
    /// into Word it is an editable chart. Needs Microsoft Excel. Long recordings are thinned to 4000 points.
    /// </summary>
    public static void CopyExcelChart(StudioProject project, string title)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("Microsoft Excel nije instaliran / Microsoft Excel is not installed. Koristi Export CSV.");
        var data = project.Data;
        var signals = data.Signals.Where(signal => signal.Visible).ToList();
        if (signals.Count == 0) signals = data.Signals.ToList();
        if (data.Time.Length == 0 || signals.Count == 0) throw new InvalidOperationException("Snimka nema podataka / The recording has no data.");

        int step = Math.Max(1, (int)Math.Ceiling(data.Time.Length / (double)MaxChartRows));
        var rows = Enumerable.Range(0, data.Time.Length).Where(i => i % step == 0 || i == data.Time.Length - 1).ToList();
        var columns = signals.Select(signal => signal.Scaled()).ToList();
        var cells = new object?[rows.Count + 1, signals.Count + 1];
        cells[0, 0] = TimeHeader(project);
        for (int s = 0; s < signals.Count; s++) cells[0, s + 1] = signals[s].Label;
        for (int r = 0; r < rows.Count; r++)
        {
            cells[r + 1, 0] = project.Options.ToAxisTime(data.Time[rows[r]]);
            for (int s = 0; s < signals.Count; s++)
                cells[r + 1, s + 1] = rows[r] < columns[s].Length && double.IsFinite(columns[s][rows[r]]) ? columns[s][rows[r]] : null;
        }

        try { if (excel != null) { _ = excel.Visible; } }
        catch { excel = null; workbook = null; } // the user closed that Excel
        if (excel == null)
        {
            excel = Activator.CreateInstance(type)!;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            if (Application.Current != null) Application.Current.Exit += (_, _) => Quit();
        }

        try { workbook?.Close(false); } catch { /* already closed */ }
        workbook = excel.Workbooks.Add();
        dynamic sheet = workbook.Worksheets[1];
        sheet.Name = "Data";
        dynamic range = sheet.Range(sheet.Cells[1, 1], sheet.Cells[rows.Count + 1, signals.Count + 1]);
        range.Value2 = cells;

        dynamic holder = sheet.ChartObjects().Add(20, 20, 760, 380);
        dynamic chart = holder.Chart;
        chart.ChartType = 75; // xlXYScatterLinesNoMarkers
        chart.SetSourceData(range);
        chart.HasTitle = true;
        chart.ChartTitle.Text = title;
        chart.HasLegend = true;
        chart.Legend.Position = -4107; // xlLegendPositionBottom
        dynamic time = chart.Axes(1);
        time.HasTitle = true;
        time.AxisTitle.Text = TimeHeader(project);
        for (int s = 0; s < signals.Count; s++)
        {
            try
            {
                dynamic series = chart.SeriesCollection(s + 1);
                if (TryColor(signals[s].Color) is int rgb) series.Format.Line.ForeColor.RGB = rgb;
                series.Format.Line.Weight = 1.5;
            }
            catch { /* colour is cosmetic */ }
        }
        holder.Copy();
    }

    /// <summary>"#2563EB" → Excel RGB (red + green·256 + blue·65536).</summary>
    static int? TryColor(string hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length < 6 || !int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)) return null;
        int red = (value >> 16) & 0xFF, green = (value >> 8) & 0xFF, blue = value & 0xFF;
        return red + green * 256 + blue * 65536;
    }

    public static void Quit()
    {
        try { workbook?.Close(false); } catch { }
        try { excel?.Quit(); } catch { }
        workbook = null; excel = null;
    }
}
