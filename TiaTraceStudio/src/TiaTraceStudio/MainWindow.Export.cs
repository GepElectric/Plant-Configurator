using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TiaTraceStudio;

/// <summary>Export CSV and Copy Excel chart next to every recording.</summary>
public partial class MainWindow
{
    /// <summary>The project of a recording, with the edits of the open one applied.</summary>
    StudioProject ProjectOf(OpenTrace doc)
    {
        if (doc != activeDocument) return doc.Project;
        Commit();
        return project;
    }

    void ExportCsv(OpenTrace doc)
    {
        var source = ProjectOf(doc);
        string name = string.Concat((string.IsNullOrWhiteSpace(source.Options.Title) ? Path.GetFileNameWithoutExtension(doc.Title) : source.Options.Title)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true, FileName = (name.Length > 0 ? name : "Trace") + ".csv" };
        if (dialog.ShowDialog(this) != true) return;
        TraceExport.SaveCsv(source, dialog.FileName);
        Status($"CSV izvezen / CSV exported: {dialog.FileName}");
    }

    void CopyExcelChart(OpenTrace doc)
    {
        var source = ProjectOf(doc);
        string title = string.Join(" · ", new[] { source.Options.Title, source.Options.Subtitle }.Where(text => !string.IsNullOrWhiteSpace(text)));
        Mouse.OverrideCursor = Cursors.Wait;
        try { TraceExport.CopyExcelChart(source, title.Length > 0 ? title : doc.Title); }
        finally { Mouse.OverrideCursor = null; }
        Status("Excel graf kopiran — zalijepi u Word (Ctrl+V), graf se ondje može uređivati / Excel chart copied — paste into Word (Ctrl+V), it stays editable.");
    }

    /// <summary>Tree header of a recording: its name and the small CSV / Excel buttons.</summary>
    FrameworkElement RecordingHeader(OpenTrace doc, Brush brush, double maxWidth)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Header("∿", brush, doc.Title, null, bold: doc == activeDocument, maxWidth: Math.Max(80, maxWidth - 92)));
        panel.Children.Add(SmallButton("CSV", "Export CSV — vrijeme i svi signali u njihovim jedinicama / time and every signal in its units", () => ExportCsv(doc)));
        panel.Children.Add(SmallButton("Excel", "Copy Excel chart — u Wordu ostaje graf koji se može uređivati / stays an editable chart in Word", () => CopyExcelChart(doc)));
        return panel;
    }

    Button SmallButton(string text, string tooltip, Action action)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), FontSize = 11, MinHeight = 0, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, e) => { e.Handled = true; Guard(action); };
        return button;
    }
}
