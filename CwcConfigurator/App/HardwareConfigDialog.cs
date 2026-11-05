using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.TiaSync;
using Microsoft.Win32;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → Hardware configuration in TIA: an EPLAN station (CPU or interface module with its modules) is completed
/// by the user — firmware versions, IP address, PROFINET name, subnet, potential groups — then checked against the
/// open TIA project and built there (station, modules in their slots, network). Existing items that differ are
/// reported and never changed or deleted.
/// </summary>
public sealed class HardwareConfigDialog : Window
{
    private readonly CwcProject _project;
    private readonly ITiaSessionService _session;
    private readonly TextBox _epjBox = new() { MinWidth = 420, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _stations = new() { MinWidth = 320, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _processes = new() { MinWidth = 320, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _tiaState = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
    private readonly StackPanel _fields = new();
    private readonly DataGrid _modules = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 160, MaxHeight = 300 };
    private readonly TextBlock _missing = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly DataGrid _results = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120 };
    private readonly Button _read = new() { Content = "Read from TIA (fill versions)", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _check = new() { Content = "Check", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _build = new() { Content = "Build in TIA", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private EplanProject? _eplan;
    private EplanStation? _station;
    private HardwareStationSettings? _plan;

    public HardwareConfigDialog(CwcProject project, ITiaSessionService session)
    {
        _project = project;
        _session = session;
        Title = "Hardware configuration in TIA (from EPLAN)";
        Width = 1050;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel();
        DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
        root.Children.Add(top);

        top.Children.Add(new TextBlock
        {
            Text = "1. EPLAN station — CPU / interface module and its modules in rack order (from the .epj). " +
                   "2. Fill in what EPLAN does not have (firmware versions, IP, PROFINET name…). 3. Check against the open TIA project, then Build. " +
                   "Items already in TIA that differ are reported, never changed or deleted. Back up the TIA project before building.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var epjRow = Row("EPLAN (.epj)");
        _epjBox.Text = project.EplanImport.LastEpjPath;
        epjRow.Children.Add(_epjBox);
        epjRow.Children.Add(SmallButton("Browse…", BrowseEpj));
        epjRow.Children.Add(SmallButton("Load", () => LoadEpj(_epjBox.Text)));
        top.Children.Add(epjRow);

        var stationRow = Row("Station");
        stationRow.Children.Add(_stations);
        _stations.SelectionChanged += (_, _) => ShowStation(_stations.SelectedItem as EplanStation);
        top.Children.Add(stationRow);

        var tiaRow = Row("TIA Portal");
        tiaRow.Children.Add(_processes);
        tiaRow.Children.Add(SmallButton("Refresh", async () => await LoadProcessesAsync()));
        tiaRow.Children.Add(SmallButton("Connect", async () => await ConnectAsync()));
        tiaRow.Children.Add(_tiaState);
        top.Children.Add(tiaRow);

        top.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
        top.Children.Add(_fields);
        top.Children.Add(new TextBlock { Text = "Modules (slot, order number, firmware version, ET 200SP potential group: ticked = new group / light BaseUnit)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        top.Children.Add(_modules);
        top.Children.Add(_missing);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _read.Click += async (_, _) => await Guard(ReadFromTiaAsync);
        _check.Click += async (_, _) => await Guard(() => RunAsync(false));
        _build.Click += async (_, _) => await Guard(() => RunAsync(true));
        buttons.Children.Add(_read);
        buttons.Children.Add(_check);
        buttons.Children.Add(_build);
        var close = new Button { Content = "Close", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        top.Children.Add(buttons);

        _results.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(TiaHardwareStep.Status)), Width = 80 });
        _results.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(TiaHardwareStep.Item)), Width = 220 });
        _results.Columns.Add(new DataGridTextColumn { Header = "Action", Binding = new Binding(nameof(TiaHardwareStep.Action)), Width = 160 });
        _results.Columns.Add(new DataGridTextColumn { Header = "Detail", Binding = new Binding(nameof(TiaHardwareStep.Detail)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _results.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is TiaHardwareStep step)
            {
                e.Row.Background = step.Status switch
                {
                    "Error" => new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2)),
                    "Conflict" => new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)),
                    "Done" => new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)),
                    "Planned" => new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE)),
                    _ => Brushes.White
                };
            }
        };
        root.Children.Add(_results);

        _modules.Columns.Add(new DataGridCheckBoxColumn { Header = "Build", Binding = new Binding(nameof(HardwareModuleSettings.Include)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        _modules.Columns.Add(new DataGridTextColumn { Header = "Slot", Binding = new Binding(nameof(HardwareModuleSettings.Slot)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 50 });
        _modules.Columns.Add(new DataGridTextColumn { Header = "Tag", Binding = new Binding(nameof(HardwareModuleSettings.Tag)), IsReadOnly = true, Width = 70 });
        _modules.Columns.Add(new DataGridTextColumn { Header = "Type (EPLAN)", Binding = new Binding(nameof(HardwareModuleSettings.Type)), IsReadOnly = true, Width = 210 });
        _modules.Columns.Add(new DataGridTextColumn { Header = "Order number", Binding = new Binding(nameof(HardwareModuleSettings.Order)), Width = 180 });
        _modules.Columns.Add(new DataGridTextColumn { Header = "Firmware (e.g. V1.0)", Binding = new Binding(nameof(HardwareModuleSettings.Version)), Width = 130 });
        _modules.Columns.Add(new DataGridCheckBoxColumn { Header = "New potential group", Binding = new Binding(nameof(HardwareModuleSettings.NewPotentialGroup)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        _modules.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdateMissing);
        _modules.CurrentCellChanged += (_, _) => UpdateMissing();

        Content = root;
        Loaded += async (_, _) =>
        {
            if (_epjBox.Text.Trim().Length > 0)
            {
                LoadEpj(_epjBox.Text);
            }

            ShowTiaState();
            await LoadProcessesAsync();
        };
        Closing += (_, _) => SavePlan();
    }

    private static StackPanel Row(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        return row;
    }

    private Button SmallButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void BrowseEpj()
    {
        var dialog = new OpenFileDialog { Filter = "EPLAN export (*.epj)|*.epj|All files (*.*)|*.*", FileName = _epjBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            _epjBox.Text = dialog.FileName;
            LoadEpj(dialog.FileName);
        }
    }

    private void LoadEpj(string path)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _eplan = EplanProjectReader.Read(path.Trim());
            _stations.ItemsSource = _eplan.Stations;
            _stations.DisplayMemberPath = nameof(EplanStation.DisplayName);
            _stations.SelectedIndex = _eplan.Stations.Count > 0 ? 0 : -1;
            if (_eplan.Stations.Count == 0)
            {
                MessageBox.Show(this, "The EPLAN export has no PLC station (CPU / interface module).", Title);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The EPLAN export could not be read: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ShowStation(EplanStation? station)
    {
        SavePlan();
        _station = station;
        _plan = station is null ? null : TiaHardwarePlanner.PlanFor(_project, station);
        _fields.Children.Clear();
        _results.ItemsSource = null;
        if (_plan is null || station is null)
        {
            _modules.ItemsSource = null;
            UpdateMissing();
            return;
        }

        var head = station.IsCpu ? "CPU" : "Interface module";
        var grid = new Grid();
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(170) : new GridLength(1, GridUnitType.Star) });
        }

        var fields = new List<(string Label, string Property, string Hint)>
        {
            ("TIA device (station)", nameof(HardwareStationSettings.DeviceName), "Name of the station in TIA; an existing station is completed, a new one is created."),
            ($"{head} name", nameof(HardwareStationSettings.HeadName), station.IsCpu ? "The CPU name is also the PLC software name." : "Name of the interface module."),
            ($"{head} order number", nameof(HardwareStationSettings.HeadOrder), $"EPLAN: {station.HeadPartNumber} ({station.HeadType})"),
            ($"{head} firmware", nameof(HardwareStationSettings.HeadVersion), "e.g. V3.0 — as in the TIA catalogue"),
            ("Bus adapter order number", nameof(HardwareStationSettings.BusAdapterOrder), "ET 200SP, e.g. 6ES7 193-6AR00-0AA0 (BA 2xRJ45); empty = none"),
            ("Bus adapter firmware", nameof(HardwareStationSettings.BusAdapterVersion), "e.g. V0.0"),
            ("IP address", nameof(HardwareStationSettings.Ip), "From the PLC database when known"),
            ("PROFINET device name", nameof(HardwareStationSettings.PnDeviceName), "lower case, e.g. intake"),
            ("Subnet", nameof(HardwareStationSettings.Subnet), "Existing subnet of the TIA project, or a new one"),
            station.IsCpu
                ? ("IO system", nameof(HardwareStationSettings.IoSystem), "PROFINET IO system of the CPU (created when missing)")
                : ("IO controller (TIA device)", nameof(HardwareStationSettings.IoControllerDevice), "The CPU station whose IO system this station joins")
        };
        for (var i = 0; i < fields.Count; i++)
        {
            var (label, property, hint) = fields[i];
            var row = i / 2;
            if (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) };
            var box = new TextBox { Margin = new Thickness(0, 2, 14, 2), ToolTip = hint };
            box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = _plan, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            box.TextChanged += (_, _) => UpdateMissing();
            Grid.SetRow(text, row);
            Grid.SetColumn(text, (i % 2) * 2);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, (i % 2) * 2 + 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }

        _fields.Children.Add(grid);
        _modules.ItemsSource = _plan.Modules;
        UpdateMissing();
    }

    private void UpdateMissing()
    {
        if (_plan is null || _station is null)
        {
            _missing.Text = "Load an EPLAN export and choose a station.";
            _check.IsEnabled = _build.IsEnabled = _read.IsEnabled = false;
            return;
        }

        var missing = TiaHardwarePlanner.Missing(_plan, _station.IsCpu);
        var connected = _session.CurrentSession is not null;
        _missing.Text = missing.Count == 0 ? "" : "Fill in: " + string.Join(" · ", missing);
        _read.IsEnabled = connected;
        _check.IsEnabled = connected && missing.Count == 0;
        _build.IsEnabled = connected && missing.Count == 0;
        if (!connected)
        {
            _missing.Text += (_missing.Text.Length > 0 ? "\n" : "") + "Connect to the TIA Portal project to check and build.";
        }
    }

    private void SavePlan()
    {
        if (_plan is not null)
        {
            TiaHardwarePlanner.Save(_project, _plan);
            if (_epjBox.Text.Trim().Length > 0)
            {
                _project.EplanImport.LastEpjPath = _epjBox.Text.Trim();
            }
        }
    }

    private void ShowTiaState()
    {
        var session = _session.CurrentSession;
        _tiaState.Text = session is null ? "not connected" : $"connected: {session.ProjectName} ({session.PortalVersion})";
        UpdateMissing();
    }

    private async Task LoadProcessesAsync()
    {
        try
        {
            var processes = await _session.GetRunningPortalProcessesAsync();
            _processes.ItemsSource = processes;
            _processes.SelectedIndex = processes.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            _tiaState.Text = ex.Message;
        }
    }

    private async Task ConnectAsync()
    {
        if (_processes.SelectedItem is not TiaPortalProcessInfo process)
        {
            MessageBox.Show(this, "Open the project in TIA Portal and press Refresh.", Title);
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _tiaState.Text = "connecting… (confirm the Openness access in TIA Portal if it asks)";
            await _session.AttachToRunningPortalAsync(process.ProcessId, process.Version);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ShowTiaState();
        }
    }

    private async Task ReadFromTiaAsync()
    {
        if (_plan is null)
        {
            return;
        }

        var hardware = await _session.ReadHardwareAsync(_plan.DeviceName.Trim());
        if (!hardware.Found)
        {
            _results.ItemsSource = new[] { new TiaHardwareStep { Status = "Planned", Item = _plan.DeviceName, Action = "Station", Detail = "not in TIA yet — it will be created. Devices in TIA: " + string.Join(", ", hardware.Devices) } };
            return;
        }

        var filled = TiaHardwarePlanner.TakeVersions(_plan, hardware);
        var rows = hardware.Items.Where(TiaHardwarePlanner.IsSlotItem).OrderBy(item => item.Position)
            .Select(item => new TiaHardwareStep { Status = "Exists", Item = $"slot {item.Position}: {item.Name}", Action = "In TIA", Detail = item.TypeIdentifier })
            .Concat(hardware.Interfaces.Select(network => new TiaHardwareStep { Status = "Exists", Item = network.Item, Action = "Network", Detail = $"IP {network.Address} · PROFINET name {network.PnDeviceName} · subnet {network.Subnet} · {network.IoSystem}" }))
            .ToList();
        _results.ItemsSource = rows;
        ShowStation(_station); // shows the filled fields
        _results.ItemsSource = rows;
        MessageBox.Show(this, $"{filled} firmware version(s) / field(s) taken from the station in TIA (same slot and order number).", Title);
    }

    private async Task RunAsync(bool apply)
    {
        if (_plan is null || _station is null)
        {
            return;
        }

        SavePlan();
        if (apply && MessageBox.Show(this,
                $"Build the station '{_plan.DeviceName}' in the open TIA project?\n\nMissing modules are plugged, the network is set; items that differ stay unchanged. Back up the TIA project first.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = await _session.BuildHardwareAsync(TiaHardwarePlanner.Request(_plan, _station.IsCpu, apply));
            _results.ItemsSource = result.Steps;
            var errors = result.Steps.Count(step => step.Status == "Error");
            var conflicts = result.Steps.Count(step => step.Status == "Conflict");
            var summary = apply
                ? $"Done: {result.Steps.Count(step => step.Status == "Done")} change(s) in TIA, {conflicts} conflict(s), {errors} error(s)."
                : $"Check: {result.Steps.Count(step => step.Status == "Planned")} change(s) would be made, {conflicts} conflict(s), {errors} error(s).";
            Title = "Hardware configuration in TIA (from EPLAN) — " + summary;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
