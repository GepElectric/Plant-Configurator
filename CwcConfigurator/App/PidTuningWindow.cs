using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.PidTuning;
using CwcConfigurator.Services.Simulation;
using Microsoft.Win32;

namespace CwcConfigurator.App;

/// <summary>
/// Tools → PID auto-tuning: connects to the PLC, records the controller output and the measurement of one PID
/// (REG_INTF, 100 ms) while the user makes a step (output in manual or reference in automatic), fits the process
/// model of the measurement's profile and proposes GAIN / TI / TD in the form of the PLC PID. The proposal is
/// written into the project's parameters on request — the PLC itself is only read.
/// </summary>
public sealed class PidTuningWindow : Window
{
    private readonly CwcProject _project;
    private readonly Action _changed;
    private readonly ComboBox _database = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0), DisplayMemberPath = nameof(PlcDatabase.Name) };
    private readonly TextBox _ip = new() { Width = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _rack = new() { Width = 30, Text = "0", Margin = new Thickness(0, 0, 4, 0) };
    private readonly TextBox _slot = new() { Width = 30, Text = "1", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _connect = new() { Content = "Connect", Padding = new Thickness(10, 2, 10, 2) };
    private readonly TextBlock _connection = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.DimGray };
    private readonly ComboBox _controller = new() { Width = 420, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _profile = new() { Width = 360 };
    private readonly ComboBox _element = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _regDb = new() { Width = 50, Text = PidTuner.RegInterfaceDb.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _measAddress = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Custom measurement: MEAS#ID of the project (e.g. MEAS#12 — gate position) or DB.offset of a REAL in %" };
    private readonly TextBox _outAddress = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Custom controller output: MEAS#ID or DB.offset of a REAL in %" };
    private readonly TextBlock _current = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _record = new() { Content = "Start recording", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _recording = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _chart = new() { Height = 230, Background = Brushes.White, ClipToBounds = true };
    private readonly ComboBox _aggressiveness = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _identify = new() { Content = "Identify and propose", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _write = new() { Content = "Write proposal to project", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Canvas _preview = new() { Height = 150, Background = Brushes.White, ClipToBounds = true };
    private readonly SimulatorS7Client _client = new();
    private readonly List<(double T, double Ref, double Out, double Meas)> _samples = [];
    private CancellationTokenSource? _stop;
    private string? _logPath;
    private ProcessModel? _model;
    private PidProposal? _proposal;

    public PidTuningWindow(CwcProject project, Action changed)
    {
        _project = project;
        _changed = changed;
        Title = "PID auto-tuning";
        Width = 1100;
        Height = 950;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        root.Children.Add(new TextBlock
        {
            Text = "Connect to the PLC, choose the controller, start the recording and make a step: in manual change the output, or in automatic change the reference. " +
                   "When the measurement is steady again, stop and press Identify. The PLC is only read; the proposal goes into the project's parameters (DB PAR) when you write it.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var plc = Row("PLC");
        plc.Children.Add(_database);
        plc.Children.Add(new TextBlock { Text = "IP", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        plc.Children.Add(_ip);
        plc.Children.Add(new TextBlock { Text = "Rack / slot", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        plc.Children.Add(_rack);
        plc.Children.Add(_slot);
        plc.Children.Add(_connect);
        plc.Children.Add(_connection);
        root.Children.Add(plc);

        var controller = Row("Controller");
        controller.Children.Add(_controller);
        controller.Children.Add(new TextBlock { Text = "Measurement kind", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        controller.Children.Add(_profile);
        root.Children.Add(controller);
        root.Children.Add(_current);

        var signals = Row("Signals");
        signals.Children.Add(new TextBlock { Text = "REG_INTF", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        signals.Children.Add(_element);
        signals.Children.Add(new TextBlock { Text = "DB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        signals.Children.Add(_regDb);
        signals.Children.Add(new TextBlock { Text = "or measurement / output (MEAS#ID or DB.offset)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        signals.Children.Add(_measAddress);
        signals.Children.Add(_outAddress);
        root.Children.Add(signals);
        root.Children.Add(_hint);

        var recordRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        recordRow.Children.Add(_record);
        var save = new Button { Content = "Save recording (CSV)…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => SaveCsv();
        recordRow.Children.Add(save);
        recordRow.Children.Add(_recording);
        root.Children.Add(recordRow);
        root.Children.Add(new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1), Child = _chart });
        root.Children.Add(new TextBlock { Text = "blue: measurement · orange: controller output · grey: reference · green dashed: fitted model", Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 8) });

        var tuneRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        tuneRow.Children.Add(new TextBlock { Text = "Tuning", VerticalAlignment = VerticalAlignment.Center, Width = 90, FontWeight = FontWeights.SemiBold });
        tuneRow.Children.Add(_aggressiveness);
        tuneRow.Children.Add(_identify);
        tuneRow.Children.Add(_write);
        root.Children.Add(tuneRow);
        root.Children.Add(_result);
        root.Children.Add(new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1), Child = _preview });
        root.Children.Add(new TextBlock { Text = "Expected closed-loop response to a 1 % reference step with the proposal (model): blue measurement, orange output.", Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        _aggressiveness.ItemsSource = PidTuner.Aggressiveness.Select(item => item.Name).ToList();
        _aggressiveness.SelectedIndex = 1;
        _aggressiveness.SelectionChanged += (_, _) => { if (_model is not null) Propose(); };
        _profile.ItemsSource = PidTuner.Profiles;
        _profile.SelectionChanged += (_, _) => ShowProfile();
        _element.ItemsSource = new[] { "(custom addresses)" }.Concat(PidTuner.RegInterface.Select(item => item.Element)).ToList();
        _database.ItemsSource = project.PlcDatabases;
        _database.SelectionChanged += (_, _) => DatabaseChanged();
        _controller.SelectionChanged += (_, _) => ControllerChanged();
        _connect.Click += async (_, _) => await Guard(ConnectAsync);
        _record.Click += async (_, _) => await Guard(RecordAsync);
        _identify.Click += (_, _) => Guard(Identify);
        _write.Click += (_, _) => Guard(WriteToProject);
        _chart.SizeChanged += (_, _) => DrawRecording();
        _preview.SizeChanged += (_, _) => DrawPreview();
        _database.SelectedItem = project.PlcDatabases.FirstOrDefault(item => item.Id == project.ActivePlcDatabaseId) ?? project.PlcDatabases.FirstOrDefault();
        Closing += (_, _) =>
        {
            _stop?.Cancel();
            _client.Dispose();
        };
    }

    private static StackPanel Row(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        return row;
    }

    private void DatabaseChanged()
    {
        if (_database.SelectedItem is not PlcDatabase database)
        {
            return;
        }

        _ip.Text = database.IpAddress;
        var controllers = PidTuner.Controllers(database.DbCatalog);
        _controller.ItemsSource = controllers;
        _controller.SelectedIndex = controllers.Count > 0 ? 0 : -1;
        if (controllers.Count == 0)
        {
            _current.Text = "This PLC database has no PID parameters (structures PID_* in DB PAR).";
        }
    }

    private void ControllerChanged()
    {
        if (_controller.SelectedItem is not PidController controller)
        {
            return;
        }

        _profile.SelectedItem = PidTuner.ProfileFor(controller.Structure);
        var element = PidTuner.RegElementFor(controller.Structure);
        _element.SelectedItem = element ?? "(custom addresses)";
        _current.Text = $"Now: GAIN = {controller.Gain.ToString("0.####", CultureInfo.InvariantCulture)} · TI = {PidTuner.TimeText(controller.Ti)} · TD = {PidTuner.TimeText(controller.Td)} · " +
                        $"P {(controller.PAction ? "on" : "off")}, I {(controller.IAction ? "on" : "off")}, D {(controller.DAction ? "on" : "off")}" +
                        (element is null ? " · no REG_INTF element by name: give the measurement and output addresses." : "");
        _model = null;
        _proposal = null;
        _write.IsEnabled = false;
        _result.Text = "";
        DrawPreview();
    }

    private void ShowProfile()
    {
        if (_profile.SelectedItem is LoopProfile profile)
        {
            _hint.Text = $"{profile.Hint}  Suggested step: {profile.StepPercent:0.#} %, record about {Duration(profile.RecordSeconds)}.";
        }
    }

    private static string Duration(double seconds) => seconds >= 120 ? $"{seconds / 60:0} min" : $"{seconds:0} s";

    private async Task ConnectAsync()
    {
        if (_client.IsConnected)
        {
            _stop?.Cancel();
            _client.Disconnect();
            _connect.Content = "Connect";
            _connection.Text = "not connected";
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _client.ConnectAsync(_ip.Text.Trim(), short.Parse(_rack.Text.Trim(), CultureInfo.InvariantCulture), short.Parse(_slot.Text.Trim(), CultureInfo.InvariantCulture));
            _connect.Content = "Disconnect";
            _connection.Text = $"connected to {_ip.Text.Trim()}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Addresses of reference (optional), output and measurement: REG_INTF element or custom DB.offset.</summary>
    private ((int Db, int Offset)? Ref, (int Db, int Offset) Out, (int Db, int Offset) Meas) Addresses()
    {
        if (_element.SelectedItem is string element && PidTuner.RegInterface.FirstOrDefault(item => item.Element == element) is { Element: not null } entry)
        {
            var db = int.Parse(_regDb.Text.Trim(), CultureInfo.InvariantCulture);
            var start = entry.Offset + PidTuner.RegValues;
            return ((db, start), (db, start + 4), (db, start + 8));
        }

        static (int, int) Parse(string text, string what) =>
            PidTuner.ParseAddress(text) ?? throw new InvalidOperationException($"Give the {what} as MEAS#ID (a measurement of the project) or DB.offset of a REAL in %, e.g. MEAS#12 or 1003.716.");

        return (null, Parse(_outAddress.Text, "controller output"), Parse(_measAddress.Text, "measurement"));
    }

    private async Task RecordAsync()
    {
        if (_stop is not null)
        {
            _stop.Cancel();
            return;
        }

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Connect to the PLC first.");
        }

        var (reference, output, measurement) = Addresses();
        _samples.Clear();
        _model = null;
        _proposal = null;
        _write.IsEnabled = false;
        _result.Text = "";
        _stop = new CancellationTokenSource();
        _record.Content = "Stop recording";
        var started = DateTime.UtcNow;

        // Every sample goes to a log file at once, so a recording survives a lost connection or a closed window.
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plant Configurator", "PID tuning");
        Directory.CreateDirectory(folder);
        _logPath = System.IO.Path.Combine(folder, $"{(_controller.SelectedItem as PidController)?.Structure ?? "PID"}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var culture = CultureInfo.CurrentCulture;
        var separator = culture.TextInfo.ListSeparator;
        await using var log = new StreamWriter(_logPath, false, new UTF8Encoding(true)) { AutoFlush = false };
        await log.WriteLineAsync(string.Join(separator, "Time [s]", "Reference [%]", "Output [%]", "Measurement [%]", "Communication"));
        var errors = 0;
        var lastError = "";
        var slowest = 0.0;
        var lastFlush = DateTime.UtcNow;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tick = DateTime.UtcNow;
                double r = double.NaN, o = double.NaN, m = double.NaN;
                var status = "OK";
                try
                {
                    if (!_client.IsConnected)
                    {
                        await _client.ConnectAsync(_ip.Text.Trim(), short.Parse(_rack.Text.Trim(), CultureInfo.InvariantCulture), short.Parse(_slot.Text.Trim(), CultureInfo.InvariantCulture));
                        status = "reconnected";
                    }

                    if (reference is { } refAddress && refAddress.Db == output.Db && output.Offset == refAddress.Offset + 4 && measurement.Offset == refAddress.Offset + 8)
                    {
                        var bytes = await _client.ReadBytesAsync(refAddress.Db, refAddress.Offset, 12);
                        (r, o, m) = (Real(bytes, 0), Real(bytes, 4), Real(bytes, 8));
                    }
                    else
                    {
                        o = Real(await _client.ReadBytesAsync(output.Db, output.Offset, 4), 0);
                        m = Real(await _client.ReadBytesAsync(measurement.Db, measurement.Offset, 4), 0);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A gap in the recording; the connection is opened again on the next sample.
                    errors++;
                    lastError = ex.Message;
                    status = "lost: " + ex.Message.Replace(separator, " ");
                    try { _client.Disconnect(); } catch { }
                }

                var time = (tick - started).TotalSeconds;
                _samples.Add((time, r, o, m));
                await log.WriteLineAsync(string.Join(separator, time.ToString("0.###", culture), Text(r), Text(o), Text(m), status));
                if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 1)
                {
                    await log.FlushAsync();
                    lastFlush = DateTime.UtcNow;
                }

                var took = (DateTime.UtcNow - tick).TotalMilliseconds;
                slowest = Math.Max(slowest, took);
                _recording.Text = $"{_samples.Count} samples · {time:0} s · output {o:0.##} % · measurement {m:0.##} % · slowest read {slowest:0} ms" +
                                  (errors > 0 ? $" · communication lost {errors}× ({lastError})" : " · communication OK") +
                                  $" · saved to {_logPath}";
                _recording.Foreground = errors > 0 ? Brushes.Firebrick : Brushes.Black;
                if (_samples.Count % 5 == 0)
                {
                    DrawRecording();
                }

                var wait = (int)(100 - took);
                if (wait > 0 || status != "OK")
                {
                    try { await Task.Delay(status.StartsWith("lost", StringComparison.Ordinal) ? 1000 : wait, _stop.Token); } catch (TaskCanceledException) { }
                }
            }
        }
        finally
        {
            await log.FlushAsync();
            _stop = null;
            _record.Content = "Start recording";
            DrawRecording();
        }
    }

    private static string Text(double value) => double.IsFinite(value) ? value.ToString("0.####", CultureInfo.CurrentCulture) : "";

    private static double Real(byte[] bytes, int offset) =>
        BitConverter.Int32BitsToSingle((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);

    private void Identify()
    {
        if (_profile.SelectedItem is not LoopProfile profile)
        {
            return;
        }

        // Gaps of a lost connection are bridged on an even time grid.
        var (time, output, measurement) = PidTuner.Resample(_samples.Select(sample => sample.T).ToList(), _samples.Select(sample => sample.Out).ToList(), _samples.Select(sample => sample.Meas).ToList());
        _model = PidTuner.Identify(time, output, measurement, profile.Process);
        Propose();
        DrawRecording();
    }

    private void Propose()
    {
        if (_model is null || _profile.SelectedItem is not LoopProfile profile || _controller.SelectedItem is not PidController controller)
        {
            return;
        }

        var factor = PidTuner.Aggressiveness[Math.Max(0, _aggressiveness.SelectedIndex)].Factor;
        _proposal = PidTuner.Tune(_model, profile, factor);
        var quality = _model.Fit < 0.8 ? "  ⚠ The model fits poorly (R² < 0.8): record longer, with a clearer step and a steady start." : "";
        _result.Text =
            $"Model: {_model.Describe()}.{quality}\n" +
            $"Proposal ({_aggressiveness.SelectedItem}, closed-loop time ≈ {_proposal.ClosedLoopTime:0.#} s): GAIN = {_proposal.Gain.ToString("0.####", CultureInfo.InvariantCulture)} · " +
            $"TI = {(_proposal.IAction ? PidTuner.TimeText(_proposal.Ti) : "— (I off)")} · TD = {PidTuner.TimeText(_proposal.Td)} · P on, I {(_proposal.IAction ? "on" : "off")}, D off\n" +
            $"Now:      GAIN = {controller.Gain.ToString("0.####", CultureInfo.InvariantCulture)} · TI = {PidTuner.TimeText(controller.Ti)} · TD = {PidTuner.TimeText(controller.Td)} · " +
            $"P {(controller.PAction ? "on" : "off")}, I {(controller.IAction ? "on" : "off")}, D {(controller.DAction ? "on" : "off")}";
        _write.IsEnabled = true;
        DrawPreview();
    }

    private void WriteToProject()
    {
        if (_proposal is null || _controller.SelectedItem is not PidController controller)
        {
            return;
        }

        if (MessageBox.Show(this, $"Write the proposal into the parameters of {controller.Structure} in the project?\n\n" +
                                  "Then push DB PAR to TIA / the PLC as usual (TIA Sync). The PLC is not changed by this tool.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var changed = PidTuner.Apply(controller, _proposal);
        _changed();
        ControllerChanged();
        MessageBox.Show(this, changed.Count == 0 ? "The parameters already had these values." : "Written:\n" + string.Join("\n", changed), Title);
    }

    private void SaveCsv()
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"{(_controller.SelectedItem as PidController)?.Structure ?? "PID"}_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var separator = culture.TextInfo.ListSeparator;
        var text = new StringBuilder();
        text.AppendLine(string.Join(separator, "Time [s]", "Reference [%]", "Output [%]", "Measurement [%]"));
        foreach (var sample in _samples)
        {
            text.AppendLine(string.Join(separator, sample.T.ToString("0.###", culture), double.IsFinite(sample.Ref) ? sample.Ref.ToString("0.####", culture) : "",
                sample.Out.ToString("0.####", culture), sample.Meas.ToString("0.####", culture)));
        }

        File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
    }

    // ---------------------------------------------------------------- charts

    private void DrawRecording()
    {
        _chart.Children.Clear();
        if (_samples.Count < 2 || _chart.ActualWidth < 10)
        {
            return;
        }

        var t0 = _samples[0].T;
        var t1 = Math.Max(_samples[^1].T, t0 + 1);
        var values = _samples.SelectMany(sample => new[] { sample.Out, sample.Meas, sample.Ref }).Where(double.IsFinite).ToList();
        var (low, high) = Range(values);
        Plot(_chart, _samples.Select(sample => (sample.T - t0, sample.Ref)), t1 - t0, low, high, Brushes.Gray, 1);
        Plot(_chart, _samples.Select(sample => (sample.T - t0, sample.Out)), t1 - t0, low, high, Brushes.DarkOrange, 1.5);
        Plot(_chart, _samples.Select(sample => (sample.T - t0, sample.Meas)), t1 - t0, low, high, Brushes.RoyalBlue, 1.5);
        if (_model is not null)
        {
            Plot(_chart, ModelResponse(_model), t1 - t0, low, high, Brushes.SeaGreen, 1.5, dashed: true);
        }

        Label(_chart, $"{high:0.##} %", 4, 2);
        Label(_chart, $"{low:0.##} %", 4, _chart.ActualHeight - 16);
        Label(_chart, $"{t1 - t0:0} s", _chart.ActualWidth - 50, _chart.ActualHeight - 16);
    }

    /// <summary>The fitted model driven by the recorded output, for comparison with the measurement.</summary>
    private IEnumerable<(double, double)> ModelResponse(ProcessModel model)
    {
        var t0 = _samples[0].T;
        var state = 0.0;
        var integral = 0.0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var time = _samples[i].T - t0;
            var delayed = _samples.LastOrDefault(sample => sample.T - t0 <= time - model.DeadTime);
            var u = (delayed == default ? _samples[0].Out : delayed.Out) - model.U0;
            var dt = i == 0 ? 0 : _samples[i].T - _samples[i - 1].T;
            if (model.Kind == ProcessKind.Integrating)
            {
                integral += model.Gain * u * dt;
                state = integral;
            }
            else if (dt > 0)
            {
                state += (model.Gain * u - state) * (1 - Math.Exp(-dt / Math.Max(model.TimeConstant, dt)));
            }

            yield return (time, model.Y0 + state);
        }
    }

    private void DrawPreview()
    {
        _preview.Children.Clear();
        if (_model is null || _proposal is null || _preview.ActualWidth < 10)
        {
            return;
        }

        var duration = Math.Max(10 * _proposal.ClosedLoopTime, 6 * (_proposal.ClosedLoopTime + _model.DeadTime));
        var (time, y, u) = PidTuner.ClosedLoopStep(_model, _proposal.Gain, _proposal.Ti, _proposal.IAction, duration);
        var (low, high) = Range(y.Concat([0.0, 1.0]).ToList());
        Plot(_preview, time.Zip(Enumerable.Repeat(1.0, time.Length)), duration, low, high, Brushes.Gray, 1);
        Plot(_preview, time.Zip(y), duration, low, high, Brushes.RoyalBlue, 1.5);
        var (uLow, uHigh) = Range(u.ToList());
        Plot(_preview, time.Zip(u.Select(value => low + (value - uLow) / Math.Max(uHigh - uLow, 1e-9) * (high - low))), duration, low, high, Brushes.DarkOrange, 1);
        Label(_preview, $"{duration:0} s", _preview.ActualWidth - 60, _preview.ActualHeight - 16);
    }

    private static (double Low, double High) Range(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 1);
        }

        var low = values.Min();
        var high = values.Max();
        var margin = Math.Max((high - low) * 0.08, 0.05);
        return (low - margin, high + margin);
    }

    private static void Plot(Canvas canvas, IEnumerable<(double X, double Y)> points, double width, double low, double high, Brush brush, double thickness, bool dashed = false)
    {
        var line = new Polyline { Stroke = brush, StrokeThickness = thickness };
        if (dashed)
        {
            line.StrokeDashArray = [4, 3];
        }

        foreach (var (x, y) in points)
        {
            if (double.IsFinite(y))
            {
                line.Points.Add(new Point(x / Math.Max(width, 1e-9) * canvas.ActualWidth, canvas.ActualHeight - (y - low) / Math.Max(high - low, 1e-9) * canvas.ActualHeight));
            }
        }

        canvas.Children.Add(line);
    }

    private static void Label(Canvas canvas, string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.DimGray };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
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

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
