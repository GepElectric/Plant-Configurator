using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TiaTraceStudio;

/// <summary>
/// Commissioning report in Word, in English: one chapter per tree node, numbered and named exactly like the tree
/// (the user names the tree in English; the export first lists every chapter name for review). Under every test its
/// status, manual entries, note, measurements and the graphs of all its recordings (legend in the picture, without
/// cursors or min/max markers), optionally with statistics.
/// </summary>
public partial class MainWindow
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static readonly Regex NonEnglish = new("[čćšžđČĆŠŽĐ]", RegexOptions.Compiled);

    void ExportReport()
    {
        if (plant == null) throw new InvalidDataException("Izvještaj prati stablo testova: prvo definiraj postrojenje (Novi projekt).");
        if (PrepareReport() is not var (template, replace, fields)) return;
        string name = string.Concat((plant.Name.Length > 0 ? plant.Name : "Plant").Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog { Filter = "Word dokument|*.docx", FileName = $"{name} - Commissioning report.docx", Title = "Izvještaj u Word" };
        if (workspacePath != null && Path.GetDirectoryName(workspacePath) is string folder) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true) return;
        var (graphs, problems) = WriteReport(dialog.FileName, template, replace, fields);
        // Page numbers of the contents: Word fills them in (when it is installed).
        Status("Word: upisujem brojeve stranica u sadržaj…");
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        bool pages;
        try { pages = WordFields.Update(dialog.FileName, TimeSpan.FromSeconds(120)); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        if (!pages) problems.Add("Brojevi stranica u sadržaju: u Wordu desni klik na sadržaj → Ažuriraj polje.");
        Status($"Izvještaj spremljen: {dialog.FileName} · grafova: {graphs}" + (problems.Count > 0 ? $" · upozorenja: {problems.Count}" : ""));
        string text = $"Izvještaj je spremljen ({graphs} grafova).\n{dialog.FileName}"
            + (problems.Count > 0 ? $"\n\nProvjeri:\n{string.Join("\n", problems.Take(12))}" + (problems.Count > 12 ? $"\n… i još {problems.Count - 12}" : "") : "") + "\n\nOtvoriti ga u Wordu?";
        if (MessageBox.Show(this, text, "Izvještaj u Word", MessageBoxButton.YesNo, problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
    }

    /// <summary>Tree nodes in report order with their chapter number and level.</summary>
    List<(int Level, string Number, object Node)> ReportChapters()
    {
        Sync(); CaptureDocument(); RefreshTree();
        var chapters = new List<(int, string, object)>();
        void Walk(ItemCollection items, int level, string number)
        {
            int index = 0;
            foreach (var item in items.OfType<TreeViewItem>())
            {
                if (item.Tag is not (string or TestSlot) || item.Tag as string == "__unassigned") continue;
                string chapter = number.Length == 0 ? $"{++index}" : $"{number}.{++index}";
                chapters.Add((level, chapter, item.Tag));
                if (item.Tag is string) Walk(item.Items, level + 1, chapter);
            }
        }
        Walk(PlantTree.Items, 1, "");
        return chapters;
    }

    /// <summary>Phases carry their own order number ("1 · Dry test"); the chapter number replaces it.</summary>
    string ChapterName(object node) => node is TestSlot slot ? slot.Title : Regex.Replace(NodeName((string)node), @"^\d+\s*·\s*", "");

    sealed class ChapterRow
    {
        public string Number { get; init; } = "";
        public object Node { get; init; } = "";
        public string Original { get; init; } = "";
        public string Name { get; set; } = "";
        public Thickness Indent { get; init; }
        public string Check => NonEnglish.IsMatch(Name) ? "hrvatska slova" : "";
    }

    sealed class TextRow
    {
        public string Original { get; init; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>Company report templates; the paths are remembered per user (other OneDrive folders).</summary>
    static readonly (string Key, string Title, string DefaultPath)[] Companies =
    [
        ("Geppert", "Geppert Electric", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive - Geppert Electric d.o.o", "50_KVALITETA", "02_Predlošci", "02_FAT izvještaj", "SCADA SYSTEM-FR-QC-001_FAT report.docx")),
        ("PrimeVolt", "Prime Volt", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive - PRIME VOLT d.o.o", "KVALITETA[Quality]", "02_Predlošci_obrasci", "FR-QC-001_FAT_report.docx")),
    ];
    static string TemplateStore => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaTraceStudio", "report-templates.json");
    static Dictionary<string, string> TemplatePaths()
    {
        try { if (File.Exists(TemplateStore)) return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TemplateStore)) ?? []; } catch (Exception) { }
        return [];
    }
    static string TemplatePath(string company) => TemplatePaths().TryGetValue(company, out var path) && File.Exists(path) ? path : Companies.FirstOrDefault(c => c.Key == company).DefaultPath ?? "";
    static void RememberTemplate(string company, string path)
    {
        var paths = TemplatePaths(); paths[company] = path;
        Directory.CreateDirectory(Path.GetDirectoryName(TemplateStore)!);
        File.WriteAllText(TemplateStore, System.Text.Json.JsonSerializer.Serialize(paths));
    }

    /// <summary>
    /// Before the export: which company template (Geppert Electric, Prime Volt or a basic report), the document data
    /// of the template (header, cover, signatures) and the English chapter names. Everything is saved in the project.
    /// </summary>
    static readonly Dictionary<string, string> FieldLabels = new()
    {
        ["Document number"] = "Oznaka dokumenta", ["Project"] = "Naziv projekta", ["Title"] = "Naslov dokumenta", ["Author"] = "Autor",
        ["Customer"] = "Kupac", ["Section"] = "Područje (Section)", ["Date"] = "Datum"
    };

    (WordTemplate? Template, Dictionary<string, string> Replace, Dictionary<string, string> Fields)? PrepareReport()
    {
        var settings = plant!.Report;
        var chapterRows = ReportChapters().Select(c => new ChapterRow { Number = c.Number, Node = c.Node, Original = ChapterName(c.Node), Name = ChapterName(c.Node), Indent = new Thickness((c.Level - 1) * 16, 0, 0, 0) }).ToList();
        var layout = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Predložak izvještaja", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var choice = new WrapPanel(); top.Children.Add(choice);
        var templateInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var templateRow = new DockPanel(); top.Children.Add(templateRow);
        var tabs = new TabControl();
        var textGrid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        textGrid.Columns.Add(new DataGridTextColumn { Header = "U predlošku", Binding = new Binding(nameof(TextRow.Original)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        textGrid.Columns.Add(new DataGridTextColumn { Header = "U izvještaju ovog projekta", Binding = new Binding(nameof(TextRow.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var textTab = new DockPanel();
        // Required document data: found in the form by its labels, filled in before every export.
        var fieldPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        fieldPanel.ColumnDefinitions.Add(new() { Width = new GridLength(170) }); fieldPanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var fieldBoxes = new Dictionary<string, TextBox>();
        var fieldBox = new StackPanel(); DockPanel.SetDock(fieldBox, Dock.Top); textTab.Children.Add(fieldBox);
        fieldBox.Children.Add(new TextBlock { Text = "Obavezni podaci dokumenta", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        fieldBox.Children.Add(fieldPanel);
        var textHint = new TextBlock { Text = "Zaglavlje, naslovnica i potpisi (Checked / Approved) predloška. Promijeni samo ono što se razlikuje za ovaj projekt (broj dokumenta, naslov, projekt, kupac, datum, autor, imena).", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(textHint, Dock.Top); textTab.Children.Add(textHint); textTab.Children.Add(textGrid);
        tabs.Items.Add(new TabItem { Header = "Podaci dokumenta", Content = textTab });

        int croatian = chapterRows.Count(r => r.Check.Length > 0);
        var chapterTab = new DockPanel();
        var chapterInfo = new TextBlock { Text = "Izvještaj je na engleskom: poglavlja se zovu točno kao stablo. Provjeri i po potrebi prevedi nazive (spremaju se u stablo projekta)." + (croatian > 0 ? $"\n{croatian} naziva sadrži hrvatska slova." : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = croatian > 0 ? Warn : Ink };
        DockPanel.SetDock(chapterInfo, Dock.Top); chapterTab.Children.Add(chapterInfo);
        var grid = new DataGrid { ItemsSource = chapterRows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        grid.Columns.Add(new DataGridTextColumn { Header = "Poglavlje", Binding = new Binding(nameof(ChapterRow.Number)), IsReadOnly = true, Width = 90 });
        var nameColumn = new DataGridTextColumn { Header = "Naziv u izvještaju (engleski)", Binding = new Binding(nameof(ChapterRow.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
        nameColumn.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(FrameworkElement.MarginProperty, new Binding(nameof(ChapterRow.Indent))) } };
        grid.Columns.Add(nameColumn);
        grid.Columns.Add(new DataGridTextColumn { Header = "Provjera", Binding = new Binding(nameof(ChapterRow.Check)), IsReadOnly = true, Width = 120, ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, Warn) } } });
        chapterTab.Children.Add(grid);
        tabs.Items.Add(new TabItem { Header = $"Poglavlja ({chapterRows.Count})", Content = chapterTab });
        tabs.SelectedIndex = croatian > 0 ? 1 : 0;

        string company = settings.Company; WordTemplate? template = null; List<TextRow> textRows = []; List<WordTemplate.Field> fields = [];
        void Show()
        {
            template = null; textRows = [];
            string path = company.Length == 0 ? "" : TemplatePath(company);
            if (company.Length > 0 && File.Exists(path))
            {
                try { template = WordTemplate.Load(path); } catch (Exception ex) { templateInfo.Text = ex.Message; templateInfo.Foreground = Warn; }
            }
            if (company.Length == 0) { templateInfo.Text = "Osnovni izvještaj bez predloška tvrtke."; templateInfo.Foreground = Brushes.SlateGray; }
            else if (template != null) { templateInfo.Text = path; templateInfo.Foreground = Brushes.SlateGray; }
            else if (!File.Exists(path)) { templateInfo.Text = $"Predložak nije pronađen: {path}\nOdaberi ga tipkom „Predložak…“."; templateInfo.Foreground = Warn; }
            var saved = settings.Replacements.TryGetValue(company, out var map) ? map : [];
            // A site acceptance (commissioning) report: "FAT" of the form becomes "SAT" unless the user wrote otherwise.
            fields = template?.Fields() ?? [];
            var fieldKeys = fields.SelectMany(f => f.Places.Select(p => p.Key)).ToHashSet();
            if (template != null) textRows = template.EditableTexts().Where(t => !fieldKeys.Contains(t)).Select(t => new TextRow { Original = t, Value = saved.TryGetValue(t, out var v) ? v : Regex.Replace(t, @"\bFAT\b", "SAT") }).ToList();
            fieldPanel.Children.Clear(); fieldPanel.RowDefinitions.Clear(); fieldBoxes.Clear();
            foreach (var field in fields)
            {
                int row = fieldPanel.RowDefinitions.Count; fieldPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var label = new TextBlock { Text = FieldLabels.GetValueOrDefault(field.Name, field.Name) + " *", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
                string value = settings.Fields.TryGetValue(field.Name, out var known) ? known
                    : field.Name switch { "Title" => "SAT report", "Date" => DateTime.Now.ToString("dd.MM.yyyy.", En), "Section" => "Commissioning", _ => "" };
                var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 6), ToolTip = $"U predlošku: {field.Current}" };
                Grid.SetRow(label, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
                fieldPanel.Children.Add(label); fieldPanel.Children.Add(box); fieldBoxes[field.Name] = box;
            }
            fieldBox.Visibility = fields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            textGrid.ItemsSource = textRows;
            ((TabItem)tabs.Items[0]).IsEnabled = template != null;
            if (template == null && tabs.SelectedIndex == 0) tabs.SelectedIndex = 1;
        }
        foreach (var (key, title) in Companies.Select(c => (c.Key, c.Title)).Append(("", "Osnovni (bez predloška)")))
        {
            var radio = new RadioButton { Content = title, GroupName = "company", IsChecked = key == company, Margin = new Thickness(0, 0, 18, 0) };
            radio.Checked += (_, _) => { company = key; Show(); };
            choice.Children.Add(radio);
        }
        var pick = new Button { Content = "Predložak…", Margin = new Thickness(8, 4, 0, 0), ToolTip = "Odaberi Word predložak (.docx) za odabranu tvrtku; pamti se na ovom računalu." };
        pick.Click += (_, _) => Guard(() =>
        {
            if (company.Length == 0) return;
            var open = new OpenFileDialog { Filter = "Word predložak|*.docx", Title = "Predložak izvještaja" };
            string current = TemplatePath(company); if (Path.GetDirectoryName(current) is string folder && Directory.Exists(folder)) open.InitialDirectory = folder;
            if (open.ShowDialog() != true) return;
            WordTemplate.Load(open.FileName); RememberTemplate(company, open.FileName); Show();
        });
        DockPanel.SetDock(pick, Dock.Right); templateRow.Children.Add(pick); templateRow.Children.Add(templateInfo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons); layout.Children.Add(tabs);
        var dialog = new Window { Owner = this, Title = "Izvještaj u Word", Content = layout, Width = Dialogs.Width(1000), Height = Dialogs.Height(860), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        bool accepted = false;
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Spremi i izvezi…", (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true); textGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (company.Length > 0 && template == null) { MessageBox.Show(dialog, "Predložak nije pronađen. Odaberi ga tipkom „Predložak…“ ili izaberi osnovni izvještaj.", "Izvještaj", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (fieldBoxes.FirstOrDefault(b => string.IsNullOrWhiteSpace(b.Value.Text)) is { Value: not null } missing)
            {
                tabs.SelectedIndex = 0; missing.Value.Focus();
                MessageBox.Show(dialog, $"Upiši sve obavezne podatke dokumenta: {string.Join(", ", fieldBoxes.Where(b => string.IsNullOrWhiteSpace(b.Value.Text)).Select(b => FieldLabels.GetValueOrDefault(b.Key, b.Key)))}.", "Izvještaj", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            accepted = true; dialog.Close();
        }, true);
        Show();
        dialog.ShowDialog();
        if (!accepted) return null;
        settings.Company = company;
        var replace = textRows.Where(r => r.Value.Trim() != r.Original).ToDictionary(r => r.Original, r => r.Value.Trim());
        if (company.Length > 0) settings.Replacements[company] = new(replace);
        var values = fieldBoxes.ToDictionary(b => b.Key, b => b.Value.Text.Trim());
        foreach (var (name, value) in values) settings.Fields[name] = value;
        // Every place of a field in the form gets the value; labels before it stay, upper-case places stay upper case.
        foreach (var field in fields)
            foreach (var (key, prefix, upper) in field.Places)
                replace[key] = prefix + (upper ? values[field.Name].ToUpperInvariant() : values[field.Name]);
        ApplyChapterNames(chapterRows.Select(r => (r.Node, r.Original, r.Name)));
        return (template, replace, values);
    }

    void ApplyChapterNames(IEnumerable<(object Node, string Original, string Name)> names)
    {
        foreach (var (node, original, name) in names.Where(n => n.Name.Trim().Length > 0 && n.Name.Trim() != n.Original))
        {
            if (node is TestSlot slot) { slot.Title = name.Trim(); slot.CustomTitle = slot.IsUser ? null : slot.Title; }
            else ApplyNodeName((string)node, name);
        }
        RefreshTree();
    }

    /// <summary>Writes the report; returns the number of graphs and the points to check (graphs not drawn, unnamed signals).</summary>
    (int Graphs, List<string> Problems) WriteReport(string path, WordTemplate? template = null, IReadOnlyDictionary<string, string>? replace = null, IReadOnlyDictionary<string, string>? fields = null)
    {
        var chapters = ReportChapters();
        var blocks = new List<ReportBlock>(); var problems = new List<string>(); int figure = 0;
        string F(double? v) => v is double d && double.IsFinite(d) ? d.ToString("G5", En) : "–";

        // Digital signals (0/1, DI / DO) have no meaningful statistics.
        static bool Digital(Signal s) => (s.Unit ?? "").Trim() == "0/1" || Regex.IsMatch(TagMatcher.Normalize(SplitSource(s.Source).Tag), @"^(DI|DO)[.#]", RegexOptions.IgnoreCase);

        // Normalized graph: which values are 0 % and 100 % for every signal, so the scaling can be read.
        void Scaling(StudioProject recording)
        {
            if (recording.Options.DisplayMode != ChartOptions.PercentMode) return;
            var rows = recording.Data.Signals.Where(s => s.Visible).Select(s =>
            {
                string unit = s.Unit?.Trim() ?? "";
                if (SignalDisplay.IsNativePercent(s))
                    return (IReadOnlyList<string>)new[] { s.Name.Trim(), SignalDisplay.IsTemperature(s) ? "not scaled (°C)" : "not scaled (%)", "" };
                return new[] { s.Name.Trim(), $"{F(s.PercentMin)} {unit}".Trim(), $"{F(s.PercentMax)} {unit}".Trim() };
            }).ToList();
            blocks.Add(new ReportTable(["Signal", "0 %", "100 %"], rows, Caption: $"Scaling of the graph (normalized) · {recording.Options.Title}"));
        }

        void Statistics(StudioProject recording)
        {
            var data = recording.Data; var o = recording.Options;
            var rows = data.Signals.Where(s => s.Visible && !Digital(s)).Select(s =>
            {
                var values = s.Scaled();
                var inside = values.Where((v, i) => data.Time[i] >= o.Start && data.Time[i] <= o.End && double.IsFinite(v)).ToArray();
                return (IReadOnlyList<string>)new[]
                {
                    s.Name.Trim(), s.Unit?.Trim() ?? "",
                    F(inside.Length > 0 ? inside.Min() : null), F(inside.Length > 0 ? inside.Max() : null), F(inside.Length > 0 ? inside.Average() : null),
                    F(AnalysisService.ValueAt(data.Time, values, o.Start)), F(AnalysisService.ValueAt(data.Time, values, o.End))
                };
            }).ToList();
            if (rows.Count == 0) return;
            blocks.Add(new ReportTable(["Signal", "Unit", "Min", "Max", "Mean", "Start", "End"], rows,
                Caption: $"Statistics · {recording.Options.Title} · interval {o.ToAxisTime(o.Start).ToString("0.###", En)} – {o.ToAxisTime(o.End).ToString("0.###", En)} {o.TimeUnit}"));
        }

        // Controller tests (valve position controllers and unit controllers): the PID structure in Plant Configurator.
        string? ControllerStructure(TestSlot slot)
        {
            var m = Regex.Match(slot.Kind, @"^(?:CTRL_)?(\w+?)_(STEP|RAMP)$");
            if (!m.Success) return null;
            int n = Math.Max(1, slot.Index);
            return m.Groups[1].Value switch
            {
                "NOZZLE" => $"PID_PELTON_NOZZLE_{n}", "DEFLECTOR" => $"PID_PELTON_DEFLECTOR_{n}", "WICKET_GATE" => "PID_FRANCIS_DISTRIBUTOR",
                "KAPLAN_GATE" => "PID_KAPLAN_DISTRIBUTOR", "RUNNER" => "PID_KAPLAN_RUNNER", "CF_SECTION" => $"PID_CROSS_FLOW_VALVE_{n}",
                "SPEED" => "PID_SPEED", "POWER" => "PID_ACTIVE_POWER", "LEVEL" => "PID_WATER_LEVEL", _ => null
            };
        }

        // Main controller parameters at the beginning of a chapter: a turbine valve (its position controller) or
        // "Regulacije" (one table per controller in it). Empty cells when Plant Configurator has no values for the unit.
        void GroupControllers(string key)
        {
            var m = Regex.Match(key, @"^U(\d+)/([^/]+)/(.+)$");
            if (!m.Success) return;
            int unit = int.Parse(m.Groups[1].Value); string phase = m.Groups[2].Value, path = m.Groups[3].Value;
            var members = slots.Where(s => !s.Deleted && s.Unit == unit && s.Phase == phase && s.Group == path).ToList();
            foreach (var structure in members.Select(ControllerStructure).OfType<string>().Distinct())
                ControllerTable(structure, members.First(s => ControllerStructure(s) == structure));
        }

        void ControllerTable(string structure, TestSlot slot)
        {
            var parameters = (CatalogOf(PlcFor(slot))?.Controllers ?? []).Where(p => p.Structure.Equals(structure, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            if (parameters.Count == 0) problems.Add($"Agregat {slot.Unit}: parametri regulatora {structure} nisu u povezanom Plant Configuratoru (tablica je prazna za ručni upis)");
            string Value(string name)
            {
                if (!parameters.TryGetValue(name, out var p)) return "";
                if (p.Type == "2") return TimeText(p.Value);
                return double.TryParse(p.Value, NumberStyles.Float, En, out double v) ? v.ToString("G6", En) + (p.Unit.Length > 0 ? " " + p.Unit : "") : p.Value;
            }
            string OnOff(string name) => parameters.TryGetValue(name, out var p) ? (p.Value is "1" or "true" or "TRUE" ? "On" : "Off") : "";
            blocks.Add(new ReportTable(["P-action", "Gain", "I-action", "Ti", "D-action", "Td", "Dead band", "Low limit", "High limit"],
                [new[] { OnOff("P_SEL"), Value("GAIN"), OnOff("I_SEL"), Value("TI"), OnOff("D_SEL"), Value("TD"), Value("DEAD_W"), Value("LMN_LLM"), Value("LMN_HLM") }],
                Caption: $"Controller parameters · {(slot.Unit > 0 ? (plant!.TreeNames.TryGetValue($"U{slot.Unit}", out var unitName) ? unitName : $"Unit {slot.Unit}") + " · " : "")}{structure}"));
        }

        // "Unit 1 · Nozzle 1" for the opening/closing test of a valve (its sub-group in the tree), "Unit 1 · MIV …" otherwise.
        string TimesName(TestSlot slot)
        {
            string unit = slot.Unit > 0 ? (plant!.TreeNames.TryGetValue($"U{slot.Unit}", out var u) ? u : $"Unit {slot.Unit}") + " · " : "";
            if (!slot.Kind.EndsWith("_OPEN_CLOSE") || slot.Group.Length == 0) return unit + slot.Title;
            return unit + NodeName($"U{slot.Unit}/{slot.Phase}/{slot.Group}");
        }

        void Test(TestSlot slot)
        {
            // What the client reads: graph, statistics and the remark (plus manually entered times).
            // Opening and closing times (MIV, bypass, every turbine valve): entered in the application, blank to fill in by hand.
            if (slot.HasTimes)
                blocks.Add(new ReportTable(["Opening time [s]", "Closing time [s]"],
                    [new[] { slot.OpeningTime?.ToString("G6", En) ?? "", slot.ClosingTime?.ToString("G6", En) ?? "" }], [50, 50], $"Opening and closing time · {TimesName(slot)}"));
            if (slot.NotApplicable) blocks.Add(new ReportParagraph("Not applicable.", Muted: true));
            var recordings = RecordingsOf(slot);
            bool first = true;
            foreach (var doc in recordings)
            {
                var recording = doc == activeDocument ? project : doc.Project;
                var data = recording.Data;
                try
                {
                    // Report graph: legend in the picture, no cursors and no min/max markers (the statistics carry the values).
                    var shown = ProjectStore.Clone(recording);
                    shown.Options.Cursors = false; shown.Options.HCursors = false; shown.Options.ZeroCursorVisible = false; shown.Options.ShowExtrema = false; shown.Options.Legend = true; shown.Options.Subtitle = "";
                    // The automatic graph title ("Agregat 3 · …") becomes the test's name from the tree; a title typed by the user stays.
                    if (shown.Options.Title == slot.ChartTitle || string.IsNullOrWhiteSpace(shown.Options.Title)) shown.Options.Title = slot.Title;
                    int index = 0;
                    foreach (var signal in shown.Data.Signals)
                    {
                        index++;
                        if (!signal.Visible || !string.IsNullOrWhiteSpace(signal.Name)) continue;
                        var (prefix, tag) = SplitSource(signal.Source);
                        var signalPlc = prefix != null ? plant!.Plcs.FirstOrDefault(p => p.Name == prefix) : PlcFor(slot);
                        signal.Name = CatalogOf(signalPlc)?.Find(tag)?.Name is string measured ? measured + (prefix != null ? $" ({prefix})" : "") : $"Signal {index}";
                        problems.Add($"{slot.Path} · {data.FileName}: signal {index} nema naziv (u izvještaju „{signal.Name}“)");
                    }
                    // Common tests (all units at once): one graph with its scaling and statistics per unit.
                    foreach (var part in UnitParts(slot, shown))
                    {
                        if (!first) blocks.Add(new ReportPageBreak());
                        first = false;
                        BitmapSource bitmap;
                        PlotService.English = true;
                        try { bitmap = PlotService.Render(part); } finally { PlotService.English = false; }
                        var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = new MemoryStream(); encoder.Save(stream);
                        blocks.Add(new ReportImage(stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, part.Options.Title)); figure++;
                        if (recording.Options.ReportStatistics || part.Options.DisplayMode == ChartOptions.PercentMode) blocks.Add(new ReportPageBreak());
                        Scaling(part);
                        if (recording.Options.ReportStatistics) Statistics(part);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"{slot.Path} · {data.FileName}: graf nije izvezen ({ex.Message})");
                    blocks.Add(new ReportParagraph($"The graph of recording {data.FileName} could not be exported.", Muted: true));
                }
            }
            // Always a small Remark table: the note from the application, or room to write one by hand.
            blocks.Add(new ReportRemark(slot.Note?.Trim() ?? ""));
        }

        // A recording of a common test split by unit: the signals of each unit's PLC (plus signals of no unit).
        List<StudioProject> UnitParts(TestSlot slot, StudioProject shown)
        {
            if (!slot.IsCommon) return [shown];
            int? UnitOf(Signal s) => SplitSource(s.Source).Plc is string prefix && plant!.Plcs.FirstOrDefault(p => p.Name == prefix)?.UnitNumbers() is [int unit] ? unit : null;
            var units = shown.Data.Signals.Where(s => s.Visible).Select(UnitOf).OfType<int>().Distinct().Order().ToList();
            if (units.Count < 2) return [shown];
            return units.Select(unit =>
            {
                var part = ProjectStore.Clone(shown);
                foreach (var s in part.Data.Signals) s.Visible = s.Visible && (UnitOf(s) ?? unit) == unit;
                string name = plant!.TreeNames.TryGetValue($"U{unit}", out var renamed) ? renamed : $"Unit {unit}";
                part.Options.Title = $"{shown.Options.Title} · {name}";
                return part;
            }).ToList();
        }

        foreach (var (level, number, node) in chapters)
        {
            // Every main chapter (unit, group, common tests) is a section of its own, starting on a new page.
            if (level == 1) blocks.Add(new ReportSection(Landscape: false, NewPage: true));
            // A test with recordings is a landscape section: heading, graph, statistics, remark.
            bool graphs = node is TestSlot recorded && RecordingsOf(recorded).Count > 0;
            if (graphs) blocks.Add(new ReportSection(Landscape: true));
            blocks.Add(new ReportHeading(level, ChapterName(node), number));
            if (node is TestSlot slot) Test(slot);
            else if (node is string key) GroupControllers(key);
            if (graphs) blocks.Add(new ReportSection(Landscape: false));
        }
        // Last chapter: sign-off of the commissioning engineer.
        int topChapters = chapters.Count(c => c.Level == 1);
        blocks.Add(new ReportSection(Landscape: false, NewPage: true));
        blocks.Add(new ReportHeading(1, "Commissioning engineer sign-off", $"{topChapters + 1}"));
        blocks.Add(new ReportParagraph("The tests described in this report were performed and the results recorded as stated above."));
        blocks.Add(new ReportTable(["Role", "Name", "Date", "Signature"], [new[] { "Commissioning engineer", "\n\n", "", "" }], [30, 30, 15, 25], "Commissioning engineer sign-off"));
        var active = slots.Where(s => !s.Retired && !s.Deleted).ToList();
        var subtitle = new List<string>
        {
            "Commissioning report",
            string.IsNullOrWhiteSpace(plant!.Location) ? "" : plant.Location,
            $"Date: {DateTime.Now.ToString("dd.MM.yyyy", En)}",
            $"Tests: {active.Count(IsDone)} of {active.Count} · recordings: {documents.Count} · graphs: {figure}"
        }.Where(s => s.Length > 0).ToList();
        if (template != null) template.Save(path, blocks, replace ?? new Dictionary<string, string>(), fields);
        else WordReport.Save(path, plant.Name, subtitle, blocks);
        return (figure, problems);
    }

    /// <summary>S7 time "T#1M_10S" / "T#250MS" as "70 s" / "0.25 s".</summary>
    static string TimeText(string value)
    {
        var m = Regex.Matches(value.ToUpperInvariant(), @"(\d+(?:\.\d+)?)(D|H|MS|M|S)");
        if (m.Count == 0) return value;
        double seconds = m.Sum(x => double.Parse(x.Groups[1].Value, En) * x.Groups[2].Value switch { "D" => 86400, "H" => 3600, "M" => 60, "MS" => 0.001, _ => 1 });
        return seconds.ToString("G6", En) + " s";
    }

    string NodeName(string key) => plant?.TreeNames.TryGetValue(key, out var name) == true ? name : treeDefaults.TryGetValue(key, out var original) ? original : key;
}
