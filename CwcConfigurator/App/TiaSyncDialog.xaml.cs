using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.App;

public partial class TiaSyncDialog : Window
{
    private readonly CwcProject _project;
    private readonly ITiaSessionService _sessionService;
    private readonly TiaMappingService _mappingService = new();
    private readonly TiaLiveSyncService _liveSyncService;
    private readonly ObservableCollection<TiaPortalProcessInfo> _processes = [];
    private readonly ObservableCollection<string> _discoveredSoftwareNames = [];
    private bool _hasChanges;
    private bool _isBusy;

    public TiaSyncDialog(CwcProject project, ITiaSessionService sessionService)
    {
        InitializeComponent();
        _project = project;
        _sessionService = sessionService;
        _liveSyncService = new TiaLiveSyncService(sessionService);
        _project.TiaSync ??= new TiaSyncConfig();
        _project.TiaSync.SyncWithDatabases(_project.PlcDatabases, _project.DbCatalog);

        ProcessCombo.ItemsSource = _processes;
        TiaSoftwareColumn.ItemsSource = _discoveredSoftwareNames;
        MappingGrid.ItemsSource = _project.TiaSync.PlcBindings;
        RefreshSessionUi();
        Loaded += async (_, _) => await RefreshProcessesAsync();
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProcessesAsync();
    }

    private async Task RefreshProcessesAsync()
    {
        SetBusy(true, "Scanning running TIA Portal processes...");
        try
        {
            _processes.Clear();
            foreach (var process in await _sessionService.GetRunningPortalProcessesAsync())
            {
                _processes.Add(process);
            }

            ProcessCombo.SelectedIndex = _processes.Count > 0 ? 0 : -1;
            WriteOutput(_processes.Count == 0
                ? "No attachable TIA Portal process found."
                : $"Found {_processes.Count} attachable TIA Portal process(es).");
        }
        catch (Exception ex)
        {
            var message = FormatException(ex);
            WriteOutput(message);
            MessageBox.Show(this, message, "TIA processes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not TiaPortalProcessInfo process)
        {
            MessageBox.Show(this, "Select a running TIA Portal process first.", "TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Attaching to TIA Portal...");
        try
        {
            var session = await _sessionService.AttachToRunningPortalAsync(process.ProcessId);
            _mappingService.StoreProjectBinding(_project, session);
            StoreDiscoveredObjects(session);
            RefreshDiscoveredSoftwareNames(session);
            MappingGrid.ItemsSource = null;
            MappingGrid.ItemsSource = _project.TiaSync.PlcBindings;
            WriteOutput($"Attached to {session.ProjectName}. Mapping is unchanged until you edit and save it. Loaded {session.Plcs.Sum(plc => plc.Objects.Count)} OBJECTS block(s).{Environment.NewLine}{Environment.NewLine}{FormatObjectsSummary(session)}");
        }
        catch (Exception ex)
        {
            var message = FormatException(ex);
            WriteOutput(message);
            MessageBox.Show(this, message, "TIA attach", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Disconnecting from TIA Portal...");
        try
        {
            await _sessionService.DisconnectAsync();
            WriteOutput("Disconnected from TIA Portal.");
        }
        catch (Exception ex)
        {
            WriteOutput(FormatException(ex));
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private void ValidateMapping_Click(object sender, RoutedEventArgs e)
    {
        var issues = _mappingService.Validate(_project, _sessionService.CurrentSession);
        if (issues.Count == 0)
        {
            WriteOutput("Mapping is complete.");
            MessageBox.Show(this, "Mapping is complete.", "TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = string.Join(Environment.NewLine, issues);
        WriteOutput(message);
        MessageBox.Show(this, message, "TIA Sync mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SaveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmAttachedAndMapped())
        {
            return;
        }

        _hasChanges = true;
        DialogResult = true;
    }

    private async void CreateDatabaseFromPlc_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredPlcList.SelectedItem is not TiaDiscoveredPlc plc)
        {
            MessageBox.Show(this, "Select discovered PLC software first.", "TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var databaseName = FirstUniqueDatabaseName(_project, string.IsNullOrWhiteSpace(plc.SoftwareName) ? plc.DeviceName : plc.SoftwareName);
        var database = PlcDatabase.CreateDefault(databaseName);
        _project.PlcDatabases.Add(database);
        _project.TiaSync.SyncWithDatabases(_project.PlcDatabases, _project.DbCatalog);

        var binding = _project.TiaSync.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
        if (binding is not null)
        {
            binding.DeviceName = plc.DeviceName;
            binding.SoftwareName = plc.SoftwareName;
            binding.SyncState = "Mapped";
            binding.Objects = plc.Objects.Select(ToObjectEntry).ToList();
        }

        MappingGrid.ItemsSource = null;
        MappingGrid.ItemsSource = _project.TiaSync.PlcBindings;
        _hasChanges = true;

        SetBusy(true, $"Created PLC database '{database.Name}'. Pulling full database from TIA...");
        try
        {
            var result = await _liveSyncService.PullAsync(_project);
            MappingGrid.ItemsSource = null;
            MappingGrid.ItemsSource = _project.TiaSync.PlcBindings;
            WriteOutput($"Created PLC database '{database.Name}' from discovered PLC '{plc}'.{Environment.NewLine}{Environment.NewLine}{result.ToOutputText()}");
            MessageBox.Show(this, $"PLC database '{database.Name}' created and pulled from TIA.", "TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            var message = FormatException(ex);
            WriteOutput(message);
            MessageBox.Show(this, message, "Create PLC database", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private void DiscoveredPlcList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DiscoveredPlcList.SelectedItem is TiaDiscoveredPlc plc)
        {
            WriteOutput(FormatObjectsSummary(plc));
        }
    }

    private bool ConfirmAttachedAndMapped()
    {
        MappingGrid.CommitEdit();
        MappingGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var issues = _mappingService.Validate(_project, _sessionService.CurrentSession);
        if (issues.Count == 0)
        {
            return true;
        }

        var message = string.Join(Environment.NewLine, issues);
        WriteOutput(message);
        MessageBox.Show(this, message, "TIA Sync mapping", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RefreshSessionUi()
    {
        var session = _sessionService.CurrentSession;
        RefreshProcessesButton.IsEnabled = !_isBusy;
        ProcessCombo.IsEnabled = !_isBusy;
        MappingGrid.IsEnabled = !_isBusy;
        ValidateMappingButton.IsEnabled = !_isBusy;
        if (session is null)
        {
            if (!_isBusy)
            {
                StatusText.Text = "Not attached. Open a TIA Portal project, select the process, then attach.";
            }

            DiscoveredPlcList.ItemsSource = null;
            ObjectsSummaryText.Text = "";
            CreateDatabaseFromPlcButton.IsEnabled = false;
            AttachButton.IsEnabled = !_isBusy;
            DisconnectButton.IsEnabled = false;
            return;
        }

        if (!_isBusy)
        {
            StatusText.Text = $"Attached: {session.ProjectName} | {session.ProjectPath}";
        }

        DiscoveredPlcList.ItemsSource = session.Plcs;
        RefreshDiscoveredSoftwareNames(session);
        ObjectsSummaryText.Text = $"OBJECTS loaded: {session.Plcs.Sum(plc => plc.Objects.Count)} block(s).";
        CreateDatabaseFromPlcButton.IsEnabled = !_isBusy && session.Plcs.Count > 0;
        AttachButton.IsEnabled = false;
        DisconnectButton.IsEnabled = !_isBusy;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }

        RefreshSessionUi();
    }

    private void WriteOutput(string text)
    {
        OutputText.Text = text;
    }

    private void RefreshDiscoveredSoftwareNames(TiaAttachedSession? session)
    {
        _discoveredSoftwareNames.Clear();
        var softwareNames = session is null
            ? Enumerable.Empty<string>()
            : session.Plcs
                .Select(plc => plc.SoftwareName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (var softwareName in softwareNames)
        {
            _discoveredSoftwareNames.Add(softwareName);
        }
    }

    private void StoreDiscoveredObjects(TiaAttachedSession session)
    {
        foreach (var binding in _project.TiaSync.PlcBindings)
        {
            var plc = session.Plcs.FirstOrDefault(item =>
                item.SoftwareName.Equals(binding.SoftwareName, StringComparison.OrdinalIgnoreCase) ||
                item.DeviceName.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (plc is not null)
            {
                binding.Objects = plc.Objects.Select(ToObjectEntry).ToList();
            }
        }
    }

    private static TiaPlcObjectEntry ToObjectEntry(TiaPlcObjectInfo item)
    {
        return new TiaPlcObjectEntry
        {
            Name = item.Name,
            Path = item.Path,
            Kind = item.Kind,
            Number = item.Number,
            ProgrammingLanguage = item.ProgrammingLanguage
        };
    }

    private static string FormatObjectsSummary(TiaAttachedSession session)
    {
        var sections = session.Plcs.Select(FormatObjectsSummary);
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string FormatObjectsSummary(TiaDiscoveredPlc plc)
    {
        if (plc.Objects.Count == 0)
        {
            return $"{plc}: no OBJECTS blocks discovered.";
        }

        var lines = new List<string> { $"{plc}: OBJECTS blocks ({plc.Objects.Count})" };
        lines.AddRange(plc.Objects
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"- {item.Path} ({item.Kind} {item.Number}, {item.ProgrammingLanguage})"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FirstUniqueDatabaseName(CwcProject project, string suggestedName)
    {
        var baseName = string.IsNullOrWhiteSpace(suggestedName) ? "PLC" : suggestedName.Trim();
        var existing = project.PlcDatabases.Select(database => database.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} {index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string FormatException(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            lines.Add(current.Message);
        }

        return string.Join(Environment.NewLine, lines.Distinct());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_hasChanges && DialogResult != true)
        {
            DialogResult = true;
        }

        base.OnClosing(e);
    }
}
