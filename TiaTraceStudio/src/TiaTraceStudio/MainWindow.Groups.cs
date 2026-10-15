using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TiaTraceStudio;

/// <summary>
/// User-built parts of the tree: groups (e.g. Intake, Common systems) with their own PLC and Plant Configurator
/// database, sub-groups and recordings, plus right-click menus for units and groups.
/// </summary>
public partial class MainWindow
{
    CustomGroup? GroupOf(TestSlot? slot) => slot == null || string.IsNullOrEmpty(slot.GroupId) ? null : plant?.CustomGroups.FirstOrDefault(g => g.Id == slot.GroupId);
    CustomGroup? GroupById(string id) => plant?.CustomGroups.FirstOrDefault(g => g.Id == id);

    /// <summary>Right-click menus on units, groups, sub-groups and the user recordings node.</summary>
    void AttachContextMenus(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is TestSlot slot)
                item.ContextMenu = slot.IsUser
                    ? Menu(("Preimenuj…", () => RenameSlot(slot)), ("Uredi mjerenja…", () => EditChannels(slot)), ("Upute i mjerenja…", () => ShowTestDetails(slot)), ("Napomena…", () => EditNote(slot)), ("Obriši snimanje…", () => DeleteSlot(slot)))
                    : Menu(("Preimenuj test…", () => RenameSlot(slot)), ("Uredi mjerenja…", () => EditChannels(slot)), ("Upute i mjerenja…", () => ShowTestDetails(slot)), ("Napomena…", () => EditNote(slot)), ("Obriši snimanje…", () => DeleteSlot(slot)));
            if (item.Tag is string key)
            {
                if (key.StartsWith("U") && int.TryParse(key[1..], out int unit))
                {
                    var entries = new List<(string, System.Action)> { ("Novo snimanje agregata…", () => NewUnitRecording(unit, TestPhases.User, "")), ("Nova podgrupa…", () => NewUnitFolder(unit, TestPhases.User, "")),
                        ("PLC i baza Plant Configuratora…", () => EditUnitPlc(unit)), ("Definicija postrojenja…", () => EditPlant_Click(this, new RoutedEventArgs())) };
                    int deleted = slots.Count(s => s.Deleted && s.Unit == unit);
                    if (deleted > 0) entries.Add(($"Vrati obrisana snimanja ({deleted})", () => RestoreDeleted(s => s.Unit == unit)));
                    item.ContextMenu = Menu([.. entries]);
                }
                else if (key == "P0/" + TestPhases.Common && slots.Count(s => s.Deleted && s.IsCommon) is int deletedCommon and > 0)
                    item.ContextMenu = Menu(($"Vrati obrisana snimanja ({deletedCommon})", () => RestoreDeleted(s => s.IsCommon)));
                else if (key.StartsWith("U") && key.IndexOf('/') is int cut and > 1 && int.TryParse(key[1..cut], out int unitNumber))
                {
                    // Phase or group of a unit: "U1/1 · Dry test/Ventili/Mlaznica 1".
                    string rest = key[(cut + 1)..]; int slash = rest.IndexOf('/');
                    string phase = slash < 0 ? rest : rest[..slash], path = slash < 0 ? "" : rest[(slash + 1)..];
                    var entries = new List<(string, System.Action)> { ("Novo snimanje ovdje…", () => NewUnitRecording(unitNumber, phase, path)), ("Nova podgrupa…", () => NewUnitFolder(unitNumber, phase, path)) };
                    if (path.Length > 0 && UnitOf(unitNumber)?.Folders.Contains($"{phase}/{path}") == true) entries.Add(("Obriši podgrupu", () => DeleteUnitFolder(unitNumber, phase, path)));
                    item.ContextMenu = Menu([.. entries]);
                }
                else if (key.StartsWith("G/"))
                {
                    var rest = key[2..]; int slash = rest.IndexOf('/');
                    string id = slash < 0 ? rest : rest[..slash], path = slash < 0 ? "" : rest[(slash + 1)..];
                    if (GroupById(id) is CustomGroup group)
                        item.ContextMenu = path.Length == 0
                            ? Menu(("Novo snimanje u grupi…", () => NewUserRecording(group, "")), ("Nova podgrupa…", () => NewFolder(group, "")), ("Uredi grupu (naziv, PLC, baza)…", () => EditGroup(group)), ("Obriši grupu", () => DeleteGroup(group)))
                            : Menu(("Novo snimanje ovdje…", () => NewUserRecording(group, path)), ("Nova podgrupa…", () => NewFolder(group, path)), ("Obriši podgrupu", () => DeleteFolder(group, path)));
                }
                else if (key == "P0/" + TestPhases.User) item.ContextMenu = Menu(("Novo korisničko snimanje…", () => NewUserRecording(null, "")), ("Nova grupa…", () => EditGroup(null)));
            }
            if (item.Tag is string nodeKey)
            {
                // Every part of the tree can be renamed.
                var rename = new MenuItem { Header = "Preimenuj…" };
                rename.Click += (_, e) => { e.Handled = true; Guard(() => RenameNode(nodeKey)); };
                item.ContextMenu ??= new ContextMenu();
                item.ContextMenu.Items.Insert(0, rename);
                if (item.ContextMenu.Items.Count > 1) item.ContextMenu.Items.Insert(1, new Separator());
            }
            AttachContextMenus(item.Items);
        }
        PlantTree.ContextMenu ??= Menu(("Nova grupa (vlastito stablo)…", () => EditGroup(null)), ("Novo korisničko snimanje…", () => NewUserRecording(null, "")));
    }

    /// <summary>Renames a test or recording; recordings whose graph title followed the test name follow the new name.</summary>
    void RenameSlot(TestSlot slot)
    {
        string? original = slot.IsUser || plant == null ? null : PlantPlanner.Generate(plant).FirstOrDefault(s => s.Id == slot.Id)?.Title;
        string prompt = original == null || original == slot.Title ? "Naziv testa" : $"Naziv testa (izvorni naziv: {original})";
        if (AskText(prompt, slot.Title) is not string text) return;
        text = text.Trim();
        if (text.Length == 0 && original == null) throw new InvalidDataException("Upiši naziv.");
        string oldChart = slot.ChartTitle;
        slot.Title = text.Length == 0 ? original! : text;
        slot.CustomTitle = slot.IsUser || slot.Title == original ? null : slot.Title;
        foreach (var doc in documents.Where(d => d.SlotId == slot.Id))
        {
            var options = (doc == activeDocument ? project : doc.Project).Options;
            if (options.Title == oldChart) options.Title = slot.ChartTitle;
        }
        if (project.Data.Time.Length > 0) { Bind(); Render(); }
        RefreshTree(); RefreshDocumentPicker();
        Status($"Test preimenovan: {slot.Path}. Spremi projekt.");
    }

    ContextMenu Menu(params (string Header, System.Action Action)[] entries)
    {
        var menu = new ContextMenu();
        foreach (var (header, action) in entries)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, e) => { e.Handled = true; Guard(action); };
            menu.Items.Add(item);
        }
        return menu;
    }

    void NewUserRecording(CustomGroup? group, string path)
    {
        if (plant == null) throw new InvalidDataException("Prvo definiraj postrojenje (Novi projekt).");
        if (plant.Plcs.Count == 0) PlantPlanner.LinkPlcs(plant, plantConfig);
        if (AskText(group == null ? "Naziv korisničkog snimanja (npr. Intake – razina i zapornica)" : $"Naziv snimanja u grupi {group.Name}" + (path.Length > 0 ? $" / {path}" : ""), "") is not string title) return;
        TestSlot slot;
        if (group == null)
        {
            var plc = plant.Plcs.FirstOrDefault(p => p.UnitNumbers().Length == 0) ?? plant.Plcs.First();
            slot = PlantPlanner.NewUserSlot(title, plc.Id);
        }
        else
        {
            // Recordings of a group use the group's PLC and database unless one is chosen for the recording itself.
            slot = PlantPlanner.NewUserSlot(title, "");
            slot.GroupId = group.Id; slot.Phase = group.Name; slot.Group = path;
            expandedKeys.Add("G/" + group.Id); if (path.Length > 0) expandedKeys.Add($"G/{group.Id}/{path}");
        }
        slots.Add(slot); selectedSlotId = slot.Id; expandedKeys.Add("P0/" + TestPhases.User);
        RefreshTree();
        Status($"Snimanje „{title}“ dodano · PLC {PlcFor(slot)?.Name ?? "nije odabran"}. Odaberi mjerenja (Uredi mjerenja) i pokreni snimanje ili učitaj CSV.");
    }

    /// <summary>
    /// Renames a tree node. User-made groups and sub-groups are renamed for real (their recordings move with them);
    /// generated nodes (unit, phase, generated group) get a display name and can be renamed back.
    /// </summary>
    void RenameNode(string key)
    {
        if (plant == null) return;
        string original = treeDefaults.TryGetValue(key, out var d) ? d : key;
        string prompt = key.StartsWith("G/") && !key[2..].Contains('/') ? "Naziv grupe" : IsUserFolder(key) ? "Naziv podgrupe" : $"Naziv u stablu (izvorni: {original})";
        if (AskText(prompt, CurrentNodeName(key)) is not string name) return;
        ApplyNodeName(key, name);
        RefreshTree(); Status("Preimenovano. Spremi projekt.");
    }

    string CurrentNodeName(string key)
    {
        if (key.StartsWith("G/") && !key[2..].Contains('/') && GroupById(key[2..]) is CustomGroup group) return group.Name;
        return plant?.TreeNames.TryGetValue(key, out var n) == true ? n : treeDefaults.TryGetValue(key, out var d) ? d : key;
    }

    bool IsUserFolder(string key) => key.StartsWith("G/") && key[2..].Contains('/')
        || key.StartsWith("U") && key.IndexOf('/') is int cut and > 1 && int.TryParse(key[1..cut], out int unit) && UnitOf(unit)?.Folders.Contains(key[(cut + 1)..]) == true;

    /// <summary>
    /// Renames a tree node. User-made groups and sub-groups are renamed for real (their recordings move with them);
    /// generated nodes (unit, phase, generated group) get a display name; the original name removes it again.
    /// </summary>
    void ApplyNodeName(string key, string name)
    {
        if (plant == null || string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        string original = treeDefaults.TryGetValue(key, out var d) ? d : key;
        if (key.StartsWith("G/"))
        {
            var rest = key[2..]; int slash = rest.IndexOf('/');
            string id = slash < 0 ? rest : rest[..slash], path = slash < 0 ? "" : rest[(slash + 1)..];
            if (GroupById(id) is not CustomGroup group) return;
            if (path.Length == 0)
            {
                group.Name = name; plant.TreeNames.Remove(key);
                foreach (var slot in slots.Where(s => s.GroupId == group.Id)) slot.Phase = name;
            }
            else MoveFolder(group.Folders, "", slots.Where(s => s.GroupId == group.Id), path, name);
        }
        else if (IsUserFolder(key) && key.IndexOf('/') is int cut && int.TryParse(key[1..cut], out int unit) && UnitOf(unit) is UnitDefinition definition)
        {
            string rest = key[(cut + 1)..]; int slash = rest.IndexOf('/');
            string phase = rest[..slash], path = rest[(slash + 1)..];
            MoveFolder(definition.Folders, phase + "/", slots.Where(s => s.Unit == unit && s.Phase == phase), path, name);
        }
        else if (name == original) plant.TreeNames.Remove(key);
        else plant.TreeNames[key] = name;
    }

    /// <summary>Renames the last part of a user sub-group path; its sub-groups and recordings follow.</summary>
    static void MoveFolder(List<string> folders, string prefix, IEnumerable<TestSlot> members, string path, string name)
    {
        name = name.Replace("/", "-").Trim();
        int cut = path.LastIndexOf('/');
        string renamed = (cut < 0 ? "" : path[..(cut + 1)]) + name;
        string Move(string p) => p == path ? renamed : p.StartsWith(path + "/") ? renamed + p[path.Length..] : p;
        for (int i = 0; i < folders.Count; i++) if (folders[i].StartsWith(prefix)) folders[i] = prefix + Move(folders[i][prefix.Length..]);
        foreach (var slot in members) slot.Group = Move(slot.Group);
    }

    /// <summary>
    /// Removes a test or recording from the tree; its recordings leave the project after confirmation (CSV files on
    /// disk stay). Generated tests are only hidden, so the plant definition does not bring them back.
    /// </summary>
    void DeleteSlot(TestSlot slot)
    {
        var recordings = RecordingsOf(slot);
        string question = recordings.Count == 0 ? $"Obrisati „{slot.Title}“ iz stabla?"
            : $"„{slot.Title}“ ima {recordings.Count} snimk{(recordings.Count == 1 ? "u" : "i")}. Obrisati snimanje i njegove snimke iz projekta?\n(CSV datoteke na disku ostaju.)";
        if (!slot.IsUser) question += "\n\nObrisani test može se vratiti: desni klik na agregat → Vrati obrisana snimanja.";
        if ((ConfirmDelete ?? (q => MessageBox.Show(this, q, "Obriši snimanje", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))(question) != true) return;
        foreach (var doc in recordings.Where(d => d != activeDocument)) documents.Remove(doc);
        if (activeDocument != null && recordings.Contains(activeDocument)) RemoveDocument_Click(this, new RoutedEventArgs());
        if (slot.IsUser) slots.Remove(slot); else slot.Deleted = true;
        if (selectedSlotId == slot.Id) selectedSlotId = null;
        RefreshDocumentPicker(); RefreshTree();
        Status($"Obrisano: {slot.Path}" + (recordings.Count > 0 ? $" i {recordings.Count} snimki" : "") + ". Spremi projekt.");
    }

    /// <summary>Confirmation of a deletion (replaceable by the checks).</summary>
    internal Func<string, bool?>? ConfirmDelete;

    void RestoreDeleted(Func<TestSlot, bool> which)
    {
        int count = 0;
        foreach (var slot in slots.Where(s => s.Deleted && which(s))) { slot.Deleted = false; count++; }
        RefreshTree(); Status($"Vraćeno snimanja: {count}. Spremi projekt.");
    }

    UnitDefinition? UnitOf(int number) => plant?.Units.FirstOrDefault(u => u.Number == number);

    void ExpandUnitPath(int unit, string phase, string path)
    {
        expandedKeys.Add($"U{unit}"); expandedKeys.Add($"U{unit}/{phase}");
        string key = $"U{unit}/{phase}";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { key += "/" + part; expandedKeys.Add(key); }
    }

    /// <summary>A recording of the user's own inside a unit (phase or group of its tree), recorded from the unit's PLC.</summary>
    void NewUnitRecording(int unit, string phase, string path)
    {
        if (plant == null) throw new InvalidDataException("Prvo definiraj postrojenje (Novi projekt).");
        if (plant.Plcs.Count == 0) PlantPlanner.LinkPlcs(plant, plantConfig);
        if (AskText($"Naziv snimanja · Agregat {unit} · {phase}" + (path.Length > 0 ? " · " + path.Replace("/", " · ") : ""), "") is not string title) return;
        var slot = PlantPlanner.NewUserSlot(title, plant.PlcForUnit(unit)?.Id ?? "");
        slot.Unit = unit; slot.Phase = phase; slot.Group = path;
        slots.Add(slot); selectedSlotId = slot.Id; ExpandUnitPath(unit, phase, path);
        RefreshTree();
        Status($"Snimanje „{title}“ dodano u Agregat {unit} · PLC {PlcFor(slot)?.Name ?? "nije odabran"}. Odaberi mjerenja (Uredi mjerenja) i pokreni snimanje ili učitaj CSV.");
    }

    void NewUnitFolder(int unit, string phase, string parent)
    {
        if (UnitOf(unit) is not UnitDefinition definition) return;
        if (AskText("Naziv podgrupe", "") is not string name) return;
        name = name.Replace("/", "-").Trim();
        string path = parent.Length > 0 ? $"{parent}/{name}" : name;
        if (!definition.Folders.Contains($"{phase}/{path}")) definition.Folders.Add($"{phase}/{path}");
        ExpandUnitPath(unit, phase, parent);
        RefreshTree();
    }

    void DeleteUnitFolder(int unit, string phase, string path)
    {
        if (slots.Any(s => s.Unit == unit && s.Phase == phase && (s.Group == path || s.Group.StartsWith(path + "/")))) throw new InvalidDataException("Podgrupa sadrži snimanja. Najprije ih obriši.");
        UnitOf(unit)?.Folders.RemoveAll(f => f == $"{phase}/{path}" || f.StartsWith($"{phase}/{path}/"));
        RefreshTree();
    }

    void NewFolder(CustomGroup group, string parent)
    {
        if (AskText("Naziv podgrupe", "") is not string name) return;
        name = name.Replace("/", "-").Trim();
        string path = parent.Length > 0 ? $"{parent}/{name}" : name;
        if (!group.Folders.Contains(path)) group.Folders.Add(path);
        expandedKeys.Add("G/" + group.Id); if (parent.Length > 0) expandedKeys.Add($"G/{group.Id}/{parent}");
        RefreshTree();
    }

    void DeleteFolder(CustomGroup group, string path)
    {
        if (slots.Any(s => s.GroupId == group.Id && (s.Group == path || s.Group.StartsWith(path + "/")))) throw new InvalidDataException("Podgrupa sadrži snimanja. Najprije ih obriši.");
        group.Folders.RemoveAll(f => f == path || f.StartsWith(path + "/"));
        RefreshTree();
    }

    void DeleteGroup(CustomGroup group)
    {
        if (slots.Any(s => s.GroupId == group.Id)) throw new InvalidDataException("Grupa sadrži snimanja. Najprije ih obriši.");
        if (MessageBox.Show(this, $"Obrisati grupu „{group.Name}“?", "Grupa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        plant!.CustomGroups.Remove(group);
        RefreshTree();
    }

    /// <summary>Creates or edits a group: name, Plant Configurator database and the PLC it is recorded from.</summary>
    void EditGroup(CustomGroup? group)
    {
        if (plant == null) throw new InvalidDataException("Prvo definiraj postrojenje (Novi projekt).");
        var plc = PlcById(group?.PlcId) ?? new PlcConnection { Name = group?.Name ?? "" };
        string? name = group?.Name ?? "";
        if (!PlcDialog(group == null ? "Nova grupa" : "Grupa · " + group.Name, ref name, plc)) return;
        if (group == null) { group = new CustomGroup(); plant.CustomGroups.Add(group); }
        group.Name = name!;
        // A PLC already known by its IP (e.g. a unit PLC) is reused; otherwise the group gets its own PLC entry.
        var existing = plc.Ip.Length > 0 ? plant.Plcs.FirstOrDefault(p => p != plc && p.Ip == plc.Ip) : null;
        if (existing != null) { existing.CatalogId = plc.CatalogId; existing.Rack = plc.Rack; existing.Slot = plc.Slot; plc = existing; }
        else if (!plant.Plcs.Contains(plc)) { plc.Name = group.Name; plant.Plcs.Add(plc); }
        group.PlcId = plc.Id;
        foreach (var slot in slots.Where(s => s.GroupId == group.Id)) slot.Phase = group.Name;
        expandedKeys.Add("G/" + group.Id);
        RefreshTree();
        Status($"Grupa „{group.Name}“ · PLC {plc.Name}" + (plantConfig?.Get(plc.CatalogId) is PlcCatalog db ? $" · baza {db.Name}" : " · baza nije odabrana") + ". Desni klik na grupu za nova snimanja i podgrupe.");
    }

    /// <summary>The PLC (IP, rack, slot) and Plant Configurator database of a unit.</summary>
    void EditUnitPlc(int unit)
    {
        if (plant == null) return;
        if (plant.PlcForUnit(unit) == null) PlantPlanner.LinkPlcs(plant, plantConfig);
        var plc = plant.PlcForUnit(unit) ?? throw new InvalidDataException($"Agregat {unit} nema PLC.");
        string? name = null;
        if (!PlcDialog($"Agregat {unit} · PLC i baza Plant Configuratora", ref name, plc)) return;
        RefreshTree();
        Status($"Agregat {unit} · PLC {plc.Name}" + (string.IsNullOrWhiteSpace(plc.Ip) ? "" : $" ({plc.Ip})") + (plantConfig?.Get(plc.CatalogId) is PlcCatalog db ? $" · baza {db.Name}" : ""));
    }

    /// <summary>Edits PLC connection fields and the database; with a name field when <paramref name="name"/> is not null.</summary>
    bool PlcDialog(string title, ref string? name, PlcConnection plc)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 9) }; panel.Children.Add(box); return box; }
        var nameBox = name != null ? Field("Naziv grupe (npr. Intake, Zajednički pogon)", name) : null;
        panel.Children.Add(new TextBlock { Text = "PLC baza u Plant Configuratoru" });
        var database = new ComboBox { ItemsSource = plantConfig?.Plcs, DisplayMemberPath = nameof(PlcCatalog.Name), SelectedValuePath = nameof(PlcCatalog.Id), SelectedValue = plc.CatalogId, IsEnabled = plantConfig != null, Margin = new Thickness(0, 3, 0, 4) };
        panel.Children.Add(database);
        panel.Children.Add(new TextBlock { Text = plantConfig == null ? "Plant Configurator nije povezan (Projekt ▾ → PLC-ovi i Plant Configurator)." : "Nazivi, jedinice i adrese mjerenja dolaze iz ove baze.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) });
        var ip = Field("IP adresa PLC-a", plc.Ip);
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); row.ColumnDefinitions.Add(new());
        var rackPanel = new StackPanel(); rackPanel.Children.Add(new TextBlock { Text = "Rack" }); var rack = new TextBox { Text = plc.Rack.ToString(), Margin = new Thickness(0, 3, 0, 9) }; rackPanel.Children.Add(rack);
        var slotPanel = new StackPanel(); slotPanel.Children.Add(new TextBlock { Text = "Slot (S7-1500: 1)" }); var slotBox = new TextBox { Text = plc.Slot.ToString(), Margin = new Thickness(0, 3, 0, 9) }; slotPanel.Children.Add(slotBox); Grid.SetColumn(slotPanel, 2);
        row.Children.Add(rackPanel); row.Children.Add(slotPanel); panel.Children.Add(row);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = title, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(Resources);
        bool saved = false; string? newName = name;
        Action(buttons, "Odustani", (_, _) => dialog.Close());
        Action(buttons, "Spremi", (_, _) => Guard(() =>
        {
            if (nameBox != null && string.IsNullOrWhiteSpace(nameBox.Text)) throw new InvalidDataException("Upiši naziv grupe.");
            if (ip.Text.Trim().Length > 0 && !System.Net.IPAddress.TryParse(ip.Text.Trim(), out _)) throw new InvalidDataException($"Neispravna IP adresa '{ip.Text}'.");
            if (!short.TryParse(rack.Text, out short r) || !short.TryParse(slotBox.Text, out short sl)) throw new InvalidDataException("Rack i slot su cijeli brojevi.");
            plc.Ip = ip.Text.Trim(); plc.Rack = r; plc.Slot = sl; plc.CatalogId = database.SelectedValue as string ?? plc.CatalogId;
            newName = nameBox?.Text.Trim();
            saved = true; dialog.Close();
        }), true);
        dialog.Loaded += (_, _) => (nameBox ?? ip).Focus();
        dialog.ShowDialog();
        name = newName;
        return saved;
    }
}
