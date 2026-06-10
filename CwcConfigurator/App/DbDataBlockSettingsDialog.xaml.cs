using System.Collections.ObjectModel;
using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbDataBlockSettingsDialog : Window
{
    public ObservableCollection<DbDataBlockNameSetting> Rows { get; }

    public DbDataBlockSettingsDialog(IEnumerable<DbDataBlockNameSetting> rows)
    {
        InitializeComponent();
        Rows = new ObservableCollection<DbDataBlockNameSetting>(rows.Select(row => row.Clone()));
        DataBlockNamesGrid.ItemsSource = Rows;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var nextNumber = Rows.Count == 0 ? 1 : Rows.Max(row => row.Number) + 1;
        var row = new DbDataBlockNameSetting
        {
            Number = nextNumber,
            BlockKind = DbDataBlockNameDefaults.StringKind
        };

        Rows.Add(row);
        DataBlockNamesGrid.SelectedItem = row;
        DataBlockNamesGrid.ScrollIntoView(row);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = DataBlockNamesGrid.SelectedItems
            .OfType<DbDataBlockNameSetting>()
            .ToList();

        foreach (var row in selectedRows)
        {
            Rows.Remove(row);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
