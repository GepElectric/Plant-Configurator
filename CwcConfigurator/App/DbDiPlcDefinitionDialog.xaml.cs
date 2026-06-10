using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbDiPlcDefinitionDialog : Window
{
    public string PlcDefinitionName { get; private set; } = "";

    public DbDiPlcDefinitionDialog(DbStringRow row, string dbName = "DB DI")
    {
        InitializeComponent();
        RowTitleText.Text = $"{dbName} ID {row.Id}";
        RowSubtitleText.Text = row.PlcStructure;
        PlcDefinitionNameText.Text = row.PlcName;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        PlcDefinitionName = PlcDefinitionNameText.Text;
        DialogResult = true;
    }
}
