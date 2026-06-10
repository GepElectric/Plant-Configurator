using System.Windows;
using CwcConfigurator.Services.Validation;

namespace CwcConfigurator.App;

public partial class ValidateOptionsDialog : Window
{
    public ValidateOptionsDialog()
    {
        InitializeComponent();
    }

    public ValidationOptions Options { get; private set; } = ValidationOptions.Default;

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        Options = new ValidationOptions
        {
            IncludeCrossReferenceChecks = IncludeCrossReferenceCheckBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
