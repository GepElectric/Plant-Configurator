using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App.Settings;

public partial class TableDesignSettingsView : SettingsEditorViewBase
{
    private static readonly string[] ColorPalette =
    [
        "White", "Yellow", "Red", "LimeGreen", "DodgerBlue", "Orange", "Gray", "Black", "Transparent",
        "#F6F8FA", "#EFF6FF", "#F0FDF4", "#FFF7ED", "#FDF2F8",
        "#C8E6C9", "#FFF9C4", "#FFE0B2", "#FFCDD2", "#EF9A9A"
    ];

    private readonly Action<TableDesignSettings, DbConditionalColorSettings> _commit;

    public TableDesignSettingsView(
        TableDesignSettings tableDesign,
        DbConditionalColorSettings conditionalColors,
        Action<TableDesignSettings, DbConditionalColorSettings> commit)
    {
        InitializeComponent();
        _commit = commit;

        LayoutCombo.ItemsSource = TableDesignPalette.LayoutNames;
        LayoutCombo.SelectedItem = tableDesign.Layout;
        ConditionalColoringCheck.IsChecked = tableDesign.ConditionalColoringEnabled;
        foreach (var combo in GetColorCombos())
        {
            combo.ItemsSource = ColorPalette;
        }
        DbAlmUsedColorCombo.Text = conditionalColors.DbAlarmUsedColor;
        DbAlmAlarmColorCombo.Text = conditionalColors.DbAlarmAlarmColor;
        DbAlmStartInterlockColorCombo.Text = conditionalColors.DbAlarmStartInterlockColor;
        DbAlmNormalShutdownColorCombo.Text = conditionalColors.DbAlarmNormalShutdownColor;
        DbAlmQuickShutdownColorCombo.Text = conditionalColors.DbAlarmQuickShutdownColor;
        DbAlmEmergencyShutdownColorCombo.Text = conditionalColors.DbAlarmEmergencyShutdownColor;
        DbParUsedColorCombo.Text = conditionalColors.DbParameterUsedColor;
        DbParActiveValueColorCombo.Text = conditionalColors.DbParameterActiveValueColor;

        _seedColors = conditionalColors.Clone();
    }

    private readonly DbConditionalColorSettings _seedColors;

    public override bool Apply()
    {
        var newDesign = new TableDesignSettings
        {
            Layout = LayoutCombo.SelectedItem?.ToString() ?? "Classic",
            ConditionalColoringEnabled = ConditionalColoringCheck.IsChecked == true
        };
        var newColors = new DbConditionalColorSettings
        {
            DbAlarmUsedColor = ReadColor(DbAlmUsedColorCombo, "LimeGreen"),
            DbAlarmColorRequiresUsed = _seedColors.DbAlarmColorRequiresUsed,
            DbAlarmAlarmColor = ReadColor(DbAlmAlarmColorCombo, "White"),
            DbAlarmStartInterlockColor = ReadColor(DbAlmStartInterlockColorCombo, "Yellow"),
            DbAlarmNormalShutdownColor = ReadColor(DbAlmNormalShutdownColorCombo, "Orange"),
            DbAlarmQuickShutdownColor = ReadColor(DbAlmQuickShutdownColorCombo, "Red"),
            DbAlarmEmergencyShutdownColor = ReadColor(DbAlmEmergencyShutdownColorCombo, "Red"),
            DbParameterUsedColor = ReadColor(DbParUsedColorCombo, "LimeGreen"),
            DbParameterActiveValueColor = ReadColor(DbParActiveValueColorCombo, "Yellow")
        };
        _commit(newDesign, newColors);
        return true;
    }

    private IEnumerable<ComboBox> GetColorCombos()
    {
        yield return DbAlmUsedColorCombo;
        yield return DbAlmAlarmColorCombo;
        yield return DbAlmStartInterlockColorCombo;
        yield return DbAlmNormalShutdownColorCombo;
        yield return DbAlmQuickShutdownColorCombo;
        yield return DbAlmEmergencyShutdownColorCombo;
        yield return DbParUsedColorCombo;
        yield return DbParActiveValueColorCombo;
    }

    private static string ReadColor(ComboBox combo, string fallback)
        => string.IsNullOrWhiteSpace(combo.Text) ? fallback : combo.Text.Trim();
}
