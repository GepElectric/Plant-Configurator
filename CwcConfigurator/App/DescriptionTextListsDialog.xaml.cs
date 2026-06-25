using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class DescriptionTextListsDialog : Window, INotifyPropertyChanged
{
    private const string AutoSourceLabel = "(auto — first device)";

    private readonly CwcProject _project;
    private readonly DescriptionTextListService _service;
    private readonly Func<string, Task<DescriptionTextListData>>? _pullFromTia;
    private readonly ObservableCollection<DescriptionTextListMapping> _mappings;

    public ObservableCollection<string> AvailableListNames { get; } = ["(none)"];
    public ObservableCollection<string> ScadaSourceDevices { get; } = [];
    public bool Changed { get; private set; }

    public DescriptionTextListsDialog(
        CwcProject project,
        DescriptionTextListService service,
        Func<string, Task<DescriptionTextListData>>? pullFromTia = null,
        IReadOnlyList<string>? scadaDevices = null)
    {
        InitializeComponent();
        _project = project;
        _service = service;
        _pullFromTia = pullFromTia;
        _project.DescriptionTextLists ??= new DescriptionTextListConfig();
        _project.DescriptionTextLists.SyncWithDatabases(_project.PlcDatabases);
        _mappings = new ObservableCollection<DescriptionTextListMapping>(_project.DescriptionTextLists.Mappings.OrderBy(item => item.PlcName, StringComparer.OrdinalIgnoreCase));
        DataContext = this;
        MappingGrid.ItemsSource = _mappings;
        WorkbookPathBox.Text = _project.DescriptionTextLists.WorkbookPath;

        if (scadaDevices is { Count: > 0 })
        {
            foreach (var device in scadaDevices)
            {
                ScadaSourceDevices.Add(device);
            }
        }
        else
        {
            ScadaSourceDevices.Add(AutoSourceLabel);
        }

        ScadaSourceCombo.SelectedIndex = 0;
        PullFromTiaButton.IsEnabled = _pullFromTia is not null;
        RefreshAvailableLists();
        RefreshStatus();
    }

    private async void PullFromTia_Click(object sender, RoutedEventArgs e)
    {
        if (_pullFromTia is null)
        {
            return;
        }

        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var selected = ScadaSourceCombo.SelectedItem as string ?? "";
            var device = selected == AutoSourceLabel ? "" : selected;
            var data = await _pullFromTia(device);
            _project.DescriptionTextLists.TextLists = data;
            _project.DescriptionTextLists.WorkbookPath = "";
            RefreshAvailableLists();
            AutoMapEmptyDescriptionLists();
            var changed = _service.ApplyDescriptionsToProject(_project);
            Changed = true;
            MappingGrid.Items.Refresh();
            RefreshStatus($"Pulled {data.AllListNames.Count} text list(s) from TIA and applied {changed} description cell(s).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Pull descriptions from TIA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            Title = "Select Unified_text_lists.xlsx",
            FileName = string.IsNullOrWhiteSpace(WorkbookPathBox.Text) ? "Unified_text_lists.xlsx" : WorkbookPathBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            WorkbookPathBox.Text = dialog.FileName;
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WorkbookPathBox.Text))
        {
            return;
        }

        try
        {
            _project.DescriptionTextLists.TextLists = _service.ImportWorkbook(WorkbookPathBox.Text);
            _project.DescriptionTextLists.WorkbookPath = WorkbookPathBox.Text;
            RefreshAvailableLists();
            AutoMapEmptyDescriptionLists();
            var changed = _service.ApplyDescriptionsToProject(_project);
            Changed = true;
            MappingGrid.Items.Refresh();
            RefreshStatus($"Loaded workbook and applied {changed} description cell(s).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Descriptions text lists", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveMappings();
        var changed = _service.ApplyDescriptionsToProject(_project);
        Changed = true;
        RefreshStatus($"Applied {changed} description cell(s).");
    }

    private void SaveWorkbook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveMappings();
            var path = string.IsNullOrWhiteSpace(_project.DescriptionTextLists.WorkbookPath)
                ? WorkbookPathBox.Text
                : _project.DescriptionTextLists.WorkbookPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Workbook path is not selected.");
            }

            _service.EnsureExportReferences(_project);
            _service.PushDescriptionsIntoTextLists(_project);
            _service.SaveWorkbook(_project, path);
            Changed = true;
            RefreshStatus("Workbook saved: " + path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Descriptions text lists", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SaveMappings();
        DialogResult = true;
    }

    private void MappingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(SaveMappings);
    }

    private void SaveMappings()
    {
        foreach (var mapping in _mappings)
        {
            mapping.AlmDescriptionsList = NormalizeListName(mapping.AlmDescriptionsList);
            mapping.ParDescriptionsList = NormalizeListName(mapping.ParDescriptionsList);
            var target = _project.DescriptionTextLists.GetOrCreate(mapping.PlcDatabaseId, mapping.PlcName);
            target.AlmDescriptionsList = mapping.AlmDescriptionsList;
            target.ParDescriptionsList = mapping.ParDescriptionsList;
            target.SequenceControlStatusList = NormalizeListName(mapping.SequenceControlStatusList);
            target.SequenceCommandList = NormalizeListName(mapping.SequenceCommandList);
            target.AlmBlockingClassList = NormalizeListName(mapping.AlmBlockingClassList);
            target.CabinetsList = NormalizeListName(mapping.CabinetsList);
            target.PortConfigurationList = NormalizeListName(mapping.PortConfigurationList);
            target.SeqCmdList = NormalizeListName(mapping.SeqCmdList);
            target.SeqConfigList = NormalizeListName(mapping.SeqConfigList);
        }

        Changed = true;
    }

    private void RefreshAvailableLists()
    {
        AvailableListNames.Clear();
        AvailableListNames.Add("(none)");
        foreach (var name in _project.DescriptionTextLists.TextLists.AllListNames.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            AvailableListNames.Add(name);
        }

        AlmDescriptionListColumn.ItemsSource = AvailableListNames;
        ParDescriptionListColumn.ItemsSource = AvailableListNames;
        SequenceControlStatusListColumn.ItemsSource = AvailableListNames;
        SequenceCommandListColumn.ItemsSource = AvailableListNames;
        AlmBlockingClassListColumn.ItemsSource = AvailableListNames;
        CabinetsListColumn.ItemsSource = AvailableListNames;
        PortConfigurationListColumn.ItemsSource = AvailableListNames;
        SeqCmdListColumn.ItemsSource = AvailableListNames;
        SeqConfigListColumn.ItemsSource = AvailableListNames;
        OnPropertyChanged(nameof(AvailableListNames));
    }

    private void AutoMapEmptyDescriptionLists()
    {
        var listNames = _project.DescriptionTextLists.TextLists.AllListNames;
        foreach (var mapping in _mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.AlmDescriptionsList))
            {
                mapping.AlmDescriptionsList = FindBestListName(listNames, mapping.PlcName, "ALARM_DESCRIPTIONS", "ALM_DESCRIPTIONS");
            }

            if (string.IsNullOrWhiteSpace(mapping.ParDescriptionsList))
            {
                mapping.ParDescriptionsList = FindBestListName(listNames, mapping.PlcName, "PARAMETER_DESCRIPTION_LIST", "PAR_DESCRIPTIONS", "PARAMETER_DESCRIPTIONS");
            }

            if (string.IsNullOrWhiteSpace(mapping.SequenceControlStatusList))
            {
                mapping.SequenceControlStatusList = FindBestListName(listNames, mapping.PlcName, "CONTROL_STATUS", "CTRL_STS", "SEQUENCE_CONTROL_STATUS");
            }

            if (string.IsNullOrWhiteSpace(mapping.SequenceCommandList))
            {
                mapping.SequenceCommandList = FindBestListName(listNames, mapping.PlcName, "SEQUENCE_COMMAND", "SEQUENCE_COMMANDS", "SEQ_CMD");
            }

            if (string.IsNullOrWhiteSpace(mapping.AlmBlockingClassList))
            {
                mapping.AlmBlockingClassList = FindBestListName(listNames, mapping.PlcName, "ALM_BLOCKING_CLASS", "ALARM_BLOCKING_CLASS", "BLOCKING_CLASS");
            }

            if (string.IsNullOrWhiteSpace(mapping.CabinetsList))
            {
                mapping.CabinetsList = FindBestListName(listNames, mapping.PlcName, "CABINETS", "CABINET");
            }

            if (string.IsNullOrWhiteSpace(mapping.PortConfigurationList))
            {
                mapping.PortConfigurationList = FindBestListName(listNames, mapping.PlcName, "PORT_CONFIGURATION", "PORT_CONFIG");
            }

            if (string.IsNullOrWhiteSpace(mapping.SeqCmdList))
            {
                mapping.SeqCmdList = FindBestListName(listNames, mapping.PlcName, "SEQ_CMD", "SEQUENCE_CMD", "SEQUENCE_COMMAND_ON_OFF");
            }

            if (string.IsNullOrWhiteSpace(mapping.SeqConfigList))
            {
                mapping.SeqConfigList = FindBestListName(listNames, mapping.PlcName, "SEQ_CONFIG", "SEQUENCE_CONFIG", "SEQUENCE_CONFIGURATION");
            }

            var target = _project.DescriptionTextLists.GetOrCreate(mapping.PlcDatabaseId, mapping.PlcName);
            target.AlmDescriptionsList = NormalizeListName(mapping.AlmDescriptionsList);
            target.ParDescriptionsList = NormalizeListName(mapping.ParDescriptionsList);
            target.SequenceControlStatusList = NormalizeListName(mapping.SequenceControlStatusList);
            target.SequenceCommandList = NormalizeListName(mapping.SequenceCommandList);
            target.AlmBlockingClassList = NormalizeListName(mapping.AlmBlockingClassList);
            target.CabinetsList = NormalizeListName(mapping.CabinetsList);
            target.PortConfigurationList = NormalizeListName(mapping.PortConfigurationList);
            target.SeqCmdList = NormalizeListName(mapping.SeqCmdList);
            target.SeqConfigList = NormalizeListName(mapping.SeqConfigList);
        }
    }

    private static string FindBestListName(IReadOnlyList<string> listNames, string plcName, params string[] suffixes)
    {
        if (listNames.Count == 0)
        {
            return "(none)";
        }

        var normalizedPlc = NormalizeMatchText(plcName);
        foreach (var suffix in suffixes)
        {
            var normalizedSuffix = NormalizeMatchText(suffix);
            var exact = listNames.FirstOrDefault(name =>
                NormalizeMatchText(name).Equals(normalizedPlc + "_" + normalizedSuffix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }
        }

        foreach (var suffix in suffixes)
        {
            var normalizedSuffix = NormalizeMatchText(suffix);
            var contains = listNames.FirstOrDefault(name =>
            {
                var normalized = NormalizeMatchText(name);
                return normalized.Contains(normalizedPlc, StringComparison.OrdinalIgnoreCase)
                       && normalized.Contains(normalizedSuffix, StringComparison.OrdinalIgnoreCase);
            });
            if (!string.IsNullOrWhiteSpace(contains))
            {
                return contains;
            }
        }

        return "(none)";
    }

    private static string NormalizeMatchText(string value)
    {
        return string.Join("_", (value ?? "")
            .Split([' ', '-', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
    }

    private void RefreshStatus(string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            return;
        }

        var data = _project.DescriptionTextLists.TextLists;
        if (data.AllListNames.Count > 0)
        {
            StatusText.Text = $"{data.AllListNames.Count} text list(s) loaded ({string.Join(", ", data.Languages)}).";
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        else
        {
            StatusText.Text = "No workbook loaded.";
            StatusText.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        }
    }

    private static string NormalizeListName(string value)
    {
        return string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase) ? "" : value ?? "";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
