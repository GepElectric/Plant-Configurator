using CwcConfigurator.Domain;

namespace CwcConfigurator.App.Settings;

public partial class ProjectPathsSettingsView : SettingsEditorViewBase
{
    private readonly ProjectPaths _paths;

    public ProjectPathsSettingsView(ProjectPaths paths)
    {
        InitializeComponent();
        _paths = paths;
        OutputPathText.Text = paths.OutputFolder;
        TiaExportPathText.Text = paths.TiaPortalExportFolder;
        UnifiedJsConnectorFolderText.Text = paths.UnifiedJsConnectorFolder;
        UnifiedJsConnectorServerPathText.Text = paths.UnifiedJsConnectorServerPath;
        PlcPathText.Text = paths.PlcToConfigSourceFolder;
        RPathText.Text = paths.RSourceFolder;
    }

    public override bool Apply()
    {
        _paths.OutputFolder = OutputPathText.Text.Trim();
        _paths.TiaPortalExportFolder = TiaExportPathText.Text.Trim();
        _paths.UnifiedJsConnectorFolder = UnifiedJsConnectorFolderText.Text.Trim();
        _paths.UnifiedJsConnectorServerPath = UnifiedJsConnectorServerPathText.Text.Trim();
        _paths.PlcToConfigSourceFolder = PlcPathText.Text.Trim();
        _paths.RSourceFolder = RPathText.Text.Trim();
        return true;
    }
}
