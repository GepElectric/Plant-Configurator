using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using Microsoft.Win32;

namespace CwcConfigurator.App;

/// <summary>
/// EPLAN hardware import in three steps: (1) every PLC database of the project gets one CPU from EPLAN,
/// (2) every CPU gets its distributed stations in module numbering order (drag &amp; drop), (3) the selected
/// database is previewed and imported. Both assignments are remembered in the project and can be changed any time.
/// </summary>
public partial class EplanImportDialog : Window
{
    public sealed record CpuChoice(string Function, string Display);

    public sealed class StationItem
    {
        public required EplanStation Station { get; init; }
        public string Display => $"={Station.Function}  -{Station.HeadDeviceTag}  ({Station.Modules.Count} modules)";
    }

    public sealed class CpuItem
    {
        public required EplanStation Cpu { get; init; }
        public ObservableCollection<StationItem> Stations { get; } = [];
        public string Header => $"={Cpu.Function}  -{Cpu.HeadDeviceTag}  {Cpu.HeadType}";
    }

    public sealed class DatabaseItem : INotifyPropertyChanged
    {
        private string _cpuFunction = "";

        public required PlcDatabase Database { get; init; }
        public required IReadOnlyList<CpuChoice> Choices { get; init; }
        public string Name => Database.Name;

        public string CpuFunction
        {
            get => _cpuFunction;
            set
            {
                if (_cpuFunction == (value ?? ""))
                {
                    return;
                }

                _cpuFunction = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CpuFunction)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record PreviewRow(string Tab, int ModuleNumber, string ModuleName, int? Port, string Station,
        string Signal, string ConnectedTo, int? PdfPage, string SchematicPage);

    private readonly IReadOnlyList<PlcDatabase> _databases;
    private readonly IReadOnlyList<EplanCpuAssignment> _remembered;
    private readonly string _initialDatabaseId;
    private readonly ObservableCollection<DatabaseItem> _databaseItems = [];
    private readonly ObservableCollection<StationItem> _unassigned = [];
    private readonly List<CpuItem> _cpus = [];
    private bool _updatingCpuChoice;
    private Point _dragStart;
    private ListBox? _dragSource;

    public EplanProject? Project { get; private set; }

    /// <summary>Database to fill after the dialog closes; null when only the assignments were saved.</summary>
    public string? ImportDatabaseId { get; private set; }

    public EplanImportDialog(IReadOnlyList<PlcDatabase> databases, EplanImportConfig remembered, string activeDatabaseId)
    {
        InitializeComponent();
        _databases = databases;
        _remembered = remembered.Assignments;
        _initialDatabaseId = activeDatabaseId;
        DatabaseList.ItemsSource = _databaseItems;
        UnassignedList.ItemsSource = _unassigned;
        PathText.Text = remembered.LastEpjPath;
        ReadButton.IsEnabled = File.Exists(remembered.LastEpjPath);
        StatusText.Text = ReadButton.IsEnabled
            ? "Press Read to load the remembered EPLAN project, or Browse for another one."
            : "Choose the EPLAN PXF export (.epj).";
    }

    /// <summary>
    /// Assignments to remember: everything shown in the dialog plus remembered CPUs that are not in this .epj
    /// (another EPLAN project of the same plant), so switching files never loses decisions.
    /// </summary>
    public List<EplanCpuAssignment> Assignments()
    {
        var current = _cpus.Select(cpu => new EplanCpuAssignment
        {
            CpuFunction = cpu.Cpu.Function,
            PlcDatabaseId = _databaseItems.FirstOrDefault(d => d.CpuFunction == cpu.Cpu.Function)?.Database.Id ?? "",
            DistributedFunctions = cpu.Stations.Select(s => s.Station.Function).ToList()
        }).ToList();

        var shownDatabases = current.Select(a => a.PlcDatabaseId).Where(id => id.Length > 0).ToHashSet();
        var kept = _remembered
            .Where(a => current.All(c => c.CpuFunction != a.CpuFunction))
            .Select(a => new EplanCpuAssignment
            {
                CpuFunction = a.CpuFunction,
                PlcDatabaseId = shownDatabases.Contains(a.PlcDatabaseId) ? "" : a.PlcDatabaseId,
                DistributedFunctions = a.DistributedFunctions.ToList()
            });
        return current.Concat(kept).ToList();
    }

    // ---------------------------------------------------------------- read

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "EPLAN PXF export (*.epj)|*.epj|All files (*.*)|*.*",
            FileName = PathText.Text
        };
        if (dialog.ShowDialog(this) == true)
        {
            PathText.Text = dialog.FileName;
            ReadButton.IsEnabled = true;
            Read_Click(sender, e);
        }
    }

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        var path = PathText.Text;
        ReadButton.IsEnabled = false;
        StatusText.Text = $"Reading {Path.GetFileName(path)}...";
        Cursor = Cursors.Wait;
        try
        {
            Project = await Task.Run(() => EplanProjectReader.Read(path));
            Fill(_remembered);
            var cpus = Project.Stations.Count(s => s.IsCpu);
            StatusText.Text = $"{Project.ProjectName}: {Project.Pages.Count} pages, {cpus} CPU, {Project.Stations.Count - cpus} distributed stations. " +
                              (_remembered.Count == 0 ? "Nothing assigned yet – give every database its CPU and every CPU its stations." : "Remembered assignments loaded.");
            SaveButton.IsEnabled = ImportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            MessageBox.Show(this, ex.Message, "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            ReadButton.IsEnabled = true;
        }
    }

    private void Fill(IReadOnlyList<EplanCpuAssignment> assignments)
    {
        if (Project is null)
        {
            return;
        }

        var selectedDatabaseId = (DatabaseList.SelectedItem as DatabaseItem)?.Database.Id ?? _initialDatabaseId;
        var selectedCpu = (CpuCombo.SelectedItem as CpuItem)?.Cpu.Function;
        var byFunction = Project.Stations.ToDictionary(s => s.Function);

        _cpus.Clear();
        foreach (var cpu in Project.Stations.Where(s => s.IsCpu))
        {
            var item = new CpuItem { Cpu = cpu };
            var assignment = assignments.FirstOrDefault(a => a.CpuFunction == cpu.Function);
            foreach (var function in assignment?.DistributedFunctions ?? [])
            {
                if (byFunction.TryGetValue(function, out var station) && station.IsDistributed
                    && _cpus.All(c => c.Stations.All(s => s.Station.Function != function)))
                {
                    item.Stations.Add(new StationItem { Station = station });
                }
            }

            _cpus.Add(item);
        }

        _unassigned.Clear();
        var assigned = _cpus.SelectMany(c => c.Stations).Select(s => s.Station.Function).ToHashSet();
        foreach (var station in Project.Stations.Where(s => s.IsDistributed && !assigned.Contains(s.Function)))
        {
            _unassigned.Add(new StationItem { Station = station });
        }

        var choices = new List<CpuChoice> { new("", "— no CPU —") };
        choices.AddRange(_cpus.Select(c => new CpuChoice(c.Cpu.Function, "=" + c.Cpu.Function)));
        _databaseItems.Clear();
        foreach (var database in _databases)
        {
            var cpu = assignments.FirstOrDefault(a => a.PlcDatabaseId == database.Id && _cpus.Any(c => c.Cpu.Function == a.CpuFunction));
            _databaseItems.Add(new DatabaseItem { Database = database, Choices = choices, CpuFunction = cpu?.CpuFunction ?? "" });
        }

        CpuCombo.ItemsSource = null;
        CpuCombo.ItemsSource = _cpus;
        DatabaseList.SelectedItem = _databaseItems.FirstOrDefault(d => d.Database.Id == selectedDatabaseId) ?? _databaseItems.FirstOrDefault();
        if (CpuCombo.SelectedItem is null)
        {
            CpuCombo.SelectedItem = _cpus.FirstOrDefault(c => c.Cpu.Function == selectedCpu) ?? _cpus.FirstOrDefault();
        }

        RefreshPreview();
    }

    // ---------------------------------------------------------------- 1) database ↔ CPU

    private void DatabaseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseList.SelectedItem is DatabaseItem { CpuFunction.Length: > 0 } item)
        {
            CpuCombo.SelectedItem = _cpus.FirstOrDefault(c => c.Cpu.Function == item.CpuFunction);
        }

        RefreshPreview();
    }

    private void DatabaseCpu_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCpuChoice || sender is not ComboBox { DataContext: DatabaseItem changed })
        {
            RefreshPreview();
            return;
        }

        // do not rely on the binding having run before SelectionChanged
        changed.CpuFunction = e.AddedItems.Count > 0 && e.AddedItems[0] is CpuChoice chosen ? chosen.Function : "";
        if (changed.CpuFunction.Length == 0)
        {
            RefreshPreview();
            return;
        }

        // a CPU belongs to one database only: take it away from the other one
        _updatingCpuChoice = true;
        foreach (var other in _databaseItems.Where(d => d != changed && d.CpuFunction == changed.CpuFunction))
        {
            other.CpuFunction = "";
            StatusText.Text = $"={changed.CpuFunction} moved from {other.Name} to {changed.Name}.";
        }

        _updatingCpuChoice = false;
        DatabaseList.SelectedItem = changed;
        CpuCombo.SelectedItem = _cpus.FirstOrDefault(c => c.Cpu.Function == changed.CpuFunction);
        RefreshPreview();
    }

    // ---------------------------------------------------------------- 2) CPU ↔ distributed stations

    private void CpuCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cpu = CpuCombo.SelectedItem as CpuItem;
        AssignedList.ItemsSource = cpu?.Stations;
        AssignedList.Tag = cpu;
        CpuSummary.Text = cpu is null
            ? ""
            : "Own modules: " + string.Join("   ", cpu.Cpu.Modules.GroupBy(m => m.Kind).OrderBy(g => g.Key)
                .Select(g => $"{EplanHardwareMapper.KindName(g.Key)} {g.Count()}"));
        RefreshPreview();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int step)
    {
        if (CpuCombo.SelectedItem is not CpuItem cpu || AssignedList.SelectedItem is not StationItem item)
        {
            return;
        }

        var index = cpu.Stations.IndexOf(item);
        var next = index + step;
        if (next >= 0 && next < cpu.Stations.Count)
        {
            cpu.Stations.Move(index, next);
            RefreshPreview();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (CpuCombo.SelectedItem is CpuItem cpu && AssignedList.SelectedItem is StationItem item)
        {
            cpu.Stations.Remove(item);
            _unassigned.Add(item);
            RefreshPreview();
        }
    }

    private void UnassignedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CpuCombo.SelectedItem is CpuItem cpu && UnassignedList.SelectedItem is StationItem item)
        {
            _unassigned.Remove(item);
            cpu.Stations.Add(item);
            RefreshPreview();
        }
    }

    // drag & drop between "stations of this CPU" and "not assigned", and reordering inside a list

    private void StationList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragSource = sender as ListBox;
    }

    private void StationList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null || !ReferenceEquals(sender, _dragSource))
        {
            return;
        }

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (ItemUnder(_dragSource, e.OriginalSource as DependencyObject) is { } item)
        {
            DragDrop.DoDragDrop(_dragSource, new DataObject(typeof(StationItem), item), DragDropEffects.Move);
        }

        _dragSource = null;
    }

    private void StationList_DragOver(object sender, DragEventArgs e)
    {
        var allowed = e.Data.GetDataPresent(typeof(StationItem)) && (sender != AssignedList || CpuCombo.SelectedItem is CpuItem);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StationList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox target || e.Data.GetData(typeof(StationItem)) is not StationItem item)
        {
            return;
        }

        ObservableCollection<StationItem>? targetList = target == UnassignedList ? _unassigned : (CpuCombo.SelectedItem as CpuItem)?.Stations;
        var sourceList = _unassigned.Contains(item) ? _unassigned : _cpus.Select(c => c.Stations).FirstOrDefault(s => s.Contains(item));
        if (targetList is null || sourceList is null)
        {
            return;
        }

        var index = targetList.Count;
        if (ItemUnder(target, e.OriginalSource as DependencyObject) is { } over && !ReferenceEquals(over, item))
        {
            index = targetList.IndexOf(over);
            if (target.ItemContainerGenerator.ContainerFromItem(over) is ListBoxItem container && e.GetPosition(container).Y > container.ActualHeight / 2)
            {
                index++;
            }
        }

        var oldIndex = sourceList.IndexOf(item);
        sourceList.Remove(item);
        if (ReferenceEquals(sourceList, targetList) && oldIndex < index)
        {
            index--;
        }

        targetList.Insert(Math.Clamp(index, 0, targetList.Count), item);
        target.SelectedItem = item;
        RefreshPreview();
        e.Handled = true;
    }

    private static StationItem? ItemUnder(ListBox list, DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem && !ReferenceEquals(source, list))
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return (source as ListBoxItem)?.DataContext as StationItem;
    }

    // ---------------------------------------------------------------- 3) preview & result

    private void RefreshPreview()
    {
        var database = DatabaseList.SelectedItem as DatabaseItem;
        var warnings = new List<string>();
        PreviewGrid.ItemsSource = null;
        if (Project is null || database is null)
        {
            PreviewTitle.Text = "3  Preview";
            WarningList.ItemsSource = null;
            return;
        }

        var cpu = _cpus.FirstOrDefault(c => c.Cpu.Function == database.CpuFunction);
        ImportButton.Content = $"Import into {database.Name}";
        if (cpu is null)
        {
            PreviewTitle.Text = $"3  Preview: {database.Name} – no CPU";
            warnings.Add($"Choose a CPU for {database.Name} in step 1.");
            WarningList.ItemsSource = warnings;
            return;
        }

        var plan = EplanHardwareMapper.BuildPlan(Project, new EplanCpuAssignment
        {
            CpuFunction = cpu.Cpu.Function,
            DistributedFunctions = cpu.Stations.Select(s => s.Station.Function).ToList()
        });
        var stations = cpu.Stations.Count == 0 ? "" : " + =" + string.Join(", =", cpu.Stations.Select(s => s.Station.Function));
        PreviewTitle.Text = $"3  Preview: {database.Name} ← ={cpu.Cpu.Function}{stations}   ({plan.Channels.Count} rows)";
        PreviewGrid.ItemsSource = plan.Channels.Select(c => new PreviewRow(
            EplanHardwareMapper.KindName(c.Kind), c.ModuleNumber, c.ModuleName, c.Port, c.StationFunction,
            c.Channel.SignalName, c.Channel.ConnectedTo, c.Channel.Schematic?.GlobalPage, c.Channel.Schematic?.Display ?? "")).ToList();

        warnings.AddRange(plan.Warnings);
        if (_unassigned.Count > 0)
        {
            warnings.Add($"Not assigned to any CPU: ={string.Join(", =", _unassigned.Select(s => s.Station.Function))}");
        }

        WarningList.ItemsSource = warnings;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ImportDatabaseId = null;
        DialogResult = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DatabaseList.SelectedItem is not DatabaseItem database || database.CpuFunction.Length == 0)
        {
            MessageBox.Show(this, "Select a database and choose its CPU in step 1.", "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportDatabaseId = database.Database.Id;
        DialogResult = true;
    }
}
