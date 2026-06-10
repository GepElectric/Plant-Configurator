using System.Windows;

namespace CwcConfigurator.App;

public partial class PlcDatabaseNameDialog : Window
{
    public string DatabaseName => NameText.Text.Trim();

    public PlcDatabaseNameDialog(string suggestedName)
    {
        InitializeComponent();
        NameText.Text = suggestedName;
        NameText.SelectAll();
        NameText.Focus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DatabaseName))
        {
            MessageBox.Show(this, "Unesi naziv PLC baze.", "PLC database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
