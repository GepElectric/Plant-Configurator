using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class TableDesignDialog : Window
{
    private static readonly string[] ColorPalette =
    [
        "White", "Yellow", "Red", "LimeGreen", "DodgerBlue", "Orange", "Gray", "Black", "Transparent",
        "#F6F8FA", "#EFF6FF", "#F0FDF4", "#FFF7ED", "#FDF2F8",
        "#C8E6C9", "#FFF9C4", "#FFE0B2", "#FFCDD2", "#EF9A9A"
    ];

    public TableDesignSettings TableDesign { get; private set; }
    public DbConditionalColorSettings ConditionalColors { get; private set; }

    public TableDesignDialog(TableDesignSettings tableDesign, DbConditionalColorSettings conditionalColors)
    {
        InitializeComponent();
        TableDesign = new TableDesignSettings
        {
            Layout = tableDesign.Layout,
            ConditionalColoringEnabled = tableDesign.ConditionalColoringEnabled
        };
        ConditionalColors = conditionalColors.Clone();
        LayoutCombo.ItemsSource = TableDesignPalette.LayoutNames;
        LayoutCombo.SelectedItem = TableDesign.Layout;
        ConditionalColoringCheck.IsChecked = TableDesign.ConditionalColoringEnabled;
        foreach (var combo in GetColorCombos())
        {
            combo.ItemsSource = ColorPalette;
        }

        DbAlmUsedColorCombo.Text = ConditionalColors.DbAlarmUsedColor;
        DbAlmAlarmColorCombo.Text = ConditionalColors.DbAlarmAlarmColor;
        DbAlmStartInterlockColorCombo.Text = ConditionalColors.DbAlarmStartInterlockColor;
        DbAlmNormalShutdownColorCombo.Text = ConditionalColors.DbAlarmNormalShutdownColor;
        DbAlmQuickShutdownColorCombo.Text = ConditionalColors.DbAlarmQuickShutdownColor;
        DbAlmEmergencyShutdownColorCombo.Text = ConditionalColors.DbAlarmEmergencyShutdownColor;
        DbParUsedColorCombo.Text = ConditionalColors.DbParameterUsedColor;
        DbParActiveValueColorCombo.Text = ConditionalColors.DbParameterActiveValueColor;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        TableDesign = new TableDesignSettings
        {
            Layout = LayoutCombo.SelectedItem?.ToString() ?? "Classic",
            ConditionalColoringEnabled = ConditionalColoringCheck.IsChecked == true
        };
        ConditionalColors = new DbConditionalColorSettings
        {
            DbAlarmUsedColor = ReadColor(DbAlmUsedColorCombo, "LimeGreen"),
            DbAlarmColorRequiresUsed = ConditionalColors.DbAlarmColorRequiresUsed,
            DbAlarmAlarmColor = ReadColor(DbAlmAlarmColorCombo, "White"),
            DbAlarmStartInterlockColor = ReadColor(DbAlmStartInterlockColorCombo, "Yellow"),
            DbAlarmNormalShutdownColor = ReadColor(DbAlmNormalShutdownColorCombo, "Orange"),
            DbAlarmQuickShutdownColor = ReadColor(DbAlmQuickShutdownColorCombo, "Red"),
            DbAlarmEmergencyShutdownColor = ReadColor(DbAlmEmergencyShutdownColorCombo, "Red"),
            DbParameterUsedColor = ReadColor(DbParUsedColorCombo, "LimeGreen"),
            DbParameterActiveValueColor = ReadColor(DbParActiveValueColorCombo, "Yellow")
        };
        DialogResult = true;
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
    {
        return string.IsNullOrWhiteSpace(combo.Text) ? fallback : combo.Text.Trim();
    }
}

public static class TableDesignPalette
{
    public static IReadOnlyList<string> LayoutNames { get; } =
    [
        "Classic", "Blue", "Green", "Amber", "Rose", "Slate", "Mint", "Lavender", "Steel", "High contrast"
    ];

    public static (string Row, string Alternate) Resolve(string layout)
    {
        return layout switch
        {
            "Blue" => ("#FFFFFF", "#EFF6FF"),
            "Green" => ("#FFFFFF", "#F0FDF4"),
            "Amber" => ("#FFFFFF", "#FFF7ED"),
            "Rose" => ("#FFFFFF", "#FDF2F8"),
            "Slate" => ("#FFFFFF", "#F1F5F9"),
            "Mint" => ("#FFFFFF", "#ECFDF5"),
            "Lavender" => ("#FFFFFF", "#F5F3FF"),
            "Steel" => ("#FFFFFF", "#F3F4F6"),
            "High contrast" => ("#FFFFFF", "#E5E7EB"),
            _ => ("#FFFFFF", "#F6F8FA")
        };
    }
}
