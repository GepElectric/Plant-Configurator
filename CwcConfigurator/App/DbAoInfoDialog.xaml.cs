using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CwcConfigurator.App;

public partial class DbAoInfoDialog : Window
{
    private const double BlockLeft = 880;
    private const double BlockRight = 1120;
    private const double BoxLeft = 30;
    private const double BoxWidth = 820;
    private const double BoxConnectorX = BoxLeft + BoxWidth;
    private const double RightBoxLeft = 1040;
    private const double RightBoxWidth = 420;

    public DbAoInfoDialog(string aoText, IReadOnlyList<MeasInfoItem> leftItems, string mapping, string texts)
    {
        InitializeComponent();
        AoText.Text = aoText;
        MappingText.Text = mapping;
        TextsText.Text = texts;
        DrawItems(leftItems, isLeftSide: true);
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

        var availableHeight = 500.0;
        var spacing = Math.Min(76.0, availableHeight / items.Count);
        var boxHeight = Math.Max(88.0, Math.Min(108.0, spacing - 8.0));
        var startTop = 128.0 + Math.Max(0.0, (availableHeight - spacing * items.Count) / 2.0);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var centerY = startTop + index * spacing + spacing / 2.0;
            var boxTop = centerY - boxHeight / 2.0;

            AddLine(isLeftSide ? BoxConnectorX : BlockRight, centerY, isLeftSide ? BlockLeft : RightBoxLeft, centerY);
            AddBlockLabel(item.Label, isLeftSide ? BlockLeft + 12 : BlockRight - 116, centerY - 9, isLeftSide);
            AddInfoBox(item, isLeftSide ? BoxLeft : RightBoxLeft, boxTop, isLeftSide ? BoxWidth : RightBoxWidth, boxHeight);
        }
    }

    private void AddInfoBox(MeasInfoItem item, double left, double top, double width, double height)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            Padding = new Thickness(4),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            Child = InfoTableCard.Create(item.TableRows, item.Text, 10.8, 8.6)
        };

        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        InfoCanvas.Children.Add(border);
    }

    private void AddBlockLabel(string text, double left, double top, bool isLeftSide)
    {
        var label = new TextBlock
        {
            Width = 120,
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = isLeftSide ? TextAlignment.Left : TextAlignment.Right,
            FontSize = 11,
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
