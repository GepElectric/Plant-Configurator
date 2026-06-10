using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class DbDiRowSettingsDialog : Window
{
    public string PlcDefinitionName { get; private set; } = "";
    public string StatusSystemEnglish { get; private set; } = "";
    public string StatusNameEnglish { get; private set; } = "";
    public string StatusSystemOther { get; private set; } = "";
    public string StatusNameOther { get; private set; } = "";
    public string ColorValue0 { get; private set; } = "";
    public string ColorValue1 { get; private set; } = "";
    public bool ApplyStatusSystemEnglishToStructure { get; private set; }
    public bool ApplyStatusSystemOtherToStructure { get; private set; }

    public DbDiRowSettingsDialog(DbStringRow row, IReadOnlyList<DbValidationOption> colorValues, string dbName = "DB DI", bool showColors = true)
    {
        InitializeComponent();
        Title = $"{dbName} Row Settings";
        RowTitleText.Text = $"{dbName} ID {row.Id}";
        RowSubtitleText.Text = $"{row.PlcStructure}  |  {row.PlcName}";

        StatusSystemEngText.Text = row.SystemNameEnglish;
        StatusNameEngText.Text = row.NameEnglish;
        StatusSystemOtherText.Text = row.SystemNameOther;
        StatusNameOtherText.Text = row.NameOther;
        ColorValue0Label.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        ColorValue0Combo.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        ColorValue1Label.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        ColorValue1Combo.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        ColorValue0Combo.ItemsSource = colorValues;
        ColorValue1Combo.ItemsSource = colorValues;
        ColorValue0Combo.SelectedValue = row.ColorValue0;
        ColorValue1Combo.SelectedValue = row.ColorValue1;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        StatusSystemEnglish = StatusSystemEngText.Text;
        StatusNameEnglish = StatusNameEngText.Text;
        StatusSystemOther = StatusSystemOtherText.Text;
        StatusNameOther = StatusNameOtherText.Text;
        ColorValue0 = ColorValue0Combo.SelectedValue as string ?? "";
        ColorValue1 = ColorValue1Combo.SelectedValue as string ?? "";
        ApplyStatusSystemEnglishToStructure = ApplyStatusSystemEngToStructureCheck.IsChecked == true;
        ApplyStatusSystemOtherToStructure = ApplyStatusSystemOtherToStructureCheck.IsChecked == true;
        DialogResult = true;
    }
}
