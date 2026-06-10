using System.Collections.ObjectModel;
using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbGroupNamesDialog : Window
{
    public ObservableCollection<DbDiSystemNameRow> Rows { get; }

    public DbGroupNamesDialog(string dbName, IEnumerable<DbDiSystemNameRow> rows)
    {
        InitializeComponent();
        Title = $"{dbName} Group names";
        Rows = new ObservableCollection<DbDiSystemNameRow>(rows.Select(row => row.Clone()));
        GroupNamesGrid.ItemsSource = Rows;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
