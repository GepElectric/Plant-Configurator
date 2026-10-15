using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace TiaTraceStudio;

public partial class MainWindow : Window
{
    StudioProject project = new();
    ObservableCollection<Signal> signals = [];
    ObservableCollection<TraceNote> notes = [];
    readonly List<WpfPlot> plots = [];
    readonly List<string> history = [];
    int historyIndex = -1;
    string? projectPath; string savedSnapshot = "";
    readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromSeconds(30) };
    static string RecoveryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaTraceStudio", "recovery.tiatrace");
    bool synchronizing;
    double renderedTimeScale = 1, renderedZero, renderedStart, renderedEnd;
    readonly bool recoveryEnabled;

    public MainWindow() : this(true) { }
    public MainWindow(bool restoreSession)
    {
        recoveryEnabled = restoreSession;
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        BuildEditors(); BuildDocumentTools(); BuildPopups(); Bind();
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.O) { OpenCsv_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.S) { SaveProject_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.E) { Export_Click(this, new()); e.Handled = true; }
        };
        autosave.Tick += (_, _) =>
        {
            if (project.Data.Time.Length == 0 || historyIndex < 0) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(SessionRecoveryPath)!); ProjectStore.Save(SessionRecoveryPath, CurrentSession(true)); }
            catch (Exception ex) { Status("Automatsko spremanje nije uspjelo: " + ex.Message); }
        };
        if (recoveryEnabled) autosave.Start();
        if (restoreSession) Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            // Plant Configurator → Tools starts the app with: --plantcfg "<project.plantcfg>"
            int option = args.FindIndex(a => a.Equals("--plantcfg", StringComparison.OrdinalIgnoreCase));
            string? plantConfigPath = option >= 0 && option + 1 < args.Count ? args[option + 1] : null;
            if (option >= 0) args.RemoveRange(option, Math.Min(2, args.Count - option));
            if (args.Count > 0 && args.Any(File.Exists)) await AddPaths(args.Where(File.Exists));
            else if (File.Exists(SessionRecoveryPath))
            {
                if (MessageBox.Show(this, "Pronađena je prethodna sesija s CSV datotekama. Obnoviti je?", "Obnova sesije", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await LoadPath(SessionRecoveryPath);
            }
            else if (File.Exists(RecoveryPath) && MessageBox.Show(this, "Pronađena je prethodna automatski spremljena sesija. Obnoviti je?", "Obnova sesije", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Guard(() => { ReplaceWorkspace(TraceSession.Load(RecoveryPath), null); savedWorkspaceState = ""; Status("Obnovljen projekt iz automatske snimke."); });
            if (plantConfigPath != null && File.Exists(plantConfigPath)) OpenFromPlantConfigurator(plantConfigPath);
        };
    }
    readonly StackPanel percentEditor = new();
    readonly TextBlock percentInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    FrameworkElement? percentCard;
    void BuildEditors()
    {
        // Signali: what the signal is, how it looks, how it is scaled and shared, normalized display, min/max cards.
        var (left, right) = Columns(SignalEditor);
        var naming = Card(left, "Naziv i jedinica");
        Field(naming, "Naziv signala *", "Name");
        Field(naming, "Jedinica (npr. bar, m, rpm, %)", "Unit");
        Toggle(naming, "Samo ovaj CSV (naziv, jedinica i scaling)", "NameOverride");
        Field(naming, "Izvorni CSV / PLC tag", "Source", true);
        var look = Card(left, "Izgled linije");
        look.Children.Add(new TextBlock { Text = "Boja" });
        var palette = new WrapPanel { Margin = new Thickness(0, 3, 0, 6) }; look.Children.Add(palette);
        foreach (string hex in new[] { "#2563EB", "#E88821", "#109C83", "#A855F7", "#E14D72", "#0891B2", "#64748B", "#CA8A04" })
        { var b = new Button { Width = 34, Height = 24, Padding = new Thickness(0), Background = (Brush)new BrushConverter().ConvertFromString(hex)!, ToolTip = hex }; b.Click += (_, _) => { if (SignalList.SelectedItem is Signal s) s.Color = hex; }; palette.Children.Add(b); }
        Field(look, "Boja (#RRGGBB)", "Color");
        Choice(look, "Stil linije", "Style", ["Puna", "Crtkana", "Točkasta"]);
        Pair(look, ("Debljina (0,5–10)", "Width"), ("Panel (1–4)", "Panel"));
        var remove = Card(left, "Signal");
        Action(remove, "Ukloni odabrani signal", RemoveSignal_Click);
        var scaling = Card(right, "Scaling i dijeljenje");
        Pair(scaling, ("Faktor", "Factor"), ("Offset", "Offset"));
        Hint(scaling, "Primijeni promjene upisuje naziv, jedinicu, faktor i offset u bazu projekta i na isti tag u drugim snimkama (osim lokalnih iznimki).");
        var shareRow = new WrapPanel(); scaling.Children.Add(shareRow);
        Action(shareRow, "Primijeni na sve snimke", NameAll_Click);
        Action(shareRow, "Preuzmi iz baze", UseLibraryName_Click);
        percentCard = Card(right, "Raspon za normalizirani prikaz (0 % – 100 %)", percentEditor);
        // Source of the range: the unit's rated values in the project, or a manual range for this graph.
        var fromProject = new RadioButton { Content = "Iz postavki projekta (nazivne vrijednosti agregata)", Margin = new Thickness(0, 2, 0, 6), GroupName = "percentSource" };
        fromProject.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("PercentFromProject") { Mode = BindingMode.TwoWay });
        var manual = new RadioButton { Content = "Ručno za ovaj graf", Margin = new Thickness(0, 0, 0, 8), GroupName = "percentSource" };
        manual.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("PercentFromProject") { Mode = BindingMode.TwoWay, Converter = InverseBool.Instance });
        fromProject.Checked += (_, _) => Guard(() => { if (SignalList.SelectedItem is Signal) { ApplyNominalRanges(project, activeDocument?.SlotId); UpdatePercentInfo(); } });
        manual.Checked += (_, _) => UpdatePercentInfo();
        percentEditor.Children.Add(fromProject); percentEditor.Children.Add(manual);
        Pair(percentEditor, ("Min (= 0 %)", "PercentMin"), ("Max (= 100 %)", "PercentMax"));
        foreach (var box in LogicalTreeHelper.GetChildren(percentEditor.Children[^1]).OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBox>()))
            box.SetBinding(IsEnabledProperty, new Binding("PercentFromProject") { Converter = InverseBool.Instance });
        percentEditor.Children.Add(percentInfo);
        var rangeRow = new WrapPanel(); percentEditor.Children.Add(rangeRow);
        Action(rangeRow, "Preuzmi min/max cijele snimke", PercentFromData_Click);
        Action(rangeRow, "Spremi kao nazivnu vrijednost agregata", (_, _) => Guard(SaveRangeToProject));
        var labels = Card(right, "Min/max oblačići ovog signala");
        Toggle(labels, "Sakrij minimum", "MinimumLabel.Hidden");
        Toggle(labels, "Sakrij maksimum", "MaximumLabel.Hidden");
        Toggle(labels, "Sakrij min = max (konstantan signal)", "ConstantLabel.Hidden");

        // Graf: titles, time axis, appearance, label layout, history and templates.
        var (graphLeft, graphRight) = Columns(OptionsEditor);
        var titles = Card(graphLeft, "Naslovi");
        Field(titles, "Naslov grafa", "Title");
        Field(titles, "Komentar ispod slike", "Subtitle");
        var time = Card(graphLeft, "Vremenska os");
        Field(time, "Naziv vremenske osi", "TimeAxisLabel");
        Choice(time, "Jedinica vremenske osi", "TimeUnit", ["s", "min", "h", "d"]);
        Pair(time, ("Od [s]", "Start"), ("Do [s]", "End"));
        Field(time, "Nula vremena [s]", "Zero");
        Hint(time, "s = sekunde · min = minute · h = sati · d = dani. Os, oblačići i Δt koriste odabranu jedinicu; raspon, nula i kursori unose se u sekundama.");
        var appearance = Card(graphRight, "Izgled");
        Toggle(appearance, "Mreža", "Grid"); Toggle(appearance, "Legenda (ispod grafa)", "Legend");
        Toggle(appearance, "Statistika ispod grafa u Word izvještaju", "ReportStatistics");
        Field(appearance, "Veličina fonta (8–32)", "FontSize");
        var cards = Card(graphRight, "Min/max oblačići");
        Action(cards, "Prikaži sve skrivene oblačiće", ShowAllExtrema_Click);
        Action(cards, "Automatski raspored svih oblačića", ResetExtremaPositions_Click);
        var history = Card(graphRight, "Povijest i predlošci");
        var undoRow = new WrapPanel(); history.Children.Add(undoRow);
        Action(undoRow, "Poništi promjenu", Undo_Click); Action(undoRow, "Ponovi promjenu", Redo_Click);
        var presetRow = new WrapPanel(); history.Children.Add(presetRow);
        Action(presetRow, "Spremi predložak", SavePreset_Click); Action(presetRow, "Učitaj predložak", LoadPreset_Click);
        Action(OptionsEditor, "Primijeni", Apply_Click, true);

        // Izvoz: image size and quality; export, copy and batch actions.
        var (exportLeft, exportRight) = Columns(ExportEditor);
        var image = Card(exportLeft, "Slika");
        Pair(image, ("Širina [px]", "ExportWidth"), ("Visina [px]", "ExportHeight"));
        Field(image, "JPEG kvaliteta (1–100)", "Quality");
        var exporting = Card(exportRight, "Izvoz i kopiranje");
        Action(exporting, "Izvezi JPG / PNG…", Export_Click, true);
        Action(exporting, "Pregled slike", PreviewExport_Click);
        Action(exporting, "Kopiraj sliku", CopyImage_Click);
        Action(exporting, "Grupni CSV → JPG…", Batch_Click);
        Hint(exporting, "Grupni izvoz koristi spremljena imena podudarnih PLC signala. Datoteke s neimenovanim signalima neće se izvesti.");

        var mode = new ComboBox { Width = 265, ItemsSource = new[] { ChartOptions.UnitsMode, ChartOptions.PercentMode }, Margin = new Thickness(0, 0, 14, 0) };
        mode.SetBinding(ComboBox.SelectedItemProperty, new Binding("DisplayMode") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        // A choice by the user (not a rebinding, which passes through no selection) redraws at once.
        mode.SelectionChanged += (_, e) => { if (e.RemovedItems.Count > 0 && e.AddedItems.Count > 0 && ModeEditor.DataContext == project.Options) Guard(Commit); };
        ModeEditor.Children.Insert(0, mode);
        var zeroToggle = new CheckBox { Content = "Kursor", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Jedan vertikalni kursor: vrijednosti svih signala u tom trenutku; Primijeni nulu grafa postavlja nulu vremena na njega." };
        zeroToggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("ZeroCursorVisible") { Mode = BindingMode.TwoWay });
        zeroToggle.Click += ZeroCursor_Click;
        var applyZero = new Button { Content = "Primijeni nulu grafa", ToolTip = "Nula vremenske osi na položaj vertikalnog kursora (vrijedi i za izvoz)." };
        applyZero.SetBinding(IsEnabledProperty, new Binding("ZeroCursorVisible"));
        applyZero.Click += ApplyZero_Click;
        int at = ModeEditor.Children.IndexOf(MeasurementToggle);
        ModeEditor.Children.Insert(at, zeroToggle); ModeEditor.Children.Insert(at + 1, applyZero);
        var horizontal = new CheckBox { Content = "Horizontalni A/B", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Dva vodoravna kursora na lijevoj osi prvog panela: vrijednosti i ΔY = B − A." };
        horizontal.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("HCursors") { Mode = BindingMode.TwoWay });
        horizontal.Click += HorizontalCursors_Click;
        ModeEditor.Children.Insert(ModeEditor.Children.IndexOf(MeasurementToggle) + 1, horizontal);
    }
    void HorizontalCursors_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length == 0) { project.Options.HCursors = false; RebindOptions(); return; }
        if (project.Options.HCursors && plots.FirstOrDefault() is ScottPlot.WPF.WpfPlot first)
        {
            // Start at a quarter and three quarters of the visible left axis.
            var range = first.Plot.Axes.Left.Range;
            project.Options.HCursorA = range.Min + (range.Max - range.Min) * .25;
            project.Options.HCursorB = range.Min + (range.Max - range.Min) * .75;
        }
        RebindOptions(); Commit();
    });
    void ZeroCursor_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length == 0) { project.Options.ZeroCursorVisible = false; RebindOptions(); return; }
        if (project.Options.ZeroCursorVisible)
        {
            // Start in the middle of what is on screen.
            var limits = plots.FirstOrDefault()?.Plot.Axes.GetLimits();
            double start = limits.HasValue ? project.Options.FromAxisTime(limits.Value.Left) : project.Options.Start;
            double end = limits.HasValue ? project.Options.FromAxisTime(limits.Value.Right) : project.Options.End;
            project.Options.ZeroCursor = Math.Clamp((start + end) / 2, project.Data.Time[0], project.Data.Time[^1]);
        }
        RebindOptions(); Commit();
    });
    void ApplyZero_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!project.Options.ZeroCursorVisible) return;
        project.Options.Zero = project.Options.ZeroCursor;
        RebindOptions(); Commit();
        Status($"Nula vremena postavljena na {project.Options.Zero:0.###} s od početka snimke (vertikalni kursor). Poništi: Graf… → Povijest ili polje Nula vremena.");
    });

    /// <summary>Two equal columns of cards.</summary>
    static (StackPanel Left, StackPanel Right) Columns(Panel host)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 2);
        grid.Children.Add(left); grid.Children.Add(right); host.Children.Add(grid);
        return (left, right);
    }

    /// <summary>A titled card; returns the panel to fill (or wraps <paramref name="content"/>).</summary>
    static StackPanel Card(Panel host, string title) { var content = new StackPanel(); Card(host, title, content); return content; }
    static FrameworkElement Card(Panel host, string title, StackPanel content)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(content);
        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 8), Margin = new Thickness(0, 0, 0, 14), Child = body };
        host.Children.Add(card); return card;
    }
    static StackPanel Expand(Panel host, string title)
    {
        var content = new StackPanel { Margin = new Thickness(0, 12, 0, 6) };
        host.Children.Add(new Expander { Header = title, Content = content, Margin = new Thickness(0, 12, 0, 6), IsExpanded = false });
        return content;
    }
    void PercentFromData_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        UpdateFields(SignalEditor);
        if (SignalList.SelectedItem is not Signal signal) return;
        var finite = signal.Scaled().Where(double.IsFinite).ToArray();
        if (finite.Length == 0 || finite.Min() == finite.Max()) throw new InvalidDataException("Signal nema promjenjiv raspon. Unesi granice za 0 % i 100 % ručno.");
        signal.PercentFromProject = false; signal.PercentMin = finite.Min(); signal.PercentMax = finite.Max();
    });
    static Binding BindingFor(string path, bool readOnly = false) => new(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true };
    static void Field(Panel host, string label, string path, bool readOnly = false)
    {
        host.Children.Add(new TextBlock { Text = label });
        var text = new TextBox { IsReadOnly = readOnly, TextWrapping = readOnly ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = readOnly ? 62 : double.PositiveInfinity };
        if (readOnly) { text.FontSize = 10; text.Background = Brushes.WhiteSmoke; }
        text.SetBinding(TextBox.TextProperty, BindingFor(path, readOnly)); host.Children.Add(text);
    }
    static void Pair(Panel host, (string Label, string Path) a, (string Label, string Path) b)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right); Field(left, a.Label, a.Path); Field(right, b.Label, b.Path); host.Children.Add(grid);
    }
    static void Choice(Panel host, string label, string path, string[] choices)
    { host.Children.Add(new TextBlock { Text = label }); var combo = new ComboBox { ItemsSource = choices }; combo.SetBinding(ComboBox.SelectedItemProperty, BindingFor(path)); host.Children.Add(combo); }
    static void Toggle(Panel host, string label, string path)
    { var check = new CheckBox { Content = label }; check.SetBinding(CheckBox.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay }); host.Children.Add(check); }
    static void Section(Panel host, string label) => host.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 14, 0, 10) });
    static void Hint(Panel host, string label) => host.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 12) });
    void Action(Panel host, string label, RoutedEventHandler action, bool primary = false)
    { var button = new Button { Content = label }; if (primary) button.Style = (Style)FindResource("Primary"); button.Click += action; host.Children.Add(button); }
    void Bind()
    {
        int selected = SignalList.SelectedIndex;
        signals = new(project.Data.Signals); notes = new(project.Notes);
        SignalList.ItemsSource = signals; SignalList.SelectedIndex = signals.Count > 0 ? Math.Clamp(selected, 0, signals.Count - 1) : -1;
        NotesGrid.ItemsSource = notes; DerivedA.ItemsSource = signals; DerivedB.ItemsSource = signals;
        DerivedA.SelectedIndex = signals.Count > 1 ? 1 : 0; DerivedB.SelectedIndex = 0;
        RebindOptions();
        ObserveNames();
    }
    void RebindOptions()
    { foreach (var editor in new Panel[] { OptionsEditor, ExportEditor, CursorEditor, ModeEditor }) { editor.DataContext = null; editor.DataContext = project.Options; } }
    void Sync()
    {
        FocusManager.SetFocusedElement(this, this); Keyboard.ClearFocus();
        NotesGrid.CommitEdit(DataGridEditingUnit.Cell, true); NotesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var editor in new[] { SignalEditor, OptionsEditor, ExportEditor, CursorEditor }) UpdateFields(editor);
        if (HasErrors(this) || new[] { SignalEditor, OptionsEditor, ExportEditor, CursorEditor }.Any(HasErrors)) throw new InvalidDataException("Ispravi crveno označena polja. Za decimalne brojeve koristi lokalni decimalni separator.");
        project.Data.Signals = signals.ToList(); project.Notes = notes.ToList();
        ApplyNominalRanges(project, activeDocument?.SlotId);
        PlotService.Validate(project);
    }
    static void UpdateFields(DependencyObject obj)
    {
        if (obj is TextBox text && !text.IsReadOnly) text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        foreach (object child in LogicalTreeHelper.GetChildren(obj)) if (child is DependencyObject dependency) UpdateFields(dependency);
    }
    static bool HasErrors(DependencyObject obj)
    {
        if (Validation.GetHasError(obj)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) if (HasErrors(VisualTreeHelper.GetChild(obj, i))) return true;
        return false;
    }
    void Commit()
    {
        Sync(); LearnNames(); RememberState(); Render();
    }
    void RememberState()
    {
        string snapshot = ProjectStore.Serialize(project);
        if (historyIndex < 0 || history[historyIndex] != snapshot)
        {
            if (historyIndex < history.Count - 1) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(snapshot); if (history.Count > 20) history.RemoveAt(0); historyIndex = history.Count - 1;
        }
    }
    void CommitLabelEdit()
    {
        // Keep the active zoom and plot controls while changing only label presentation.
        Sync(); RememberState();
        foreach (var plot in plots) plot.Refresh();
        Status("Raspored oblačića spremljen za prikaz, kopiranje i izvoz.");
    }
    void ResetHistory() { history.Clear(); history.Add(ProjectStore.Serialize(project)); historyIndex = 0; }
    void Render()
    {
        renderedTimeScale = project.Options.TimeScale; renderedZero = project.Options.Zero;
        renderedStart = project.Options.Start; renderedEnd = project.Options.End;
        foreach (var old in plots) old.Plot.Dispose(); plots.Clear(); PlotHost.Children.Clear(); PlotHost.RowDefinitions.Clear();
        EmptyState.Visibility = project.Data.Time.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (project.Data.Time.Length == 0) return;
        int row = 0;
        foreach (int panel in PlotService.Panels(project))
        {
            PlotHost.RowDefinitions.Add(new());
            var plot = new WpfPlot(); PlotService.Configure(plot.Plot, project, panel);
            _ = new ExtremaInteraction(plot, () => Guard(CommitLabelEdit));
            _ = new CursorInteraction(plot, project.Options, project.Data.Time[0], project.Data.Time[^1],
                () => { foreach (var p in plots) p.Refresh(); },
                () => Guard(() =>
                {
                    CursorEditor.DataContext = null; CursorEditor.DataContext = project.Options;
                    RememberState();
                    StatsGrid.ItemsSource = AnalysisService.Statistics(project.Data, project.Options);
                    DeltaText.Text = $"Δt = {Math.Abs(project.Options.CursorB - project.Options.CursorA) / project.Options.TimeScale:G6} {project.Options.TimeUnit}";
                    foreach (var p in plots) p.Refresh();
                    Status("Kursori ažurirani · vrijednosti na grafu ulaze u kopiranje i izvoz.");
                }));
            if (project.Options.HCursors && row == 0)
                _ = new HorizontalCursorInteraction(plot, project.Options, () => { foreach (var p in plots) p.Refresh(); },
                    () => Guard(() => { RememberState(); Status($"Horizontalni kursori: ΔY = {project.Options.HCursorB - project.Options.HCursorA:G6}"); }));
            if (project.Options.ZeroCursorVisible)
            {
                plot.Plot.Add.Plottable(new ZeroCursorOverlay(project, panel));
                _ = new ZeroCursorInteraction(plot, project.Options, project.Data.Time[0], project.Data.Time[^1],
                    () => { foreach (var p in plots) p.Refresh(); },
                    () => Guard(() => { RememberState(); Status($"Vertikalni kursor: {project.Options.ToAxisTime(project.Options.ZeroCursor):0.###} {project.Options.TimeUnit}"); }));
            }
            plot.ToolTip = "Min/max oblačić: povuci za pomicanje; desni klik za skrivanje ili automatski položaj.";
            Grid.SetRow(plot, row++); PlotHost.Children.Add(plot); plots.Add(plot);
            plot.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Shift && Keyboard.Modifiers != ModifierKeys.Control) return;
                var point = e.GetPosition(plot); var dpi = VisualTreeHelper.GetDpi(plot);
                var coordinates = plot.Plot.GetCoordinates(new ScottPlot.Pixel((float)(point.X * dpi.DpiScaleX), (float)(point.Y * dpi.DpiScaleY)));
                double t = project.Options.FromAxisTime(coordinates.X);
                if (Keyboard.Modifiers == ModifierKeys.Shift) project.Options.CursorA = t; else project.Options.CursorB = t;
                project.Options.Cursors = true; e.Handled = true; RebindOptions(); Guard(Commit);
            };
            plot.MouseUp += (_, _) => SyncPlotViews(plot);
            plot.MouseWheel += (_, _) => Dispatcher.BeginInvoke(() => SyncPlotViews(plot));
            plot.Refresh();
        }
        foreach (var p in plots) foreach (var other in plots.Where(q => q != p)) p.Plot.Axes.Link(other, x: true, y: false);
        StatsGrid.ItemsSource = AnalysisService.Statistics(project.Data, project.Options);
        DeltaText.Text = $"Δt = {Math.Abs(project.Options.CursorB - project.Options.CursorA) / project.Options.TimeScale:G6} {project.Options.TimeUnit}";
        FileTitle.Text = DocumentHeading();
        FileInfo.Text = (FileTitle.Text != project.Data.FileName ? project.Data.FileName + "  ·  " : "") + $"{project.Data.Time.Length:N0} uzoraka  ·  {signals.Count} signala  ·  {project.Data.Time[^1]:0.###} s  ·  interval {project.Options.Start:0.###}–{project.Options.End:0.###} s";
        int missingNames = signals.Count(s => s.Visible && string.IsNullOrWhiteSpace(s.Name));
        NamingHint.Text = missingNames > 0 ? $"Unesi nazive za {missingNames} signala prije izvoza. Izvorni tag služi samo za identifikaciju." : "";
        NamingHint.Visibility = missingNames > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningsText.Text = $"Izvorno vrijeme: {project.Data.TimeHeader}\n\n" + (project.Data.Warnings.Count > 0 ? string.Join("\n", project.Data.Warnings) : "Uvoz bez pronađenih nepravilnosti.") + "\n\nStatistika: konačne vrijednosti u potvrđenom intervalu; σ je populacijska standardna devijacija. Kursori koriste linearnu interpolaciju bez ekstrapolacije. Izvedeni signali čuvaju rezultat iz trenutka izračuna.";
        Title = "TIA Trace Studio · " + project.Data.FileName;
    }
    void SyncPlotViews(WpfPlot source)
    {
        if (synchronizing || !plots.Contains(source)) return;
        synchronizing = true;
        try { var limits = source.Plot.Axes.GetLimits(); foreach (var p in plots.Where(p => p != source)) { p.Plot.Axes.SetLimitsX(limits.Left, limits.Right); p.Refresh(); } }
        finally { synchronizing = false; }
    }
    void Status(string text) => StatusText.Text = text;
    void Guard(System.Action action)
    { try { action(); } catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "TIA Trace Studio", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    Task LoadPath(string path) => IsProjectPath(path) ? OpenWorkspacePath(path) : AddPaths([path]);
    public static StudioProject NewProject(TraceData data) => new() { Data = data, Options = new() { Title = Path.GetFileNameWithoutExtension(data.FileName), Start = data.Time[0], End = data.Time[^1], CursorA = data.Time[0], CursorB = data.Time[^1] } };
    async void OpenCsv_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "Trace CSV / TSV|*.csv;*.tsv|Sve datoteke|*.*", Multiselect = true }; if (dialog.ShowDialog(this) == true) await AddPaths(dialog.FileNames); }
    async void OpenProject_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Filter = "Trace Studio projekt|*.tracestudio;*.tiaproject;*.tiatrace;*.tiasession" }; if (dialog.ShowDialog(this) == true) await OpenWorkspacePath(dialog.FileName); }
    bool SaveProject()
    {
        return SaveSession();
    }
    void SaveProject_Click(object sender, RoutedEventArgs e) => Guard(() => SaveProject());
    /// <summary>Toolbar switches without their own logic (e.g. min / max) redraw at once.</summary>
    void Redraw_Click(object sender, RoutedEventArgs e) => Guard(Commit);
    void Apply_Click(object sender, RoutedEventArgs e) => Guard(() => { Commit(); Status("Postavke primijenjene · izvoz koristi ovaj raspon i izgled."); });
    void ToggleCursors_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length == 0) { project.Options.Cursors = false; RebindOptions(); return; }
        if (project.Options.Cursors)
        {
            var limits = plots.FirstOrDefault()?.Plot.Axes.GetLimits();
            double start = Math.Max(project.Data.Time[0], limits.HasValue ? project.Options.FromAxisTime(limits.Value.Left) : project.Options.Start);
            double end = Math.Min(project.Data.Time[^1], limits.HasValue ? project.Options.FromAxisTime(limits.Value.Right) : project.Options.End);
            if (end <= start) { start = project.Data.Time[0]; end = project.Data.Time[^1]; }
            project.Options.CursorA = start + (end - start) * .25;
            project.Options.CursorB = start + (end - start) * .75;
        }
        RebindOptions(); Commit();
    });
    void SignalList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SignalEditor != null) { SignalEditor.DataContext = SignalList.SelectedItem; UpdatePercentInfo(); } }
    void AllSignals_Click(object sender, RoutedEventArgs e) => Guard(() => { foreach (var s in signals) s.Visible = true; Commit(); });
    void RemoveSignal_Click(object sender, RoutedEventArgs e) => Guard(() => { if (SignalList.SelectedItem is not Signal s) return; if (signals.Count == 1) throw new InvalidDataException("Zadrži barem jedan signal."); signals.Remove(s); Commit(); });
    void Fit_Click(object sender, RoutedEventArgs e) => Guard(() => { if (project.Data.Time.Length == 0) return; project.Options.Start = project.Data.Time[0]; project.Options.End = project.Data.Time[^1]; project.Options.YMin = project.Options.YMax = project.Options.RightMin = project.Options.RightMax = null; RebindOptions(); Commit(); });
    void Zero_Click(object sender, RoutedEventArgs e) => Guard(() => { project.Options.Zero = project.Options.Start; RebindOptions(); Commit(); });
    void UseView_Click(object sender, RoutedEventArgs e) => Guard(() => { if (plots.Count == 0) return; var limits = plots[0].Plot.Axes.GetLimits(); project.Options.Start = project.Options.FromAxisTime(limits.Left); project.Options.End = project.Options.FromAxisTime(limits.Right); RebindOptions(); Commit(); Status("Vremenski raspon pogleda primijenjen na statistiku i izvoz."); });
    void CursorRange_Click(object sender, RoutedEventArgs e) => Guard(() => { project.Options.Start = Math.Min(project.Options.CursorA, project.Options.CursorB); project.Options.End = Math.Max(project.Options.CursorA, project.Options.CursorB); RebindOptions(); Commit(); });
    void AddNote_Click(object sender, RoutedEventArgs e) => Guard(() => { Sync(); notes.Add(new() { Time = project.Options.Start, Value = plots.FirstOrDefault()?.Plot.Axes.Left.Max ?? 0, Panel = PlotService.Panels(project)[0] }); Commit(); });
    void RemoveNote_Click(object sender, RoutedEventArgs e) => Guard(() => { if (NotesGrid.SelectedItem is TraceNote note) { notes.Remove(note); Commit(); } });
    void Difference_Click(object sender, RoutedEventArgs e) => Guard(() => { Sync(); if (DerivedA.SelectedItem is Signal a && DerivedB.SelectedItem is Signal b) { signals.Add(AnalysisService.Difference(a, b)); Commit(); } });
    void Derivative_Click(object sender, RoutedEventArgs e) => Guard(() => { Sync(); if (DerivedA.SelectedItem is Signal a) { signals.Add(AnalysisService.Derivative(a, project.Data.Time)); Commit(); } });
    void Undo_Click(object sender, RoutedEventArgs e) => Guard(() => { if (historyIndex <= 0) return; project = ProjectStore.Deserialize<StudioProject>(history[--historyIndex]); Bind(); Render(); Status("Promjena poništena."); });
    void Redo_Click(object sender, RoutedEventArgs e) => Guard(() => { if (historyIndex >= history.Count - 1) return; project = ProjectStore.Deserialize<StudioProject>(history[++historyIndex]); Bind(); Render(); Status("Promjena ponovljena."); });
    void SavePreset_Click(object sender, RoutedEventArgs e) => Guard(() => { Sync(); var dialog = new SaveFileDialog { Filter = "Predložak grafa|*.tiastyle", FileName = "Commissioning.tiastyle" }; if (dialog.ShowDialog(this) == true) { ProjectStore.Save(dialog.FileName, ProjectStore.Preset(project)); Status("Predložak spremljen."); } });
    void LoadPreset_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        Sync(); var dialog = new OpenFileDialog { Filter = "Predložak grafa|*.tiastyle" }; if (dialog.ShowDialog(this) != true) return;
        var next = ProjectStore.Clone(project); int count = ProjectStore.ApplyPreset(next, ProjectStore.Deserialize<StudioPreset>(File.ReadAllText(dialog.FileName))); PlotService.Validate(next); project = next; Bind(); Commit(); Status($"Predložak primijenjen na {count} podudarnih signala. Vremenski raspon je zadržan.");
    });
    void Export_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        Sync(); SignalDisplay.RequireNames(project); var dialog = new SaveFileDialog { Filter = "JPEG slika|*.jpg|PNG slika|*.png", FileName = Path.GetFileNameWithoutExtension(project.Data.FileName) + ".jpg" };
        if (dialog.ShowDialog(this) != true) return;
        Commit(); PlotService.Export(project, dialog.FileName); Status($"Slika spremljena · {project.Options.ExportWidth} × {project.Options.ExportHeight} px · {dialog.FileName}");
    });
    void CopyImage_Click(object sender, RoutedEventArgs e) => Guard(() => { Commit(); Clipboard.SetImage(PlotService.Render(project)); Status("Slika kopirana u međuspremnik."); });
    void ShowAllExtrema_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        foreach (var s in signals) foreach (var label in new[] { s.MinimumLabel, s.MaximumLabel, s.ConstantLabel }) label.Hidden = false;
        SignalEditor.DataContext = null; SignalEditor.DataContext = SignalList.SelectedItem;
        Commit();
    });
    void ResetExtremaPositions_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        foreach (var s in signals) foreach (var label in new[] { s.MinimumLabel, s.MaximumLabel, s.ConstantLabel }) label.ResetPosition();
        Commit();
    });
    void PreviewExport_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        Commit(); var image = PlotService.Render(project);
        var layout = new DockPanel { Margin = new Thickness(15) };
        var save = new Button { Content = $"Spremi sliku · {image.PixelWidth} × {image.PixelHeight} px", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("Primary") };
        save.Click += Export_Click; DockPanel.SetDock(save, Dock.Bottom); layout.Children.Add(save);
        layout.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 12) });
        new Window { Owner = this, Title = "Pregled izvoza · TIA Trace Studio", Width = Dialogs.Width(1400), Height = Dialogs.Height(920), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.WhiteSmoke, Content = layout }.ShowDialog();
    });
    async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Sync(); var dialog = new OpenFileDialog { Filter = "Trace CSV / TSV|*.csv;*.tsv", Title = "Odaberi trace za usporedbu" }; if (dialog.ShowDialog(this) != true) return;
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Pomak drugog tracea [s]", FontWeight = FontWeights.SemiBold });
            var input = new TextBox { Text = "0" }; panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = "Pozitivno = kasnije. Drugi trace se interpolira na vremena prvog; izvan preklapanja ostaju praznine.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var confirm = new Button { Content = "Dodaj usporedbu", IsDefault = true }; panel.Children.Add(confirm);
            double shift = 0; var prompt = new Window { Owner = this, Title = "Vremensko poravnanje", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ResizeMode = ResizeMode.NoResize };
            confirm.Click += (_, _) => { if (double.TryParse(input.Text, out shift) && double.IsFinite(shift)) prompt.DialogResult = true; else input.BorderBrush = Brushes.Red; };
            if (prompt.ShowDialog() != true) return;
            IsEnabled = false; Status("Učitavanje usporednog tracea…");
            var other = await Task.Run(() => TraceCsv.Read(dialog.FileName));
            var additions = AnalysisService.Compare(project.Data, other, shift);
            if (!additions.Any(s => s.Values.Any(double.IsFinite))) throw new InvalidDataException("Traceovi nemaju preklapanje s tim vremenskim pomakom.");
            foreach (var signal in additions) signals.Add(signal);
            project.Data.Warnings.Add($"Usporedba: {other.FileName}, pomak {shift:G6} s; linearna interpolacija na vremensku os osnovnog tracea.");
            project.Data.Warnings.AddRange(other.Warnings.Select(w => $"{other.FileName}: {w}"));
            Commit(); Status("Usporedni trace dodan crtkanim linijama. Uklanjanje usporedbe: Poništi ili ukloni signale.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Usporedba", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsEnabled = true; }
    }
    async void Batch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Sync(); var files = new OpenFileDialog { Filter = "Trace CSV|*.csv;*.tsv", Multiselect = true }; if (files.ShowDialog(this) != true) return;
            var folder = new OpenFolderDialog { Title = "Odaberi mapu za JPG slike" }; if (folder.ShowDialog(this) != true) return;
            var preset = ProjectStore.Preset(project); var errors = new List<string>(); int done = 0; IsEnabled = false;
            foreach (string file in files.FileNames)
            {
                try
                {
                    Status($"Grupni izvoz {done + errors.Count + 1}/{files.FileNames.Length} · {Path.GetFileName(file)}");
                    var next = NewProject(await Task.Run(() => TraceCsv.Read(file))); ProjectStore.ApplyPreset(next, preset); next.Options.Title = Path.GetFileNameWithoutExtension(file);
                    string stem = Path.Combine(folder.FolderName, Path.GetFileNameWithoutExtension(file)), target = stem + ".jpg"; int suffix = 2;
                    while (File.Exists(target)) target = stem + $" ({suffix++}).jpg";
                    PlotService.Export(next, target); done++;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
            Status($"Grupni izvoz završen: {done} slika, {errors.Count} pogrešaka.");
            MessageBox.Show(this, $"Spremljeno slika: {done}\nMapa: {folder.FolderName}" + (errors.Count > 0 ? "\n\n" + string.Join("\n", errors) : ""), "Grupni izvoz");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Grupni izvoz"); }
        finally { IsEnabled = true; }
    }
    void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) await AddPaths(paths); }
    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (recorders.Any(r => r.IsRecording))
        {
            MessageBox.Show(this, "Snimanje s PLC-a je u tijeku. Zaustavi ga i spremi prije zatvaranja aplikacije.", "Snimanje", MessageBoxButton.OK, MessageBoxImage.Warning);
            recorders.First(r => r.IsRecording).Activate(); e.Cancel = true; return;
        }
        if (!CanCloseDocuments()) { e.Cancel = true; return; }
        ClosePopups();
        autosave.Stop();
        try { if (recoveryEnabled && File.Exists(RecoveryPath)) File.Delete(RecoveryPath); } catch { /* recovery is best effort */ }
        try { if (recoveryEnabled && File.Exists(SessionRecoveryPath)) File.Delete(SessionRecoveryPath); } catch { /* recovery is best effort */ }
        foreach (var plot in plots) plot.Plot.Dispose();
    }
}

sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
