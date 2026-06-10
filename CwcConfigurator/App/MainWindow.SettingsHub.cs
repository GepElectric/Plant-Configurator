using System.Windows;
using CwcConfigurator.App.Settings;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Validation;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private void SettingsHub_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("Open or create a project first to edit settings.");
            return;
        }

        var hub = new SettingsHubDialog(
            viewFactory: CreateSettingsView,
            onApplied: OnSettingsApplied,
            readBilingual: () => _project.Multilingual,
            writeBilingual: SetBilingualFromHub)
        {
            Owner = this
        };

        _editorOwnerOverride = hub;
        try
        {
            hub.ShowDialog();
        }
        finally
        {
            _editorOwnerOverride = null;
        }
    }

    private SettingsEditorViewBase? CreateSettingsView(SettingsHubKey key)
    {
        return key switch
        {
            SettingsHubKey.ProjectPaths => new ProjectPathsSettingsView(_project.Paths),
            SettingsHubKey.TableDesign => new TableDesignSettingsView(
                _project.TableDesign,
                _project.DbCatalog.ConditionalColors,
                CommitTableDesign),
            SettingsHubKey.DbNamesAndNumbers => new DbDataBlockSettingsView(
                GetDataBlockNameSettings(),
                ApplyDataBlockNameSettings),
            SettingsHubKey.DbDiPlcNames => BuildDbDiPlcNamesView(),
            SettingsHubKey.DbDoPlcNames => BuildDbDoPlcNamesView(),
            SettingsHubKey.DbAoPlcNames => BuildDbAoPlcNamesView(),
            SettingsHubKey.DbMeasPlcNames => BuildGeneratedPlcNamesView(
                "DB MEAS",
                _project.DbCatalog.MeasRows,
                _project.DbCatalog.MeasSystemNameRows,
                _project.DbCatalog.MeasStructTypePrefix,
                _project.DbCatalog.MeasSimulatorTypePrefix,
                showSimulatorPrefix: true,
                rows => _project.DbCatalog.MeasSystemNameRows = rows.ToList(),
                (sp, simp) =>
                {
                    _project.DbCatalog.MeasStructTypePrefix = sp;
                    _project.DbCatalog.MeasSimulatorTypePrefix = simp;
                }),
            SettingsHubKey.DbAlmPlcNames => BuildGeneratedPlcNamesView(
                "DB ALM",
                _project.DbCatalog.AlarmRows,
                _project.DbCatalog.AlarmSystemNameRows,
                _project.DbCatalog.AlarmStructTypePrefix,
                "",
                showSimulatorPrefix: false,
                rows => _project.DbCatalog.AlarmSystemNameRows = rows.ToList(),
                (sp, _) => _project.DbCatalog.AlarmStructTypePrefix = sp),
            SettingsHubKey.DbParPlcNames => BuildGeneratedPlcNamesView(
                "DB PAR",
                _project.DbCatalog.ParameterRows,
                _project.DbCatalog.ParameterSystemNameRows,
                _project.DbCatalog.ParameterStructTypePrefix,
                "",
                showSimulatorPrefix: false,
                rows => _project.DbCatalog.ParameterSystemNameRows = rows.ToList(),
                (sp, _) => _project.DbCatalog.ParameterStructTypePrefix = sp),

            SettingsHubKey.DbDiValidationColors => BuildDbDiValidationView(),
            SettingsHubKey.DbDoValidationColors => BuildDbValidationView("DB DO", [], _project.DbCatalog.DoTextLengths,
                showColors: false,
                ["DB_DO."],
                (colors, lengths) =>
                {
                    _project.DbCatalog.DoTextLengths = lengths;
                    _dbDoView?.Refresh();
                    DbDoGrid.Items.Refresh();
                }),
            SettingsHubKey.DbAoValidationColors => BuildDbValidationView("DB AO", [], _project.DbCatalog.AoTextLengths,
                showColors: false,
                [DbValidationDefaults.DbMeasUnit],
                (_, lengths) =>
                {
                    _project.DbCatalog.AoTextLengths = lengths;
                    _validationStateUpdater.Update(_project);
                    DbAoGrid.Items.Refresh();
                }),
            SettingsHubKey.DbMeasValidationColors => BuildDbValidationView("DB MEAS", [], _project.DbCatalog.MeasTextLengths,
                showColors: false,
                ["DB_MEAS."],
                (_, lengths) =>
                {
                    _project.DbCatalog.MeasTextLengths = lengths;
                    _validationStateUpdater.Update(_project);
                    DbMeasGrid.Items.Refresh();
                }),
            SettingsHubKey.DbAlmValidationColors => BuildDbValidationView("DB ALM", [], _project.DbCatalog.AlarmTextLengths,
                showColors: false,
                ["DB_ALM."],
                (_, lengths) =>
                {
                    _project.DbCatalog.AlarmTextLengths = lengths;
                    _validationStateUpdater.Update(_project);
                    DbAlmGrid.Items.Refresh();
                }),
            SettingsHubKey.DbParValidationColors => BuildDbValidationView("DB PAR", [], _project.DbCatalog.ParameterTextLengths,
                showColors: false,
                ["DB_PAR.", "LIST_PAR."],
                (_, lengths) =>
                {
                    _project.DbCatalog.ParameterTextLengths = lengths;
                    _validationStateUpdater.Update(_project);
                    DbParGrid.Items.Refresh();
                }),
            _ => null
        };
    }

    private DbValidationColorsSettingsView BuildDbDiValidationView()
    {
        return new DbValidationColorsSettingsView(
            _project.DbCatalog.DiColorSettings,
            _project.DbCatalog.DiTextLengths,
            showColors: true,
            GetValidationSettings("DB_DI."),
            new DbValidationColorsCommit
            {
                Commit = (colors, lengths, validation) =>
                {
                    _project.DbCatalog.DiColorSettings = colors.ToList();
                    _project.DbCatalog.DiTextLengths = lengths;
                    ApplyValidationSettings(validation);
                    ApplyConfiguredColorPalette();
                    ConfigureDbValidationCombos();
                    UpdateDbDiFilterItems();
                    _dbDiView?.Refresh();
                    DbDiGrid.Items.Refresh();
                }
            });
    }

    private DbValidationColorsSettingsView BuildDbValidationView(
        string dbName,
        IEnumerable<DbColorSetting> colors,
        DbTextLengthSettings textLengths,
        bool showColors,
        string[] validationPrefixes,
        Action<IEnumerable<DbColorSetting>, DbTextLengthSettings> after)
    {
        return new DbValidationColorsSettingsView(
            colors,
            textLengths,
            showColors,
            GetValidationSettings(validationPrefixes),
            new DbValidationColorsCommit
            {
                Commit = (newColors, newLengths, validation) =>
                {
                    ApplyValidationSettings(validation);
                    ConfigureDbValidationCombos();
                    after(newColors, newLengths.Clone());
                }
            });
    }

    private DbSystemNameSettingsView BuildDbDiPlcNamesView()
    {
        return new DbSystemNameSettingsView(
            _project.DbCatalog.DiSystemNameRows,
            _project.DbCatalog.DiStructTypePrefix,
            _project.DbCatalog.DiSimulatorTypePrefix,
            showSimulatorPrefix: true,
            new DbSystemNameSettingsCommit
            {
                Commit = (rows, sp, simp) =>
                {
                    _project.DbCatalog.DiSystemNameRows = rows.Select(r => r.Clone()).ToList();
                    _project.DbCatalog.DiStructTypePrefix = sp;
                    _project.DbCatalog.DiSimulatorTypePrefix = simp;
                    ApplyDbDiSystemNamesToRows();
                    DiGrid.Items.Refresh();
                }
            });
    }

    private DbSystemNameSettingsView BuildDbDoPlcNamesView()
    {
        return new DbSystemNameSettingsView(
            _project.DbCatalog.DoSystemNameRows,
            _project.DbCatalog.DoStructTypePrefix,
            _project.DbCatalog.DoSimulatorTypePrefix,
            showSimulatorPrefix: true,
            new DbSystemNameSettingsCommit
            {
                Commit = (rows, sp, simp) =>
                {
                    _project.DbCatalog.DoSystemNameRows = rows.Select(r => r.Clone()).ToList();
                    _project.DbCatalog.DoStructTypePrefix = sp;
                    _project.DbCatalog.DoSimulatorTypePrefix = simp;
                    ApplyDbDoSystemNamesToRows();
                    DoGrid.Items.Refresh();
                }
            });
    }

    private DbSystemNameSettingsView BuildDbAoPlcNamesView()
    {
        var generated = MergeAoStructSettingsRows(
            _project.DbCatalog.AoRows,
            _project.DbCatalog.AoSystemNameRows,
            _project.DbCatalog.AoStructTypePrefix);
        return new DbSystemNameSettingsView(
            generated,
            _project.DbCatalog.AoStructTypePrefix,
            "",
            showSimulatorPrefix: false,
            new DbSystemNameSettingsCommit
            {
                Commit = (rows, sp, _) =>
                {
                    _project.DbCatalog.AoSystemNameRows = rows.Select(r => r.Clone()).ToList();
                    _project.DbCatalog.AoStructTypePrefix = sp;
                    DbAoGrid.Items.Refresh();
                }
            });
    }

    private DbSystemNameSettingsView BuildGeneratedPlcNamesView(
        string dbName,
        IEnumerable<DbStringRow> sourceRows,
        IReadOnlyList<DbDiSystemNameRow> savedRows,
        string structPrefix,
        string simulatorPrefix,
        bool showSimulatorPrefix,
        Action<IEnumerable<DbDiSystemNameRow>> saveRows,
        Action<string, string> savePrefixes)
    {
        var generated = MergeStructSettingsRows(sourceRows, savedRows, structPrefix, simulatorPrefix, showSimulatorPrefix);
        return new DbSystemNameSettingsView(
            generated,
            structPrefix,
            simulatorPrefix,
            showSimulatorPrefix,
            new DbSystemNameSettingsCommit
            {
                Commit = (rows, sp, simp) =>
                {
                    saveRows(rows);
                    savePrefixes(sp, simp);
                    ApplyGeneratedStructSettingsToRows(sourceRows, rows);
                    RefreshAllDbViews();
                }
            });
    }

    private void CommitTableDesign(TableDesignSettings design, DbConditionalColorSettings colors)
    {
        _project.TableDesign = design;
        _project.DbCatalog.ConditionalColors = colors.Clone();
        ApplyConfiguredConditionalColors();
        ApplyTableDesign();
        RefreshAllDataGrids();
    }

    private void OnSettingsApplied(SettingsHubKey key)
    {
        switch (key)
        {
            case SettingsHubKey.ProjectPaths:
                ShowMessage("Project paths updated.");
                break;
            case SettingsHubKey.TableDesign:
                ShowMessage("Table design updated.");
                break;
            case SettingsHubKey.DbNamesAndNumbers:
                ShowMessage("DB names and numbers updated.");
                break;
            case SettingsHubKey.DbDiPlcNames: ShowMessage("DB DI PLC names & types updated."); break;
            case SettingsHubKey.DbDoPlcNames: ShowMessage("DB DO PLC names & types updated."); break;
            case SettingsHubKey.DbAoPlcNames: ShowMessage("DB AO PLC names & types updated."); break;
            case SettingsHubKey.DbMeasPlcNames: ShowMessage("DB MEAS PLC names & types updated."); break;
            case SettingsHubKey.DbAlmPlcNames: ShowMessage("DB ALM PLC names & types updated."); break;
            case SettingsHubKey.DbParPlcNames: ShowMessage("DB PAR PLC names & types updated."); break;
            case SettingsHubKey.DbDiValidationColors: ShowMessage("DB DI validation & colors updated."); break;
            case SettingsHubKey.DbDoValidationColors: ShowMessage("DB DO validation & colors updated."); break;
            case SettingsHubKey.DbAoValidationColors: ShowMessage("DB AO validation & colors updated."); break;
            case SettingsHubKey.DbMeasValidationColors: ShowMessage("DB MEAS validation & colors updated."); break;
            case SettingsHubKey.DbAlmValidationColors: ShowMessage("DB ALM validation & colors updated."); break;
            case SettingsHubKey.DbParValidationColors: ShowMessage("DB PAR validation & colors updated."); break;
        }
    }

    private void SetBilingualFromHub(bool value)
    {
        if (_project.Multilingual == value)
        {
            return;
        }

        _project.Multilingual = value;
        RepairContext.IsMultilingual = value;
        if (MultilingualMenuItem is not null)
        {
            MultilingualMenuItem.IsChecked = value;
        }
        RecordUndoSnapshot();
        RefreshAllDbGridsAfterMultilingualToggle();
        ShowMessage(value
            ? "Multilingual mode ON — *Other text fields are validated."
            : "Monolingual mode — *Other text fields are skipped by repair/validate.");
    }
}
