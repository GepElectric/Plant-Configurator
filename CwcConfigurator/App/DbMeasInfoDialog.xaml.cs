using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CwcConfigurator.App;

public sealed record InfoTableCell(string Label, string Value);
public sealed record InfoTableRow(IReadOnlyList<InfoTableCell> Cells);
public sealed record MeasInfoItem(string Label, string Text, IReadOnlyList<InfoTableRow>? TableRows = null);

public static class InfoTableCard
{
    public static FrameworkElement Create(IReadOnlyList<InfoTableRow>? rows, string fallbackText, double fontSize, double labelFontSize)
    {
        if (rows is null || rows.Count == 0)
        {
            return new TextBlock
            {
                Text = fallbackText,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = fontSize,
                LineHeight = fontSize + 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var row in rows)
        {
            if (row.Cells.Count == 0)
            {
                continue;
            }

            panel.Children.Add(CreateTable(row.Cells, fontSize, labelFontSize));
        }

        return panel;
    }

    private static FrameworkElement CreateTable(IReadOnlyList<InfoTableCell> cells, double fontSize, double labelFontSize)
    {
        if (cells.Count == 1)
        {
            return CreateSingleValueTable(cells[0], fontSize, labelFontSize);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < cells.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var header = CreateTableCell(Normalize(cell.Label), labelFontSize, FontWeights.SemiBold, TextAlignment.Center);
            var value = CreateTableCell(NormalizeValue(cell.Value), fontSize, FontWeights.Normal, TextAlignment.Center);

            Grid.SetColumn(header, index);
            Grid.SetColumn(value, index);
            Grid.SetRow(value, 1);
            grid.Children.Add(header);
            grid.Children.Add(value);
        }

        return CreateTableBorder(grid, cells);
    }

    private static FrameworkElement CreateSingleValueTable(InfoTableCell cell, double fontSize, double labelFontSize)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var header = CreateTableCell(Normalize(cell.Label), labelFontSize, FontWeights.SemiBold, TextAlignment.Left);
        var value = CreateTableCell(NormalizeValue(cell.Value), fontSize, FontWeights.Normal, TextAlignment.Left);

        Grid.SetColumn(value, 1);
        grid.Children.Add(header);
        grid.Children.Add(value);

        return CreateTableBorder(grid, [cell]);
    }

    private static Border CreateTableBorder(UIElement child, IReadOnlyList<InfoTableCell> cells)
    {
        var labels = string.Join("|", cells.Select(cell => (cell.Label ?? "").Trim().ToUpperInvariant()));
        var background = labels switch
        {
            var value when value == "PLC" => Color.FromRgb(239, 246, 255),
            var value when value == "TEXT" || value == "ALARM TEXT" => Color.FromRgb(240, 253, 244),
            var value when value.Contains("BLOCKING CLASS", StringComparison.OrdinalIgnoreCase) => Color.FromRgb(255, 251, 235),
            _ => Color.FromRgb(248, 250, 252)
        };

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(4, 1, 4, 1),
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            Child = child
        };
    }

    private static TextBlock CreateTableCell(string text, double fontSize, FontWeight fontWeight, TextAlignment alignment)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(2, 0, 2, 0),
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = Brushes.Black,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = fontSize + 1,
            MaxHeight = (fontSize + 1) * 2.2,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "NOT CONFIGURED" : value.Trim();
    }
}

public partial class DbMeasInfoDialog : Window
{
    private const double BlockLeft = 740;
    private const double BlockRight = 1020;
    private const double LeftBoxLeft = 20;
    private const double LeftParameterBoxWidth = 700;
    private const double LeftEnableBoxLeft = 630;
    private const double LeftEnableBoxWidth = 90;
    private const double RightBoxLeft = 1080;
    private const double RightBoxWidth = 720;

    public DbMeasInfoDialog(string measurementText, IReadOnlyList<MeasInfoItem> leftItems, IReadOnlyList<MeasInfoItem> rightItems, string logging, string mapping, string texts)
    {
        InitializeComponent();
        MeasurementText.Text = measurementText;
        LoggingText.Text = logging;
        MappingText.Text = mapping;
        TextsText.Text = texts;
        DrawItems(leftItems, isLeftSide: true);
        DrawItems(rightItems, isLeftSide: false);
    }

    public void AddCrossReferenceTab(TiaCrossReferencePanel panel)
    {
        RootTabs.Items.Add(new TabItem
        {
            Header = "TIA cross-reference",
            Content = panel
        });
    }

    private void DrawItems(IReadOnlyList<MeasInfoItem> items, bool isLeftSide)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (isLeftSide)
        {
            DrawLeftItems(items);
            return;
        }

        var availableHeight = isLeftSide ? 820.0 : 780.0;
        var spacing = Math.Min(isLeftSide ? 72.0 : 154.0, availableHeight / items.Count);
        var boxHeight = isLeftSide
            ? Math.Max(58.0, Math.Min(66.0, spacing - 3.0))
            : Math.Max(126.0, Math.Min(142.0, spacing - 10.0));
        var startTop = (isLeftSide ? 130.0 : 120.0) + Math.Max(0.0, (availableHeight - spacing * items.Count) / 2.0);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var centerY = startTop + index * spacing + spacing / 2.0;
            var boxTop = centerY - boxHeight / 2.0;

            AddLine(isLeftSide ? LeftBoxLeft + 520 : BlockRight, centerY, isLeftSide ? BlockLeft : RightBoxLeft, centerY);
            AddBlockLabel(item.Label, isLeftSide ? BlockLeft + 12 : BlockRight - 104, centerY - 9, isLeftSide);
            AddInfoBox(item, isLeftSide ? LeftBoxLeft : RightBoxLeft, boxTop, isLeftSide ? 540 : RightBoxWidth, boxHeight, isLeftSide);
        }
    }

    private void DrawLeftItems(IReadOnlyList<MeasInfoItem> items)
    {
        const double availableHeight = 800.0;
        const double gap = 4.0;
        var heights = items.Select(item => IsEnableInput(item) ? 28.0 : 74.0).ToList();
        var totalHeight = heights.Sum() + gap * Math.Max(0, items.Count - 1);
        var top = 76.0 + Math.Max(0.0, (availableHeight - totalHeight) / 2.0);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var isEnable = IsEnableInput(item);
            var boxHeight = heights[index];
            var boxLeft = isEnable ? LeftEnableBoxLeft : LeftBoxLeft;
            var boxWidth = isEnable ? LeftEnableBoxWidth : LeftParameterBoxWidth;
            var centerY = top + boxHeight / 2.0;

            AddLine(boxLeft + boxWidth, centerY, BlockLeft, centerY);
            AddBlockLabel(item.Label, BlockLeft + 12, centerY - 9, isLeftSide: true);
            AddInfoBox(item, boxLeft, top, boxWidth, boxHeight, isLeftSide: true);

            top += boxHeight + gap;
        }
    }

    private static bool IsEnableInput(MeasInfoItem item)
    {
        return item.Label.StartsWith("ENB", StringComparison.OrdinalIgnoreCase);
    }

    private void AddInfoBox(MeasInfoItem item, double left, double top, double width, double height, bool isLeftSide)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            Padding = new Thickness(4),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            Child = InfoTableCard.Create(item.TableRows, item.Text, isLeftSide ? 10.8 : 10.0, isLeftSide ? 8.6 : 8.0)
        };

        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        InfoCanvas.Children.Add(border);
    }

    private void AddBlockLabel(string text, double left, double top, bool isLeftSide)
    {
        var label = new TextBlock
        {
            Width = 100,
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = isLeftSide ? TextAlignment.Left : TextAlignment.Right,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };

        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        InfoCanvas.Children.Add(label);
    }

    private void AddLine(double x1, double y1, double x2, double y2)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            StrokeThickness = 2
        };

        InfoCanvas.Children.Add(line);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyOwnerRelativeSize();
    }

    private void ApplyOwnerRelativeSize()
    {
        var ownerWidth = Owner?.ActualWidth > 0 ? Owner.ActualWidth : SystemParameters.WorkArea.Width;
        var ownerHeight = Owner?.ActualHeight > 0 ? Owner.ActualHeight : SystemParameters.WorkArea.Height;
        Width = ownerWidth * 0.96;
        Height = ownerHeight * 0.96;

        if (Owner is null)
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2.0;
            return;
        }

        if (Owner.WindowState == WindowState.Maximized)
        {
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            return;
        }

        Left = Owner.Left;
        Top = Owner.Top;
    }
}
