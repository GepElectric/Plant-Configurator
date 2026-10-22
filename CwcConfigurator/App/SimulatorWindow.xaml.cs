using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation;

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
/// Tools → Simulator: reads and writes the PLC simulator DB (DB30) over S7. The simulator drives the PLC
/// program's inputs (DI, AI, RTD) and shows its outputs (DO, AO). The PLC uses DB30 instead of the I/O modules
/// only in total simulation (PLC_CONFIGURATION.RUN_MODE = 2).
/// </summary>
public partial class SimulatorWindow : Window
{
    public const string CommandLineSwitch = "--simulator";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));

    private readonly SimulatorS7Client _client = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = PollInterval };
    private readonly List<TextBox> _constantBoxes = [];
    private List<SimulatorSignal> _signals = [];
    private ICollectionView? _view;
    private SimulatorLayout _layout = SimulatorLayout.Default;
    private SimulatorPlcOption? _option;
    private int _dbNumber = SimulatorLayout.DefaultDbNumber;
    private bool _polling;

    public SimulatorWindow(IReadOnlyList<SimulatorPlcOption> options, string? activeName)
    {
        InitializeComponent();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        foreach (var name in SimulatorLayout.ConstantNames)
        {
            var box = new TextBox { Width = 40, VerticalContentAlignment = VerticalAlignment.Center };
            _constantBoxes.Add(box);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
            item.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            item.Children.Add(box);
            ConstantsPanel.Children.Add(item);
        }

        foreach (var option in options)
        {
            PlcSelector.Items.Add(option);
        }

        PlcSelector.SelectedItem = options.FirstOrDefault(option => option.Name == activeName) ?? options.FirstOrDefault();
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
        var project = new CwcConfigurator.Services.ProjectFileService().Load(fullPath);
        var active = project.PlcDatabases.FirstOrDefault(database => database.Id == project.ActivePlcDatabaseId)?.Name;
        var window = new SimulatorWindow(CreateOptions(project, fullPath), active)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Title = $"Simulator – {System.IO.Path.GetFileName(fullPath)}";
        return window;
    }

    private void PlcSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlcSelector.SelectedItem is not SimulatorPlcOption option)
        {
            return;
        }

        Disconnect("Not connected");
        _option = option;
        var settings = SimulatorSettingsStore.Load(option.SettingsKey);
        IpBox.Text = settings.IpAddress;
        RackBox.Text = settings.Rack.ToString(CultureInfo.InvariantCulture);
        SlotBox.Text = settings.Slot.ToString(CultureInfo.InvariantCulture);
        DbBox.Text = settings.DbNumber.ToString(CultureInfo.InvariantCulture);

        var layout = SimulatorLayout.FromProject(option.Project);
        if (settings.Constants is { } constants)
        {
            try
            {
                layout = SimulatorLayout.FromConstants(constants);
            }
            catch (ArgumentException)
            {
            }
        }

        var values = layout.ToConstants();
        for (var index = 0; index < _constantBoxes.Count; index++)
        {
            _constantBoxes[index].Text = values[index].ToString(CultureInfo.InvariantCulture);
        }

        LayoutExpander.IsExpanded = settings.Constants is null;
        ApplyLayout(layout);
        StatusText.Text = string.IsNullOrWhiteSpace(settings.IpAddress)
            ? "Enter the PLC IP address and connect. PUT/GET access must be allowed in the PLC."
            : "Ready to connect.";
        UpdateSummary();
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_option is null)
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

        Disconnect("Not connected");
        ApplyLayout(SimulatorLayout.FromConstants(values));
        var settings = SimulatorSettingsStore.Load(_option.SettingsKey);
        settings.Constants = values.ToArray();
        SimulatorSettingsStore.Save(_option.SettingsKey, settings);
        StatusText.Text = "Layout applied: " + _layout.Describe() + ".";
    }

    private void ApplyLayout(SimulatorLayout layout)
    {
        _layout = layout;
        _signals = SimulatorPlanBuilder.Build(_option!.Project, _layout);
        _view = CollectionViewSource.GetDefaultView(_signals);
        _view.Filter = item => item is SimulatorSignal signal && Matches(signal);
        SignalsGrid.ItemsSource = _view;
        LayoutText.Text = $"Simulator DB: {_layout.Describe()}. " +
                          "The PLC reads DI/AI/RTD from this DB and writes DO/AO into it only in total simulation (PLC_CONFIGURATION.RUN_MODE = 2).";
        UpdateSummary();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client.IsConnected)
        {
            Disconnect("Disconnected");
            return;
        }

        if (_option is null)
        {
            return;
        }

        if (!short.TryParse(RackBox.Text, out var rack) || !short.TryParse(SlotBox.Text, out var slot)
            || !int.TryParse(DbBox.Text, out var dbNumber) || dbNumber <= 0 || string.IsNullOrWhiteSpace(IpBox.Text))
        {
            StatusText.Text = "Enter a valid IP address, rack, slot and DB number.";
            return;
        }

        SetBusy(true, $"Connecting to {IpBox.Text.Trim()}...");
        try
        {
            await _client.ConnectAsync(IpBox.Text, rack, slot);
            _dbNumber = dbNumber;
            if (!await _client.HasLengthAsync(dbNumber, _layout.TotalBytes))
            {
                throw new InvalidOperationException(
                    $"DB{dbNumber} is shorter than the expected {_layout.TotalBytes} bytes ({_layout.Describe()}). " +
                    "Check the DB number and the PLC constants under 'Simulator DB layout'.");
            }

            var longer = await _client.HasLengthAsync(dbNumber, _layout.TotalBytes + 2);
            SimulatorSettingsStore.Save(_option.SettingsKey, new SimulatorConnectionSettings
            {
                IpAddress = IpBox.Text.Trim(),
                Rack = rack,
                Slot = slot,
                DbNumber = dbNumber,
                Constants = _layout.ToConstants()
            });

            ConnectionLamp.Fill = ConnectedBrush;
            ConnectionText.Text = $"Connected to {IpBox.Text.Trim()} · DB{dbNumber}";
            ConnectButton.Content = "Disconnect";
            SetConnectionFieldsEnabled(false);
            StatusText.Text = longer
                ? $"Connected. Warning: DB{dbNumber} is longer than {_layout.TotalBytes} bytes; check the PLC constants under 'Simulator DB layout'."
                : "Connected. Values are refreshed every 0.5 s.";
            await PollAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Connect", ex);
            Disconnect("Not connected");
            StatusText.Text = "Connection failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PollAsync()
    {
        if (_polling || !_client.IsConnected)
        {
            return;
        }

        _polling = true;
        try
        {
            var data = await _client.ReadBytesAsync(_dbNumber, 0, _layout.TotalBytes);
            foreach (var signal in _signals)
            {
                signal.UpdateFrom(data);
            }
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Poll", ex);
            Disconnect("Connection lost");
            StatusText.Text = "Reading the PLC failed: " + ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private async void SetTrue_Click(object sender, RoutedEventArgs e) => await SetBoolAsync(sender, true);

    private async void SetFalse_Click(object sender, RoutedEventArgs e) => await SetBoolAsync(sender, false);

    private async Task SetBoolAsync(object sender, bool value)
    {
        if ((sender as FrameworkElement)?.DataContext is not SimulatorSignal signal || !EnsureConnected())
        {
            return;
        }

        await WriteAsync(signal, () => _client.WriteBitAsync(_dbNumber, signal.ByteOffset, signal.BitOffset, value),
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

        await WriteAsync(signal, () => _client.WriteIntAsync(_dbNumber, signal.ByteOffset, raw), description);
    }

    private async Task WriteAsync(SimulatorSignal signal, Func<Task> write, string description)
    {
        try
        {
            await write();
            StatusText.Text = "Set " + description;
            await PollAsync();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Write", ex);
            StatusText.Text = $"Writing {signal.Address} failed: {ex.Message}";
        }
    }

    private bool EnsureConnected()
    {
        if (_client.IsConnected)
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
            6 => signal.Kind == SimulatorSignalKind.DO,
            7 => signal.Kind == SimulatorSignalKind.AO,
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
        if (SummaryText is null)
        {
            return;
        }

        var enabled = _signals.Where(signal => signal.IsEnabled).ToList();
        var shown = _view?.Cast<object>().Count() ?? 0;
        SummaryText.Text = $"{shown} shown · enabled: DI {Count(enabled, SimulatorSignalKind.DI)}, AI {Count(enabled, SimulatorSignalKind.AI)}, " +
                           $"RTD {Count(enabled, SimulatorSignalKind.RTD)}, DO {Count(enabled, SimulatorSignalKind.DO)}, AO {Count(enabled, SimulatorSignalKind.AO)}";
    }

    private static int Count(IEnumerable<SimulatorSignal> signals, SimulatorSignalKind kind) => signals.Count(signal => signal.Kind == kind);

    private void Disconnect(string text)
    {
        _pollTimer.Stop();
        _client.Disconnect();
        foreach (var signal in _signals)
        {
            signal.ClearValue();
        }

        ConnectionLamp.Fill = DisconnectedBrush;
        ConnectionText.Text = text;
        ConnectButton.Content = "Connect";
        SetConnectionFieldsEnabled(true);
    }

    private void SetConnectionFieldsEnabled(bool enabled)
    {
        PlcSelector.IsEnabled = enabled;
        IpBox.IsEnabled = enabled;
        RackBox.IsEnabled = enabled;
        SlotBox.IsEnabled = enabled;
        DbBox.IsEnabled = enabled;
        LayoutExpander.IsEnabled = enabled;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ConnectButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _client.Dispose();
    }
}
