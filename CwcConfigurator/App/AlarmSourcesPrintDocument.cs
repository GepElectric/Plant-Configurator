using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

/// <summary>
/// Printable "Alarms by source" (landscape): one table per source category, one after another — Digital input, AI,
/// RTD, Program. Column widths follow the page, so the same document fits A4 or A3.
/// </summary>
public static class AlarmSourcesPrintDocument
{
    public const double Dpi = 96.0;
    public const double MillimetreToDip = Dpi / 25.4;

    public static Size LandscapeSize(bool a3) => a3
        ? new Size(420 * MillimetreToDip, 297 * MillimetreToDip)
        : new Size(297 * MillimetreToDip, 210 * MillimetreToDip);

    private sealed record Column(string Header, Func<AlarmSourceViewRow, string> Value, double Width, bool Hardware = false);

    private static readonly Column[] Columns =
    [
        new("MOD", row => row.Module, 3.2, Hardware: true),
        new("PORT", row => row.Port, 3.2, Hardware: true),
        new("SRC ID", row => row.SourceId, 3.6, Hardware: true),
        new("SOURCE NAME", row => row.SourceName, 10, Hardware: true),
        new("ALM ID", row => row.AlarmId.ToString(), 3.6),
        new("PLC STRUCT", row => row.PlcStructure, 8),
        new("PLC NAME", row => row.PlcName, 11),
        new("ALARM SYSTEM ENG", row => row.SystemEnglish, 10),
        new("ALARM TEXT ENG", row => row.TextEnglish, 15),
        new("ALARM SYSTEM OTHER", row => row.SystemOther, 10),
        new("ALARM TEXT OTHER", row => row.TextOther, 15),
        new("ALM CONTROL", row => row.Control, 5),
        new("SR ALARM", row => row.SrAlarm, 4),
        new("ALARM CLASS", row => row.AlarmClass, 6),
        new("REACT TO UNIT", row => row.ReactToUnit, 5),
        new("SPARE", row => row.Spare, 4),
        new("BLOCK CLASS 1", row => row.BlockingClass1, 6),
        new("BLOCK CLASS 2", row => row.BlockingClass2, 6),
        new("DELAY", row => row.Delay, 3.6),
        new("TIME BASE", row => row.TimeBase, 4)
    ];

    private static readonly (AlarmSourceCategory Category, string Title)[] Sections =
    [
        (AlarmSourceCategory.DigitalInput, "Digital input"),
        (AlarmSourceCategory.AnalogInput, "AI"),
        (AlarmSourceCategory.Rtd, "RTD"),
        (AlarmSourceCategory.Program, "Program")
    ];

    public static FlowDocument Create(string title, IReadOnlyList<AlarmSourceViewRow> rows, bool a3)
    {
        var page = LandscapeSize(a3);
        var margin = 10 * MillimetreToDip;
        var fontSize = a3 ? 9.5 : 7.0;
        var document = new FlowDocument
        {
            PageWidth = page.Width,
            PageHeight = page.Height,
            PagePadding = new Thickness(margin),
            ColumnWidth = page.Width - 2 * margin, // one column across the printable width
            IsColumnWidthFlexible = false,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            TextAlignment = TextAlignment.Left
        };
        document.Blocks.Add(new Paragraph(new Run(title)) { FontSize = fontSize + 6, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        document.Blocks.Add(new Paragraph(new Run(
                $"Used alarms by source of alarm · printed {DateTime.Now:yyyy-MM-dd HH:mm} · inputs by module, port and alarm ID, program alarms by ID; " +
                "source \"both\" is listed under its digital and its analog input."))
            { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) });

        foreach (var (category, sectionTitle) in Sections)
        {
            var items = rows.Where(row => row.Category == category).ToList();
            document.Blocks.Add(new Paragraph(new Run($"{sectionTitle} ({items.Count})"))
            {
                FontSize = fontSize + 3,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 3),
                KeepWithNext = true
            });
            if (items.Count == 0)
            {
                document.Blocks.Add(new Paragraph(new Run("—")) { Foreground = Brushes.DimGray });
                continue;
            }

            document.Blocks.Add(CreateTable(items, category != AlarmSourceCategory.Program, page.Width - 2 * margin));
        }

        return document;
    }

    // Absolute column widths: the relative weights spread over the printable width (WPF star widths in a
    // paginated FlowDocument table overflow the page).
    private static Table CreateTable(IReadOnlyList<AlarmSourceViewRow> items, bool hardware, double width)
    {
        var columns = Columns.Where(column => hardware || !column.Hardware).ToList();
        var unit = (width - 2) / columns.Sum(column => column.Width);
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
        foreach (var column in columns)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(column.Width * unit) });
        }

        var header = new TableRowGroup { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF0)), FontWeight = FontWeights.SemiBold };
        header.Rows.Add(CreateRow(columns.Select(column => column.Header)));
        table.RowGroups.Add(header);

        var body = new TableRowGroup();
        string? previousModule = null;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = CreateRow(columns.Select(column => column.Value(item)));
            if (index % 2 == 1)
            {
                row.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFA));
            }

            // A thicker line where a new module starts.
            if (hardware && previousModule is not null && previousModule != item.Module)
            {
                foreach (var cell in row.Cells)
                {
                    cell.BorderThickness = new Thickness(0.5, 1.5, 0.5, 0.5);
                }
            }

            previousModule = item.Module;
            body.Rows.Add(row);
        }

        table.RowGroups.Add(body);
        return table;
    }

    private static TableRow CreateRow(IEnumerable<string> values)
    {
        var row = new TableRow();
        foreach (var value in values)
        {
            row.Cells.Add(new TableCell(new Paragraph(new Run(value ?? "")) { Margin = new Thickness(0) })
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(2, 1, 2, 1)
            });
        }

        return row;
    }
}
