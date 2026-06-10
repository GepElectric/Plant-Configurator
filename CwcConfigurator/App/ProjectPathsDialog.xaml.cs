using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class ProjectPathsDialog : Window
{
    public string PlcSourceFolder => PlcPathText.Text.Trim();
    public string RSourceFolder => RPathText.Text.Trim();
    public string OutputFolder => OutputPathText.Text.Trim();
    public string TiaPortalExportFolder => TiaExportPathText.Text.Trim();
    public string UnifiedJsConnectorFolder => UnifiedJsConnectorFolderText.Text.Trim();
    public string UnifiedJsConnectorServerPath => UnifiedJsConnectorServerPathText.Text.Trim();

    public ProjectPathsDialog(ProjectPaths paths)
    {
        InitializeComponent();
        PlcPathText.Text = paths.PlcToConfigSourceFolder;
        RPathText.Text = paths.RSourceFolder;
        OutputPathText.Text = paths.OutputFolder;
        TiaExportPathText.Text = paths.TiaPortalExportFolder;
        UnifiedJsConnectorFolderText.Text = paths.UnifiedJsConnectorFolder;
        UnifiedJsConnectorServerPathText.Text = paths.UnifiedJsConnectorServerPath;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
