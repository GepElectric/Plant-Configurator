using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace TiaTraceStudio;

public partial class MainWindow
{
    internal sealed class OpenTrace
    {
        public StudioProject Project { get; set; } = new();
        public string? Path { get; set; }
        public string Saved { get; set; } = "";
        public List<string> History { get; set; } = [];
        public int HistoryIndex { get; set; } = -1;
        public int SelectedSignal { get; set; }
        public double? ViewLeft { get; set; }
        public double? ViewRight { get; set; }
        public string Title => Project.Data.FileName;
        public int Number { get; set; }
        public string? SlotId { get; set; }
        public string DisplayTitle => $"{Number} · {Title}";
    }
    readonly ObservableCollection<OpenTrace> documents = [];
    OpenTrace? activeDocument;
    SignalNameLibrary nameLibrary = new();
    Dictionary<Signal, (string Name, bool Local, string Unit, double Factor, double Offset)> observedNames = [];
    bool selectingDocument;
    string? workspacePath;
    string savedWorkspaceState = "";
    static string SessionRecoveryPath => System.IO.Path.ChangeExtension(RecoveryPath, ".tiasession");

    void BuildDocumentTools()
    {
        DocumentPicker.ItemsSource = documents;
    }
    void ObserveNames() => observedNames = project.Data.Signals.ToDictionary(s => s, s => (s.Name, s.NameOverride, s.Unit, s.Factor, s.Offset));
    IEnumerable<StudioProject> AllProjects() => documents.Select(d => d == activeDocument ? project : d.Project).Append(project).Distinct();
    void LearnNames()
    {
        bool changed = false;
        foreach (var signal in signals)
        {
            if (!observedNames.TryGetValue(signal, out var previous)) continue;
            if (previous == (signal.Name, signal.NameOverride, signal.Unit, signal.Factor, signal.Offset) || signal.NameOverride) continue;
            nameLibrary.Share(signal, AllProjects()); changed = true;
        }
        if (changed) { RecordOtherNameChanges(); }
        ObserveNames();
    }
    void RecordOtherNameChanges()
    {
        foreach (var doc in documents.Where(d => d != activeDocument))
        {
            string state = ProjectStore.Serialize(doc.Project);
            if (doc.HistoryIndex >= 0 && doc.History[doc.HistoryIndex] == state) continue;
            if (doc.HistoryIndex + 1 < doc.History.Count) doc.History.RemoveRange(doc.HistoryIndex + 1, doc.History.Count - doc.HistoryIndex - 1);
            doc.History.Add(state); if (doc.History.Count > 20) doc.History.RemoveAt(0); doc.HistoryIndex = doc.History.Count - 1;
        }
    }
    void CaptureDocument()
    {
        if (project.Data.Time.Length == 0) return;
        if (activeDocument == null) { activeDocument = new(); documents.Add(activeDocument); }
        activeDocument.Project = project; activeDocument.Path = projectPath; activeDocument.Saved = savedSnapshot;
        activeDocument.History = history.ToList(); activeDocument.HistoryIndex = historyIndex;
        activeDocument.SelectedSignal = SignalList.SelectedIndex;
        if (plots.Count > 0) { var limits = plots[0].Plot.Axes.GetLimits(); activeDocument.ViewLeft = limits.Left * renderedTimeScale + renderedZero; activeDocument.ViewRight = limits.Right * renderedTimeScale + renderedZero; }
    }
    void RefreshDocumentPicker()
    {
        selectingDocument = true;
        for (int i = 0; i < documents.Count; i++) documents[i].Number = i + 1;
        DocumentPicker.Items.Refresh(); DocumentPicker.SelectedItem = activeDocument;
        bool picker = documents.Count > 1 && plant == null;
        DocumentPicker.Visibility = picker ? Visibility.Visible : Visibility.Collapsed;
        FileTitle.Visibility = picker ? Visibility.Collapsed : Visibility.Visible;
        selectingDocument = false;
        ScheduleTreeRefresh();
    }
    void ActivateDocument(OpenTrace doc)
    {
        activeDocument = doc; project = doc.Project; selectedSlotId = SlotById(doc.SlotId)?.Id;
        ApplyNominalRanges(project, doc.SlotId); projectPath = doc.Path; savedSnapshot = doc.Saved;
        history.Clear(); history.AddRange(doc.History); historyIndex = doc.HistoryIndex;
        if (historyIndex < 0) ResetHistory();
        Bind(); SignalList.SelectedIndex = Math.Clamp(doc.SelectedSignal, 0, signals.Count - 1); Render();
        if (doc.ViewLeft is double left && doc.ViewRight is double right)
            foreach (var plot in plots) { plot.Plot.Axes.SetLimitsX(project.Options.ToAxisTime(left), project.Options.ToAxisTime(right)); plot.Refresh(); }
        RefreshDocumentPicker();
    }
    void DocumentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selectingDocument || DocumentPicker.SelectedItem is not OpenTrace next || next == activeDocument) return;
        try { Sync(); LearnNames(); RememberState(); CaptureDocument(); ActivateDocument(next); Status("Aktivni CSV: " + next.Title); }
        catch (Exception ex) { RefreshDocumentPicker(); Status(ex.Message); MessageBox.Show(this, ex.Message, "Promjena CSV-a"); }
    }
    internal Task AddPaths(IEnumerable<string> paths) => AddRecordings(paths, null);
    /// <summary>Loads CSV files; with a defined plant each file is assigned to a test (given, or chosen in a dialog).</summary>
    internal async Task AddRecordings(IEnumerable<string> paths, string? targetSlot)
    {
        var pathList = paths.ToArray();
        if (pathList.Any(IsProjectPath))
        {
            if (pathList.Length != 1) { MessageBox.Show(this, "Otvori jedan projekt, a zatim mu dodaj CSV datoteke.", "Otvori projekt"); return; }
            await OpenWorkspacePath(pathList[0]); return;
        }
        try
        {
            if (project.Data.Time.Length > 0) { Sync(); LearnNames(); RememberState(); CaptureDocument(); }
            IsEnabled = false; var errors = new List<string>(); OpenTrace? firstAdded = null; int added = 0;
            for (int index = 0; index < pathList.Length; index++)
            {
                string path = pathList[index];
                try
                {
                    Status("Učitavanje: " + System.IO.Path.GetFileName(path));
                    var next = await Task.Run(() => NewProject(TraceCsv.Read(path)));
                    nameLibrary.Apply(next); LearnAddresses(next); PlotService.Validate(next);
                    string? slotId = targetSlot;
                    if (slotId == null && plant != null && slots.Count > 0)
                    {
                        IsEnabled = true;
                        var choice = AskSlot(next, path, index + 1, pathList.Length);
                        IsEnabled = false;
                        if (choice.Skip) continue;
                        slotId = choice.SlotId;
                    }
                    ApplyMeasurementNames(next, slotId); ApplyNominalRanges(next, slotId);
                    if (SlotById(slotId) is TestSlot slot) { next.Options.Title = slot.ChartTitle; next.Options.Subtitle = plant!.Name; }
                    var doc = new OpenTrace { Project = next, SlotId = slotId };
                    documents.Add(doc); firstAdded ??= doc; added++;
                }
                catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
            }
            if (firstAdded != null) ActivateDocument(firstAdded);
            RefreshDocumentPicker(); Status($"Dodano: {added} · učitano CSV-ova: {documents.Count}" + (errors.Count > 0 ? $" · pogrešaka: {errors.Count}" : ""));
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Datoteke koje nisu učitane");
        }
        catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Uvoz"); }
        finally { IsEnabled = true; }
    }
    void RemoveDocument_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length == 0) return;
        Sync(); LearnNames(); RememberState(); CaptureDocument();
        documents.Remove(activeDocument!); activeDocument = null;
        if (documents.Count > 0) ActivateDocument(documents[0]);
        else { project = new(); projectPath = null; savedSnapshot = ""; history.Clear(); historyIndex = -1; Bind(); Render(); FileTitle.Text = "Otvori CSV trace"; FileInfo.Text = "Učitaj podatke, unesi nazive i izvezi sliku."; NamingHint.Visibility = Visibility.Collapsed; RefreshDocumentPicker(); }
    });
    TraceSession CurrentSession(bool committedOnly = false)
    {
        CaptureDocument();
        return new() {
            ActiveIndex = Math.Max(0, documents.IndexOf(activeDocument!)),
            Projects = documents.Select(d => committedOnly && d.HistoryIndex >= 0 ? ProjectStore.Deserialize<StudioProject>(d.History[d.HistoryIndex]) : d.Project).ToList(),
            NameLibrary = ProjectStore.Clone(nameLibrary),
            Views = documents.Select(d => new TraceView(d.ViewLeft ?? d.Project.Options.Start, d.ViewRight ?? d.Project.Options.End, d.SelectedSignal, d.SlotId)).ToList(),
            Plant = plant == null ? null : ProjectStore.Clone(plant),
            Slots = ProjectStore.Clone(slots),
            Catalog = plant == null ? null : ProjectStore.Clone(catalog),
            PlantConfig = plantConfig == null ? null : ProjectStore.Clone(plantConfig),
            TagAddresses = new(tagAddresses, StringComparer.OrdinalIgnoreCase)
        };
    }
    void SaveSession_Click(object sender, RoutedEventArgs e) => Guard(() => SaveSession());
    bool SaveSession(bool saveAs = false)
    {
        string? target = saveAs ? null : workspacePath;
        if (target == null)
        {
            var dialog = new SaveFileDialog { Filter = "Trace Studio projekt (sve snimke)|*.tracestudio", DefaultExt = ".tracestudio", AddExtension = true, FileName = workspacePath ?? "Traces.tracestudio" };
            if (dialog.ShowDialog(this) != true) return false; target = dialog.FileName;
        }
        SaveWorkspaceTo(target); return true;
    }
    void SaveWorkspaceTo(string path)
    {
        if (project.Data.Time.Length > 0)
        {
            var limits = plots.FirstOrDefault()?.Plot.Axes.GetLimits();
            double? left = limits?.Left * renderedTimeScale + renderedZero, right = limits?.Right * renderedTimeScale + renderedZero;
            Sync(); LearnNames(); RememberState();
            bool keepView = renderedStart == project.Options.Start && renderedEnd == project.Options.End;
            Render();
            if (keepView && left.HasValue && right.HasValue)
                foreach (var plot in plots) { plot.Plot.Axes.SetLimitsX(project.Options.ToAxisTime(left.Value), project.Options.ToAxisTime(right.Value)); plot.Refresh(); }
        }
        ProjectStore.Save(path, CurrentSession()); workspacePath = path;
        foreach (var doc in documents) doc.Saved = ProjectStore.Serialize(doc.Project);
        savedSnapshot = ProjectStore.Serialize(project); savedWorkspaceState = WorkspaceFingerprint();
        Status($"Projekt spremljen · {documents.Count} snimki · {path}");
    }
    string WorkspaceFingerprint()
    {
        var state = CurrentSession(); state.ActiveIndex = 0; state.Views = state.Views.Select(v => v with { SelectedSignal = 0 }).ToList();
        return ProjectStore.Serialize(state);
    }
    bool CanCloseDocuments()
    {
        if (project.Data.Time.Length == 0 && documents.Count == 0 && plant == null && nameLibrary.Names.Count == 0 && nameLibrary.Scaling.Count == 0 && workspacePath == null) return true;
        // Flush edited fields before checking every document for unsaved changes.
        foreach (var editor in new[] { SignalEditor, OptionsEditor, ExportEditor, CursorEditor }) UpdateFields(editor);
        project.Data.Signals = signals.ToList(); project.Notes = notes.ToList(); CaptureDocument();
        if (!HasErrors(this) && WorkspaceFingerprint() == savedWorkspaceState) return true;
        var answer = MessageBox.Show(this, "Spremiti projekt sa svim snimkama i nazivima prije nastavka?", "Nespremljene promjene", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        bool saved = false; Guard(() => saved = SaveSession()); return saved;
    }
    static bool IsProjectPath(string path) => new[] { ".tracestudio", ".tiaproject", ".tiatrace", ".tiasession" }.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));
    async Task OpenWorkspacePath(string path)
    {
        try
        {
            // Read and validate first, so a broken file cannot replace a working project.
            IsEnabled = false; var next = await Task.Run(() => TraceSession.Load(path)); IsEnabled = true;
            if (!CanCloseDocuments()) return;
            ReplaceWorkspace(next, path);
        }
        catch (Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Otvori projekt"); }
        finally { IsEnabled = true; }
    }
    void ReplaceWorkspace(TraceSession next, string? path)
    {
        selectingDocument = true; documents.Clear(); activeDocument = null;
        nameLibrary = ProjectStore.Clone(next.NameLibrary);
        plant = next.Plant; slots = next.Slots ?? []; catalog = next.Catalog ?? TestCatalog.LoadGlobal();
        plantConfig = next.PlantConfig; tagAddresses = new(next.TagAddresses ?? [], StringComparer.OrdinalIgnoreCase);
        selectedSlotId = null; expandedKeys.Clear(); collapsedKeys.Clear();
        bool recovering = path != null && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(SessionRecoveryPath), StringComparison.OrdinalIgnoreCase);
        workspacePath = path?.EndsWith(".tracestudio", StringComparison.OrdinalIgnoreCase) == true && !recovering ? path : null;
        for (int i = 0; i < next.Projects.Count; i++)
        {
            var p = next.Projects[i]; var view = next.Views.ElementAtOrDefault(i);
            documents.Add(new OpenTrace { Project = p, Saved = ProjectStore.Serialize(p), ViewLeft = view?.Left, ViewRight = view?.Right, SelectedSignal = view?.SelectedSignal ?? 0, SlotId = view?.SlotId });
        }
        selectingDocument = false;
        if (documents.Count > 0) ActivateDocument(documents[Math.Clamp(next.ActiveIndex, 0, documents.Count - 1)]);
        else { project = new(); projectPath = null; savedSnapshot = ""; history.Clear(); historyIndex = -1; Bind(); Render(); FileTitle.Text = plant == null ? "Projekt bez snimki" : plant.Name; FileInfo.Text = plant == null ? "Dodaj CSV — svaka snimka dobiva svoj graf." : "Odaberi test u stablu lijevo i učitaj njegov CSV."; NamingHint.Visibility = Visibility.Collapsed; RefreshDocumentPicker(); }
        savedWorkspaceState = recovering ? "" : WorkspaceFingerprint();
        Status($"Projekt otvoren · {documents.Count} snimki · nazivi, jedinice i scaling zajednički su unutar projekta.");
    }
    void NewWorkspace_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!CanCloseDocuments()) return;
        var (result, definition) = RunPlantWizard(null, SuggestedPlant(), askConfig: true);
        if (result == PlantWizard.Result.Cancel) return;
        var session = new TraceSession();
        if (definition != null) { PlantPlanner.LinkPlcs(definition, plantConfig); session.Plant = definition; session.Slots = PlantPlanner.Generate(definition); session.Catalog = TestCatalog.LoadGlobal(); session.PlantConfig = plantConfig; }
        ReplaceWorkspace(session, null);
        if (definition != null) { savedWorkspaceState = ""; Status($"Projekt {definition.Name} kreiran · {session.Slots.Count} testova. Odaberi test i učitaj CSV."); }
    });
    void NameAll_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        Sync(); if (SignalList.SelectedItem is not Signal signal) return;
        int count = nameLibrary.Share(signal, AllProjects(), true);
        RecordOtherNameChanges(); ObserveNames(); Commit();
        Status($"Naziv, jedinica i scaling primijenjeni na {count} signala u učitanim CSV-ovima i spremljen u bazu, uključujući lokalne iznimke.");
    });
    void UseLibraryName_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SignalList.SelectedItem is not Signal signal) return;
        if (!nameLibrary.Names.ContainsKey(signal.Source) && !nameLibrary.Scaling.ContainsKey(signal.Source)) throw new InvalidDataException("Za ovaj izvorni tag još nema postavki u bazi.");
        signal.NameOverride = false;
        if (nameLibrary.Names.TryGetValue(signal.Source, out var name)) signal.Name = name;
        if (nameLibrary.Scaling.TryGetValue(signal.Source, out var scaling)) scaling.Apply(signal); SignalEditor.DataContext = null; SignalEditor.DataContext = signal;
        ObserveNames(); Commit();
    });
    void EditNameLibrary_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (project.Data.Time.Length > 0) Commit();
        var entries = new ObservableCollection<NameEntry>(nameLibrary.Names.Keys.Union(nameLibrary.Scaling.Keys).OrderBy(s => s).Select(s => new NameEntry { Source = s, Name = nameLibrary.Names.GetValueOrDefault(s, ""), Unit = nameLibrary.Scaling.GetValueOrDefault(s)?.Unit ?? "", Factor = nameLibrary.Scaling.GetValueOrDefault(s)?.Factor ?? 1, Offset = nameLibrary.Scaling.GetValueOrDefault(s)?.Offset ?? 0 }));
        var layout = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock { Text = "Točno podudaranje izvornog taga. Spremanje ažurira učitane CSV-ove osim lokalnih iznimki. Dijele se naziv, jedinica, faktor i offset. Brisanje iz baze ne mijenja postojeće grafove.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var grid = new DataGrid { ItemsSource = entries, AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Izvorni CSV / PLC tag", Binding = new Binding("Source"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Naziv za graf", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Jedinica", Binding = BindingFor("Unit"), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Faktor", Binding = BindingFor("Factor"), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Offset", Binding = BindingFor("Offset"), Width = 85 });
        layout.Children.Add(grid);
        var dialog = new Window { Owner = this, Title = "Baza postavki signala", Content = layout, Width = Dialogs.Width(1100), Height = Dialogs.Height(700), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Action(buttons, "Ukloni iz baze", (_, _) => { if (grid.SelectedItem is NameEntry entry) entries.Remove(entry); });
        Action(buttons, "Spremi i primijeni", (_, _) => Guard(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (HasErrors(grid)) throw new InvalidDataException("Ispravi crveno označena polja.");
            foreach (var entry in entries) new SignalScaling(entry.Unit, entry.Factor, entry.Offset).Validate();
            nameLibrary.Names = entries.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Source, x => x.Name.Trim(), StringComparer.Ordinal);
            nameLibrary.Scaling = entries.ToDictionary(x => x.Source, x => new SignalScaling(x.Unit, x.Factor, x.Offset), StringComparer.Ordinal);
            foreach (var p in AllProjects()) nameLibrary.Apply(p);
            RecordOtherNameChanges(); ObserveNames(); if (project.Data.Time.Length > 0) { RememberState(); Render(); } dialog.Close();
        }), true);
        dialog.ShowDialog();
    });
    sealed class NameEntry { public string Source { get; set; } = ""; public string Name { get; set; } = ""; public string Unit { get; set; } = ""; public double Factor { get; set; } = 1; public double Offset { get; set; } }
    async void ExportDocuments_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Commit(); CaptureDocument(); var folder = new OpenFolderDialog { Title = "Mapa za sve učitane grafove" }; if (folder.ShowDialog(this) != true) return;
            IsEnabled = false; int done = 0; var errors = new List<string>();
            foreach (var doc in documents)
            {
                try
                {
                    string stem = System.IO.Path.Combine(folder.FolderName, System.IO.Path.GetFileNameWithoutExtension(doc.Title)); string path = stem + ".jpg"; int suffix = 2;
                    while (File.Exists(path)) path = stem + $" ({suffix++}).jpg";
                    PlotService.Export(doc.Project, path); done++;
                }
                catch (Exception ex) { errors.Add(doc.Title + ": " + ex.Message); }
                Status($"Izvezeno {done}/{documents.Count} grafova…"); await System.Windows.Threading.Dispatcher.Yield();
            }
            Status($"Izvezeno {done} grafova · pogrešaka: {errors.Count} · {folder.FolderName}");
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Izvoz");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Izvoz"); }
        finally { IsEnabled = true; }
    }
}
