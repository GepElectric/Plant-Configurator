using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot.WPF;

namespace TiaTraceStudio;

public sealed class ChannelRow : Observable
{
    string tag = "", name = "", address = "", type = "", origin = "ručno", status = "", plcId = "";
    public string Tag { get => tag; set => Set(ref tag, value); }
    public string PlcId { get => plcId; set => Set(ref plcId, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string Address { get => address; set => Set(ref address, value); }
    public string Type { get => type; set => Set(ref type, value); }
    public string Origin { get => origin; set => Set(ref origin, value); }
    public string Status { get => status; set => Set(ref status, value); }
}

public sealed record RecorderSetup(
    TestSlot Slot, IReadOnlyList<PlcConnection> Plcs, PlcConnection DefaultPlc, IReadOnlyList<ChannelRow> Channels,
    Func<PlcConnection, PlcCatalog?> CatalogOf, string Folder, Func<string, short, short, IPlcReader> CreateReader,
    Action<string, TagAddress> RememberAddress, Action<IReadOnlyList<SlotChannel>> ChannelsUsed, Action<TraceData> Save);

/// <summary>
/// Records the measurements of one test directly from one or more PLCs (S7, ≥ 100 ms) and hands the result
/// to the project. Several PLCs (e.g. common load rejection of all units) share one time axis.
/// </summary>
public sealed class RecorderWindow : Window
{
    static readonly int[] Intervals = [100, 200, 500, 1000, 2000, 5000, 10000];
    static readonly string[] Colors = ["#2563EB", "#E88821", "#109C83", "#A855F7", "#E14D72", "#0891B2"];
    readonly RecorderSetup setup;
    readonly ObservableCollection<ChannelRow> rows;
    readonly TextBox ip = new() { Width = 140 }, rack = new() { Width = 40 }, slotBox = new() { Width = 40 };
    readonly ComboBox interval = new() { Width = 110, ItemsSource = Intervals.Select(i => i < 1000 ? $"{i} ms" : $"{i / 1000} s").ToArray() };
    readonly ComboBox connectionPlc = new() { Width = 200, DisplayMemberPath = nameof(PlcConnection.Name) };
    readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = true, Height = 220, HeadersVisibility = DataGridHeadersVisibility.Column };
    readonly ComboBox pickerPlc = new() { Width = 170, DisplayMemberPath = nameof(PlcConnection.Name) };
    readonly ComboBox measurementPicker = new() { IsEditable = true, Width = 460, DisplayMemberPath = nameof(PlantMeasurement.Display) };
    readonly WpfPlot plot = new();
    readonly TextBlock stats = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly Button start, stop, discard, test;
    readonly CheckBox wholeRecording = new() { Content = "Prikaži cijelu snimku (inače zadnjih 10 min)", VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    PlcRecorder? recorder; CancellationTokenSource? cancel; Task? running;
    bool finished;
    public bool IsRecording => running != null && !finished;

    public RecorderWindow(Window owner, RecorderSetup setup)
    {
        this.setup = setup; Owner = owner;
        Title = "Snimanje s PLC-a · " + setup.Slot.ChartTitle; Width = Dialogs.Width(1380); Height = Dialogs.Height(940); WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        Resources.MergedDictionaries.Add(owner.Resources);
        rows = new(setup.Channels);
        interval.SelectedIndex = setup.Slot.Kind is "HEAT_RUN" or "BEARING_RUN" ? 3 : 0;

        var layout = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = setup.Slot.Path, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        var connection = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        connectionPlc.ItemsSource = setup.Plcs; connectionPlc.SelectedItem = setup.DefaultPlc;
        connectionPlc.SelectionChanged += (_, _) => { StoreConnection(); ShowConnection(); };
        connection.Children.Add(Label("PLC")); connection.Children.Add(connectionPlc);
        connection.Children.Add(Label("IP")); connection.Children.Add(ip);
        connection.Children.Add(Label("Rack")); connection.Children.Add(rack);
        connection.Children.Add(Label("Slot")); connection.Children.Add(slotBox);
        connection.Children.Add(Label("Takt")); connection.Children.Add(interval);
        test = Button("Provjeri vezu i adrese", Test);
        connection.Children.Add(test);
        head.Children.Add(connection);
        head.Children.Add(new TextBlock { Text = "S7 komunikacija (PUT/GET) na neoptimizirane blokove; mjerenja iz DB1003. Čitaju se samo mjerenja s popisa. Mjerenja s više PLC-ova snimaju se istodobno, s jednom vremenskom osi. Vrijeme uzorka je s PC sata.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(head, Dock.Top); layout.Children.Add(head);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        start = Button("●  Pokreni snimanje", Start, primary: true);
        stop = Button("■  Zaustavi i spremi", () => _ = Finish(save: true)); stop.IsEnabled = false;
        discard = Button("Odbaci snimku", () => { if (MessageBox.Show(this, "Odbaciti snimku? Podaci se brišu.", "Snimanje", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) _ = Finish(save: false); }); discard.IsEnabled = false;
        buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(discard);
        DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons); bottom.Children.Add(stats);
        DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);

        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "PLC", ItemsSource = setup.Plcs, DisplayMemberPath = nameof(PlcConnection.Name), SelectedValuePath = nameof(PlcConnection.Id), SelectedValueBinding = new Binding(nameof(ChannelRow.PlcId)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tag", Binding = new Binding(nameof(ChannelRow.Tag)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Naziv", Binding = new Binding(nameof(ChannelRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Adresa", Binding = new Binding(nameof(ChannelRow.Address)), Width = 130 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Tip", SelectedItemBinding = new Binding(nameof(ChannelRow.Type)), ItemsSource = new[] { "" }.Concat(S7Address.Types).ToArray(), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Izvor adrese", Binding = new Binding(nameof(ChannelRow.Origin)), IsReadOnly = true, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Provjera", Binding = new Binding(nameof(ChannelRow.Status)), IsReadOnly = true, Width = 140 });
        grid.InitializingNewItem += (_, e) => { if (e.NewItem is ChannelRow row) row.PlcId = setup.DefaultPlc.Id; };
        grid.ItemsSource = rows; body.Children.Add(grid);
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        pickerPlc.ItemsSource = setup.Plcs.Where(p => setup.CatalogOf(p) != null).ToList(); pickerPlc.SelectedItem = setup.CatalogOf(setup.DefaultPlc) != null ? setup.DefaultPlc : null;
        pickerPlc.SelectionChanged += (_, _) => FillPicker();
        SearchPicker.Attach(measurementPicker, m => ((PlantMeasurement)m).Display, AddMeasurement);
        add.Children.Add(Label("Dodaj mjerenje · PLC")); add.Children.Add(pickerPlc); add.Children.Add(measurementPicker);
        add.Children.Add(Button("Dodaj", AddMeasurement));
        add.Children.Add(Button("Ukloni odabrani red", () => { if (grid.SelectedItem is ChannelRow row) rows.Remove(row); }));
        add.Children.Add(new TextBlock { Text = "Ručni tag: upiši tag i adresu u zadnji red tablice.", Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) });
        Grid.SetRow(add, 1); body.Children.Add(add);
        var chart = new DockPanel(); var chartBar = new StackPanel { Orientation = Orientation.Horizontal }; chartBar.Children.Add(wholeRecording);
        DockPanel.SetDock(chartBar, Dock.Top); chart.Children.Add(chartBar); chart.Children.Add(plot);
        Grid.SetRow(chart, 2); body.Children.Add(chart);
        layout.Children.Add(body);
        Content = layout;
        FillPicker(); ShowConnection();

        refresh.Tick += (_, _) => UpdateView();
        Closing += (_, e) =>
        {
            if (!IsRecording) return;
            var answer = MessageBox.Show(this, "Snimanje je u tijeku. Zaustaviti i spremiti snimku?", "Snimanje", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            e.Cancel = true;
            if (answer == MessageBoxResult.Yes) _ = Finish(save: true);
            else if (answer == MessageBoxResult.No) _ = Finish(save: false);
        };
        int plcCount = rows.Select(r => r.PlcId).Distinct().Count();
        stats.Text = rows.Count == 0 ? "Test nema odabranih mjerenja. Dodaj mjerenja iz baze ili upiši tagove s adresama." : $"{rows.Count} mjerenja s {plcCount} PLC-a. Provjeri vezu, zatim pokreni snimanje.";
    }

    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) };
    Button Button(string text, System.Action action, bool primary = false)
    {
        var button = new Button { Content = text };
        if (primary && TryFindResource("Primary") is Style style) button.Style = style;
        button.Click += (_, _) => action(); return button;
    }

    PlcConnection? shownPlc;
    void ShowConnection()
    {
        shownPlc = connectionPlc.SelectedItem as PlcConnection;
        ip.Text = shownPlc?.Ip ?? ""; rack.Text = (shownPlc?.Rack ?? 0).ToString(); slotBox.Text = (shownPlc?.Slot ?? 1).ToString();
    }
    void StoreConnection()
    {
        if (shownPlc == null) return;
        shownPlc.Ip = ip.Text.Trim();
        if (short.TryParse(rack.Text, out short r)) shownPlc.Rack = r;
        if (short.TryParse(slotBox.Text, out short s)) shownPlc.Slot = s;
    }

    void FillPicker() => measurementPicker.ItemsSource = pickerPlc.SelectedItem is PlcConnection plc ? setup.CatalogOf(plc)?.AllItems.ToList() : null;

    void AddMeasurement()
    {
        if (pickerPlc.SelectedItem is not PlcConnection plc || setup.CatalogOf(plc) is not PlcCatalog source) { stats.Text = "Odaberi PLC povezan s bazom Plant Configuratora."; return; }
        var measurement = SearchPicker.Picked<PlantMeasurement>(measurementPicker);
        if (measurement == null) { stats.Text = "Nema mjerenja koje odgovara upisanom tekstu."; return; }
        if (rows.Any(r => r.PlcId == plc.Id && TagMatcher.Matches(r.Tag, measurement.Tag))) { stats.Text = "Mjerenje je već na popisu."; return; }
        rows.Add(new ChannelRow { PlcId = plc.Id, Tag = measurement.Tag, Name = measurement.Name, Address = source.AddressOf(measurement).ToString(), Type = source.AddressOf(measurement).Type, Origin = "Plant Configurator" });
        stats.Text = $"Dodano: {measurement.Display}"; measurementPicker.SelectedItem = null; measurementPicker.Text = ""; SearchPicker.ClearFilter(measurementPicker);
    }

    PlcConnection PlcOf(ChannelRow row) => setup.Plcs.FirstOrDefault(p => p.Id == row.PlcId) ?? setup.DefaultPlc;

    /// <summary>Valid channels grouped by PLC. Catalogue measurements always take the address from their PLC's database.</summary>
    List<(PlcConnection Plc, List<RecordingChannel> Channels)> Channels(bool requireAll)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        StoreConnection();
        var result = new List<(PlcConnection Plc, List<RecordingChannel> Channels)>(); var problems = new List<string>();
        var used = rows.Where(r => !string.IsNullOrWhiteSpace(r.Tag) || !string.IsNullOrWhiteSpace(r.Address)).ToList();
        bool multi = used.Select(PlcOf).Distinct().Count() > 1;
        foreach (var row in used)
        {
            var plc = PlcOf(row);
            if (string.IsNullOrWhiteSpace(row.Tag)) { row.Status = "nedostaje tag"; problems.Add(row.Address); continue; }
            if (setup.CatalogOf(plc) is PlcCatalog source && source.Find(row.Tag) is PlantMeasurement m)
            { var known = source.AddressOf(m); row.Address = known.ToString(); row.Type = known.Type; row.Origin = "Plant Configurator"; }
            if (string.IsNullOrWhiteSpace(row.Address)) { row.Status = "nema adrese"; problems.Add($"{plc.Name}: {row.Tag}"); continue; }
            try
            {
                var address = S7Address.Parse(row.Address, row.Type);
                var group = result.FirstOrDefault(g => g.Plc == plc);
                if (group.Plc == null) { group = (plc, []); result.Add(group); }
                if (group.Channels.Any(c => TagMatcher.Matches(c.Tag, row.Tag))) { row.Status = "dvostruki tag"; problems.Add(row.Tag); continue; }
                group.Channels.Add(new RecordingChannel(row.Tag.Trim(), address, multi ? plc.Name : "")); row.Address = address.ToString(); row.Status = "";
            }
            catch (Exception ex) { row.Status = "neispravna adresa"; problems.Add($"{row.Tag}: {ex.Message}"); }
        }
        if (problems.Count > 0 && requireAll)
        {
            if (result.Count == 0) throw new InvalidDataException("Nijedno mjerenje nema ispravnu adresu.\n" + string.Join("\n", problems));
            if (MessageBox.Show(this, $"{problems.Count} mjerenja se ne mogu snimati (nema ispravne adrese):\n\n{string.Join("\n", problems.Take(12))}\n\nNastaviti bez njih?", "Snimanje", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return [];
        }
        foreach (var (plc, _) in result)
            if (string.IsNullOrWhiteSpace(plc.Ip)) throw new InvalidDataException($"Upiši IP adresu za {plc.Name} (odaberi ga u polju PLC gore).");
        return result;
    }

    void Remember(List<(PlcConnection Plc, List<RecordingChannel> Channels)> groups)
    {
        foreach (var row in rows.Where(r => r.Origin != "Plant Configurator" && !string.IsNullOrWhiteSpace(r.Address)))
            if (groups.SelectMany(g => g.Channels).FirstOrDefault(c => TagMatcher.Matches(c.Tag, row.Tag)) is RecordingChannel channel)
                setup.RememberAddress(row.Tag, new TagAddress { Address = channel.Address.ToString(), Type = channel.Address.Type });
        setup.ChannelsUsed(groups.SelectMany(g => g.Channels.Select(c => new SlotChannel(TagMatcher.Normalize(c.Tag), g.Plc.Id))).ToList());
    }

    async void Test()
    {
        try
        {
            var groups = Channels(false); if (groups.Count == 0) throw new InvalidDataException("Nema mjerenja s ispravnom adresom.");
            test.IsEnabled = false; start.IsEnabled = false; stats.Text = $"Spajanje na {string.Join(", ", groups.Select(g => $"{g.Plc.Name} ({g.Plc.Ip})"))}…";
            var results = await Task.Run(() => groups.Select(g =>
            {
                try
                {
                    using var reader = setup.CreateReader(g.Plc.Ip, g.Plc.Rack, g.Plc.Slot);
                    reader.Connect();
                    return g.Channels.Select(c => { try { var data = reader.Read(c.Address.Area, c.Address.Db, c.Address.Byte, c.Address.Size); double v = c.Address.Decode(data); return double.IsNaN(v) ? "= NaN (neispravno)" : "= " + v.ToString("G6", CultureInfo.CurrentCulture); } catch (Exception ex) { return "greška: " + ex.Message; } }).ToList();
                }
                catch (Exception ex) { return g.Channels.Select(_ => "nema veze: " + ex.Message).ToList(); }
            }).ToList());
            int ok = 0, all = 0;
            for (int i = 0; i < groups.Count; i++)
                for (int j = 0; j < groups[i].Channels.Count; j++)
                {
                    all++; if (results[i][j].StartsWith("=")) ok++;
                    var plc = groups[i].Plc; var tag = groups[i].Channels[j].Tag;
                    if (rows.FirstOrDefault(r => PlcOf(r) == plc && TagMatcher.Matches(r.Tag, tag)) is ChannelRow row) row.Status = results[i][j];
                }
            Remember(groups);
            stats.Text = $"Pročitano {ok} od {all} mjerenja s {groups.Count} PLC-a.";
        }
        catch (Exception ex) { stats.Text = "Provjera nije uspjela: " + ex.Message; MessageBox.Show(this, ex.Message, "Veza s PLC-om", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { test.IsEnabled = !IsRecording; start.IsEnabled = !IsRecording && !finished; }
    }

    void Start()
    {
        try
        {
            var groups = Channels(true); if (groups.Count == 0) return;
            Remember(groups);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safe = string.Concat(setup.Slot.ChartTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(setup.Folder, $"{stamp} {safe}.csv");
            var sources = groups.Select(g => new PlcSource(g.Plc.Name, setup.CreateReader(g.Plc.Ip, g.Plc.Rack, g.Plc.Slot), g.Channels)).ToList();
            recorder = new PlcRecorder(sources, TimeSpan.FromMilliseconds(Intervals[Math.Max(0, interval.SelectedIndex)]), path);
            cancel = new CancellationTokenSource(); var token = cancel.Token; var active = recorder;
            running = Task.Factory.StartNew(() => active.Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            foreach (var control in new Control[] { start, test, ip, rack, slotBox, interval, grid, measurementPicker, pickerPlc, connectionPlc }) control.IsEnabled = false;
            stop.IsEnabled = true; discard.IsEnabled = true; refresh.Start(); UpdateView();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Snimanje", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    Task Finish(bool save)
    {
        if (recorder == null || running == null || finished) return Task.CompletedTask;
        stop.IsEnabled = false; discard.IsEnabled = false; refresh.Stop();
        cancel!.Cancel();
        // The recording thread finishes in the background; the result is handled on the UI thread.
        var done = new TaskCompletionSource();
        running.ContinueWith(_ => Dispatcher.BeginInvoke(() => { try { Complete(save); } finally { done.SetResult(); } }));
        return done.Task;
    }

    void Complete(bool save)
    {
        if (recorder == null) return;
        finished = true; UpdateView();
        try
        {
            if (save) { var data = recorder.ToTraceData(); recorder.Dispose(); setup.Save(data); }
            else { recorder.Dispose(); File.Delete(recorder.CsvPath); }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + $"\n\nPodaci ostaju u datoteci:\n{recorder.CsvPath}", "Snimanje", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }

    void UpdateView()
    {
        if (recorder == null) return;
        var (t, values) = recorder.Snapshot(wholeRecording.IsChecked == true ? null : 600);
        plot.Plot.Clear(); plot.Plot.Legend.ManualItems.Clear();
        for (int i = 0; i < values.Length && t.Length > 0; i++)
        {
            var scatter = plot.Plot.Add.Scatter(t, values[i]);
            scatter.MarkerSize = 0; scatter.LineWidth = 1.5f; scatter.Color = ScottPlot.Color.FromHex(Colors[i % Colors.Length]);
            var channel = recorder.Channels[i];
            var row = rows.FirstOrDefault(r => TagMatcher.Matches(r.Tag, channel.Tag) && (channel.Prefix.Length == 0 || PlcOf(r).Name == channel.Prefix));
            string name = string.IsNullOrWhiteSpace(row?.Name) ? channel.Tag : row!.Name;
            scatter.LegendText = channel.Prefix.Length > 0 ? $"{channel.Prefix} · {name}" : name;
            PlotService.AddInvalid(plot.Plot, t, values[i], null, 1.5f);
        }
        PlotService.AddInvalidLegend(plot.Plot);
        plot.Plot.ShowLegend(); plot.Plot.XLabel("Time [s]"); plot.Plot.Axes.AutoScale(); plot.Refresh();
        var span = TimeSpan.FromSeconds(recorder.Elapsed);
        stats.Text = $"{(finished ? "Završeno" : recorder.Connected ? "● Snima" : "Spajanje…")} · {span:hh\\:mm\\:ss} · uzoraka {recorder.Samples:N0} · čitanje Ø {recorder.AverageReadMs:0.0} ms / max {recorder.MaxReadMs:0.0} ms · propušteno {recorder.Overruns} · greške {recorder.Errors}"
            + (recorder.Errors > 0 ? $" ({recorder.LastError})" : "") + $"\n{recorder.CsvPath}";
    }
}
