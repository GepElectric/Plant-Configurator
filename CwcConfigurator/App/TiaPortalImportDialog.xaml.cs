using System.IO;
using System.Windows;
using CwcConfigurator.Services;
using Microsoft.Win32;

namespace CwcConfigurator.App;

public partial class TiaPortalImportDialog : Window
{
    private readonly TiaPortalExportImporter _importer = new();
    private List<TiaPortalImportFile> _files = [];
    private bool _allSelected = true;

    public string FolderPath => FolderText.Text.Trim();
    public IReadOnlyList<TiaPortalImportFile> SelectedFiles => _files.Where(file => file.IsSelected).ToList();

    public TiaPortalImportDialog(string initialFolder = "")
    {
        InitializeComponent();
        FolderText.Text = initialFolder;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose TIA Portal export folder",
            InitialDirectory = Directory.Exists(FolderPath) ? FolderPath : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderText.Text = dialog.FolderName;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(FolderPath))
        {
            MessageBox.Show(this, "Choose existing folder.", "TIA Portal import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _files = _importer.Scan(FolderPath);
        if (_files.Count == 0)
        {
            MessageBox.Show(this, "No supported TIA Portal export files found in this folder.", "TIA Portal import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FilesList.ItemsSource = _files;
        _allSelected = true;
        ToggleAllButton.Content = "Deselect all";
        DetectedText.Text = $"Recognized files in {FolderPath}";
        FolderPage.Visibility = Visibility.Collapsed;
        FilesPage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        FilesPage.Visibility = Visibility.Collapsed;
        FolderPage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Visible;
        FinishButton.Visibility = Visibility.Collapsed;
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFiles.Count == 0)
        {
            MessageBox.Show(this, "Select at least one file to import.", "TIA Portal import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void ToggleAll_Click(object sender, RoutedEventArgs e)
    {
        _allSelected = !_allSelected;
        foreach (var file in _files)
        {
            file.IsSelected = _allSelected;
        }

        ToggleAllButton.Content = _allSelected ? "Deselect all" : "Select all";
        FilesList.Items.Refresh();
    }
}
