using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.App;

public partial class TiaSyncDialog : Window
{
    private readonly CwcProject _project;
    private readonly ITiaSessionService _sessionService;
    private readonly TiaMappingService _mappingService = new();
    private readonly TiaLiveSyncService _liveSyncService;
    private readonly ObservableCollection<TiaPortalProcessInfo> _processes = [];
    private readonly ObservableCollection<MappingRow> _rows = [];
    private readonly ObservableCollection<ScadaMappingRow> _scadaRows = [];
    private Dictionary<string, MappingSnapshot> _savedMapping = new();
    private bool _databasesCreated;
    private bool _isBusy;

    public ObservableCollection<string> DeviceNames { get; } = [];
    public ObservableCollection<string> SoftwareNames { get; } = [];
    public ObservableCollection<string> UnifiedDeviceNames { get; } = [];

    private sealed record MappingSnapshot(string DeviceName, string SoftwareName, bool IncludeSequence);

    public TiaSyncDialog(CwcProject project, ITiaSessionService sessionService)
    {
        InitializeComponent();
        _project = project;
        _sessionService = sessionService;
        _liveSyncService = new TiaLiveSyncService(sessionService);
        _project.TiaSync ??= new TiaSyncConfig();
        _project.TiaSync.SyncWithDatabases(_project.PlcDatabases, _project.DbCatalog);

        ProcessCombo.ItemsSource = _processes;
        MappingGrid.ItemsSource = _rows;
        ScadaMappingGrid.ItemsSource = _scadaRows;
        RebuildMappingRows();
        RebuildScadaMappingRows();
        TakeMappingSnapshot();
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
            var session = await _sessionService.AttachToRunningPortalAsync(process.ProcessId, process.Version);
            _mappingService.StoreProjectBinding(_project, session);

            // Attach is the moment the user finally knows what the TIA project
            // contains, so resolve every binding that does not point at a real
            // TIA PLC automatically; valid mappings stay untouched.
            var autoMapped = _mappingService.AutoMapUnresolved(_project, session);

            StoreDiscoveredObjects(session);
            RefreshDiscoveredNames(session);
            RebuildMappingRows();
            await RefreshUnifiedDevicesAsync();
            var scadaMapped = AutoMapScada();

            var autoMapNote = autoMapped > 0
                ? $"Auto-mapped {autoMapped} PLC database(s) by name — review the mapping and save."
                : "Existing mapping left unchanged.";
            if (UnifiedDeviceNames.Count > 0)
            {
                autoMapNote += $" WinCC Unified devices: {string.Join(", ", UnifiedDeviceNames)}" +
                               (scadaMapped > 0 ? $"; auto-mapped {scadaMapped} SCADA database(s)." : ".");
            }
            WriteOutput($"Attached to {session.ProjectName}. {autoMapNote} Loaded {session.Plcs.Sum(plc => plc.Objects.Count)} OBJECTS block(s).{Environment.NewLine}{Environment.NewLine}{FormatObjectsSummary(session)}");
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
            UnifiedDeviceNames.Clear();
            UpdateScadaRowStates();
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

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal first — auto-map needs the list of discovered PLCs.", "Auto-map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MappingGrid.CommitEdit();
        var mapped = _mappingService.AutoMapUnresolved(_project, session);
        RebuildMappingRows();
        WriteOutput(mapped > 0
            ? $"Auto-mapped {mapped} PLC database(s) by name."
            : "Nothing to auto-map: every PLC database already points at an existing TIA PLC, or no name match was found.");
    }

    private void ValidateMapping_Click(object sender, RoutedEventArgs e)
    {
        MappingGrid.CommitEdit();
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

        foreach (var row in _rows.Where(item => item.State == "OK"))
        {
            row.Binding.SyncState = "Mapped";
        }

        TakeMappingSnapshot();
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

        RebuildMappingRows();
        _databasesCreated = true;

        SetBusy(true, $"Created PLC database '{database.Name}'. Pulling full database from TIA...");
        try
        {
            var result = await _liveSyncService.PullAsync(_project);
            RebuildMappingRows();
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
            // Creation (and the pull) is committed to the project immediately,
            // so it survives Cancel; only later manual edits are revertable.
            TakeMappingSnapshot();
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
        AutoMapButton.IsEnabled = !_isBusy && session is not null;
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
            UpdateRowStates();
            return;
        }

        if (!_isBusy)
        {
            StatusText.Text = $"Attached: {session.ProjectName} | {session.ProjectPath}";
        }

        DiscoveredPlcList.ItemsSource = session.Plcs;
        RefreshDiscoveredNames(session);
        ObjectsSummaryText.Text = $"OBJECTS loaded: {session.Plcs.Sum(plc => plc.Objects.Count)} block(s).";
        CreateDatabaseFromPlcButton.IsEnabled = !_isBusy && session.Plcs.Count > 0;
        AttachButton.IsEnabled = false;
        DisconnectButton.IsEnabled = !_isBusy;
        UpdateRowStates();
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

    private void RebuildMappingRows()
    {
        _rows.Clear();
        foreach (var binding in _project.TiaSync.PlcBindings)
        {
            _rows.Add(new MappingRow(binding, UpdateRowStates));
        }

        UpdateRowStates();
    }

    private async Task RefreshUnifiedDevicesAsync()
    {
        UnifiedDeviceNames.Clear();
        try
        {
            var result = await _sessionService.GetUnifiedDevicesAsync();
            foreach (var device in result.Devices)
            {
                UnifiedDeviceNames.Add(device);
            }
        }
        catch
        {
            // V18 / no WinCC Unified in this project — leave the device list empty.
        }

        RebuildScadaMappingRows();
    }

    private void RebuildScadaMappingRows()
    {
        _project.TiaSync ??= new TiaSyncConfig();
        _project.TiaSync.SyncWithScadaDatabases(_project.ScadaDatabases);
        _scadaRows.Clear();
        foreach (var binding in _project.TiaSync.ScadaBindings)
        {
            _scadaRows.Add(new ScadaMappingRow(binding, UpdateScadaRowStates));
        }

        UpdateScadaRowStates();
    }

    // ---- SCADA: WinCC Unified devices ↔ SCADA databases (AUX_FUNCTIONS._SET_INTERNAL_TAG + screens) ----

    /// <summary>Maps SCADA databases without a device to the Unified device of the same name, or to the only one.</summary>
    private int AutoMapScada()
    {
        if (UnifiedDeviceNames.Count == 0)
        {
            return 0;
        }

        var mapped = 0;
        foreach (var row in _scadaRows.Where(row => string.IsNullOrWhiteSpace(row.DeviceName)))
        {
            var used = _scadaRows.Select(item => item.DeviceName).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var free = UnifiedDeviceNames.Where(name => !used.Contains(name)).ToList();
            var match = free.FirstOrDefault(name => name.Equals(row.ScadaName, StringComparison.OrdinalIgnoreCase))
                        ?? (free.Count == 1 && _scadaRows.Count(item => string.IsNullOrWhiteSpace(item.DeviceName)) == 1 ? free[0] : null);
            if (match is not null)
            {
                row.DeviceName = match;
                mapped++;
            }
        }

        UpdateScadaRowStates();
        return mapped;
    }

    private void AutoMapScada_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUnifiedDevices())
        {
            return;
        }

        ScadaMappingGrid.CommitEdit();
        var mapped = AutoMapScada();
        WriteOutput(mapped > 0 ? $"Auto-mapped {mapped} SCADA database(s)." : "Nothing to auto-map: no SCADA database without a device has a matching Unified device.");
    }

    private async void CreateScadaDatabases_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUnifiedDevices())
        {
            return;
        }

        ScadaMappingGrid.CommitEdit();
        var mapped = _project.TiaSync.ScadaBindings.Select(binding => binding.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = UnifiedDeviceNames.Where(name => !mapped.Contains(name)).ToList();
        if (missing.Count == 0)
        {
            WriteOutput("Every WinCC Unified device already has a SCADA database.");
            return;
        }

        var created = new List<ScadaDatabase>();
        foreach (var device in missing)
        {
            var database = ScadaDatabase.CreateDefault();
            database.Name = UniqueScadaName(device);
            _project.ScadaDatabases.Add(database);
            _project.TiaSync.SyncWithScadaDatabases(_project.ScadaDatabases);
            _project.TiaSync.ScadaBindings.First(binding => binding.ScadaDatabaseId == database.Id).DeviceName = device;
            created.Add(database);
        }

        if (string.IsNullOrWhiteSpace(_project.ActiveScadaDatabaseId) || _project.ScadaDatabases.All(database => database.Id != _project.ActiveScadaDatabaseId))
        {
            _project.ActiveScadaDatabaseId = created[0].Id;
        }

        _databasesCreated = true;
        RebuildScadaMappingRows();
        await PullScadaAsync(created.Select(database => database.Id).ToHashSet());
        TakeMappingSnapshot();
    }

    private string UniqueScadaName(string baseName)
    {
        var name = baseName;
        for (var index = 2; _project.ScadaDatabases.Any(database => database.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); index++)
        {
            name = $"{baseName} {index}";
        }

        return name;
    }

    private async void PullScada_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUnifiedDevices())
        {
            return;
        }

        ScadaMappingGrid.CommitEdit();
        await PullScadaAsync(null);
        TakeMappingSnapshot();
    }

    /// <summary>Pulls the mapped SCADA databases (all, or only <paramref name="ids"/>): script into the database, plus screens.</summary>
    private async Task PullScadaAsync(HashSet<string>? ids)
    {
        var rows = _scadaRows.Where(row => row.State == "OK" && (ids is null || ids.Contains(row.Binding.ScadaDatabaseId))).ToList();
        if (rows.Count == 0)
        {
            WriteOutput("No SCADA database is mapped to a WinCC Unified device of the attached project.");
            return;
        }

        var service = new ScadaUnifiedSyncService();
        var lines = new List<string>();
        SetBusy(true, "Pulling SCADA from TIA...");
        try
        {
            foreach (var row in rows)
            {
                try
                {
                    var result = await service.PullFromTiaAsync(_sessionService, row.DeviceName);
                    var index = _project.ScadaDatabases.FindIndex(database => database.Id == row.Binding.ScadaDatabaseId);
                    if (index < 0)
                    {
                        continue;
                    }

                    var pulled = result.Database;
                    pulled.Id = _project.ScadaDatabases[index].Id;
                    pulled.Name = _project.ScadaDatabases[index].Name;
                    pulled.LastImportedAtUtc = DateTime.UtcNow.ToString("O");
                    _project.ScadaDatabases[index] = pulled;
                    if (_project.Scada?.Id == pulled.Id)
                    {
                        _project.Scada = pulled;
                    }

                    row.Binding.SyncState = "Pulled";
                    lines.Add($"{row.ScadaName}: {result.Message} {pulled.Variables.Count} variables, {pulled.TagWrites.Count} tag writes.");
                }
                catch (Exception ex)
                {
                    lines.Add($"{row.ScadaName}: {FormatException(ex)}");
                }
            }

            _databasesCreated = true;
            WriteOutput(string.Join(Environment.NewLine, lines));
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private async void PushScada_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUnifiedDevices())
        {
            return;
        }

        ScadaMappingGrid.CommitEdit();
        var rows = _scadaRows.Where(row => row.State == "OK").ToList();
        var empty = rows.Where(row => _project.ScadaDatabases.FirstOrDefault(database => database.Id == row.Binding.ScadaDatabaseId)?.Variables.Count is null or 0).ToList();
        rows = rows.Except(empty).ToList();
        if (rows.Count == 0)
        {
            WriteOutput("Nothing to push: no mapped SCADA database with data (pull or import it first).");
            return;
        }

        var targets = string.Join(Environment.NewLine, rows.Select(row => $"  {row.ScadaName} → {row.DeviceName}"));
        if (MessageBox.Show(this,
                $"Write {ScadaUnifiedSyncService.ModuleName}.{ScadaInternalTagModule.DefaultFunctionName} into:{Environment.NewLine}{targets}{Environment.NewLine}{Environment.NewLine}" +
                "Only that function is replaced; the rest of the module stays. The TIA project is not saved — check it in TIA and save it there.",
                "Push SCADA to TIA", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var service = new ScadaUnifiedSyncService();
        var lines = empty.Select(row => $"{row.ScadaName}: skipped (empty database).").ToList();
        SetBusy(true, "Pushing SCADA to TIA...");
        try
        {
            foreach (var row in rows)
            {
                try
                {
                    var database = _project.ScadaDatabases.First(item => item.Id == row.Binding.ScadaDatabaseId);
                    lines.Add($"{row.ScadaName}: {await service.PushToTiaAsync(_sessionService, database, row.DeviceName)}");
                    row.Binding.SyncState = "Pushed";
                }
                catch (Exception ex)
                {
                    lines.Add($"{row.ScadaName}: {FormatException(ex)}");
                }
            }

            WriteOutput(string.Join(Environment.NewLine, lines));
        }
        finally
        {
            SetBusy(false);
            RefreshSessionUi();
        }
    }

    private bool RequireUnifiedDevices()
    {
        if (_sessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to the TIA Portal project first.", "SCADA", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (UnifiedDeviceNames.Count == 0)
        {
            MessageBox.Show(this, "No WinCC Unified device was found in the attached project (WinCC Unified needs the TIA V21 helper).",
                "SCADA", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void UpdateScadaRowStates()
    {
        foreach (var row in _scadaRows)
        {
            row.State = ComputeScadaRowState(row);
        }
    }

    private string ComputeScadaRowState(ScadaMappingRow row)
    {
        if (string.IsNullOrWhiteSpace(row.DeviceName))
        {
            return "Not mapped";
        }

        if (_sessionService.CurrentSession is null || UnifiedDeviceNames.Count == 0)
        {
            return "Mapped"; // can't verify against TIA until attached / devices fetched
        }

        return UnifiedDeviceNames.Any(name => name.Equals(row.DeviceName, StringComparison.OrdinalIgnoreCase))
            ? "OK"
            : "Not found in TIA";
    }

    private void UpdateRowStates()
    {
        var session = _sessionService.CurrentSession;
        var duplicates = _rows
            .Where(row => !string.IsNullOrWhiteSpace(row.SoftwareName))
            .GroupBy(row => $"{row.DeviceName.Trim()}|{row.SoftwareName.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();

        foreach (var row in _rows)
        {
            row.State = ComputeRowState(row, session, duplicates.Contains(row));
        }
    }

    private static string ComputeRowState(MappingRow row, TiaAttachedSession? session, bool isDuplicate)
    {
        if (string.IsNullOrWhiteSpace(row.DeviceName) || string.IsNullOrWhiteSpace(row.SoftwareName))
        {
            return "Missing TIA name";
        }

        if (isDuplicate)
        {
            return "Duplicate mapping";
        }

        if (session is null)
        {
            return "Not attached";
        }

        return TiaMappingService.PairExists(session, row.DeviceName, row.SoftwareName)
            ? "OK"
            : "Not found in TIA";
    }

    private void TakeMappingSnapshot()
    {
        _savedMapping = _project.TiaSync.PlcBindings.ToDictionary(
            binding => binding.PlcDatabaseId,
            binding => new MappingSnapshot(binding.DeviceName, binding.SoftwareName, binding.IncludeSequenceInGlobalSync));
    }

    private bool HasUnsavedMappingChanges()
    {
        foreach (var binding in _project.TiaSync.PlcBindings)
        {
            if (!_savedMapping.TryGetValue(binding.PlcDatabaseId, out var saved)
                || !saved.DeviceName.Equals(binding.DeviceName, StringComparison.Ordinal)
                || !saved.SoftwareName.Equals(binding.SoftwareName, StringComparison.Ordinal)
                || saved.IncludeSequence != binding.IncludeSequenceInGlobalSync)
            {
                return true;
            }
        }

        return false;
    }

    private void RestoreMappingSnapshot()
    {
        foreach (var binding in _project.TiaSync.PlcBindings)
        {
            if (_savedMapping.TryGetValue(binding.PlcDatabaseId, out var saved))
            {
                binding.DeviceName = saved.DeviceName;
                binding.SoftwareName = saved.SoftwareName;
                binding.IncludeSequenceInGlobalSync = saved.IncludeSequence;
            }
        }
    }

    private void RefreshDiscoveredNames(TiaAttachedSession? session)
    {
        DeviceNames.Clear();
        SoftwareNames.Clear();
        if (session is null)
        {
            return;
        }

        foreach (var name in session.Plcs
                     .Select(plc => plc.DeviceName)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            DeviceNames.Add(name);
        }

        foreach (var name in session.Plcs
                     .Select(plc => plc.SoftwareName)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            SoftwareNames.Add(name);
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
        if (DialogResult != true)
        {
            MappingGrid.CommitEdit();
            if (HasUnsavedMappingChanges())
            {
                var answer = MessageBox.Show(
                    this,
                    "The mapping has unsaved changes. Close without saving them?",
                    "TIA Sync",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    base.OnClosing(e);
                    return;
                }

                RestoreMappingSnapshot();
            }

            // Database creation is committed immediately, so the main window
            // must rebind even when mapping edits were discarded.
            if (_databasesCreated)
            {
                DialogResult = true;
            }
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Editable wrapper over <see cref="TiaPlcBinding"/> (a plain POCO) so the
    /// grid can both write through immediately and reflect the live per-row
    /// mapping state without rebinding the whole ItemsSource.
    /// </summary>
    private sealed class ScadaMappingRow : INotifyPropertyChanged
    {
        private readonly Action _mappingChanged;
        private string _state = "";

        public ScadaMappingRow(TiaScadaBinding binding, Action mappingChanged)
        {
            Binding = binding;
            _mappingChanged = mappingChanged;
        }

        public TiaScadaBinding Binding { get; }

        public string ScadaName => Binding.ScadaName;

        public string DeviceName
        {
            get => Binding.DeviceName;
            set
            {
                if (!string.Equals(Binding.DeviceName, value, StringComparison.Ordinal))
                {
                    Binding.DeviceName = value ?? "";
                    OnPropertyChanged();
                    _mappingChanged();
                }
            }
        }

        public string State
        {
            get => _state;
            set
            {
                if (!string.Equals(_state, value, StringComparison.Ordinal))
                {
                    _state = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class MappingRow : INotifyPropertyChanged
    {
        private readonly Action _mappingChanged;
        private string _state = "";

        public MappingRow(TiaPlcBinding binding, Action mappingChanged)
        {
            Binding = binding;
            _mappingChanged = mappingChanged;
        }

        public TiaPlcBinding Binding { get; }

        public string PlcName => Binding.PlcName;

        public string DeviceName
        {
            get => Binding.DeviceName;
            set
            {
                if (!string.Equals(Binding.DeviceName, value, StringComparison.Ordinal))
                {
                    Binding.DeviceName = value ?? "";
                    OnPropertyChanged();
                    _mappingChanged();
                }
            }
        }

        public string SoftwareName
        {
            get => Binding.SoftwareName;
            set
            {
                if (!string.Equals(Binding.SoftwareName, value, StringComparison.Ordinal))
                {
                    Binding.SoftwareName = value ?? "";
                    OnPropertyChanged();
                    _mappingChanged();
                }
            }
        }

        public bool IncludeSequenceInGlobalSync
        {
            get => Binding.IncludeSequenceInGlobalSync;
            set
            {
                if (Binding.IncludeSequenceInGlobalSync != value)
                {
                    Binding.IncludeSequenceInGlobalSync = value;
                    OnPropertyChanged();
                }
            }
        }

        public string State
        {
            get => _state;
            set
            {
                if (!string.Equals(_state, value, StringComparison.Ordinal))
                {
                    _state = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
