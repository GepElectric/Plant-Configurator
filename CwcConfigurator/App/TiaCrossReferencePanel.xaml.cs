using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.App;

public sealed record TiaCrossReferenceSpec(string Member, string VariablePath);

public partial class TiaCrossReferencePanel : UserControl
{
    private readonly Dictionary<string, CrossReferenceControls> _controls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, TiaCrossReferenceItem?, Task>? _openCrossReferenceAsync;

    public TiaCrossReferencePanel(
        IReadOnlyList<TiaCrossReferenceSpec> specs,
        Func<string, TiaCrossReferenceItem?, Task>? openCrossReferenceAsync)
    {
        InitializeComponent();
        _openCrossReferenceAsync = openCrossReferenceAsync;

        foreach (var spec in specs)
        {
            AddSection(spec);
        }
    }

    public void SetCrossReferenceLoading(string member)
    {
        if (!_controls.TryGetValue(member, out var target))
        {
            return;
        }

        target.Summary.Text = $"Scanning TIA blocks for {member} usage...";
        target.Grid.ItemsSource = null;
        target.OpenButton.IsEnabled = false;
    }

    public void SetCrossReferenceResult(string member, TiaCrossReferenceResult result)
    {
        if (!_controls.TryGetValue(member, out var target))
        {
            return;
        }

        target.Summary.Text = result.Warnings.Count == 0
            ? $"Found {result.UsageCount} usage(s), shown as {result.Items.Count} row(s)."
            : $"Found {result.UsageCount} usage(s), shown as {result.Items.Count} row(s). Warnings: {string.Join(" | ", result.Warnings.Take(3))}";
        target.Grid.ItemsSource = result.Items;
        target.Grid.SelectedItem = null;
        target.OpenButton.IsEnabled = false;
    }

    public void SetCrossReferenceUnavailable(string member, string message)
    {
        if (!_controls.TryGetValue(member, out var target))
        {
            return;
        }

        target.Summary.Text = message;
        target.Grid.ItemsSource = null;
        target.OpenButton.IsEnabled = false;
    }

    private void AddSection(TiaCrossReferenceSpec spec)
    {
        var section = new Grid
        {
            MinHeight = 230,
            Margin = new Thickness(0, 0, 0, 18)
        };
        section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{spec.Member} VARIABLE",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = spec.VariablePath,
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(titleStack);

        var openButton = new Button
        {
            Content = "Open in TIA",
            Width = 120,
            Height = 32,
            Margin = new Thickness(18, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = false
        };
        Grid.SetColumn(openButton, 1);
        header.Children.Add(openButton);

        section.Children.Add(header);

        var summary = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(spec.VariablePath)
                ? "No variable path is available for this row."
                : "Cross-reference scan will start when this window opens.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        var summaryBorder = new Border
        {
            Margin = new Thickness(0, 10, 0, 8),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            Child = summary
        };
        Grid.SetRow(summaryBorder, 1);
        section.Children.Add(summaryBorder);

        var grid = CreateCrossReferenceGrid();
        Grid.SetRow(grid, 2);
        section.Children.Add(grid);

        grid.SelectionChanged += (_, _) =>
        {
            openButton.IsEnabled = _openCrossReferenceAsync is not null && grid.SelectedItem is TiaCrossReferenceItem;
        };
        openButton.Click += async (_, _) =>
        {
            if (_openCrossReferenceAsync is null || grid.SelectedItem is not TiaCrossReferenceItem selectedItem)
            {
                openButton.IsEnabled = false;
                return;
            }

            openButton.IsEnabled = false;
            try
            {
                await _openCrossReferenceAsync(spec.Member, selectedItem);
            }
            finally
            {
                openButton.IsEnabled = grid.SelectedItem is TiaCrossReferenceItem;
            }
        };

        _controls[spec.Member] = new CrossReferenceControls(summary, grid, openButton);
        SectionsHost.Children.Add(section);
    }

    private static DataGrid CreateCrossReferenceGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            MinHeight = 145
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "Block", Binding = new System.Windows.Data.Binding("BlockName"), Width = new DataGridLength(180) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Network", Binding = new System.Windows.Data.Binding("Network"), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Reference location", Binding = new System.Windows.Data.Binding("ReferenceLocation"), Width = new DataGridLength(230) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Reference type", Binding = new System.Windows.Data.Binding("ReferenceType"), Width = new DataGridLength(115) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Access", Binding = new System.Windows.Data.Binding("Access"), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("Address"), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding("TypeName"), Width = new DataGridLength(100) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new System.Windows.Data.Binding("Path"), Width = new DataGridLength(230) });

        return grid;
    }

    private sealed record CrossReferenceControls(TextBlock Summary, DataGrid Grid, Button OpenButton);
}
