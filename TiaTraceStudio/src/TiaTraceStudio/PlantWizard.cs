using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TiaTraceStudio;

/// <summary>Dialog that defines the plant (units, turbines, actuators, controllers) before commissioning starts.</summary>
public sealed class PlantWizard
{
    public enum Result { Cancel, Plant, Blank }
    readonly Window dialog;
    readonly PlantDefinition plant;
    readonly ObservableCollection<UnitDefinition> units;
    readonly ListBox unitList = new() { DisplayMemberPath = nameof(UnitDefinition.Summary), Width = 270, Margin = new Thickness(0, 0, 14, 0) };
    readonly StackPanel editor = new();
    readonly StackPanel peltonPanel = new(), crossFlowPanel = new();
    readonly TextBlock turbineInfo = new() { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBlock preview = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x2B, 0x45)) };
    readonly NominalSources? sources;
    readonly DataGrid nominalGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, Height = 230, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 4, 0, 6) };
    readonly ComboBox nominalPicker = new() { IsEditable = true, IsTextSearchEnabled = true, DisplayMemberPath = nameof(PlantMeasurement.Display), MinWidth = 360 };
    readonly TextBlock configInfo = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox count = new() { ItemsSource = Enumerable.Range(1, 12).ToArray() };
    readonly TextBox nameBox;
    readonly bool isNew;
    Result result = Result.Cancel;

    PlantWizard(Window owner, PlantDefinition? existing, PlantDefinition? suggestion, NominalSources? sources, bool askConfig)
    {
        isNew = existing == null;
        this.sources = sources;
        plant = existing != null ? ProjectStore.Clone(existing) : suggestion ?? new PlantDefinition { Units = [new UnitDefinition { Number = 1 }] };
        units = new(plant.Units);
        var layout = new DockPanel { Margin = new Thickness(18) };

        // Which Plant Configurator project this plant uses: names, units, addresses and ranges of all PLC databases.
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        if (sources?.LinkConfig != null)
        {
            var configRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var configButtons = new StackPanel { Orientation = Orientation.Horizontal };
            configButtons.Children.Add(Button("↻ Osvježi", () => { if (sources.CurrentConfig?.Invoke() is string file) LoadConfig(file); }));
            configButtons.Children.Add(Button("Odaberi .plantcfg…", ChooseConfig));
            DockPanel.SetDock(configButtons, Dock.Right); configRow.Children.Add(configButtons);
            var label = new TextBlock { Text = "Plant Configurator projekt", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(label, Dock.Left); configRow.Children.Add(label); configRow.Children.Add(configInfo);
            header.Children.Add(new Border { Child = configRow, BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 12) });
        }
        var top = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var w in new[] { 2.0, 1.4, 0.7, 1.6 }) top.ColumnDefinitions.Add(new() { Width = new GridLength(w, GridUnitType.Star) });
        nameBox = Text(nameof(PlantDefinition.Name), plant);
        top.Children.Add(Labeled("Naziv postrojenja *", nameBox, 0));
        top.Children.Add(Labeled("Lokacija", Text(nameof(PlantDefinition.Location), plant), 1));
        count.SelectedItem = units.Count;
        count.SelectionChanged += (_, _) => Resize((int)count.SelectedItem);
        top.Children.Add(Labeled("Broj agregata", count, 2));
        var common = Text(nameof(PlantDefinition.CommonLoadRejectionLevels), plant);
        common.TextChanged += (_, _) => UpdatePreview();
        top.Children.Add(Labeled("Zajedničko odbacivanje tereta [%] (svi agregati)", common, 3));
        header.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (existing == null) buttons.Children.Add(Button("Prazan projekt (bez definicije)", () => { result = Result.Blank; dialog!.Close(); }));
        buttons.Children.Add(Button("Odustani", () => dialog!.Close()));
        buttons.Children.Add(Button(existing == null ? "Kreiraj projekt" : "Spremi definiciju", Accept, primary: true));
        DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons); bottom.Children.Add(preview);
        DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);

        unitList.ItemsSource = units; DockPanel.SetDock(unitList, Dock.Left); layout.Children.Add(unitList);
        BuildEditor();
        layout.Children.Add(new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        unitList.SelectionChanged += (_, _) => ShowUnit();

        dialog = new Window
        {
            Owner = owner, Title = existing == null ? "Novi projekt · definicija postrojenja" : "Definicija postrojenja",
            Content = layout, Width = Dialogs.Width(1200), Height = Dialogs.Height(900), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        foreach (var unit in units) unit.PropertyChanged += (_, _) => UpdatePreview();
        foreach (var unit in units.Where(u => u.NominalRanges == null || u.NominalRanges.Count == 0)) Prefill(unit);
        unitList.SelectedIndex = 0; UpdatePreview(); ShowConfig(); ShowUnit();
        // New project: ask right away which Plant Configurator project it belongs to.
        if (askConfig && sources?.LinkConfig != null) { bool asked = false; dialog.ContentRendered += (_, _) => { if (!asked) { asked = true; ChooseConfig(); } }; }
    }

    void ShowConfig()
    {
        string? file = sources?.CurrentConfig?.Invoke();
        bool changed = file != null && sources?.ConfigChanged?.Invoke() == true;
        configInfo.Text = file == null ? "nije odabran – nazivi, adrese i rasponi mjerenja dolaze iz PLC baza Plant Configuratora"
            : $"{System.IO.Path.GetFileName(file)}   ·   {System.IO.Path.GetDirectoryName(file)}" + (changed ? "   ·   datoteka je izmijenjena, klikni ↻ Osvježi" : "");
        configInfo.Foreground = file == null || changed ? new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C)) : new SolidColorBrush(Color.FromRgb(0x17, 0x5C, 0xD3));
        configInfo.ToolTip = file;
    }

    void ChooseConfig()
    {
        string? current = sources?.CurrentConfig?.Invoke();
        if (PickConfigFile(dialog, current) is string file) LoadConfig(file);
    }

    /// <summary>File dialog for the .plantcfg (replaceable by the checks).</summary>
    internal static Func<Window, string?, string?> PickConfigFile = (owner, current) =>
    {
        var open = new Microsoft.Win32.OpenFileDialog { Title = "Plant Configurator projekt za ovo postrojenje", Filter = "Plant Configurator projekt|*.plantcfg;*.json|Sve datoteke|*.*" };
        if (current != null) { open.FileName = System.IO.Path.GetFileName(current); if (System.IO.Path.GetDirectoryName(current) is string folder && Directory.Exists(folder)) open.InitialDirectory = folder; }
        return open.ShowDialog(owner) == true ? open.FileName : null;
    };

    void LoadConfig(string path)
    {
        try
        {
            var suggestion = sources!.LinkConfig!(path);
            if (isNew && suggestion != null)
            {
                // Name and number of units from the "Unit n" databases of the chosen project.
                if (!string.IsNullOrWhiteSpace(suggestion.Name)) nameBox.Text = suggestion.Name;
                count.SelectedItem = suggestion.Units.Count;
            }
            foreach (var unit in units) Prefill(unit);
            ShowConfig(); ShowUnit(); UpdatePreview();
        }
        catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Plant Configurator", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static (Result Result, PlantDefinition? Plant) Show(Window owner, PlantDefinition? existing, PlantDefinition? suggestion = null, NominalSources? sources = null, bool askConfig = false)
    {
        var wizard = new PlantWizard(owner, existing, suggestion, sources, askConfig);
        wizard.dialog.ShowDialog();
        return (wizard.result, wizard.result == Result.Plant ? wizard.plant : null);
    }

    void BuildEditor()
    {
        Section("Turbina");
        var turbine = new ComboBox { ItemsSource = Turbines.All, Margin = new Thickness(0, 3, 0, 9) };
        turbine.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(UnitDefinition.Turbine)) { Mode = BindingMode.TwoWay });
        editor.Children.Add(new TextBlock { Text = "Tip turbine" }); editor.Children.Add(turbine);
        editor.Children.Add(turbineInfo);
        peltonPanel.Children.Add(Pair(("Broj mlaznica", Numbers(nameof(UnitDefinition.Nozzles), 1, 6)), ("Broj deflektora", Numbers(nameof(UnitDefinition.Deflectors), 0, 6))));
        crossFlowPanel.Children.Add(new TextBlock { Text = "Broj sekcija privodnog aparata" });
        crossFlowPanel.Children.Add(Numbers(nameof(UnitDefinition.GuideVaneSections), 1, 3));
        editor.Children.Add(peltonPanel); editor.Children.Add(crossFlowPanel);

        Section("Oprema");
        Check("MIV (glavni ulazni ventil)", nameof(UnitDefinition.HasMiv));
        Check("Bypass ventil", nameof(UnitDefinition.HasBypass));
        Check("Uzbuda (snimanja prve uzbude, regulacije napona i jalove snage)", nameof(UnitDefinition.HasExcitation));
        Check("Mjerenje vibracija (snima se kod odbacivanja tereta)", nameof(UnitDefinition.HasVibration));

        Section("Regulacije (step i rampa)");
        var controllers = new WrapPanel();
        foreach (var (label, path) in new[] { ("Brzina", nameof(UnitDefinition.SpeedControl)), ("Napon", nameof(UnitDefinition.VoltageControl)), ("Snaga", nameof(UnitDefinition.PowerControl)), ("Razina", nameof(UnitDefinition.LevelControl)), ("Otvor", nameof(UnitDefinition.OpeningControl)), ("Jalova snaga", nameof(UnitDefinition.ReactiveControl)) })
        {
            var check = new CheckBox { Content = label, Width = 130 }; check.SetBinding(CheckBox.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay }); controllers.Children.Add(check);
        }
        editor.Children.Add(controllers);
        editor.Children.Add(new TextBlock { Text = "Brzina i napon snimaju se u wet testu (prazni hod), ostale regulacije u testovima na mreži.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });

        Section("Odbacivanje tereta");
        editor.Children.Add(new TextBlock { Text = "Razine snage u % (odvojene zarezom)" });
        var levels = new TextBox(); levels.SetBinding(TextBox.TextProperty, new Binding(nameof(UnitDefinition.LoadRejectionLevels)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        editor.Children.Add(levels);

        Section("Nazivne vrijednosti (postotni graf: 0 % – 100 %)");
        editor.Children.Add(new TextBlock { Text = "Vrijede za sve snimke ovog agregata kad je kod signala uključeno „Raspon iz projekta“ (zadano). Vrijednosti su u jedinici mjerenja.", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        Binding Number(string path) => new(path) { Mode = BindingMode.TwoWay, ValidatesOnExceptions = true, TargetNullValue = "", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        nominalGrid.Columns.Add(new DataGridTextColumn { Header = "Mjerenje", Binding = new Binding(nameof(NominalRange.Name)), IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        nominalGrid.Columns.Add(new DataGridTextColumn { Header = "Tag", Binding = new Binding(nameof(NominalRange.Tag)), IsReadOnly = true, Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        nominalGrid.Columns.Add(new DataGridTextColumn { Header = "Jedinica", Binding = new Binding(nameof(NominalRange.Unit)), IsReadOnly = true, Width = 65 });
        nominalGrid.Columns.Add(new DataGridTextColumn { Header = "0 % =", Binding = Number(nameof(NominalRange.Min)), Width = 80 });
        nominalGrid.Columns.Add(new DataGridTextColumn { Header = "100 % =", Binding = Number(nameof(NominalRange.Max)), Width = 80 });
        editor.Children.Add(nominalGrid);
        SearchPicker.Attach(nominalPicker, m => ((PlantMeasurement)m).Display, () => AddNominal(SearchPicker.Picked<PlantMeasurement>(nominalPicker)));
        var nominalButtons = new WrapPanel();
        nominalButtons.Children.Add(nominalPicker);
        nominalButtons.Children.Add(Button("Dodaj", () => AddNominal(SearchPicker.Picked<PlantMeasurement>(nominalPicker))));
        nominalButtons.Children.Add(Button("Dodaj sva mjerenja iz testova", () =>
        {
            if (unitList.SelectedItem is not UnitDefinition unit || sources == null) return;
            foreach (var m in sources.FromTests(plant, unit)) AddNominal(m);
        }));
        nominalButtons.Children.Add(Button("Ukloni odabrano", () =>
        {
            if (unitList.SelectedItem is UnitDefinition unit && nominalGrid.SelectedItem is NominalRange range) { unit.NominalRanges.Remove(range); nominalGrid.Items.Refresh(); }
        }));
        editor.Children.Add(nominalButtons);

        editor.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        editor.Children.Add(Button("Kopiraj postavke ovog agregata na sve ostale", CopyToAll));
    }

    void ShowUnit()
    {
        if (unitList.SelectedItem is not UnitDefinition unit) return;
        nominalGrid.CommitEdit(DataGridEditingUnit.Cell, true); nominalGrid.CommitEdit(DataGridEditingUnit.Row, true);
        editor.DataContext = unit; UpdateTurbinePanels(unit);
        unit.NominalRanges ??= [];
        nominalGrid.ItemsSource = unit.NominalRanges; nominalGrid.Items.Refresh();
        nominalPicker.ItemsSource = sources?.All(plant, unit);
    }
    /// <summary>
    /// Rated values of the measurements recorded in the unit's tests, from the Plant Configurator parameters of its
    /// PLC database; missing rows are added, empty values filled, values typed in stay.
    /// </summary>
    void Prefill(UnitDefinition unit)
    {
        if (sources == null) return;
        unit.NominalRanges ??= [];
        foreach (var m in sources.FromTests(plant, unit))
        {
            var range = unit.NominalRanges.FirstOrDefault(r => TagMatcher.Matches(r.Tag, m.Tag));
            if (range == null) unit.NominalRanges.Add(new NominalRange { Tag = m.Tag, Name = m.Name, Unit = m.Unit, Min = m.RangeLow, Max = m.RangeHigh });
            else if (range.Min == null && range.Max == null && m.HasRange) { range.Min = m.RangeLow; range.Max = m.RangeHigh; }
        }
        if (unitList.SelectedItem == unit) nominalGrid.Items.Refresh();
    }

    void AddNominal(PlantMeasurement? measurement)
    {
        if (measurement == null || unitList.SelectedItem is not UnitDefinition unit) return;
        if (unit.NominalRanges.Any(r => TagMatcher.Matches(r.Tag, measurement.Tag))) return;
        unit.NominalRanges.Add(new NominalRange { Tag = measurement.Tag, Name = measurement.Name, Unit = measurement.Unit, Min = measurement.RangeLow, Max = measurement.RangeHigh });
        nominalGrid.Items.Refresh();
    }
    void UpdateTurbinePanels(UnitDefinition unit)
    {
        peltonPanel.Visibility = unit.Turbine == Turbines.Pelton ? Visibility.Visible : Visibility.Collapsed;
        crossFlowPanel.Visibility = unit.Turbine == Turbines.CrossFlow ? Visibility.Visible : Visibility.Collapsed;
        turbineInfo.Text = unit.Turbine switch
        {
            Turbines.Pelton => "Testovi ventila za svaku mlaznicu i svaki deflektor.",
            Turbines.Francis => "Testovi ventila za privodni aparat.",
            Turbines.Kaplan => "Testovi ventila za privodni aparat i lopatice rotora.",
            _ => "Testovi ventila za svaku sekciju privodnog aparata."
        };
    }
    void Resize(int count)
    {
        while (units.Count > count) units.RemoveAt(units.Count - 1);
        while (units.Count < count)
        {
            var unit = units[^1].CopyAs(units.Count + 1);
            unit.PropertyChanged += (_, _) => UpdatePreview(); units.Add(unit); Prefill(unit);
        }
        if (unitList.SelectedIndex < 0) unitList.SelectedIndex = 0;
        UpdatePreview();
    }
    void CopyToAll()
    {
        if (unitList.SelectedItem is not UnitDefinition source) return;
        int selected = unitList.SelectedIndex;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] == source) continue;
            var copy = source.CopyAs(units[i].Number); copy.PropertyChanged += (_, _) => UpdatePreview(); units[i] = copy;
        }
        unitList.SelectedIndex = selected; UpdatePreview();
    }
    void UpdatePreview()
    {
        if (unitList.SelectedItem is UnitDefinition unit) UpdateTurbinePanels(unit);
        unitList.Items.Refresh();
        try
        {
            plant.Units = units.ToList();
            var slots = PlantPlanner.Generate(plant);
            preview.Text = $"Stablo testova: {slots.Count} · dry {slots.Count(s => s.Phase == TestPhases.Dry)} · wet {slots.Count(s => s.Phase == TestPhases.Wet)} · na mreži {slots.Count(s => s.Phase == TestPhases.Grid)}"
                + (units.Count > 1 ? $" · zajednički {slots.Count(s => s.Phase == TestPhases.Common)}" : "");
            preview.Foreground = Brushes.SlateGray;
        }
        catch (Exception ex) { preview.Text = ex.Message; preview.Foreground = Brushes.Firebrick; }
    }
    void Accept()
    {
        try
        {
            nominalGrid.CommitEdit(DataGridEditingUnit.Cell, true); nominalGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (Validation.GetHasError(nominalGrid) || nominalGrid.Items.Cast<object>().Any(i => nominalGrid.ItemContainerGenerator.ContainerFromItem(i) is DataGridRow row && Validation.GetHasError(row)))
                throw new InvalidDataException("Nazivne vrijednosti: ispravi crveno označena polja (broj s lokalnim decimalnim separatorom).");
            plant.Units = units.ToList(); plant.Name = plant.Name.Trim(); plant.Validate();
            result = Result.Plant; dialog.Close();
        }
        catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Definicija postrojenja", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void Section(string text) => editor.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 6) });
    void Check(string label, string path)
    { var check = new CheckBox { Content = label }; check.SetBinding(CheckBox.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay }); editor.Children.Add(check); }
    static ComboBox Numbers(string path, int min, int max)
    {
        var combo = new ComboBox { ItemsSource = Enumerable.Range(min, max - min + 1).ToArray() };
        combo.SetBinding(ComboBox.SelectedItemProperty, new Binding(path) { Mode = BindingMode.TwoWay }); return combo;
    }
    static TextBox Text(string path, object source)
    { var text = new TextBox(); text.SetBinding(TextBox.TextProperty, new Binding(path) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); return text; }
    static FrameworkElement Labeled(string label, FrameworkElement control, int column)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control);
        Grid.SetColumn(panel, column); return panel;
    }
    static Grid Pair((string Label, FrameworkElement Control) a, (string Label, FrameworkElement Control) b)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel(); left.Children.Add(new TextBlock { Text = a.Label }); left.Children.Add(a.Control);
        var right = new StackPanel(); right.Children.Add(new TextBlock { Text = b.Label }); right.Children.Add(b.Control); Grid.SetColumn(right, 2);
        grid.Children.Add(left); grid.Children.Add(right); return grid;
    }
    static Button Button(string label, System.Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary && Application.Current?.MainWindow?.TryFindResource("Primary") is Style style) button.Style = style;
        button.Click += (_, _) => action(); return button;
    }
}

/// <summary>Measurements offered for rated values: those of the unit's tests, and all analog signals of its PLC database.</summary>
public sealed record NominalSources(
    Func<PlantDefinition, UnitDefinition, IReadOnlyList<PlantMeasurement>> FromTests,
    Func<PlantDefinition, UnitDefinition, IReadOnlyList<PlantMeasurement>> All,
    Func<string?>? CurrentConfig = null,
    Func<string, PlantDefinition?>? LinkConfig = null,
    Func<bool>? ConfigChanged = null);
