using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScottPlot.WPF;
using TiaTraceStudio;

internal static partial class Program
{
    /// <summary>Every control around the graph, clicked like a mouse click, must change the graph at once.</summary>
    static void ToolbarChecks(string sample)
    {
        var window = new MainWindow(false) { ShowActivated = false, Width = 1600, Height = 950 }; window.Show();
        typeof(MainWindow).GetField("workspacePath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Path.Combine(output, "Toolbar.tracestudio"));
        AddFiles(window, sample); Pump(TimeSpan.FromMilliseconds(200));
        var p = Active(window);
        for (int i = 0; i < p.Data.Signals.Count; i++)
        {
            var s = p.Data.Signals[i];
            s.Name = $"Kanal {i + 1}"; s.NameOverride = true; s.Unit = "bar"; s.PercentFromProject = false;
            var finite = s.Values.Where(double.IsFinite).ToArray(); s.PercentMin = finite.Min(); s.PercentMax = finite.Max() > finite.Min() ? finite.Max() : finite.Min() + 1;
        }
        Invoke(window, "Commit"); Pump(TimeSpan.FromMilliseconds(150));
        var editor = (WrapPanel)window.FindName("ModeEditor");
        T Control<T>(string content) where T : ContentControl => editor.Children.OfType<T>().First(c => c.Content as string == content);
        void Click(ButtonBase b) { typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, null); Pump(TimeSpan.FromMilliseconds(150)); }
        WpfPlot Plot() => ((Grid)window.FindName("PlotHost")).Children.OfType<WpfPlot>().First();
        bool Has<T>() where T : ScottPlot.IPlottable => Plot().Plot.GetPlottables<T>().Any();

        var extrema = Control<CheckBox>("Označi min / max");
        Click(extrema); bool on = p.Options.ShowExtrema && Has<ExtremaOverlay>();
        Click(extrema);
        Check(on && !p.Options.ShowExtrema && !Has<ExtremaOverlay>(), "Toolbar: Označi min / max shows and hides the min/max labels at once");

        var zero = Control<CheckBox>("Kursor"); var applyZero = Control<Button>("Primijeni nulu grafa");
        Click(zero); bool cursorOn = Has<ZeroCursorOverlay>() && applyZero.IsEnabled;
        p.Options.ZeroCursor = p.Data.Time[10]; Click(applyZero);
        bool zeroed = p.Options.Zero == p.Data.Time[10];
        Click(zero);
        Check(cursorOn && zeroed && !Has<ZeroCursorOverlay>() && !applyZero.IsEnabled, "Toolbar: Kursor shows the vertical cursor; Primijeni nulu grafa moves time zero to it");
        p.Options.Zero = 0; Invoke(window, "RebindOptions"); Invoke(window, "Commit");

        var vertical = Control<CheckBox>("Vertikalni A/B");
        Click(vertical); bool abOn = Has<CursorReadoutOverlay>();
        Click(vertical);
        Check(abOn && !Has<CursorReadoutOverlay>(), "Toolbar: Vertikalni A/B shows and hides the A/B statistics");

        var horizontal = Control<CheckBox>("Horizontalni A/B");
        Click(horizontal); Plot().Refresh(); Pump(TimeSpan.FromMilliseconds(150));
        var hOverlay = Plot().Plot.GetPlottables<HorizontalCursorOverlay>().SingleOrDefault();
        int visibleSignals = p.Data.Signals.Count(s => s.Visible && s.Panel == PlotService.Panels(p)[0]);
        bool hOn = hOverlay != null && hOverlay.LastRows.Count == visibleSignals + 1 && hOverlay.LastRows.Skip(1).All(r => r.Contains("A = ") && r.Contains("ΔY") && r.Contains("bar"));
        window.UpdateLayout();
        var shot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot)); using (var stream = File.Create(Path.Combine(output, "Horizontal-cursors.png"))) png.Save(stream);
        Click(horizontal);
        Check(hOn && !Has<HorizontalCursorOverlay>(), "Toolbar: Horizontalni A/B shows a value box for every signal (A, B, ΔY in its unit) and hides again");

        var mode = editor.Children.OfType<ComboBox>().First();
        mode.SelectedItem = ChartOptions.PercentMode; Pump(TimeSpan.FromMilliseconds(150));
        bool percent = (Plot().Plot.Axes.Left.Label.Text ?? "").StartsWith("Normalizirano");
        mode.SelectedItem = ChartOptions.UnitsMode; Pump(TimeSpan.FromMilliseconds(150));
        Check(percent && Plot().Plot.Axes.Left.Label.Text == "bar", "Toolbar: display mode switches between normalized and physical units at once");

        p.Options.Start = p.Data.Time[5]; p.Options.End = p.Data.Time[20]; Invoke(window, "RebindOptions"); Invoke(window, "Commit");
        Click(Control<Button>("Cijela snimka"));
        Check(p.Options.Start == p.Data.Time[0] && p.Options.End == p.Data.Time[^1], "Toolbar: Cijela snimka shows the whole recording");
        double left = p.Options.ToAxisTime(p.Data.Time[3]), right = p.Options.ToAxisTime(p.Data.Time[30]);
        Plot().Plot.Axes.SetLimitsX(left, right);
        Click(Control<Button>("Koristi pogled"));
        Check(Math.Abs(p.Options.Start - p.Data.Time[3]) < 1e-9 && Math.Abs(p.Options.End - p.Data.Time[30]) < 1e-9, "Toolbar: Koristi pogled takes the zoomed time range");
        // As typed in Graf… → Naslov: the text box holds the new title until Primijeni.
        var titleBox = FindVisual<TextBox>((Window)Get(window, "settingsWindow")).Concat(FindVisual<TextBox>(window)).FirstOrDefault(t => t.Text == p.Options.Title && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "Title");
        if (titleBox != null) titleBox.Text = "Naslov iz testa tipki"; else { p.Options.Title = "Naslov iz testa tipki"; Invoke(window, "RebindOptions"); }
        Click(Control<Button>("Primijeni"));
        Check(Plot().Plot.Axes.Title.Label.Text == "Naslov iz testa tipki", "Toolbar: Primijeni redraws the graph with the settings");

        var settings = (Window)Get(window, "settingsWindow"); var tools = (Window)Get(window, "toolsWindow");
        foreach (var (label, tab) in new[] { ("Signali…", 0), ("Graf…", 1), ("Izvoz…", 2) })
        {
            Click(Control<Button>(label));
            var tabs = (TabControl)Get(window, "settingsTabs");
            Check(settings.IsVisible && tabs?.SelectedIndex == tab, $"Toolbar: {label} opens its settings tab");
            settings.Hide();
        }
        Click(Control<Button>("Alati…"));
        Check(tools.IsVisible, "Toolbar: Alati… opens the tools window"); tools.Hide();

        Button Top(string content) => FindVisual<Button>(window).First(b => b.Content as string == content);
        Clipboard.Clear(); Click(Top("Kopiraj sliku"));
        Check(Clipboard.ContainsImage(), "Toolbar: Kopiraj sliku puts the graph on the clipboard");
        Click(Top("Spremi projekt"));
        Check(File.Exists(Path.Combine(output, "Toolbar.tracestudio")) && TraceSession.Load(Path.Combine(output, "Toolbar.tracestudio")).Projects.Single().Options.Title == "Naslov iz testa tipki", "Toolbar: Spremi projekt saves the recording with its settings");
        Click(Top("Projekt  ▾"));
        var menu = PresentationSource.CurrentSources.OfType<PresentationSource>().Select(src => src.RootVisual).SelectMany(root => root == null ? [] : FindVisual<ContextMenu>(root).Prepend(root as ContextMenu)).OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)
            ?? FindVisual<FrameworkElement>(window).Select(e => e.ContextMenu).FirstOrDefault(m => m?.IsOpen == true);
        Check(menu != null && menu.Items.Count > 3, "Toolbar: Projekt ▾ opens the project menu"); if (menu != null) menu.IsOpen = false;
        Invoke(window, "SaveWorkspaceTo", Path.Combine(output, "Toolbar.tracestudio"));
        window.Close();
    }
}
