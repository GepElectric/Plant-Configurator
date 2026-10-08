using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Services.AlarmTesting;

namespace CwcConfigurator.App;

public partial class AlarmTesterDialog : Window
{
    private static readonly TimeSpan AlarmPollTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AlarmPollInterval = TimeSpan.FromMilliseconds(400);

    private const int PipeConnectTimeoutMs = 5000;
    private const int PipeSubscribeTimeoutMs = 8000;
    private const int PipeRequestTimeoutMs = 6000;

    // Alarm-reset command sent over OpenPipe before each toggle. The unit prefix
    // (e.g. "Intake") depends on the PLC; the rest is fixed across PLCs. Writing 3
    // triggers the SCADA-local reset. The operator can correct the tag in the UI.
    private const string ResetTagSuffix = "_CONTROL_START_STOP_SCADA_LOCAL_CMD";
    private const string ResetValue = "3";

    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6));
    private static readonly Brush HeaderTextBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xD1));
    private static readonly Brush HeaderOkBrush = new SolidColorBrush(Color.FromRgb(0x7D, 0xE6, 0xA8));

    private readonly List<AlarmTestPlcOption> _options;
    private readonly AlarmTesterS7Client _s7Client = new();
    private readonly AlarmTesterOpenPipeClient _pipeClient = new();
    private readonly ObservableCollection<AlarmTestModule> _modules = [];
    private readonly ObservableCollection<PlcAlarmEvent> _events = [];

    private readonly Dictionary<int, bool> _moduleExpandState = [];

    private AlarmTestPlan _plan = new();
    private CancellationTokenSource? _pollCts;
    private AlarmTestCase? _selectedSignal;
    private string _lastAutoResetTag = "";

    public AlarmTesterDialog(IReadOnlyList<AlarmTestPlcOption> plcOptions, string activePlcDatabaseId)
    {
        InitializeComponent();
        _options = plcOptions.ToList();

        ModulesList.ItemsSource = _modules;
        EventsGrid.ItemsSource = _events;

        foreach (var option in _options)
        {
            PlcSelector.Items.Add(option);
        }

        var active = _options.FirstOrDefault(option => option.Id == activePlcDatabaseId) ?? _options.FirstOrDefault();
        PlcSelector.SelectedItem = active;
        if (active is null)
        {
            UpdateSummary();
        }
    }

    private void PlcSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlcSelector.SelectedItem is not AlarmTestPlcOption option)
        {
            return;
        }

        CancelPendingWait();
        _plan = option.Plan;
        _events.Clear();
        PopulateModuleFilter();
        RefreshModules();
        UpdateSummary();
        SelectFirstVisibleSignal();
        ApplyDefaultResetTag(option.Name);
    }

    private void ApplyDefaultResetTag(string plcName)
    {
        // Seed a sensible default from the PLC name, but keep any tag the operator
        // has typed/picked manually.
        var current = ResetTagBox.Text.Trim();
        if (!string.IsNullOrEmpty(current) && current != _lastAutoResetTag)
        {
            return;
        }

        var prefix = (plcName ?? "").Trim();
        _lastAutoResetTag = string.IsNullOrEmpty(prefix) ? "" : prefix + ResetTagSuffix;
        ResetTagBox.Text = _lastAutoResetTag;
    }

    private async void BrowseResetTag_Click(object sender, RoutedEventArgs e)
    {
        if (!_pipeClient.IsSubscribed)
        {
            StatusText.Text = "Connect OpenPipe first to search runtime tags.";
            return;
        }

        try
        {
            SetBusy(true);
            var matches = await _pipeClient.BrowseTagsAsync("*START_STOP_SCADA_LOCAL_CMD*", PipeRequestTimeoutMs, CancellationToken.None);
            var current = ResetTagBox.Text;
            ResetTagBox.Items.Clear();
            foreach (var name in matches)
            {
                ResetTagBox.Items.Add(name);
            }

            ResetTagBox.Text = current;
            ResetTagBox.IsDropDownOpen = matches.Count > 0;
            StatusText.Text = matches.Count > 0
                ? $"Found {matches.Count} matching tag(s) — pick the reset command."
                : "No matching tags found. Type the reset tag manually.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Tag search failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateModuleFilter()
    {
        ModuleFilter.Items.Clear();
        ModuleFilter.Items.Add(new ComboBoxItem { Content = "All modules" });

        foreach (var moduleNumber in _plan.DigitalModules.Select(module => module.ModuleNumber))
        {
            ModuleFilter.Items.Add(new ComboBoxItem
            {
                Content = $"Module {moduleNumber}",
                Tag = moduleNumber
            });
        }

        ModuleFilter.SelectedIndex = 0;
    }

    private void RefreshModules()
    {
        var filter = FilterBox.Text.Trim();
        var selectedModule = (ModuleFilter.SelectedItem as ComboBoxItem)?.Tag as int?;
        var cases = _plan.Cases
            .Where(item => item.SourceKind == AlarmTestSourceKind.DigitalInput)
            .Where(item => item.SimulatorAddress?.ValueKind == S7ValueKind.Bool)
            .Where(item => !selectedModule.HasValue || item.ModuleNumber == selectedModule.Value)
            .Where(item => SignalMatchesFilter(item, filter))
            .GroupBy(item => item.ModuleNumber ?? -1)
            .OrderBy(group => group.Key)
            .Select(group => new AlarmTestModule(
                group.Key,
                group.OrderBy(item => item.Port ?? int.MaxValue)
                    .ThenBy(item => item.SignalId ?? int.MaxValue)
                    .ThenBy(item => item.SignalName, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        _modules.Clear();
        foreach (var module in cases)
        {
            module.IsExpanded = _moduleExpandState.TryGetValue(module.ModuleNumber, out var expanded) ? expanded : true;
            _modules.Add(module);
        }
    }

    private void Module_Expanded(object sender, RoutedEventArgs e) => RememberModuleExpand(sender, true);

    private void Module_Collapsed(object sender, RoutedEventArgs e) => RememberModuleExpand(sender, false);

    private void RememberModuleExpand(object sender, bool expanded)
    {
        if (sender is FrameworkElement { DataContext: AlarmTestModule module })
        {
            _moduleExpandState[module.ModuleNumber] = expanded;
            module.IsExpanded = expanded;
        }
    }

    private void SignalsScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // The inner DataGrids swallow the wheel, so scroll the outer viewer here
        // (PreviewMouseWheel tunnels before the grids get it).
        SignalsScroll.ScrollToVerticalOffset(SignalsScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static bool SignalMatchesFilter(AlarmTestCase testCase, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return testCase.SignalName.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || testCase.SystemName.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || testCase.SignalId?.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase) == true
               || testCase.SimulatorAddressLabel.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectFirstVisibleSignal()
    {
        _selectedSignal = _modules.SelectMany(module => module.Signals).FirstOrDefault();
    }

    private async void ConnectS7_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            if (!short.TryParse(RackText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rack)
                || !short.TryParse(SlotText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
            {
                SetS7Status("Rack/slot must be numbers.", connected: false);
                return;
            }

            await _s7Client.ConnectAsync(IpAddressText.Text, rack, slot, CancellationToken.None);
            SetS7Status($"S7 connected to {IpAddressText.Text.Trim()}", connected: true);
            await RefreshStatesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetS7Status($"S7 connect failed: {ex.Message}", connected: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        _s7Client.Disconnect();
        SetS7Status("S7 not connected", connected: false);
    }

    private async void ConnectPipe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await _pipeClient.ConnectAsync(PipeConnectTimeoutMs, CancellationToken.None);

            var systemName = RtSystemText.Text.Trim();
            var systemNames = string.IsNullOrEmpty(systemName) ? Array.Empty<string>() : new[] { systemName };

            await _pipeClient.SubscribeAsync(
                systemNames,
                filter: "",
                languageId: AlarmTesterOpenPipeClient.LanguageEnUs,
                responseTimeoutMs: PipeSubscribeTimeoutMs,
                CancellationToken.None);

            SetPipeStatus("OpenPipe subscribed", connected: true);
        }
        catch (Exception ex)
        {
            SetPipeStatus($"OpenPipe failed: {ex.Message}", connected: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshStates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            await RefreshStatesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshStatesAsync(CancellationToken cancellationToken)
    {
        if (!_s7Client.IsConnected)
        {
            return;
        }

        foreach (var testCase in _plan.Cases.Where(item => item.SourceKind == AlarmTestSourceKind.DigitalInput && item.SimulatorAddress is not null))
        {
            testCase.CurrentBoolState = await _s7Client.ReadBoolAsync(testCase.SimulatorAddress!, cancellationToken);
        }

        RefreshModules();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshModules();

    private void ModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshModules();

    private void SignalsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: AlarmTestCase selected })
        {
            _selectedSignal = selected;
        }
    }

    private async void ToggleSignal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AlarmTestCase testCase)
        {
            return;
        }

        _selectedSignal = testCase;

        if (testCase.SimulatorAddress is null)
        {
            StatusText.Text = "Simulator address is not resolved for this signal.";
            return;
        }

        if (!_s7Client.IsConnected)
        {
            StatusText.Text = "Connect S7 first.";
            return;
        }

        if (!_pipeClient.IsSubscribed)
        {
            StatusText.Text = "Connect OpenPipe first so program alarms can be captured.";
            return;
        }

        CancelPendingWait();
        _pollCts = new CancellationTokenSource();

        try
        {
            SetBusy(true);
            _events.Clear();
            var token = _pollCts.Token;

            // Reset any latched alarm from the previous test before re-triggering.
            await SendAlarmResetAsync(token);

            _pipeClient.ClearBuffer();
            var startedUtc = DateTimeOffset.UtcNow;

            var current = testCase.CurrentBoolState ?? await _s7Client.ReadBoolAsync(testCase.SimulatorAddress, token);
            var newValue = !current;

            testCase.CurrentBoolState = newValue;
            testCase.LastActionUtc = startedUtc;
            testCase.Status = AlarmTestStatus.Waiting;
            testCase.LastResult = "Waiting...";
            MarkLastTested(testCase);
            RefreshModules();

            await _s7Client.WriteAsync(testCase.SimulatorAddress, newValue ? "TRUE" : "FALSE", token);
            StatusText.Text = $"Wrote {testCase.SimulatorAddressLabel} := {(newValue ? "TRUE" : "FALSE")}. Waiting for program alarms...";

            var alarms = await WaitForProgramAlarmsAsync(startedUtc, token);
            foreach (var alarm in alarms)
            {
                _events.Add(alarm);
            }

            testCase.Status = alarms.Count > 0 ? AlarmTestStatus.Passed : AlarmTestStatus.Failed;
            testCase.LastResult = alarms.Count > 0 ? $"{alarms.Count} alarm(s)" : "No alarm";
            StatusText.Text = alarms.Count > 0
                ? $"{alarms.Count} program alarm(s) after toggling {testCase.SignalName}."
                : $"No program alarm within {AlarmPollTimeout.TotalSeconds:0}s after toggling {testCase.SignalName}.";
        }
        catch (OperationCanceledException)
        {
            if (testCase.Status == AlarmTestStatus.Waiting)
            {
                testCase.Status = AlarmTestStatus.Ready;
                testCase.LastResult = "Cancelled";
            }

            StatusText.Text = "Alarm wait cancelled.";
        }
        catch (Exception ex)
        {
            testCase.Status = AlarmTestStatus.Failed;
            testCase.LastResult = ex.Message;
            StatusText.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            RefreshModules();
            UpdateSummary();
            SetBusy(false);
        }
    }

    private async Task SendAlarmResetAsync(CancellationToken cancellationToken)
    {
        var resetTag = ResetTagBox.Text.Trim();
        if (string.IsNullOrEmpty(resetTag))
        {
            return;
        }

        try
        {
            await _pipeClient.WriteTagAsync(resetTag, ResetValue, PipeRequestTimeoutMs, cancellationToken);
            await Task.Delay(300, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't abort the test on a reset hiccup, but make it visible.
            StatusText.Text = $"Alarm reset to '{resetTag}' failed: {ex.Message}";
        }
    }

    private void MarkLastTested(AlarmTestCase testCase)
    {
        foreach (var item in _plan.Cases)
        {
            item.IsLastTested = false;
        }

        testCase.IsLastTested = true;
    }

    private void CancelPendingWait()
    {
        if (_selectedSignal?.Status == AlarmTestStatus.Waiting)
        {
            _selectedSignal.Status = AlarmTestStatus.Ready;
            _selectedSignal.LastResult = "Cancelled";
        }

        _pollCts?.Cancel();
    }

    private async Task<List<PlcAlarmEvent>> WaitForProgramAlarmsAsync(DateTimeOffset startedUtc, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AlarmPollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(AlarmPollInterval, cancellationToken);

            var program = PrepareForDisplay(_pipeClient.AlarmsReceivedSince(startedUtc));
            if (program.Count > 0)
            {
                return program;
            }
        }

        return PrepareForDisplay(_pipeClient.AlarmsReceivedSince(startedUtc));
    }

    private static List<PlcAlarmEvent> PrepareForDisplay(IEnumerable<PlcAlarmEvent> alarms)
    {
        // Only real raises (Incoming) of program-alarm classes (*_ALM), collapsing
        // the duplicate notifications WinCC pushes for one event. DI-event classes
        // and clear/normal transitions are filtered out.
        return alarms
            .Where(alarm => alarm.IsIncoming && alarm.IsProgramAlarmClass)
            .GroupBy(alarm => $"{alarm.Id}|{alarm.AlarmNumber}")
            .Select(group => group.First())
            .OrderBy(alarm => alarm.ReceivedUtc)
            .ToList();
    }

    private void UpdateSummary()
    {
        var plcName = (PlcSelector.SelectedItem as AlarmTestPlcOption)?.Name ?? "—";
        SummaryText.Text = $"PLC: {plcName}  ·  {_plan.DigitalModules.Sum(module => module.Signals.Count)} DI signal(s)";
    }

    private void SetS7Status(string text, bool connected)
    {
        S7ConnectionText.Text = text;
        S7ConnectionText.Foreground = connected ? HeaderOkBrush : HeaderTextBrush;
        S7Dot.Fill = connected ? ConnectedBrush : DisconnectedBrush;
    }

    private void SetPipeStatus(string text, bool connected)
    {
        PipeConnectionText.Text = text;
        PipeConnectionText.Foreground = connected ? HeaderOkBrush : HeaderTextBrush;
        PipeDot.Fill = connected ? ConnectedBrush : DisconnectedBrush;
    }

    private void SetBusy(bool isBusy)
    {
        ConnectS7Button.IsEnabled = !isBusy;
        ConnectPipeButton.IsEnabled = !isBusy;
        BrowseResetButton.IsEnabled = !isBusy;
        PlcSelector.IsEnabled = !isBusy;
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _s7Client.Dispose();
        _pipeClient.Dispose();
        base.OnClosed(e);
    }
}
