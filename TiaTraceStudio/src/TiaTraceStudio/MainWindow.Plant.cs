using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TiaTraceStudio;

public partial class MainWindow
{
    PlantDefinition? plant;
    List<TestSlot> slots = [];
    TestCatalog catalog = TestCatalog.Default();
    PlantConfigLink? plantConfig;
    Dictionary<string, TagAddress> tagAddresses = new(StringComparer.OrdinalIgnoreCase);
    readonly List<RecorderWindow> recorders = [];
    internal Func<string, short, short, IPlcReader> CreatePlcReader = (ip, rack, slot) => new S7NetReader(ip, rack, slot);
    readonly HashSet<string> expandedKeys = [], collapsedKeys = [];
    bool buildingTree, treeRefreshPending;
    string? selectedSlotId;

    static readonly Brush Accent = Frozen(0x14, 0x6C, 0xDA), Done = Frozen(0x10, 0x9C, 0x5B), Muted = Frozen(0x94, 0xA3, 0xB8), Ink = Frozen(0x17, 0x2B, 0x45), Warn = Frozen(0xA8, 0x64, 0x0A);
    static Brush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    TestSlot? SlotById(string? id) => id == null ? null : slots.FirstOrDefault(s => s.Id == id);
    List<OpenTrace> RecordingsOf(TestSlot slot) => documents.Where(d => d.SlotId == slot.Id).ToList();
    (string Glyph, Brush Brush, string Text) State(TestSlot slot)
    {
        if (slot.NotApplicable) return ("–", Muted, "Nije primjenjivo");
        if (slot.Reviewed) return ("✔", Done, "Pregledano");
        int count = RecordingsOf(slot).Count;
        if (count > 0) return ("●", Accent, count == 1 ? "Učitano · 1 snimka" : $"Učitano · {count} snimke");
        if (slot.HasManualValues) return ("●", Accent, "Uneseno ručno");
        return ("○", Muted, "Čeka");
    }
    bool IsDone(TestSlot slot) => State(slot).Glyph != "○";
    string Progress(IEnumerable<TestSlot> list) { var items = list.Where(s => !s.Retired).ToList(); return $"{items.Count(IsDone)}/{items.Count}"; }

    void ApplyPlantLayout()
    {
        bool visible = plant != null;
        TreeColumn.Width = visible ? new GridLength(340) : new GridLength(0);
        TreeGap.Width = visible ? new GridLength(10) : new GridLength(0);
        TreePane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rebuilding the tree inside its own selection event is unsafe, so refreshes are queued.</summary>
    void ScheduleTreeRefresh()
    {
        if (treeRefreshPending) return;
        treeRefreshPending = true;
        Dispatcher.BeginInvoke(() => { treeRefreshPending = false; RefreshTree(); }, DispatcherPriority.Background);
    }

    void RefreshTree()
    {
        ApplyPlantLayout();
        if (plant == null) return;
        buildingTree = true;
        try
        {
            PlantTitle.Text = string.IsNullOrWhiteSpace(plant.Location) ? plant.Name : $"{plant.Name} · {plant.Location}";
            var active = slots.Where(s => !s.Retired && !s.Deleted).ToList();
            PlantProgress.Text = $"Testovi: {active.Count(IsDone)} od {active.Count} · snimki: {documents.Count}";
            ShowPlantConfigInfo();
            PlantTree.Items.Clear();
            TreeViewItem? docItem = null, slotItem = null;
            BuildSlotTree(PlantTree.Items, slots, slot =>
            {
                var recordings = RecordingsOf(slot);
                var item = SlotItem(slot, recordings.Count > 0 ? $" ({recordings.Count})" : "");
                Track(item, slot.Id, false);
                double width = TextWidth(SlotLevel(slot) + 1);
                foreach (var doc in recordings)
                {
                    var child = new TreeViewItem { Header = Header("∿", Accent, doc.Title, null, bold: doc == activeDocument, maxWidth: width), Tag = doc, ToolTip = doc.Title };
                    item.Items.Add(child); if (doc == activeDocument) docItem = child;
                }
                if (slot.Id == selectedSlotId) slotItem = item;
                return item;
            }, (key, level) => expandedKeys.Contains(key) || (!collapsedKeys.Contains(key) && level <= 1), Track);
            var unassigned = documents.Where(d => SlotById(d.SlotId) == null).ToList();
            if (unassigned.Count > 0)
            {
                var node = new TreeViewItem { Header = Header("!", Warn, "Nepridružene snimke", $"{unassigned.Count}", bold: true), Tag = "__unassigned", IsExpanded = true };
                foreach (var doc in unassigned)
                {
                    var child = new TreeViewItem { Header = Header("∿", Warn, doc.Title, null, bold: doc == activeDocument, maxWidth: TextWidth(1)), Tag = doc, ToolTip = doc.Title };
                    node.Items.Add(child); if (doc == activeDocument) docItem = child;
                }
                PlantTree.Items.Add(node);
            }
            // Keep a test the user selected even when the graph still shows a recording of another test.
            var target = selectedSlotId == null || activeDocument?.SlotId == selectedSlotId ? docItem ?? slotItem : slotItem ?? docItem;
            if (target != null)
            {
                for (var parent = target.Parent as TreeViewItem; parent != null; parent = parent.Parent as TreeViewItem) parent.IsExpanded = true;
                target.IsSelected = true; target.BringIntoView();
            }
            AttachContextMenus(PlantTree.Items);
            ShowSelection();
        }
        finally { buildingTree = false; }
    }

    void Track(TreeViewItem item, string key, bool unused)
    {
        item.Expanded += (_, e) => { if (e.OriginalSource == item) { expandedKeys.Add(key); collapsedKeys.Remove(key); } };
        item.Collapsed += (_, e) => { if (e.OriginalSource == item) { collapsedKeys.Add(key); expandedKeys.Remove(key); } };
    }

    TreeViewItem SlotItem(TestSlot slot, string suffix = "")
    {
        var state = State(slot);
        string title = slot.Title + (slot.Retired ? " (izvan definicije)" : "") + suffix;
        return new TreeViewItem { Header = Header(state.Glyph, state.Brush, title, null, dim: slot.NotApplicable, maxWidth: TextWidth(SlotLevel(slot))), Tag = slot, ToolTip = $"{slot.Path}\n{state.Text}" };
    }
    static int SlotLevel(TestSlot slot) => (slot.Unit == 0 ? 1 : 2) + slot.Group.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    /// <summary>Header text width that fits the tree column at the given depth (each level indents 19 px).</summary>
    static double TextWidth(int level) => Math.Max(90, 255 - 19 * level);

    /// <summary>Builds unit → phase → group → test nodes; shared by the main tree and the assignment dialog.</summary>
    void BuildSlotTree(ItemCollection root, IEnumerable<TestSlot> source, Func<TestSlot, TreeViewItem> makeSlot, Func<string, int, bool> expanded, Action<TreeViewItem, string, bool>? track = null)
    {
        var list = source.Where(s => !s.Deleted).ToList();
        var unitNumbers = plant!.Units.Select(u => u.Number).Union(list.Select(s => s.Unit)).Where(n => n > 0).Distinct().Order();
        foreach (int number in unitNumbers)
        {
            var unitSlots = list.Where(s => s.Unit == number).ToList();
            if (unitSlots.Count == 0) continue;
            var unit = plant.Units.FirstOrDefault(u => u.Number == number);
            string unitKey = $"U{number}";
            var unitPlc = plant.PlcForUnit(number);
            var unitItem = Node(unitKey, Header("▣", Ink, TreeName(unitKey, unit?.Summary ?? $"Agregat {number} (uklonjen)"), $"{Progress(unitSlots)} · {PlcBadge(unitPlc)}", bold: true, maxWidth: TextWidth(0) - 90), 0);
            unitItem.ToolTip = PlcTooltip(unitPlc);
            root.Add(unitItem);
            // Generated phases, then the unit's own recordings; user sub-groups appear even while empty.
            foreach (string phase in TestPhases.All.Append(TestPhases.User))
            {
                var phaseSlots = unitSlots.Where(s => s.Phase == phase).ToList();
                var phaseFolders = (unit?.Folders ?? []).Where(f => f.StartsWith(phase + "/")).Select(f => f[(phase.Length + 1)..]).ToList();
                if (phaseSlots.Count == 0 && phaseFolders.Count == 0) continue;
                string phaseKey = $"{unitKey}/{phase}";
                var phaseItem = Node(phaseKey, Header("", Ink, TreeName(phaseKey, phase), phase == TestPhases.User ? phaseSlots.Count.ToString() : Progress(phaseSlots), bold: true), 1);
                unitItem.Items.Add(phaseItem);
                var groups = new Dictionary<string, TreeViewItem>();
                TreeViewItem GroupNode(string groupPath)
                {
                    TreeViewItem parent = phaseItem; string path = "";
                    foreach (string part in groupPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        path += "/" + part;
                        if (!groups.TryGetValue(path, out var group))
                        {
                            string prefix = path[1..];
                            var members = phaseSlots.Where(s => s.Group == prefix || s.Group.StartsWith(prefix + "/"));
                            int level = 1 + path.Count(c => c == '/');
                            group = Node(phaseKey + path, Header("", Ink, TreeName(phaseKey + path, part), Progress(members), maxWidth: TextWidth(level) - 30), level);
                            groups[path] = group; parent.Items.Add(group);
                        }
                        parent = group;
                    }
                    return parent;
                }
                foreach (var slot in phaseSlots.OrderBy(s => s.Retired)) GroupNode(slot.Group).Items.Add(makeSlot(slot));
                foreach (var folder in phaseFolders.Order()) GroupNode(folder);
            }
        }

        // User-built groups: their own sub-groups (kept even while empty) and recordings.
        foreach (var custom in plant.CustomGroups)
        {
            string key = "G/" + custom.Id;
            var groupSlots = list.Where(s => s.GroupId == custom.Id).ToList();
            var groupPlc = PlcById(custom.PlcId);
            string badge = groupPlc == null ? "bez PLC-a" : PlcBadge(groupPlc);
            var groupItem = Node(key, Header("▤", Ink, TreeName(key, custom.Name), badge, bold: true, maxWidth: TextWidth(0) - 60), 0);
            root.Add(groupItem);
            var folders = new Dictionary<string, TreeViewItem>();
            TreeViewItem Folder(string path)
            {
                if (path.Length == 0) return groupItem;
                if (folders.TryGetValue(path, out var existing)) return existing;
                int cut = path.LastIndexOf('/');
                var parent = Folder(cut < 0 ? "" : path[..cut]);
                var folder = Node($"{key}/{path}", Header("", Ink, TreeName($"{key}/{path}", path[(cut + 1)..]), null), 1 + path.Count(c => c == '/'));
                parent.Items.Add(folder); folders[path] = folder; return folder;
            }
            foreach (var path in custom.Folders.Concat(groupSlots.Select(s => s.Group)).Where(p => p.Length > 0).Distinct().Order()) Folder(path);
            foreach (var slot in groupSlots) Folder(slot.Group).Items.Add(makeSlot(slot));
        }

        foreach (var (phase, glyph) in new[] { (TestPhases.Common, "⧉"), (TestPhases.User, "★") })
        {
            var phaseSlots = list.Where(s => s.Unit == 0 && s.Phase == phase && string.IsNullOrEmpty(s.GroupId)).ToList();
            if (phaseSlots.Count == 0) continue;
            string key = "P0/" + phase;
            var phaseItem = Node(key, Header(glyph, Ink, TreeName(key, phase), phase == TestPhases.User ? phaseSlots.Count.ToString() : Progress(phaseSlots), bold: true), 0);
            root.Add(phaseItem);
            var phaseGroups = new Dictionary<string, TreeViewItem>();
            foreach (var slot in phaseSlots)
            {
                TreeViewItem parent = phaseItem; string path = "";
                foreach (string part in slot.Group.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    path += "/" + part;
                    if (!phaseGroups.TryGetValue(path, out var group)) { group = Node(key + path, Header("", Ink, TreeName(key + path, part), Progress(phaseSlots.Where(s => ("/" + s.Group + "/").StartsWith(path + "/")))), path.Count(c => c == '/')); phaseGroups[path] = group; parent.Items.Add(group); }
                    parent = group;
                }
                parent.Items.Add(makeSlot(slot));
            }
        }

        TreeViewItem Node(string key, object header, int level)
        {
            var item = new TreeViewItem { Header = header, IsExpanded = expanded(key, level), Tag = key };
            track?.Invoke(item, key, false); return item;
        }
    }

    /// <summary>Original names of the tree nodes, by key (for renaming back).</summary>
    readonly Dictionary<string, string> treeDefaults = [];
    /// <summary>Name of a tree node: the user's name if renamed, otherwise the generated one.</summary>
    string TreeName(string key, string original)
    {
        treeDefaults[key] = original;
        return plant?.TreeNames.TryGetValue(key, out var name) == true ? name : original;
    }

    static FrameworkElement Header(string glyph, Brush brush, string text, string? badge, bool bold = false, bool dim = false, double maxWidth = 230)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        if (glyph.Length > 0) panel.Children.Add(new TextBlock { Text = glyph, Foreground = brush, Width = 16, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = dim ? Muted : Ink, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = maxWidth });
        if (badge != null) panel.Children.Add(new TextBlock { Text = "  " + badge, Foreground = Muted });
        return panel;
    }

    void PlantTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (buildingTree) return;
        switch ((PlantTree.SelectedItem as TreeViewItem)?.Tag)
        {
            case TestSlot slot:
                selectedSlotId = slot.Id;
                var recordings = RecordingsOf(slot);
                if (recordings.Count > 0 && activeDocument?.SlotId != slot.Id) SwitchDocument(recordings[^1]);
                break;
            case OpenTrace doc:
                selectedSlotId = SlotById(doc.SlotId)?.Id;
                if (doc != activeDocument) SwitchDocument(doc);
                break;
        }
        ShowSelection();
    }

    void SwitchDocument(OpenTrace next)
    {
        try { Sync(); LearnNames(); RememberState(); CaptureDocument(); ActivateDocument(next); Status("Aktivna snimka: " + next.Title); }
        catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Promjena snimke"); ScheduleTreeRefresh(); }
    }

    void ShowSelection()
    {
        var tag = (PlantTree.SelectedItem as TreeViewItem)?.Tag;
        if (tag is OpenTrace doc && SlotById(doc.SlotId) == null) ShowUnassigned(doc);
        else ShowSlot(SlotById(selectedSlotId));
    }

    void ShowUnassigned(OpenTrace doc)
    {
        SlotPanel.Children.Clear();
        SlotPanel.Children.Add(new TextBlock { Text = doc.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        SlotPanel.Children.Add(new TextBlock { Text = "Snimka nije pridružena nijednom testu.", Foreground = Warn, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap });
        Action(SlotPanel, "Pridruži testu…", (_, _) => Guard(AssignActiveDocument), true);
        Action(SlotPanel, "Ukloni snimku iz projekta", RemoveDocument_Click);
    }

    void ShowSlot(TestSlot? slot)
    {
        SlotPanel.Children.Clear();
        if (slot == null)
        {
            SlotPanel.Children.Add(new TextBlock { Text = "Odaberi test u stablu. Postavke projekta (postrojenje, PLC-ovi, katalog testova, korisnička snimanja) su u izborniku Projekt ▾ gore.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            SlotPanel.Children.Add(new TextBlock { Text = plantConfig == null ? "Plant Configurator nije povezan." : $"Plant Configurator: {System.IO.Path.GetFileName(plantConfig.SourceFile)} · {plantConfig.Plcs.Count} PLC baza · učitano {plantConfig.LoadedAt:dd.MM.yyyy HH:mm}", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var state = State(slot);
        SlotPanel.Children.Add(new TextBlock { Text = slot.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        SlotPanel.Children.Add(new TextBlock { Text = slot.Path, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        SlotPanel.Children.Add(new TextBlock { Text = $"{state.Glyph}  {state.Text}", Foreground = state.Brush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        if (slot.Retired) SlotPanel.Children.Add(new TextBlock { Text = "Ovaj test više nije u definiciji postrojenja. Zadržan je jer sadrži snimke ili unose.", Foreground = Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

        var flags = new WrapPanel();
        flags.Children.Add(Flag("Pregledano", slot.Reviewed, v => slot.Reviewed = v));
        flags.Children.Add(Flag("Nije primjenjivo", slot.NotApplicable, v => slot.NotApplicable = v));
        SlotPanel.Children.Add(flags);

        if (slot.HasTimes)
        {
            var times = new Grid(); times.ColumnDefinitions.Add(new()); times.ColumnDefinitions.Add(new() { Width = new GridLength(10) }); times.ColumnDefinitions.Add(new());
            var open = Seconds("Vrijeme otvaranja [s]", slot.OpeningTime, v => slot.OpeningTime = v);
            var close = Seconds("Vrijeme zatvaranja [s]", slot.ClosingTime, v => slot.ClosingTime = v); Grid.SetColumn(close, 2);
            times.Children.Add(open); times.Children.Add(close); SlotPanel.Children.Add(times);
        }

        var load = new WrapPanel();
        Action(load, "Učitaj CSV…", async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Trace CSV / TSV|*.csv;*.tsv|Sve datoteke|*.*", Multiselect = true, Title = slot.Title };
            if (dialog.ShowDialog(this) == true) await AddRecordings(dialog.FileNames, slot.Id);
        }, true);
        Action(load, "●  Snimaj s PLC-a…", (_, _) => Guard(() => OpenRecorder(slot)), true);
        SlotPanel.Children.Add(load);
        var plc = PlcFor(slot);
        if (slot.IsUser)
        {
            var plcRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var label = new TextBlock { Text = "PLC: ", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(label, Dock.Left); plcRow.Children.Add(label);
            var picker = new ComboBox { ItemsSource = plant!.Plcs, DisplayMemberPath = nameof(PlcConnection.Name), SelectedItem = plc };
            picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is PlcConnection chosen) { slot.PlcId = chosen.Id; ShowSlot(slot); } };
            plcRow.Children.Add(picker); SlotPanel.Children.Add(plcRow);
            var manage = new WrapPanel();
            Action(manage, "Preimenuj…", (_, _) => Guard(() => RenameSlot(slot)));
            Action(manage, "Obriši snimanje…", (_, _) => Guard(() => DeleteSlot(slot)));
            SlotPanel.Children.Add(manage);
        }
        if (!slot.IsUser && slot.Unit > 0) Action(SlotPanel, "PLC i baza agregata…", (_, _) => Guard(() => EditUnitPlc(slot.Unit)));
        if (GroupOf(slot) is CustomGroup ownGroup) Action(SlotPanel, $"Grupa {ownGroup.Name}: PLC i baza…", (_, _) => Guard(() => EditGroup(ownGroup)));
        if (!slot.IsUser) SlotPanel.Children.Add(new TextBlock { Text = plc == null ? "Agregat nema dodijeljen PLC (PLC-ovi i Plant Configurator…)." : $"PLC: {plc.Name}" + (string.IsNullOrWhiteSpace(plc.Ip) ? " · IP nije upisan" : $" · {plc.Ip}") + (plantConfig?.Get(plc.CatalogId) is PlcCatalog linked ? $" · baza {linked.Name}" : ""), Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var channels = ChannelsOf(slot);
        SlotPanel.Children.Add(new TextBlock { Text = $"Mjerenja za snimanje: {channels.Count}" + (slot.Channels != null && !slot.IsUser ? " (uređeno)" : ""), Foreground = Brushes.SlateGray, Margin = new Thickness(0, 6, 0, 2) });
        if (!string.IsNullOrWhiteSpace(slot.Note)) SlotPanel.Children.Add(new TextBlock { Text = "✎ " + slot.Note.Split('\n')[0], Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = slot.Note, Margin = new Thickness(0, 0, 0, 2) });
        var more = new WrapPanel();
        Action(more, "Upute i mjerenja…", (_, _) => Guard(() => ShowTestDetails(slot)));
        Action(more, "Uredi mjerenja…", (_, _) => Guard(() => EditChannels(slot)));
        Action(more, "Napomena…", (_, _) => Guard(() => EditNote(slot)));
        if (!slot.IsUser) Action(more, "Preimenuj test…", (_, _) => Guard(() => RenameSlot(slot)));
        if (!slot.IsUser) Action(more, "Obriši snimanje…", (_, _) => Guard(() => DeleteSlot(slot)));
        SlotPanel.Children.Add(more);
        if (activeDocument?.SlotId == slot.Id)
        {
            var row = new WrapPanel();
            Action(row, "Premjesti snimku…", (_, _) => Guard(AssignActiveDocument));
            Action(row, "Ukloni snimku", RemoveDocument_Click);
            SlotPanel.Children.Add(row);
            var statistics = new CheckBox { Content = "Statistika ispod grafa u izvještaju", IsChecked = project.Options.ReportStatistics, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Word izvještaj: min, max, srednja vrijednost, početak i kraj svakog signala ispod grafa ove snimke." };
            statistics.Click += (_, _) => { project.Options.ReportStatistics = statistics.IsChecked == true; RebindOptions(); Commit(); };
            SlotPanel.Children.Add(statistics);
        }
    }

    CheckBox Flag(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = value };
        check.Click += (_, _) => { set(check.IsChecked == true); ScheduleTreeRefresh(); };
        return check;
    }

    FrameworkElement Seconds(string label, double? value, Action<double?> set)
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Text = value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "" };
        box.LostFocus += (_, _) =>
        {
            string raw = box.Text.Trim();
            if (raw.Length == 0) { set(null); box.ClearValue(Control.BorderBrushProperty); ScheduleTreeRefresh(); return; }
            if ((double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) && double.IsFinite(v) && v >= 0)
            { set(v); box.ClearValue(Control.BorderBrushProperty); ScheduleTreeRefresh(); }
            else { box.BorderBrush = Brushes.Firebrick; Status("Vrijeme unesi kao pozitivan broj sekundi."); }
        };
        panel.Children.Add(box); return panel;
    }

    void AssignActiveDocument()
    {
        if (activeDocument == null) return;
        Sync(); LearnNames(); RememberState(); CaptureDocument();
        var choice = AskSlot(project, null, 0, 0, activeDocument.SlotId);
        if (choice.Skip) return;
        activeDocument.SlotId = choice.SlotId; selectedSlotId = choice.SlotId;
        Render(); RefreshTree();
        Status(choice.SlotId == null ? "Snimka više nije pridružena testu." : "Snimka pridružena: " + SlotById(choice.SlotId)!.Path);
    }

    /// <summary>Asks which test a recording belongs to. Skip = do not load (or do not change) the file.</summary>
    (bool Skip, string? SlotId) AskSlot(StudioProject recording, string? path, int index, int total, string? current = null)
    {
        var sources = recording.Data.Signals.Select(s => s.Source).ToList();
        var layout = new DockPanel { Margin = new Thickness(16) };
        double duration = recording.Data.Time.Length > 0 ? recording.Data.Time[^1] : 0;
        var info = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        info.Children.Add(new TextBlock { Text = (total > 1 ? $"Datoteka {index} od {total}: " : "Datoteka: ") + (path != null ? System.IO.Path.GetFileName(path) : recording.Data.FileName), FontWeight = FontWeights.SemiBold, FontSize = 15 });
        info.Children.Add(new TextBlock { Text = $"{recording.Data.Signals.Count} signala · {recording.Data.Time.Length:N0} uzoraka · {duration:0.###} s · odaberi test kojem snimka pripada.", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 0) });
        DockPanel.SetDock(info, Dock.Top); layout.Children.Add(info);
        var filter = new TextBox { ToolTip = "Filtriraj testove po nazivu, agregatu ili fazi" };
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var filterLabel = new TextBlock { Text = "Traži: ", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(filterLabel, Dock.Left);
        filterRow.Children.Add(filterLabel); filterRow.Children.Add(filter);
        DockPanel.SetDock(filterRow, Dock.Top); layout.Children.Add(filterRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1.1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); body.ColumnDefinitions.Add(new());
        var tree = new TreeView(); body.Children.Add(tree);
        var details = new StackPanel(); var detailsScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(detailsScroll, 2); body.Children.Add(detailsScroll);
        layout.Children.Add(body);

        (bool Skip, string? SlotId) result = (true, null);
        var dialog = new Window { Owner = this, Title = "Pridruži snimku testu", Content = layout, Width = Dialogs.Width(1200), Height = Dialogs.Height(820), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        TestSlot? chosen = null;
        var assign = new Button { Content = "Pridruži", Style = (Style)FindResource("Primary"), IsEnabled = false };
        Action(buttons, path != null ? "Ne učitavaj ovu datoteku" : "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Bez pridruživanja", (_, _) => { result = (false, null); dialog.Close(); });
        assign.Click += (_, _) => { if (chosen != null) { result = (false, chosen.Id); dialog.Close(); } };
        buttons.Children.Add(assign);

        double Score(TestSlot slot)
        {
            var tags = ChannelsOf(slot).Select(c => Display(c.Tag, c.Plc)).ToList(); if (tags.Count == 0) return 0;
            return TagMatcher.Check(tags, sources).Count(r => r.Found) / (double)tags.Count;
        }
        var preferred = SlotById(current) ?? SlotById(selectedSlotId)
            ?? slots.Where(s => !s.Retired && !s.Deleted).Select(s => (Slot: s, Score: Score(s))).Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => RecordingsOf(x.Slot).Count > 0).Select(x => x.Slot).FirstOrDefault();

        void ShowDetails(TestSlot? slot)
        {
            chosen = slot; assign.IsEnabled = slot != null; details.Children.Clear();
            if (slot == null) { details.Children.Add(new TextBlock { Text = "Odaberi test u stablu lijevo.", Foreground = Brushes.SlateGray }); return; }
            details.Children.Add(new TextBlock { Text = slot.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            details.Children.Add(new TextBlock { Text = slot.Path, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
            int existing = RecordingsOf(slot).Count(d => d != activeDocument || path != null);
            if (existing > 0) details.Children.Add(new TextBlock { Text = $"Test već ima {existing} snimku/e; nova se dodaje uz njih.", Foreground = Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var tags = ChannelsOf(slot).Select(c => Display(c.Tag, c.Plc)).Distinct().ToList();
            if (tags.Count > 0)
            {
                var results = TagMatcher.Check(tags, sources);
                details.Children.Add(new TextBlock { Text = $"Tagovi u datoteci: {results.Count(r => r.Found)} od {results.Count}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var (tag, found) in results)
                    details.Children.Add(new TextBlock { Text = (found ? "✓  " : "✗  ") + tag, Foreground = found ? Done : Brushes.Firebrick, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            }
            else details.Children.Add(new TextBlock { Text = "Za ovaj test još nisu definirani tagovi.", Foreground = Brushes.SlateGray });
            details.Children.Add(new TextBlock { Text = "Kako izvesti test", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            details.Children.Add(new TextBlock { Text = catalog.HelpFor(slot), TextWrapping = TextWrapping.Wrap, Foreground = Ink });
        }
        void Fill()
        {
            string query = filter.Text.Trim();
            tree.Items.Clear(); TreeViewItem? select = null;
            var visible = slots.Where(s => !s.Deleted).Where(s => query.Length == 0 || s.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            BuildSlotTree(tree.Items, visible, slot =>
            {
                var item = SlotItem(slot, RecordingsOf(slot).Count > 0 ? $" ({RecordingsOf(slot).Count})" : "");
                if (slot == preferred) select = item;
                return item;
            }, (key, level) => query.Length > 0 || level <= 1);
            if (select != null)
            {
                for (var parent = select.Parent as TreeViewItem; parent != null; parent = parent.Parent as TreeViewItem) parent.IsExpanded = true;
                select.IsSelected = true; select.BringIntoView();
            }
            ShowDetails(select?.Tag as TestSlot);
        }
        tree.SelectedItemChanged += (_, _) => ShowDetails((tree.SelectedItem as TreeViewItem)?.Tag as TestSlot);
        tree.MouseDoubleClick += (_, _) => { if (chosen != null && (tree.SelectedItem as TreeViewItem)?.Tag is TestSlot) { result = (false, chosen.Id); dialog.Close(); } };
        filter.TextChanged += (_, _) => Fill();
        Fill();
        dialog.ShowDialog();
        return result;
    }

    void EditPlant_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        bool first = plant == null;
        var linkedBefore = plantConfig;
        var (result, definition) = RunPlantWizard(plant, first ? SuggestedPlant() : null, askConfig: first && plantConfig == null);
        if (result != PlantWizard.Result.Plant || definition == null) return;
        definition.Plcs = plant?.Plcs ?? [];
        plant = definition;
        PlantPlanner.LinkPlcs(plant, plantConfig);
        if (first) catalog = TestCatalog.LoadGlobal();
        slots = PlantPlanner.Merge(slots, PlantPlanner.Generate(plant), id => documents.Any(d => d.SlotId == id));
        if (plantConfig != null && plantConfig != linkedBefore) LinkPlantConfig(plantConfig.SourceFile);
        ApplyNominalRangesEverywhere();
        RefreshDocumentPicker(); RefreshTree();
        Status($"Definicija postrojenja spremljena · {slots.Count(s => !s.Retired && !s.Deleted)} testova u stablu. Spremi projekt za trajnu pohranu.");
    });

    void EditCatalog(string? focusKind)
    {
        var working = ProjectStore.Clone(catalog).WithDefaults();
        var layout = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock { Text = "Upute i tagovi vrijede za sve testove iste vrste. U tagovima {n} zamjenjuje broj ventila / razinu snage, a {u} broj agregata. Adresa u uglatim zagradama ([%DB…]) iz TIA tracea se zanemaruje.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        var list = new ListBox { ItemsSource = working.Kinds, DisplayMemberPath = nameof(TestKind.Title), Width = 300, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(list, Dock.Left); layout.Children.Add(list);
        var form = new Grid(); form.RowDefinitions.Add(new() { Height = GridLength.Auto }); form.RowDefinitions.Add(new() { Height = new GridLength(1.6, GridUnitType.Star) }); form.RowDefinitions.Add(new() { Height = GridLength.Auto }); form.RowDefinitions.Add(new());
        var title = new TextBox(); var help = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        var tags = new TextBox { AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        var head = new StackPanel(); head.Children.Add(new TextBlock { Text = "Naziv vrste testa" }); head.Children.Add(title); head.Children.Add(new TextBlock { Text = "Kako izvesti test (upute)" });
        Grid.SetRow(help, 1); var tagLabel = new TextBlock { Text = "Tagovi za snimanje (jedan po retku)", Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(tagLabel, 2); Grid.SetRow(tags, 3);
        form.Children.Add(head); form.Children.Add(help); form.Children.Add(tagLabel); form.Children.Add(tags);
        layout.Children.Add(form);
        TestKind? current = null;
        void Store()
        {
            if (current == null) return;
            current.Title = string.IsNullOrWhiteSpace(title.Text) ? current.Title : title.Text.Trim(); current.Help = help.Text;
            current.Signals = tags.Text.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        void Load(TestKind? kind)
        {
            current = kind; form.IsEnabled = kind != null;
            title.Text = kind?.Title ?? ""; help.Text = kind?.Help ?? ""; tags.Text = kind == null ? "" : string.Join(Environment.NewLine, kind.Signals);
        }
        list.SelectionChanged += (_, _) => { Store(); Load(list.SelectedItem as TestKind); };
        list.SelectedItem = working.Find(focusKind ?? "") ?? working.Kinds.FirstOrDefault();

        var dialog = new Window { Owner = this, Title = "Katalog testova · upute i tagovi", Content = layout, Width = Dialogs.Width(1260), Height = Dialogs.Height(860), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var asDefault = new CheckBox { Content = "Spremi i kao zadani katalog za nove projekte", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        Action(left, "Uvezi katalog…", (_, _) => Guard(() =>
        {
            var open = new OpenFileDialog { Filter = "Katalog testova|*.json" }; if (open.ShowDialog(dialog) != true) return;
            var imported = ProjectStore.Deserialize<TestCatalog>(File.ReadAllText(open.FileName)).WithDefaults();
            working.Kinds = imported.Kinds; current = null; list.ItemsSource = working.Kinds; list.SelectedIndex = 0;
        }));
        Action(left, "Izvezi katalog…", (_, _) => Guard(() =>
        {
            Store(); var save = new SaveFileDialog { Filter = "Katalog testova|*.json", FileName = "test-catalog.json" };
            if (save.ShowDialog(dialog) == true) ProjectStore.Save(save.FileName, working);
        }));
        left.Children.Add(asDefault);
        Action(right, "Odustani", (_, _) => dialog.Close());
        Action(right, plant == null ? "Spremi" : "Spremi u projekt", (_, _) => Guard(() =>
        {
            Store(); catalog = working;
            if (asDefault.IsChecked == true) catalog.SaveGlobal();
            dialog.Close(); ShowSelection();
            Status("Katalog testova spremljen" + (asDefault.IsChecked == true ? " u projekt i kao zadani katalog." : " u projekt."));
        }), true);
        bottom.Children.Add(right); bottom.Children.Add(left);
        dialog.ShowDialog();
    }

    /// <summary>Default PLC of a test: the unit's PLC, the chosen PLC of a user recording, the first unit's PLC for common tests.</summary>
    PlcConnection? PlcFor(TestSlot? slot) => slot == null || plant == null ? null
        : slot.IsUser ? PlcById(slot.PlcId) ?? PlcById(GroupOf(slot)?.PlcId) ?? plant.Plcs.FirstOrDefault()
        : slot.IsCommon ? plant.Units.Select(u => plant.PlcForUnit(u.Number)).FirstOrDefault(p => p != null) ?? plant.Plcs.FirstOrDefault()
        : plant.PlcForUnit(slot.Unit);
    PlcConnection? PlcById(string? id) => string.IsNullOrEmpty(id) ? null : plant?.Plcs.FirstOrDefault(p => p.Id == id);
    /// <summary>Plant Configurator database of a PLC; without an explicit link, the only database (if there is just one).</summary>
    PlcCatalog? CatalogOf(PlcConnection? plc) => plantConfig?.Get(plc?.CatalogId) ?? (plantConfig?.Plcs.Count == 1 && plc != null ? plantConfig.Plcs[0] : null);
    PlcCatalog? CatalogFor(TestSlot? slot) => CatalogOf(PlcFor(slot));

    /// <summary>Catalogue default measurements of a test; common tests take the per-unit list for every unit's PLC.</summary>
    List<SlotChannel> DefaultChannels(TestSlot slot)
    {
        if (slot.IsUser || plant == null) return [];
        if (slot.IsCommon)
            return plant.Units.SelectMany(u => catalog.ExpectedTags(slot, u.Number, u).Select(t => new SlotChannel(t, plant.PlcForUnit(u.Number)?.Id ?? ""))).ToList();
        return catalog.ExpectedTags(slot, null, plant.Units.FirstOrDefault(u => u.Number == slot.Unit)).Select(t => new SlotChannel(t)).ToList();
    }
    /// <summary>The measurements a test records (edited list or catalogue default) with the PLC each one is read from.</summary>
    List<(string Tag, PlcConnection? Plc)> ChannelsOf(TestSlot slot) =>
        (slot.Channels ?? DefaultChannels(slot)).Select(c => (c.Tag, PlcById(c.PlcId) ?? PlcFor(slot))).ToList();
    /// <summary>Stores an edited list; a list equal to the catalogue default is stored as "default" so catalogue changes still apply.</summary>
    void SetChannels(TestSlot slot, IEnumerable<(string Tag, PlcConnection? Plc)> channels)
    {
        var defaultPlc = PlcFor(slot);
        var list = channels.Select(c => new SlotChannel(TagMatcher.Normalize(c.Tag), slot.IsCommon || c.Plc == null || c.Plc != defaultPlc ? c.Plc?.Id ?? "" : "")).ToList();
        var defaults = ChannelsOf(new TestSlot { Id = slot.Id, Unit = slot.Unit, Kind = slot.Kind, Index = slot.Index, PlcId = slot.PlcId })
            .Select(c => (Tag: Display(c.Tag, c.Plc), c.Plc)).ToList();
        bool same = !slot.IsUser && defaults.Count == list.Count && defaults.Zip(list).All(x => TagMatcher.Matches(x.First.Tag, Display(x.Second.Tag, PlcById(x.Second.PlcId) ?? defaultPlc)) && x.First.Plc == (PlcById(x.Second.PlcId) ?? defaultPlc));
        slot.Channels = same ? null : list;
    }
    /// <summary>Real tag of an ID reference ("DI#23") when the PLC's database knows it.</summary>
    string Display(string tag, PlcConnection? plc) => CatalogOf(plc)?.Find(tag)?.Tag ?? TagMatcher.Normalize(tag);
    static (string? Plc, string Tag) SplitSource(string source)
    {
        int index = source.IndexOf(": ", StringComparison.Ordinal);
        return index > 0 && !source[..index].Contains('.') ? (source[..index], source[(index + 2)..]) : (null, source);
    }

    /// <summary>Address of a tag: explicit in the test catalogue, from the PLC's Plant Configurator database, or learned from a TIA trace / entered.</summary>
    (S7Address? Address, string Origin) ResolveAddress(string tag, PlcCatalog? source)
    {
        try
        {
            if (S7Address.FromTraceHeader(tag) is string embedded) return (S7Address.Parse(embedded), "katalog testova");
            if (source?.Find(tag) is PlantMeasurement m) return (source.AddressOf(m), "Plant Configurator");
            if (PlantLayout.ParseIdTag(tag) is var (kind, id)) return (PlantLayout.Address(kind, id), $"{kind} ID {id}");
            if (PlantLayout.FindRegulator(tag) is PlantMeasurement regulator) return (PlantLayout.Address("REG", regulator.Id), "REG_INTF");
            if (tagAddresses.TryGetValue(TagMatcher.Normalize(tag), out var known)) return (known.Parse(), "zapamćena");
        }
        catch (InvalidDataException) { }
        return (null, "nema adrese");
    }

    /// <summary>Remembers the absolute addresses TIA writes into trace CSV column headers.</summary>
    void LearnAddresses(StudioProject recording)
    {
        foreach (var signal in recording.Data.Signals)
        {
            if (S7Address.FromTraceHeader(signal.Source) is not string text || SplitSource(signal.Source).Plc != null) continue;
            try { var address = S7Address.Parse(text); tagAddresses.TryAdd(TagMatcher.Normalize(signal.Source), new TagAddress { Address = address.ToString(), Type = address.Type }); }
            catch (InvalidDataException) { /* unknown address format: keep the recording, skip the address */ }
        }
    }

    /// <summary>MEAS tags take name and unit as configured in the PLC's Plant Configurator database (unless overridden for this CSV).</summary>
    void ApplyMeasurementNames(StudioProject recording, string? slotId)
    {
        var slot = SlotById(slotId);
        foreach (var signal in recording.Data.Signals.Where(s => !s.NameOverride))
        {
            var (prefix, tag) = SplitSource(signal.Source);
            var plc = prefix != null ? plant?.Plcs.FirstOrDefault(p => p.Name == prefix) : PlcFor(slot);
            if (prefix == null && slot == null && plantConfig?.Plcs.Count != 1) continue;
            if (CatalogOf(plc ?? PlcFor(slot)) is not PlcCatalog source || source.Find(tag) is not PlantMeasurement m) continue;
            string label = plc?.UnitNumbers() is [int unit] ? $"Agr. {unit}" : plc?.Name ?? "";
            if (m.Name.Length > 0) signal.Name = prefix != null && label.Length > 0 ? $"{label} · {m.Name}" : m.Name;
            signal.Unit = m.IsDigital ? "0/1" : m.Unit;
        }
    }

    /// <summary>Links (or refreshes) the Plant Configurator project; names, units and addresses follow its PLC databases.</summary>
    void LinkPlantConfig(string path)
    {
        var link = PlantConfigLink.Load(path);
        plantConfig = link;
        if (plant != null) PlantPlanner.LinkPlcs(plant, link);
        foreach (var doc in documents) ApplyMeasurementNames(doc == activeDocument ? project : doc.Project, doc.SlotId);
        if (project.Data.Time.Length > 0) { Bind(); Render(); }
        RefreshTree();
        Status($"Plant Configurator povezan: {System.IO.Path.GetFileName(path)} · PLC baze: {string.Join(", ", link.Plcs.Select(p => $"{p.Name} ({p.Items.Count}, snimanje iz DB{p.ReadDb})"))}.");
    }

    void EditPlcs()
    {
        if (plant == null) throw new InvalidDataException("Prvo definiraj postrojenje (Novi projekt).");
        var working = new System.Collections.ObjectModel.ObservableCollection<PlcConnection>(ProjectStore.Clone(plant.Plcs));
        PlantConfigLink? link = plantConfig;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var source = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        void ShowSource() => source.Text = link == null ? "Plant Configurator nije povezan. Odaberi njegov projekt (.plantcfg) da bi nazivi, jedinice i adrese mjerenja došli iz PLC baza."
            : $"Plant Configurator: {link.SourceFile}\nPLC baze: {string.Join(", ", link.Plcs.Select(p => $"{p.Name} ({p.Items.Count} mjerenja, MEAS DB{p.Db} → snimanje iz DB{p.ReadDb})"))}";
        ShowSource();
        var top = new StackPanel(); top.Children.Add(source);
        var topButtons = new WrapPanel(); top.Children.Add(topButtons);
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var grid = new DataGrid { ItemsSource = working, AutoGenerateColumns = false, CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0) };
        var catalogColumn = new DataGridComboBoxColumn { Header = "PLC baza u Plant Configuratoru", SelectedValuePath = nameof(PlcCatalog.Id), DisplayMemberPath = nameof(PlcCatalog.Name), SelectedValueBinding = new Binding(nameof(PlcConnection.CatalogId)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ItemsSource = link?.Plcs };
        grid.Columns.Add(new DataGridTextColumn { Header = "Naziv PLC-a", Binding = new Binding(nameof(PlcConnection.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "IP adresa", Binding = new Binding(nameof(PlcConnection.Ip)), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Rack", Binding = BindingFor(nameof(PlcConnection.Rack)), Width = 55 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Slot", Binding = BindingFor(nameof(PlcConnection.Slot)), Width = 55 });
        grid.Columns.Add(catalogColumn);
        grid.Columns.Add(new DataGridTextColumn { Header = "Agregati (npr. 1 ili 1, 2)", Binding = new Binding(nameof(PlcConnection.Units)), Width = 170 });
        var hint = new TextBlock { Text = "Testovi agregata snimaju s PLC-a kojem je agregat dodijeljen. PLC bez agregata (Common, Intake) koristi se za korisnička snimanja. S7-1500: rack 0, slot 1.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons); layout.Children.Add(hint); layout.Children.Add(grid);
        var dialog = new Window { Owner = this, Title = "PLC-ovi i Plant Configurator", Content = layout, Width = Dialogs.Width(1160), Height = Dialogs.Height(660), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        Action(topButtons, link == null ? "Poveži projekt Plant Configuratora…" : "Promijeni / osvježi Plant Configurator…", (_, _) => Guard(() =>
        {
            var open = new OpenFileDialog { Filter = "Plant Configurator projekt|*.plantcfg;*.json|Sve datoteke|*.*", FileName = link?.SourceFile ?? "" };
            if (open.ShowDialog(dialog) != true) return;
            link = PlantConfigLink.Load(open.FileName);
            var temp = new PlantDefinition { Units = plant.Units, Plcs = working.ToList() }; PlantPlanner.LinkPlcs(temp, link);
            working.Clear(); foreach (var p in temp.Plcs) working.Add(p);
            catalogColumn.ItemsSource = link.Plcs; ShowSource(); grid.Items.Refresh();
        }));
        Action(topButtons, "Dodaj PLC", (_, _) => working.Add(new PlcConnection { Name = $"PLC {working.Count + 1}" }));
        Action(topButtons, "DB za snimanje mjerenja…", (_, _) => Guard(() =>
        {
            if (link == null) throw new InvalidDataException("Prvo poveži projekt Plant Configuratora.");
            foreach (var catalog in link.Plcs)
            {
                if (AskText($"{catalog.Name}: DB iz kojeg se čitaju mjerenja (konfiguracijski MEAS je DB{catalog.Db})", catalog.ReadDb.ToString()) is not string text) return;
                if (!int.TryParse(text, out int db) || db is < 1 or > 65535) throw new InvalidDataException("Broj DB-a je cijeli broj 1–65535.");
                catalog.ReadDb = db;
            }
            ShowSource();
        }));
        Action(topButtons, "Ukloni odabrani PLC", (_, _) => { if (grid.SelectedItem is PlcConnection p) working.Remove(p); });
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Spremi", (_, _) => Guard(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (HasErrors(grid)) throw new InvalidDataException("Ispravi crveno označena polja.");
            foreach (var unit in plant.Units)
                if (working.Count(p => p.UnitNumbers().Contains(unit.Number)) > 1) throw new InvalidDataException($"Agregat {unit.Number} je dodijeljen na više PLC-ova.");
            if (working.Any(p => string.IsNullOrWhiteSpace(p.Name))) throw new InvalidDataException("Svaki PLC treba naziv.");
            plant.Plcs = working.ToList();
            if (link != plantConfig) LinkPlantConfig(link!.SourceFile); else { PlantPlanner.LinkPlcs(plant, plantConfig); RefreshTree(); }
            dialog.Close();
        }), true);
        dialog.ShowDialog();
    }

    /// <summary>Answers <see cref="AskText"/> without a dialog (checks).</summary>
    internal Func<string, string, string?>? AnswerText;
    string? AskText(string title, string value)
    {
        if (AnswerText != null) return AnswerText(title, value);
        var box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 10) };
        var panel = new StackPanel { Margin = new Thickness(16) }; panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = title, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        string? result = null;
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "U redu", (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) { result = box.Text.Trim(); dialog.Close(); } }, true);
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        dialog.ShowDialog(); return result;
    }

    void NewUserRecording() => NewUserRecording(null, "");

    string RecordingFolder()
    {
        if (workspacePath != null)
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(workspacePath)!, System.IO.Path.GetFileNameWithoutExtension(workspacePath) + " - snimke");
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TIA Trace Studio", "Snimke");
    }

    void OpenRecorder(TestSlot slot)
    {
        if (recorders.FirstOrDefault(r => r.Tag as string == slot.Id) is RecorderWindow open) { open.Activate(); return; }
        if (plant == null) return;
        if (PlcFor(slot) == null) PlantPlanner.LinkPlcs(plant, plantConfig);
        var defaultPlc = PlcFor(slot) ?? throw new InvalidDataException("Odaberi PLC za ovo snimanje (PLC-ovi i Plant Configurator).");
        var rows = ChannelsOf(slot).Select(c =>
        {
            var plc = c.Plc ?? defaultPlc; var source = CatalogOf(plc);
            var (address, origin) = ResolveAddress(c.Tag, source);
            string tag = Display(c.Tag, plc);
            string name = source?.Find(c.Tag)?.Name ?? (nameLibrary.Names.TryGetValue(tag, out var known) ? known : PlantLayout.ParseIdTag(c.Tag) is var (k, id) ? $"{k} {id}" : "");
            return new ChannelRow { PlcId = plc.Id, Tag = tag, Name = name, Address = address?.ToString() ?? "", Type = address?.Type ?? "", Origin = origin };
        }).ToList();
        string slotId = slot.Id;
        var window = new RecorderWindow(this, new RecorderSetup(slot, plant.Plcs, defaultPlc, rows, CatalogOf, RecordingFolder(), CreatePlcReader,
            (tag, address) => tagAddresses[TagMatcher.Normalize(tag)] = address,
            used => { if (SlotById(slotId) is TestSlot s) { SetChannels(s, used.Select(c => (c.Tag, PlcById(c.PlcId)))); ShowSelection(); } },
            data => AddRecordedTrace(data, slotId))) { Tag = slot.Id };
        recorders.Add(window); window.Closed += (_, _) => recorders.Remove(window);
        window.Show();
    }

    /// <summary>Edits the measurements of a test: search the PLC databases of Plant Configurator, add or remove, or enter any tag / address.</summary>
    void EditChannels(TestSlot slot)
    {
        if (plant == null) return;
        if (PlcFor(slot) == null) PlantPlanner.LinkPlcs(plant, plantConfig);
        var defaultPlc = PlcFor(slot);
        var list = new System.Collections.ObjectModel.ObservableCollection<ChannelRow>();
        void Load(IEnumerable<(string Tag, PlcConnection? Plc)> channels)
        {
            list.Clear();
            foreach (var (tag, plc) in channels)
            {
                var source = CatalogOf(plc); var (address, origin) = ResolveAddress(tag, source);
                list.Add(new ChannelRow { PlcId = plc?.Id ?? "", Tag = Display(tag, plc), Name = source?.Find(tag)?.Name ?? "", Address = address?.ToString() ?? "", Origin = origin });
            }
        }
        Load(ChannelsOf(slot));

        var layout = new DockPanel { Margin = new Thickness(16) };
        var title = new TextBlock { Text = slot.Path, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1.25, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); body.ColumnDefinitions.Add(new());
        layout.Children.Add(body);

        var left = new DockPanel();
        var leftTitle = new TextBlock { Text = "Mjerenja ovog testa", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(leftTitle, Dock.Top); left.Children.Add(leftTitle);
        var leftButtons = new WrapPanel(); DockPanel.SetDock(leftButtons, Dock.Bottom); left.Children.Add(leftButtons);
        var grid = new DataGrid { ItemsSource = list, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "PLC", ItemsSource = plant.Plcs, DisplayMemberPath = nameof(PlcConnection.Name), SelectedValuePath = nameof(PlcConnection.Id), SelectedValueBinding = new Binding(nameof(ChannelRow.PlcId)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Tag", Binding = new Binding(nameof(ChannelRow.Tag)), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Naziv", Binding = new Binding(nameof(ChannelRow.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Adresa", Binding = new Binding(nameof(ChannelRow.Address)), IsReadOnly = true, Width = 120 });
        left.Children.Add(grid); body.Children.Add(left);
        Action(leftButtons, "Ukloni odabrana", (_, _) => { foreach (var row in grid.SelectedItems.Cast<ChannelRow>().ToList()) list.Remove(row); });
        Action(leftButtons, "Gore", (_, _) => { if (grid.SelectedItem is ChannelRow row && list.IndexOf(row) > 0) list.Move(list.IndexOf(row), list.IndexOf(row) - 1); });
        Action(leftButtons, "Dolje", (_, _) => { if (grid.SelectedItem is ChannelRow row && list.IndexOf(row) < list.Count - 1) list.Move(list.IndexOf(row), list.IndexOf(row) + 1); });
        if (!slot.IsUser) Action(leftButtons, "Vrati zadano iz kataloga", (_, _) => Load(DefaultChannels(slot).Select(c => (c.Tag, PlcById(c.PlcId) ?? defaultPlc))));

        var right = new DockPanel(); Grid.SetColumn(right, 2); body.Children.Add(right);
        var search = new StackPanel(); DockPanel.SetDock(search, Dock.Top); right.Children.Add(search);
        search.Children.Add(new TextBlock { Text = "Baza Plant Configuratora", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var plcPicker = new ComboBox { ItemsSource = plant.Plcs, DisplayMemberPath = nameof(PlcConnection.Name), SelectedItem = defaultPlc };
        var kindPicker = new ComboBox { ItemsSource = new[] { "Sve", "Mjerenja (MEAS)", "Digitalni ulazi (DI)", "Digitalni izlazi (DO)", "Regulator interface (REG_INTF)" }, SelectedIndex = 0 };
        var filter = new TextBox { ToolTip = "Pretraži po ID-u, sustavu, PLC nazivu ili nazivu" };
        var pickerRow = new Grid(); pickerRow.ColumnDefinitions.Add(new()); pickerRow.ColumnDefinitions.Add(new() { Width = new GridLength(8) }); pickerRow.ColumnDefinitions.Add(new());
        Grid.SetColumn(kindPicker, 2); pickerRow.Children.Add(plcPicker); pickerRow.Children.Add(kindPicker); search.Children.Add(pickerRow);
        search.Children.Add(new TextBlock { Text = "Traži" }); search.Children.Add(filter);
        var manual = new StackPanel(); DockPanel.SetDock(manual, Dock.Bottom); right.Children.Add(manual);
        var results = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(PlantMeasurement.Display) };
        right.Children.Add(results);
        var info = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        void Fill()
        {
            var source = CatalogOf(plcPicker.SelectedItem as PlcConnection);
            string q = filter.Text.Trim();
            string? kind = kindPicker.SelectedIndex switch { 1 => "MEAS", 2 => "DI", 3 => "DO", 4 => "REG", _ => null };
            var items = source?.AllItems.Where(m => (kind == null || m.Kind == kind)
                && q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => m.Display.Contains(w, StringComparison.CurrentCultureIgnoreCase) || m.Tag.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            results.ItemsSource = items;
            info.Text = source == null ? "PLC nije povezan s bazom Plant Configuratora (PLC-ovi i Plant Configurator). Mjerenja možeš dodati ručno ispod." : $"{items!.Count} od {source.AllItems.Count()} · dvoklik ili Dodaj ←";
        }
        void AddSelected()
        {
            if (plcPicker.SelectedItem is not PlcConnection plc || CatalogOf(plc) is not PlcCatalog source) return;
            foreach (var m in results.SelectedItems.Cast<PlantMeasurement>())
                if (!list.Any(r => r.PlcId == plc.Id && TagMatcher.Matches(r.Tag, m.Tag)))
                    list.Add(new ChannelRow { PlcId = plc.Id, Tag = m.Tag, Name = m.Name, Address = source.AddressOf(m).ToString(), Origin = "Plant Configurator" });
        }
        plcPicker.SelectionChanged += (_, _) => Fill(); kindPicker.SelectionChanged += (_, _) => Fill(); filter.TextChanged += (_, _) => Fill();
        results.MouseDoubleClick += (_, _) => AddSelected();
        var addRow = new WrapPanel(); Action(addRow, "←  Dodaj odabrana", (_, _) => AddSelected(), true); manual.Children.Add(addRow); manual.Children.Add(info);
        manual.Children.Add(new TextBlock { Text = "Bilo koja adresa (DB i offset)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        var adr = new Grid(); foreach (var w in new[] { 1.6, 0.1, 0.8, 0.1, 0.8, 0.1, 0.5, 0.1, 1.0 }) adr.ColumnDefinitions.Add(new() { Width = new GridLength(w, GridUnitType.Star) });
        TextBox Box(string label, int column, string text = "") { var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = text }; panel.Children.Add(box); Grid.SetColumn(panel, column); adr.Children.Add(panel); return box; }
        var nameBox = Box("Naziv / tag", 0); var dbBox = Box("DB", 2, "1003"); var offsetBox = Box("Offset", 4); var bitBox = Box("Bit", 6, "0");
        var typePanel = new StackPanel(); typePanel.Children.Add(new TextBlock { Text = "Tip" }); var typeBox = new ComboBox { ItemsSource = S7Address.Types, SelectedItem = "REAL" }; typePanel.Children.Add(typeBox); Grid.SetColumn(typePanel, 8); adr.Children.Add(typePanel);
        manual.Children.Add(adr);
        Action(manual, "←  Dodaj adresu", (_, _) => Guard(() =>
        {
            if (!int.TryParse(dbBox.Text, out int db) || !int.TryParse(offsetBox.Text, out int offset) || !int.TryParse(bitBox.Text, out int bit) || db < 1 || offset < 0 || bit is < 0 or > 7)
                throw new InvalidDataException("DB, offset i bit su cijeli brojevi (bit 0–7).");
            string type = typeBox.SelectedItem as string ?? "REAL";
            var address = new S7Address(S7Area.DataBlock, db, offset, type == "BOOL" ? bit : 0, type);
            string tag = string.IsNullOrWhiteSpace(nameBox.Text) ? address.ToString() : nameBox.Text.Trim();
            tagAddresses[TagMatcher.Normalize(tag)] = new TagAddress { Address = address.ToString(), Type = type };
            list.Add(new ChannelRow { PlcId = (plcPicker.SelectedItem as PlcConnection ?? defaultPlc)?.Id ?? "", Tag = tag, Name = nameBox.Text.Trim(), Address = address.ToString(), Origin = "ručno" });
        }));

        var dialog = new Window { Owner = this, Title = "Mjerenja za snimanje · " + slot.ChartTitle, Content = layout, Width = Dialogs.Width(1380), Height = Dialogs.Height(880), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var allUnits = new CheckBox { Content = "Primijeni isti popis na ovaj test u svim agregatima", VerticalAlignment = VerticalAlignment.Center, Visibility = slot.Unit > 0 ? Visibility.Visible : Visibility.Collapsed };
        var bottomButtons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(bottomButtons, Dock.Right);
        buttons.Children.Add(bottomButtons); buttons.Children.Add(allUnits);
        Action(bottomButtons, "Odustani", (_, _) => dialog.Close());
        Action(bottomButtons, "Spremi", (_, _) => Guard(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            var chosen = list.Where(r => !string.IsNullOrWhiteSpace(r.Tag)).Select(r => (r.Tag, Plc: PlcById(r.PlcId) ?? defaultPlc)).ToList();
            SetChannels(slot, chosen);
            if (allUnits.IsChecked == true)
                foreach (var other in slots.Where(s => s != slot && s.Unit > 0 && s.Kind == slot.Kind && s.Index == slot.Index))
                {
                    // Channels read from this unit's PLC move to the other unit's PLC; explicit other PLCs stay.
                    var otherPlc = PlcFor(other);
                    SetChannels(other, chosen.Select(c => (c.Tag, c.Plc == defaultPlc ? otherPlc : c.Plc)));
                }
            dialog.Close(); ShowSelection();
            Status($"Mjerenja testa spremljena ({chosen.Count})" + (allUnits.IsChecked == true ? " i primijenjena na isti test u svim agregatima." : "."));
        }), true);
        Fill();
        dialog.ShowDialog();
    }

    /// <summary>Adds a recording made in the app under its test, exactly like an imported CSV.</summary>
    internal void AddRecordedTrace(TraceData data, string slotId)
    {
        if (project.Data.Time.Length > 0) { Sync(); LearnNames(); RememberState(); CaptureDocument(); }
        var next = NewProject(data);
        string? target = SlotById(slotId)?.Id;
        nameLibrary.Apply(next); ApplyMeasurementNames(next, target); ApplyNominalRanges(next, target); PlotService.Validate(next);
        if (SlotById(target) is TestSlot slot) { next.Options.Title = slot.ChartTitle; next.Options.Subtitle = plant!.Name; }
        var doc = new OpenTrace { Project = next, SlotId = target };
        documents.Add(doc); ActivateDocument(doc);
        Status($"Snimka spremljena pod test: {SlotById(target)?.Path ?? "nepridružene snimke"} · {data.Time.Length:N0} uzoraka. Spremi projekt.");
    }

    /// <summary>Starting point for a new plant: name and unit count from the linked Plant Configurator project.</summary>
    PlantDefinition? SuggestedPlant()
    {
        if (plantConfig == null) return null;
        int units = Math.Max(1, plantConfig.Plcs.Select(p => PlantConfigLink.UnitNumber(p.Name) ?? 0).DefaultIfEmpty(0).Max());
        return new PlantDefinition { Name = plantConfig.ProjectName, Units = Enumerable.Range(1, Math.Min(units, 12)).Select(n => new UnitDefinition { Number = n }).ToList() };
    }

    /// <summary>Started from Plant Configurator: link its project and, without an open plant, start the plant definition.</summary>
    internal void OpenFromPlantConfigurator(string path)
    {
        try
        {
            if (plant == null && documents.Count == 0)
            {
                plantConfig = PlantConfigLink.Load(path);
                var (result, definition) = RunPlantWizard(null, SuggestedPlant(), askConfig: false);
                if (result == PlantWizard.Result.Plant && definition != null)
                {
                    var session = new TraceSession { Plant = definition, Slots = PlantPlanner.Generate(definition), Catalog = TestCatalog.LoadGlobal(), PlantConfig = plantConfig };
                    ReplaceWorkspace(session, null); savedWorkspaceState = "";
                }
            }
            LinkPlantConfig(plantConfig?.SourceFile ?? path);
        }
        catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Plant Configurator", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Unit whose rated values apply to a signal: the PLC prefix of multi-PLC recordings, otherwise the unit of the test.</summary>
    int? NominalUnit(Signal signal, string? slotId)
    {
        var (prefix, _) = SplitSource(signal.Source);
        if (prefix != null) return plant?.Plcs.FirstOrDefault(p => p.Name == prefix)?.UnitNumbers() is [int unit] ? unit : null;
        return SlotById(slotId) is { Unit: > 0 } slot ? slot.Unit : null;
    }

    /// <summary>
    /// Range from the project for a signal: a rated value entered for its unit (plant definition) wins, otherwise the
    /// measurement range from the Plant Configurator parameters (ParVal1Id / ParVal2Id) of its PLC.
    /// </summary>
    (double Min, double Max, string Source)? ProjectRange(Signal signal, string? slotId)
    {
        if (plant == null) return null;
        var (prefix, tag) = SplitSource(signal.Source);
        var unit = NominalUnit(signal, slotId) is int number ? plant.Units.FirstOrDefault(u => u.Number == number) : null;
        if (unit?.NominalFor(tag) is NominalRange range) return (range.Min!.Value, range.Max!.Value, $"Definicija postrojenja · {unit.Title}");
        var plc = prefix != null ? plant.Plcs.FirstOrDefault(p => p.Name == prefix) : PlcFor(SlotById(slotId));
        if (CatalogOf(plc)?.Find(tag) is PlantMeasurement m && m.HasRange) return (m.RangeLow!.Value, m.RangeHigh!.Value, $"Plant Configurator · {m.RangeSource}");
        return null;
    }

    /// <summary>Signals set to "Iz postavki projekta" take 0 % / 100 % from <see cref="ProjectRange"/>.</summary>
    void ApplyNominalRanges(StudioProject recording, string? slotId)
    {
        if (plant == null) return;
        foreach (var signal in recording.Data.Signals.Where(s => s.PercentFromProject))
            if (ProjectRange(signal, slotId) is var (min, max, _)) { signal.PercentMin = min; signal.PercentMax = max; }
    }
    /// <summary>Says where the selected signal's 0 % / 100 % come from.</summary>
    void UpdatePercentInfo()
    {
        if (SignalList.SelectedItem is not Signal signal) { percentInfo.Text = ""; return; }
        percentInfo.Foreground = Brushes.SlateGray;
        if (SignalDisplay.IsNativePercent(signal) || signal.PercentFromProject && ((signal.Unit ?? "").Trim() == "%" || SignalDisplay.IsTemperature(signal)))
        { percentInfo.Text = "Položaj (%), regulator interface (−100…100 %) ili temperatura (°C): prikazuje se izravno, raspon nije potreban."; return; }
        if (!signal.PercentFromProject) { percentInfo.Text = "Ručni raspon vrijedi samo za ovaj graf."; return; }
        if (ProjectRange(signal, activeDocument?.SlotId) is var (min, max, source))
            percentInfo.Text = $"Iz projekta: {min:G6} – {max:G6} {signal.Unit} ({source})".Replace("  ", " ");
        else
        {
            percentInfo.Text = "Ni u Plant Configuratoru ni u Definiciji postrojenja nema raspona za ovo mjerenje. Upiši ručno i spremi ga kao nazivnu vrijednost agregata.";
            percentInfo.Foreground = Warn;
        }
    }

    /// <summary>Stores the manual range of the selected signal as the rated value of its unit, for all its recordings.</summary>
    void SaveRangeToProject()
    {
        Sync();
        if (SignalList.SelectedItem is not Signal signal) return;
        if (signal.PercentMin is not double min || signal.PercentMax is not double max || !(min < max)) throw new InvalidDataException("Upiši min i max (min manji od max), pa ih spremi u projekt.");
        if (NominalUnit(signal, activeDocument?.SlotId) is not int number || plant?.Units.FirstOrDefault(u => u.Number == number) is not UnitDefinition unit)
            throw new InvalidDataException("Snimka nije pridružena agregatu; nazivna vrijednost vrijedi po agregatu.");
        string tag = SplitSource(signal.Source).Tag;
        var range = unit.NominalRanges.FirstOrDefault(r => TagMatcher.Matches(r.Tag, tag));
        if (range == null) { range = new NominalRange { Tag = TagMatcher.Normalize(tag), Name = signal.DisplayName, Unit = signal.Unit }; unit.NominalRanges.Add(range); }
        range.Min = min; range.Max = max;
        signal.PercentFromProject = true;
        ApplyNominalRangesEverywhere(); UpdatePercentInfo();
        Status($"Nazivna vrijednost spremljena za {unit.Title}: {signal.DisplayName} {min:G6} – {max:G6} {signal.Unit}. Vrijedi za sve snimke agregata. Spremi projekt.");
    }

    /// <summary>"Unit 1 · 10.25.4.11": the Plant Configurator database and IP of a PLC, for tree badges.</summary>
    string PlcBadge(PlcConnection? plc) => plc == null ? "bez PLC-a"
        : (plantConfig?.Get(plc.CatalogId) is PlcCatalog db ? db.Name : "bez baze") + (string.IsNullOrWhiteSpace(plc.Ip) ? "" : " · " + plc.Ip);
    string PlcTooltip(PlcConnection? plc) => plc == null ? "Nema dodijeljen PLC (desni klik → PLC i baza Plant Configuratora)."
        : $"PLC: {plc.Name}\nIP: {(string.IsNullOrWhiteSpace(plc.Ip) ? "nije upisan" : plc.Ip)} · rack {plc.Rack} · slot {plc.Slot}\n"
          + (plantConfig?.Get(plc.CatalogId) is PlcCatalog db ? $"PLC baza: {db.Name} ({db.Items.Count(i => i.Kind == "MEAS")} mjerenja)\nPlant Configurator: {plantConfig.SourceFile}" : "PLC baza: nije odabrana")
          + "\nDesni klik → PLC i baza Plant Configuratora";

    /// <summary>Which Plant Configurator file the project is linked to, shown under the plant name.</summary>
    void ShowPlantConfigInfo()
    {
        if (plantConfig == null)
        {
            PlantConfigInfo.Text = "⛓ Plant Configurator: nije povezan (klikni za povezivanje)";
            PlantConfigInfo.Foreground = Warn; PlantConfigInfo.ToolTip = "Projekt ▾ → PLC-ovi i Plant Configurator";
            PlantConfigRefresh.Visibility = Visibility.Collapsed;
            return;
        }
        PlantConfigInfo.Text = $"⛓ Plant Configurator: {System.IO.Path.GetFileName(plantConfig.SourceFile)} · učitano {plantConfig.LoadedAt:dd.MM.yyyy HH:mm}";
        PlantConfigInfo.Foreground = Accent;
        PlantConfigRefresh.Visibility = PlantConfigChanged() ? Visibility.Visible : Visibility.Collapsed;
        PlantConfigInfo.ToolTip = $"{plantConfig.SourceFile}\nProjekt: {plantConfig.ProjectName}\nPLC baze: {string.Join(", ", plantConfig.Plcs.Select(p => p.Name))}\nKlik: PLC-ovi i Plant Configurator (promjena ili osvježavanje)"
            + (File.Exists(plantConfig.SourceFile) ? "" : "\n⚠ Datoteka više ne postoji na toj putanji – koristi se kopija spremljena u projektu.");
    }
    /// <summary>Back from Plant Configurator: offer the refresh when the .plantcfg was saved meanwhile.</summary>
    void Window_Activated(object? sender, EventArgs e) { if (PlantConfigInfo != null) ShowPlantConfigInfo(); }

    void PlantConfigInfo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Guard(EditPlcs);

    void ApplyNominalRangesEverywhere()
    {
        foreach (var doc in documents) ApplyNominalRanges(doc == activeDocument ? project : doc.Project, doc.SlotId);
        if (project.Data.Time.Length > 0) { Bind(); Render(); }
    }

    /// <summary>Positions and regulator outputs are already in %, temperatures stay in °C: only the others need rated values.</summary>
    static bool NeedsNominal(PlantMeasurement m) => !m.IsDigital && m.Unit.Trim() != "%" && !SignalDisplay.IsTemperatureUnit(m.Unit);

    /// <summary>Measurements offered for rated values: those used by the unit's tests first, then the whole PLC database (analog only).</summary>
    /// <summary>
    /// Plant definition with the Plant Configurator project chosen (or refreshed) inside it; cancelling keeps the
    /// previous link.
    /// </summary>
    (PlantWizard.Result Result, PlantDefinition? Plant) RunPlantWizard(PlantDefinition? existing, PlantDefinition? suggestion, bool askConfig)
    {
        var before = plantConfig;
        var answer = PlantWizard.Show(this, existing, suggestion, WizardSources() with
        {
            CurrentConfig = () => plantConfig?.SourceFile,
            LinkConfig = path => { plantConfig = PlantConfigLink.Load(path); return SuggestedPlant(); },
            ConfigChanged = PlantConfigChanged
        }, askConfig);
        if (answer.Result == PlantWizard.Result.Cancel) plantConfig = before;
        return answer;
    }

    /// <summary>The linked .plantcfg was saved in Plant Configurator after it was loaded here.</summary>
    bool PlantConfigChanged() => plantConfig != null && File.Exists(plantConfig.SourceFile) && File.GetLastWriteTime(plantConfig.SourceFile) > plantConfig.LoadedAt;

    void PlantConfigRefresh_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (plantConfig == null) return;
        LinkPlantConfig(plantConfig.SourceFile);
        ApplyNominalRangesEverywhere();
    });

    /// <summary>PLC database of a unit in the wizard: its PLC, else the "Unit n" database, else the first one.</summary>
    PlcCatalog? WizardCatalog(PlantDefinition definition, UnitDefinition unit) =>
        CatalogOf(definition.PlcForUnit(unit.Number) ?? plant?.PlcForUnit(unit.Number))
        ?? plantConfig?.Plcs.FirstOrDefault(p => PlantConfigLink.UnitNumber(p.Name) == unit.Number)
        ?? plantConfig?.Plcs.FirstOrDefault();

    NominalSources WizardSources() => new(
        (definition, unit) =>
        {
            var source = WizardCatalog(definition, unit);
            var single = new PlantDefinition { Name = definition.Name, Units = [unit] };
            var tags = PlantPlanner.Generate(single).Where(s => !s.IsCommon).SelectMany(s => SlotById(s.Id)?.Channels?.Select(c => c.Tag) ?? catalog.ExpectedTags(s, null, unit)).Distinct(StringComparer.OrdinalIgnoreCase);
            return tags.Select(t => source?.Find(t) ?? PlantLayout.FindRegulator(t)).OfType<PlantMeasurement>().Where(NeedsNominal).DistinctBy(m => m.Tag).ToList();
        },
        (definition, unit) =>
        {
            var source = WizardCatalog(definition, unit);
            return (source?.AllItems ?? PlantLayout.Regulators).Where(NeedsNominal).ToList();
        });

    string DocumentHeading() => SlotById(activeDocument?.SlotId) is TestSlot slot ? slot.Path : project.Data.FileName;
}
