using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TiaTraceStudio;

/// <summary>
/// Keeps the main screen to the test tree and the graph: graph settings, analysis tools and project
/// actions open in popups or a menu instead of permanent side panels.
/// </summary>
public partial class MainWindow
{
    Window? settingsWindow, toolsWindow;
    TabControl? settingsTabs;
    bool closingPopups;

    void BuildPopups()
    {
        // Graph settings (Signali / Graf / Izvoz) move from the side column into one popup window.
        settingsTabs = (TabControl)SettingsPane.Child;
        SettingsPane.Child = null; SettingsPane.Visibility = Visibility.Collapsed;
        SettingsColumn.Width = new GridLength(0); SettingsGap.Width = new GridLength(0);
        if (settingsTabs.Items[0] is TabItem signalsTab)
        {
            SignalsTab.Children.Clear();
            var listPane = new DockPanel { Margin = new Thickness(4) };
            var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
            heading.Children.Add(new TextBlock { Text = "Signali", FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 6, 0, 4) });
            heading.Children.Add(new TextBlock { Text = "Kvačica uključuje signal u graf. Odaberi signal za uređivanje. * Naziv je obavezan za izvoz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) });
            listPane.Children.Add(heading);
            var apply = new Button { Content = "Primijeni promjene", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 10, 0, 0) };
            apply.Click += Apply_Click; DockPanel.SetDock(apply, Dock.Bottom); listPane.Children.Add(apply);
            SignalList.Height = double.NaN; listPane.Children.Add(SignalList);
            var editorScroll = new ScrollViewer { Content = SignalEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
            SignalEditor.Margin = new Thickness(0, 8, 0, 0);
            var split = new Grid(); split.ColumnDefinitions.Add(new() { Width = new GridLength(320) }); split.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); split.ColumnDefinitions.Add(new());
            Grid.SetColumn(editorScroll, 2); split.Children.Add(listPane); split.Children.Add(editorScroll);
            signalsTab.Content = split;
        }
        foreach (var tab in settingsTabs.Items.OfType<TabItem>()) { tab.FontSize = 14; tab.Padding = new Thickness(18, 9, 18, 9); }
        settingsWindow = ToolWindow("Postavke grafa", settingsTabs, Dialogs.Width(1180), Dialogs.Height(840));

        // Statistics, cursors, notes and derived signals move from below the graph into their own popup.
        var tools = (FrameworkElement)AdvancedTools.Content;
        AdvancedTools.Content = null; AdvancedTools.Visibility = Visibility.Collapsed;
        tools.Height = double.NaN; tools.Margin = new Thickness(0);
        toolsWindow = ToolWindow("Alati · statistika, kursori, bilješke, izvedeni signali", tools, Dialogs.Width(1260), Dialogs.Height(700));

        foreach (var (label, action) in new (string, System.Action)[]
        {
            ("Signali…", () => ShowSettings(0)), ("Graf…", () => ShowSettings(1)), ("Izvoz…", () => ShowSettings(2)), ("Alati…", () => ShowPopup(toolsWindow))
        })
        {
            var button = new Button { Content = label, ToolTip = label.TrimEnd('…') };
            button.Click += (_, _) => action();
            ModeEditor.Children.Add(button);
        }
    }

    Window ToolWindow(string title, FrameworkElement content, double width, double height)
    {
        var window = new Window
        {
            Title = title, Width = width, Height = height, ShowInTaskbar = false, Background = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(10), Child = content }
        };
        window.Resources.MergedDictionaries.Add(Resources);
        // Closing only hides the popup so its fields, bindings and position stay as they were.
        window.Closing += (_, e) => { if (!closingPopups) { e.Cancel = true; window.Hide(); } };
        return window;
    }

    void ShowPopup(Window? window)
    {
        if (window == null) return;
        if (window.Owner == null && IsLoaded) window.Owner = this;
        window.Show(); window.Activate();
    }

    void ShowSettings(int tab)
    {
        if (settingsTabs != null) settingsTabs.SelectedIndex = tab;
        ShowPopup(settingsWindow);
    }

    void ClosePopups()
    {
        closingPopups = true;
        settingsWindow?.Close(); toolsWindow?.Close();
    }

    void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        void Item(string header, RoutedEventHandler handler, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += handler; menu.Items.Add(item);
        }
        Item("Novi projekt…", NewWorkspace_Click);
        Item("Spremi projekt kao…", (_, _) => Guard(() => SaveSession(true)));
        menu.Items.Add(new Separator());
        Item("Definicija postrojenja…", EditPlant_Click);
        Item("PLC-ovi i Plant Configurator…", (_, _) => Guard(EditPlcs), plant != null);
        Item("Katalog testova (upute i zadana mjerenja)…", (_, _) => Guard(() => EditCatalog(null)));
        Item("Nova grupa (vlastito stablo)…", (_, _) => Guard(() => EditGroup(null)), plant != null);
        Item("Novo korisničko snimanje…", (_, _) => Guard(NewUserRecording), plant != null);
        menu.Items.Add(new Separator());
        Item("Dodaj CSV datoteke…", OpenCsv_Click);
        Item("Ukloni trenutačnu snimku", RemoveDocument_Click, project.Data.Time.Length > 0);
        Item("Postavke signala ovog projekta (nazivi, jedinice, scaling)…", EditNameLibrary_Click);
        Item("Izvezi sve učitane grafove…", ExportDocuments_Click, documents.Count > 0);
        Item("Izvještaj u Word (.docx)…", (_, _) => Guard(ExportReport), plant != null);
        menu.IsOpen = true;
    }

    /// <summary>How to perform the test and which measurements it records.</summary>
    void ShowTestDetails(TestSlot slot)
    {
        var helpColumn = new StackPanel();
        string text = catalog.HelpFor(slot);
        Card(helpColumn, "Kako izvesti test").Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(text) ? "Upute još nisu upisane." : text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 14, LineHeight = 21 });
        var channels = ChannelsOf(slot);
        var channelColumn = new StackPanel();
        Card(channelColumn, $"Mjerenja za snimanje ({channels.Count})" + (slot.Channels != null && !slot.IsUser ? " · uređeno za ovaj test" : "")).Children.Add(ChannelList(slot, channels));
        var columns = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1.1, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        var helpScroll = new ScrollViewer { Content = helpColumn, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var channelScroll = new ScrollViewer { Content = channelColumn, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(channelScroll, 2);
        columns.Children.Add(helpScroll); columns.Children.Add(channelScroll);
        var heading = new TextBlock { Text = slot.Path, FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 14, 16, 12) };

        var dialog = new Window { Owner = this, Title = "Upute i mjerenja · " + slot.ChartTitle, Width = Dialogs.Width(1180), Height = Dialogs.Height(820), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var buttons = new WrapPanel { Margin = new Thickness(16, 0, 16, 12) };
        Action(buttons, "Uredi mjerenja…", (_, _) => { dialog.Close(); Guard(() => EditChannels(slot)); }, true);
        Action(buttons, "Kopiraj popis tagova", (_, _) => { Clipboard.SetText(string.Join(Environment.NewLine, channels.Select(c => Display(c.Tag, c.Plc)))); Status("Popis tagova kopiran u međuspremnik."); });
        if (!slot.IsUser) Action(buttons, "Uredi upute i zadana mjerenja (katalog)…", (_, _) => { dialog.Close(); Guard(() => EditCatalog(slot.Kind)); });
        Action(buttons, "Zatvori", (_, _) => dialog.Close());
        var layout = new DockPanel(); DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        DockPanel.SetDock(buttons, Dock.Bottom); buttons.Margin = new Thickness(16, 10, 16, 14); layout.Children.Add(buttons);
        layout.Children.Add(columns);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    /// <summary>Measurements of a test; with its recording shown, each one is marked present (✓) or missing (✗).</summary>
    StackPanel ChannelList(TestSlot slot, List<(string Tag, PlcConnection? Plc)> channels)
    {
        var panel = new StackPanel();
        if (channels.Count == 0) { panel.Children.Add(new TextBlock { Text = "Nema odabranih mjerenja. Dodaj ih tipkom Uredi mjerenja.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap }); return panel; }
        bool multiPlc = channels.Select(c => c.Plc).Distinct().Count() > 1;
        bool check = activeDocument?.SlotId == slot.Id;
        var present = check ? project.Data.Signals.Select(s => SplitSource(s.Source)).Select(x => (x.Plc, Tag: TagMatcher.Normalize(x.Tag))).ToList() : [];
        int missing = 0;
        foreach (var (tag, channelPlc) in channels)
        {
            string shown = Display(tag, channelPlc); var measurement = CatalogOf(channelPlc)?.Find(tag);
            bool found = present.Any(p => TagMatcher.Matches(p.Tag, shown) && (p.Plc == null || p.Plc == channelPlc?.Name));
            if (check && !found) missing++;
            panel.Children.Add(new TextBlock
            {
                Text = (check ? (found ? "✓  " : "✗  ") : "•  ") + (multiPlc ? $"{channelPlc?.Name ?? "?"}: " : "") + shown + (measurement != null ? $"  ({measurement.Name})" : ""),
                TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
                Foreground = !check ? Ink : found ? Done : Brushes.Firebrick, Margin = new Thickness(0, 1, 0, 1)
            });
        }
        if (missing > 0) panel.Children.Add(new TextBlock { Text = $"U prikazanoj snimci nedostaje {missing} od {channels.Count} mjerenja.", Foreground = Brushes.Firebrick, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    void EditNote(TestSlot slot)
    {
        var box = new TextBox { Text = slot.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new Window { Owner = this, Title = "Napomena · " + slot.ChartTitle, Width = Dialogs.Width(760), Height = Dialogs.Height(520), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Spremi", (_, _) => { slot.Note = box.Text; dialog.Close(); ShowSlot(slot); }, true);
        var layout = new DockPanel { Margin = new Thickness(14) }; DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons); layout.Children.Add(box);
        dialog.Content = layout; dialog.Loaded += (_, _) => box.Focus();
        dialog.ShowDialog();
    }
}
