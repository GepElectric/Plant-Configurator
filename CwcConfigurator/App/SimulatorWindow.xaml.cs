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

    /// <summary>Identifies the project (its file path) for remembering the last model file.</summary>
    public string ProjectKey { get; init; } = "";

    /// <summary>Connection settings from a .plantsim package; null = the settings stored on this PC.</summary>
    public SimulatorConnectionSettings? Settings { get; init; }

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
    private readonly string _projectKey;
    private Func<byte[]>? _projectBytes;
    private readonly string _projectName;
    private bool _skipClosePrompt;
    private ICollectionView? _view;
    private bool _cycleRunning;
    private int _tick;

    /// <param name="models">Models from a .plantsim package (null = read <paramref name="modelPath"/>).</param>
    /// <param name="projectBytes">The project file content for saving a .plantsim package.</param>
    public SimulatorWindow(IReadOnlyList<SimulatorPlcOption> options, string? activeName, string? modelPath,
        Dictionary<string, ProcessModel>? models = null, Func<byte[]>? projectBytes = null, string projectName = "")
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
        _sessions = options.Select(option => new PlcSession(option.Name, option.Project, option.SettingsKey, option.Settings)).ToList();
        _projectKey = options.FirstOrDefault()?.ProjectKey ?? "";
        _projectBytes = projectBytes;
        _projectName = projectName;
        if (models is null)
        {
            // Continue with the model file used last time for this project (it may be elsewhere than next to the project).
            var remembered = _projectKey.Length > 0 ? SimulatorSettingsStore.LoadModelPath(_projectKey) : null;
            if (remembered is not null && System.IO.File.Exists(remembered))
            {
                modelPath = remembered;
            }
        }

        _controller = new SimulationController(_sessions, modelPath, models);
        _controller.PackageWriter = WritePackage;
        _controller.FileStateChanged += UpdateModelFileText;
        UpdateModelFileText();
        ProgramPanel.Attach(_controller);
        ProgramPanel.SaveRequested += SaveModel_Click;
        ScadaPanel.Attach(_controller);
        foreach (var session in _sessions)
        {
            PlcSelector.Items.Add(session);
        }

        RestoreForces();
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
            return new SimulatorPlcOption { Name = database.Name, Project = scoped, SettingsKey = projectKey + "|" + database.Name, ProjectKey = projectKey };
        }).ToList();
        if (options.Count == 0)
        {
            options.Add(new SimulatorPlcOption { Name = "PLC", Project = project, SettingsKey = projectKey + "|PLC", ProjectKey = projectKey });
        }

        return options;
    }

    /// <summary>
    /// Stand-alone start: a ".plantsim" package (database, model and connections inside), or a ".plantcfg" project
    /// (the database is read fresh from the project; the model and connections come from "&lt;project&gt;.plantsim" next
    /// to it when it exists). Without a path a file dialog asks. Returns null when nothing was chosen.
    /// </summary>
    public static SimulatorWindow? CreateStandalone(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Plant Simulator – open a simulation or a Plant Configurator project",
                Filter = "Simulation or project (*.plantsim;*.plantcfg)|*.plantsim;*.plantcfg|Simulation (*.plantsim)|*.plantsim|Plant Configurator project (*.plantcfg)|*.plantcfg"
            };
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            path = dialog.FileName;
        }

        var fullPath = System.IO.Path.GetFullPath(path);
        SimulatorWindow window;
        if (SimulationPackage.IsPackage(fullPath))
        {
            var content = SimulationPackage.Load(fullPath);
            window = Create(content.Project, content.ProjectName.Length > 0 ? content.ProjectName : System.IO.Path.GetFileNameWithoutExtension(fullPath),
                fullPath, fullPath, content.Models, content.Connections);
        }
        else
        {
            var packagePath = SimulationPackage.PathFor(fullPath);
            Dictionary<string, ProcessModel>? models = null;
            Dictionary<string, SimulatorConnectionSettings>? connections = null;
            if (System.IO.File.Exists(packagePath))
            {
                var content = SimulationPackage.Load(packagePath);
                models = content.Models;
                connections = content.Connections;
            }
            else if (System.IO.File.Exists(ProcessModelStore.PathFor(fullPath)))
            {
                models = ProcessModelStore.Load(ProcessModelStore.PathFor(fullPath));   // earlier model file of this project
            }

            var bytes = System.IO.File.ReadAllBytes(fullPath);
            window = Create(bytes, System.IO.Path.GetFileNameWithoutExtension(fullPath), fullPath, packagePath, models ?? new(StringComparer.OrdinalIgnoreCase), connections,
                () => System.IO.File.ReadAllBytes(fullPath));
        }

        SimulatorSettingsStore.SaveLastProject(fullPath);
        return window;
    }

    private static SimulatorWindow Create(byte[] projectBytes, string projectName, string key, string modelPath,
        Dictionary<string, ProcessModel> models, Dictionary<string, SimulatorConnectionSettings>? connections, Func<byte[]>? readProject = null)
    {
        var temp = SimulationPackage.ExtractProject(projectBytes, projectName);
        CwcProject project;
        try
        {
            project = new Services.ProjectFileService().Load(temp);
        }
        finally
        {
            try
            {
                System.IO.File.Delete(temp);
            }
            catch (System.IO.IOException)
            {
            }
        }

        var options = CreateOptions(project, key).Select(option => new SimulatorPlcOption
        {
            Name = option.Name,
            Project = option.Project,
            SettingsKey = option.SettingsKey,
            ProjectKey = option.ProjectKey,
            Settings = connections is not null && connections.TryGetValue(option.Name, out var settings) ? settings : null
        }).ToList();
        var active = project.PlcDatabases.FirstOrDefault(database => database.Id == project.ActivePlcDatabaseId)?.Name;
        return new SimulatorWindow(options, active, modelPath, models, readProject ?? (() => projectBytes), projectName)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = $"Plant Simulator – {System.IO.Path.GetFileName(modelPath)}"
        };
    }

    /// <summary>Writes the .plantsim package: project copy, model and the connection settings of every PLC.</summary>
    private void WritePackage(string path)
    {
        var project = _projectBytes?.Invoke() ?? throw new InvalidOperationException("The project of this simulation is not available.");
        foreach (var session in _sessions)
        {
            session.SaveSettings();
        }

        SimulationPackage.Save(path, new SimulationPackage.Content
        {
            Project = project,
            Models = _controller.Models,
            Connections = _sessions.ToDictionary(session => session.Name, session => session.Settings, StringComparer.OrdinalIgnoreCase),
            ProjectName = _projectName,
            SourceProject = _projectKey
        });
        Title = $"Plant Simulator – {System.IO.Path.GetFileName(path)}";
    }

    /// <summary>Replaces this window by a simulator for another file (package or project).</summary>
    private void SwitchTo(SimulatorWindow? next)
    {
        if (next is null)
        {
            return;
        }

        next.Icon = Icon;
        next.Show();
        if (Application.Current.MainWindow == this)
        {
            Application.Current.MainWindow = next;
        }

        _skipClosePrompt = true;
        Close();
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

    private void UpdateModelFileText()
    {
        var path = _controller.ModelPath;
        ModelFileText.Text = path is null ? "Model: not saved" : (_controller.Dirty ? "● " : "") + "Model: " + System.IO.Path.GetFileName(path);
        ModelFileText.ToolTip = path ?? "The plant model has not been saved to a file yet.";
    }

    private void OpenModel_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Dirty && MessageBox.Show(this, "The plant model has unsaved changes. Discard them and open another model?", "Simulator",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a simulation, a project or a model",
            Filter = "Simulation (*.plantsim)|*.plantsim|Plant Configurator project (*.plantcfg)|*.plantcfg|Plant model only (*.simulator.yaml)|*.simulator.yaml|All files (*.*)|*.*",
            InitialDirectory = _controller.ModelPath is null ? null : System.IO.Path.GetDirectoryName(_controller.ModelPath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (SimulationPackage.IsPackage(dialog.FileName) || dialog.FileName.EndsWith(".plantcfg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SwitchTo(CreateStandalone(dialog.FileName));
            }
            catch (Exception ex)
            {
                CwcApplication.LogException("Simulator.Open", ex);
                StatusText.Text = "Could not open: " + ex.Message;
            }

            return;
        }

        try
        {
            foreach (var session in _sessions)
            {
                foreach (var signal in session.Signals)
                {
                    signal.ClearForces();
                }
            }

            var unknown = _controller.Open(dialog.FileName);
            RestoreForces();
            RememberModel();
            StatusText.Text = $"Opened {dialog.FileName}." + (unknown.Count == 0 ? ""
                : $" Objects of {string.Join(", ", unknown)} are kept in the file but this project has no PLC database with that name.");
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.OpenModel", ex);
            StatusText.Text = "Could not open the model: " + ex.Message;
        }
    }

    /// <summary>Loads the database from a (newer) .plantcfg; the model, forces and connection settings stay.</summary>
    private void UpdateDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Update the database from a Plant Configurator project",
            Filter = "Plant Configurator project (*.plantcfg)|*.plantcfg"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            foreach (var session in _sessions)
            {
                session.SaveSettings();
                if (_controller.Models.TryGetValue(session.Name, out var model))
                {
                    model.Forces = session.Signals.Where(signal => signal.IsForced).SelectMany(DescribeForces).ToList();
                }
            }

            var projectPath = dialog.FileName;
            var bytes = System.IO.File.ReadAllBytes(projectPath);
            var connections = _sessions.ToDictionary(session => session.Name, session => session.Settings, StringComparer.OrdinalIgnoreCase);
            var modelPath = _controller.ModelPath ?? SimulationPackage.PathFor(projectPath);
            var next = Create(bytes, System.IO.Path.GetFileNameWithoutExtension(projectPath), _projectKey.Length > 0 ? _projectKey : projectPath,
                modelPath, _controller.Models, connections, SimulationPackage.IsPackage(modelPath) ? () => bytes : () => System.IO.File.ReadAllBytes(projectPath));
            next._controller.MarkDirty();
            next.StatusText.Text = $"Database updated from {projectPath}. Save to keep it in the simulation.";
            SwitchTo(next);
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.UpdateDatabase", ex);
            StatusText.Text = "Could not update the database: " + ex.Message;
        }
    }

    private void SaveModel_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.ModelPath is null)
        {
            SaveModelAs_Click(sender, e);
            return;
        }

        try
        {
            _controller.Save();
            RememberModel();
            StatusText.Text = "Model saved: " + _controller.ModelPath;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save the model: " + ex.Message;
        }
    }

    private void SaveModelAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save simulation as",
            Filter = "Simulation with its database (*.plantsim)|*.plantsim|Plant model only (*.simulator.yaml)|*.simulator.yaml",
            FileName = _controller.ModelPath is null ? (_projectName.Length > 0 ? _projectName : "Plant") + SimulationPackage.Extension
                : System.IO.Path.GetFileName(_controller.ModelPath),
            InitialDirectory = _controller.ModelPath is null ? null : System.IO.Path.GetDirectoryName(_controller.ModelPath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;
        if (!SimulationPackage.IsPackage(path) && !path.EndsWith(".simulator.yaml", StringComparison.OrdinalIgnoreCase))
        {
            path = System.IO.Path.ChangeExtension(path, null) + (dialog.FilterIndex == 2 ? ".simulator.yaml" : SimulationPackage.Extension);
        }
        try
        {
            _controller.SaveAs(path);
            RememberModel();
            StatusText.Text = "Model saved as " + path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save the model: " + ex.Message;
        }
    }

    private void RememberModel()
    {
        if (_projectKey.Length > 0 && _controller.ModelPath is not null)
        {
            SimulatorSettingsStore.SaveModelPath(_projectKey, _controller.ModelPath);
        }
    }

    /// <summary>SET on an analog signal: press = hold the typed value, press again = release.</summary>
    private void SetAnalogToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SimulatorSignal signal)
        {
            return;
        }

        if (signal.ForceValueOn)
        {
            signal.ForceValueOn = false;
            StatusText.Text = $"Released {signal.Kind} {signal.Name}.";
            return;
        }

        HoldAnalog(signal);
    }

    private void AnalogBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (sender as FrameworkElement)?.DataContext is SimulatorSignal signal)
        {
            e.Handled = true;
            HoldAnalog(signal);
        }
    }

    /// <summary>While SET is held, the typed value is sent as soon as it is a valid number (no Enter needed).</summary>
    private void AnalogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SimulatorSignal { ForceValueOn: true } signal)
        {
            HoldAnalog(signal, releaseOnError: false);
        }
    }

    private void HoldAnalog(SimulatorSignal signal, bool releaseOnError = true)
    {
        var text = signal.SetText.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (!releaseOnError)
            {
                return;
            }

            signal.ForceValueOn = false;
            StatusText.Text = $"'{signal.SetText}' is not a number.";
            return;
        }

        var value = number;
        var description = $"{number.ToString("0.###", CultureInfo.InvariantCulture)} {signal.Unit}".Trim();
        if (!signal.IsReal)
        {
            var useRaw = RawEntryCheck.IsChecked == true || !signal.HasScaling;
            var exactRaw = useRaw ? Math.Round(number) : signal.Scaling!.ToRawExact(number);
            if (double.IsNaN(exactRaw) || exactRaw < short.MinValue || exactRaw > short.MaxValue)
            {
                if (!releaseOnError)
                {
                    StatusText.Text = $"{signal.SetText} is outside the measuring range {signal.ScalingText}; still holding the previous value.";
                    return;
                }

                signal.ForceValueOn = false;
                StatusText.Text = $"{signal.SetText} cannot be held: the raw value would be {exactRaw:0}, outside −32768…32767. The measuring range is {signal.ScalingText}.";
                return;
            }

            var raw = (short)Math.Round(exactRaw, MidpointRounding.AwayFromZero);
            value = signal.HasScaling ? signal.Scaling!.ToEngineering(raw) : raw;
            description = useRaw ? $"raw {raw}" : $"{description} (raw {raw})";
            if (AnalogScaling.IsErrorRaw(raw) && signal.Kind is SimulatorSignalKind.AI or SimulatorSignalKind.RTD)
            {
                description += " – the PLC treats this raw value as a measurement error";
            }
        }

        signal.ForceValue = value;
        signal.ForceValueOn = true;
        StatusText.Text = $"Holding {signal.Kind} {signal.Name} = {description}" +
                          (signal.IsWritable ? "" : " (the simulator model sees this value instead of the PLC output)") +
                          (_session?.IsConnected == true || !signal.IsWritable ? "." : ". Connect to send it to the PLC.");
    }

    private void SaveForces_Click(object sender, RoutedEventArgs e)
    {
        foreach (var session in _sessions)
        {
            if (!_controller.Models.TryGetValue(session.Name, out var model))
            {
                model = new ProcessModel();
                _controller.Models[session.Name] = model;
            }

            model.Forces = session.Signals.Where(signal => signal.IsForced).SelectMany(DescribeForces).ToList();
        }

        _controller.MarkDirty();
        SaveModel_Click(sender, e);
        if (!_controller.Dirty)
        {
            var count = _sessions.Sum(session => session.Signals.Count(signal => signal.IsForced));
            StatusText.Text = $"Saved {count} forced signal(s) in {_controller.ModelPath}.";
        }
    }

    private void ClearForces_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        foreach (var signal in _session.Signals)
        {
            signal.ClearForces();
        }

        StatusText.Text = $"{_session.Name}: all forces released (Save forces to remember that).";
    }

    private static IEnumerable<string> DescribeForces(SimulatorSignal signal)
    {
        if (signal.ForceTrue)
        {
            yield return signal.ForceKey + "|TRUE";
        }

        if (signal.ForceFalse)
        {
            yield return signal.ForceKey + "|FALSE";
        }

        if (signal.ForceValueOn)
        {
            yield return $"{signal.ForceKey}|SET {signal.ForceValue.ToString("R", CultureInfo.InvariantCulture)}|{signal.SetText}";
        }

        if (signal.WireBreak)
        {
            yield return signal.ForceKey + "|WIREBREAK";
        }
    }

    /// <summary>Presses the saved force buttons again.</summary>
    private void RestoreForces()
    {
        foreach (var session in _sessions)
        {
            if (!_controller.Models.TryGetValue(session.Name, out var model) || model.Forces.Count == 0)
            {
                continue;
            }

            var byKey = session.Signals.GroupBy(signal => signal.ForceKey).ToDictionary(group => group.Key, group => group.First());
            foreach (var entry in model.Forces)
            {
                var parts = entry.Split('|');
                if (parts.Length < 2 || !byKey.TryGetValue(parts[0], out var signal))
                {
                    continue;
                }

                if (parts[1] == "TRUE")
                {
                    signal.ForceTrue = true;
                }
                else if (parts[1] == "FALSE")
                {
                    signal.ForceFalse = true;
                }
                else if (parts[1] == "WIREBREAK")
                {
                    signal.WireBreak = true;
                }
                else if (parts[1].StartsWith("SET ", StringComparison.Ordinal)
                         && double.TryParse(parts[1][4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    signal.ForceValue = value;
                    signal.SetText = parts.Length > 2 ? parts[2] : value.ToString("0.###", CultureInfo.InvariantCulture);
                    signal.ForceValueOn = true;
                }
            }
        }
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
            1 => signal.Kind == SimulatorSignalKind.DI,
            2 => signal.Kind == SimulatorSignalKind.DO,
            3 => signal.Kind == SimulatorSignalKind.AI,
            4 => signal.Kind == SimulatorSignalKind.RTD,
            5 => signal.Kind == SimulatorSignalKind.COUNTER,
            6 => signal.Kind == SimulatorSignalKind.AO,
            7 => signal.InDeviceDb,
            _ => true
        };
        if (!kindMatches
            || !NumberFilter.Matches(ModuleFilterBox.Text, signal.Module)
            || !NumberFilter.Matches(ChannelFilterBox.Text, signal.Channel)
            || !NumberFilter.Matches(IdFilterBox.Text, signal.Id))
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
        if (!_controller.Dirty || _skipClosePrompt)
        {
            return;
        }

        var answer = MessageBox.Show(this, "The simulation has unsaved changes. Save them?", "Plant Simulator",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            SaveModel_Click(this, new RoutedEventArgs());
            if (_controller.Dirty)
            {
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
