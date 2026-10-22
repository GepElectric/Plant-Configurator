using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App;

/// <summary>One PLC database offered by the simulator.</summary>
public sealed class SimulatorPlcOption
{
    public required string Name { get; init; }
    public required CwcProject Project { get; init; }
    public required string SettingsKey { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Tools → Simulator. Signals: reads and writes the simulator DB (DB30) of each PLC over S7 and lets the user set
/// DI/AI/RTD/COUNTER and watch DO/AO. Program / SCADA: the plant model (objects per PLC, linked across PLCs) that
/// computes the inputs from the PLC outputs. The PLC uses DB30 only in total simulation (PLC_CONFIGURATION.RUN_MODE = 2).
/// </summary>
public partial class SimulatorWindow : Window
{
    public const string CommandLineSwitch = "--simulator";

    private static readonly TimeSpan CycleTime = TimeSpan.FromMilliseconds(100);
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));

    private readonly List<PlcSession> _sessions;
    private readonly SimulationController _controller;
    private readonly DispatcherTimer _timer = new() { Interval = CycleTime };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<TextBox> _constantBoxes = [];
    private PlcSession? _session;
    private ICollectionView? _view;
    private bool _cycleRunning;
    private int _tick;

    public SimulatorWindow(IReadOnlyList<SimulatorPlcOption> options, string? activeName, string? modelPath)
    {
        InitializeComponent();
        foreach (var name in SimulatorLayout.ConstantNames)
        {
            var box = new TextBox { Width = 40, VerticalContentAlignment = VerticalAlignment.Center };
            _constantBoxes.Add(box);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
            item.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            item.Children.Add(box);
            ConstantsPanel.Children.Add(item);
        }

        DeviceTypeColumn.ItemsSource = Enum.GetValues<DeviceType>();
        DevicePresetBox.ItemsSource = DevicePresets;
        DevicePresetBox.SelectedIndex = 0;
        _sessions = options.Select(option => new PlcSession(option.Name, option.Project, option.SettingsKey)).ToList();
        _controller = new SimulationController(_sessions, modelPath);
        ProgramPanel.Attach(_controller);
        ScadaPanel.Attach(_controller);
        foreach (var session in _sessions)
        {
            PlcSelector.Items.Add(session);
        }

        PlcSelector.SelectedItem = _sessions.FirstOrDefault(session => session.Name == activeName) ?? _sessions.FirstOrDefault();
        if (_controller.Engine.Problems.Count > 0)
        {
            StatusText.Text = "Model: " + string.Join(" · ", _controller.Engine.Problems.Take(3));
        }

        _timer.Tick += async (_, _) => await CycleAsync();
        _timer.Start();
        UpdateRunButton();
    }

    /// <summary>One option per PLC database: each PLC has its own hardware tables and simulator DB.</summary>
    public static List<SimulatorPlcOption> CreateOptions(CwcProject project, string projectKey)
    {
        var options = project.PlcDatabases.Select(database =>
        {
            var scoped = new CwcProject();
            database.ApplyTo(scoped);
            return new SimulatorPlcOption { Name = database.Name, Project = scoped, SettingsKey = projectKey + "|" + database.Name };
        }).ToList();
        if (options.Count == 0)
        {
            options.Add(new SimulatorPlcOption { Name = "PLC", Project = project, SettingsKey = projectKey + "|PLC" });
        }

        return options;
    }

    /// <summary>
    /// Stand-alone start (PlantConfigurator.exe --simulator [project.plantcfg]): only the simulator window,
    /// for the given project or one picked in a file dialog. Returns null when no project was chosen.
    /// </summary>
    public static SimulatorWindow? CreateStandalone(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Simulator – open Plant Configurator project",
                Filter = "Plant Configurator project (*.plantcfg)|*.plantcfg|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            projectPath = dialog.FileName;
        }

        var fullPath = System.IO.Path.GetFullPath(projectPath);
        var project = new Services.ProjectFileService().Load(fullPath);
        var active = project.PlcDatabases.FirstOrDefault(database => database.Id == project.ActivePlcDatabaseId)?.Name;
        var window = new SimulatorWindow(CreateOptions(project, fullPath), active, ProcessModelStore.PathFor(fullPath))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Title = $"Simulator – {System.IO.Path.GetFileName(fullPath)}";
        return window;
    }

    private void PlcSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlcSelector.SelectedItem is not PlcSession session)
        {
            return;
        }

        _session = session;
        IpBox.Text = session.Settings.IpAddress;
        RackBox.Text = session.Settings.Rack.ToString(CultureInfo.InvariantCulture);
        SlotBox.Text = session.Settings.Slot.ToString(CultureInfo.InvariantCulture);
        DbBox.Text = session.Settings.DbNumber.ToString(CultureInfo.InvariantCulture);
        var values = session.Layout.ToConstants();
        for (var index = 0; index < _constantBoxes.Count; index++)
        {
            _constantBoxes[index].Text = values[index].ToString(CultureInfo.InvariantCulture);
        }

        LayoutExpander.IsExpanded = session.Settings.Constants is null;
        ShowDevices(session.Devices, session.DevicesDbNumber);
        ShowSignals();
        UpdateConnectionUi();
        ProgramPanel.ShowPlc(session.Name);
        ScadaPanel.ShowPlc(session.Name);
        if (!session.IsConnected)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(session.Settings.IpAddress)
                ? "Enter the PLC IP address and connect. PUT/GET access must be allowed in the PLC."
                : $"{session.Name}: ready to connect.";
        }
    }

    private void ShowSignals()
    {
        if (_session is null)
        {
            return;
        }

        _view = CollectionViewSource.GetDefaultView(_session.Signals);
        _view.Filter = item => item is SimulatorSignal signal && Matches(signal);
        SignalsGrid.ItemsSource = _view;
        LayoutText.Text = $"{_session.Name} simulator DB: {_session.Layout.Describe()}. " +
                          "The PLC reads DI/AI/RTD/COUNTER from this DB and writes DO/AO into it only in total simulation (PLC_CONFIGURATION.RUN_MODE = 2).";
        UpdateSummary();
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var values = new List<int>();
        foreach (var box in _constantBoxes)
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 255)
            {
                StatusText.Text = "Every constant must be a whole number from 0 to 255.";
                box.Focus();
                return;
            }

            values.Add(value);
        }

        _session.Disconnect();
        _session.ApplyLayout(SimulatorLayout.FromConstants(values));
        _session.SaveSettings();
        ShowSignals();
        UpdateConnectionUi();
        StatusText.Text = $"{_session.Name}: layout applied: {_session.Layout.Describe()}.";
    }

    /// <summary>Devices that can be added (names and IDs of our standard PLC program).</summary>
    private static readonly List<DeviceDefinition> DevicePresets =
    [
        new() { Name = "GEN_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 48, FirstEnergyId = 64 },
        new() { Name = "AUX_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 216, FirstEnergyId = 232 },
        new() { Name = "LINE_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 120, FirstEnergyId = 136 },
        new() { Name = "AUX_DIZEL_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 216, FirstEnergyId = 232 },
        new() { Name = "AUX_GRID_SENTRON", Type = DeviceType.Measurement, FirstMeasId = 216, FirstEnergyId = 232 },
        new() { Name = "GEN_PROT", Type = DeviceType.Protection, FirstMeasId = 72, FirstEnergyId = 0 },
        new() { Name = "LINE_PROT", Type = DeviceType.Protection, FirstMeasId = 144, FirstEnergyId = 164 },
        new() { Name = "TRAFO_PROT", Type = DeviceType.TrafoProtection, FirstMeasId = 120, FirstEnergyId = 0 },
        new() { Name = "EXC", Type = DeviceType.Excitation, FirstMeasId = 96, FirstEnergyId = 0 }
    ];

    private System.Collections.ObjectModel.ObservableCollection<DeviceDefinition> _devices = [];

    private void ShowDevices(IEnumerable<DeviceDefinition> devices, int dbNumber)
    {
        _devices = new(devices.Select(device => device.Clone()));
        DevicesGrid.ItemsSource = _devices;
        DevicesDbBox.Text = dbNumber.ToString(CultureInfo.InvariantCulture);
        UpdateDevicesInfo();
    }

    private void UpdateDevicesInfo()
    {
        var bytes = DeviceLayout.TotalBytes(_devices);
        DevicesInfoText.Text = _devices.Count == 0
            ? "No devices: the devices DB is not used."
            : $"{_devices.Count} devices, {bytes} bytes (the standard unit DB27 has 552). MEAS[i] of a device is copied by the PLC to FIRST MEAS ID + i, " +
              "ENERGY[i] to FIRST ENERGY ID + i. Bind model outputs to them as DEV:<MEAS ID>.";
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicePresetBox.SelectedItem is DeviceDefinition preset)
        {
            _devices.Add(preset.Clone());
            UpdateDevicesInfo();
        }
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is DeviceDefinition device)
        {
            _devices.Remove(device);
            UpdateDevicesInfo();
        }
    }

    private void MoveDeviceUp_Click(object sender, RoutedEventArgs e) => MoveDevice(-1);

    private void MoveDeviceDown_Click(object sender, RoutedEventArgs e) => MoveDevice(1);

    private void MoveDevice(int step)
    {
        if (DevicesGrid.SelectedItem is not DeviceDefinition device)
        {
            return;
        }

        var index = _devices.IndexOf(device);
        var target = index + step;
        if (target >= 0 && target < _devices.Count)
        {
            _devices.Move(index, target);
            DevicesGrid.SelectedItem = device;
        }
    }

    private void DeviceTemplate_Click(object sender, RoutedEventArgs e)
    {
        _devices = new(DeviceLayout.UnitTemplate());
        DevicesGrid.ItemsSource = _devices;
        UpdateDevicesInfo();
    }

    private void ApplyDevices_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        DevicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!int.TryParse(DevicesDbBox.Text.Trim(), out var dbNumber) || dbNumber <= 0)
        {
            StatusText.Text = "Enter a valid devices DB number.";
            return;
        }

        if (_devices.Any(device => string.IsNullOrWhiteSpace(device.Name)))
        {
            StatusText.Text = "Every device needs a name.";
            return;
        }

        var wasConnected = _session.IsConnected;
        _session.Disconnect();
        _session.ApplyDevices(_devices, dbNumber);
        ShowSignals();
        UpdateConnectionUi();
        _controller.Rebuild();
        StatusText.Text = $"{_session.Name}: {_devices.Count} devices applied ({DeviceLayout.TotalBytes(_devices)} bytes in DB{dbNumber})." +
                          (wasConnected ? " Connect again." : "");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (_session.IsConnected)
        {
            _session.Disconnect();
            UpdateConnectionUi();
            StatusText.Text = $"{_session.Name}: disconnected.";
            return;
        }

        if (!short.TryParse(RackBox.Text, out var rack) || !short.TryParse(SlotBox.Text, out var slot)
            || !int.TryParse(DbBox.Text, out var dbNumber) || dbNumber <= 0 || string.IsNullOrWhiteSpace(IpBox.Text))
        {
            StatusText.Text = "Enter a valid IP address, rack, slot and DB number.";
            return;
        }

        var session = _session;
        ConnectButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        StatusText.Text = $"{session.Name}: connecting to {IpBox.Text.Trim()}...";
        try
        {
            await session.ConnectAsync(IpBox.Text, rack, slot, dbNumber);
            StatusText.Text = session.Warning is null
                ? $"{session.Name}: connected. Values are refreshed every {CycleTime.TotalMilliseconds:0} ms."
                : $"{session.Name}: connected. Warning: {session.Warning}";
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Connect", ex);
            session.Disconnect();
            StatusText.Text = $"{session.Name}: connection failed: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            Cursor = null;
            UpdateConnectionUi();
        }
    }

    /// <summary>Timer cycle: PLC read, model step, PLC write, view refresh.</summary>
    private async Task CycleAsync()
    {
        if (_cycleRunning)
        {
            return;
        }

        _cycleRunning = true;
        try
        {
            var dt = Math.Min(0.5, _clock.Elapsed.TotalSeconds);
            _clock.Restart();
            var errors = await _controller.StepAsync(dt);
            if (errors.Count > 0)
            {
                StatusText.Text = string.Join(" · ", errors);
                UpdateConnectionUi();
            }

            _tick++;
            SimTimeText.Text = _controller.Running ? TimeSpan.FromSeconds(_controller.SimulatedTime).ToString(@"hh\:mm\:ss") : "";
            if (_tick % 3 == 0)
            {
                ProgramPanel.RefreshLive();
                ScadaPanel.RefreshLive();
            }

            if (_tick % 10 == 0)
            {
                UpdatePlcStatus();
            }
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Cycle", ex);
            StatusText.Text = "Simulation error: " + ex.Message;
        }
        finally
        {
            _cycleRunning = false;
        }
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.Running = !_controller.Running;
        UpdateRunButton();
        StatusText.Text = _controller.Running
            ? _sessions.Any(session => session.IsConnected)
                ? "Model running: it writes the simulated inputs of the connected PLCs."
                : "Model running without a PLC (test mode): values are visible in Program and SCADA only."
            : "Model stopped: the PLC inputs keep their last values.";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _controller.Reset();
        StatusText.Text = "Model reset to the initial state; forces removed.";
    }

    private void UpdateRunButton()
    {
        RunButton.Content = _controller.Running ? "■ Stop model" : "▶ Run model";
        RunButton.Background = _controller.Running ? new SolidColorBrush(Color.FromRgb(0xB7, 0xE4, 0xC7)) : SystemColors.ControlBrush;
    }

    private void UpdateConnectionUi()
    {
        var connected = _session?.IsConnected == true;
        ConnectionLamp.Fill = connected ? ConnectedBrush : DisconnectedBrush;
        ConnectionText.Text = connected ? $"Connected to {_session!.Settings.IpAddress} · DB{_session.DbNumber}" : "Not connected";
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        IpBox.IsEnabled = !connected;
        RackBox.IsEnabled = !connected;
        SlotBox.IsEnabled = !connected;
        DbBox.IsEnabled = !connected;
        LayoutExpander.IsEnabled = !connected;
        DevicesExpander.IsEnabled = !connected;
        UpdatePlcStatus();
    }

    private void UpdatePlcStatus()
    {
        PlcStatusText.Text = _sessions.Count <= 1
            ? ""
            : string.Join("   ", _sessions.Select(session => $"{(session.IsConnected ? "●" : "○")} {session.Name}"));
    }

    private async void SetTrue_Click(object sender, RoutedEventArgs e) => await SetBoolAsync(sender, true);

    private async void SetFalse_Click(object sender, RoutedEventArgs e) => await SetBoolAsync(sender, false);

    private async Task SetBoolAsync(object sender, bool value)
    {
        if ((sender as FrameworkElement)?.DataContext is not SimulatorSignal signal || !EnsureConnected())
        {
            return;
        }

        await WriteAsync(signal, () => _session!.WriteBitAsync(signal, value),
            $"{signal.Kind} {signal.Module}.{signal.Channel} {signal.Name} := {(value ? "TRUE" : "FALSE")}");
    }

    private async void SetAnalog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SimulatorSignal signal)
        {
            await SetAnalogAsync(signal);
        }
    }

    private async void AnalogBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as FrameworkElement)?.DataContext is SimulatorSignal signal)
        {
            e.Handled = true;
            await SetAnalogAsync(signal);
        }
    }

    private async Task SetAnalogAsync(SimulatorSignal signal)
    {
        if (!EnsureConnected())
        {
            return;
        }

        var text = signal.SetText.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            StatusText.Text = $"'{signal.SetText}' is not a number.";
            return;
        }

        if (signal.IsReal)
        {
            var real = (float)number;
            await WriteAsync(signal, () => _session!.WriteRealAsync(signal, real),
                $"{signal.Kind} {signal.Module} {signal.Name} := {number.ToString("0.###", CultureInfo.InvariantCulture)} {signal.Unit}");
            return;
        }

        var useRaw = RawEntryCheck.IsChecked == true || !signal.HasScaling;
        var exactRaw = useRaw ? Math.Round(number) : signal.Scaling!.ToRawExact(number);
        if (double.IsNaN(exactRaw) || exactRaw < short.MinValue || exactRaw > short.MaxValue)
        {
            var range = useRaw ? "" : $" The measuring range is {signal.ScalingText}.";
            StatusText.Text = $"{signal.SetText} {(useRaw ? "" : signal.Unit)} cannot be written: the raw value would be {exactRaw:0}, outside −32768…32767.{range}";
            return;
        }

        var raw = (short)Math.Round(exactRaw, MidpointRounding.AwayFromZero);
        var description = useRaw
            ? $"{signal.Kind} {signal.Module}.{signal.Channel} {signal.Name} := raw {raw}"
            : $"{signal.Kind} {signal.Module}.{signal.Channel} {signal.Name} := {number.ToString("0.###", CultureInfo.InvariantCulture)} {signal.Unit} (raw {raw})";
        if (AnalogScaling.IsErrorRaw(raw))
        {
            description += " – the PLC treats this raw value as a measurement error";
        }
        else if (!useRaw && signal.Scaling is { TenthsOfValue: false } scaling
                 && (raw < Math.Min(scaling.RawLow, scaling.RawHigh) || raw > Math.Max(scaling.RawLow, scaling.RawHigh)))
        {
            description += $" – outside the measuring range {signal.ScalingText}";
        }

        await WriteAsync(signal, () => _session!.WriteIntAsync(signal, raw), description);
    }

    private async Task WriteAsync(SimulatorSignal signal, Func<Task> write, string description)
    {
        try
        {
            await write();
            StatusText.Text = "Set " + description + (_controller.Running ? " (the running model may overwrite it if it drives this signal)" : "");
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Write", ex);
            StatusText.Text = $"Writing {signal.Address} failed: {ex.Message}";
        }
    }

    private bool EnsureConnected()
    {
        if (_session?.IsConnected == true)
        {
            return true;
        }

        StatusText.Text = "Connect to the PLC first.";
        return false;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        UpdateSummary();
    }

    private bool Matches(SimulatorSignal signal)
    {
        if (ShowDisabledCheck.IsChecked != true && !signal.IsEnabled)
        {
            return false;
        }

        var kindMatches = KindFilter.SelectedIndex switch
        {
            1 => signal.IsWritable,
            2 => !signal.IsWritable,
            3 => signal.Kind == SimulatorSignalKind.DI,
            4 => signal.Kind == SimulatorSignalKind.AI,
            5 => signal.Kind == SimulatorSignalKind.RTD,
            6 => signal.Kind == SimulatorSignalKind.COUNTER,
            7 => signal.Kind == SimulatorSignalKind.DO,
            8 => signal.Kind == SimulatorSignalKind.AO,
            9 => signal.InDeviceDb,
            _ => true
        };
        if (!kindMatches)
        {
            return false;
        }

        var search = SearchBox.Text.Trim();
        return search.Length == 0
               || search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .All(word => signal.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateSummary()
    {
        if (SummaryText is null || _session is null)
        {
            return;
        }

        var enabled = _session.Signals.Where(signal => signal.IsEnabled).ToList();
        var shown = _view?.Cast<object>().Count() ?? 0;
        SummaryText.Text = $"{shown} shown · enabled: DI {Count(enabled, SimulatorSignalKind.DI)}, AI {Count(enabled, SimulatorSignalKind.AI)}, " +
                           $"RTD {Count(enabled, SimulatorSignalKind.RTD)}, COUNTER {Count(enabled, SimulatorSignalKind.COUNTER)}, " +
                           $"DO {Count(enabled, SimulatorSignalKind.DO)}, AO {Count(enabled, SimulatorSignalKind.AO)}";
    }

    private static int Count(IEnumerable<SimulatorSignal> signals, SimulatorSignalKind kind) => signals.Count(signal => signal.Kind == kind);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_controller.Dirty || _controller.ModelPath is null)
        {
            return;
        }

        var answer = MessageBox.Show(this, "The plant model has unsaved changes. Save them?", "Simulator",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                _controller.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the model: " + ex.Message, "Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Cancel = true;
            }
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        foreach (var session in _sessions)
        {
            session.Dispose();
        }
    }
}
