using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Services;
using Microsoft.Win32;

namespace CwcConfigurator.App;

public partial class GenerateFilesDialog : Window
{
    public string OutputFolder => OutputFolderText.Text.Trim();

    public GenerationSelection Selection => new()
    {
        DigitalInputs = DiCheck.IsChecked == true,
        DigitalOutputs = DoCheck.IsChecked == true,
        AnalogInputs = AiCheck.IsChecked == true,
        RtdInputs = RtdCheck.IsChecked == true,
        Counters = CounterCheck.IsChecked == true,
        AnalogOutputs = AoCheck.IsChecked == true,
        DbDiConfiguration = DbDiConfigurationCheck.IsChecked == true,
        DbDoConfiguration = DbDoConfigurationCheck.IsChecked == true,
        DbMeasConfiguration = DbMeasConfigurationCheck.IsChecked == true,
        DbAoConfiguration = DbAoConfigurationCheck.IsChecked == true,
        DbAlmConfiguration = DbAlmConfigurationCheck.IsChecked == true,
        DbParConfiguration = DbParConfigurationCheck.IsChecked == true,
        DbDiStrings = DbDiStringsCheck.IsChecked == true,
        DbDoStrings = DbDoStringsCheck.IsChecked == true,
        DbMeasStrings = DbMeasStringsCheck.IsChecked == true,
        DbAlmStrings = DbAlmStringsCheck.IsChecked == true,
        DbParStrings = DbParStringsCheck.IsChecked == true,
        DbDiGroupNames = DbDiGroupNamesCheck.IsChecked == true,
        DbDoGroupNames = DbDoGroupNamesCheck.IsChecked == true,
        DbMeasGroupNames = DbMeasGroupNamesCheck.IsChecked == true,
        DbAlmGroupNames = DbAlmGroupNamesCheck.IsChecked == true,
        DbParGroupNames = DbParGroupNamesCheck.IsChecked == true
    };

    public GenerateFilesDialog(string outputFolder)
    {
        InitializeComponent();
        OutputFolderText.Text = outputFolder;
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose export folder",
            InitialDirectory = string.IsNullOrWhiteSpace(OutputFolder) ? Environment.CurrentDirectory : OutputFolder
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderText.Text = dialog.FolderName;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetChecks(SelectAllCheck.IsChecked == true, GetAllGenerationChecks());
        ConfigurationGroupCheck.IsChecked = SelectAllCheck.IsChecked;
        DbConfigurationGroupCheck.IsChecked = SelectAllCheck.IsChecked;
        DbNameGroupCheck.IsChecked = SelectAllCheck.IsChecked;
        DbGroupNameGroupCheck.IsChecked = SelectAllCheck.IsChecked;
    }

    private void ConfigurationGroup_Click(object sender, RoutedEventArgs e)
    {
        SetChecks(ConfigurationGroupCheck.IsChecked == true, DiCheck, DoCheck, AiCheck, RtdCheck, CounterCheck, AoCheck);
    }

    private void DbConfigurationGroup_Click(object sender, RoutedEventArgs e)
    {
        SetChecks(DbConfigurationGroupCheck.IsChecked == true, DbDiConfigurationCheck, DbDoConfigurationCheck, DbMeasConfigurationCheck, DbAoConfigurationCheck, DbAlmConfigurationCheck, DbParConfigurationCheck);
    }

    private void DbNameGroup_Click(object sender, RoutedEventArgs e)
    {
        SetChecks(DbNameGroupCheck.IsChecked == true, DbDiStringsCheck, DbDoStringsCheck, DbMeasStringsCheck, DbAlmStringsCheck, DbParStringsCheck);
    }

    private void DbGroupNameGroup_Click(object sender, RoutedEventArgs e)
    {
        SetChecks(DbGroupNameGroupCheck.IsChecked == true, DbDiGroupNamesCheck, DbDoGroupNamesCheck, DbMeasGroupNamesCheck, DbAlmGroupNamesCheck, DbParGroupNamesCheck);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private CheckBox[] GetAllGenerationChecks()
    {
        return
        [
            DiCheck,
            DoCheck,
            AiCheck,
            RtdCheck,
            CounterCheck,
            AoCheck,
            DbDiConfigurationCheck,
            DbDoConfigurationCheck,
            DbMeasConfigurationCheck,
            DbAoConfigurationCheck,
            DbAlmConfigurationCheck,
            DbParConfigurationCheck,
            DbDiStringsCheck,
            DbDoStringsCheck,
            DbMeasStringsCheck,
            DbAlmStringsCheck,
            DbParStringsCheck,
            DbDiGroupNamesCheck,
            DbDoGroupNamesCheck,
            DbMeasGroupNamesCheck,
            DbAlmGroupNamesCheck,
            DbParGroupNamesCheck
        ];
    }

    private static void SetChecks(bool isChecked, params CheckBox[] checks)
    {
        foreach (var check in checks)
        {
            check.IsChecked = isChecked;
        }
    }
}
