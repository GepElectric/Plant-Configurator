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
/// Reads an EPLAN .epj, lets the user assign distributed stations to CPUs (drag &amp; drop, order = module numbering)
/// and CPUs to PLC databases, and previews what will be written. Applying is done by the caller.
/// </summary>
public partial class EplanImportDialog : Window
{
    public sealed record DatabaseChoice(string Id, string Name);

    public sealed class StationItem
    {
        public required EplanStation Station { get; init; }
        public string Display => $"={Station.Function}  -{Station.HeadDeviceTag}  ({Station.Modules.Count} modules)";
    }

    public sealed class CpuCard : INotifyPropertyChanged
    {
        private string _plcDatabaseId = "";

        public required EplanStation Cpu { get; init; }
        public required IReadOnlyList<DatabaseChoice> Databases { get; init; }
        public ObservableCollection<StationItem> Stations { get; } = [];
        public string Header => $"={Cpu.Function}  -{Cpu.HeadDeviceTag}  {Cpu.HeadType}";
        public string Summary => string.Join("   ", Cpu.Modules.GroupBy(m => m.Kind).OrderBy(g => g.Key)
            .Select(g => $"{EplanHardwareMapper.KindName(g.Key)} {g.Count()}"));

        public string PlcDatabaseId
        {
            get => _plcDatabaseId;
            set
            {
                _plcDatabaseId = value ?? "";
                OnPropertyChanged();
            }
        }

        public EplanCpuAssignment ToAssignment() => new()
        {
            CpuFunction = Cpu.Function,
            PlcDatabaseId = PlcDatabaseId,
            DistributedFunctions = Stations.Select(s => s.Station.Function).ToList()
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed record PreviewRow(string Tab, int ModuleNumber, string ModuleName, int? Port, string Station,
        string Signal, string ConnectedTo, int? PdfPage, string SchematicPage);

    private readonly IReadOnlyList<PlcDatabase> _databases;
    private readonly IReadOnlyList<EplanCpuAssignment> _previous;
    private readonly ObservableCollection<StationItem> _unassigned = [];
    private readonly ObservableCollection<CpuCard> _cards = [];
    private CpuCard? _selected;
    private Point _dragStart;
    private ListBox? _dragSource;

    public EplanProject? Project { get; private set; }
    public IReadOnlyList<EplanCpuAssignment> Assignments => _cards.Select(c => c.ToAssignment()).ToList();

    public EplanImportDialog(IReadOnlyList<PlcDatabase> databases, EplanImportConfig remembered)
    {
        InitializeComponent();
        _databases = databases;
        _previous = remembered.Assignments;
        UnassignedList.ItemsSource = _unassigned;
        CpuCards.ItemsSource = _cards;
        PathText.Text = remembered.LastEpjPath;
        ReadButton.IsEnabled = File.Exists(remembered.LastEpjPath);
        StatusText.Text = "Choose the EPLAN PXF export (.epj) and press Read.";
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
            Fill(EplanHardwareMapper.AutoMap(Project, _databases, _previous));
            var cpus = Project.Stations.Count(s => s.IsCpu);
            StatusText.Text = $"{Project.ProjectName}: {Project.Pages.Count} pages, {cpus} CPU, " +
                              $"{Project.Stations.Count - cpus} distributed stations, " +
                              $"{Project.Stations.Sum(s => s.Modules.Sum(m => m.Channels.Count))} channels. Check the assignment, then Apply.";
            AutoMapButton.IsEnabled = true;
            ApplyButton.IsEnabled = true;
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

    private void AutoMap_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null)
        {
            return;
        }

        // automap from scratch (ignores what is on screen), the user then corrects it
        Fill(EplanHardwareMapper.AutoMap(Project, _databases));
        StatusText.Text = "Automap done – check every CPU before Apply.";
    }

    private void Fill(IReadOnlyList<EplanCpuAssignment> assignments)
    {
        if (Project is null)
        {
            return;
        }

        var choices = new List<DatabaseChoice> { new("", "— do not import —") };
        choices.AddRange(_databases.Select(d => new DatabaseChoice(d.Id, d.Name)));

        _cards.Clear();
        _unassigned.Clear();
        var byFunction = Project.Stations.ToDictionary(s => s.Function);
        foreach (var cpu in Project.Stations.Where(s => s.IsCpu))
        {
            var assignment = assignments.FirstOrDefault(a => a.CpuFunction == cpu.Function);
            var card = new CpuCard { Cpu = cpu, Databases = choices, PlcDatabaseId = assignment?.PlcDatabaseId ?? "" };
            foreach (var function in assignment?.DistributedFunctions ?? [])
            {
                if (byFunction.TryGetValue(function, out var station))
                {
                    card.Stations.Add(new StationItem { Station = station });
                }
            }

            _cards.Add(card);
        }

        var assigned = _cards.SelectMany(c => c.Stations).Select(s => s.Station.Function).ToHashSet();
        foreach (var station in Project.Stations.Where(s => s.IsDistributed && !assigned.Contains(s.Function)))
        {
            _unassigned.Add(new StationItem { Station = station });
        }

        _selected = _cards.FirstOrDefault();
        RefreshPreview();
    }

    // ---------------------------------------------------------------- preview

    private void CpuCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CpuCard card })
        {
            _selected = card;
            RefreshPreview();
        }
    }

    private void CpuDatabase_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (Project is null || _selected is null)
        {
            PreviewGrid.ItemsSource = null;
            WarningList.ItemsSource = null;
            PreviewTitle.Text = "Preview";
            return;
        }

        var plan = EplanHardwareMapper.BuildPlan(Project, _selected.ToAssignment());
        var target = _databases.FirstOrDefault(d => d.Id == _selected.PlcDatabaseId)?.Name ?? "not imported";
        PreviewTitle.Text = $"Preview: ={_selected.Cpu.Function} → {target}  ({plan.Channels.Count} rows)";
        PreviewGrid.ItemsSource = plan.Channels.Select(c => new PreviewRow(
            EplanHardwareMapper.KindName(c.Kind), c.ModuleNumber, c.ModuleName, c.Port, c.StationFunction,
            c.Channel.SignalName, c.Channel.ConnectedTo, c.Channel.Schematic?.GlobalPage, c.Channel.Schematic?.Display ?? "")).ToList();

        var warnings = plan.Warnings.ToList();
        if (_selected.PlcDatabaseId.Length == 0)
        {
            warnings.Insert(0, "No PLC database chosen – this CPU will not be imported.");
        }

        var conflict = _cards.Where(c => c != _selected && c.PlcDatabaseId.Length > 0 && c.PlcDatabaseId == _selected.PlcDatabaseId).ToList();
        if (conflict.Count > 0)
        {
            warnings.Insert(0, $"The same PLC database is chosen for ={string.Join(", =", conflict.Select(c => c.Cpu.Function))}.");
        }

        WarningList.ItemsSource = warnings;
    }

    // ---------------------------------------------------------------- drag & drop

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
        e.Effects = e.Data.GetDataPresent(typeof(StationItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StationList_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox target || e.Data.GetData(typeof(StationItem)) is not StationItem item)
        {
            return;
        }

        var targetList = CollectionOf(target);
        var sourceList = _unassigned.Contains(item) ? _unassigned : _cards.Select(c => c.Stations).First(s => s.Contains(item));

        var index = targetList.Count;
        if (ItemUnder(target, e.OriginalSource as DependencyObject) is { } over && !ReferenceEquals(over, item))
        {
            index = targetList.IndexOf(over);
            var container = (ListBoxItem)target.ItemContainerGenerator.ContainerFromItem(over);
            if (container is not null && e.GetPosition(container).Y > container.ActualHeight / 2)
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

    private ObservableCollection<StationItem> CollectionOf(ListBox list) =>
        list.Tag is CpuCard card ? card.Stations : _unassigned;

    private static StationItem? ItemUnder(ListBox list, DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem && !ReferenceEquals(source, list))
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return (source as ListBoxItem)?.DataContext as StationItem;
    }

    // ---------------------------------------------------------------- order buttons

    private static ListBox? StationListOf(object sender) =>
        sender is FrameworkElement element ? FindChild<ListBox>(FindAncestor<Border>(element)) : null;

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int step)
    {
        if ((sender as FrameworkElement)?.Tag is not CpuCard card || StationListOf(sender)?.SelectedItem is not StationItem item)
        {
            return;
        }

        var index = card.Stations.IndexOf(item);
        var next = index + step;
        if (next < 0 || next >= card.Stations.Count)
        {
            return;
        }

        card.Stations.Move(index, next);
        _selected = card;
        RefreshPreview();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CpuCard card || StationListOf(sender)?.SelectedItem is not StationItem item)
        {
            return;
        }

        card.Stations.Remove(item);
        _unassigned.Add(item);
        _selected = card;
        RefreshPreview();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && node is not T)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as T;
    }

    private static T? FindChild<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- apply

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null)
        {
            return;
        }

        var chosen = _cards.Where(c => c.PlcDatabaseId.Length > 0).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Choose a PLC database for at least one CPU.", "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (chosen.GroupBy(c => c.PlcDatabaseId).Any(g => g.Count() > 1))
        {
            MessageBox.Show(this, "Two CPUs are mapped to the same PLC database.", "EPLAN import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_unassigned.Count > 0
            && MessageBox.Show(this,
                $"{_unassigned.Count} distributed station(s) are not assigned to any CPU and will not be imported:\n" +
                string.Join("\n", _unassigned.Select(s => "  =" + s.Station.Function)) + "\n\nContinue?",
                "EPLAN import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        DialogResult = true;
    }
}
