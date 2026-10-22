using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// "Program" tab of the simulator: the objects of one PLC's plant model, their variants, port bindings
/// (signals, links, constants), parameters and characteristic tables.
/// </summary>
public partial class ProgramView : UserControl
{
    private SimulationController? _controller;
    private string _plc = "";
    private ObjectConfig? _current;
    private List<PortRow> _ports = [];
    private bool _selfChange;

    public ProgramView()
    {
        InitializeComponent();
        TypeBox.ItemsSource = ObjectCatalog.All;
        TypeBox.SelectedIndex = 0;
    }

    private ProcessModel? Model => _controller is not null && _controller.Models.TryGetValue(_plc, out var model) ? model : null;

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += () =>
        {
            if (!_selfChange)
            {
                ReloadList();
            }

            UpdateProblems();
        };
        controller.FileStateChanged += () => ModelPathText.Text = controller.ModelPath is null ? "Model not saved yet." : "Model file: " + controller.ModelPath;
        ModelPathText.Text = controller.ModelPath is null ? "Model not saved yet." : "Model file: " + controller.ModelPath;
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        ReloadList();
    }

    /// <summary>Refreshes the live values of the shown ports (called from the window timer).</summary>
    public void RefreshLive()
    {
        if (_controller is null || _current is null || !IsVisible)
        {
            return;
        }

        var instance = _controller.Engine.Find(_plc, _current.Name);
        foreach (var row in _ports)
        {
            row.Refresh(instance);
        }
    }

    private void ReloadList()
    {
        var selected = _current?.Name;
        ObjectList.ItemsSource = Model?.Objects
            .OrderBy(item => ObjectCatalog.Find(item.Type)?.Category ?? "~")
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ObjectItem(item))
            .ToList();
        var restore = ObjectList.Items.Cast<ObjectItem>().FirstOrDefault(item => item.Config.Name == selected);
        ObjectList.SelectedItem = restore;
        if (restore is null)
        {
            ShowObject(null);
        }

        UpdateProblems();
    }

    private void UpdateProblems()
    {
        if (_controller is null)
        {
            return;
        }

        var problems = _controller.Engine.Problems.Where(problem => problem.StartsWith(_plc + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        ProblemsText.Text = problems.Count == 0 ? "" : string.Join(Environment.NewLine, problems.Take(10));
    }

    private void ObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowObject((ObjectList.SelectedItem as ObjectItem)?.Config);
    }

    private void ShowObject(ObjectConfig? config)
    {
        _current = config;
        DetailsPanel.IsEnabled = config is not null;
        var info = config is null ? null : ObjectCatalog.Find(config.Type);
        if (config is null || info is null || _controller is null)
        {
            NameBox.Text = "";
            DescriptionBox.Text = "";
            TypeText.Text = config is null ? "" : $"Unknown type '{config.Type}'.";
            OptionsList.ItemsSource = null;
            PortsGrid.ItemsSource = null;
            ParamsGrid.ItemsSource = null;
            TablesList.ItemsSource = null;
            _ports = [];
            return;
        }

        NameBox.Text = config.Name;
        DescriptionBox.Text = config.Description;
        TypeText.Text = $"{info.Title} — {info.Description}";
        // Only the options and ports of the chosen variant are shown (e.g. no Setpoint for OPEN/CLOSE control,
        // no Esd without an ESD valve); hidden ports keep their binding but work as not connected.
        OptionsList.ItemsSource = info.Options.Where(option => info.IsActive(option.ShownWhen, config))
            .Select(option => new OptionRow(option, config, VariantChanged)).ToList();
        _ports = info.Ports
            .Where(port => info.IsActive(port.ShownWhen, config))
            .OrderBy(port => port.Direction)
            .Select(port => new PortRow(port, config, BindingSuggestions.For(port, _plc, _controller), StructureChanged))
            .ToList();
        PortsGrid.ItemsSource = _ports;
        ParamsGrid.ItemsSource = info.Params.Select(param => new ParamRow(param, config, Changed)).ToList();
        TablesList.ItemsSource = info.Tables.Select(table => new TableRow(table, config, Changed)).ToList();
        RefreshLive();
    }

    private void Changed() => _controller?.MarkDirty();

    private void VariantChanged()
    {
        StructureChanged();
        var config = _current;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(config, _current))
            {
                ShowObject(config);
            }
        });
    }

    private void StructureChanged()
    {
        if (_controller is null)
        {
            return;
        }

        _selfChange = true;
        try
        {
            _controller.Rebuild();
        }
        finally
        {
            _selfChange = false;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TypeBox.SelectedItem is not ObjectTypeInfo info)
        {
            return;
        }

        var name = UniqueName(model, info.Type);
        var top = model.Objects.Count == 0 ? 20 : model.Objects.Max(item => item.Y) + 150;
        model.Objects.Add(new ObjectConfig { Name = name, Type = info.Type, Description = info.Title, X = 20, Y = top });
        _controller!.Rebuild();
        Select(name);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _current is null)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete object '{_current.Name}'?", "Simulator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        model.Objects.Remove(_current);
        _current = null;
        _controller!.Rebuild();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _current is null)
        {
            return;
        }

        var copy = new ObjectConfig
        {
            Name = UniqueName(model, _current.Name),
            Type = _current.Type,
            Description = _current.Description,
            Options = new(_current.Options, StringComparer.OrdinalIgnoreCase),
            Params = new(_current.Params, StringComparer.OrdinalIgnoreCase),
            Tables = _current.Tables.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            Ports = new(_current.Ports.Where(pair => ObjectCatalog.Find(_current.Type)?.Port(pair.Key)?.Direction == PortDirection.Input), StringComparer.OrdinalIgnoreCase),
            X = _current.X + 30,
            Y = _current.Y + 30
        };
        model.Objects.Add(copy);
        _controller!.Rebuild();
        Select(copy.Name);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _controller?.Session(_plc) is not { } session)
        {
            return;
        }

        var result = ModelGenerator.Generate(session, model);
        if (result.Objects.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, string.Join(Environment.NewLine, result.Report), "Generate from signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine, result.Report.Take(40)) + (result.Report.Count > 40 ? $"{Environment.NewLine}… ({result.Report.Count - 40} more)" : "");
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Add {result.Objects.Count} objects to {_plc}: {string.Join(", ", result.Objects.Select(item => item.Name))}?{Environment.NewLine}{Environment.NewLine}{preview}",
                "Generate from signals", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        model.Objects.AddRange(result.Objects);
        _controller.Rebuild();
    }

    /// <summary>Save: the window's Save model (asks for a file when none is chosen yet).</summary>
    public event RoutedEventHandler? SaveRequested;

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, e);

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (_controller.Dirty && MessageBox.Show(Window.GetWindow(this)!, "Discard the unsaved model changes and reload the file?", "Simulator",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_controller.Reload())
        {
            MessageBox.Show(Window.GetWindow(this)!, "There is no saved model file yet.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ModelPath is not { } path || !File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "Save the model first.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Rename();
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => Rename();

    private void Rename()
    {
        if (_current is null || Model is not { } model || _controller is null)
        {
            return;
        }

        var name = NameBox.Text.Trim().Replace("/", "_").Replace(".", "_").Replace("@", "");
        if (name.Length == 0 || name == _current.Name)
        {
            NameBox.Text = _current.Name;
            return;
        }

        if (model.Find(name) is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"An object named '{name}' already exists.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Text = _current.Name;
            return;
        }

        var old = _current.Name;
        foreach (var (plc, other) in _controller.Models)
        {
            var prefix = plc.Equals(_plc, StringComparison.OrdinalIgnoreCase) ? $"@{old}." : $"@{_plc}/{old}.";
            var replacement = plc.Equals(_plc, StringComparison.OrdinalIgnoreCase) ? $"@{name}." : $"@{_plc}/{name}.";
            foreach (var item in other.Objects)
            {
                foreach (var key in item.Ports.Keys.ToList())
                {
                    if (item.Ports[key].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Ports[key] = replacement + item.Ports[key][prefix.Length..];
                    }
                }
            }
        }

        _current.Name = name;
        _controller.Rebuild();
        Select(name);
    }

    private void DescriptionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_current is not null && _current.Description != DescriptionBox.Text)
        {
            _current.Description = DescriptionBox.Text;
            Changed();
        }
    }

    private void Select(string name)
    {
        ReloadList();
        ObjectList.SelectedItem = ObjectList.Items.Cast<ObjectItem>().FirstOrDefault(item => item.Config.Name == name);
        ObjectList.ScrollIntoView(ObjectList.SelectedItem);
    }

    private static string UniqueName(ProcessModel model, string baseName)
    {
        var stem = baseName.TrimEnd("0123456789".ToCharArray());
        for (var index = 1; ; index++)
        {
            var name = stem + index;
            if (model.Find(name) is null)
            {
                return name;
            }
        }
    }

    private sealed record ObjectItem(ObjectConfig Config)
    {
        public override string ToString() => $"{Config.Name}   ({ObjectCatalog.Find(Config.Type)?.Title ?? Config.Type})";
    }
}
