using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class PlcDatabasePickerDialog : Window
{
    public PlcDatabase? SelectedDatabase => CreateNew ? null : DatabaseCombo.SelectedItem as PlcDatabase;
    public bool CreateNew => CreateNewCheck.IsChecked == true;
    public string NewDatabaseName => NewNameText.Text.Trim();

    public PlcDatabasePickerDialog(string prompt, IEnumerable<PlcDatabase> databases, bool allowCreateNew = false, string suggestedNewName = "PLC")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        DatabaseCombo.ItemsSource = databases.ToList();
        DatabaseCombo.SelectedIndex = DatabaseCombo.Items.Count > 0 ? 0 : -1;
        CreateNewCheck.Visibility = allowCreateNew ? Visibility.Visible : Visibility.Collapsed;
        NewNameText.Text = suggestedNewName;
    }

    private void CreateNewCheck_Changed(object sender, RoutedEventArgs e)
    {
        var createNew = CreateNew;
        DatabaseCombo.IsEnabled = !createNew;
        NewNameText.Visibility = createNew ? Visibility.Visible : Visibility.Collapsed;
        if (createNew)
        {
            NewNameText.SelectAll();
            NewNameText.Focus();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CreateNew)
        {
            if (string.IsNullOrWhiteSpace(NewDatabaseName))
            {
                MessageBox.Show(this, "Unesi naziv nove PLC baze.", "PLC database", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (SelectedDatabase is null)
        {
            MessageBox.Show(this, "Odaberi PLC bazu.", "PLC database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
