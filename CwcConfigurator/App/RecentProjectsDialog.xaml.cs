using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CwcConfigurator.Services;
using Microsoft.Win32;

namespace CwcConfigurator.App;

public partial class RecentProjectsDialog : Window
{
    private readonly RecentProjectsService _service;
    private readonly ObservableCollection<RecentProjectEntry> _entries;

    public RecentProjectsDialog(RecentProjectsService service)
    {
        InitializeComponent();
        _service = service;
        _entries = new ObservableCollection<RecentProjectEntry>(_service.Load());
        RecentList.ItemsSource = _entries;
    }

    public string? SelectedPath { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Plant Configurator project (*.plantcfg)|*.plantcfg|JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "plantcfg"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SelectedPath = dialog.FileName;
            DialogResult = true;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentProjectEntry entry)
        {
            return;
        }

        SelectedPath = entry.Path;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentProjectEntry entry)
        {
            return;
        }

        _service.Remove(entry.Path);
        _entries.Remove(entry);
    }

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Open_Click(sender, e);
    }
}
