using System.IO;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows;
using System.Windows.Documents;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using CwcConfigurator.Services.TiaSync;

namespace CwcConfigurator.App;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand NewProjectCommand = new("New", nameof(NewProjectCommand), typeof(MainWindow));
    public static readonly RoutedUICommand OpenProjectCommand = new("Open", nameof(OpenProjectCommand), typeof(MainWindow));
    public static readonly RoutedUICommand SaveProjectCommand = new("Save", nameof(SaveProjectCommand), typeof(MainWindow));
    public static readonly RoutedUICommand SaveProjectAsCommand = new("Save As", nameof(SaveProjectAsCommand), typeof(MainWindow));
    public static readonly RoutedUICommand UndoCommand = new("Undo", nameof(UndoCommand), typeof(MainWindow));
    public static readonly RoutedUICommand RedoCommand = new("Redo", nameof(RedoCommand), typeof(MainWindow));
    public static readonly RoutedUICommand ValidateProjectCommand = new("Validate", nameof(ValidateProjectCommand), typeof(MainWindow));
    public static readonly RoutedUICommand GenerateFilesCommand = new("Generate", nameof(GenerateFilesCommand), typeof(MainWindow));
    public static readonly RoutedUICommand FocusSearchCommand = new("Focus search", nameof(FocusSearchCommand), typeof(MainWindow));
    public static readonly RoutedUICommand OpenReplaceCommand = new("Open replace", nameof(OpenReplaceCommand), typeof(MainWindow));

    // When a modal Settings Hub is open, every dialog launched from a
    // settings handler should be owned by the Hub (so it appears on top
    // and the Hub stays visible afterwards). All MainWindow dialog
    // constructions use `Owner = EditorOwner` which transparently
    // resolves to the Hub when active, otherwise to MainWindow.
    private Window? _editorOwnerOverride;
    private Window EditorOwner => _editorOwnerOverride ?? this;

    private void NewProjectCommand_Executed(object sender, ExecutedRoutedEventArgs e) => NewProject_Click(sender, e);
    private void OpenProjectCommand_Executed(object sender, ExecutedRoutedEventArgs e) => OpenProject_Click(sender, e);
    private void SaveProjectCommand_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProject_Click(sender, e);
    private void SaveProjectAsCommand_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProjectAs_Click(sender, e);
    private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Undo_Click(sender, e);
    private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Redo_Click(sender, e);
    private void ValidateProjectCommand_Executed(object sender, ExecutedRoutedEventArgs e) => ValidateProject_Click(sender, e);
    private void GenerateFilesCommand_Executed(object sender, ExecutedRoutedEventArgs e) => GenerateFiles_Click(sender, e);
    private void FocusSearchCommand_Executed(object sender, ExecutedRoutedEventArgs e) => OpenFindBar(replaceMode: false);
    private void OpenReplaceCommand_Executed(object sender, ExecutedRoutedEventArgs e) => OpenFindBar(replaceMode: true);

    private void FocusActiveTabSearch()
    {
        var box = MainTabs?.SelectedItem switch
        {
            TabItem t when t == DbDiTab => DbDiTextFilter,
            TabItem t when t == DbDoTab => DbDoTextFilter,
            TabItem t when t == DbMeasTab => DbMeasTextFilter,
            TabItem t when t == DbAoTab => DbAoTextFilter,
            TabItem t when t == DbAlmTab => DbAlmTextFilter,
            TabItem t when t == DbParTab => DbParTextFilter,
            _ => (TextBox?)null
        };

        if (box is null)
        {
            ShowMessage("Search is available on DB tabs (DI/DO/MEAS/AO/ALM/PAR).");
            return;
        }

        box.Focus();
        box.SelectAll();
    }

    private readonly ProjectFileService _projectFileService = new();
    private readonly ProjectValidator _validator = new();
    private readonly RecentProjectsService _recentProjectsService = new();
    private readonly TiaSourceGenerator _generator = new();
    private readonly ExcelProjectImporter _excelImporter = new();
    private readonly ExcelProjectExporter _excelExporter = new();
    private readonly DbTextWorkbookService _dbTextWorkbookService = new();
    private readonly DescriptionTextListService _descriptionTextListService = new();
    private readonly DescriptionTemplateService _descriptionTemplateService = new();
    private readonly DescriptionTemplateWorkbookService _descriptionTemplateWorkbookService = new();
    private readonly TiaPortalExportImporter _tiaPortalImporter = new();
    private readonly ScadaConfigFileService _scadaConfigFileService = new();
    private readonly TiaSessionService _tiaSessionService = new();
    private readonly ConfigurationCalculator _calculator = new();
    private readonly ValidationStateUpdater _validationStateUpdater = new();
    private readonly SequenceParDbParser _sequenceParser = new();
    private readonly UnifiedJsConnectorImporter _unifiedJsConnectorImporter = new();
    private readonly UnifiedJsConnectorRuntime _unifiedJsConnectorRuntime = new();
    private static readonly JsonSerializerOptions CloneJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private CwcProject _project = CwcProject.CreateEmpty();
    private string? _projectPath;
    private byte[] _savedSnapshotHash = [];
    private readonly Stack<byte[]> _undoSnapshots = new();
    private readonly Stack<byte[]> _redoSnapshots = new();
    private bool _restoringSnapshot;
    private bool _lastKnownDirty;
    private DispatcherTimer? _dirtyCheckTimer;
    private DispatcherTimer? _dbAlmRefreshTimer;
    private DispatcherTimer? _sequenceControlStatusRefreshTimer;
    private DispatcherTimer? _sequenceCommandRefreshTimer;
    private bool _closingConfirmed;
    private bool _projectOpen = true;
    private ICollectionView? _diView;
    private ICollectionView? _doView;
    private ICollectionView? _aiView;
    private ICollectionView? _rtdView;
    private ICollectionView? _counterView;
    private ICollectionView? _aoView;
    private ICollectionView? _dbDiView;
    private ICollectionView? _dbDoView;
    private ICollectionView? _dbMeasView;
    private ICollectionView? _dbAoRowsView;
    private ICollectionView? _dbAlmView;
    private ICollectionView? _dbParView;
    private ICollectionView? _sequenceControlStatusView;
    private ICollectionView? _sequenceCommandView;
    private ICollectionView? _scadaVariablesView;
    private ICollectionView? _scadaNavigationView;
    private ICollectionView? _scadaScreensView;
    private ICollectionView? _scadaTagWritesView;
    private ScadaGeneralSettings? _scadaGeneralSettings;
    private List<ScadaLocationRow> _scadaLocationRows = [];
    private List<ScadaHeaderRow> _scadaHeaderRows = [];
    private ScadaMainNavSettings? _scadaMainNavSettings;
    private List<ScadaMainNavRow> _scadaMainNavRows = [];
    private List<ScadaScreenMatrixButtonRow> _scadaScreenMatrixRows = [];
    private List<ScadaLocationMatrixRow> _scadaLocationMatrixRows = [];
    private List<ScadaPlcLocationTab> _scadaPlcTabs = [];
    private string _scadaTagsFilterText = "";
    private object? _copiedScadaMatrixCell;
    private object? _copiedScadaMatrixCellSource;
    private object? _copiedScadaMatrixRow;
    private object? _copiedScadaMatrixRowSource;
    private readonly List<ScadaOption> _scadaLanguageOptions = BuildScadaLanguageOptions();
    private readonly List<ScadaOption> _scadaLocalRemoteOptions =
    [
        new("0", "Local"),
        new("1", "Remote")
    ];
    private readonly List<ScadaOption> _scadaScreenShapeOptions =
    [
        new("0", "LAYOUT_WITH_TOP_NAV"),
        new("1", "LAYOUT_WITHOUT_TOP_NAV"),
        new("2", "LAYOUT_WITHOUT_SIDE_CONTENT"),
        new("3", "LAYOUT_WITH_CONTENT_ONLY")
    ];
    private bool _loadingDbDiFilters;
    private bool _loadingDbDoFilters;
    private bool _loadingDbMeasFilters;
    private bool _loadingDbAoFilters;
    private bool _loadingDbAlmFilters;
    private bool _loadingDbParFilters;
    private bool _loadingSequenceControlStatusFilters;
    private bool _loadingSequenceCommandFilters;
    private bool _updatingDbAlmSlicer;
    private bool _bindingProject;
    private bool _loadingPlcDatabaseTree;
    private DataGrid? _fillGrid;
    private object? _fillSourceRow;
    private object? _fillTargetRow;
    private DataGridColumn? _fillColumn;
    private string? _fillProperty;
    private string? _fillValue;
    private string? _fillTemplateKey;
    private Point _fillStartPoint;
    private bool _fillDragging;
    private List<TemplateClipboardCell>? _templateClipboardCells;
    // Text we last wrote to the OS clipboard. If the clipboard now contains
    // anything else (user copied from another app), template metadata is stale
    // and must NOT be applied during the next paste.
    private string? _lastInternalClipboardText;
    private DragFillAdorner? _dragFillAdorner;
    private AdornerLayer? _dragFillAdornerLayer;
    private bool _dbDiTiaOperationRunning;
    private bool _tiaStatusRefreshRunning;
    private bool _dbCrossReferenceUpdateRunning;
    private readonly DispatcherTimer _tiaStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, Border> _tiaTabIndicators = new(StringComparer.OrdinalIgnoreCase);
    private List<TabItem> _allMainTabs = [];

    public IReadOnlyList<DbValidationOption> DbDiColorValues => GetValidationOptions(DbValidationDefaults.DbDiColorValues);

    private sealed record ModuleFilterItem(int? ModuleNumber, string DisplayText)
    {
        public override string ToString()
        {
            return DisplayText;
        }
    }

    private sealed record DbFilterSpec(ComboBox Filter, string AllText, Func<DbStringRow, string> ValueSelector, bool SortAsNumber = false);
    private sealed record DbAoFilterSpec(ComboBox Filter, string AllText, Func<DbAoRow, string> ValueSelector, bool SortAsNumber = false);
    private sealed record ColumnFilterLayout(DataGridColumn Column, FrameworkElement FilterElement, double BaseWidth, bool Stretch);
    private sealed record CrossReferenceCountRequest(string Member, string VariablePath, Action<int?> ApplyCount, Action<int?>? ApplyWriteCount = null);
    private sealed record TemplateClipboardCell(int RowOffset, int ColumnOffset, string TemplateKey);
    private sealed record ScadaOption(string Code, string Display);
    private sealed record ScadaScreenCellSettings(string ScreenName, string TextLanguage1, string TextLanguage2, string GraphicListId);
    private sealed record ScadaLocationCellSettings(string Name1, string Name2, string GraphicListId);
    private sealed record ScadaPlcCellSettings(string ModuleColor);
    private sealed record ScadaScreenRowSettings(IReadOnlyList<ScadaScreenCellSettings> Cells);
    private sealed record ScadaLocationRowSettings(IReadOnlyList<ScadaLocationCellSettings> Cells);
    private sealed record ScadaPlcRowSettings(IReadOnlyList<ScadaPlcCellSettings> Cells);
    private sealed record ScadaMatrixChoice(object Item, string Display);

    private sealed class ScadaGeneralSettings
    {
        private readonly ScadaConfigVariable _runtimeName;
        private readonly ScadaConfigVariable _bilingualScada;
        private readonly ScadaConfigVariable _locationNum;
        private readonly ScadaConfigVariable _locationForSetLanguage;
        private readonly ScadaConfigVariable _secondLanguage;

        public ScadaGeneralSettings(
            ScadaConfigVariable runtimeName,
            ScadaConfigVariable bilingualScada,
            ScadaConfigVariable locationNum,
            ScadaConfigVariable locationForSetLanguage,
            ScadaConfigVariable secondLanguage)
        {
            _runtimeName = runtimeName;
            _bilingualScada = bilingualScada;
            _locationNum = locationNum;
            _locationForSetLanguage = locationForSetLanguage;
            _secondLanguage = secondLanguage;
        }

        public string RuntimeNameValue
        {
            get => _runtimeName.Value;
            set => _runtimeName.Value = value ?? "";
        }

        public bool BilingualScadaChecked
        {
            get => IsScadaIntEnabled(_bilingualScada.Value);
            set => _bilingualScada.Value = value ? "1" : "0";
        }

        public string LocationNumValue
        {
            get => _locationNum.Value;
            set => _locationNum.Value = NormalizeIntText(value);
        }

        public string LocationForSetLanguageValue
        {
            get => _locationForSetLanguage.Value;
            set => _locationForSetLanguage.Value = NormalizeIntText(value);
        }

        public string SecondLanguageValue
        {
            get => _secondLanguage.Value;
            set => _secondLanguage.Value = NormalizeIntText(value);
        }
    }

    private sealed class ScadaLocationRow
    {
        private readonly ScadaConfigVariable _active;
        private readonly ScadaConfigVariable _name1;
        private readonly ScadaConfigVariable _name2;
        private readonly ScadaConfigVariable _source;
        private readonly ScadaConfigVariable _entry;
        private readonly ScadaConfigVariable _localRemote;

        public ScadaLocationRow(
            int index,
            ScadaConfigVariable active,
            ScadaConfigVariable name1,
            ScadaConfigVariable name2,
            ScadaConfigVariable source,
            ScadaConfigVariable entry,
            ScadaConfigVariable localRemote)
        {
            Index = index;
            _active = active;
            _name1 = name1;
            _name2 = name2;
            _source = source;
            _entry = entry;
            _localRemote = localRemote;
        }

        public int Index { get; }

        public bool IsActive
        {
            get => IsScadaIntEnabled(_active.Value);
            set => _active.Value = value ? "1" : "0";
        }

        public string Name1
        {
            get => _name1.Value;
            set => _name1.Value = value ?? "";
        }

        public string Name2
        {
            get => _name2.Value;
            set => _name2.Value = value ?? "";
        }

        public string Source
        {
            get => _source.Value;
            set => _source.Value = NormalizeIntText(value);
        }

        public string Entry
        {
            get => _entry.Value;
            set => _entry.Value = IsActive ? NormalizeOptionalIntText(value) : "";
        }

        public string LocalRemoteValue
        {
            get => _localRemote.Value;
            set => _localRemote.Value = value == "1" ? "1" : "0";
        }

        public void Validate()
        {
            if (!IsActive)
            {
                _entry.Value = "";
            }
        }
    }

    private sealed class ScadaHeaderRow
    {
        private readonly ScadaConfigVariable _entry;
        private readonly ScadaConfigVariable _name1;
        private readonly ScadaConfigVariable _name2;
        private readonly ScadaConfigVariable _powerVisible;

        public ScadaHeaderRow(int index, ScadaConfigVariable entry, ScadaConfigVariable name1, ScadaConfigVariable name2, ScadaConfigVariable powerVisible)
        {
            Index = index;
            _entry = entry;
            _name1 = name1;
            _name2 = name2;
            _powerVisible = powerVisible;
        }

        public int Index { get; }

        public bool Visible
        {
            get => !string.IsNullOrWhiteSpace(_entry.Value);
            set => _entry.Value = value ? string.IsNullOrWhiteSpace(_entry.Value) ? $"HEADER_TAB_{Index.ToString(CultureInfo.InvariantCulture)}" : _entry.Value : "";
        }

        public string Entry
        {
            get => _entry.Value;
            set => _entry.Value = Visible ? value ?? "" : "";
        }

        public string Name1
        {
            get => _name1.Value;
            set => _name1.Value = value ?? "";
        }

        public string Name2
        {
            get => _name2.Value;
            set => _name2.Value = value ?? "";
        }

        public bool PowerVisible
        {
            get => IsScadaIntEnabled(_powerVisible.Value);
            set => _powerVisible.Value = value ? "1" : "0";
        }

        public void Validate()
        {
            _entry.Value = Visible
                ? string.IsNullOrWhiteSpace(_entry.Value) ? $"HEADER_TAB_{Index.ToString(CultureInfo.InvariantCulture)}" : _entry.Value
                : "";
        }
    }

    private sealed class ScadaMainNavSettings
    {
        private readonly ScadaConfigVariable _buttonNum;

        public ScadaMainNavSettings(ScadaConfigVariable buttonNum)
        {
            _buttonNum = buttonNum;
        }

        public string ButtonNumValue
        {
            get => _buttonNum.Value;
            set => _buttonNum.Value = NormalizeIntText(value);
        }
    }

    private sealed class ScadaMainNavRow
    {
        private readonly ScadaConfigVariable _entry;
        private readonly ScadaConfigVariable _text1;
        private readonly ScadaConfigVariable _text2;
        private readonly ScadaConfigVariable _graphicListId;
        private readonly ScadaConfigVariable _screenShape;
        private readonly ScadaConfigVariable _screenNum;
        private readonly ScadaConfigVariable _sideContentScreen;

        public ScadaMainNavRow(
            int index,
            ScadaConfigVariable entry,
            ScadaConfigVariable text1,
            ScadaConfigVariable text2,
            ScadaConfigVariable graphicListId,
            ScadaConfigVariable screenShape,
            ScadaConfigVariable screenNum,
            ScadaConfigVariable sideContentScreen)
        {
            Index = index;
            _entry = entry;
            _text1 = text1;
            _text2 = text2;
            _graphicListId = graphicListId;
            _screenShape = screenShape;
            _screenNum = screenNum;
            _sideContentScreen = sideContentScreen;
        }

        public int Index { get; }

        public bool Visible
        {
            get => !string.IsNullOrWhiteSpace(_entry.Value);
            set => _entry.Value = value ? string.IsNullOrWhiteSpace(_entry.Value) ? $"Button {Index.ToString(CultureInfo.InvariantCulture)}" : _entry.Value : "";
        }

        public string Entry
        {
            get => _entry.Value;
            set => _entry.Value = Visible ? value ?? "" : "";
        }

        public string TextLanguage1
        {
            get => _text1.Value;
            set => _text1.Value = value ?? "";
        }

        public string TextLanguage2
        {
            get => _text2.Value;
            set => _text2.Value = value ?? "";
        }

        public string GraphicListId
        {
            get => _graphicListId.Value;
            set => _graphicListId.Value = NormalizeIntText(value);
        }

        public string ScreenShape
        {
            get => _screenShape.Value;
            set => _screenShape.Value = NormalizeIntText(value);
        }

        public string ScreenNum
        {
            get => _screenNum.Value;
            set => _screenNum.Value = NormalizeIntText(value);
        }

        public string SideContentScreen
        {
            get => _sideContentScreen.Value;
            set => _sideContentScreen.Value = value ?? "";
        }

        public void Validate()
        {
            _entry.Value = Visible
                ? string.IsNullOrWhiteSpace(_entry.Value) ? $"Button {Index.ToString(CultureInfo.InvariantCulture)}" : _entry.Value
                : "";
            _screenNum.Value = NormalizeIntText(_screenNum.Value);
            if (_screenShape.Value is "2" or "3")
            {
                _sideContentScreen.Value = "SIDE_CONTENT_General";
            }
        }
    }

    private sealed class ScadaScreenMatrixButtonRow
    {
        public ScadaScreenMatrixButtonRow(int button, string entryName, IReadOnlyList<ScadaScreenMatrixCell> cells)
        {
            Button = button;
            EntryName = entryName;
            Cells = cells;
        }

        public int Button { get; }
        public string EntryName { get; }
        public string Header => string.IsNullOrWhiteSpace(EntryName)
            ? $"Button {Button.ToString(CultureInfo.InvariantCulture)}"
            : $"{Button.ToString(CultureInfo.InvariantCulture)} - {EntryName}";
        public IReadOnlyList<ScadaScreenMatrixCell> Cells { get; }
    }

    private sealed class ScadaScreenMatrixCell
    {
        private readonly ScadaConfigVariable _screenEntry;
        private readonly ScadaConfigVariable _text1;
        private readonly ScadaConfigVariable _text2;
        private readonly ScadaConfigVariable _graphicListId;

        public ScadaScreenMatrixCell(int button, int topNav, ScadaConfigVariable screenEntry, ScadaConfigVariable text1, ScadaConfigVariable text2, ScadaConfigVariable graphicListId)
        {
            Button = button;
            TopNav = topNav;
            _screenEntry = screenEntry;
            _text1 = text1;
            _text2 = text2;
            _graphicListId = graphicListId;
        }

        public int Button { get; }
        public int TopNav { get; }
        public string TopNavName => $"TOP NAV {TopNav.ToString(CultureInfo.InvariantCulture)}";

        public string ScreenName
        {
            get => _screenEntry.Value;
            set => _screenEntry.Value = value ?? "";
        }

        public string TextLanguage1
        {
            get => _text1.Value;
            set => _text1.Value = value ?? "";
        }

        public string TextLanguage2
        {
            get => _text2.Value;
            set => _text2.Value = value ?? "";
        }

        public string GraphicListId
        {
            get => _graphicListId.Value;
            set => _graphicListId.Value = NormalizeIntText(value);
        }

        public ScadaScreenCellSettings CaptureSettings()
        {
            return new ScadaScreenCellSettings(ScreenName, TextLanguage1, TextLanguage2, GraphicListId);
        }

        public void ApplySettings(ScadaScreenCellSettings settings)
        {
            ScreenName = settings.ScreenName;
            TextLanguage1 = settings.TextLanguage1;
            TextLanguage2 = settings.TextLanguage2;
            GraphicListId = settings.GraphicListId;
        }
    }

    private sealed class ScadaLocationMatrixRow
    {
        public ScadaLocationMatrixRow(int location, string entry, string name, IReadOnlyList<ScadaLocationMatrixCell> cells)
        {
            Location = location;
            Entry = entry;
            Name = name;
            Cells = cells;
        }

        public int Location { get; }
        public string Entry { get; }
        public string Name { get; }
        public string Header => string.IsNullOrWhiteSpace(Name)
            ? $"LOCATION {EntryLabel}"
            : $"{EntryLabel} - {Name}";
        private string EntryLabel => string.IsNullOrWhiteSpace(Entry) ? Location.ToString(CultureInfo.InvariantCulture) : Entry;
        public IReadOnlyList<ScadaLocationMatrixCell> Cells { get; }
    }

    private sealed class ScadaLocationMatrixCell
    {
        private readonly ScadaConfigVariable _systemName;
        private readonly ScadaConfigVariable _systemName2;
        private readonly ScadaConfigVariable _graphicListId;

        public ScadaLocationMatrixCell(int location, int system, ScadaConfigVariable systemName, ScadaConfigVariable systemName2, ScadaConfigVariable graphicListId)
        {
            Location = location;
            System = system;
            _systemName = systemName;
            _systemName2 = systemName2;
            _graphicListId = graphicListId;
        }

        public int Location { get; }
        public int System { get; }
        public string SystemNameHeader => $"SYSTEM {System.ToString(CultureInfo.InvariantCulture)}";

        public string Name1
        {
            get => _systemName.Value;
            set => _systemName.Value = value ?? "";
        }

        public string Name2
        {
            get => _systemName2.Value;
            set => _systemName2.Value = value ?? "";
        }

        public string GraphicListId
        {
            get => _graphicListId.Value;
            set => _graphicListId.Value = NormalizeIntText(value);
        }

        public ScadaLocationCellSettings CaptureSettings()
        {
            return new ScadaLocationCellSettings(Name1, Name2, GraphicListId);
        }

        public void ApplySettings(ScadaLocationCellSettings settings)
        {
            Name1 = settings.Name1;
            Name2 = settings.Name2;
            GraphicListId = settings.GraphicListId;
        }
    }

    private sealed class ScadaPlcLocationTab
    {
        public ScadaPlcLocationTab(int location, string entry, string name, IReadOnlyList<ScadaPlcRackRow> rows)
        {
            Location = location;
            Entry = entry;
            Name = name;
            Rows = rows;
        }

        public int Location { get; }
        public string Entry { get; }
        public string Name { get; }
        public string Header => string.IsNullOrWhiteSpace(Name)
            ? $"LOCATION {EntryLabel}"
            : $"{EntryLabel} - {Name}";
        private string EntryLabel => string.IsNullOrWhiteSpace(Entry) ? Location.ToString(CultureInfo.InvariantCulture) : Entry;
        public IReadOnlyList<ScadaPlcRackRow> Rows { get; }
    }

    private sealed class ScadaPlcRackRow
    {
        public ScadaPlcRackRow(string rackName, IReadOnlyList<ScadaPlcSlotCell> slots)
        {
            RackName = rackName;
            Slots = slots;
        }

        public string RackName { get; }
        public IReadOnlyList<ScadaPlcSlotCell> Slots { get; }
    }

    private sealed class ScadaPlcSlotCell
        : INotifyPropertyChanged
    {
        private readonly ScadaConfigVariable _moduleColor;

        public ScadaPlcSlotCell(int slot, ScadaConfigVariable moduleColor)
        {
            Slot = slot;
            _moduleColor = moduleColor;
        }

        public int Slot { get; }
        public string SlotHeader => $"S{Slot.ToString(CultureInfo.InvariantCulture)}";

        public string ModuleColor
        {
            get => _moduleColor.Value == "1" ? "1" : "0";
            set
            {
                var normalized = value == "1" ? "1" : "0";
                if (_moduleColor.Value == normalized)
                {
                    return;
                }

                _moduleColor.Value = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModuleColor)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBrush)));
            }
        }

        public Brush CellBrush => ModuleColor == "1"
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(229, 231, 235));

        public event PropertyChangedEventHandler? PropertyChanged;

        public ScadaPlcCellSettings CaptureSettings()
        {
            return new ScadaPlcCellSettings(ModuleColor);
        }

        public void ApplySettings(ScadaPlcCellSettings settings)
        {
            ModuleColor = settings.ModuleColor;
        }
    }

    private static bool IsScadaIntEnabled(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIntText(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private static string NormalizeOptionalIntText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : NormalizeIntText(value);
    }

    private static List<ScadaOption> BuildScadaLanguageOptions()
    {
        return CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(culture => culture.LCID > 0)
            .GroupBy(culture => culture.LCID)
            .Select(group => group.OrderBy(culture => culture.EnglishName, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(culture => culture.EnglishName, StringComparer.OrdinalIgnoreCase)
            .Select(culture => new ScadaOption(
                culture.LCID.ToString(CultureInfo.InvariantCulture),
                $"{culture.EnglishName} ({culture.LCID.ToString(CultureInfo.InvariantCulture)})"))
            .ToList();
    }

    public MainWindow()
    {
        InitializeComponent();
        RestoreUiState();
        InitializeGridSafetyHandlers();
        InitializeTiaTabIndicators();
        InitializeColumnFilterSync();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _tiaStatusTimer.Tick += TiaStatusTimer_Tick;
        _allMainTabs = MainTabs.Items.OfType<TabItem>().ToList();
        ArrangeMainTabs();
        LoadProjectDatabase();
        BindProject();
        MarkProjectClean();
        ShowMessage("Ready.");
        ShowWelcomeScreen();
        InitializeAutosave();
        Dispatcher.BeginInvoke(OfferAutosaveRecovery, DispatcherPriority.Loaded);
    }

    private void InitializeGridSafetyHandlers()
    {
        foreach (var grid in GetSpreadsheetLikeGrids())
        {
            grid.PreviewMouseLeftButtonDown += DbGrid_PreviewMouseLeftButtonDown;
            grid.PreviewMouseMove += DbGrid_PreviewMouseMove;
            grid.PreviewMouseLeftButtonUp += DbGrid_PreviewMouseLeftButtonUp;
            grid.PreviewMouseDoubleClick += DbGrid_PreviewMouseDoubleClick;
            grid.SelectedCellsChanged += SpreadsheetGrid_SelectedCellsChanged;
            grid.LoadingRow += SpreadsheetGrid_LoadingRow;
            grid.Sorting += SpreadsheetGrid_Sorting;
            if (!IsDbGrid(grid))
            {
                grid.PreviewKeyDown += DbGrid_PreviewKeyDown;
            }

            AttachSpreadsheetContextMenu(grid);
            EnableAutoFitOnHeaderGripper(grid);
            grid.ContextMenuOpening += DbGrid_ContextMenuOpening;
        }
    }

    /// <summary>
    /// WPF fires Sorting BEFORE applying the new sort, so we defer the status
    /// bar refresh until WPF has updated SortDescriptions on the collection
    /// view. Also clears phantom marching ants since rows shuffled positions.
    /// </summary>
    private void SpreadsheetGrid_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        if (sender is DataGrid grid && ReferenceEquals(grid, _clipboardSourceGrid))
        {
            ClearClipboardMarchingAnts();
        }

        Dispatcher.BeginInvoke(RefreshActiveTabFilterUi, DispatcherPriority.Background);
    }

    private void SpreadsheetGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // When DataGrid virtualization recycles a row container, re-apply the
        // marching-ants flag if any of its cells correspond to copied cells.
        if (_clipboardCellAnchors.Count > 0 && ReferenceEquals(sender, _clipboardSourceGrid))
        {
            RefreshClipboardCellMarkers();
        }

        // Same for the persistent problem-cell red border: a recycled row may
        // be reassigned to a problematic item, and without this it would render
        // unmarked until the next full sweep.
        if (sender is DataGrid grid && _problemCellAnchors.Count > 0)
        {
            ApplyProblemCellMarkers(grid);
        }
    }

    private DataGrid[] GetSpreadsheetLikeGrids()
    {
        return
        [
            DiGrid, DoGrid, AiGrid, RtdGrid, CounterGrid, AoGrid,
            ModuleNamesDiGrid, ModuleNamesDoGrid, ModuleNamesAiGrid, ModuleNamesRtdGrid, ModuleNamesCounterGrid, ModuleNamesAoGrid,
            DbDiGrid, DbDoGrid, DbMeasGrid, DbAoGrid, DbAlmGrid, DbParGrid,
            SequenceControlStatusGrid, SequenceCommandGrid,
            ScadaVariablesGrid, ScadaNavigationGrid, ScadaScreensGrid, ScadaTagsGrid, ScadaLocationsGrid, ScadaHeaderGrid, ScadaMainNavGrid,
            TextListAlmBlockingClassGrid, TextListCabinetsGrid, TextListPortConfigurationGrid, TextListSeqCmdGrid, TextListSeqConfigGrid,
            DevicesGrid
        ];
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSavePendingChanges())
        {
            return;
        }

        // The data model always needs at least one PLC database (every grid
        // binds to the active database's working catalog and
        // EnsureProjectDefaults would recreate one anyway), so instead of
        // silently spawning "PLC 1" ask for its real name up front.
        var nameDialog = new PlcDatabaseNameDialog("PLC 1")
        {
            Owner = EditorOwner,
            Title = "New project — name the first PLC database"
        };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        _project = CwcProject.CreateEmpty();
        _project.PlcDatabases[0].Name = nameDialog.DatabaseName;
        _projectPath = null;
        _projectOpen = true;
        BindProject();
        MarkProjectClean();
        ShowMessage("New project created.");
        HideWelcomeScreen();
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSavePendingChanges())
        {
            return;
        }

        var dialog = new RecentProjectsDialog(_recentProjectsService)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        OpenProjectFromPath(dialog.SelectedPath);
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveProject();
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSavePendingChanges())
        {
            return;
        }

        _project = CwcProject.CreateEmpty();
        _projectPath = null;
        _projectOpen = false;
        _savedSnapshotHash = _projectFileService.ComputeSnapshotHash(_project);
        _lastKnownDirty = false;
        PlcDatabaseTree.ItemsSource = null;
        ProjectWorkspace.Visibility = Visibility.Collapsed;
        ProjectActionBar.Visibility = Visibility.Collapsed;
        ProblemsPane.Visibility = Visibility.Collapsed;
        ProblemsList.ItemsSource = null;
        StatusText.Text = "Project closed.";
        UpdateProjectPathText();
        ShowWelcomeScreen();
    }

    private async void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        SaveActivePlcDatabase();
        var dialog = new OpenFileDialog
        {
            Filter = "Excel macro workbook (*.xlsm)|*.xlsm|Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var importedProject = await RunBusyAsync(
                $"Importing {Path.GetFileName(dialog.FileName)}...",
                () => _excelImporter.Import(dialog.FileName));
            var targetDialog = new PlcDatabasePickerDialog(
                "Choose PLC database to fill from Excel",
                _project.PlcDatabases,
                allowCreateNew: true,
                suggestedNewName: Path.GetFileNameWithoutExtension(dialog.FileName))
            {
                Owner = EditorOwner
            };

            if (targetDialog.ShowDialog() != true)
            {
                return;
            }

            var importedDatabase = PlcDatabase.FromProject(importedProject, targetDialog.NewDatabaseName);
            PlcDatabase targetDatabase;
            if (targetDialog.CreateNew)
            {
                importedDatabase.Name = targetDialog.NewDatabaseName;
                _project.PlcDatabases.Add(importedDatabase);
                targetDatabase = importedDatabase;
            }
            else
            {
                targetDatabase = targetDialog.SelectedDatabase ?? GetActivePlcDatabase() ?? throw new InvalidOperationException("PLC database is missing.");
                ReplacePlcDatabaseContent(targetDatabase, importedDatabase);
            }

            _project.ActivePlcDatabaseId = targetDatabase.Id;
            targetDatabase.ApplyTo(_project);
            EnsureProjectDefaults();
            _projectOpen = true;
            BindProject();
            await CheckTiaAfterImportAsync("DI", "DO", "AI", "RTD", "COUNTER", "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR");
            ShowMessage($"Imported Excel workbook into {targetDatabase.Name}: {dialog.FileName}");
            HideWelcomeScreen();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectAs();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        RestoreUndoSnapshot();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.Z)
        {
            RestoreUndoSnapshot();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            RestoreRedoSnapshot();
            e.Handled = true;
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        RestoreRedoSnapshot();
    }

    private void ValidateProject_Click(object sender, RoutedEventArgs e)
    {
        ValidateProject();
    }

    private async void TiaSync_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        var dialog = new TiaSyncDialog(_project, _tiaSessionService)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() == true)
        {
            SaveActivePlcDatabase();
            _validationStateUpdater.Update(_project);
            BindProject();
            ShowMessage("TIA Sync changes saved.");
        }

        CancelPendingTiaStatusCheck();
        UpdateTiaVisualState();
        await RefreshPlcDatabaseOnlineStatesAsync();
    }

    private async void DbDiTiaCheck_Click(object sender, RoutedEventArgs e)
    {
        await RunDbDiTiaOperationAsync(
            "Checking DB DI against TIA...",
            service => service.CheckDbDiAsync(_project),
            progressTitle: "DB DI TIA check",
            progressStatus: "Exporting DB DI sources from TIA for comparison...",
            showProgressDialog: true);
    }

    private async void DbDiTiaPull_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Pull DB DI will overwrite DB DI data in the active PLC database with values exported from the mapped TIA PLC.",
                "Pull DB DI",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDbDiTiaOperationAsync(
            "Pulling DB DI from TIA...",
            service => service.PullDbDiAsync(_project),
            rebindAfterSuccess: true,
            progressTitle: "DB DI TIA pull",
            progressStatus: "Exporting DB DI sources from TIA and importing them into CWC...",
            showProgressDialog: true);
    }

    private async void DbDiTiaPush_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Push DB DI will generate DB DI source files and import them into the mapped TIA PLC. Reset start values is skipped.",
                "Push DB DI",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDbDiTiaOperationAsync(
            "Pushing DB DI to TIA...",
            service => service.PushDbDiAsync(_project),
            rebindAfterSuccess: true,
            progressTitle: "DB DI TIA push",
            progressStatus: "TIA Portal is minimized. Generating, importing and compiling DB DI...",
            showProgressDialog: true,
            minimizeTiaPortal: true);
    }

    private async void DbTiaCheck_Click(object sender, RoutedEventArgs e)
    {
        var dbName = GetDbNameFromSender(sender);
        await RunDbFamilyTiaOperationAsync(
            dbName,
            $"Checking {dbName} against TIA...",
            service => service.CheckDbAsync(_project, dbName),
            progressTitle: $"{dbName} TIA check",
            progressStatus: $"Exporting {dbName} sources from TIA and comparing generated sources...");
    }

    private async void DbTiaPull_Click(object sender, RoutedEventArgs e)
    {
        var dbName = GetDbNameFromSender(sender);
        if (MessageBox.Show(this,
                $"Pull {dbName} will overwrite {dbName} data in the active PLC database with values exported from the mapped TIA PLC.",
                $"Pull {dbName}",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDbFamilyTiaOperationAsync(
            dbName,
            $"Pulling {dbName} from TIA...",
            service => service.PullDbAsync(_project, dbName),
            rebindAfterSuccess: true,
            progressTitle: $"{dbName} TIA pull",
            progressStatus: $"Exporting {dbName} sources from TIA and importing them into CWC...");
    }

    private async void DbTiaPush_Click(object sender, RoutedEventArgs e)
    {
        var dbName = GetDbNameFromSender(sender);
        if (MessageBox.Show(this,
                $"Push {dbName} will generate {dbName} source files and import them into the mapped TIA PLC. Reset start values is skipped.",
                $"Push {dbName}",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDbFamilyTiaOperationAsync(
            dbName,
            $"Pushing {dbName} to TIA...",
            service => service.PushDbAsync(_project, dbName),
            rebindAfterSuccess: true,
            progressTitle: $"{dbName} TIA push",
            progressStatus: $"TIA Portal is minimized. Generating, importing and compiling {dbName}...",
            minimizeTiaPortal: true);
    }

    private static string GetDbNameFromSender(object sender)
    {
        return sender is FrameworkElement { Tag: string dbName } && !string.IsNullOrWhiteSpace(dbName)
            ? NormalizeDbName(dbName)
            : throw new InvalidOperationException("DB TIA command is missing DB name.");
    }

    private async Task RunDbFamilyTiaOperationAsync(
        string dbName,
        string busyText,
        Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation,
        bool rebindAfterSuccess = false,
        string progressTitle = "TIA operation",
        string progressStatus = "Working with TIA Portal...",
        bool minimizeTiaPortal = false)
    {
        if (_dbDiTiaOperationRunning)
        {
            ShowMessage("TIA operation is already running.");
            return;
        }

        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal from TIA Sync first.", $"{dbName} TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        SetDbFamilyTiaBusy(dbName, true, busyText);
        TiaProgressDialog? progressDialog = null;
        try
        {
            _dbDiTiaOperationRunning = true;
            if (minimizeTiaPortal)
            {
                TiaPortalWindowService.MinimizePortalWindow(_tiaSessionService.CurrentSession);
            }

            progressDialog = new TiaProgressDialog(progressTitle, progressStatus)
            {
                Owner = EditorOwner
            };
            progressDialog.Show();

            var service = new TiaLiveSyncService(_tiaSessionService);
            var operationTask = operation(service);
            var completedTask = await Task.WhenAny(operationTask, Task.Delay(TimeSpan.FromMinutes(2)));
            if (completedTask != operationTask)
            {
                _tiaSessionService.AbortHelper();
                SetDbFamilyTiaBusy(dbName, false, "TIA: timeout");
                MessageBox.Show(this,
                    $"{progressTitle} did not finish within 2 minutes. The helper process was stopped. Attach to TIA Portal again before the next operation.",
                    $"{dbName} TIA Sync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = await operationTask;
            if (rebindAfterSuccess)
            {
                progressDialog.SetStatus("Refreshing Plant Configurator project data...");
                SaveActivePlcDatabase();
                _validationStateUpdater.Update(_project);
                BindProject();
                MarkTiaSectionState(dbName, "InSync", $"{dbName} is synchronized with TIA.");
            }

            CancelPendingTiaStatusCheck();
            SetDbFamilyTiaBusy(dbName, false, result.Message);
            UpdateTiaVisualState();
            ShowMessage(result.Message);
            if (result.Warnings.Count > 0 || result.Messages.Any(message => message.Contains("differs", StringComparison.OrdinalIgnoreCase)))
            {
                ShowTiaResult($"{dbName} TIA Sync", result);
            }
        }
        catch (Exception ex)
        {
            SetDbFamilyTiaBusy(dbName, false, "TIA: error");
            ShowError(ex.Message);
        }
        finally
        {
            progressDialog?.Close();
            _dbDiTiaOperationRunning = false;
            SetDbFamilyTiaBusy(dbName, false);
            UpdateToolbarVisibility();
        }
    }

    /// <summary>
    /// Presents a TIA operation result: short outputs keep the familiar
    /// MessageBox, anything longer opens the structured result window
    /// (summary + warnings up front, full log collapsed).
    /// </summary>
    private void ShowTiaResult(string title, TiaLiveSyncResult result)
    {
        ShowOperationResult(
            title,
            string.IsNullOrWhiteSpace(result.SnapshotFolder)
                ? result.Message
                : $"{result.Message}{Environment.NewLine}Snapshot: {result.SnapshotFolder}",
            result.Warnings,
            result.Messages,
            result.Warnings.Count == 0);
    }

    private void ShowOperationResult(string title, string summary, IReadOnlyList<string> warnings, IReadOnlyList<string> details, bool success)
    {
        if (warnings.Count + details.Count <= 8)
        {
            var text = string.Join(
                Environment.NewLine,
                new[] { summary }
                    .Concat(warnings.Select(warning => "Warning: " + warning))
                    .Concat(details));
            MessageBox.Show(this, text, title, MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return;
        }

        var dialog = new OperationResultDialog(title, summary, warnings, details)
        {
            Owner = EditorOwner
        };
        dialog.ShowDialog();
    }

    private async void DiHwTiaPull_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPullAsync("DI", service => service.PullHwSectionAsync(_project, "DI"));
    private async void DiHwTiaPush_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPushAsync("DI", service => service.PushHwSectionAsync(_project, "DI"));

    private async void DoHwTiaPull_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPullAsync("DO", service => service.PullHwSectionAsync(_project, "DO"));
    private async void DoHwTiaPush_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPushAsync("DO", service => service.PushHwSectionAsync(_project, "DO"));

    private async void AiHwTiaPull_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPullAsync("AI", service => service.PullHwSectionAsync(_project, "AI"));
    private async void AiHwTiaPush_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPushAsync("AI", service => service.PushHwSectionAsync(_project, "AI"));

    private async void RtdHwTiaPull_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPullAsync("RTD", service => service.PullHwSectionAsync(_project, "RTD"));
    private async void RtdHwTiaPush_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPushAsync("RTD", service => service.PushHwSectionAsync(_project, "RTD"));

    private async void CounterHwTiaPull_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPullAsync("COUNTER", service => service.PullHwSectionAsync(_project, "COUNTER"));
    private async void CounterHwTiaPush_Click(object sender, RoutedEventArgs e) => await ConfirmAndRunHwPushAsync("COUNTER", service => service.PushHwSectionAsync(_project, "COUNTER"));

    private async Task ConfirmAndRunHwPullAsync(string section, Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation)
    {
        if (MessageBox.Show(this,
                $"Pull HW {section} will overwrite only the {section} section in HW_ID_s (DB56) for the active PLC database.",
                $"Pull HW {section}",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunHwTiaOperationAsync(section, operation, $"Pulling HW {section} from HW_ID_s...", rebindAfterSuccess: true);
    }

    private async Task ConfirmAndRunHwPushAsync(string section, Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation)
    {
        if (MessageBox.Show(this,
                $"Push HW {section} will update only the {section} section in HW_ID_s (DB56). DT/UDT files and Reset start values are skipped.",
                $"Push HW {section}",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunHwTiaOperationAsync(section, operation, $"Pushing HW {section} to HW_ID_s...", rebindAfterSuccess: true, minimizeTiaPortal: true);
    }

    private async Task RunHwTiaOperationAsync(
        string section,
        Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation,
        string busyText,
        bool rebindAfterSuccess = false,
        bool minimizeTiaPortal = false)
    {
        if (_dbDiTiaOperationRunning)
        {
            ShowMessage("TIA operation is already running.");
            return;
        }

        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal from TIA Sync first.", $"HW {section} TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        SetHwTiaBusy(section, true, busyText);
        TiaProgressDialog? progressDialog = null;
        try
        {
            _dbDiTiaOperationRunning = true;
            if (minimizeTiaPortal)
            {
                TiaPortalWindowService.MinimizePortalWindow(_tiaSessionService.CurrentSession);
            }

            progressDialog = new TiaProgressDialog($"HW {section} TIA Sync", busyText)
            {
                Owner = EditorOwner
            };
            progressDialog.Show();

            var service = new TiaLiveSyncService(_tiaSessionService);
            var operationTask = operation(service);
            var completedTask = await Task.WhenAny(operationTask, Task.Delay(TimeSpan.FromMinutes(2)));
            if (completedTask != operationTask)
            {
                _tiaSessionService.AbortHelper();
                SetHwTiaBusy(section, false, "HW_ID_s: timeout");
                progressDialog.Close();
                progressDialog = null;
                MessageBox.Show(this,
                    $"HW {section} TIA Sync did not finish within 2 minutes. The helper process was stopped. Attach to TIA Portal again before the next operation.",
                    $"HW {section} TIA Sync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = await operationTask;
            if (rebindAfterSuccess)
            {
                progressDialog.SetStatus("Refreshing Plant Configurator project data...");
                SaveActivePlcDatabase();
                _validationStateUpdater.Update(_project);
                BindProject();
            }

            CancelPendingTiaStatusCheck();
            SetHwTiaBusy(section, false, result.Message);
            UpdateTiaVisualState();
            ShowMessage(result.Message);
            if (result.Warnings.Count > 0)
            {
                progressDialog.Close();
                progressDialog = null;
                ShowTiaResult($"HW {section} TIA Sync", result);
            }
        }
        catch (Exception ex)
        {
            SetHwTiaBusy(section, false, "HW_ID_s: error");
            progressDialog?.Close();
            progressDialog = null;
            ShowError(ex.Message);
        }
        finally
        {
            progressDialog?.Close();
            _dbDiTiaOperationRunning = false;
            SetHwTiaBusy(section, false);
            UpdateToolbarVisibility();
        }
    }

    private async Task RunDbDiTiaOperationAsync(
        string busyText,
        Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation,
        bool rebindAfterSuccess = false,
        string progressTitle = "TIA operation",
        string progressStatus = "Working with TIA Portal...",
        bool showProgressDialog = false,
        bool minimizeTiaPortal = false)
    {
        if (_dbDiTiaOperationRunning)
        {
            ShowMessage("DB DI TIA operation is already running.");
            return;
        }

        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal from TIA Sync first.", "DB DI TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        SetDbDiTiaBusy(true, busyText);
        TiaProgressDialog? progressDialog = null;
        try
        {
            _dbDiTiaOperationRunning = true;
            if (minimizeTiaPortal)
            {
                TiaPortalWindowService.MinimizePortalWindow(_tiaSessionService.CurrentSession);
            }

            if (showProgressDialog)
            {
                progressDialog = new TiaProgressDialog(progressTitle, progressStatus)
                {
                    Owner = EditorOwner
                };
                progressDialog.Show();
            }

            var service = new TiaLiveSyncService(_tiaSessionService);
            var operationTask = operation(service);
            var completedTask = await Task.WhenAny(operationTask, Task.Delay(TimeSpan.FromMinutes(2)));
            if (completedTask != operationTask)
            {
                _tiaSessionService.AbortHelper();
                DbDiTiaStatusText.Text = "TIA: timeout";
                progressDialog?.Close();
                progressDialog = null;
                MessageBox.Show(this,
                    $"{progressTitle} did not finish within 2 minutes. The helper process was stopped. Attach to TIA Portal again before the next operation.",
                    "DB DI TIA Sync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var result = await operationTask;
            if (rebindAfterSuccess)
            {
                progressDialog?.SetStatus("Refreshing Plant Configurator project data...");
                SaveActivePlcDatabase();
                _validationStateUpdater.Update(_project);
                BindProject();
                MarkTiaSectionState("DB DI", "InSync", "DB DI is synchronized with TIA.");
            }

            CancelPendingTiaStatusCheck();
            DbDiTiaStatusText.Text = result.Message;
            ShowMessage(result.Message);
            if (result.Warnings.Count > 0)
            {
                progressDialog?.Close();
                progressDialog = null;
                ShowTiaResult("DB DI TIA Sync", result);
            }
        }
        catch (Exception ex)
        {
            DbDiTiaStatusText.Text = "TIA: error";
            progressDialog?.Close();
            progressDialog = null;
            ShowError(ex.Message);
        }
        finally
        {
            progressDialog?.Close();
            _dbDiTiaOperationRunning = false;
            SetDbDiTiaBusy(false);
            UpdateToolbarVisibility();
        }
    }

    private void SetDbDiTiaBusy(bool busy, string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            DbDiTiaStatusText.Text = status;
        }

        UpdateActiveTiaToolbarBusy("DB DI", busy);
    }

    private void SetDbFamilyTiaBusy(string dbName, bool busy, string? status = null)
    {
        var statusText = GetDbTiaStatusText(dbName);

        if (!string.IsNullOrWhiteSpace(status))
        {
            statusText.Text = status;
        }

        UpdateActiveTiaToolbarBusy(dbName, busy);
    }

    private TextBlock GetDbTiaStatusText(string dbName)
    {
        return dbName.ToUpperInvariant() switch
        {
            "DB DI" => DbDiTiaStatusText,
            "DB DO" => DbDoTiaStatusText,
            "DB MEAS" => DbMeasTiaStatusText,
            "DB AO" => DbAoTiaStatusText,
            "DB ALM" => DbAlmTiaStatusText,
            "DB PAR" => DbParTiaStatusText,
            "DB SEQUENCE" => DbSequenceTiaStatusText,
            _ => throw new NotSupportedException($"DB family '{dbName}' is not supported.")
        };
    }

    private void UpdateActiveTiaToolbarBusy(string dbName, bool busy)
    {
        if (!NormalizeDbName(GetActiveTabKey()).Equals(dbName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TabTiaPullButton.Opacity = busy ? 0.72 : 1.0;
        TabTiaPushButton.Opacity = busy ? 0.72 : 1.0;
        TabTiaPullButton.IsEnabled = !busy;
        TabTiaPushButton.IsEnabled = !busy;
    }

    private void MarkTiaSectionState(string section, string state, string message)
    {
        var binding = GetActiveTiaBinding();
        if (binding is null)
        {
            return;
        }

        var item = binding.SectionStates.FirstOrDefault(row => row.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new TiaSectionSyncState { Section = section };
            binding.SectionStates.Add(item);
        }

        item.State = state;
        item.Message = message;
        item.LastCheckedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private void SetHwTiaBusy(string section, bool busy, string? status = null)
    {
        var (statusText, pullButton, pushButton) = section.ToUpperInvariant() switch
        {
            "DI" => (DiHwTiaStatusText, DiHwTiaPullButton, DiHwTiaPushButton),
            "DO" => (DoHwTiaStatusText, DoHwTiaPullButton, DoHwTiaPushButton),
            "AI" => (AiHwTiaStatusText, AiHwTiaPullButton, AiHwTiaPushButton),
            "RTD" => (RtdHwTiaStatusText, RtdHwTiaPullButton, RtdHwTiaPushButton),
            "COUNTER" => (CounterHwTiaStatusText, CounterHwTiaPullButton, CounterHwTiaPushButton),
            _ => throw new NotSupportedException($"HW section '{section}' is not supported.")
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            statusText.Text = status;
        }

        pullButton.Opacity = busy ? 0.72 : 1.0;
        pushButton.Opacity = busy ? 0.72 : 1.0;
        pullButton.IsEnabled = !busy;
        pushButton.IsEnabled = !busy;
    }

    private async Task RefreshHwTiaStatusAsync()
    {
        if (_dbDiTiaOperationRunning || _dbCrossReferenceUpdateRunning || !_projectOpen || _tiaSessionService.CurrentSession is null)
        {
            UpdateTiaVisualState();
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        try
        {
            _dbDiTiaOperationRunning = true;
            SetAllHwTiaBusy(true, "Checking HW_ID_s...");
            var service = new TiaLiveSyncService(_tiaSessionService);
            await service.CheckHwSectionsAsync(_project);
            UpdateTiaVisualState();
            ShowMessage("HW_ID_s status refreshed.");
        }
        catch (Exception ex)
        {
            SetAllHwTiaStatus("HW_ID_s: check error");
            ShowError(ex.Message);
        }
        finally
        {
            _dbDiTiaOperationRunning = false;
            SetAllHwTiaBusy(false);
        }
    }

    private void ScheduleActiveTabTiaStatusCheck()
    {
        if (_tiaSessionService.CurrentSession is null || !_projectOpen)
        {
            return;
        }

        _tiaStatusTimer.Stop();
        _tiaStatusTimer.Start();
    }

    private void MarkActiveTabTiaStatusDirty()
    {
        if (_bindingProject || _dbDiTiaOperationRunning || _dbCrossReferenceUpdateRunning || _tiaStatusRefreshRunning)
        {
            return;
        }

        var tabKey = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        if (!TryGetTiaSectionKey(tabKey, out var section))
        {
            return;
        }

        var binding = GetActiveTiaBinding();
        if (binding is not null)
        {
            var state = binding.SectionStates.FirstOrDefault(item => item.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                state = new TiaSectionSyncState { Section = section };
                binding.SectionStates.Add(state);
            }

            state.State = "Differs";
            state.Message = $"{section}: local changes pending TIA check.";
            state.LastCheckedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            UpdateTiaVisualState();
        }

        ScheduleActiveTabTiaStatusCheck();
    }

    private static bool TryGetTiaSectionKey(string tabKey, out string section)
    {
        section = tabKey switch
        {
            "DI" or "DO" or "AI" or "RTD" or "COUNTER" => "HW " + tabKey,
            "DB DI" or "DB DO" or "DB MEAS" or "DB AO" or "DB ALM" or "DB PAR" => tabKey,
            "Sequence" => "DB SEQUENCE",
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(section);
    }

    private void CancelPendingTiaStatusCheck()
    {
        if (_tiaStatusTimer.IsEnabled)
        {
            _tiaStatusTimer.Stop();
        }
    }

    private async void TiaStatusTimer_Tick(object? sender, EventArgs e)
    {
        _tiaStatusTimer.Stop();
        await RefreshActiveTabTiaStatusAsync();
    }

    private async Task RefreshActiveTabTiaStatusAsync()
    {
        if (_tiaStatusRefreshRunning || _dbDiTiaOperationRunning || _dbCrossReferenceUpdateRunning || !_projectOpen || _tiaSessionService.CurrentSession is null)
        {
            return;
        }

        var tabKey = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        if (string.IsNullOrWhiteSpace(tabKey))
        {
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        try
        {
            _tiaStatusRefreshRunning = true;
            var service = new TiaLiveSyncService(_tiaSessionService);
            var checkTask = CheckTiaSectionAsync(service, tabKey);
            if (checkTask is null)
            {
                return;
            }

            await checkTask;
            UpdateTiaVisualState();
            StatusText.Text = $"{tabKey} TIA status refreshed: {DateTime.Now:HH:mm:ss}";
        }
        catch
        {
            SetActiveTabTiaStatus("TIA: check error");
            StatusText.Text = "TIA status check failed.";
            UpdateTiaVisualState();
        }
        finally
        {
            _tiaStatusRefreshRunning = false;
        }
    }

    private Task<TiaLiveSyncResult>? CheckTiaSectionAsync(TiaLiveSyncService service, string tabKey)
    {
        return tabKey switch
        {
            "DI" or "DO" or "AI" or "RTD" or "COUNTER" => service.CheckHwSectionAsync(_project, tabKey),
            "DB DI" or "DB DO" or "DB MEAS" or "DB AO" or "DB ALM" or "DB PAR" => service.CheckDbAsync(_project, tabKey),
            "Sequence" => service.CheckDbAsync(_project, "DB SEQUENCE"),
            _ => null
        };
    }

    private void SetActiveTabTiaStatus(string status)
    {
        var tabKey = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        switch (tabKey)
        {
            case "DI": DiHwTiaStatusText.Text = status; break;
            case "DO": DoHwTiaStatusText.Text = status; break;
            case "AI": AiHwTiaStatusText.Text = status; break;
            case "RTD": RtdHwTiaStatusText.Text = status; break;
            case "COUNTER": CounterHwTiaStatusText.Text = status; break;
            case "DB DI": DbDiTiaStatusText.Text = status; break;
            case "DB DO": DbDoTiaStatusText.Text = status; break;
            case "DB MEAS": DbMeasTiaStatusText.Text = status; break;
            case "DB AO": DbAoTiaStatusText.Text = status; break;
            case "DB ALM": DbAlmTiaStatusText.Text = status; break;
            case "DB PAR": DbParTiaStatusText.Text = status; break;
            case "Sequence": DbSequenceTiaStatusText.Text = status; break;
        }
    }

    private async Task RefreshAllTiaStatusAsync(bool showStatusMessage = false)
    {
        if (_tiaStatusRefreshRunning || _dbDiTiaOperationRunning || _dbCrossReferenceUpdateRunning || !_projectOpen)
        {
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            CancelPendingTiaStatusCheck();
            UpdateTiaVisualState();
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        try
        {
            _tiaStatusRefreshRunning = true;
            SetAllTiaStatus("TIA: checking...");
            var service = new TiaLiveSyncService(_tiaSessionService);
            await service.CheckHwSectionsAsync(_project);
            foreach (var dbName in new[] { "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR", "DB SEQUENCE" })
            {
                await service.CheckDbAsync(_project, dbName);
            }

            UpdateTiaVisualState();
            StatusText.Text = showStatusMessage ? "TIA status refreshed." : $"TIA status refreshed: {DateTime.Now:HH:mm:ss}";
        }
        catch
        {
            SetAllTiaStatus("TIA: check error");
            CancelPendingTiaStatusCheck();
            _tiaSessionService.AbortHelper();
            StatusText.Text = "TIA status check failed. Attach to TIA again.";
            UpdateTiaVisualState();
        }
        finally
        {
            _tiaStatusRefreshRunning = false;
        }
    }

    private void SetAllTiaStatus(string status)
    {
        SetAllHwTiaStatus(status);
        DbDiTiaStatusText.Text = status;
        DbDoTiaStatusText.Text = status;
        DbMeasTiaStatusText.Text = status;
        DbAoTiaStatusText.Text = status;
        DbAlmTiaStatusText.Text = status;
        DbParTiaStatusText.Text = status;
        DbSequenceTiaStatusText.Text = status;
    }

    private void SetAllHwTiaBusy(bool busy, string? status = null)
    {
        foreach (var section in new[] { "DI", "DO", "AI", "RTD", "COUNTER" })
        {
            SetHwTiaBusy(section, busy, status);
        }
    }

    private void SetAllHwTiaStatus(string status)
    {
        DiHwTiaStatusText.Text = status;
        DoHwTiaStatusText.Text = status;
        AiHwTiaStatusText.Text = status;
        RtdHwTiaStatusText.Text = status;
        CounterHwTiaStatusText.Text = status;
    }

    private void InitializeTiaTabIndicators()
    {
        AddTiaTabIndicator(DiTab, "DI", "HW DI");
        AddTiaTabIndicator(DoTab, "DO", "HW DO");
        AddTiaTabIndicator(AiTab, "AI", "HW AI");
        AddTiaTabIndicator(RtdTab, "RTD", "HW RTD");
        AddTiaTabIndicator(CounterTab, "COUNTER", "HW COUNTER");
        AddTiaTabIndicator(DbDiTab, "DB DI", "DB DI");
        AddTiaTabIndicator(DbDoTab, "DB DO", "DB DO");
        AddTiaTabIndicator(DbMeasTab, "DB MEAS", "DB MEAS");
        AddTiaTabIndicator(DbAoTab, "DB AO", "DB AO");
        AddTiaTabIndicator(DbAlmTab, "DB ALM", "DB ALM");
        AddTiaTabIndicator(DbParTab, "DB PAR", "DB PAR");
        AddTiaTabIndicator(SequenceTab, "Sequence", "DB SEQUENCE");
    }

    private void AddTiaTabIndicator(TabItem tab, string label, string stateKey)
    {
        tab.Tag = label;
        var indicator = new Border
        {
            Height = 4,
            MinWidth = 64,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(208, 215, 222)),
            BorderThickness = new Thickness(1, 0, 1, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        tab.Header = new Grid
        {
            MinWidth = Math.Max(72, label.Length * 9),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 5)
                },
                indicator
            }
        };
        Grid.SetRow(indicator, 0);
        indicator.VerticalAlignment = VerticalAlignment.Bottom;
        _tiaTabIndicators[stateKey] = indicator;
    }

    private void UpdateTiaVisualState()
    {
        var attached = _tiaSessionService.CurrentSession is not null;
        var binding = GetActiveTiaBinding();
        UpdateHwTabVisual(DiTab, DiHwTiaStatusText, attached, binding, "HW DI");
        UpdateHwTabVisual(DoTab, DoHwTiaStatusText, attached, binding, "HW DO");
        UpdateHwTabVisual(AiTab, AiHwTiaStatusText, attached, binding, "HW AI");
        UpdateHwTabVisual(RtdTab, RtdHwTiaStatusText, attached, binding, "HW RTD");
        UpdateHwTabVisual(CounterTab, CounterHwTiaStatusText, attached, binding, "HW COUNTER");
        UpdateDbTabVisual(DbDiTab, DbDiTiaStatusText, attached, binding, "DB DI");
        UpdateDbTabVisual(DbDoTab, DbDoTiaStatusText, attached, binding, "DB DO");
        UpdateDbTabVisual(DbMeasTab, DbMeasTiaStatusText, attached, binding, "DB MEAS");
        UpdateDbTabVisual(DbAoTab, DbAoTiaStatusText, attached, binding, "DB AO");
        UpdateDbTabVisual(DbAlmTab, DbAlmTiaStatusText, attached, binding, "DB ALM");
        UpdateDbTabVisual(DbParTab, DbParTiaStatusText, attached, binding, "DB PAR");
        UpdateDbTabVisual(SequenceTab, DbSequenceTiaStatusText, attached, binding, "DB SEQUENCE");
        UpdatePlcDatabaseTiaVisualStates();
        PlcDatabaseTree.Items.Refresh();
    }

    private void UpdateHwTabVisual(TabItem tab, TextBlock statusText, bool attached, TiaPlcBinding? binding, string section)
    {
        var state = attached ? GetSectionVisualState(binding, section) : "NotAttached";
        UpdateTabIndicator(section, state);
        statusText.Text = state switch
        {
            "InSync" => "HW_ID_s: in sync",
            "Differs" => "HW_ID_s: differs",
            _ => attached ? "HW_ID_s: not checked" : "HW_ID_s: not attached"
        };
    }

    private void UpdateDbTabVisual(TabItem tab, TextBlock statusText, bool attached, TiaPlcBinding? binding, string section)
    {
        var state = attached ? GetSectionVisualState(binding, section) : "NotAttached";
        UpdateTabIndicator(section, state);
        statusText.Text = state switch
        {
            "InSync" => "TIA: in sync",
            "Differs" => "TIA: differs",
            _ => attached ? "TIA: not checked" : "TIA: not attached"
        };
    }

    private void UpdateTabIndicator(string key, string state)
    {
        if (!_tiaTabIndicators.TryGetValue(key, out var indicator))
        {
            return;
        }

        indicator.Background = GetTiaStateBrush(state);
        indicator.BorderBrush = GetTiaStateBorderBrush(state);
    }

    private void UpdatePlcDatabaseTiaVisualStates()
    {
        var attached = _tiaSessionService.CurrentSession is not null;
        foreach (var database in _project.PlcDatabases)
        {
            var binding = _project.TiaSync?.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
            database.TiaSyncVisualState = attached ? GetBindingVisualState(binding) : "NotAttached";
            if (!attached || binding is null)
            {
                database.TiaOnlineState = "";
            }
        }
    }

    private TiaPlcBinding? GetActiveTiaBinding()
    {
        return _project.TiaSync?.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == _project.ActivePlcDatabaseId);
    }

    private string GetConfigurationDataBlockNameForDb(string dbName)
    {
        var activeCatalog = GetActivePlcDatabase()?.DbCatalog ?? _project.DbCatalog;
        return activeCatalog.DataBlockNames
            .Where(item => item.DbName.Equals(dbName, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.DataBlockName?.Trim() ?? "")
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";
    }

    private static string GetBindingVisualState(TiaPlcBinding? binding)
    {
        if (binding is null)
        {
            return "NotAttached";
        }

        var sections = new[] { "HW DI", "HW DO", "HW AI", "HW RTD", "HW COUNTER", "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR" };
        var states = sections
            .Select(section => GetSectionVisualState(binding, section))
            .Where(state => state != "NotAttached")
            .ToList();
        if (states.Any(state => state == "Differs"))
        {
            return "Differs";
        }

        return states.Count == sections.Length && states.All(state => state == "InSync")
            ? "InSync"
            : "NotAttached";
    }

    private static string GetSectionVisualState(TiaPlcBinding? binding, string section)
    {
        var state = binding?.SectionStates.FirstOrDefault(item => item.Section.Equals(section, StringComparison.OrdinalIgnoreCase))?.State;
        return state?.Equals("Differs", StringComparison.OrdinalIgnoreCase) == true
            ? "Differs"
            : state?.Equals("InSync", StringComparison.OrdinalIgnoreCase) == true
                ? "InSync"
                : "NotAttached";
    }

    private async Task RefreshPlcDatabaseOnlineStatesAsync()
    {
        if (_tiaSessionService.CurrentSession is null || _project.TiaSync is null)
        {
            UpdateTiaVisualState();
            return;
        }

        foreach (var database in _project.PlcDatabases)
        {
            var binding = _project.TiaSync.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
            if (binding is null)
            {
                database.TiaOnlineState = "";
                continue;
            }

            try
            {
                var result = await _tiaSessionService.ExecutePlcControlAsync(new TiaPlcControlRequest
                {
                    PlcName = binding.PlcName,
                    DeviceName = binding.DeviceName,
                    SoftwareName = binding.SoftwareName,
                    Command = "GetOnlineState"
                });
                database.TiaOnlineState = NormalizeTiaOnlineState(result.State, "");
            }
            catch
            {
                database.TiaOnlineState = "";
            }
        }

        UpdateTiaVisualState();
    }

    private static string NormalizeTiaOnlineState(string state, string fallback)
    {
        if (state.Contains("Online", StringComparison.OrdinalIgnoreCase))
        {
            return "Online";
        }

        if (state.Contains("Offline", StringComparison.OrdinalIgnoreCase))
        {
            return "Offline";
        }

        return fallback;
    }

    private static Brush GetTiaStateBrush(string state)
    {
        return state switch
        {
            "InSync" => new SolidColorBrush(Color.FromRgb(218, 251, 225)),
            "Differs" => new SolidColorBrush(Color.FromRgb(255, 235, 233)),
            _ => Brushes.White
        };
    }

    private static Brush GetTiaStateBorderBrush(string state)
    {
        return state switch
        {
            "InSync" => new SolidColorBrush(Color.FromRgb(45, 164, 78)),
            "Differs" => new SolidColorBrush(Color.FromRgb(207, 34, 46)),
            _ => new SolidColorBrush(Color.FromRgb(208, 215, 222))
        };
    }

    private void AddPlcDatabase_Click(object sender, RoutedEventArgs e)
    {
        SaveActivePlcDatabase();
        var suggestedName = $"PLC {_project.PlcDatabases.Count + 1}";
        var dialog = new PlcDatabaseNameDialog(suggestedName)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var database = PlcDatabase.CreateDefault(dialog.DatabaseName);
        _project.PlcDatabases.Add(database);
        _project.ActivePlcDatabaseId = database.Id;
        database.ApplyTo(_project);
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Added PLC database: {database.Name}");
    }

    private void RenamePlcDatabase_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        var dialog = new PlcDatabaseNameDialog(database.Name)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        database.Name = dialog.DatabaseName;
        BindPlcDatabaseTree();
        ShowMessage($"Renamed PLC database: {database.Name}");
    }

    private void CopyPlcDatabase_Click(object sender, RoutedEventArgs e)
    {
        SaveActivePlcDatabase();
        var source = GetSelectedPlcDatabase();
        if (source is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        var copy = ClonePlcDatabase(source, $"{source.Name} Copy");
        _project.PlcDatabases.Add(copy);
        _project.ActivePlcDatabaseId = copy.Id;
        copy.ApplyTo(_project);
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Copied PLC database: {copy.Name}");
    }

    private void AddScadaDatabase_Click(object sender, RoutedEventArgs e)
    {
        SaveActiveScadaDatabase();
        var suggestedName = $"SCADA {_project.ScadaDatabases.Count + 1}";
        var dialog = new PlcDatabaseNameDialog(suggestedName)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var database = ScadaDatabase.CreateDefault();
        database.Name = dialog.DatabaseName;
        _project.ScadaDatabases.Add(database);
        _project.ActiveScadaDatabaseId = database.Id;
        _project.Scada = database;
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Added SCADA database: {database.Name}");
    }

    private void RenameScadaDatabase_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedScadaDatabase();
        if (database is null)
        {
            ShowMessage("Select SCADA database first.");
            return;
        }

        var dialog = new PlcDatabaseNameDialog(database.Name)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        database.Name = dialog.DatabaseName;
        BindScadaDatabaseTree();
        ShowMessage($"Renamed SCADA database: {database.Name}");
    }

    private void CopyScadaDatabase_Click(object sender, RoutedEventArgs e)
    {
        SaveActiveScadaDatabase();
        var source = GetSelectedScadaDatabase();
        if (source is null)
        {
            ShowMessage("Select SCADA database first.");
            return;
        }

        var copy = CloneScadaDatabase(source, $"{source.Name} Copy");
        _project.ScadaDatabases.Add(copy);
        _project.ActiveScadaDatabaseId = copy.Id;
        _project.Scada = copy;
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Copied SCADA database: {copy.Name}");
    }

    private void DeleteScadaDatabase_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedScadaDatabase();
        if (database is null)
        {
            ShowMessage("Select SCADA database first.");
            return;
        }

        if (_project.ScadaDatabases.Count <= 1)
        {
            MessageBox.Show(this, "Ne mozes izbrisati zadnju SCADA bazu.", "SCADA database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this, $"Izbrisati SCADA bazu '{database.Name}'?", "Delete SCADA database", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _project.ScadaDatabases.Remove(database);
        var next = _project.ScadaDatabases.First();
        _project.ActiveScadaDatabaseId = next.Id;
        _project.Scada = next;
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Deleted SCADA database: {database.Name}");
    }

    private async void PullSelectedPlcDatabaseFromTia_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        if (MessageBox.Show(this,
                $"Pull whole database '{database.Name}' from the mapped TIA PLC?",
                "TIA pull whole database",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunWholeDatabaseTiaOperationAsync(database, service => service.PullActiveDatabaseAsync(_project), $"Pulling {database.Name} from TIA...");
    }

    private async void PushSelectedPlcDatabaseToTia_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        if (MessageBox.Show(this,
                $"Push whole database '{database.Name}' to the mapped TIA PLC?",
                "TIA push whole database",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunWholeDatabaseTiaOperationAsync(database, service => service.PushActiveDatabaseAsync(_project), $"Pushing {database.Name} to TIA...", minimizeTiaPortal: true);
    }

    private async void PlcDatabaseGoOnline_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        await RunPlcDatabaseControlAsync(database, "GoOnline", $"Force '{database.Name}' online in TIA Portal?");
    }

    private async void PlcDatabaseGoOffline_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        await RunPlcDatabaseControlAsync(database, "GoOffline", $"Switch '{database.Name}' offline in TIA Portal?");
    }

    private async Task RunPlcDatabaseControlAsync(PlcDatabase database, string command, string confirmation, IReadOnlyList<string>? dataBlockNames = null)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal from TIA Sync first.", "TIA PLC control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var binding = _project.TiaSync.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
        if (binding is null)
        {
            MessageBox.Show(this, "PLC database is not mapped to TIA software.", "TIA PLC control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, confirmation, "TIA PLC control", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            StatusText.Text = $"{command}: {database.Name}...";
            var result = await _tiaSessionService.ExecutePlcControlAsync(new TiaPlcControlRequest
            {
                PlcName = binding.PlcName,
                DeviceName = binding.DeviceName,
                SoftwareName = binding.SoftwareName,
                Command = command,
                DataBlockNames = dataBlockNames?.ToList() ?? []
            });

            database.TiaOnlineState = NormalizeTiaOnlineState(result.State, database.TiaOnlineState);
            UpdateTiaVisualState();
            var summary = string.IsNullOrWhiteSpace(result.State)
                ? result.Command
                : $"{result.Command}: {result.State}";
            ShowOperationResult("TIA PLC control", summary, result.Warnings, result.Messages, result.Success);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RunWholeDatabaseTiaOperationAsync(
        PlcDatabase database,
        Func<TiaLiveSyncService, Task<TiaLiveSyncResult>> operation,
        string busyText,
        bool minimizeTiaPortal = false)
    {
        if (_dbDiTiaOperationRunning)
        {
            ShowMessage("TIA operation is already running.");
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal from TIA Sync first.", "TIA Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveActivePlcDatabase();
        _project.ActivePlcDatabaseId = database.Id;
        database.ApplyTo(_project);
        EnsureProjectDefaults();

        TiaProgressDialog? progressDialog = null;
        try
        {
            _dbDiTiaOperationRunning = true;
            if (minimizeTiaPortal)
            {
                TiaPortalWindowService.MinimizePortalWindow(_tiaSessionService.CurrentSession);
            }

            progressDialog = new TiaProgressDialog("TIA Sync", busyText) { Owner = EditorOwner };
            progressDialog.Show();
            SetAllHwTiaBusy(true, busyText);

            var service = new TiaLiveSyncService(_tiaSessionService);
            var result = await operation(service);
            progressDialog.SetStatus("Refreshing Plant Configurator project data...");
            SaveActivePlcDatabase();
            _validationStateUpdater.Update(_project);
            BindProject();
            CancelPendingTiaStatusCheck();
            UpdateTiaVisualState();
            ShowMessage(result.Message);
            if (result.Warnings.Count > 0)
            {
                ShowTiaResult("TIA Sync", result);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            progressDialog?.Close();
            _dbDiTiaOperationRunning = false;
            SetAllHwTiaBusy(false);
            UpdateTiaVisualState();
        }
    }

    private void DeletePlcDatabase_Click(object sender, RoutedEventArgs e)
    {
        var database = GetSelectedPlcDatabase();
        if (database is null)
        {
            ShowMessage("Select PLC database first.");
            return;
        }

        if (_project.PlcDatabases.Count <= 1)
        {
            MessageBox.Show(this, "Ne mozes izbrisati zadnju PLC bazu.", "PLC database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(this, $"Izbrisati PLC bazu '{database.Name}'?", "Delete PLC database", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _project.PlcDatabases.Remove(database);
        var next = _project.PlcDatabases.First();
        _project.ActivePlcDatabaseId = next.Id;
        next.ApplyTo(_project);
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Deleted PLC database: {database.Name}");
    }

    private void LoadPlcDatabaseFromExisting_Click(object sender, RoutedEventArgs e)
    {
        SaveActivePlcDatabase();
        var target = GetSelectedPlcDatabase();
        if (target is null)
        {
            ShowMessage("Select target PLC database first.");
            return;
        }

        var sources = _project.PlcDatabases.Where(database => database.Id != target.Id).ToList();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "Nema druge PLC baze iz koje se moze ucitati.", "PLC database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PlcDatabasePickerDialog($"Load data into '{target.Name}' from:", sources)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true || dialog.SelectedDatabase is null)
        {
            return;
        }

        ReplacePlcDatabaseContent(target, dialog.SelectedDatabase);
        _project.ActivePlcDatabaseId = target.Id;
        target.ApplyTo(_project);
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Loaded {target.Name} from {dialog.SelectedDatabase.Name}.");
    }

    private void ImportTiaPortalExport_Click(object sender, RoutedEventArgs e)
    {
        SaveActivePlcDatabase();
        var target = GetSelectedPlcDatabase();
        if (target is null)
        {
            ShowMessage("Select target PLC database first.");
            return;
        }

        _project.ActivePlcDatabaseId = target.Id;
        target.ApplyTo(_project);
        EnsureProjectDefaults();

        var initialFolder = !string.IsNullOrWhiteSpace(_project.Paths.TiaPortalExportFolder)
            ? _project.Paths.TiaPortalExportFolder
            : _project.Paths.OutputFolder;
        var dialog = new TiaPortalImportDialog(initialFolder)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _project.Paths.TiaPortalExportFolder = dialog.FolderPath;
            var result = _tiaPortalImporter.Import(_project, dialog.SelectedFiles);
            _validationStateUpdater.Update(_project);
            SaveActivePlcDatabase();
            BindProject();
            var message = $"Imported TIA Portal export into {target.Name}:{Environment.NewLine}{string.Join(Environment.NewLine, result.Messages)}";
            ShowMessage(message);
            MessageBox.Show(this, message, "TIA Portal import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void PlcDatabaseTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loadingPlcDatabaseTree || e.NewValue is not PlcDatabase database || database.Id == _project.ActivePlcDatabaseId)
        {
            return;
        }

        SaveActivePlcDatabase();
        _project.ActivePlcDatabaseId = database.Id;
        database.ApplyTo(_project);
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Active PLC database: {database.Name}");
    }

    private void ScadaDatabaseTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loadingPlcDatabaseTree || e.NewValue is not ScadaDatabase database || database.Id == _project.ActiveScadaDatabaseId)
        {
            return;
        }

        SaveActiveScadaDatabase();
        _project.ActiveScadaDatabaseId = database.Id;
        _project.Scada = database;
        EnsureProjectDefaults();
        BindProject();
        ShowMessage($"Active SCADA database: {database.Name}");
    }

    private void TogglePlcPane_Click(object sender, RoutedEventArgs e)
    {
        var collapsing = PlcPaneContent.Visibility == Visibility.Visible;
        PlcPaneContent.Visibility = collapsing ? Visibility.Collapsed : Visibility.Visible;
        PlcPaneColumn.Width = collapsing ? new GridLength(44) : new GridLength(260);
        TogglePlcPaneButton.Content = collapsing ? ">" : "<";
    }

    private void PlcDatabaseTreeItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void DiModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _diView?.Refresh();
    }

    private void DoModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _doView?.Refresh();
    }

    private void AiModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _aiView?.Refresh();
    }

    private void RtdModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _rtdView?.Refresh();
    }

    private void CounterModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _counterView?.Refresh();
    }

    private void AoModuleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        _aoView?.Refresh();
    }

    private void ClearDiModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        DiModuleFilter.SelectedIndex = 0;
    }

    private void ClearDoModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        DoModuleFilter.SelectedIndex = 0;
    }

    private void ClearAiModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        AiModuleFilter.SelectedIndex = 0;
    }

    private void ClearRtdModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        RtdModuleFilter.SelectedIndex = 0;
    }

    private void ClearCounterModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        CounterModuleFilter.SelectedIndex = 0;
    }

    private void ClearAoModuleFilter_Click(object sender, RoutedEventArgs e)
    {
        AoModuleFilter.SelectedIndex = 0;
    }

    private void ConfigureDiRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DigitalSignalConfig row })
        {
            return;
        }

        var dialog = new DigitalInputConfigDialog(row, _project.DbCatalog.DiRows)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Config = dialog.SelectedConfig;
        row.Id = dialog.SelectedId;
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DI row configured: module {row.ModuleNumber}, port {row.Port}, ID {row.Id}, {row.Config}.");
    }

    private void ConfigureDoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DigitalSignalConfig row })
        {
            return;
        }

        var dialog = new DigitalInputConfigDialog(row, _project.DbCatalog.DoRows, "DO", includeInverted: false)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Config = dialog.SelectedConfig;
        row.Id = dialog.SelectedId;
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DO row configured: module {row.ModuleNumber}, port {row.Port}, ID {row.Id}, {row.Config}.");
    }

    private void ConfigureAiRow_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAnalogRow(sender, "AI");
    }

    private void ConfigureRtdRow_Click(object sender, RoutedEventArgs e)
    {
        ConfigureAnalogRow(sender, "RTD");
    }

    private void ConfigureCounterRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CounterConfig row })
        {
            return;
        }

        var dialog = new CounterConfigDialog(row, _project.DbCatalog.MeasRows)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Config = dialog.SelectedConfig;
        row.Id = dialog.SelectedId;
        row.FilterTime = dialog.SelectedFilterTime;
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"COUNTER row configured: module {row.ModuleNumber}, ID {row.Id}, {row.Config}.");
    }

    private void ConfigureAoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AnalogOutputConfig row })
        {
            return;
        }

        var dialog = new AnalogOutputConfigDialog(row, _project.DbCatalog.AoRows)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Config = dialog.SelectedConfig;
        row.Id = dialog.SelectedId;
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"AO row configured: module {row.ModuleNumber}, port {row.Port}, ID {row.Id}, {row.Config}.");
    }

    private void ConfigureAnalogRow(object sender, string signalType)
    {
        if (sender is not Button { DataContext: AnalogSignalConfig row })
        {
            return;
        }

        var dialog = new AnalogConfigDialog(row, _project.DbCatalog.MeasRows, signalType)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.Config = dialog.SelectedConfig;
        row.Id = dialog.SelectedId;
        row.FilterTime = dialog.SelectedFilterTime;
        row.CustomLimits = dialog.SelectedCustomLimits;
        row.LowLimit = dialog.SelectedLowLimit;
        row.HighLimit = dialog.SelectedHighLimit;
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"{signalType} row configured: module {row.ModuleNumber}, port {row.Port}, ID {row.Id}, {row.Config}.");
    }

    private void DiGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView("DI");
            MarkActiveTabTiaStatusDirty();
        }), DispatcherPriority.Background);
    }

    private void DoGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView("DO");
            MarkActiveTabTiaStatusDirty();
        }), DispatcherPriority.Background);
    }

    private void AiGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView("AI");
            MarkActiveTabTiaStatusDirty();
        }), DispatcherPriority.Background);
    }

    private void RtdGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView("RTD");
            MarkActiveTabTiaStatusDirty();
        }), DispatcherPriority.Background);
    }

    private void CounterGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView("COUNTER");
            MarkActiveTabTiaStatusDirty();
        }), DispatcherPriority.Background);
    }

    private void AoGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
        }), DispatcherPriority.Background);
    }

    private void ModuleNamesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ModuleNameDisplayConverter.SetModuleNames(_project.ModuleCatalog);
            RecalculateAndRefreshSignalGrids();
            UpdateDbDiFilterItems();
            UpdateDbDoFilterItems();
            _dbDiView?.Refresh();
            _dbDoView?.Refresh();
            DbDiGrid.Items.Refresh();
            DbDoGrid.Items.Refresh();
        }), DispatcherPriority.Background);
    }

    private void DbDiFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbDiFilters || _dbDiView is null)
        {
            return;
        }

        UpdateDbDiFilterItems();
        _dbDiView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void ClearDbDiFilters_Click(object sender, RoutedEventArgs e)
    {
        DbDiTextFilter.Text = "";
        _loadingDbDiFilters = true;
        foreach (var spec in GetDbDiFilterSpecs())
        {
            spec.Filter.SelectedIndex = 0;
        }

        _loadingDbDiFilters = false;
        UpdateDbDiFilterItems();
        SortDbDiById();
        _dbDiView?.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void DbDiColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDbDiColumnVisibility();
    }

    private void DbDoFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbDoFilters || _dbDoView is null)
        {
            return;
        }

        UpdateDbDoFilterItems();
        _dbDoView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void ClearDbDoFilters_Click(object sender, RoutedEventArgs e)
    {
        DbDoTextFilter.Text = "";
        _loadingDbDoFilters = true;
        foreach (var spec in GetDbDoFilterSpecs())
        {
            spec.Filter.SelectedIndex = 0;
        }

        _loadingDbDoFilters = false;
        UpdateDbDoFilterItems();
        SortDbDoById();
        _dbDoView?.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void DbDoColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDbDoColumnVisibility();
    }

    private void DbMeasFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbMeasFilters || _dbMeasView is null)
        {
            return;
        }

        UpdateDbMeasFilterItems();
        _dbMeasView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void DbAlmFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbAlmFilters || _dbAlmView is null)
        {
            return;
        }

        UpdateDbAlmFilterItems();
        _dbAlmView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void DbParFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbParFilters || _dbParView is null)
        {
            return;
        }

        UpdateDbParFilterItems();
        _dbParView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void ClearDbMeasFilters_Click(object sender, RoutedEventArgs e)
    {
        DbMeasTextFilter.Text = "";
        ClearDbFilters(GetDbMeasFilterSpecs(), ref _loadingDbMeasFilters, _dbMeasView, SortDbMeasById, UpdateDbMeasFilterItems);
        RefreshActiveTabFilterUi();
    }

    private void ClearDbAoFilters_Click(object sender, RoutedEventArgs e)
    {
        DbAoTextFilter.Text = "";
        _loadingDbAoFilters = true;
        foreach (var spec in GetDbAoFilterSpecs())
        {
            spec.Filter.SelectedIndex = 0;
        }

        _loadingDbAoFilters = false;
        UpdateDbAoFilterItems();
        _dbAoRowsView?.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void ClearDbAlmFilters_Click(object sender, RoutedEventArgs e)
    {
        DbAlmTextFilter.Text = "";
        ClearDbFilters(GetDbAlmFilterSpecs(), ref _loadingDbAlmFilters, _dbAlmView, SortDbAlmById, UpdateDbAlmFilterItems);
        RefreshActiveTabFilterUi();
    }

    private void ClearDbParFilters_Click(object sender, RoutedEventArgs e)
    {
        DbParTextFilter.Text = "";
        ClearDbFilters(GetDbParFilterSpecs(), ref _loadingDbParFilters, _dbParView, SortDbParById, UpdateDbParFilterItems);
        RefreshActiveTabFilterUi();
    }

    private static void ClearDbFilters(List<DbFilterSpec> specs, ref bool loadingFlag, ICollectionView? view, Action sort, Action update)
    {
        loadingFlag = true;
        foreach (var spec in specs)
        {
            spec.Filter.SelectedIndex = 0;
        }

        loadingFlag = false;
        update();
        sort();
        view?.Refresh();
    }

    private void DbTextFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _dbDiView?.Refresh();
        _dbDoView?.Refresh();
        _dbMeasView?.Refresh();
        _dbAoRowsView?.Refresh();
        _dbAlmView?.Refresh();
        _dbParView?.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void SequenceFilter_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSequenceControlStatusFilters)
        {
            UpdateSequenceControlStatusFilterItems();
            _sequenceControlStatusView?.Refresh();
        }

        if (!_loadingSequenceCommandFilters)
        {
            UpdateSequenceCommandFilterItems();
            _sequenceCommandView?.Refresh();
        }
    }

    private void ScadaDatabaseTreeItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void ClearSequenceControlStatusFilters_Click(object sender, RoutedEventArgs e)
    {
        SequenceControlStatusTextFilter.Text = "";
        ClearDbFilters(GetSequenceControlStatusFilterSpecs(), ref _loadingSequenceControlStatusFilters, _sequenceControlStatusView, SortSequenceControlStatusById, UpdateSequenceControlStatusFilterItems);
    }

    private void ClearSequenceCommandFilters_Click(object sender, RoutedEventArgs e)
    {
        SequenceCommandTextFilter.Text = "";
        ClearDbFilters(GetSequenceCommandFilterSpecs(), ref _loadingSequenceCommandFilters, _sequenceCommandView, SortSequenceCommandById, UpdateSequenceCommandFilterItems);
    }

    private void DbMeasColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDbMeasColumnVisibility();
    }

    private void DbAoColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDbAoColumnVisibility();
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        _project.Devices.Add(new DeviceConfig());
        DevicesGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage("Device row added.");
    }

    private void DeleteSelectedDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DeviceConfig device)
        {
            return;
        }

        _project.Devices.Remove(device);
        DevicesGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage("Device row deleted.");
    }

    private void DbAoFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDbAoFilters || _dbAoRowsView is null)
        {
            return;
        }

        UpdateDbAoFilterItems();
        _dbAoRowsView.Refresh();
        RefreshActiveTabFilterUi();
    }

    private void DbAlmColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDbAlmSlicer)
        {
            return;
        }

        NormalizeDbAlmRepairSlicerSelection(e);
        ApplyDbAlmColumnVisibility();
        UpdateDbAlmFilterItems();
        _dbAlmView?.Refresh();
    }

    private void NormalizeDbAlmRepairSlicerSelection(SelectionChangedEventArgs e)
    {
        var added = e.AddedItems
            .OfType<ListBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added.Count == 0)
        {
            return;
        }

        _updatingDbAlmSlicer = true;
        try
        {
            if (added.Contains("Repair"))
            {
                SetDbAlmSlicerItemSelected("All", false);
                SetDbAlmSlicerItemSelected("Nothing", false);
            }
            else if (added.Contains("All") || added.Contains("Nothing"))
            {
                SetDbAlmSlicerItemSelected("Repair", false);
            }
        }
        finally
        {
            _updatingDbAlmSlicer = false;
        }
    }

    private void SetDbAlmSlicerItemSelected(string content, bool isSelected)
    {
        var item = DbAlmColumnSlicer.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(listItem => string.Equals(listItem.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.IsSelected = isSelected;
        }
    }

    private void DbParColumnSlicer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDbParColumnVisibility();
    }

    private void DbAlmColorModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject || DbAlmColorModeCombo.SelectedItem is not DbValidationOption selected)
        {
            return;
        }

        _project.DbCatalog.ConditionalColors.DbAlarmColorRequiresUsed = selected.Value.Equals("1", StringComparison.OrdinalIgnoreCase)
                                                                        || selected.Text.Contains("only when USED", StringComparison.OrdinalIgnoreCase)
                                                                        || selected.Text.Contains("uz uvjet", StringComparison.OrdinalIgnoreCase);
        ApplyConfiguredConditionalColors();
        DbAlmGrid.Items.Refresh();
        _dbAlmView?.Refresh();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }

        // The user moved away from the grid we copied from — the marching-ants
        // outline would otherwise linger as phantom selection when they return,
        // and confuses Ctrl+V semantics. Clear it on every tab switch.
        if (_clipboardSourceGrid is not null)
        {
            ClearClipboardMarchingAnts();
        }

        RefreshActiveHardwareTabIndexing();
        UpdateToolbarVisibility();
        AdjustMainTabHeaderWidths();
        RefreshActiveTabFilterUi();
    }

    private void RefreshActiveHardwareTabIndexing()
    {
        var activeHeader = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        if (activeHeader is not ("DI" or "DO" or "AI" or "RTD" or "COUNTER"))
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            RecalculateAndRefreshSignalGrids();
            RefreshHardwareView(activeHeader);
        }), DispatcherPriority.Background);
    }

    private void RefreshHardwareView(string section)
    {
        switch (section)
        {
            case "DI":
                _diView?.Refresh();
                DiGrid.Items.Refresh();
                break;
            case "DO":
                _doView?.Refresh();
                DoGrid.Items.Refresh();
                break;
            case "AI":
                _aiView?.Refresh();
                AiGrid.Items.Refresh();
                break;
            case "RTD":
                _rtdView?.Refresh();
                RtdGrid.Items.Refresh();
                break;
            case "COUNTER":
                _counterView?.Refresh();
                CounterGrid.Items.Refresh();
                break;
        }
    }

    private void MainTabs_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AdjustMainTabHeaderWidths();
    }

    private void AdjustMainTabHeaderWidths()
    {
        if (MainTabs.ActualWidth <= 0 || MainTabs.Items.Count == 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, MainTabs.ActualWidth - 8);
        var tabWidth = Math.Max(72, Math.Floor(availableWidth / MainTabs.Items.Count));
        foreach (var tabItem in MainTabs.Items.OfType<TabItem>())
        {
            tabItem.Width = tabWidth;
            ApplyMainTabColor(tabItem);
        }

        // First-layout race fix: on the very first SizeChanged the internal
        // TabPanel has already committed to a two-row wrap (because the
        // initial measure ran with no explicit Widths). Setting Width on
        // children does NOT always force TabPanel to re-run its row split
        // in the same layout pass, so tabs stay wrapped until the user
        // clicks something. Defer an InvalidateMeasure to the Loaded
        // priority so it re-arranges after layout settles.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainTabs is not null)
            {
                MainTabs.InvalidateMeasure();
            }
        }), DispatcherPriority.Loaded);
    }

    private void ArrangeMainTabs()
    {
        var headers = IsScadaWorkspace()
            ? ["SCADA"]
            : new[]
            {
                "MODULE NAMES",
                "DI",
                "DO",
                "AI",
                "RTD",
                "COUNTER",
                "DEVICES",
                "DB DI",
                "DB DO",
                "DB MEAS",
                "DB AO",
                "DB ALM",
                "DB PAR",
                "Sequence",
                "Text lists"
            };

        var sourceTabs = _allMainTabs.Count > 0 ? _allMainTabs : MainTabs.Items.OfType<TabItem>().ToList();
        var tabs = sourceTabs
            .GroupBy(GetTabKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Preserve the active tab across Items.Clear() — otherwise BindProject()
        // (called after undo/redo, file open, etc.) yanks the user back to the
        // first tab ("MODULE NAMES") every time.
        var activeKey = MainTabs.SelectedItem is TabItem active ? GetTabKey(active) : null;

        MainTabs.Items.Clear();
        foreach (var header in headers)
        {
            if (tabs.TryGetValue(header, out var tab))
            {
                MainTabs.Items.Add(tab);
                ApplyMainTabColor(tab);
            }
        }

        if (MainTabs.Items.Count == 0)
        {
            return;
        }

        if (activeKey is not null && tabs.TryGetValue(activeKey, out var restored) && MainTabs.Items.Contains(restored))
        {
            MainTabs.SelectedItem = restored;
        }
        else if (MainTabs.SelectedItem is null)
        {
            MainTabs.SelectedIndex = 0;
        }
    }

    private void ApplyMainTabColor(TabItem tabItem)
    {
        var isDbTab = GetTabKey(tabItem).StartsWith("DB ", StringComparison.OrdinalIgnoreCase);
        var isSelected = ReferenceEquals(tabItem, MainTabs.SelectedItem);
        tabItem.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDbTab
            ? (isSelected ? "#9EC5FE" : "#DCEBFF")
            : (isSelected ? "#B7E4C7" : "#EAF7EE"))!);
        tabItem.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDbTab ? "#5B8DEF" : "#64A878")!);
        tabItem.Foreground = Brushes.Black;
    }

    private void InitializeColumnFilterSync()
    {
        AttachColumnFilterSync(DbDiGrid, GetDbDiColumnLayouts);
        AttachColumnFilterSync(DbDoGrid, GetDbDoColumnLayouts);
        AttachColumnFilterSync(DbMeasGrid, GetDbMeasColumnLayouts);
        AttachColumnFilterSync(DbAoGrid, GetDbAoColumnLayouts);
        AttachColumnFilterSync(DbAlmGrid, GetDbAlmColumnLayouts);
        AttachColumnFilterSync(DbParGrid, GetDbParColumnLayouts);
        AttachColumnFilterSync(SequenceControlStatusGrid, GetSequenceControlStatusColumnLayouts);
        AttachColumnFilterSync(SequenceCommandGrid, GetSequenceCommandColumnLayouts);
    }

    private static void AttachColumnFilterSync(DataGrid grid, Func<List<ColumnFilterLayout>> getLayouts)
    {
        grid.LayoutUpdated += (_, _) => SyncFilterWidthsToColumns(getLayouts());
    }

    private void UpdateToolbarVisibility()
    {
        var activeHeader = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        GetFromConfigurationButton.Visibility = activeHeader is "DB DI" or "DB DO" or "DB MEAS" or "DB AO"
            ? Visibility.Visible
            : Visibility.Collapsed;
        var activeDb = activeHeader is "DB DI" or "DB DO" or "DB MEAS" or "DB AO" or "DB ALM" or "DB PAR" or "Sequence";
        TabGroupNamesButton.Visibility = activeHeader is "DB DI" or "DB DO" or "DB MEAS" or "DB ALM" or "DB PAR"
            ? Visibility.Visible
            : Visibility.Collapsed;
        TabExportTextButton.Visibility = activeDb ? Visibility.Visible : Visibility.Collapsed;
        TabImportTextButton.Visibility = activeDb ? Visibility.Visible : Visibility.Collapsed;
        TabTiaPullButton.Visibility = activeDb ? Visibility.Visible : Visibility.Collapsed;
        TabTiaPushButton.Visibility = activeDb ? Visibility.Visible : Visibility.Collapsed;
        if (activeDb)
        {
            var label = activeHeader == "Sequence" ? "DB SEQUENCE" : activeHeader;
            TabTiaPullButton.Content = "Pull " + label;
            TabTiaPushButton.Content = "Push " + label;
            var busy = _dbDiTiaOperationRunning;
            TabTiaPullButton.Opacity = busy ? 0.72 : 1.0;
            TabTiaPushButton.Opacity = busy ? 0.72 : 1.0;
            TabTiaPullButton.IsEnabled = !busy;
            TabTiaPushButton.IsEnabled = !busy;
        }
    }

    private void UpdateWorkspaceVisualState()
    {
        var scada = IsScadaWorkspace();
        PlcDatabaseTree.Visibility = scada ? Visibility.Collapsed : Visibility.Visible;
        ScadaDatabaseTree.Visibility = scada ? Visibility.Visible : Visibility.Collapsed;
        AddPlcDatabaseButton.Visibility = scada ? Visibility.Collapsed : Visibility.Visible;
        ScadaPaneActions.Visibility = scada ? Visibility.Visible : Visibility.Collapsed;
        DatabasePaneTitle.Text = scada ? "SCADA databases" : "PLC databases";
        PlcWorkspaceButton.Background = scada ? Brushes.WhiteSmoke : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B7E4C7")!);
        ScadaWorkspaceButton.Background = scada ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B7E4C7")!) : Brushes.WhiteSmoke;
        ScadaPaneSourceText.Text = string.IsNullOrWhiteSpace(_project.Scada.SourceTextPath)
            ? "No SCADA text imported."
            : _project.Scada.SourceTextPath;
    }

    private static string GetTabKey(TabItem tabItem)
    {
        return tabItem.Tag?.ToString() ?? tabItem.Header?.ToString() ?? "";
    }

    private bool IsScadaWorkspace()
    {
        return _project.ActiveWorkspace.Equals("SCADA", StringComparison.OrdinalIgnoreCase);
    }

    private void PlcWorkspace_Click(object sender, RoutedEventArgs e)
    {
        SwitchWorkspace("PLC");
    }

    private void ScadaWorkspace_Click(object sender, RoutedEventArgs e)
    {
        SwitchWorkspace("SCADA");
    }

    private void SwitchWorkspace(string workspace)
    {
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();
        _project.ActiveWorkspace = workspace.Equals("SCADA", StringComparison.OrdinalIgnoreCase) ? "SCADA" : "PLC";
        ArrangeMainTabs();
        UpdateWorkspaceVisualState();
        UpdateToolbarVisibility();
        ShowMessage(IsScadaWorkspace() ? "SCADA workspace active." : "PLC workspace active.");
    }

    private void ProjectPathsSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProjectPathsDialog(_project.Paths)
        {
            Owner = EditorOwner
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.Paths.PlcToConfigSourceFolder = dialog.PlcSourceFolder;
        _project.Paths.RSourceFolder = dialog.RSourceFolder;
        _project.Paths.OutputFolder = dialog.OutputFolder;
        _project.Paths.TiaPortalExportFolder = dialog.TiaPortalExportFolder;
        _project.Paths.UnifiedJsConnectorFolder = dialog.UnifiedJsConnectorFolder;
        _project.Paths.UnifiedJsConnectorServerPath = dialog.UnifiedJsConnectorServerPath;
        ShowMessage("Project paths updated.");
    }

    private void ImportUnifiedJsConnector_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose WinCC Unified JS Connector folder",
            InitialDirectory = Directory.Exists(_project.Paths.UnifiedJsConnectorFolder)
                ? _project.Paths.UnifiedJsConnectorFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = _unifiedJsConnectorImporter.Import(dialog.FolderName);
            _project.Paths.UnifiedJsConnectorFolder = result.InstallFolder;
            _project.Paths.UnifiedJsConnectorServerPath = result.ServerExecutablePath;
            ShowMessage($"Imported WinCC Unified JS Connector {result.PackageVersion}. REST server: {result.ServerExecutablePath}");
            MessageBox.Show(this,
                "WinCC Unified JS Connector was imported into Plant Configurator.\n\n" +
                $"Connector folder:\n{result.InstallFolder}\n\n" +
                $"REST server:\n{result.ServerExecutablePath}\n\n" +
                "Next step is to call its TIA endpoints from the app for Unified script module import/export.",
                "WinCC Unified JS Connector",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void TestUnifiedJsConnector_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowMessage("Starting WinCC Unified JS Connector...");
            var processes = await _unifiedJsConnectorRuntime.StartAndFetchProcessesAsync(_project.Paths.UnifiedJsConnectorServerPath);
            if (processes.Count == 0)
            {
                MessageBox.Show(this,
                    "Connector is running, but no open TIA Portal process was returned.\n\nOpen the TIA project and try again.",
                    "WinCC Unified script connection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                ShowMessage("WinCC Unified JS Connector is running. No TIA processes found.");
                return;
            }

            var message = string.Join("\n", processes.Select(process =>
                $"{process.ProcessId} - {process.ProjectName} {process.ProjectPath}".Trim()));
            MessageBox.Show(this,
                "Connector is running and TIA processes were found:\n\n" + message,
                "WinCC Unified script connection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ShowMessage($"WinCC Unified JS Connector connected. TIA processes: {processes.Count}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ImportScadaText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import SCADA configuration text",
            Filter = "JavaScript/text files (*.js;*.txt)|*.js;*.txt|All files (*.*)|*.*",
            InitialDirectory = File.Exists(_project.Scada.SourceTextPath)
                ? Path.GetDirectoryName(_project.Scada.SourceTextPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SaveActivePlcDatabase();
            SaveActiveScadaDatabase();
            var imported = _scadaConfigFileService.Import(dialog.FileName);
            ApplyImportedScadaDatabase(imported);
            ShowMessage($"Imported SCADA configuration: {_project.Scada.Variables.Count} variables, {_project.Scada.TagWrites.Count} tag writes.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    // Replaces the active SCADA database with a freshly parsed one (from a file or a live
    // Unified pull), keeping its id/name, and rebinds the UI.
    private void ApplyImportedScadaDatabase(ScadaDatabase imported)
    {
        var active = GetActiveScadaDatabase();
        if (active is not null)
        {
            imported.Id = active.Id;
            imported.Name = active.Name;
            var index = _project.ScadaDatabases.FindIndex(database => database.Id == active.Id);
            if (index >= 0)
            {
                _project.ScadaDatabases[index] = imported;
            }
        }
        else
        {
            imported.Name = "SCADA 1";
            _project.ScadaDatabases.Add(imported);
        }

        _project.Scada = imported;
        _project.ActiveScadaDatabaseId = imported.Id;
        _project.ActiveWorkspace = "SCADA";
        BindProject();
    }

    private async void PullScadaFromUnified_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            SaveActivePlcDatabase();
            SaveActiveScadaDatabase();
            var service = new ScadaUnifiedSyncService();
            var result = await service.PullAsync(_project.Paths.UnifiedJsConnectorServerPath, _project.Paths.UnifiedScadaDeviceName);
            ApplyImportedScadaDatabase(result.Database);
            ShowMessage($"{result.Message} ({_project.Scada.Variables.Count} variables, {_project.Scada.TagWrites.Count} tag writes).");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private async void PushScadaToUnified_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Scada.Variables.Count == 0)
        {
            MessageBox.Show(this,
                "SCADA configuration is empty. Pull from Unified or import a text file first.",
                "SCADA push",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"This overwrites the '{ScadaUnifiedSyncService.ModuleName}.{ScadaInternalTagModule.DefaultFunctionName}' function in the live WinCC Unified project.\n\n"
            + "The rest of the module (global definition area, other functions) is preserved, and a backup of the exported module is kept. Continue?",
            "Push SCADA config to WinCC Unified",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            NormalizeScadaDatabase();
            var service = new ScadaUnifiedSyncService();
            var message = await service.PushAsync(_project.Scada, _project.Paths.UnifiedJsConnectorServerPath, _project.Paths.UnifiedScadaDeviceName);
            BindScadaViews();
            ShowMessage(message);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private void ExportScadaText_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Scada.Variables.Count == 0)
        {
            MessageBox.Show(this,
                "SCADA database is empty. Import a SCADA configuration text file first.",
                "SCADA export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export SCADA configuration text",
            Filter = "JavaScript file (*.js)|*.js|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_project.Scada.LastGeneratedPath)
                ? "SCADA_configuration.js"
                : Path.GetFileName(_project.Scada.LastGeneratedPath),
            InitialDirectory = !string.IsNullOrWhiteSpace(_project.Scada.LastGeneratedPath) && Directory.Exists(Path.GetDirectoryName(_project.Scada.LastGeneratedPath))
                ? Path.GetDirectoryName(_project.Scada.LastGeneratedPath)
                : File.Exists(_project.Scada.SourceTextPath)
                    ? Path.GetDirectoryName(_project.Scada.SourceTextPath)
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            NormalizeScadaDatabase();
            _scadaConfigFileService.Export(_project.Scada, dialog.FileName);
            BindScadaViews();
            ShowMessage($"Generated SCADA configuration: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    // Catalog of WinCC Unified screen names, pulled from the attached TIA project, used as
    // the dropdown for the SCREENS matrix VALUE cells. Always also includes the cells'
    // current values so existing entries stay selectable.
    private readonly List<string> _tiaScreenNames = new();

    private void RebuildScadaScreenCatalog()
    {
        var catalog = new List<string>(_tiaScreenNames);
        if (_scadaScreensView is not null)
        {
            foreach (var item in _scadaScreensView)
            {
                if (item is ScadaConfigVariable variable && !string.IsNullOrWhiteSpace(variable.Value))
                {
                    catalog.Add(variable.Value);
                }
            }
        }

        var sorted = new List<string> { "" };
        sorted.AddRange(catalog
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        if (ScadaScreenValueColumn is not null)
        {
            ScadaScreenValueColumn.ItemsSource = sorted;
        }

        if (ScadaScreenCatalogInfo is not null)
        {
            ScadaScreenCatalogInfo.Text = _tiaScreenNames.Count > 0
                ? $"{_tiaScreenNames.Count} screen(s) loaded from TIA."
                : "No TIA screens loaded — attach a V21 project in 'TIA Sync', then click 'Refresh screens from TIA'.";
        }
    }

    private async void RefreshScadaScreensFromTia_Click(object sender, RoutedEventArgs e)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this,
                "Attach to the TIA Portal project (V21) in the 'TIA Sync' window first, then refresh screens.",
                "Refresh screens",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var result = await _tiaSessionService.GetUnifiedScreensAsync();
            _tiaScreenNames.Clear();
            _tiaScreenNames.AddRange(result.Screens);
            RebuildScadaScreenCatalog();
            ShowMessage(result.Warnings.Count > 0
                ? string.Join(Environment.NewLine, result.Warnings)
                : $"Loaded {result.Screens.Count} WinCC Unified screen(s) from TIA.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private void ConfigureNavigation_Click(object sender, RoutedEventArgs e)
    {
        NormalizeScadaDatabase();

        // Screen catalog for the editor dropdowns: TIA-fetched screens plus whatever the
        // current config already references (so existing entries stay selectable offline).
        var model = ScadaNavigationModel.FromDatabase(_project.Scada);
        var catalog = new List<string>(_tiaScreenNames);
        foreach (var button in model.Buttons)
        {
            if (!string.IsNullOrWhiteSpace(button.SideContentScreen))
            {
                catalog.Add(button.SideContentScreen);
            }

            foreach (var card in button.Cards.Where(card => !string.IsNullOrWhiteSpace(card.Screen)))
            {
                catalog.Add(card.Screen);
            }
        }

        var screens = catalog
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dialog = new NavigationEditorWindow(_project.Scada, screens) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Applied)
        {
            NormalizeScadaDatabase();
            BindScadaViews();
            ShowMessage("Navigation applied to the SCADA database. Use 'Push to Unified' or 'Export text...' to write it back.");
        }
    }

    private bool _suppressScadaDeviceSelection;

    // Keeps the device combo showing the persisted target even before a live refresh.
    private void InitScadaUnifiedDeviceCombo()
    {
        if (ScadaUnifiedDeviceCombo is null || ScadaUnifiedDeviceCombo.Items.Count > 0)
        {
            // Already showing the persisted target or a fetched device list — don't clobber it.
            return;
        }

        var persisted = _project.Paths.UnifiedScadaDeviceName;
        _suppressScadaDeviceSelection = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(persisted))
            {
                ScadaUnifiedDeviceCombo.ItemsSource = new List<string> { persisted };
                ScadaUnifiedDeviceCombo.SelectedItem = persisted;
            }
        }
        finally
        {
            _suppressScadaDeviceSelection = false;
        }
    }

    private async void RefreshScadaDevices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var names = await new ScadaUnifiedSyncService().ListScadaDeviceNamesAsync(_project.Paths.UnifiedJsConnectorServerPath);
            var persisted = _project.Paths.UnifiedScadaDeviceName;
            _suppressScadaDeviceSelection = true;
            try
            {
                ScadaUnifiedDeviceCombo.ItemsSource = names;
                ScadaUnifiedDeviceCombo.SelectedItem =
                    names.FirstOrDefault(name => name.Equals(persisted, StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault();
            }
            finally
            {
                _suppressScadaDeviceSelection = false;
            }

            _project.Paths.UnifiedScadaDeviceName = ScadaUnifiedDeviceCombo.SelectedItem as string ?? "";
            ShowMessage(names.Count == 0
                ? "No WinCC Unified device exposing AUX_FUNCTIONS was found."
                : $"Found {names.Count} SCADA device(s): {string.Join(", ", names)}.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private void ScadaUnifiedDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressScadaDeviceSelection)
        {
            return;
        }

        _project.Paths.UnifiedScadaDeviceName = ScadaUnifiedDeviceCombo.SelectedItem as string ?? "";
    }

    private void ScadaGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NormalizeScadaDatabase();
            if (ReferenceEquals(sender, ScadaTagsGrid))
            {
                BindScadaViews();
            }
            else
            {
                RefreshScadaTagValues();
            }
        }), DispatcherPriority.Background);
    }

    private void NormalizeScadaDatabase()
    {
        _project.Scada.EnsureDefaults();
        ScadaConfigFileService.ApplyTagsToVariables(_project.Scada);
    }

    private void RefreshScadaTagValues()
    {
        ScadaConfigFileService.ApplyTagsToVariables(_project.Scada);
        _scadaTagWritesView?.Refresh();
        ScadaTagsGrid.Items.Refresh();
    }

    private void TableDesignSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TableDesignDialog(_project.TableDesign, _project.DbCatalog.ConditionalColors)
        {
            Owner = EditorOwner
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.TableDesign = dialog.TableDesign;
        _project.DbCatalog.ConditionalColors = dialog.ConditionalColors.Clone();
        ApplyConfiguredConditionalColors();
        ApplyTableDesign();
        RefreshAllDataGrids();
        ShowMessage("Table design updated.");
    }

    private void DbDiSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DbDiSystemSettingsDialog(_project.DbCatalog.DiSystemNameRows, "DB DI", _project.DbCatalog.DiStructTypePrefix, _project.DbCatalog.DiSimulatorTypePrefix, showSimulatorPrefix: true)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.DbCatalog.DiSystemNameRows = dialog.Rows.Select(row => row.Clone()).ToList();
        _project.DbCatalog.DiStructTypePrefix = dialog.StructTypePrefix;
        _project.DbCatalog.DiSimulatorTypePrefix = dialog.SimulatorTypePrefix;
        ApplyDbDiSystemNamesToRows();
        UpdateDbDiFilterItems();
        _dbDiView?.Refresh();
        DiGrid.Items.Refresh();
        ShowMessage("DB DI system settings updated.");
    }

    private void DbDoSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DbDiSystemSettingsDialog(_project.DbCatalog.DoSystemNameRows, "DB DO", _project.DbCatalog.DoStructTypePrefix, _project.DbCatalog.DoSimulatorTypePrefix, showSimulatorPrefix: true)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.DbCatalog.DoSystemNameRows = dialog.Rows.Select(row => row.Clone()).ToList();
        _project.DbCatalog.DoStructTypePrefix = dialog.StructTypePrefix;
        _project.DbCatalog.DoSimulatorTypePrefix = dialog.SimulatorTypePrefix;
        ApplyDbDoSystemNamesToRows();
        UpdateDbDoFilterItems();
        _dbDoView?.Refresh();
        DoGrid.Items.Refresh();
        ShowMessage("DB DO system settings updated.");
    }

    private void DbMeasSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneratedStructSettings("DB MEAS", _project.DbCatalog.MeasRows, _project.DbCatalog.MeasSystemNameRows, _project.DbCatalog.MeasStructTypePrefix, _project.DbCatalog.MeasSimulatorTypePrefix, showSimulatorPrefix: true, rows => _project.DbCatalog.MeasSystemNameRows = rows, (dtPrefix, simPrefix) =>
        {
            _project.DbCatalog.MeasStructTypePrefix = dtPrefix;
            _project.DbCatalog.MeasSimulatorTypePrefix = simPrefix;
        });
    }

    private void DbAlmSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneratedStructSettings("DB ALM", _project.DbCatalog.AlarmRows, _project.DbCatalog.AlarmSystemNameRows, _project.DbCatalog.AlarmStructTypePrefix, "", showSimulatorPrefix: false, rows => _project.DbCatalog.AlarmSystemNameRows = rows, (dtPrefix, _) => _project.DbCatalog.AlarmStructTypePrefix = dtPrefix);
    }

    private void DbAoSettings_Click(object sender, RoutedEventArgs e)
    {
        var generatedRows = MergeAoStructSettingsRows(_project.DbCatalog.AoRows, _project.DbCatalog.AoSystemNameRows, _project.DbCatalog.AoStructTypePrefix);
        var dialog = new DbDiSystemSettingsDialog(generatedRows, "DB AO", _project.DbCatalog.AoStructTypePrefix, "", showSimulatorPrefix: false)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.DbCatalog.AoSystemNameRows = dialog.Rows.Select(row => row.Clone()).ToList();
        _project.DbCatalog.AoStructTypePrefix = dialog.StructTypePrefix;
        DbAoGrid.Items.Refresh();
        ShowMessage("DB AO struct settings updated.");
    }

    private void DbParSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneratedStructSettings("DB PAR", _project.DbCatalog.ParameterRows, _project.DbCatalog.ParameterSystemNameRows, _project.DbCatalog.ParameterStructTypePrefix, "", showSimulatorPrefix: false, rows => _project.DbCatalog.ParameterSystemNameRows = rows, (dtPrefix, _) => _project.DbCatalog.ParameterStructTypePrefix = dtPrefix);
    }

    private void ShowGeneratedStructSettings(string dbName, IEnumerable<DbStringRow> rows, IReadOnlyList<DbDiSystemNameRow> savedRows, string structPrefix, string simulatorPrefix, bool showSimulatorPrefix, Action<List<DbDiSystemNameRow>> saveRows, Action<string, string> savePrefixes)
    {
        var generatedRows = MergeStructSettingsRows(rows, savedRows, structPrefix, simulatorPrefix, showSimulatorPrefix);

        var dialog = new DbDiSystemSettingsDialog(generatedRows, dbName, structPrefix, simulatorPrefix, showSimulatorPrefix)
        {
            Owner = EditorOwner
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        saveRows(dialog.Rows.Select(row => row.Clone()).ToList());
        savePrefixes(dialog.StructTypePrefix, dialog.SimulatorTypePrefix);
        ApplyGeneratedStructSettingsToRows(rows, dialog.Rows);
        RefreshAllDbViews();
    }

    private void DbDiGroupNames_Click(object sender, RoutedEventArgs e)
    {
        ShowGroupNames("DB DI", _project.DbCatalog.DiRows, _project.DbCatalog.DiSystemNameRows, _project.DbCatalog.DiStructTypePrefix, _project.DbCatalog.DiSimulatorTypePrefix, showSimulatorPrefix: true, rows => _project.DbCatalog.DiSystemNameRows = rows);
    }

    private void DbDoGroupNames_Click(object sender, RoutedEventArgs e)
    {
        ShowGroupNames("DB DO", _project.DbCatalog.DoRows, _project.DbCatalog.DoSystemNameRows, _project.DbCatalog.DoStructTypePrefix, _project.DbCatalog.DoSimulatorTypePrefix, showSimulatorPrefix: true, rows => _project.DbCatalog.DoSystemNameRows = rows);
    }

    private void DbMeasGroupNames_Click(object sender, RoutedEventArgs e)
    {
        ShowGroupNames("DB MEAS", _project.DbCatalog.MeasRows, _project.DbCatalog.MeasSystemNameRows, _project.DbCatalog.MeasStructTypePrefix, _project.DbCatalog.MeasSimulatorTypePrefix, showSimulatorPrefix: true, rows => _project.DbCatalog.MeasSystemNameRows = rows);
    }

    private void DbAlmGroupNames_Click(object sender, RoutedEventArgs e)
    {
        ShowGroupNames("DB ALM", _project.DbCatalog.AlarmRows, _project.DbCatalog.AlarmSystemNameRows, _project.DbCatalog.AlarmStructTypePrefix, "", showSimulatorPrefix: false, rows => _project.DbCatalog.AlarmSystemNameRows = rows);
    }

    private void DbParGroupNames_Click(object sender, RoutedEventArgs e)
    {
        ShowGroupNames("DB PAR", _project.DbCatalog.ParameterRows, _project.DbCatalog.ParameterSystemNameRows, _project.DbCatalog.ParameterStructTypePrefix, "", showSimulatorPrefix: false, rows => _project.DbCatalog.ParameterSystemNameRows = rows);
    }

    private void ShowGroupNames(string dbName, IEnumerable<DbStringRow> sourceRows, IReadOnlyList<DbDiSystemNameRow> savedRows, string structPrefix, string simulatorPrefix, bool showSimulatorPrefix, Action<List<DbDiSystemNameRow>> saveRows)
    {
        var generatedRows = MergeStructSettingsRows(sourceRows, savedRows, structPrefix, simulatorPrefix, showSimulatorPrefix);
        var dialog = new DbGroupNamesDialog(dbName, generatedRows)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var updatedRows = dialog.Rows.Select(row => row.Clone()).ToList();
        saveRows(updatedRows);
        RefreshAllDbViews();
        ShowMessage($"{dbName} group names updated.");
    }

    private static List<DbDiSystemNameRow> MergeStructSettingsRows(IEnumerable<DbStringRow> rows, IReadOnlyList<DbDiSystemNameRow> savedRows, string structPrefix, string simulatorPrefix, bool showSimulatorPrefix)
    {
        var savedByShortName = savedRows
            .Where(row => !string.IsNullOrWhiteSpace(row.SystemPlcShortName))
            .ToDictionary(row => row.SystemPlcShortName, StringComparer.OrdinalIgnoreCase);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.PlcStructure))
            .GroupBy(row => row.PlcStructure, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                var memberName = group.Key;
                var row = savedByShortName.TryGetValue(memberName, out var saved) ? saved.Clone() : new DbDiSystemNameRow();
                row.SystemNumber = row.SystemNumber <= 0 ? index + 1 : row.SystemNumber;
                row.SystemPlcShortName = memberName;
                row.PlcVariableName = memberName;
                row.SystemPlcLongName = FirstNonEmpty(row.SystemPlcLongName, JoinPrefixAndName(structPrefix, memberName));
                row.HmiSystemName = FirstNonEmpty(row.HmiSystemName, group.First().SystemNameEnglish, memberName);
                row.HmiSystemNameOther = FirstNonEmpty(row.HmiSystemNameOther, group.First().SystemNameOther, row.HmiSystemName);
                row.NameLength = row.HmiSystemName.Length;
                row.SimulatorDataType = showSimulatorPrefix ? FirstNonEmpty(row.SimulatorDataType, JoinPrefixAndName(simulatorPrefix, memberName)) : "";
                return row;
            })
            .ToList();
    }

    private static List<DbDiSystemNameRow> MergeAoStructSettingsRows(IEnumerable<DbAoRow> rows, IReadOnlyList<DbDiSystemNameRow> savedRows, string structPrefix)
    {
        if (savedRows.Count > 0)
        {
            return savedRows
                .Select((saved, index) =>
                {
                    var row = saved.Clone();
                    row.SystemNumber = row.SystemNumber <= 0 ? index + 1 : row.SystemNumber;
                    row.SystemPlcShortName = FirstNonEmpty(row.SystemPlcShortName, row.PlcVariableName, index == 0 ? "MECH" : $"AO_{index + 1}");
                    row.PlcVariableName = FirstNonEmpty(row.PlcVariableName, row.SystemPlcShortName);
                    row.SystemPlcLongName = FirstNonEmpty(row.SystemPlcLongName, JoinPrefixAndName(structPrefix, row.SystemPlcShortName));
                    row.HmiSystemName = FirstNonEmpty(row.HmiSystemName, row.SystemPlcShortName);
                    row.HmiSystemNameOther = FirstNonEmpty(row.HmiSystemNameOther, row.HmiSystemName);
                    row.NameLength = row.HmiSystemName.Length;
                    row.SimulatorDataType = "";
                    return row;
                })
                .ToList();
        }

        var structures = rows
            .Select(row => row.AoSystemName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (structures.Count == 0)
        {
            var rowCount = rows.Count();
            structures = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(rowCount / 32.0)))
                .Select(index => index == 0 ? "MECH" : $"AO_{index + 1}")
                .ToList();
        }

        return structures
            .Select((memberName, index) =>
            {
                var row = new DbDiSystemNameRow();
                row.SystemNumber = index + 1;
                row.SystemPlcShortName = memberName;
                row.PlcVariableName = memberName;
                row.SystemPlcLongName = FirstNonEmpty(row.SystemPlcLongName, JoinPrefixAndName(structPrefix, memberName));
                row.HmiSystemName = FirstNonEmpty(row.HmiSystemName, memberName);
                row.HmiSystemNameOther = FirstNonEmpty(row.HmiSystemNameOther, row.HmiSystemName);
                row.NameLength = row.HmiSystemName.Length;
                row.SimulatorDataType = "";
                return row;
            })
            .ToList();
    }

    private static string JoinPrefixAndName(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return name.Trim().TrimStart('_');
        }

        return $"{prefix.Trim().TrimEnd('_')}_{name.Trim().TrimStart('_')}";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static void ApplyGeneratedStructSettingsToRows(IEnumerable<DbStringRow> rows, IEnumerable<DbDiSystemNameRow> settingsRows)
    {
        var settingsByShortName = settingsRows.ToDictionary(row => row.SystemPlcShortName, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (settingsByShortName.TryGetValue(row.PlcStructure, out var setting))
            {
                row.PlcStructure = setting.SystemPlcShortName;
            }
        }
    }

    private void RefreshAllDbViews()
    {
        UpdateDbDiFilterItems();
        UpdateDbDoFilterItems();
        UpdateDbMeasFilterItems();
        UpdateDbAlmFilterItems();
        UpdateDbParFilterItems();
        _dbDiView?.Refresh();
        _dbDoView?.Refresh();
        _dbMeasView?.Refresh();
        _dbAlmView?.Refresh();
        _dbParView?.Refresh();
        DbDiGrid.Items.Refresh();
        DbDoGrid.Items.Refresh();
        DbMeasGrid.Items.Refresh();
        DbAlmGrid.Items.Refresh();
        DbParGrid.Items.Refresh();
    }

    private void ToolbarSettings_Click(object sender, RoutedEventArgs e)
    {
        var activeHeader = MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
        switch (activeHeader)
        {
            case "DB DI":
                DbDiAdvancedSettings_Click(sender, e);
                break;
            case "DB DO":
                DbDoAdvancedSettings_Click(sender, e);
                break;
            case "DB AO":
                DbAoAdvancedSettings_Click(sender, e);
                break;
            case "DB MEAS":
                DbMeasAdvancedSettings_Click(sender, e);
                break;
            case "DB ALM":
                DbAlmAdvancedSettings_Click(sender, e);
                break;
            case "DB PAR":
                DbParAdvancedSettings_Click(sender, e);
                break;
            default:
                DbDiAdvancedSettings_Click(sender, e);
                break;
        }
    }

    private void DbDiAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DbAdvancedSettingsDialog("DB DI", _project.DbCatalog.DiColorSettings, _project.DbCatalog.DiTextLengths, showColors: true, GetValidationSettings("DB_DI."), _project.DbCatalog.ConditionalColors)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.DbCatalog.DiColorSettings = dialog.ColorRows.Select(row => row.Clone()).ToList();
        _project.DbCatalog.DiTextLengths = dialog.TextLengths.Clone();
        ApplyValidationSettings(dialog.ValidationRows);
        _project.DbCatalog.ConditionalColors = dialog.ConditionalColors.Clone();
        ApplyConfiguredColorPalette();
        ApplyConfiguredConditionalColors();
        ConfigureDbValidationCombos();
        UpdateDbDiFilterItems();
        _dbDiView?.Refresh();
        DbDiGrid.Items.Refresh();
        ShowMessage("DB DI settings updated.");
    }

    private void DbDoAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DbAdvancedSettingsDialog("DB DO", [], _project.DbCatalog.DoTextLengths, showColors: false, GetValidationSettings("DB_DO."), _project.DbCatalog.ConditionalColors)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.DbCatalog.DoTextLengths = dialog.TextLengths.Clone();
        ApplyValidationSettings(dialog.ValidationRows);
        _project.DbCatalog.ConditionalColors = dialog.ConditionalColors.Clone();
        ApplyConfiguredConditionalColors();
        ConfigureDbValidationCombos();
        _dbDoView?.Refresh();
        DbDoGrid.Items.Refresh();
        ShowMessage("DB DO settings updated.");
    }

    private void DbMeasAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenDbTextSettings("DB MEAS", _project.DbCatalog.MeasTextLengths, settings => _project.DbCatalog.MeasTextLengths = settings, "DB_MEAS.");
    }

    private void DbAoAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenDbTextSettings("DB AO", _project.DbCatalog.AoTextLengths, settings => _project.DbCatalog.AoTextLengths = settings, DbValidationDefaults.DbMeasUnit);
    }

    private void DbAlmAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenDbTextSettings("DB ALM", _project.DbCatalog.AlarmTextLengths, settings => _project.DbCatalog.AlarmTextLengths = settings, "DB_ALM.");
    }

    private void DbParAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenDbTextSettings("DB PAR", _project.DbCatalog.ParameterTextLengths, settings => _project.DbCatalog.ParameterTextLengths = settings, "DB_PAR.", "LIST_PAR.");
    }

    private void OpenDbTextSettings(string dbName, DbTextLengthSettings currentSettings, Action<DbTextLengthSettings> applySettings, params string[] validationPrefixes)
    {
        var dialog = new DbAdvancedSettingsDialog(dbName, [], currentSettings, showColors: false, GetValidationSettings(validationPrefixes), _project.DbCatalog.ConditionalColors)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        applySettings(dialog.TextLengths.Clone());
        ApplyValidationSettings(dialog.ValidationRows);
        _project.DbCatalog.ConditionalColors = dialog.ConditionalColors.Clone();
        ApplyConfiguredConditionalColors();
        ConfigureDbValidationCombos();
        _validationStateUpdater.Update(_project);
        DbMeasGrid.Items.Refresh();
        DbAlmGrid.Items.Refresh();
        DbParGrid.Items.Refresh();
        ShowMessage($"{dbName} settings updated.");
    }

    private void DbDataBlockSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DbDataBlockSettingsDialog(GetDataBlockNameSettings())
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ApplyDataBlockNameSettings(dialog.Rows);
        ShowMessage("DB names and numbers updated.");
    }

    private void GetFromConfiguration_Click(object sender, RoutedEventArgs e)
    {
        FillDigitalDbFromConfiguration(_project.DbCatalog.DiRows, _project.DigitalInputs, includeInverted: true);
        FillDigitalDbFromConfiguration(_project.DbCatalog.DoRows, _project.DigitalOutputs, includeInverted: false);
        FillMeasDbFromConfiguration();
        FillAoDbFromConfiguration();
        DbDiGrid.Items.Refresh();
        DbDoGrid.Items.Refresh();
        DbMeasGrid.Items.Refresh();
        _dbAoRowsView?.Refresh();
        DbAoGrid.Items.Refresh();
        ShowMessage("DB DI, DB DO, DB MEAS and DB AO configuration columns updated.");
    }

    private void ConfigureDbDiRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var dialog = new DbDiRowSettingsDialog(row, DbDiColorValues)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!ValidateDbTextValues("DB DI", dialog.StatusSystemEnglish, dialog.StatusNameEnglish, dialog.StatusSystemOther, dialog.StatusNameOther, _project.DbCatalog.DiTextLengths))
        {
            return;
        }

        row.SystemNameEnglish = dialog.StatusSystemEnglish;
        row.NameEnglish = dialog.StatusNameEnglish;
        row.SystemNameOther = dialog.StatusSystemOther;
        row.NameOther = dialog.StatusNameOther;
        row.ColorValue0 = dialog.ColorValue0;
        row.ColorValue1 = dialog.ColorValue1;

        var structureStartId = (row.Id / 32) * 32;
        var structureEndId = structureStartId + 31;

        if (dialog.ApplyStatusSystemEnglishToStructure)
        {
            foreach (var structureRow in _project.DbCatalog.DiRows.Where(item => item.Id >= structureStartId && item.Id <= structureEndId))
            {
                structureRow.SystemNameEnglish = dialog.StatusSystemEnglish;
            }
        }

        if (dialog.ApplyStatusSystemOtherToStructure)
        {
            foreach (var structureRow in _project.DbCatalog.DiRows.Where(item => item.Id >= structureStartId && item.Id <= structureEndId))
            {
                structureRow.SystemNameOther = dialog.StatusSystemOther;
            }
        }

        UpdateDbDiFilterItems();
        _dbDiView?.Refresh();
        DbDiGrid.Items.Refresh();
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB DI row updated: ID {row.Id}.");
    }

    private void ConfigureDbDiPlcDefinition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var dialog = new DbDiPlcDefinitionDialog(row)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.PlcName = dialog.PlcDefinitionName;
        UpdateDbDiFilterItems();
        _dbDiView?.Refresh();
        DbDiGrid.Items.Refresh();
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB DI PLC definition updated: ID {row.Id}.");
    }

    private void ConfigureDbDoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var dialog = new DbDiRowSettingsDialog(row, [], "DB DO", showColors: false)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!ValidateDbTextValues("DB DO", dialog.StatusSystemEnglish, dialog.StatusNameEnglish, dialog.StatusSystemOther, dialog.StatusNameOther, _project.DbCatalog.DoTextLengths))
        {
            return;
        }

        row.SystemNameEnglish = dialog.StatusSystemEnglish;
        row.NameEnglish = dialog.StatusNameEnglish;
        row.SystemNameOther = dialog.StatusSystemOther;
        row.NameOther = dialog.StatusNameOther;

        var structureStartId = (row.Id / 32) * 32;
        var structureEndId = structureStartId + 31;

        if (dialog.ApplyStatusSystemEnglishToStructure)
        {
            foreach (var structureRow in _project.DbCatalog.DoRows.Where(item => item.Id >= structureStartId && item.Id <= structureEndId))
            {
                structureRow.SystemNameEnglish = dialog.StatusSystemEnglish;
            }
        }

        if (dialog.ApplyStatusSystemOtherToStructure)
        {
            foreach (var structureRow in _project.DbCatalog.DoRows.Where(item => item.Id >= structureStartId && item.Id <= structureEndId))
            {
                structureRow.SystemNameOther = dialog.StatusSystemOther;
            }
        }

        UpdateDbDoFilterItems();
        _dbDoView?.Refresh();
        DbDoGrid.Items.Refresh();
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB DO row updated: ID {row.Id}.");
    }

    private void ConfigureDbDoPlcDefinition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var dialog = new DbDiPlcDefinitionDialog(row, "DB DO")
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        row.PlcName = dialog.PlcDefinitionName;
        UpdateDbDoFilterItems();
        _dbDoView?.Refresh();
        DbDoGrid.Items.Refresh();
        RecalculateAndRefreshSignalGrids();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB DO PLC definition updated: ID {row.Id}.");
    }

    private void ConfigureDbMeasRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbStringRow.NameEnglish), "MEAS NAME ENG", row.NameEnglish),
            Field(nameof(DbStringRow.NameOther), "MEAS NAME OTHER", row.NameOther),
            Field(nameof(DbStringRow.Unit), "UNIT", row.Unit, GetValidationOptions(DbValidationDefaults.DbMeasUnit)),
            Field(nameof(DbStringRow.LoggingEnable), "Logging enable", row.LoggingEnable, GetValidationOptions(DbValidationDefaults.DbMeasLoggingEnable)),
            Field(nameof(DbStringRow.LoggingTime), "Logging time (s)", row.LoggingTime, GetValidationOptions(DbValidationDefaults.DbMeasLoggingTime))
        };

        if (!ShowDbStringRowDialog("DB MEAS row", row, fields, _project.DbCatalog.MeasTextLengths, "DB MEAS"))
        {
            return;
        }

        DbMeasGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB MEAS row updated: ID {row.Id}.");
    }

    private void ConfigureDbMeasPlcName_Click(object sender, RoutedEventArgs e)
    {
        ConfigureDbStringPlcName(sender, "DB MEAS", DbMeasGrid);
    }

    private void ConfigureDbAoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbAoRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbAoRow.AoSystemName), "AO SYSTEM NAME", row.AoSystemName),
            Field(nameof(DbAoRow.NameEnglish), "NAME ENGLISH", row.NameEnglish),
            Field(nameof(DbAoRow.NameOther), "NAME OTHER", row.NameOther),
            Field(nameof(DbAoRow.Unit), "UNIT", row.Unit, GetValidationOptions(DbValidationDefaults.DbMeasUnit))
        };

        if (!ShowDbAoRowDialog("DB AO row", row, fields))
        {
            return;
        }

        _dbAoRowsView?.Refresh();
        DbAoGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB AO row updated: AO {row.AoNumber}.");
    }

    private void ConfigureDbAoNames_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbAoRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbAoRow.AoPlcName), "AO PLC NAME", row.AoPlcName)
        };

        if (!ShowDbAoRowDialog("DB AO names", row, fields))
        {
            return;
        }

        _dbAoRowsView?.Refresh();
        DbAoGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB AO PLC name updated: AO {row.AoNumber}.");
    }

    private void ConfigureDbAlmRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbStringRow.SystemNameEnglish), "ALARM SYSTEM ENG", row.SystemNameEnglish),
            Field(nameof(DbStringRow.NameEnglish), "ALARM TEXT ENG", row.NameEnglish),
            Field(nameof(DbStringRow.SystemNameOther), "ALARM SYSTEM OTHER", row.SystemNameOther),
            Field(nameof(DbStringRow.NameOther), "ALARM TEXT OTHER", row.NameOther),
            Field(nameof(DbStringRow.AlarmControl), "ALM CONTROL", row.AlarmControl, GetValidationOptions(DbValidationDefaults.DbAlmEnableDisable)),
            Field(nameof(DbStringRow.SrAlarm), "SR ALARM", row.SrAlarm, row.SrAlarmOptions),
            Field(nameof(DbStringRow.AlarmClass), "ALARM CLASS", row.AlarmClass, GetValidationOptions(DbValidationDefaults.DbAlmAlarmClass)),
            Field(nameof(DbStringRow.ReactToUnit), "REACT TO UNIT", row.ReactToUnit, GetValidationOptions(DbValidationDefaults.DbAlmReactToUnit)),
            Field(nameof(DbStringRow.Spare), "SPARE", row.Spare, GetValidationOptions(DbValidationDefaults.DbAlmAlarmClass)),
            Field(nameof(DbStringRow.BlockingClass1), "BLOCKING CLASS 1", row.BlockingClass1, GetValidationOptions(DbValidationDefaults.DbAlmBlockingClass)),
            Field(nameof(DbStringRow.BlockingClass2), "BLOCKING CLASS 2", row.BlockingClass2, GetValidationOptions(DbValidationDefaults.DbAlmBlockingClass)),
            Field(nameof(DbStringRow.Delay), "DELAY", row.Delay),
            Field(nameof(DbStringRow.TimeBase), "TIME base", row.TimeBase, GetValidationOptions(DbValidationDefaults.DbAlmTimeBase)),
            Field(nameof(DbStringRow.DescriptionEnglish), "DESCRIPTION ENGLISH", row.DescriptionEnglish),
            Field(nameof(DbStringRow.DescriptionOther), "DESCRIPTION OTHER", row.DescriptionOther)
        };

        if (!ShowDbStringRowDialog("DB ALM row", row, fields, _project.DbCatalog.AlarmTextLengths, "DB ALM", showApplyToStructure: true, applyToStructureRows: _project.DbCatalog.AlarmRows))
        {
            return;
        }

        DbAlmGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB ALM row updated: ID {row.Id}.");
    }

    private void ConfigureDbAlmPlcName_Click(object sender, RoutedEventArgs e)
    {
        ConfigureDbStringPlcName(sender, "DB ALM", DbAlmGrid);
    }

    private void ConfigureDbParRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbStringRow.NameEnglish), "PARAMETER NAME ENG", row.NameEnglish),
            Field(nameof(DbStringRow.NameOther), "PARAMETAR NAME OTHER", row.NameOther),
            Field(nameof(DbStringRow.ParameterType), "PAR TYPE", row.ParameterType, GetValidationOptions(DbValidationDefaults.DbParType)),
            Field(nameof(DbStringRow.IntegerValue), "I_VAL", row.IntegerValue),
            Field(nameof(DbStringRow.TimeValue), "T_VAL", row.TimeValue),
            Field(nameof(DbStringRow.RealValue), "R_VAL", row.RealValue),
            Field(nameof(DbStringRow.Unit), "UNIT", row.Unit, GetValidationOptions(DbValidationDefaults.DbMeasUnit)),
            Field(nameof(DbStringRow.MinValue), "MIN VAL", row.MinValue),
            Field(nameof(DbStringRow.MaxValue), "MAX VAL", row.MaxValue),
            Field(nameof(DbStringRow.DescriptionEnglish), "DESCRIPTION ENGLISH", row.DescriptionEnglish),
            Field(nameof(DbStringRow.DescriptionOther), "DESCRIPTION OTHER", row.DescriptionOther)
        };

        if (!ShowDbStringRowDialog("DB PAR row", row, fields, _project.DbCatalog.ParameterTextLengths, "DB PAR"))
        {
            return;
        }

        NormalizeDbParRealValue(row);
        DbParGrid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"DB PAR row updated: ID {row.Id}.");
    }

    private void ConfigureDbParPlcName_Click(object sender, RoutedEventArgs e)
    {
        ConfigureDbStringPlcName(sender, "DB PAR", DbParGrid);
    }

    private bool ShowDbStringRowDialog(string title, DbStringRow row, IEnumerable<DbStringRowConfigField> fields, DbTextLengthSettings textLengths, string dbName, bool showApplyToStructure = false, IEnumerable<DbStringRow>? applyToStructureRows = null)
    {
        var templateScope = dbName.Equals("DB ALM", StringComparison.OrdinalIgnoreCase)
            ? "ALM"
            : dbName.Equals("DB PAR", StringComparison.OrdinalIgnoreCase) ? "PAR" : "";
        var dialog = new DbStringRowConfigDialog(title, fields, showApplyToStructure, _project.DescriptionTemplates, templateScope)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var originalDescriptionEnglish = row.DescriptionEnglish;
        var originalDescriptionOther = row.DescriptionOther;
        var originalTemplateKey = row.DescriptionTemplateKey;
        var values = dialog.Values;
        var systemEng = values.GetValueOrDefault(nameof(DbStringRow.SystemNameEnglish), row.SystemNameEnglish);
        var nameEng = values.GetValueOrDefault(nameof(DbStringRow.NameEnglish), row.NameEnglish);
        var systemOther = values.GetValueOrDefault(nameof(DbStringRow.SystemNameOther), row.SystemNameOther);
        var nameOther = values.GetValueOrDefault(nameof(DbStringRow.NameOther), row.NameOther);
        if (!ValidateDbTextValues(dbName, systemEng, nameEng, systemOther, nameOther, textLengths))
        {
            return false;
        }

        foreach (var (key, value) in values)
        {
            SetDbStringRowValue(row, key, value);
        }

        if (!string.IsNullOrWhiteSpace(dialog.AppliedTemplateKey))
        {
            row.DescriptionTemplateKey = dialog.AppliedTemplateKey;
            row.DescriptionTemplateOptOut = false;
            row.DescriptionDirty = false;
        }
        else if (!string.IsNullOrWhiteSpace(originalTemplateKey)
                 && (!string.Equals(originalDescriptionEnglish, row.DescriptionEnglish, StringComparison.Ordinal)
                     || !string.Equals(originalDescriptionOther, row.DescriptionOther, StringComparison.Ordinal)))
        {
            row.DescriptionTemplateKey = "";
            row.DescriptionDirty = true;
        }

        ApplyDescriptionTemplatesAndRefresh();

        if (dialog.ApplyToStructure && applyToStructureRows is not null)
        {
            foreach (var structureRow in applyToStructureRows.Where(item => item.PlcStructure.Equals(row.PlcStructure, StringComparison.OrdinalIgnoreCase)))
            {
                if (dialog.ApplySystemEnglishToStructure)
                {
                    structureRow.SystemNameEnglish = systemEng;
                }

                if (dialog.ApplySystemOtherToStructure)
                {
                    structureRow.SystemNameOther = systemOther;
                }
            }
        }

        return true;
    }

    private bool ShowDbAoRowDialog(string title, DbAoRow row, IEnumerable<DbStringRowConfigField> fields)
    {
        var dialog = new DbStringRowConfigDialog(title, fields)
        {
            Owner = EditorOwner
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var values = dialog.Values;
        var systemName = values.GetValueOrDefault(nameof(DbAoRow.AoSystemName), row.AoSystemName);
        var plcName = values.GetValueOrDefault(nameof(DbAoRow.AoPlcName), row.AoPlcName);
        var nameEnglish = values.GetValueOrDefault(nameof(DbAoRow.NameEnglish), row.NameEnglish);
        var nameOther = values.GetValueOrDefault(nameof(DbAoRow.NameOther), row.NameOther);
        if (!ValidateDbTextValues("DB AO", systemName, plcName, nameEnglish, nameOther, _project.DbCatalog.AoTextLengths))
        {
            return false;
        }

        foreach (var (key, value) in values)
        {
            SetDbAoRowValue(row, key, value);
        }

        return true;
    }

    private void ConfigureDbStringPlcName(object sender, string dbName, DataGrid grid)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[] { Field(nameof(DbStringRow.PlcName), "PLC NAME", row.PlcName) };
        if (!ShowDbStringRowDialog($"{dbName} PLC name", row, fields, DbTextLengthSettings.CreateParameterDefaults(), dbName))
        {
            return;
        }

        grid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"{dbName} PLC name updated: ID {row.Id}.");
    }

    private static DbStringRowConfigField Field(string key, string label, string value, IReadOnlyList<DbValidationOption>? options = null)
    {
        return new DbStringRowConfigField(key, label, value, options ?? []);
    }

    private static void SetDbAoRowValue(DbAoRow row, string key, string value)
    {
        switch (key)
        {
            case nameof(DbAoRow.AoSystemName): row.AoSystemName = value; break;
            case nameof(DbAoRow.AoPlcName): row.AoPlcName = value; break;
            case nameof(DbAoRow.NameEnglish): row.NameEnglish = value; break;
            case nameof(DbAoRow.NameOther): row.NameOther = value; break;
            case nameof(DbAoRow.Unit): row.Unit = value; break;
        }
    }

    private static void SetDbStringRowValue(DbStringRow row, string key, string value)
    {
        switch (key)
        {
            case nameof(DbStringRow.PlcName): row.PlcName = value; break;
            case nameof(DbStringRow.NameEnglish): row.NameEnglish = value; break;
            case nameof(DbStringRow.NameOther): row.NameOther = value; break;
            case nameof(DbStringRow.SystemNameEnglish): row.SystemNameEnglish = value; break;
            case nameof(DbStringRow.ParameterType): row.ParameterType = value; break;
            case nameof(DbStringRow.SystemNameOther): row.SystemNameOther = value; break;
            case nameof(DbStringRow.LoggingEnable): row.LoggingEnable = value; break;
            case nameof(DbStringRow.LoggingTime): row.LoggingTime = value; break;
            case nameof(DbStringRow.AlarmControl): row.AlarmControl = value; break;
            case nameof(DbStringRow.SrAlarm): row.SrAlarm = value; break;
            case nameof(DbStringRow.AlarmClass): row.AlarmClass = value; break;
            case nameof(DbStringRow.ReactToUnit): row.ReactToUnit = value; break;
            case nameof(DbStringRow.Spare): row.Spare = value; break;
            case nameof(DbStringRow.BlockingClass1): row.BlockingClass1 = value; break;
            case nameof(DbStringRow.BlockingClass2): row.BlockingClass2 = value; break;
            case nameof(DbStringRow.Delay): row.Delay = value; break;
            case nameof(DbStringRow.TimeBase): row.TimeBase = value; break;
            case nameof(DbStringRow.IntegerValue): row.IntegerValue = value; break;
            case nameof(DbStringRow.TimeValue): row.TimeValue = value; break;
            case nameof(DbStringRow.RealValue): row.RealValue = value; break;
            case nameof(DbStringRow.Unit): row.Unit = value; break;
            case nameof(DbStringRow.MinValue): row.MinValue = value; break;
            case nameof(DbStringRow.MaxValue): row.MaxValue = value; break;
            case nameof(DbStringRow.DescriptionEnglish): row.DescriptionEnglish = value; row.DescriptionTemplateKey = ""; row.DescriptionDirty = true; break;
            case nameof(DbStringRow.DescriptionOther): row.DescriptionOther = value; row.DescriptionTemplateKey = ""; row.DescriptionDirty = true; break;
            case nameof(DbStringRow.DescriptionTemplateKey): row.DescriptionTemplateKey = value; row.DescriptionDirty = true; break;
        }
    }

    private static void NormalizeDbParRealValue(DbStringRow row)
    {
        if (!IsDbParRealType(row.ParameterType))
        {
            return;
        }

        var text = row.RealValue.Trim().Replace(',', '.');
        row.RealValue = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.0000", CultureInfo.InvariantCulture)
            : "0.0000";
    }

    private static bool IsDbParRealType(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("0", StringComparison.Ordinal))
        {
            return true;
        }

        var separator = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            text = text[(separator + 3)..].Trim();
        }

        return text.Equals("REAL", StringComparison.OrdinalIgnoreCase);
    }

    private void DbGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            ResetAutoFillState();

            if (sender is not DataGrid grid || e.ClickCount > 1)
            {
                return;
            }

            var cell = FindAutoFillCellFromEvent(grid, e.OriginalSource);
            if (cell?.DataContext is null || cell.Column is null || !IsEditableSpreadsheetColumn(grid, cell.Column))
            {
                return;
            }

            if (!IsNearCellBottomEdge(cell, e))
            {
                return;
            }

            var propertyName = GetDbColumnProperty(cell.Column);
            if (propertyName is null)
            {
                return;
            }

            _fillGrid = grid;
            _fillSourceRow = cell.DataContext;
            _fillColumn = cell.Column;
            _fillProperty = propertyName;
            _fillValue = GetDbGridCellText(cell.DataContext, cell.Column);
            _fillTemplateKey = GetDescriptionTemplateKey(cell.DataContext, cell.Column);
            _fillStartPoint = e.GetPosition(grid);

            // Collapse any prior multi-cell selection to just the source. Without
            // this, neighboring selected cells in other columns keep showing
            // their AutoFillHandle gripper, making it look like the drag-fill
            // affects multiple columns (visual confusion in DB DI / DB DO where
            // wide selections are common).
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(new DataGridCellInfo(cell.DataContext, cell.Column));
            grid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);

            Mouse.Capture(grid, CaptureMode.SubTree);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            ResetAutoFillState();
            ShowError("Auto-fill start failed: " + ex.Message);
        }
    }

    private void DataGridCell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not DataGridCell cell)
        {
            return;
        }

        var grid = FindVisualParent<DataGrid>(cell);
        cell.Tag = grid is not null && cell.Column is not null && IsEditableSpreadsheetColumn(grid, cell.Column)
            ? "CanAutoFill"
            : null;
    }

    private void DataGridCell_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is DataGridCell cell && !cell.IsSelected)
        {
            cell.Tag = null;
        }
    }

    private void SpreadsheetGrid_SelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => UpdateAutoFillCellTags(grid), DispatcherPriority.Background);
        UpdateSelectionStatus(grid);
    }

    private void UpdateAutoFillCellTags(DataGrid grid)
    {
        foreach (var item in grid.Items)
        {
            if (ReferenceEquals(item, CollectionView.NewItemPlaceholder))
            {
                continue;
            }

            var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            var presenter = row is null ? null : FindVisualChild<DataGridCellsPresenter>(row);
            if (presenter is null)
            {
                continue;
            }

            foreach (var column in grid.Columns)
            {
                if (presenter.ItemContainerGenerator.ContainerFromIndex(column.DisplayIndex) is DataGridCell cell)
                {
                    cell.Tag = cell.IsSelected && IsEditableSpreadsheetColumn(grid, column) ? "CanAutoFill" : null;
                }
            }
        }
    }

    private void DbDiGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbCellEdit(e, "DB DI", _project.DbCatalog.DiTextLengths);
        MarkActiveTabTiaStatusDirty();
    }

    private void DbDoGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbCellEdit(e, "DB DO", _project.DbCatalog.DoTextLengths);
        MarkActiveTabTiaStatusDirty();
    }

    private void DbMeasGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbCellEdit(e, "DB MEAS", _project.DbCatalog.MeasTextLengths);
        MarkActiveTabTiaStatusDirty();
    }

    private void DbAoGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbAoCellEdit(e);
        MarkActiveTabTiaStatusDirty();
    }

    private void DbAlmGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbCellEdit(e, "DB ALM", _project.DbCatalog.AlarmTextLengths);
        MarkDescriptionDirtyIfNeeded(e);
        MarkActiveTabTiaStatusDirty();
        // Rebuilding ~20 filter combo item lists scans every ALM row, so batch
        // rapid consecutive edits behind one debounced refresh instead of paying
        // that cost (plus a full grid re-render) per committed cell.
        DebounceGridRefresh(ref _dbAlmRefreshTimer, () => RefreshFilteredGrid(_dbAlmView, UpdateDbAlmFilterItems, _dbAlmRefreshTimer));
    }

    private static void DebounceGridRefresh(ref DispatcherTimer? timer, Action refresh)
    {
        if (timer is null)
        {
            var created = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            created.Tick += (_, _) =>
            {
                created.Stop();
                refresh();
            };
            timer = created;
        }

        timer.Stop();
        timer.Start();
    }

    private static void RefreshFilteredGrid(ICollectionView? view, Action updateFilterItems, DispatcherTimer? timer)
    {
        // Refresh would throw while another cell edit is already in progress —
        // wait for the next debounce window instead.
        if (view is IEditableCollectionView { IsAddingNew: false, IsEditingItem: false } or null)
        {
            updateFilterItems();
            view?.Refresh();
        }
        else
        {
            timer?.Start();
        }
    }

    private void DbParGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        ValidateDbCellEdit(e, "DB PAR", _project.DbCatalog.ParameterTextLengths);
        MarkDescriptionDirtyIfNeeded(e);
        MarkActiveTabTiaStatusDirty();
    }

    private void SequenceControlStatusGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        DebounceGridRefresh(ref _sequenceControlStatusRefreshTimer, () => RefreshFilteredGrid(
            _sequenceControlStatusView,
            () =>
            {
                UpdateSequenceReferenceOptions();
                UpdateSequenceControlStatusFilterItems();
            },
            _sequenceControlStatusRefreshTimer));
    }

    private void SequenceCommandGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        DebounceGridRefresh(ref _sequenceCommandRefreshTimer, () => RefreshFilteredGrid(
            _sequenceCommandView,
            () =>
            {
                UpdateSequenceReferenceOptions();
                UpdateSequenceCommandFilterItems();
            },
            _sequenceCommandRefreshTimer));
    }

    private void TextListGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            MarkTiaSectionState("DB SEQUENCE", "Differs", "Text lists changed.");
        }
    }

    private static void MarkDescriptionDirtyIfNeeded(DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not DbStringRow row)
        {
            return;
        }

        var propertyName = GetDbColumnProperty(e.Column);
        if (propertyName is nameof(DbStringRow.DescriptionEnglish)
            or nameof(DbStringRow.DescriptionOther)
            or nameof(DbStringRow.DescriptionTemplateKey))
        {
            if (propertyName is nameof(DbStringRow.DescriptionEnglish) or nameof(DbStringRow.DescriptionOther))
            {
                row.DescriptionTemplateKey = "";
            }

            row.DescriptionDirty = true;
        }
    }

    private void DbGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        // Excel cancels the marching-ants when the user starts editing.
        ClearClipboardMarchingAnts();

        if (sender is DataGrid grid && IsTextListGrid(grid))
        {
            if (e.Column.IsReadOnly)
            {
                e.Cancel = true;
                return;
            }

            RecordUndoSnapshot();
            return;
        }

        if (sender is DataGrid scadaGrid && IsScadaGrid(scadaGrid))
        {
            if (e.Column.IsReadOnly)
            {
                e.Cancel = true;
                return;
            }

            RecordUndoSnapshot();
            return;
        }

        if (!IsEditableDbColumn(e.Column))
        {
            e.Cancel = true;
            return;
        }

        RecordUndoSnapshot();
    }

    private void DbGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (sender is not DataGrid grid || cell?.Column is null || IsEditableSpreadsheetColumn(grid, cell.Column))
        {
            return;
        }

        e.Handled = true;
    }

    private bool IsEditableDbColumn(DataGridColumn column)
    {
        return ReferenceEquals(column, DbDiStatusSystemEngColumn)
            || ReferenceEquals(column, DbDiStatusNameEngColumn)
            || ReferenceEquals(column, DbDiStatusSystemOtherColumn)
            || ReferenceEquals(column, DbDiStatusNameOtherColumn)
            || ReferenceEquals(column, DbDoStatusSystemEngColumn)
            || ReferenceEquals(column, DbDoStatusNameEngColumn)
            || ReferenceEquals(column, DbDoStatusSystemOtherColumn)
            || ReferenceEquals(column, DbDoStatusNameOtherColumn)
            || ReferenceEquals(column, DbMeasNameEngColumn)
            || ReferenceEquals(column, DbMeasNameOtherColumn)
            || ReferenceEquals(column, DbAoNameEnglishColumn)
            || ReferenceEquals(column, DbAoNameOtherColumn)
            || ReferenceEquals(column, DbAlmSystemEngColumn)
            || ReferenceEquals(column, DbAlmNameEngColumn)
            || ReferenceEquals(column, DbAlmSystemOtherColumn)
            || ReferenceEquals(column, DbAlmNameOtherColumn)
            || ReferenceEquals(column, DbAlmDescriptionEnglishColumn)
            || ReferenceEquals(column, DbAlmDescriptionOtherColumn)
            || ReferenceEquals(column, DbParNameEngColumn)
            || ReferenceEquals(column, DbParNameOtherColumn)
            || ReferenceEquals(column, DbParDescriptionEnglishColumn)
            || ReferenceEquals(column, DbParDescriptionOtherColumn)
            || ReferenceEquals(column, SequenceControlStatusEngColumn)
            || ReferenceEquals(column, SequenceControlStatusOtherColumn)
            || ReferenceEquals(column, SequenceCommandEngColumn)
            || ReferenceEquals(column, SequenceCommandOtherColumn);
    }

    private bool IsEditableSpreadsheetColumn(DataGrid grid, DataGridColumn column)
    {
        if (IsDbGrid(grid))
        {
            return IsEditableDbColumn(column);
        }

        return !column.IsReadOnly && GetDbColumnProperty(column) is not null;
    }

    private bool IsDbGrid(DataGrid grid)
    {
        return ReferenceEquals(grid, DbDiGrid)
               || ReferenceEquals(grid, DbDoGrid)
               || ReferenceEquals(grid, DbMeasGrid)
               || ReferenceEquals(grid, DbAoGrid)
               || ReferenceEquals(grid, DbAlmGrid)
               || ReferenceEquals(grid, DbParGrid)
               || ReferenceEquals(grid, SequenceControlStatusGrid)
               || ReferenceEquals(grid, SequenceCommandGrid);
    }

    private bool IsTextListGrid(DataGrid grid)
    {
        return ReferenceEquals(grid, TextListAlmBlockingClassGrid)
               || ReferenceEquals(grid, TextListCabinetsGrid)
               || ReferenceEquals(grid, TextListPortConfigurationGrid)
               || ReferenceEquals(grid, TextListSeqCmdGrid)
               || ReferenceEquals(grid, TextListSeqConfigGrid);
    }

    private bool IsScadaGrid(DataGrid grid)
    {
        return ReferenceEquals(grid, ScadaVariablesGrid)
               || ReferenceEquals(grid, ScadaNavigationGrid)
               || ReferenceEquals(grid, ScadaScreensGrid)
               || ReferenceEquals(grid, ScadaTagsGrid)
               || ReferenceEquals(grid, ScadaLocationsGrid)
               || ReferenceEquals(grid, ScadaHeaderGrid)
               || ReferenceEquals(grid, ScadaMainNavGrid);
    }

    private void DbDiInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var alarmState = IsEnabledNumber(row.AlarmConfig) ? "YES" : "NOT";
        var statusTechnical = FormatTechnicalInfo(row.Id.ToString(CultureInfo.InvariantCulture), row.PlcStructure, row.PlcName);
        var statusDescription = JoinNonEmpty(Environment.NewLine, row.SystemNameEnglish, row.NameEnglish);
        var invert = DisplayValidation(DbValidationDefaults.DbDiAlarmBoolean, row.AlarmInvert);
        var alarmId = DisplayAlarmReference(row, "Id");
        var alarmRow = GetAlarmRow(row);
        var alarmTechnical = string.IsNullOrWhiteSpace(alarmId)
            ? ""
            : FormatTechnicalInfo(
                alarmId,
                alarmRow?.PlcStructure ?? "",
                alarmRow?.PlcName ?? "");
        var alarmDescription = JoinNonEmpty(Environment.NewLine, alarmRow?.SystemNameEnglish ?? "", alarmRow?.NameEnglish ?? "");
        var alarmSettings = alarmRow is null ? "" : FormatAlarmSettingsCompact(alarmRow);
        var alarmSettingsRows = alarmRow is null ? null : CreateAlarmSettingsTableRows(alarmRow);
        var color0 = GetDiColorInfo(row.ColorValue0);
        var color1 = GetDiColorInfo(row.ColorValue1);
        var mapping = FormatInfoLines(
            ("USED", ToYesNo(IsEnabledNumber(row.TrueText))),
            ("MODULE", FormatIndexedModuleName(_project.ModuleCatalog.DigitalInputs, row.ModuleName)),
            ("PORT", row.ModulePort?.ToString(CultureInfo.InvariantCulture) ?? ""));
        var texts = FormatInfoLines(
            ("STATUS SYSTEM ENG", row.SystemNameEnglish),
            ("STATUS NAME ENG", row.NameEnglish),
            ("STATUS SYSTEM OTHER", row.SystemNameOther),
            ("STATUS NAME OTHER", row.NameOther));

        var idCrossReferenceVariable = BuildDbDiCrossReferenceVariable(row, "ID");
        var valCrossReferenceVariable = BuildDbDiCrossReferenceVariable(row, "VAL");
        var dialog = new DbDiInfoDialog(
            alarmState,
            statusTechnical,
            statusDescription,
            invert,
            alarmTechnical,
            alarmDescription,
            alarmSettings,
            alarmSettingsRows,
            color0,
            color1,
            mapping,
            texts,
            idCrossReferenceVariable,
            valCrossReferenceVariable,
            (member, selectedItem) => OpenDbDiCrossReferenceInTiaAsync(row, member, selectedItem))
        {
            Owner = EditorOwner
        };

        dialog.Loaded += async (_, _) => await LoadDbDiCrossReferenceAsync(dialog, row);
        dialog.ShowDialog();
    }

    private static string BuildDbDiCrossReferenceVariable(DbStringRow row, string member)
    {
        var safeMember = member.Equals("VAL", StringComparison.OrdinalIgnoreCase) ? "VAL" : "ID";
        return $"DI.{row.PlcStructure}.{row.PlcName}.DATA.{safeMember}";
    }

    private TiaCrossReferenceRequest? CreateDbDiCrossReferenceRequest(DbStringRow row, string member)
    {
        var binding = GetActiveTiaBinding();
        if (binding is null)
        {
            return null;
        }

        return new TiaCrossReferenceRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            VariablePath = BuildDbDiCrossReferenceVariable(row, member),
            MaxItems = 80
        };
    }

    private async Task LoadDbDiCrossReferenceAsync(DbDiInfoDialog dialog, DbStringRow row)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Attach to TIA Portal first to scan cross-references.");
            dialog.SetCrossReferenceUnavailable("VAL", "Attach to TIA Portal first to scan cross-references.");
            return;
        }

        var idRequest = CreateDbDiCrossReferenceRequest(row, "ID");
        var valRequest = CreateDbDiCrossReferenceRequest(row, "VAL");
        if (idRequest is null || valRequest is null)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Active PLC database is not mapped to TIA.");
            dialog.SetCrossReferenceUnavailable("VAL", "Active PLC database is not mapped to TIA.");
            return;
        }

        dialog.SetCrossReferenceLoading("ID");
        dialog.SetCrossReferenceLoading("VAL");
        try
        {
            var idTask = _tiaSessionService.GetCrossReferencesAsync(idRequest);
            var valTask = _tiaSessionService.GetCrossReferencesAsync(valRequest);
            var idResult = await idTask;
            dialog.SetCrossReferenceResult("ID", idResult);
            var valResult = await valTask;
            dialog.SetCrossReferenceResult("VAL", valResult);
        }
        catch (Exception ex)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Cross-reference scan failed: " + ex.Message);
            dialog.SetCrossReferenceUnavailable("VAL", "Cross-reference scan failed: " + ex.Message);
        }
    }

    private async Task OpenDbDiCrossReferenceInTiaAsync(DbStringRow row, string member, TiaCrossReferenceItem? selectedItem)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal first.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var request = CreateDbDiCrossReferenceRequest(row, member);
        if (request is null)
        {
            MessageBox.Show(this, "Active PLC database is not mapped to TIA.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _tiaSessionService.OpenCrossReferenceAsync(new TiaCrossReferenceOpenRequest
            {
                PlcName = request.PlcName,
                DeviceName = request.DeviceName,
                SoftwareName = request.SoftwareName,
                VariablePath = request.VariablePath,
                PreferredBlockName = selectedItem?.BlockName ?? "",
                PreferredNetwork = selectedItem?.Network ?? ""
            });
            MessageBox.Show(this, result.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open TIA cross-reference: " + ex.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private TiaCrossReferenceRequest? CreateCrossReferenceRequest(string variablePath)
    {
        var binding = GetActiveTiaBinding();
        if (binding is null)
        {
            return null;
        }

        return new TiaCrossReferenceRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            VariablePath = variablePath,
            MaxItems = 80
        };
    }

    private async void UpdateDbCrossReferenceCounts_Click(object sender, RoutedEventArgs e)
    {
        var dbName = (sender as FrameworkElement)?.Tag?.ToString() ?? GetActiveTabKey();
        await UpdateDbCrossReferenceCountsAsync(dbName);
    }

    private async Task UpdateDbCrossReferenceCountsAsync(string dbName)
    {
        if (_dbCrossReferenceUpdateRunning || _dbDiTiaOperationRunning || _tiaStatusRefreshRunning)
        {
            ShowMessage("TIA operation is already running.");
            return;
        }

        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal first to update cross-reference counts.", "TIA cross-reference update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (GetActiveTiaBinding() is null)
        {
            MessageBox.Show(this, "Active PLC database is not mapped to TIA.", "TIA cross-reference update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var requests = BuildDbCrossReferenceCountRequests(dbName);
        if (requests.Count == 0)
        {
            ShowMessage($"{dbName}: no cross-reference fields to update.");
            return;
        }

        _dbCrossReferenceUpdateRunning = true;
        var warnings = new List<string>();
        var completed = 0;
        var progressDialog = new TiaProgressDialog($"{dbName} cross-reference update", $"Scanning 0 / {requests.Count} references...")
        {
            Owner = EditorOwner
        };
        progressDialog.Show();

        try
        {
            // Skip unusable paths up-front; group the rest into a single batch.
            var batchedRequests = new List<CrossReferenceCountRequest>();
            foreach (var item in requests)
            {
                if (!IsUsableCrossReferencePath(item.VariablePath))
                {
                    item.ApplyCount(null);
                    item.ApplyWriteCount?.Invoke(null);
                    continue;
                }
                batchedRequests.Add(item);
            }

            var binding = GetActiveTiaBinding();
            if (binding is null)
            {
                warnings.Add("Active PLC database is not mapped to TIA.");
            }
            else if (batchedRequests.Count > 0)
            {
                progressDialog.SetStatus($"Scanning {batchedRequests.Count} references in one batch...");
                var batchRequest = new TiaCrossReferenceBatchRequest
                {
                    PlcName = binding.PlcName,
                    DeviceName = binding.DeviceName,
                    SoftwareName = binding.SoftwareName,
                    VariablePaths = batchedRequests.Select(r => r.VariablePath).ToList()
                };
                try
                {
                    var batchResult = await _tiaSessionService.GetCrossReferencesBatchAsync(batchRequest);
                    var byPath = batchResult.Results.ToDictionary(r => r.VariablePath, StringComparer.OrdinalIgnoreCase);
                    foreach (var item in batchedRequests)
                    {
                        completed++;
                        if (byPath.TryGetValue(item.VariablePath, out var hit))
                        {
                            item.ApplyCount(hit.UsageCount);
                            item.ApplyWriteCount?.Invoke(hit.WriteCount);
                        }
                        else
                        {
                            item.ApplyCount(null);
                            item.ApplyWriteCount?.Invoke(null);
                        }
                    }
                    foreach (var w in batchResult.Warnings.Take(8))
                    {
                        warnings.Add(w);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Batch cross-reference call failed: {ex.Message}");
                }
            }

            RefreshDbCrossReferenceCountView(dbName);
            var warningText = warnings.Count == 0 ? "" : $" Warnings: {string.Join(" | ", warnings.Take(3))}";
            ShowMessage($"{dbName}: cross-reference counts updated ({requests.Count} checks).{warningText}");
        }
        finally
        {
            progressDialog.Close();
            _dbCrossReferenceUpdateRunning = false;
        }
    }

    private List<CrossReferenceCountRequest> BuildDbCrossReferenceCountRequests(string dbName)
    {
        var normalizedDbName = NormalizeDbName(dbName);
        var requests = new List<CrossReferenceCountRequest>();
        switch (normalizedDbName)
        {
            case "DB DI":
                foreach (var row in _project.DbCatalog.DiRows)
                {
                    requests.Add(new CrossReferenceCountRequest("ID", BuildDbDiCrossReferenceVariable(row, "ID"), count => row.CrossReferenceIdCount = count));
                    {
                        var rowRef = row;
                        requests.Add(new CrossReferenceCountRequest("VAL", BuildDbDiCrossReferenceVariable(rowRef, "VAL"), count => rowRef.CrossReferenceValCount = count, writes => rowRef.CrossReferenceValWriteCount = writes));
                    }
                }
                break;
            case "DB DO":
                foreach (var row in _project.DbCatalog.DoRows)
                {
                    requests.Add(new CrossReferenceCountRequest("ID", $"DO.{row.PlcStructure}.{row.PlcName}.DATA.ID", count => row.CrossReferenceIdCount = count));
                    {
                        var rowRef = row;
                        requests.Add(new CrossReferenceCountRequest("VAL", $"DO.{rowRef.PlcStructure}.{rowRef.PlcName}.DATA.VAL", count => rowRef.CrossReferenceValCount = count, writes => rowRef.CrossReferenceValWriteCount = writes));
                    }
                }
                break;
            case "DB MEAS":
                foreach (var row in _project.DbCatalog.MeasRows)
                {
                    requests.Add(new CrossReferenceCountRequest("MEAS_ID", $"MEAS.{row.PlcStructure}.{row.PlcName}.MEAS_ID", count => row.CrossReferenceMeasIdCount = count));
                    {
                        var rowRef = row;
                        requests.Add(new CrossReferenceCountRequest("VAL", $"MEAS.{rowRef.PlcStructure}.{rowRef.PlcName}.VAL", count => rowRef.CrossReferenceValCount = count, writes => rowRef.CrossReferenceValWriteCount = writes));
                    }
                }
                break;
            case "DB AO":
                foreach (var row in _project.DbCatalog.AoRows)
                {
                    var rowRef = row;
                    var specs = BuildDbAoCrossReferenceSpecs(rowRef);
                    foreach (var spec in specs)
                    {
                        requests.Add(spec.Member.Equals("ANA_CTRL_ID", StringComparison.OrdinalIgnoreCase)
                            ? new CrossReferenceCountRequest(spec.Member, spec.VariablePath, count => rowRef.CrossReferenceAnaCtrlIdCount = count)
                            : new CrossReferenceCountRequest(spec.Member, spec.VariablePath, count => rowRef.CrossReferenceValCount = count, writes => rowRef.CrossReferenceValWriteCount = writes));
                    }
                }
                break;
            case "DB ALM":
                foreach (var row in _project.DbCatalog.AlarmRows)
                {
                    requests.Add(new CrossReferenceCountRequest("ALM_ID", $"ALM.{row.PlcStructure}.{row.PlcName}.ALM_ID", count => row.CrossReferenceAlmIdCount = count));
                    {
                        var rowRef = row;
                        requests.Add(new CrossReferenceCountRequest("TEMP", $"ALM.{rowRef.PlcStructure}.{rowRef.PlcName}.TEMP", count => rowRef.CrossReferenceTempCount = count, writes => rowRef.CrossReferenceTempWriteCount = writes));
                    }
                    requests.Add(new CrossReferenceCountRequest("ALM", $"ALM.{row.PlcStructure}.{row.PlcName}.ALM", count => row.CrossReferenceAlmCount = count));
                }
                break;
            case "DB PAR":
                foreach (var row in _project.DbCatalog.ParameterRows)
                {
                    requests.Add(new CrossReferenceCountRequest("ID", $"PAR.{row.PlcStructure}.{row.PlcName}.ID", count => row.CrossReferenceIdCount = count));
                    requests.Add(new CrossReferenceCountRequest("R_VAL", $"PAR.{row.PlcStructure}.{row.PlcName}.R_VAL", count => row.CrossReferenceRValCount = count));
                    requests.Add(new CrossReferenceCountRequest("I_VAL", $"PAR.{row.PlcStructure}.{row.PlcName}.I_VAL", count => row.CrossReferenceIValCount = count));
                    requests.Add(new CrossReferenceCountRequest("T_VAL", $"PAR.{row.PlcStructure}.{row.PlcName}.T_VAL", count => row.CrossReferenceTValCount = count));
                }
                break;
            case "DB SEQUENCE":
                foreach (var row in _project.DbCatalog.Sequence.ControlStatusRows)
                {
                    requests.Add(new CrossReferenceCountRequest("CTRL_STS ID", BuildSequenceCrossReferenceVariable(row, isCommand: false, "ID"), count => row.CrossReferenceIdCount = count));
                    requests.Add(new CrossReferenceCountRequest("CTRL_STS VAL", BuildSequenceCrossReferenceVariable(row, isCommand: false, "VAL"), count => row.CrossReferenceValCount = count));
                }

                foreach (var row in _project.DbCatalog.Sequence.CommandRows)
                {
                    requests.Add(new CrossReferenceCountRequest("SEQ_CMD ID", BuildSequenceCrossReferenceVariable(row, isCommand: true, "ID"), count => row.CrossReferenceIdCount = count));
                    requests.Add(new CrossReferenceCountRequest("SEQ_CMD VAL", BuildSequenceCrossReferenceVariable(row, isCommand: true, "VAL"), count => row.CrossReferenceValCount = count));
                }
                break;
        }

        return requests;
    }

    private void RefreshDbCrossReferenceCountView(string dbName)
    {
        switch (NormalizeDbName(dbName))
        {
            case "DB DI":
                UpdateDbDiFilterItems();
                _dbDiView?.Refresh();
                DbDiGrid.Items.Refresh();
                break;
            case "DB DO":
                UpdateDbDoFilterItems();
                _dbDoView?.Refresh();
                DbDoGrid.Items.Refresh();
                break;
            case "DB MEAS":
                UpdateDbMeasFilterItems();
                _dbMeasView?.Refresh();
                DbMeasGrid.Items.Refresh();
                break;
            case "DB AO":
                UpdateDbAoFilterItems();
                _dbAoRowsView?.Refresh();
                DbAoGrid.Items.Refresh();
                break;
            case "DB ALM":
                UpdateDbAlmFilterItems();
                _dbAlmView?.Refresh();
                DbAlmGrid.Items.Refresh();
                break;
            case "DB PAR":
                UpdateDbParFilterItems();
                _dbParView?.Refresh();
                DbParGrid.Items.Refresh();
                break;
            case "DB SEQUENCE":
                UpdateSequenceControlStatusFilterItems();
                UpdateSequenceCommandFilterItems();
                _sequenceControlStatusView?.Refresh();
                _sequenceCommandView?.Refresh();
                SequenceControlStatusGrid.Items.Refresh();
                SequenceCommandGrid.Items.Refresh();
                break;
        }
    }

    private static bool IsUsableCrossReferencePath(string variablePath)
    {
        return !string.IsNullOrWhiteSpace(variablePath)
               && variablePath.Split('.', StringSplitOptions.None).All(part => !string.IsNullOrWhiteSpace(part));
    }

    private static string NormalizeDbName(string dbName)
    {
        var text = (dbName ?? "").Trim().ToUpperInvariant();
        return text switch
        {
            "DI" => "DB DI",
            "DO" => "DB DO",
            "MEAS" => "DB MEAS",
            "AO" => "DB AO",
            "ALM" => "DB ALM",
            "PAR" => "DB PAR",
            "SEQUENCE" or "DB SEQ" or "DB SEQ PAR" or "DB SEQUENCE PAR" => "DB SEQUENCE",
            _ => text
        };
    }

    private async Task LoadTiaCrossReferencePanelAsync(TiaCrossReferencePanel panel, IReadOnlyList<TiaCrossReferenceSpec> specs)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            foreach (var spec in specs)
            {
                panel.SetCrossReferenceUnavailable(spec.Member, "Attach to TIA Portal first to scan cross-references.");
            }

            return;
        }

        var requests = specs
            .Select(spec => (Spec: spec, Request: CreateCrossReferenceRequest(spec.VariablePath)))
            .ToList();
        if (requests.Any(item => item.Request is null))
        {
            foreach (var spec in specs)
            {
                panel.SetCrossReferenceUnavailable(spec.Member, "Active PLC database is not mapped to TIA.");
            }

            return;
        }

        foreach (var spec in specs)
        {
            panel.SetCrossReferenceLoading(spec.Member);
        }

        try
        {
            var tasks = requests
                .Select(async item => (item.Spec, Result: await _tiaSessionService.GetCrossReferencesAsync(item.Request!)))
                .ToList();
            var results = await Task.WhenAll(tasks);
            foreach (var item in results)
            {
                panel.SetCrossReferenceResult(item.Spec.Member, item.Result);
            }
        }
        catch (Exception ex)
        {
            foreach (var spec in specs)
            {
                panel.SetCrossReferenceUnavailable(spec.Member, "Cross-reference scan failed: " + ex.Message);
            }
        }
    }

    private async Task OpenCrossReferenceInTiaAsync(IReadOnlyDictionary<string, string> variablesByMember, string member, TiaCrossReferenceItem? selectedItem)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            MessageBox.Show(this, "Attach to TIA Portal first.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!variablesByMember.TryGetValue(member, out var variablePath))
        {
            MessageBox.Show(this, "Variable path is not available.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var request = CreateCrossReferenceRequest(variablePath);
        if (request is null)
        {
            MessageBox.Show(this, "Active PLC database is not mapped to TIA.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _tiaSessionService.OpenCrossReferenceAsync(new TiaCrossReferenceOpenRequest
            {
                PlcName = request.PlcName,
                DeviceName = request.DeviceName,
                SoftwareName = request.SoftwareName,
                VariablePath = request.VariablePath,
                PreferredBlockName = selectedItem?.BlockName ?? "",
                PreferredNetwork = selectedItem?.Network ?? ""
            });
            MessageBox.Show(this, result.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open TIA cross-reference: " + ex.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private TiaCrossReferencePanel CreateCrossReferencePanel(IReadOnlyList<TiaCrossReferenceSpec> specs)
    {
        var variablesByMember = specs.ToDictionary(spec => spec.Member, spec => spec.VariablePath, StringComparer.OrdinalIgnoreCase);
        return new TiaCrossReferencePanel(specs, (member, selectedItem) => OpenCrossReferenceInTiaAsync(variablesByMember, member, selectedItem));
    }

    private static IReadOnlyList<TiaCrossReferenceSpec> BuildDbStringCrossReferenceSpecs(string dbName, DbStringRow row, params string[] members)
    {
        return members
            .Select(member => new TiaCrossReferenceSpec(member, $"{dbName}.{row.PlcStructure}.{row.PlcName}.DATA.{member}"))
            .ToList();
    }

    private static IReadOnlyList<TiaCrossReferenceSpec> BuildDbStringCrossReferenceSpecsWithoutData(string dbName, DbStringRow row, params string[] members)
    {
        return members
            .Select(member => new TiaCrossReferenceSpec(member, $"{dbName}.{row.PlcStructure}.{row.PlcName}.{member}"))
            .ToList();
    }

    private IReadOnlyList<TiaCrossReferenceSpec> BuildDbAoCrossReferenceSpecs(DbAoRow row)
    {
        var groupName = FindAoGroupVariableName(row);
        var signalName = FirstNonEmpty(row.AoPlcName, $"AO_{row.AoNumber.ToString(CultureInfo.InvariantCulture)}");
        var signalPath = $"{groupName}.{signalName}";
        return new[]
        {
            new TiaCrossReferenceSpec("ANA_CTRL_ID", $"ANA_CTRL.{signalPath}.ANA_CTRL_ID"),
            new TiaCrossReferenceSpec("VAL", $"ANA_CTRL.{signalPath}.VAL")
        };
    }

    private string FindAoGroupVariableName(DbAoRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.AoSystemName))
        {
            return row.AoSystemName;
        }

        var orderedRows = _project.DbCatalog.AoRows.OrderBy(item => item.AoNumber).ToList();
        var rowIndex = orderedRows.FindIndex(item => ReferenceEquals(item, row));
        if (rowIndex < 0)
        {
            rowIndex = orderedRows.FindIndex(item => item.AoNumber == row.AoNumber
                && string.Equals(item.AoSystemName, row.AoSystemName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.AoPlcName, row.AoPlcName, StringComparison.OrdinalIgnoreCase));
        }

        var groupIndex = Math.Max(0, rowIndex) / 32;
        var systems = _project.DbCatalog.AoSystemNameRows.OrderBy(item => item.SystemNumber).ToList();
        if (systems.Count > 0 && groupIndex < systems.Count)
        {
            var system = systems[groupIndex];
            return FirstNonEmpty(system.PlcVariableName, system.SystemPlcShortName, $"AO_{groupIndex + 1}");
        }

        if (groupIndex == 0)
        {
            return "MECH";
        }

        var groupFirst = orderedRows.Skip(groupIndex * 32).FirstOrDefault();
        return FirstNonEmpty(row.AoSystemName, groupFirst?.AoSystemName ?? "", $"AO_{groupIndex + 1}");
    }

    private static string FormatTechnicalInfo(string id, string plcStructure, string plcName)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"ID - {id}",
            $"{plcStructure}.{plcName}".TrimEnd('.')
        });
    }

    private DbStringRow? GetAlarmRow(DbStringRow row)
    {
        return int.TryParse((row.AlarmId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var alarmId)
            ? _project.DbCatalog.AlarmRows.FirstOrDefault(item => item.Id == alarmId)
            : null;
    }

    private ColorInfo GetDiColorInfo(string value)
    {
        var rawValue = (value ?? "").Trim();
        var displayValue = DisplayValidation(DbValidationDefaults.DbDiColorValues, rawValue);
        var colorSetting = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorNumber)
            ? _project.DbCatalog.DiColorSettings.FirstOrDefault(color => color.Value == colorNumber)
            : _project.DbCatalog.DiColorSettings.FirstOrDefault(color => color.Name.Equals(rawValue, StringComparison.OrdinalIgnoreCase));

        var label = JoinNonEmpty(" - ",
            string.IsNullOrWhiteSpace(rawValue) ? "" : rawValue,
            colorSetting?.Name ?? displayValue);
        var color = colorSetting?.Color
                    ?? (ColorValueToBrushConverter.ResolveBrush(displayValue) is SolidColorBrush solidBrush
                        ? solidBrush.Color.ToString(CultureInfo.InvariantCulture)
                        : displayValue);

        return new ColorInfo(string.IsNullOrWhiteSpace(label) ? "NOT CONFIGURED" : label, color);
    }

    private static string FormatIndexedModuleName(IEnumerable<ModuleNameRow> moduleRows, string value)
    {
        var rawValue = (value ?? "").Trim();
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleNumber))
        {
            return rawValue;
        }

        var moduleName = moduleRows.FirstOrDefault(row => row.ModuleNumber == moduleNumber)?.ModuleName;
        return string.IsNullOrWhiteSpace(moduleName) ? rawValue : $"{rawValue} - {moduleName}";
    }

    private string FormatMeasModuleName(DbStringRow row)
    {
        var type = ResolveMeasType(row).ToUpperInvariant();
        var rows = type switch
        {
            "AI" or "ANALOG" or "ANALOG INPUT" => _project.ModuleCatalog.AnalogInputs,
            "RTD" => _project.ModuleCatalog.RtdInputs,
            "COUNTER" => _project.ModuleCatalog.Counters,
            _ when type.Contains("COUNTER", StringComparison.OrdinalIgnoreCase) => _project.ModuleCatalog.Counters,
            _ when type.Contains("RTD", StringComparison.OrdinalIgnoreCase) => _project.ModuleCatalog.RtdInputs,
            _ when type.Contains("AI", StringComparison.OrdinalIgnoreCase) || type.Contains("ANALOG", StringComparison.OrdinalIgnoreCase) => _project.ModuleCatalog.AnalogInputs,
            _ => []
        };

        return rows.Count == 0 ? (row.ModuleName ?? "").Trim() : FormatIndexedModuleName(rows, row.ModuleName);
    }

    private string ResolveMeasType(DbStringRow row)
    {
        var type = (row.Type ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type;
        }

        if (_project.AnalogInputs.Any(item => item.Id == row.Id))
        {
            return "AI";
        }

        if (_project.RtdInputs.Any(item => item.Id == row.Id))
        {
            return "RTD";
        }

        if (_project.Counters.Any(item => item.Id == row.Id))
        {
            return "COUNTER";
        }

        if (_project.Devices.Any(device => device.Config == SignalConfigState.Enabled && row.Id >= device.FirstValueId && row.Id < device.FirstValueId + 24))
        {
            return "DEVICE";
        }

        return "";
    }

    private string FormatAlarmSource(DbStringRow row)
    {
        var digitalSource = FormatSourceReference("Digital", row.SourceDigId, _project.DbCatalog.DiRows);
        var analogSource = FormatSourceReference("Analog", row.SourceAnaId, _project.DbCatalog.MeasRows);

        return JoinNonEmpty(
            Environment.NewLine + Environment.NewLine,
            digitalSource,
            analogSource,
            string.IsNullOrWhiteSpace(digitalSource) && string.IsNullOrWhiteSpace(analogSource)
                ? "Software"
                : "");
    }

    private static string FormatSourceReference(string sourceName, string sourceId, IReadOnlyList<DbStringRow> rows)
    {
        var rawSourceId = (sourceId ?? "").Trim();
        if (!int.TryParse(rawSourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "";
        }

        var row = rows.FirstOrDefault(item => item.Id == id);
        return string.Join(Environment.NewLine, new[]
        {
            sourceName,
            $"ID - {rawSourceId}",
            row is null ? "" : $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'),
            row is null ? "" : JoinNonEmpty(" - ", row.SystemNameEnglish, row.NameEnglish)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private void DbMeasInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var leftItems = new List<MeasInfoItem>
        {
            new("ENB LL", ToYesNo(row.LimitLlEnabled)),
            new("ENB LO", ToYesNo(row.LimitLoEnabled)),
            new("ENB HI", ToYesNo(row.LimitHiEnabled)),
            new("ENB HH", ToYesNo(row.LimitHhEnabled))
        };

        leftItems.AddRange(CreateParameterItems(
            ("PAR_VAL_1", row.ParVal1Id),
            ("PAR_VAL_2", row.ParVal2Id),
            ("PAR_OFFSET", row.ParOffsetId),
            ("PAR_SCAL", row.ParScaleFactorId),
            ("PAR_LL", row.ParLlId),
            ("PAR_LO", row.ParLoId),
            ("PAR_HI", row.ParHiId),
            ("PAR_HH", row.ParHhId)));

        var rightItems = CreateAlarmItems(
            ("ALM_ERR", row.AlarmErrId),
            ("ALM_LL", row.AlarmLlId),
            ("ALM_LO", row.AlarmLoId),
            ("ALM_HI", row.AlarmHiId),
            ("ALM_HH", row.AlarmHhId));
        var logging = FormatInfoLines(
            ("UNIT", DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit)),
            ("LOGGING ENABLE", DisplayValidation(DbValidationDefaults.DbMeasLoggingEnable, row.LoggingEnable)),
            ("LOGGING TIME", DisplayValidation(DbValidationDefaults.DbMeasLoggingTime, row.LoggingTime)));
        var mapping = FormatInfoLines(
            ("USED", ToYesNo(IsEnabledNumber(row.TrueText))),
            ("TYPE", row.Type),
            ("MODULE", FormatMeasModuleName(row)),
            ("PORT", row.ModulePort?.ToString(CultureInfo.InvariantCulture) ?? ""));
        var texts = FormatInfoLines(
            ("MEAS NAME ENG", row.NameEnglish),
            ("MEAS NAME OTHER", row.NameOther));

        var measurementText = string.Join(Environment.NewLine, new[]
        {
            $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'),
            JoinNonEmpty(" - ", row.SystemNameEnglish, row.NameEnglish)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var dialog = new DbMeasInfoDialog(measurementText, leftItems, rightItems, logging, mapping, texts)
        {
            Owner = EditorOwner
        };
        var crossReferenceSpecs = BuildDbStringCrossReferenceSpecsWithoutData("MEAS", row, "MEAS_ID", "VAL");
        var crossReferencePanel = CreateCrossReferencePanel(crossReferenceSpecs);
        dialog.AddCrossReferenceTab(crossReferencePanel);
        dialog.Loaded += async (_, _) => await LoadTiaCrossReferencePanelAsync(crossReferencePanel, crossReferenceSpecs);
        dialog.ShowDialog();
    }

    private void DbAoInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbAoRow row })
        {
            return;
        }

        var leftItems = new List<MeasInfoItem>();

        leftItems.AddRange(CreateParameterItems(
            ("PAR_VAL_LOW", row.ParValLowId),
            ("PAR_VAL_HIGH", row.ParValHighId),
            ("PAR_OFFSET", row.ParOffsetId),
            ("PAR_SCAL", row.ParScaleFactorId)));
        var mapping = FormatInfoLines(
            ("USED", ToYesNo(IsEnabledNumber(row.TrueText))),
            ("MODULE", FormatIndexedModuleName(_project.ModuleCatalog.AnalogOutputs, row.ModuleName)),
            ("PORT", row.ModulePort?.ToString(CultureInfo.InvariantCulture) ?? ""));
        var texts = FormatInfoLines(
            ("NAME ENG", row.NameEnglish),
            ("NAME OTHER", row.NameOther));

        var aoText = string.Join(Environment.NewLine, new[]
        {
            $"AO NUMBER - {row.AoNumber.ToString(CultureInfo.InvariantCulture)}",
            row.AoSystemName,
            row.AoPlcName,
            row.NameEnglish
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var dialog = new DbAoInfoDialog(aoText, leftItems, mapping, texts)
        {
            Owner = EditorOwner
        };
        var crossReferenceSpecs = BuildDbAoCrossReferenceSpecs(row);
        var crossReferencePanel = CreateCrossReferencePanel(crossReferenceSpecs);
        dialog.AddCrossReferenceTab(crossReferencePanel);
        dialog.Loaded += async (_, _) => await LoadTiaCrossReferencePanelAsync(crossReferencePanel, crossReferenceSpecs);
        dialog.ShowDialog();
    }

    private void DbDoInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var columns = new List<SummaryInfoColumn>
        {
            new("MAPPING", FormatInfoLines(
                ("USED", ToYesNo(IsEnabledNumber(row.TrueText))),
                ("MODULE", FormatIndexedModuleName(_project.ModuleCatalog.DigitalOutputs, row.ModuleName)),
                ("PORT", row.ModulePort?.ToString(CultureInfo.InvariantCulture) ?? ""))),
            new("TEXTS", FormatInfoLines(
                ("STATUS SYSTEM ENG", row.SystemNameEnglish),
                ("STATUS NAME ENG", row.NameEnglish),
                ("STATUS SYSTEM OTHER", row.SystemNameOther),
                ("STATUS NAME OTHER", row.NameOther)))
        };

        var crossReferenceSpecs = BuildDbStringCrossReferenceSpecs("DO", row, "ID", "VAL");
        ShowSummaryDialog("DB DO info", FormatRowHeader(row.Id, row.PlcStructure, row.PlcName), columns, crossReferenceSpecs);
    }

    private void DbAlmInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var columns = new List<SummaryInfoColumn>
        {
            new("SOURCE OF ALARM", FormatAlarmSource(row)),
            new("SETTINGS", FormatInfoLines(
                ("USED", DisplayValidation(DbValidationDefaults.DbParInUse, row.Used)),
                ("ALM CONTROL", DisplayValidation(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl)),
                ("SR ALM", DisplayValidation(DbValidationDefaults.DbAlmYesNo, row.SrAlarm)),
                ("ALM CLASS", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass)),
                ("REACT TO UNIT", DisplayValidation(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit)),
                ("SPARE", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.Spare)),
                ("BLOCKING CLASS 1", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1)),
                ("BLOCKING CLASS 2", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2)),
                ("DELAY", row.Delay),
                ("TIME BASE", DisplayValidation(DbValidationDefaults.DbAlmTimeBase, row.TimeBase)))),
            new("TEXTS", FormatInfoLines(
                ("ALARM SYSTEM ENG", row.SystemNameEnglish),
                ("ALARM TEXT ENG", row.NameEnglish),
                ("ALARM SYSTEM OTHER", row.SystemNameOther),
                ("ALARM TEXT OTHER", row.NameOther))),
            new("DESCRIPTION ENG", FormatDescriptionInfo(row, "ALM", "en-US")),
            new("DESCRIPTION OTHER", FormatDescriptionInfo(row, "ALM", "Other"))
        };

        var crossReferenceSpecs = BuildDbStringCrossReferenceSpecsWithoutData("ALM", row, "ALM_ID", "TEMP", "ALM");
        ShowSummaryDialog("DB ALM info", FormatRowHeader(row.Id, row.PlcStructure, row.PlcName), columns, crossReferenceSpecs);
    }

    private void DbParInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var columns = new List<SummaryInfoColumn>
        {
            new("SETTINGS", FormatParameterSettings(row)),
            new("NAMES", FormatInfoLines(
                ("PARAMETER STRUCT", row.PlcStructure),
                ("PARAMETER PLC NAME", row.PlcName),
                ("PARAMETER NAME ENG", row.NameEnglish),
                ("PARAMETER NAME OTHER", row.NameOther))),
            new("DESCRIPTION ENG", FormatDescriptionInfo(row, "PAR", "en-US")),
            new("DESCRIPTION OTHER", FormatDescriptionInfo(row, "PAR", "Other"))
        };

        var crossReferenceSpecs = BuildDbStringCrossReferenceSpecsWithoutData("PAR", row, "ID", "R_VAL", "I_VAL", "T_VAL");
        ShowSummaryDialog("DB PAR info", FormatRowHeader(row.Id, row.PlcStructure, row.PlcName), columns, crossReferenceSpecs);
    }

    private void ShowSummaryDialog(string title, string header, IReadOnlyList<SummaryInfoColumn> columns, IReadOnlyList<TiaCrossReferenceSpec>? crossReferenceSpecs = null)
    {
        var dialog = new SummaryInfoDialog(title, header, columns)
        {
            Owner = EditorOwner
        };
        if (crossReferenceSpecs is { Count: > 0 })
        {
            var crossReferencePanel = CreateCrossReferencePanel(crossReferenceSpecs);
            dialog.AddCrossReferenceTab(crossReferencePanel);
            dialog.Loaded += async (_, _) => await LoadTiaCrossReferencePanelAsync(crossReferencePanel, crossReferenceSpecs);
        }

        dialog.ShowDialog();
    }

    private string FormatDescriptionInfo(DbStringRow row, string scope, string languageCode)
    {
        var template = FindDescriptionTemplate(row, scope);
        if (template is null)
        {
            return ValueOrNotConfigured(languageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                ? row.DescriptionEnglish
                : row.DescriptionOther);
        }

        var resolved = _descriptionTemplateService.ResolveDescription(row, _project.DescriptionTemplates, scope, languageCode);
        return string.Join(Environment.NewLine, new[]
        {
            "TEMPLATE",
            ValueOrNotConfigured(resolved)
        });
    }

    private DescriptionTemplateEntry? FindDescriptionTemplate(DbStringRow row, string scope)
    {
        return _project.DescriptionTemplates.ForScope(scope)
            .FirstOrDefault(template =>
                TextEquals(row.DescriptionTemplateKey, template.Key)
                || TextEquals(row.DescriptionEnglish, template.Key)
                || TextEquals(row.DescriptionOther, template.Key)
                || TextEquals(row.DescriptionEnglish, template.TextEnglish)
                || TextEquals(row.DescriptionOther, template.TextOther));
    }

    private static bool TextEquals(string current, string expected)
    {
        return !string.IsNullOrWhiteSpace(current)
               && !string.IsNullOrWhiteSpace(expected)
               && string.Join(" ", current.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                   .Equals(string.Join(" ", expected.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatRowHeader(int id, string plcStructure, string plcName)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"ID - {id.ToString(CultureInfo.InvariantCulture)}",
            $"{plcStructure}.{plcName}".TrimEnd('.')
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string FormatParameterSettings(DbStringRow row)
    {
        var type = DisplayValidation(DbValidationDefaults.DbParType, row.ParameterType);
        var typeKey = (type ?? "").Trim().ToUpperInvariant();
        (string Label, string Value) valueLine = typeKey switch
        {
            "REAL" => ("RVAL", row.RealValue ?? ""),
            "INT" => ("IVAL", row.IntegerValue ?? ""),
            "TIME" => ("TVAL", row.TimeValue ?? ""),
            _ => ("VALUE", "")
        };

        return FormatInfoLines(
            ("USED", DisplayValidation(DbValidationDefaults.DbParInUse, row.InUse)),
            ("TYPE", type ?? ""),
            valueLine,
            ("UNIT", DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit)),
            ("MIN VAL", row.MinValue ?? ""),
            ("MAX VAL", row.MaxValue ?? ""));
    }

    private static string FormatInfoLines(params (string Label, string Value)[] lines)
    {
        return string.Join(Environment.NewLine, lines.Select(line => $"{line.Label} - {ValueOrNotConfigured(line.Value)}"));
    }

    private static string ValueOrNotConfigured(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "NOT CONFIGURED" : value.Trim();
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string ToYesNo(bool value)
    {
        return value ? "YES" : "NOT";
    }

    private IEnumerable<MeasInfoItem> CreateParameterItems(params (string Label, string Id)[] parameters)
    {
        foreach (var parameter in parameters)
        {
            var row = GetParameterRow(parameter.Id);
            if (row is null)
            {
                yield return new MeasInfoItem(parameter.Label, "NOT CONFIGURED");
                continue;
            }

            var text = FormatParameterReference(parameter.Id);
            yield return new MeasInfoItem(parameter.Label, string.IsNullOrWhiteSpace(text) ? "NOT CONFIGURED" : text, CreateParameterTableRows(parameter.Id, row));
        }
    }

    private List<MeasInfoItem> CreateAlarmItems(params (string Label, string Id)[] alarms)
    {
        var result = new List<MeasInfoItem>();
        foreach (var alarm in alarms)
        {
            var row = GetAlarmRow(alarm.Id);
            if (row is null)
            {
                result.Add(new MeasInfoItem(alarm.Label, "NOT CONFIGURED"));
                continue;
            }

            var text = FormatAlarmReference(alarm.Id);
            result.Add(new MeasInfoItem(alarm.Label, string.IsNullOrWhiteSpace(text) ? "NOT CONFIGURED" : text, CreateAlarmReferenceTableRows(alarm.Id, row)));
        }

        return result;
    }

    private IReadOnlyList<InfoTableRow> CreateParameterTableRows(string parameterId, DbStringRow row)
    {
        var type = DisplayValidation(DbValidationDefaults.DbParType, row.ParameterType);
        var value = GetParameterValue(row);
        var unit = DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit);
        return
        [
            InfoRow(("PLC", $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'))),
            InfoRow(("TEXT", row.NameEnglish)),
            InfoRow(("ID", parameterId), ("TYPE", type), ("VALUE", value), ("UNIT", unit))
        ];
    }

    private IReadOnlyList<InfoTableRow> CreateAlarmReferenceTableRows(string alarmId, DbStringRow row)
    {
        return
        [
            InfoRow(("PLC", $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'))),
            InfoRow(("ALARM TEXT", JoinNonEmpty(" - ", row.SystemNameEnglish, row.NameEnglish))),
            InfoRow(("ID", alarmId), ("USED", DisplayValidation(DbValidationDefaults.DbParInUse, row.Used)), ("CTRL", DisplayValidation(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl)), ("SR", DisplayValidation(DbValidationDefaults.DbAlmYesNo, row.SrAlarm)), ("CLASS", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass)), ("REACT", DisplayValidation(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit)), ("SPARE", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.Spare)), ("DELAY", row.Delay), ("BASE", DisplayValidation(DbValidationDefaults.DbAlmTimeBase, row.TimeBase))),
            InfoRow(("BLOCKING CLASS 1", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1)), ("BLOCKING CLASS 2", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2)))
        ];
    }

    private IReadOnlyList<InfoTableRow> CreateAlarmSettingsTableRows(DbStringRow row)
    {
        return
        [
            InfoRow(("USED", DisplayValidation(DbValidationDefaults.DbParInUse, row.Used)), ("CTRL", DisplayValidation(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl)), ("SR", DisplayValidation(DbValidationDefaults.DbAlmYesNo, row.SrAlarm)), ("CLASS", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass)), ("REACT", DisplayValidation(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit)), ("SPARE", DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.Spare)), ("DELAY", row.Delay), ("BASE", DisplayValidation(DbValidationDefaults.DbAlmTimeBase, row.TimeBase))),
            InfoRow(("BLOCKING CLASS 1", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1)), ("BLOCKING CLASS 2", DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2)))
        ];
    }

    private static InfoTableRow InfoRow(params (string Label, string Value)[] cells)
    {
        return new InfoTableRow(cells.Select(cell => new InfoTableCell(cell.Label, cell.Value)).ToList());
    }

    private string FormatParameterReference(string parameterId)
    {
        var row = GetParameterRow(parameterId);
        if (row is null)
        {
            return "";
        }

        var type = DisplayValidation(DbValidationDefaults.DbParType, row.ParameterType);
        var value = GetParameterValue(row);
        var unit = DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit);
        var valueWithUnit = JoinNonEmpty(" ", value, unit);

        return string.Join(Environment.NewLine, new[]
        {
            JoinNonEmpty(" | ",
                $"ID - {parameterId}",
                string.IsNullOrWhiteSpace(type) ? "" : type,
                string.IsNullOrWhiteSpace(valueWithUnit) ? "" : valueWithUnit),
            $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'),
            row.NameEnglish
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string GetParameterValue(DbStringRow row)
    {
        return (row.ParameterType ?? "").Trim() switch
        {
            "1" => row.IntegerValue ?? "",
            "2" => row.TimeValue ?? "",
            _ => row.RealValue ?? ""
        };
    }

    private string FormatAlarmReference(string alarmId)
    {
        var row = GetAlarmRow(alarmId);
        if (row is null)
        {
            return "";
        }

        return string.Join(Environment.NewLine, new[]
        {
            $"ID - {alarmId}",
            $"{row.PlcStructure}.{row.PlcName}".TrimEnd('.'),
            JoinNonEmpty(" - ", row.SystemNameEnglish, row.NameEnglish),
            FormatAlarmSettingsCompact(row)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string FormatAlarmSettingsCompact(DbStringRow row)
    {
        return string.Join(Environment.NewLine, new[]
        {
            JoinNonEmpty(" | ",
                "USED " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbParInUse, row.Used)),
                "CTRL " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl)),
                "SR " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmYesNo, row.SrAlarm))),
            JoinNonEmpty(" | ",
                "CLASS " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass)),
                "REACT " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit)),
                "SPARE " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.Spare))),
            JoinNonEmpty(" | ",
                "BLOCK 1 " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1)),
                "BLOCK 2 " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2))),
            JoinNonEmpty(" | ",
                "DELAY " + ValueOrNotConfigured(row.Delay),
                "BASE " + ValueOrNotConfigured(DisplayValidation(DbValidationDefaults.DbAlmTimeBase, row.TimeBase)))
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatReference(string id, string plcStructure, string plcName, string description)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"ID - {id}",
            $"{plcStructure}.{plcName}".TrimEnd('.'),
            description
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private DbStringRow? GetParameterRow(string parameterId)
    {
        return int.TryParse((parameterId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? _project.DbCatalog.ParameterRows.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private DbStringRow? GetAlarmRow(string alarmId)
    {
        return int.TryParse((alarmId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? _project.DbCatalog.AlarmRows.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void DbGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        try
        {
            if (e.LeftButton != MouseButtonState.Pressed
                || sender is not DataGrid grid
                || !ReferenceEquals(grid, _fillGrid)
                || _fillSourceRow is null
                || _fillColumn is null
                || _fillProperty is null
                || _fillValue is null)
            {
                return;
            }

            var currentPoint = e.GetPosition(grid);
            if (!_fillDragging)
            {
                var movedEnough = Math.Abs(currentPoint.X - _fillStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance
                                  || Math.Abs(currentPoint.Y - _fillStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;
                if (!movedEnough)
                {
                    return;
                }

                _fillDragging = true;
            }

            var cell = FindAutoFillCellFromEvent(grid, e.OriginalSource);
            if (cell?.DataContext is null)
            {
                return;
            }

            if (!ReferenceEquals(cell.Column, _fillColumn)
                || !string.Equals(GetDbColumnProperty(cell.Column), _fillProperty, StringComparison.Ordinal))
            {
                return;
            }

            _fillTargetRow = cell.DataContext;
            UpdateDragFillAdornerForCurrentTarget(grid, cell);
            e.Handled = true;
        }
        catch
        {
            ResetAutoFillState();
        }
    }

    /// <summary>
    /// Draws / updates the dashed preview rectangle that spans from the source
    /// row to the target row during a drag-fill. Mirrors Excel's outline so the
    /// user can see exactly which rows will be overwritten before mouse-up.
    /// </summary>
    private void UpdateDragFillAdornerForCurrentTarget(DataGrid grid, DataGridCell targetCell)
    {
        if (_fillSourceRow is null || _fillColumn is null)
        {
            return;
        }

        var sourceCell = TryGetCellByRowAndColumn(grid, _fillSourceRow, _fillColumn);
        if (sourceCell is null)
        {
            return;
        }

        EnsureDragFillAdorner(grid);
        if (_dragFillAdorner is null)
        {
            return;
        }

        try
        {
            var sourceBounds = TransformBounds(sourceCell, _dragFillAdorner);
            var targetBounds = TransformBounds(targetCell, _dragFillAdorner);
            if (sourceBounds is null || targetBounds is null)
            {
                return;
            }

            _dragFillAdorner.UpdateBounds(Rect.Union(sourceBounds.Value, targetBounds.Value));
        }
        catch
        {
            // Adorner geometry can transiently fail during virtualization; skip
            // this tick rather than aborting the whole drag.
        }
    }

    private static Rect? TransformBounds(FrameworkElement element, UIElement reference)
    {
        try
        {
            var transform = element.TransformToVisual(reference);
            var rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            return transform.TransformBounds(rect);
        }
        catch
        {
            return null;
        }
    }

    private static DataGridCell? TryGetCellByRowAndColumn(DataGrid grid, object item, DataGridColumn column)
    {
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
        {
            return null;
        }

        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            return null;
        }

        return presenter.ItemContainerGenerator.ContainerFromIndex(column.DisplayIndex) as DataGridCell;
    }

    private void EnsureDragFillAdorner(DataGrid grid)
    {
        if (_dragFillAdorner is not null && ReferenceEquals(_dragFillAdorner.AdornedElement, grid))
        {
            return;
        }

        DisposeDragFillAdorner();

        _dragFillAdornerLayer = AdornerLayer.GetAdornerLayer(grid);
        if (_dragFillAdornerLayer is null)
        {
            return;
        }

        _dragFillAdorner = new DragFillAdorner(grid);
        _dragFillAdornerLayer.Add(_dragFillAdorner);
    }

    private void DisposeDragFillAdorner()
    {
        if (_dragFillAdorner is not null && _dragFillAdornerLayer is not null)
        {
            try { _dragFillAdornerLayer.Remove(_dragFillAdorner); } catch { /* layer may be gone */ }
        }
        _dragFillAdorner = null;
        _dragFillAdornerLayer = null;
    }

    private void DbGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var grid = _fillGrid;
            var sourceRow = _fillSourceRow;
            var targetRow = _fillTargetRow;
            var column = _fillColumn;
            var value = _fillValue;
            var templateKey = _fillTemplateKey;
            if (grid is not null && targetRow is null)
            {
                targetRow = FindAutoFillCellFromEvent(grid, e.OriginalSource)?.DataContext;
            }

            var shouldFinalizeFill = _fillDragging
                                     && grid is not null
                                     && sourceRow is not null
                                     && targetRow is not null
                                     && column is not null
                                     && value is not null;

            ResetAutoFillState();

            if (shouldFinalizeFill)
            {
                FillDbGridRows(grid!, sourceRow!, targetRow!, column!, value!, templateKey);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            ResetAutoFillState();
            ShowError("Auto-fill failed: " + ex.Message);
        }
    }

    private void DbGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        try
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.C)
            {
                CopyDbGridSelectionToClipboard(grid);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.X)
            {
                CutDbGridSelectionToClipboard(grid);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
            {
                PasteDbGridClipboard(grid);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Delete or Key.Back)
            {
                ClearDbGridSelection(grid);
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.D)
            {
                FillDbGridSelectionDown(grid);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _clipboardSourceGrid is not null)
            {
                ClearClipboardMarchingAnts();
                ShowMessage("Copy cleared.");
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            ShowError("Spreadsheet operation failed: " + ex.Message);
        }
    }

    private void CopyDbGridSelectionToClipboard(DataGrid grid)
    {
        // Commit any in-flight edit so we read the persisted model value, not
        // the half-typed text inside an editing TextBox.
        CommitActiveEdits(grid);

        var selectedCells = grid.SelectedCells
            .Where(cell => !ReferenceEquals(cell.Item, CollectionView.NewItemPlaceholder))
            .ToList();
        if (selectedCells.Count == 0)
        {
            return;
        }

        var rows = GetDbGridVisibleRows(grid);
        var columns = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToList();
        var selected = selectedCells
            .Select(cell => new
            {
                RowIndex = rows.FindIndex(row => ReferenceEquals(row, cell.Item)),
                ColumnIndex = columns.IndexOf(cell.Column),
                Value = GetDbGridCellText(cell.Item, cell.Column),
                TemplateKey = GetDescriptionTemplateKey(cell.Item, cell.Column)
            })
            .Where(cell => cell.RowIndex >= 0 && cell.ColumnIndex >= 0)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var minRow = selected.Min(cell => cell.RowIndex);
        var maxRow = selected.Max(cell => cell.RowIndex);
        var minColumn = selected.Min(cell => cell.ColumnIndex);
        var maxColumn = selected.Max(cell => cell.ColumnIndex);
        var lookup = selected.ToDictionary(cell => (cell.RowIndex, cell.ColumnIndex), cell => cell.Value);
        _templateClipboardCells = selected
            .Where(cell => !string.IsNullOrWhiteSpace(cell.TemplateKey))
            .Select(cell => new TemplateClipboardCell(cell.RowIndex - minRow, cell.ColumnIndex - minColumn, cell.TemplateKey!))
            .ToList();
        if (_templateClipboardCells.Count == 0)
        {
            _templateClipboardCells = null;
        }

        var lines = new List<string>();

        for (var row = minRow; row <= maxRow; row++)
        {
            var values = new List<string>();
            for (var column = minColumn; column <= maxColumn; column++)
            {
                lookup.TryGetValue((row, column), out var value);
                values.Add(value ?? "");
            }

            lines.Add(string.Join('\t', values));
        }

        var clipboardText = string.Join(Environment.NewLine, lines);
        Clipboard.SetText(clipboardText);
        _lastInternalClipboardText = clipboardText;

        // Marching-ants Excel-style copy mode: remember which cells the user copied
        // so they appear with the animated dashed border until paste/edit/Esc.
        var anchors = selectedCells
            .Select(cell => (Item: cell.Item, Property: GetDbColumnProperty(cell.Column)))
            .Where(pair => pair.Property is not null)
            .Select(pair => (pair.Item, pair.Property!))
            .ToList();
        MarkClipboardSelection(grid, anchors);

        var cellCount = selected.Count;
        var rowCount = maxRow - minRow + 1;
        var colCount = maxColumn - minColumn + 1;
        ShowMessage(cellCount == 1
            ? "Copied 1 cell."
            : $"Copied {rowCount} row(s) × {colCount} col(s) ({cellCount} cells).");
    }

    private void CutDbGridSelectionToClipboard(DataGrid grid)
    {
        CopyDbGridSelectionToClipboard(grid);
        ClearDbGridSelection(grid);
    }

    private void PasteDbGridClipboard(DataGrid grid)
    {
        if (!Clipboard.ContainsText())
        {
            return;
        }

        var startCell = grid.SelectedCells.FirstOrDefault();
        if (!IsValidGridCell(startCell))
        {
            return;
        }

        // Commit any in-flight edit before we trigger Refresh() in
        // RefreshAfterDbGridExcelEdit — otherwise WPF throws
        // "Refresh is not allowed during an AddNew or EditItem transaction".
        CommitActiveEdits(grid);

        var rows = GetDbGridVisibleRows(grid);
        var columns = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToList();
        var startRowIndex = rows.FindIndex(row => ReferenceEquals(row, startCell.Item));
        var startColumnIndex = columns.IndexOf(startCell.Column);
        if (startRowIndex < 0 || startColumnIndex < 0)
        {
            return;
        }

        var clipboardText = Clipboard.GetText();

        // If the clipboard no longer matches what we last wrote, the user
        // copied something elsewhere — drop our description-template metadata
        // so we don't silently apply old template keys to fresh paste content.
        if (!string.Equals(clipboardText, _lastInternalClipboardText, StringComparison.Ordinal))
        {
            _templateClipboardCells = null;
        }

        var pasteRows = clipboardText
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.None)
            .Where((line, index) => index == 0 || line.Length > 0)
            .Select(line => line.Split('\t'))
            .ToList();

        // Pasting multiple rows into a sorted view writes by visible order,
        // which can confuse the user who expects "model order". Warn once
        // and let them cancel — same convention as Excel's sort+paste prompt.
        if (pasteRows.Count > 1 && IsGridViewSorted(grid))
        {
            var proceed = MessageBox.Show(this,
                "The current view is sorted. Pasting will write rows in the order they are currently visible, which may not match the original row order.\n\nContinue?",
                "Paste into sorted view",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.OK)
            {
                return;
            }
        }

        var rejections = new List<string>();

        if (pasteRows.Count == 1 && pasteRows[0].Length == 1)
        {
            var selectedCells = grid.SelectedCells.Where(IsValidGridCell).ToList();
            if (selectedCells.Count > 1)
            {
                var fillValueRaw = pasteRows[0][0];
                var fillTemplateKey = _templateClipboardCells?.Count == 1 ? _templateClipboardCells[0].TemplateKey : null;
                var fillChanged = false;
                RecordUndoSnapshot();
                foreach (var cell in selectedCells)
                {
                    if (!IsEditableSpreadsheetColumn(grid, cell.Column))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(fillTemplateKey))
                    {
                        fillChanged |= TrySetDbGridCellTemplateValue(cell.Item, cell.Column, fillTemplateKey);
                        continue;
                    }

                    var coerced = CoercePastedValue(grid, cell.Column, cell.Item, fillValueRaw, out var rejection);
                    if (coerced is null)
                    {
                        if (rejection is not null) rejections.Add(rejection);
                        continue;
                    }
                    fillChanged |= TrySetDbGridCellValue(cell.Item, cell.Column, coerced);
                }

                if (fillChanged)
                {
                    SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
                }

                ReportPasteRejections(rejections);
                ClearClipboardMarchingAnts();
                return;
            }
        }

        var changed = false;
        RecordUndoSnapshot();
        for (var rowOffset = 0; rowOffset < pasteRows.Count && startRowIndex + rowOffset < rows.Count; rowOffset++)
        {
            var pasteColumns = pasteRows[rowOffset];
            for (var columnOffset = 0; columnOffset < pasteColumns.Length && startColumnIndex + columnOffset < columns.Count; columnOffset++)
            {
                var targetColumn = columns[startColumnIndex + columnOffset];
                if (!IsEditableSpreadsheetColumn(grid, targetColumn))
                {
                    continue;
                }

                var targetRow = rows[startRowIndex + rowOffset];
                var templateKey = GetTemplateClipboardKey(rowOffset, columnOffset);
                if (!string.IsNullOrWhiteSpace(templateKey))
                {
                    changed |= TrySetDbGridCellTemplateValue(targetRow, targetColumn, templateKey);
                    continue;
                }

                var coerced = CoercePastedValue(grid, targetColumn, targetRow, pasteColumns[columnOffset], out var rejection);
                if (coerced is null)
                {
                    if (rejection is not null) rejections.Add(rejection);
                    continue;
                }
                changed |= TrySetDbGridCellValue(targetRow, targetColumn, coerced);
            }
        }

        if (changed)
        {
            SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
            var pastedRows = Math.Min(pasteRows.Count, rows.Count - startRowIndex);
            var pastedCols = pasteRows.Max(r => r.Length);
            ShowMessage(pastedRows == 1 && pastedCols == 1
                ? "Pasted 1 cell."
                : $"Pasted {pastedRows} row(s) × {pastedCols} col(s).");
        }

        ReportPasteRejections(rejections);

        // Excel pastes once and then clears the "ants". Match that behavior.
        ClearClipboardMarchingAnts();
    }

    /// <summary>
    /// Surfaces paste-time validation failures to the user without crashing the
    /// flow. The first three reasons are shown verbatim; the rest are summarized.
    /// </summary>
    private void ReportPasteRejections(IReadOnlyList<string> rejections)
    {
        if (rejections.Count == 0)
        {
            return;
        }

        var preview = string.Join(Environment.NewLine, rejections.Take(3).Distinct());
        var extra = rejections.Count > 3 ? $"{Environment.NewLine}… and {rejections.Count - 3} more." : "";
        MessageBox.Show(this,
            $"{rejections.Count} cell(s) were skipped during paste:{Environment.NewLine}{preview}{extra}",
            "Paste skipped some cells",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ClearDbGridSelection(DataGrid grid)
    {
        CommitActiveEdits(grid);

        var changed = false;
        RecordUndoSnapshot();
        foreach (var cell in grid.SelectedCells.Where(IsValidGridCell))
        {
            if (!IsEditableSpreadsheetColumn(grid, cell.Column))
            {
                continue;
            }

            changed |= TrySetDbGridCellValue(cell.Item, cell.Column, "");
        }

        if (changed)
        {
            SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
            var cleared = grid.SelectedCells.Count(IsValidGridCell);
            ShowMessage(cleared == 1 ? "Cleared 1 cell." : $"Cleared {cleared} cells.");
        }
    }

    private void FillDbGridSelectionDown(DataGrid grid)
    {
        CommitActiveEdits(grid);

        var selectedCells = grid.SelectedCells.Where(IsValidGridCell).ToList();
        if (selectedCells.Count < 2)
        {
            return;
        }

        var rows = GetDbGridVisibleRows(grid);
        var columns = grid.Columns.Where(column => column.Visibility == Visibility.Visible).OrderBy(column => column.DisplayIndex).ToList();
        var selectedByColumn = selectedCells.GroupBy(cell => columns.IndexOf(cell.Column)).Where(group => group.Key >= 0);
        var changed = false;
        RecordUndoSnapshot();

        foreach (var columnGroup in selectedByColumn)
        {
            var ordered = columnGroup
                .Select(cell => new { Cell = cell, RowIndex = rows.FindIndex(row => ReferenceEquals(row, cell.Item)) })
                .Where(item => item.RowIndex >= 0)
                .OrderBy(item => item.RowIndex)
                .ToList();
            if (ordered.Count < 2 || !IsEditableSpreadsheetColumn(grid, ordered[0].Cell.Column))
            {
                continue;
            }

            var sourceValue = GetDbGridCellText(ordered[0].Cell.Item, ordered[0].Cell.Column);
            var sourceTemplateKey = GetDescriptionTemplateKey(ordered[0].Cell.Item, ordered[0].Cell.Column);
            foreach (var target in ordered.Skip(1))
            {
                changed |= !string.IsNullOrWhiteSpace(sourceTemplateKey)
                    ? TrySetDbGridCellTemplateValue(target.Cell.Item, target.Cell.Column, sourceTemplateKey)
                    : TrySetDbGridCellValue(target.Cell.Item, target.Cell.Column, sourceValue);
            }
        }

        if (changed)
        {
            SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
            ShowMessage("Filled down.");
        }
    }

    private static bool IsValidGridCell(DataGridCellInfo cell)
    {
        return cell.IsValid
               && cell.Item is not null
               && !ReferenceEquals(cell.Item, CollectionView.NewItemPlaceholder)
               && GetDbColumnProperty(cell.Column) is not null;
    }

    private List<object> GetDbGridVisibleRows(DataGrid grid)
    {
        return grid.Items
            .Cast<object>()
            .Where(item => !ReferenceEquals(item, CollectionView.NewItemPlaceholder))
            .ToList();
    }

    private static string GetDbGridCellText(object row, DataGridColumn column)
    {
        var propertyName = GetDbColumnProperty(column);
        if (propertyName is null)
        {
            return "";
        }

        return GetObjectStringValue(row, propertyName);
    }

    private string? GetTemplateClipboardKey(int rowOffset, int columnOffset)
    {
        return _templateClipboardCells?
            .FirstOrDefault(cell => cell.RowOffset == rowOffset && cell.ColumnOffset == columnOffset)
            ?.TemplateKey;
    }

    private static string? GetDescriptionTemplateKey(object row, DataGridColumn column)
    {
        var propertyName = GetDbColumnProperty(column);
        if (row is not DbStringRow stringRow
            || propertyName is not (nameof(DbStringRow.DescriptionEnglish) or nameof(DbStringRow.DescriptionOther))
            || string.IsNullOrWhiteSpace(stringRow.DescriptionTemplateKey))
        {
            return null;
        }

        return stringRow.DescriptionTemplateKey;
    }

    private static string GetObjectStringValue(object row, string propertyName)
    {
        return row.GetType().GetProperty(propertyName)?.GetValue(row)?.ToString() ?? "";
    }

    private bool TrySetDbGridCellValue(object row, DataGridColumn column, string value)
    {
        var propertyName = GetDbColumnProperty(column);
        if (propertyName is null)
        {
            return false;
        }

        var property = row.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return false;
        }

        if (!TryConvertDbGridCellValue(value, property.PropertyType, out var converted))
        {
            return false;
        }

        var current = property.GetValue(row);
        if (Equals(current, converted))
        {
            return false;
        }

        try
        {
            property.SetValue(row, converted);
        }
        catch
        {
            return false;
        }
        if (row is DbStringRow stringRow
            && propertyName is nameof(DbStringRow.DescriptionEnglish) or nameof(DbStringRow.DescriptionOther) or nameof(DbStringRow.DescriptionTemplateKey))
        {
            if (propertyName is nameof(DbStringRow.DescriptionEnglish) or nameof(DbStringRow.DescriptionOther))
            {
                stringRow.DescriptionTemplateKey = "";
            }

            stringRow.DescriptionDirty = true;
        }

        return true;
    }

    private bool TrySetDbGridCellTemplateValue(object row, DataGridColumn column, string templateKey)
    {
        var propertyName = GetDbColumnProperty(column);
        if (row is not DbStringRow stringRow
            || propertyName is not (nameof(DbStringRow.DescriptionEnglish) or nameof(DbStringRow.DescriptionOther))
            || string.IsNullOrWhiteSpace(templateKey))
        {
            return TrySetDbGridCellValue(row, column, templateKey);
        }

        var key = templateKey.Trim();
        var changed = !string.Equals(stringRow.DescriptionTemplateKey, key, StringComparison.Ordinal)
                      || !string.Equals(stringRow.DescriptionEnglish, key, StringComparison.Ordinal)
                      || !string.Equals(stringRow.DescriptionOther, key, StringComparison.Ordinal);
        if (!changed)
        {
            return false;
        }

        stringRow.DescriptionTemplateKey = key;
        stringRow.DescriptionEnglish = key;
        stringRow.DescriptionOther = key;
        stringRow.DescriptionDirty = false;
        return true;
    }

    private static bool TryConvertDbGridCellValue(string value, Type targetType, out object? converted)
    {
        converted = null;
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var type = nullableType ?? targetType;
        if (nullableType is not null && string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (type == typeof(string))
        {
            converted = value;
            return true;
        }

        if (type.IsEnum)
        {
            if (Enum.TryParse(type, value, ignoreCase: true, out var parsed))
            {
                converted = parsed;
                return true;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber))
            {
                return false;
            }

            converted = Enum.ToObject(type, enumNumber);
            return true;
        }

        if (type == typeof(int))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            converted = parsed;
            return true;
        }

        if (type == typeof(double))
        {
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            converted = parsed;
            return true;
        }

        if (type == typeof(bool))
        {
            converted = value.Equals("1", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        converted = value;
        return true;
    }

    private void RefreshAfterDbGridExcelEdit(DataGrid grid)
    {
        MarkActiveTabTiaStatusDirty();
        if (ReferenceEquals(grid, DbDiGrid))
        {
            UpdateDbDiFilterItems();
            _dbDiView?.Refresh();
            DbDiGrid.Items.Refresh();
            RecalculateAndRefreshSignalGrids();
            return;
        }

        if (ReferenceEquals(grid, DbDoGrid))
        {
            UpdateDbDoFilterItems();
            _dbDoView?.Refresh();
            DbDoGrid.Items.Refresh();
            RecalculateAndRefreshSignalGrids();
            return;
        }

        if (ReferenceEquals(grid, DbMeasGrid))
        {
            UpdateDbMeasFilterItems();
            _dbMeasView?.Refresh();
            DbMeasGrid.Items.Refresh();
            RecalculateAndRefreshSignalGrids();
            return;
        }

        if (ReferenceEquals(grid, DbAoGrid))
        {
            UpdateDbAoFilterItems();
            _dbAoRowsView?.Refresh();
            DbAoGrid.Items.Refresh();
            return;
        }

        if (ReferenceEquals(grid, DbAlmGrid))
        {
            ApplyDescriptionTemplatesAndRefresh();
            UpdateDbAlmFilterItems();
            _dbAlmView?.Refresh();
            DbAlmGrid.Items.Refresh();
            return;
        }

        if (ReferenceEquals(grid, DbParGrid))
        {
            ApplyDescriptionTemplatesAndRefresh();
            UpdateDbParFilterItems();
            _dbParView?.Refresh();
            DbParGrid.Items.Refresh();
            return;
        }

        if (ReferenceEquals(grid, SequenceControlStatusGrid))
        {
            UpdateSequenceReferenceOptions();
            UpdateSequenceControlStatusFilterItems();
            _sequenceControlStatusView?.Refresh();
            SequenceControlStatusGrid.Items.Refresh();
            return;
        }

        if (ReferenceEquals(grid, SequenceCommandGrid))
        {
            UpdateSequenceReferenceOptions();
            UpdateSequenceCommandFilterItems();
            _sequenceCommandView?.Refresh();
            SequenceCommandGrid.Items.Refresh();
            return;
        }

        if (IsScadaGrid(grid))
        {
            NormalizeScadaDatabase();
            if (ReferenceEquals(grid, ScadaTagsGrid)
                || ReferenceEquals(grid, ScadaLocationsGrid)
                || ReferenceEquals(grid, ScadaHeaderGrid)
                || ReferenceEquals(grid, ScadaMainNavGrid)
                || ReferenceEquals(grid, ScadaVariablesGrid)
                || ReferenceEquals(grid, ScadaNavigationGrid)
                || ReferenceEquals(grid, ScadaScreensGrid))
            {
                BindScadaViews();
            }
            else
            {
                RefreshScadaTagValues();
            }

            return;
        }

        if (IsTextListGrid(grid))
        {
            MarkTiaSectionState("DB SEQUENCE", "Differs", "Text lists changed.");
            grid.Items.Refresh();
            return;
        }

        grid.Items.Refresh();
        RecalculateAndRefreshSignalGrids();
    }

    private void DbDiGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbDiFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbDiGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbDiColumnsToViewport();
    }

    private void SignalGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            ResizeDataGridColumnsToViewport(grid);
        }
    }

    private void DbDoGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbDoFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbDoGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbDoColumnsToViewport();
    }

    private void DbMeasGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbMeasFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbMeasGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbMeasColumnsToViewport();
    }

    private void DbAoGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbAoFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbAoGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbAoColumnsToViewport();
    }

    private void DbAlmGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbAlmFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbAlmGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbAlmColumnsToViewport();
    }

    private void DbParGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            DbParFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void DbParGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDbParColumnsToViewport();
    }

    private void SequenceControlStatusGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            SequenceControlStatusFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void SequenceControlStatusGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeSequenceControlStatusColumnsToViewport();
    }

    private void SequenceCommandGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > 0)
        {
            SequenceCommandFilterScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        }
    }

    private void SequenceCommandGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeSequenceCommandColumnsToViewport();
    }

    private void ExportSummary_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        var dialog = new SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{SanitizeFileName(_project.ProjectName)}_summary.txt"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, ProjectSummaryWriter.Write(_project));
            ShowMessage($"Project summary exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void GenerateFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen || IsBusy)
        {
            if (!_projectOpen)
            {
                ShowMessage("No project is open.");
            }

            return;
        }

        SyncPathFieldsToProject();
        _calculator.Recalculate(_project);
        _validationStateUpdater.Update(_project);
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();

        // Generate preflight uses default options — xref checks stay off so the
        // confirmation dialog never blocks the user behind a multi-minute scan.
        // If the user wants xref-aware preflight, they can run Validate (F5)
        // with the checkbox first, then click Generate.
        var preflightIssues = await RunBusyAsync(
            "Checking project before generation...",
            () => _validator.Validate(_project, Services.Validation.ValidationOptions.Default).ToList());
        PopulateProblemsPane(preflightIssues);
        if (!ConfirmGenerateWithIssues(preflightIssues))
        {
            return;
        }

        EnsureGenerateOutputFolder();
        var dialog = new GenerateFilesDialog(_project.Paths.OutputFolder)
        {
            Owner = EditorOwner
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _project.Paths.OutputFolder = dialog.OutputFolder;
        EnsureGenerateOutputFolder();
        try
        {
            var result = await RunBusyAsync(
                "Generating DB source files...",
                () => _generator.GenerateSelected(_project, dialog.Selection));

            if (result.CreatedFiles.Count == 0 && result.Issues.Count == 0)
            {
                ShowMessage("No files generated. Select at least one item in the Generate dialog.");
                return;
            }

            var summary = new List<string>();
            if (result.CreatedFiles.Count > 0)
            {
                summary.Add($"Generated {result.CreatedFiles.Count} file(s).");
            }
            if (result.Issues.Count > 0)
            {
                var errorCount = result.Issues.Count(issue => issue.Severity == "ERROR");
                var warningCount = result.Issues.Count - errorCount;
                summary.Add($"{errorCount} error(s), {warningCount} warning(s) during generation.");
            }

            ShowMessage(string.Join(" ", summary));

            if (result.Issues.Count > 0)
            {
                // The Problems panel is built for browsing issues (filtering,
                // double-click navigation) — no point dumping them into a popup.
                PopulateProblemsPane(result.Issues.ToList());
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, summary)
                        + Environment.NewLine + Environment.NewLine
                        + "The issues are listed in the Problems panel at the bottom of the window.",
                    "Generation completed with issues",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool ConfirmGenerateWithIssues(IReadOnlyList<ProjectIssue> issues)
    {
        if (issues.Count == 0)
        {
            return true;
        }

        var errorCount = issues.Count(issue => issue.Severity == "ERROR");
        var warningCount = issues.Count - errorCount;

        string header;
        MessageBoxImage icon;
        if (errorCount > 0 && warningCount > 0)
        {
            header = $"{errorCount} error(s) and {warningCount} warning(s) found.";
            icon = MessageBoxImage.Error;
        }
        else if (errorCount > 0)
        {
            header = $"{errorCount} error(s) found.";
            icon = MessageBoxImage.Error;
        }
        else
        {
            header = $"{warningCount} warning(s) found.";
            icon = MessageBoxImage.Warning;
        }

        var preview = issues.Take(10).Select(issue => "  • " + issue.ToString());
        var more = issues.Count > 10 ? $"{Environment.NewLine}  ... and {issues.Count - 10} more" : "";

        var result = MessageBox.Show(
            this,
            header + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, preview) + more + Environment.NewLine + Environment.NewLine
                + (errorCount > 0
                    ? "Generation may produce incomplete or invalid output. Proceed anyway?"
                    : "Warnings indicate fields still labelled as 'spare' or missing translations. Proceed?"),
            errorCount > 0 ? "Generate despite errors?" : "Generate despite warnings?",
            MessageBoxButton.OKCancel,
            icon,
            MessageBoxResult.Cancel);

        return result == MessageBoxResult.OK;
    }

    private void SaveProject()
    {
        if (IsBusy)
        {
            return;
        }

        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SyncPathFieldsToProject();
        _calculator.Recalculate(_project);
        _validationStateUpdater.Update(_project);
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();

        if (_projectPath is null)
        {
            SaveProjectAs();
            return;
        }

        try
        {
            _project.ProjectFilePath = _projectPath;
            _projectFileService.Save(_projectPath, _project);
            MarkProjectClean();
            DeleteAutosave();
            _recentProjectsService.Add(_projectPath);
            ShowMessage($"Saved project: {_projectPath}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveProjectAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Plant Configurator project (*.plantcfg)|*.plantcfg|JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "plantcfg"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _projectPath = dialog.FileName;
        _project.ProjectFilePath = _projectPath;
        SaveProject();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            return;
        }

        if (!ConfirmSavePendingChanges())
        {
            e.Cancel = true;
            return;
        }

        _closingConfirmed = true;
        SaveUiState();
        // Clean exit: the user either saved or explicitly declined; either way
        // the autosave copy must not resurface as a recovery prompt next run.
        DeleteAutosave();
        CancelPendingTiaStatusCheck();
        try
        {
            _unifiedJsConnectorRuntime.Dispose();
            _tiaSessionService.AbortHelper();
            _tiaSessionService.Dispose();
        }
        catch
        {
        }
    }

    private void AlmParExport_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        EnsureProjectDefaults();
        var dialog = new AlmParExportDialog(_project)
        {
            Owner = EditorOwner
        };
        dialog.ShowDialog();
        if (dialog.HasChanges)
        {
            SaveActivePlcDatabase();
            ShowMessage("ALM/PAR export settings updated.");
        }
    }

    private bool ConfirmSavePendingChanges()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "Projekt ima nespremljene promjene. Zelis li spremiti prije izlaza?",
            "Save changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            return true;
        }

        SaveProject();
        return !HasUnsavedChanges();
    }

    private bool HasUnsavedChanges()
    {
        if (!_projectOpen)
        {
            return false;
        }

        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();
        return !_projectFileService.ComputeSnapshotHash(_project).AsSpan().SequenceEqual(_savedSnapshotHash);
    }

    private void MarkProjectClean()
    {
        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();
        _savedSnapshotHash = _projectFileService.ComputeSnapshotHash(_project);
        _lastKnownDirty = false;
    }

    private async void ValidateProject()
    {
        if (!_projectOpen || IsBusy)
        {
            if (!_projectOpen)
            {
                ShowMessage("No project is open.");
            }

            return;
        }

        SyncPathFieldsToProject();
        _calculator.Recalculate(_project);
        _validationStateUpdater.Update(_project);
        SaveActivePlcDatabase();
        SaveActiveScadaDatabase();
        // Xref rules fire automatically when WriteCount data is present
        // (populated by manual "XRef update" button). Default options carry no
        // toggles that gate the rule set anymore.
        // The validator only reads the project, so it can run off the UI thread
        // while the busy overlay blocks further input.
        var issues = await RunBusyAsync(
            "Validating project...",
            () => _validator.Validate(_project, Services.Validation.ValidationOptions.Default).ToList());
        PopulateProblemsPane(issues);
        if (issues.Count == 0)
        {
            ShowMessage("Validation OK.");
            return;
        }

        var errorCount = issues.Count(issue => issue.Severity == "ERROR");
        var warningCount = issues.Count(issue => issue.Severity == "WARNING");
        ShowMessage(errorCount == 0
            ? $"Validation: {warningCount} warning(s) — generation allowed."
            : $"Validation: {errorCount} error(s), {warningCount} warning(s).");
    }

    private void BindProject()
    {
        ProjectWorkspace.Visibility = Visibility.Visible;
        ProjectActionBar.Visibility = Visibility.Visible;
        if (Resources["DescriptionTemplateTooltipConverter"] is DescriptionTemplateTooltipConverter templateTooltipConverter)
        {
            templateTooltipConverter.Catalog = _project.DescriptionTemplates;
        }
        SyncMultilingualFromProject();
        _bindingProject = true;
        ArrangeMainTabs();
        BindPlcDatabaseTree();
        BindScadaDatabaseTree();
        UpdateWorkspaceVisualState();
        ApplyConfiguredColorPalette();
        ApplyConfiguredConditionalColors();

        ConfigureDiGrid();
        ConfigureDoGrid();
        ConfigureAiGrid();
        ConfigureRtdGrid();
        ConfigureCounterGrid();
        ConfigureAoGrid();
        _diView = CollectionViewSource.GetDefaultView(_project.DigitalInputs);
        _diView.Filter = FilterDiRow;
        DiGrid.ItemsSource = _diView;

        _doView = CollectionViewSource.GetDefaultView(_project.DigitalOutputs);
        _doView.Filter = FilterDoRow;
        DoGrid.ItemsSource = _doView;
        _aiView = CollectionViewSource.GetDefaultView(_project.AnalogInputs);
        _aiView.Filter = FilterAiRow;
        AiGrid.ItemsSource = _aiView;
        _rtdView = CollectionViewSource.GetDefaultView(_project.RtdInputs);
        _rtdView.Filter = FilterRtdRow;
        RtdGrid.ItemsSource = _rtdView;
        _counterView = CollectionViewSource.GetDefaultView(_project.Counters);
        _counterView.Filter = FilterCounterRow;
        CounterGrid.ItemsSource = _counterView;
        _aoView = CollectionViewSource.GetDefaultView(_project.AnalogOutputs);
        _aoView.Filter = FilterAoRow;
        AoGrid.ItemsSource = _aoView;
        ModuleNamesDiGrid.ItemsSource = _project.ModuleCatalog.DigitalInputs;
        ModuleNamesDoGrid.ItemsSource = _project.ModuleCatalog.DigitalOutputs;
        ModuleNamesAiGrid.ItemsSource = _project.ModuleCatalog.AnalogInputs;
        ModuleNamesRtdGrid.ItemsSource = _project.ModuleCatalog.RtdInputs;
        ModuleNamesCounterGrid.ItemsSource = _project.ModuleCatalog.Counters;
        ModuleNamesAoGrid.ItemsSource = _project.ModuleCatalog.AnalogOutputs;
        _dbDiView = CollectionViewSource.GetDefaultView(_project.DbCatalog.DiRows);
        _dbDiView.Filter = FilterDbDiRow;
        SortDbDiById();
        LoadDbDiFilterItems();
        DbDiGrid.ItemsSource = _dbDiView;
        _dbDoView = CollectionViewSource.GetDefaultView(_project.DbCatalog.DoRows);
        _dbDoView.Filter = FilterDbDoRow;
        SortDbDoById();
        LoadDbDoFilterItems();
        DbDoGrid.ItemsSource = _dbDoView;
        _dbMeasView = CollectionViewSource.GetDefaultView(_project.DbCatalog.MeasRows);
        _dbMeasView.Filter = FilterDbMeasRow;
        SortDbMeasById();
        LoadDbMeasFilterItems();
        DbMeasGrid.ItemsSource = _dbMeasView;
        _dbAoRowsView = CollectionViewSource.GetDefaultView(_project.DbCatalog.AoRows);
        _dbAoRowsView.Filter = FilterDbAoRow;
        LoadDbAoFilterItems();
        DbAoGrid.ItemsSource = _dbAoRowsView;
        _dbAlmView = CollectionViewSource.GetDefaultView(_project.DbCatalog.AlarmRows);
        _dbAlmView.Filter = FilterDbAlmRow;
        SortDbAlmById();
        LoadDbAlmFilterItems();
        DbAlmGrid.ItemsSource = _dbAlmView;
        _dbParView = CollectionViewSource.GetDefaultView(_project.DbCatalog.ParameterRows);
        _dbParView.Filter = FilterDbParRow;
        SortDbParById();
        LoadDbParFilterItems();
        DbParGrid.ItemsSource = _dbParView;
        BindSequenceViews();
        BindTextListViews();
        BindScadaViews();
        DevicesGrid.ItemsSource = _project.Devices;
        ConfigureDbValidationCombos();
        ConfigureOnOffGridCombos(DevicesGrid);
        EnsureDefaultSlicerSelection(DbDiColumnSlicer, "Nothing");
        ApplyDbDiColumnVisibility();
        EnsureDefaultSlicerSelection(DbDoColumnSlicer, "Nothing");
        ApplyDbDoColumnVisibility();
        EnsureDefaultSlicerSelection(DbMeasColumnSlicer, "Nothing");
        ApplyDbMeasColumnVisibility();
        EnsureDefaultSlicerSelection(DbAoColumnSlicer, "Nothing");
        ApplyDbAoColumnVisibility();
        EnsureDefaultSlicerSelection(DbAlmColumnSlicer, "Nothing");
        ApplyDbAlmColumnVisibility();
        EnsureDefaultSlicerSelection(DbParColumnSlicer, "Nothing");
        ApplyDbParColumnVisibility();
        ApplySequenceColumnVisibility();
        ApplyTableDesign();

        UpdateProjectPathText();
        UpdateToolbarVisibility();
        _validationStateUpdater.Update(_project);
        _bindingProject = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AdjustMainTabHeaderWidths();
            ResizeDataGridColumnsToViewport(DiGrid);
            ResizeDataGridColumnsToViewport(DoGrid);
            ResizeDataGridColumnsToViewport(AiGrid);
            ResizeDataGridColumnsToViewport(RtdGrid);
            ResizeDataGridColumnsToViewport(CounterGrid);
            ResizeDataGridColumnsToViewport(AoGrid);
            ResizeDataGridColumnsToViewport(DevicesGrid);
            ResizeDbAoColumnsToViewport();
            ResizeSequenceControlStatusColumnsToViewport();
            ResizeSequenceCommandColumnsToViewport();
        }), DispatcherPriority.Background);
    }

    private void BindSequenceViews()
    {
        _project.DbCatalog.Sequence.EnsureDefaults();
        SequenceStartView.Sequence = _project.DbCatalog.Sequence.GetOrCreateSequence("START");
        SequenceNsdView.Sequence = _project.DbCatalog.Sequence.GetOrCreateSequence("NSD");
        SequenceQsdView.Sequence = _project.DbCatalog.Sequence.GetOrCreateSequence("QSD");
        SequenceEsdView.Sequence = _project.DbCatalog.Sequence.GetOrCreateSequence("ESD");

        foreach (var view in new[] { SequenceStartView, SequenceNsdView, SequenceQsdView, SequenceEsdView })
        {
            view.AllSequences = _project.DbCatalog.Sequence.Sequences;
            view.ControlStatusRows = _project.DbCatalog.Sequence.ControlStatusRows;
            view.CommandRows = _project.DbCatalog.Sequence.CommandRows;
        }

        SequenceParSourceText.Text = GetSequenceParSourceText();
        _sequenceControlStatusView = CollectionViewSource.GetDefaultView(_project.DbCatalog.Sequence.ControlStatusRows);
        _sequenceControlStatusView.Filter = FilterSequenceControlStatusRow;
        LoadSequenceControlStatusFilterItems();
        SequenceControlStatusGrid.ItemsSource = _sequenceControlStatusView;

        _sequenceCommandView = CollectionViewSource.GetDefaultView(_project.DbCatalog.Sequence.CommandRows);
        _sequenceCommandView.Filter = FilterSequenceCommandRow;
        LoadSequenceCommandFilterItems();
        SequenceCommandGrid.ItemsSource = _sequenceCommandView;
    }

    private void BindTextListViews()
    {
        _project.DbCatalog.TextLists ??= TextListCatalog.CreateDefault();
        _project.DbCatalog.TextLists.EnsureDefaults();
        TextListAlmBlockingClassGrid.ItemsSource = _project.DbCatalog.TextLists.AlmBlockingClassRows;
        TextListCabinetsGrid.ItemsSource = _project.DbCatalog.TextLists.CabinetRows;
        TextListPortConfigurationGrid.ItemsSource = _project.DbCatalog.TextLists.PortConfigurationRows;
        TextListSeqCmdGrid.ItemsSource = _project.DbCatalog.TextLists.SeqCmdRows;
        TextListSeqConfigGrid.ItemsSource = _project.DbCatalog.TextLists.SeqConfigRows;
        RefreshTextListComments();
    }

    private void BindScadaViews()
    {
        _project.Scada ??= ScadaDatabase.CreateDefault();
        _project.Scada.EnsureDefaults();
        ScadaConfigFileService.ApplyTagsToVariables(_project.Scada);
        BindScadaGeneralView();
        BindScadaHeaderView();
        BindScadaMainNavView();
        BindScadaScreenMatrixView();
        BindScadaLocationMatrixView();
        BindScadaPlcView();
        ScadaConfigFileService.ApplyTagsToVariables(_project.Scada);
        ScadaOverviewGrid.ItemsSource = ScadaConfigFileService.BuildSummary(_project.Scada);
        _scadaVariablesView = new ListCollectionView(_project.Scada.Variables);
        ScadaVariablesGrid.ItemsSource = _scadaVariablesView;
        _scadaNavigationView = new ListCollectionView(_project.Scada.Variables);
        _scadaNavigationView.Filter = FilterScadaNavigationRow;
        ScadaNavigationGrid.ItemsSource = _scadaNavigationView;
        _scadaScreensView = new ListCollectionView(_project.Scada.Variables);
        _scadaScreensView.Filter = FilterScadaScreenRow;
        ScadaScreensGrid.ItemsSource = _scadaScreensView;
        RebuildScadaScreenCatalog();
        InitScadaUnifiedDeviceCombo();
        _scadaTagWritesView = new ListCollectionView(_project.Scada.TagWrites);
        _scadaTagWritesView.SortDescriptions.Add(new SortDescription(nameof(ScadaTagWrite.Id), ListSortDirection.Ascending));
        _scadaTagWritesView.Filter = FilterScadaTagWriteRow;
        ScadaTagsGrid.ItemsSource = _scadaTagWritesView;
        if (ScadaTagsFilterBox is not null && !ScadaTagsFilterBox.Text.Equals(_scadaTagsFilterText, StringComparison.Ordinal))
        {
            ScadaTagsFilterBox.Text = _scadaTagsFilterText;
        }
        ScadaSourceText.Text = string.IsNullOrWhiteSpace(_project.Scada.SourceTextPath)
            ? "No SCADA text imported."
            : _project.Scada.SourceTextPath;
        ScadaPaneSourceText.Text = ScadaSourceText.Text;
    }

    private void BindScadaGeneralView()
    {
        _scadaGeneralSettings = new ScadaGeneralSettings(
            GetOrCreateScadaVariable("RUNTIME_NAME", "STRING", "MAIN", "SCADA_PC", "RUNTIME_NAME", "Runtime station name"),
            GetOrCreateScadaVariable("BILINGUAL_SCADA", "INT", "MAIN", "1", "BILINGUAL_SCADA", "0 - one language, 1 - two languages"),
            GetOrCreateScadaVariable("LOCATION_NUM", "INT", "MAIN", "0", "LOCATION_NUM", "Number of configured locations"),
            GetOrCreateScadaVariable("LOCATION_FOR_SET_LANGUAGE", "INT", "MAIN", "0", "LOCATION_FOR_SET_LANGUAGE", "Location used for PLC language selection"),
            GetOrCreateScadaVariable("SECOND_LANGUAGE", "INT", "MAIN", "1044", "SECOND_LANGUAGE", "Siemens language code for second language"));

        _scadaLocationRows = Enumerable.Range(0, 11)
            .Select(index => new ScadaLocationRow(
                index,
                GetOrCreateScadaVariable($"LOCATION_{index}_ACTIVE", "INT", "MAIN", "0", $"LOCATION_{index}.LOCATION_ACTIVE", "0 - inactive, 1 - active"),
                GetOrCreateScadaVariable($"LOCATION_{index}_NAME_1", "STRING", "MAIN", "", $"LOCATION_{index}.LOCATION_NAME", "Location name"),
                GetOrCreateScadaVariable($"LOCATION_{index}_NAME_2", "STRING", "MAIN", "", $"LOCATION_{index}.LOCATION_NAME_2", "Location name second language"),
                GetOrCreateScadaVariable($"LOCATION_{index}_SOURCE", "INT", "MAIN", "0", $"LOCATION_{index}.SOURCE", "Location source"),
                GetOrCreateScadaVariable($"LOCATION_{index}_ENTRY", "INT", "MAIN", "", $"LOCATION_{index}.ENTRY", "Location entry"),
                GetOrCreateScadaVariable($"LOCATION_{index}_LOCAL_REMOTE", "INT", "MAIN", "0", $"LOCATION_{index}_LOCAL_REMOTE", "0 - Local, 1 - Remote")))
            .ToList();

        ScadaSecondLanguageCombo.ItemsSource = _scadaLanguageOptions;
        ScadaLocationLocalRemoteColumn.ItemsSource = _scadaLocalRemoteOptions;
        ScadaGeneralSettingsPanel.DataContext = _scadaGeneralSettings;
        ScadaLocationsGrid.ItemsSource = _scadaLocationRows;
    }

    private void ValidateScadaGeneral_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _scadaLocationRows)
        {
            row.Validate();
        }

        NormalizeScadaDatabase();
        BindScadaViews();
        ShowMessage("SCADA general locations validated.");
    }

    private void BindScadaHeaderView()
    {
        _scadaHeaderRows = Enumerable.Range(1, 10)
            .Select(index => new ScadaHeaderRow(
                index,
                GetOrCreateScadaVariable($"HEADER_TABS_ENTRY_{index}", "STRING", "TABS", "", $"HEADER_TABS.ENTRY_{index}", "Header tab visible entry"),
                GetOrCreateScadaVariable($"HEADER_TAB_{index}_NAME_1", "STRING", "TABS", "", $"HEADER_TAB_{index}.NAME_1", "Header tab name language 1"),
                GetOrCreateScadaVariable($"HEADER_TAB_{index}_NAME_2", "STRING", "TABS", "", $"HEADER_TAB_{index}.NAME_2", "Header tab name language 2"),
                GetOrCreateScadaVariable($"HEADER_TAB_{index}_POWER_VISIBLE", "INT", "TABS", "0", $"HEADER_TAB_{index}.POWER_VISIBLE", "0 - hidden, 1 - visible")))
            .ToList();

        ScadaHeaderGrid.ItemsSource = _scadaHeaderRows;
    }

    private void ValidateScadaHeader_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _scadaHeaderRows)
        {
            row.Validate();
        }

        NormalizeScadaDatabase();
        BindScadaViews();
        ShowMessage("SCADA header entries validated.");
    }

    private void BindScadaMainNavView()
    {
        _scadaMainNavSettings = new ScadaMainNavSettings(
            GetOrCreateScadaVariable("MAIN_NAV_BUTTONS_NUM", "INT", "MAIN", "0", "MAIN_NAV_BUTTONS_NUM", "Number of visible main navigation buttons"));

        _scadaMainNavRows = Enumerable.Range(1, 16)
            .Select(index => new ScadaMainNavRow(
                index,
                GetOrCreateScadaVariable($"MAIN_NAV_BUTTONS_ENTRY_{index}", "STRING", "MAIN_NAV", "", $"MAIN_NAV_BUTTONS.ENTRY_{index}", "Main navigation button entry"),
                GetOrCreateScadaVariable($"MAIN_NAV_BTTN_{index}_TEXT_LANGUAGE_1", "STRING", "MAIN_NAV", "", $"MAIN_NAV_BTTN_{index}.TEXT_LANGUAGE_1", "Main navigation button text language 1"),
                GetOrCreateScadaVariable($"MAIN_NAV_BTTN_{index}_TEXT_LANGUAGE_2", "STRING", "MAIN_NAV", "", $"MAIN_NAV_BTTN_{index}.TEXT_LANGUAGE_2", "Main navigation button text language 2"),
                GetOrCreateScadaVariable($"MAIN_NAV_BTTN_{index}_GRAPHIC_LIST_ID", "INT", "MAIN_NAV", "0", $"MAIN_NAV_BTTN_{index}.GRAPHIC_LIST_ID", "Main navigation button graphic list ID"),
                GetOrCreateScadaVariable($"MAIN_NAV_SCREEN_SHAPE_{index}", "INT", "MAIN_NAV", "0", $"MAIN_NAV_SCREEN_SHAPE[{index}]", "Main navigation screen layout shape"),
                GetOrCreateScadaVariable($"BUTTON_{index}_SCREEN_NUM", "INT", "MAIN", "0", $"BUTTON_{index}_SCREEN_NUM", "Number of configured screens for this main navigation button"),
                GetOrCreateScadaVariable($"SIDE_CONTENT_DEFAULT_SCREENS_ENTRY_{index}", "STRING", "SIDE_CONTENT", "", $"SIDE_CONTENT_DEFAULT_SCREENS.ENTRY_{index}", "Default side content screen")))
            .ToList();

        ScadaMainNavScreenShapeColumn.ItemsSource = _scadaScreenShapeOptions;
        ScadaMainNavSettingsPanel.DataContext = _scadaMainNavSettings;
        ScadaMainNavGrid.ItemsSource = _scadaMainNavRows;
    }

    private void ValidateScadaMainNav_Click(object sender, RoutedEventArgs e)
    {
        var visibleCount = 0;
        foreach (var row in _scadaMainNavRows)
        {
            row.Validate();
            if (row.Visible)
            {
                visibleCount++;
            }
        }

        if (_scadaMainNavSettings is not null)
        {
            _scadaMainNavSettings.ButtonNumValue = visibleCount.ToString(CultureInfo.InvariantCulture);
        }

        NormalizeScadaDatabase();
        BindScadaViews();
        ShowMessage("SCADA main navigation validated.");
    }

    private int CountScadaScreensForButton(int button)
    {
        return Enumerable.Range(1, 16)
            .Count(entry => !string.IsNullOrWhiteSpace(GetScadaVariableValue($"SCREENS_Button_{button}_ENTRY_{entry}")));
    }

    private void BindScadaScreenMatrixView()
    {
        var buttonCount = ClampScadaCount(GetScadaVariableValue("MAIN_NAV_BUTTONS_NUM"), 16);
        _scadaScreenMatrixRows = Enumerable.Range(1, buttonCount)
            .Select(button =>
            {
                var cellCount = ClampScadaCount(GetScadaVariableValue($"BUTTON_{button}_SCREEN_NUM"), 16);
                var cells = Enumerable.Range(1, cellCount)
                    .Select(topNav => new ScadaScreenMatrixCell(
                        button,
                        topNav,
                        GetOrCreateScadaVariable($"SCREENS_Button_{button}_ENTRY_{topNav}", "STRING", "SCREEN_ENTRY", "", $"SCREENS_Button_{button}.ENTRY_{topNav}", "Screen opened by selected main nav/top nav card"),
                        GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_1", "STRING", "TOP_NAV_CARDS", "", $"Button_{button}_TOP_NAV_CARD_{topNav}.TEXT_LANGUAGE_1", "Top nav card text language 1"),
                        GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_2", "STRING", "TOP_NAV_CARDS", "", $"Button_{button}_TOP_NAV_CARD_{topNav}.TEXT_LANGUAGE_2", "Top nav card text language 2"),
                        GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_GRAPHIC_LIST_ID", "INT", "TOP_NAV_CARDS", "0", $"Button_{button}_TOP_NAV_CARD_{topNav}.GRAPHIC_LIST_ID", "Top nav card graphic list ID")))
                    .ToList();

                var entryName = GetScadaVariableValue($"MAIN_NAV_BUTTONS_ENTRY_{button}");
                return new ScadaScreenMatrixButtonRow(button, entryName, cells);
            })
            .ToList();

        ScadaScreenMatrixItems.ItemsSource = _scadaScreenMatrixRows;
        var rowChoices = _scadaScreenMatrixRows
            .Select(row => new ScadaMatrixChoice(row, row.Header))
            .ToList();
        var cellChoices = _scadaScreenMatrixRows
            .SelectMany(row => row.Cells.Select(cell => new ScadaMatrixChoice(cell, $"{row.Header} / {cell.TopNavName}")))
            .ToList();
        SetScadaMatrixChoices(rowChoices, ScadaScreenCopyRowSourceCombo, ScadaScreenCopyRowTargetCombo, ScadaScreenSwapRowACombo, ScadaScreenSwapRowBCombo);
        SetScadaMatrixChoices(cellChoices, ScadaScreenCopyCellSourceCombo, ScadaScreenCopyCellTargetCombo, ScadaScreenSwapCellACombo, ScadaScreenSwapCellBCombo);
    }

    private void BindScadaLocationMatrixView()
    {
        _scadaLocationMatrixRows = Enumerable.Range(0, 11)
            .Where(location => IsScadaIntEnabled(GetScadaVariableValue($"LOCATION_{location}_ACTIVE")))
            .Select(location =>
            {
                var name = GetScadaVariableValue($"LOCATION_{location}_NAME_1");
                var entry = GetScadaVariableValue($"LOCATION_{location}_ENTRY");
                var cells = Enumerable.Range(0, 12)
                    .Select(system => new ScadaLocationMatrixCell(
                        location,
                        system,
                        GetOrCreateScadaVariable($"LOCATION_{location}_SYSTEM_{system}_SYSTEM_NAME", "STRING", "LOCATION_SYSTEMS", "", $"LOCATION_{location}_SYSTEM_{system}.SYSTEM_NAME", "Location system name"),
                        GetOrCreateScadaVariable($"LOCATION_{location}_SYSTEM_{system}_SYSTEM_NAME_2", "STRING", "LOCATION_SYSTEMS", "", $"LOCATION_{location}_SYSTEM_{system}.SYSTEM_NAME_2", "Location system name language 2"),
                        GetOrCreateScadaVariable($"LOCATION_{location}_SYSTEM_{system}_GRAPHIC_LIST_ID", "INT", "LOCATION_SYSTEMS", "0", $"LOCATION_{location}_SYSTEM_{system}.GRAPHIC_LIST_ID", "Location system graphic list ID")))
                    .ToList();
                return new ScadaLocationMatrixRow(location, entry, name, cells);
            })
            .ToList();

        ScadaLocationMatrixItems.ItemsSource = _scadaLocationMatrixRows;
        var rowChoices = _scadaLocationMatrixRows
            .Select(row => new ScadaMatrixChoice(row, row.Header))
            .ToList();
        var cellChoices = _scadaLocationMatrixRows
            .SelectMany(row => row.Cells.Select(cell => new ScadaMatrixChoice(cell, $"{row.Header} / {cell.SystemNameHeader}")))
            .ToList();
        SetScadaMatrixChoices(rowChoices, ScadaLocationCopyRowSourceCombo, ScadaLocationCopyRowTargetCombo, ScadaLocationSwapRowACombo, ScadaLocationSwapRowBCombo);
        SetScadaMatrixChoices(cellChoices, ScadaLocationCopyCellSourceCombo, ScadaLocationCopyCellTargetCombo, ScadaLocationSwapCellACombo, ScadaLocationSwapCellBCombo);
    }

    private void BindScadaPlcView()
    {
        _scadaPlcTabs = Enumerable.Range(0, 10)
            .Where(location => IsScadaIntEnabled(GetScadaVariableValue($"LOCATION_{location}_ACTIVE")))
            .Select(location =>
            {
                var name = GetScadaVariableValue($"LOCATION_{location}_NAME_1");
                var entry = GetScadaVariableValue($"LOCATION_{location}_ENTRY");
                var rows = new List<ScadaPlcRackRow>
                {
                    new("Central", Enumerable.Range(2, 29)
                        .Select(slot => new ScadaPlcSlotCell(
                            slot,
                            GetOrCreateScadaVariable(
                                $"LOCATION_{location}_CENTRAL_RACK_BASE_UNIT_SLOT_{slot}",
                                "INT",
                                "PLC",
                                "0",
                                $"LOCATION_{location}_CENTRAL_RACK_BASE_UNIT.SLOT_{slot}",
                                "0 - GREY MODULE, 1 - WHITE MODULE")))
                        .ToList())
                };

                for (var rack = 1; rack <= 5; rack++)
                {
                    rows.Add(new ScadaPlcRackRow(
                        $"Distrib rack {rack.ToString(CultureInfo.InvariantCulture)}",
                        Enumerable.Range(1, 15)
                            .Select(slot => new ScadaPlcSlotCell(
                                slot,
                                GetOrCreateScadaVariable(
                                    $"LOCATION_{location}_DISTRIB_RACK_{rack}_BASE_UNIT_SLOT_{slot}",
                                    "INT",
                                    "PLC",
                                    "0",
                                    $"LOCATION_{location}_DISTRIB_RACK_{rack}_BASE_UNIT.SLOT_{slot}",
                                    "0 - GREY MODULE, 1 - WHITE MODULE")))
                            .ToList()));
                }

                return new ScadaPlcLocationTab(location, entry, name, rows);
            })
            .ToList();

        ScadaPlcLocationTabs.ItemsSource = _scadaPlcTabs;
        var rowChoices = _scadaPlcTabs
            .SelectMany(tab => tab.Rows.Select(row => new ScadaMatrixChoice(row, $"{tab.Header} / {row.RackName}")))
            .ToList();
        var cellChoices = _scadaPlcTabs
            .SelectMany(tab => tab.Rows.SelectMany(row => row.Slots.Select(cell => new ScadaMatrixChoice(cell, $"{tab.Header} / {row.RackName} / {cell.SlotHeader}"))))
            .ToList();
        SetScadaMatrixChoices(rowChoices, ScadaPlcCopyRowSourceCombo, ScadaPlcCopyRowTargetCombo, ScadaPlcSwapRowACombo, ScadaPlcSwapRowBCombo);
        SetScadaMatrixChoices(cellChoices, ScadaPlcCopyCellSourceCombo, ScadaPlcCopyCellTargetCombo, ScadaPlcSwapCellACombo, ScadaPlcSwapCellBCombo);
    }

    private static void SetScadaMatrixChoices(IReadOnlyList<ScadaMatrixChoice> choices, params ComboBox[] combos)
    {
        foreach (var combo in combos)
        {
            var selectedItem = combo.SelectedItem as ScadaMatrixChoice;
            combo.ItemsSource = choices;
            combo.SelectedItem = selectedItem is not null && choices.Any(choice => ReferenceEquals(choice.Item, selectedItem.Item))
                ? choices.First(choice => ReferenceEquals(choice.Item, selectedItem.Item))
                : choices.FirstOrDefault();
        }
    }

    private string GetScadaVariableValue(string name)
    {
        return _project.Scada.Variables.FirstOrDefault(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    }

    private static int ClampScadaCount(string value, int max)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, 0, max)
            : 0;
    }

    private ScadaConfigVariable GetOrCreateScadaVariable(string name, string type, string section, string value, string tag, string comment)
    {
        var variable = _project.Scada.Variables.FirstOrDefault(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (variable is null)
        {
            variable = new ScadaConfigVariable
            {
                Id = _project.Scada.Variables.Count == 0 ? 1 : _project.Scada.Variables.Max(row => row.Id) + 1,
                Section = section,
                Name = name,
                Type = type,
                Value = value,
                Comment = comment,
                SourceLine = 0
            };
            _project.Scada.Variables.Add(variable);
        }
        else
        {
            variable.Type = type;
            if (string.IsNullOrWhiteSpace(variable.Section))
            {
                variable.Section = section;
            }

            if (string.IsNullOrWhiteSpace(variable.Value))
            {
                variable.Value = value;
            }

            if (string.IsNullOrWhiteSpace(variable.Comment))
            {
                variable.Comment = comment;
            }
        }

        if (!_project.Scada.TagWrites.Any(row => row.VariableName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                 && row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
        {
            _project.Scada.TagWrites.Add(new ScadaTagWrite
            {
                Id = _project.Scada.TagWrites.Count == 0 ? 1 : _project.Scada.TagWrites.Max(row => row.Id) + 1,
                Section = section,
                Tag = tag,
                VariableName = name,
                SourceLine = 0
            });
        }

        return variable;
    }

    private static bool FilterScadaNavigationRow(object item)
    {
        if (item is not ScadaConfigVariable row)
        {
            return false;
        }

        return row.Section.Contains("MAIN", StringComparison.OrdinalIgnoreCase)
               || row.Section.Contains("TAB", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("NAV", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("HEADER", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("LOCATION", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilterScadaScreenRow(object item)
    {
        if (item is not ScadaConfigVariable row)
        {
            return false;
        }

        return row.Section.Contains("SCREEN", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("SCREEN", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("GRAPHIC", StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains("ICON", StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterScadaTagWriteRow(object item)
    {
        if (item is not ScadaTagWrite row || string.IsNullOrWhiteSpace(_scadaTagsFilterText))
        {
            return true;
        }

        var text = _scadaTagsFilterText.Trim();
        return row.Id.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Section.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Tag.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.VariableName.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Type.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.SourceLine.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void ScadaTagsFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _scadaTagsFilterText = ScadaTagsFilterBox.Text ?? "";
        _scadaTagWritesView?.Refresh();
    }

    private void ScadaObjectField_LostFocus(object sender, RoutedEventArgs e)
    {
        RefreshScadaTagValues();
    }

    private void ScadaObjectField_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingProject)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(RefreshScadaTagValues), DispatcherPriority.Background);
    }

    private void CopyScadaMatrixCell_Click(object sender, RoutedEventArgs e)
    {
        var source = GetScadaMenuDataContext(sender);
        CopyScadaMatrixCell(source);

        if (_copiedScadaMatrixCell is not null)
        {
            ShowMessage("SCADA cell settings copied.");
        }
    }

    private void PasteScadaMatrixCell_Click(object sender, RoutedEventArgs e)
    {
        var target = GetScadaMenuDataContext(sender);
        if (target is null || _copiedScadaMatrixCell is null)
        {
            return;
        }

        RecordUndoSnapshot();
        if (PasteScadaMatrixCell(target))
        {
            ShowMessage("SCADA cell settings pasted.");
        }
    }

    private void SwapScadaMatrixCell_Click(object sender, RoutedEventArgs e)
    {
        var target = GetScadaMenuDataContext(sender);
        var source = _copiedScadaMatrixCellSource;
        if (source is null || target is null || ReferenceEquals(source, target))
        {
            return;
        }

        RecordUndoSnapshot();
        if (SwapScadaMatrixCells(source, target))
        {
            ShowMessage("SCADA cell settings swapped.");
        }
    }

    private void CopyScadaMatrixRow_Click(object sender, RoutedEventArgs e)
    {
        var source = GetScadaMenuDataContext(sender);
        CopyScadaMatrixRow(source);
        if (_copiedScadaMatrixRow is not null)
        {
            ShowMessage("SCADA row settings copied.");
        }
    }

    private void PasteScadaMatrixRow_Click(object sender, RoutedEventArgs e)
    {
        var target = GetScadaMenuDataContext(sender);
        if (target is null || _copiedScadaMatrixRow is null)
        {
            return;
        }

        RecordUndoSnapshot();
        if (PasteScadaMatrixRow(target))
        {
            ShowMessage("SCADA row settings pasted.");
        }
    }

    private void SwapScadaMatrixRow_Click(object sender, RoutedEventArgs e)
    {
        var target = GetScadaMenuDataContext(sender);
        var source = _copiedScadaMatrixRowSource;
        if (source is null || target is null || ReferenceEquals(source, target))
        {
            return;
        }

        RecordUndoSnapshot();
        if (SwapScadaMatrixRows(source, target))
        {
            ShowMessage("SCADA row settings swapped.");
        }
    }

    private void CopySelectedScadaScreenCell_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixCell(ScadaScreenCopyCellSourceCombo, ScadaScreenCopyCellTargetCombo);
    private void SwapSelectedScadaScreenCell_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixCell(ScadaScreenSwapCellACombo, ScadaScreenSwapCellBCombo);
    private void CopySelectedScadaScreenRow_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixRow(ScadaScreenCopyRowSourceCombo, ScadaScreenCopyRowTargetCombo);
    private void SwapSelectedScadaScreenRow_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixRow(ScadaScreenSwapRowACombo, ScadaScreenSwapRowBCombo);
    private void CopySelectedScadaLocationCell_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixCell(ScadaLocationCopyCellSourceCombo, ScadaLocationCopyCellTargetCombo);
    private void SwapSelectedScadaLocationCell_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixCell(ScadaLocationSwapCellACombo, ScadaLocationSwapCellBCombo);
    private void CopySelectedScadaLocationRow_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixRow(ScadaLocationCopyRowSourceCombo, ScadaLocationCopyRowTargetCombo);
    private void SwapSelectedScadaLocationRow_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixRow(ScadaLocationSwapRowACombo, ScadaLocationSwapRowBCombo);
    private void CopySelectedScadaPlcCell_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixCell(ScadaPlcCopyCellSourceCombo, ScadaPlcCopyCellTargetCombo);
    private void SwapSelectedScadaPlcCell_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixCell(ScadaPlcSwapCellACombo, ScadaPlcSwapCellBCombo);
    private void CopySelectedScadaPlcRow_Click(object sender, RoutedEventArgs e) => CopySelectedScadaMatrixRow(ScadaPlcCopyRowSourceCombo, ScadaPlcCopyRowTargetCombo);
    private void SwapSelectedScadaPlcRow_Click(object sender, RoutedEventArgs e) => SwapSelectedScadaMatrixRow(ScadaPlcSwapRowACombo, ScadaPlcSwapRowBCombo);

    private void CopySelectedScadaMatrixCell(ComboBox sourceCombo, ComboBox targetCombo)
    {
        var source = GetScadaMatrixChoiceItem(sourceCombo);
        var target = GetScadaMatrixChoiceItem(targetCombo);
        if (source is null || target is null)
        {
            return;
        }

        RecordUndoSnapshot();
        CopyScadaMatrixCell(source);
        if (PasteScadaMatrixCell(target))
        {
            ShowMessage("SCADA cell settings copied.");
        }
    }

    private void SwapSelectedScadaMatrixCell(ComboBox firstCombo, ComboBox secondCombo)
    {
        var first = GetScadaMatrixChoiceItem(firstCombo);
        var second = GetScadaMatrixChoiceItem(secondCombo);
        if (first is null || second is null || ReferenceEquals(first, second))
        {
            return;
        }

        RecordUndoSnapshot();
        if (SwapScadaMatrixCells(first, second))
        {
            ShowMessage("SCADA cell settings swapped.");
        }
    }

    private void CopySelectedScadaMatrixRow(ComboBox sourceCombo, ComboBox targetCombo)
    {
        var source = GetScadaMatrixChoiceItem(sourceCombo);
        var target = GetScadaMatrixChoiceItem(targetCombo);
        if (source is null || target is null)
        {
            return;
        }

        RecordUndoSnapshot();
        CopyScadaMatrixRow(source);
        if (PasteScadaMatrixRow(target))
        {
            ShowMessage("SCADA row settings copied.");
        }
    }

    private void SwapSelectedScadaMatrixRow(ComboBox firstCombo, ComboBox secondCombo)
    {
        var first = GetScadaMatrixChoiceItem(firstCombo);
        var second = GetScadaMatrixChoiceItem(secondCombo);
        if (first is null || second is null || ReferenceEquals(first, second))
        {
            return;
        }

        RecordUndoSnapshot();
        if (SwapScadaMatrixRows(first, second))
        {
            ShowMessage("SCADA row settings swapped.");
        }
    }

    private static object? GetScadaMatrixChoiceItem(ComboBox combo)
    {
        return combo.SelectedItem is ScadaMatrixChoice choice ? choice.Item : null;
    }

    private void CopyScadaMatrixCell(object? source)
    {
        _copiedScadaMatrixCellSource = source;
        _copiedScadaMatrixCell = CaptureScadaMatrixCellSettings(source);
    }

    private bool PasteScadaMatrixCell(object target)
    {
        if (_copiedScadaMatrixCell is null || !TryApplyScadaMatrixCellSettings(target, _copiedScadaMatrixCell))
        {
            return false;
        }

        RefreshScadaMatrixAfterClipboardEdit();
        return true;
    }

    private bool SwapScadaMatrixCells(object source, object target)
    {
        if (!TrySwapScadaMatrixCellSettings(source, target))
        {
            return false;
        }

        _copiedScadaMatrixCell = CaptureScadaMatrixCellSettings(target);
        _copiedScadaMatrixCellSource = target;
        RefreshScadaMatrixAfterClipboardEdit();
        return true;
    }

    private void CopyScadaMatrixRow(object? source)
    {
        _copiedScadaMatrixRowSource = source;
        _copiedScadaMatrixRow = CaptureScadaMatrixRowSettings(source);
    }

    private bool PasteScadaMatrixRow(object target)
    {
        if (_copiedScadaMatrixRow is null || !TryApplyScadaMatrixRowSettings(target, _copiedScadaMatrixRow))
        {
            return false;
        }

        RefreshScadaMatrixAfterClipboardEdit();
        return true;
    }

    private bool SwapScadaMatrixRows(object source, object target)
    {
        if (!TrySwapScadaMatrixRowSettings(source, target))
        {
            return false;
        }

        _copiedScadaMatrixRow = CaptureScadaMatrixRowSettings(target);
        _copiedScadaMatrixRowSource = target;
        RefreshScadaMatrixAfterClipboardEdit();
        return true;
    }

    private static object? GetScadaMenuDataContext(object sender)
    {
        return sender is MenuItem item
            ? item.CommandParameter ?? item.DataContext
            : null;
    }

    private static object? CaptureScadaMatrixCellSettings(object? source)
    {
        return source switch
        {
            ScadaScreenMatrixCell cell => cell.CaptureSettings(),
            ScadaLocationMatrixCell cell => cell.CaptureSettings(),
            ScadaPlcSlotCell cell => cell.CaptureSettings(),
            _ => null
        };
    }

    private static bool TryApplyScadaMatrixCellSettings(object target, object settings)
    {
        switch (target, settings)
        {
            case (ScadaScreenMatrixCell cell, ScadaScreenCellSettings cellSettings):
                cell.ApplySettings(cellSettings);
                return true;
            case (ScadaLocationMatrixCell cell, ScadaLocationCellSettings cellSettings):
                cell.ApplySettings(cellSettings);
                return true;
            case (ScadaPlcSlotCell cell, ScadaPlcCellSettings cellSettings):
                cell.ApplySettings(cellSettings);
                return true;
            default:
                return false;
        }
    }

    private static bool TrySwapScadaMatrixCellSettings(object source, object target)
    {
        var sourceSettings = CaptureScadaMatrixCellSettings(source);
        var targetSettings = CaptureScadaMatrixCellSettings(target);
        if (sourceSettings is null || targetSettings is null)
        {
            return false;
        }

        return TryApplyScadaMatrixCellSettings(source, targetSettings)
               && TryApplyScadaMatrixCellSettings(target, sourceSettings);
    }

    private object? CaptureScadaMatrixRowSettings(object? source)
    {
        return source switch
        {
            ScadaScreenMatrixButtonRow row => new ScadaScreenRowSettings(CaptureAllScadaScreenRowSettings(row.Button)),
            ScadaLocationMatrixRow row => new ScadaLocationRowSettings(row.Cells.Select(cell => cell.CaptureSettings()).ToList()),
            ScadaPlcRackRow row => new ScadaPlcRowSettings(row.Slots.Select(cell => cell.CaptureSettings()).ToList()),
            _ => null
        };
    }

    private List<ScadaScreenCellSettings> CaptureAllScadaScreenRowSettings(int button)
    {
        return Enumerable.Range(1, 16)
            .Select(topNav => new ScadaScreenCellSettings(
                GetScadaVariableValue($"SCREENS_Button_{button}_ENTRY_{topNav}"),
                GetScadaVariableValue($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_1"),
                GetScadaVariableValue($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_2"),
                GetScadaVariableValue($"Button_{button}_TOP_NAV_CARD_{topNav}_GRAPHIC_LIST_ID")))
            .ToList();
    }

    private bool TryApplyScadaMatrixRowSettings(object target, object settings)
    {
        switch (target, settings)
        {
            case (ScadaScreenMatrixButtonRow row, ScadaScreenRowSettings rowSettings):
                ApplyAllScadaScreenRowSettings(row.Button, rowSettings.Cells);
                return true;
            case (ScadaLocationMatrixRow row, ScadaLocationRowSettings rowSettings):
                ApplyScadaMatrixSettings(row.Cells, rowSettings.Cells, (cell, value) => cell.ApplySettings(value));
                return true;
            case (ScadaPlcRackRow row, ScadaPlcRowSettings rowSettings):
                ApplyScadaMatrixSettings(row.Slots, rowSettings.Cells, (cell, value) => cell.ApplySettings(value));
                return true;
            default:
                return false;
        }
    }

    private void ApplyAllScadaScreenRowSettings(int button, IReadOnlyList<ScadaScreenCellSettings> settings)
    {
        var count = Math.Min(16, settings.Count);
        for (var index = 0; index < count; index++)
        {
            var topNav = index + 1;
            var cellSettings = settings[index];
            GetOrCreateScadaVariable($"SCREENS_Button_{button}_ENTRY_{topNav}", "STRING", "SCREEN_ENTRY", "", $"SCREENS_Button_{button}.ENTRY_{topNav}", "Screen opened by selected main nav/top nav card").Value = cellSettings.ScreenName;
            GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_1", "STRING", "TOP_NAV_CARDS", "", $"Button_{button}_TOP_NAV_CARD_{topNav}.TEXT_LANGUAGE_1", "Top nav card text language 1").Value = cellSettings.TextLanguage1;
            GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_TEXT_LANGUAGE_2", "STRING", "TOP_NAV_CARDS", "", $"Button_{button}_TOP_NAV_CARD_{topNav}.TEXT_LANGUAGE_2", "Top nav card text language 2").Value = cellSettings.TextLanguage2;
            GetOrCreateScadaVariable($"Button_{button}_TOP_NAV_CARD_{topNav}_GRAPHIC_LIST_ID", "INT", "TOP_NAV_CARDS", "0", $"Button_{button}_TOP_NAV_CARD_{topNav}.GRAPHIC_LIST_ID", "Top nav card graphic list ID").Value = NormalizeIntText(cellSettings.GraphicListId);
        }
    }

    private static void ApplyScadaMatrixSettings<TCell, TSettings>(
        IReadOnlyList<TCell> targetCells,
        IReadOnlyList<TSettings> settings,
        Action<TCell, TSettings> apply)
    {
        var count = Math.Min(targetCells.Count, settings.Count);
        for (var index = 0; index < count; index++)
        {
            apply(targetCells[index], settings[index]);
        }
    }

    private bool TrySwapScadaMatrixRowSettings(object source, object target)
    {
        var sourceSettings = CaptureScadaMatrixRowSettings(source);
        var targetSettings = CaptureScadaMatrixRowSettings(target);
        if (sourceSettings is null || targetSettings is null)
        {
            return false;
        }

        return TryApplyScadaMatrixRowSettings(source, targetSettings)
               && TryApplyScadaMatrixRowSettings(target, sourceSettings);
    }

    private void RefreshScadaMatrixAfterClipboardEdit()
    {
        NormalizeScadaDatabase();
        RefreshScadaTagValues();
        ScadaScreenMatrixItems.Items.Refresh();
        ScadaLocationMatrixItems.Items.Refresh();
        ScadaPlcLocationTabs.Items.Refresh();
    }

    private void RefreshTextListComments()
    {
        var database = GetActivePlcDatabase();
        var mapping = database is null
            ? null
            : _project.DescriptionTextLists.GetOrCreate(database.Id, database.Name);
        var data = _project.DescriptionTextLists.TextLists;

        TextListAlmBlockingClassCommentText.Text = FormatTextListComment(mapping?.AlmBlockingClassList, data);
        TextListCabinetsCommentText.Text = FormatTextListComment(mapping?.CabinetsList, data);
        TextListPortConfigurationCommentText.Text = FormatTextListComment(mapping?.PortConfigurationList, data);
        TextListSeqCmdCommentText.Text = FormatTextListComment(mapping?.SeqCmdList, data);
        TextListSeqConfigCommentText.Text = FormatTextListComment(mapping?.SeqConfigList, data);
    }

    private static string FormatTextListComment(string? listName, DescriptionTextListData data)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            return "Comment: no TIA text list mapped.";
        }

        var comment = data.GetComment(listName);
        return string.IsNullOrWhiteSpace(comment)
            ? $"Comment ({listName}): no comment in imported TIA text list workbook."
            : $"Comment ({listName}): {comment}";
    }

    private void BindPlcDatabaseTree()
    {
        _loadingPlcDatabaseTree = true;
        foreach (var database in _project.PlcDatabases)
        {
            database.IsActive = database.Id == _project.ActivePlcDatabaseId;
        }

        UpdatePlcDatabaseTiaVisualStates();

        PlcDatabaseTree.ItemsSource = null;
        PlcDatabaseTree.ItemsSource = _project.PlcDatabases;
        PlcDatabaseTree.UpdateLayout();
        var active = GetActivePlcDatabase();
        if (active is not null)
        {
            SelectPlcDatabaseTreeItem(active);
        }

        _loadingPlcDatabaseTree = false;
    }

    private void BindScadaDatabaseTree()
    {
        _loadingPlcDatabaseTree = true;
        foreach (var database in _project.ScadaDatabases)
        {
            database.IsActive = database.Id == _project.ActiveScadaDatabaseId;
        }

        ScadaDatabaseTree.ItemsSource = null;
        ScadaDatabaseTree.ItemsSource = _project.ScadaDatabases;
        ScadaDatabaseTree.UpdateLayout();
        var active = GetActiveScadaDatabase();
        if (active is not null)
        {
            SelectScadaDatabaseTreeItem(active);
        }

        _loadingPlcDatabaseTree = false;
    }

    private void SelectPlcDatabaseTreeItem(PlcDatabase active)
    {
        foreach (var item in PlcDatabaseTree.Items)
        {
            if (ReferenceEquals(item, active) && PlcDatabaseTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.BringIntoView();
                return;
            }
        }
    }

    private void ConfigureDiGrid()
    {
        if (DiGrid.Columns.OfType<DataGridComboBoxColumn>().FirstOrDefault(column => (string)column.Header == "CONFIG") is { } configColumn)
        {
            configColumn.ItemsSource = Enum.GetValues<SignalConfigState>();
        }

        var moduleNumbers = BuildModuleFilterItems(
            _project.DigitalInputs.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.DigitalInputs);

        DiModuleFilter.ItemsSource = moduleNumbers;
        DiModuleFilter.SelectedIndex = 0;
    }

    private void ConfigureDoGrid()
    {
        if (DoGrid.Columns.OfType<DataGridComboBoxColumn>().FirstOrDefault(column => (string)column.Header == "CONFIG") is { } configColumn)
        {
            configColumn.ItemsSource = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        }

        var moduleNumbers = BuildModuleFilterItems(
            _project.DigitalOutputs.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.DigitalOutputs);

        DoModuleFilter.ItemsSource = moduleNumbers;
        DoModuleFilter.SelectedIndex = 0;
    }

    private void ConfigureAiGrid()
    {
        ConfigureAnalogGridCombos(AiGrid);
        AiModuleFilter.ItemsSource = BuildModuleFilterItems(
            _project.AnalogInputs.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.AnalogInputs);
        AiModuleFilter.SelectedIndex = 0;
    }

    private void ConfigureRtdGrid()
    {
        ConfigureAnalogGridCombos(RtdGrid);
        RtdModuleFilter.ItemsSource = BuildModuleFilterItems(
            _project.RtdInputs.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.RtdInputs);
        RtdModuleFilter.SelectedIndex = 0;
    }

    private static void ConfigureAnalogGridCombos(DataGrid grid)
    {
        var onOffStates = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        foreach (var column in grid.Columns.OfType<DataGridComboBoxColumn>())
        {
            column.ItemsSource = onOffStates;
        }
    }

    private void ConfigureCounterGrid()
    {
        ConfigureOnOffGridCombos(CounterGrid);
        CounterModuleFilter.ItemsSource = BuildModuleFilterItems(
            _project.Counters.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.Counters);
        CounterModuleFilter.SelectedIndex = 0;
    }

    private void ConfigureAoGrid()
    {
        ConfigureOnOffGridCombos(AoGrid);
        AoModuleFilter.ItemsSource = BuildModuleFilterItems(
            _project.AnalogOutputs.Select(row => row.ModuleNumber),
            _project.ModuleCatalog.AnalogOutputs);
        AoModuleFilter.SelectedIndex = 0;
    }

    private static void ConfigureOnOffGridCombos(DataGrid grid)
    {
        var onOffStates = new[] { SignalConfigState.Disabled, SignalConfigState.Enabled };
        foreach (var column in grid.Columns.OfType<DataGridComboBoxColumn>())
        {
            column.ItemsSource = onOffStates;
        }
    }

    private void ConfigureDbValidationCombos()
    {
        ValidationValueDisplayConverter.SetOptions(_project.DbCatalog.ValidationLists);
        ModuleNameDisplayConverter.SetModuleNames(_project.ModuleCatalog);
        ModuleNameDisplayConverter.SetMeasurementTypes(_project.AnalogInputs, _project.RtdInputs, _project.Counters, _project.Devices);
        AlarmReferenceDisplayConverter.SetAlarmRows(_project.DbCatalog.AlarmRows);
        AlarmIdReferenceDisplayConverter.SetAlarmRows(_project.DbCatalog.AlarmRows);
        ParameterReferenceDisplayConverter.SetParameterRows(_project.DbCatalog.ParameterRows);
        ConfigureValidationColumn(DbDiAlarmConfigColumn, DbValidationDefaults.DbDiAlarmBoolean);
        ConfigureValidationColumn(DbDiAlarmInvertColumn, DbValidationDefaults.DbDiAlarmBoolean);

        ConfigureValidationColumn(DbMeasUnitColumn, DbValidationDefaults.DbMeasUnit);
        ConfigureValidationColumn(DbMeasLoggingEnableColumn, DbValidationDefaults.DbMeasLoggingEnable);
        ConfigureValidationColumn(DbMeasLoggingTimeColumn, DbValidationDefaults.DbMeasLoggingTime);
        ConfigureValidationColumn(DbAoUnitColumn, DbValidationDefaults.DbMeasUnit);

        ConfigureValidationColumn(DbAlmControlColumn, DbValidationDefaults.DbAlmEnableDisable);
        ConfigureValidationColumn(DbAlmClassColumn, DbValidationDefaults.DbAlmAlarmClass);
        ConfigureValidationColumn(DbAlmReactToUnitColumn, DbValidationDefaults.DbAlmReactToUnit);
        ConfigureValidationColumn(DbAlmSpareColumn, DbValidationDefaults.DbAlmAlarmClass);
        ConfigureValidationColumn(DbAlmBlockingClass1Column, DbValidationDefaults.DbAlmBlockingClass);
        ConfigureValidationColumn(DbAlmBlockingClass2Column, DbValidationDefaults.DbAlmBlockingClass);
        ConfigureValidationColumn(DbAlmTimeBaseColumn, DbValidationDefaults.DbAlmTimeBase);

        ConfigureValidationColumn(DbParInUseColumn, DbValidationDefaults.DbParInUse);
        ConfigureValidationColumn(DbParTypeColumn, DbValidationDefaults.DbParType);
        var colorModes = GetValidationOptions(DbValidationDefaults.DbAlmColorMode);
        DbAlmColorModeCombo.ItemsSource = colorModes;
        DbAlmColorModeCombo.DisplayMemberPath = nameof(DbValidationOption.Text);
        DbAlmColorModeCombo.SelectedValuePath = nameof(DbValidationOption.Value);
        DbAlmColorModeCombo.SelectedItem = colorModes.FirstOrDefault(item => item.Value.Equals(_project.DbCatalog.ConditionalColors.DbAlarmColorRequiresUsed ? "1" : "0", StringComparison.OrdinalIgnoreCase))
                                          ?? colorModes.FirstOrDefault();
        ApplyAlarmSrOptions();
    }

    private void ConfigureValidationColumn(DataGridComboBoxColumn column, string key)
    {
        column.ItemsSource = GetValidationOptions(key);
        column.DisplayMemberPath = nameof(DbValidationOption.Text);
        column.SelectedValuePath = nameof(DbValidationOption.Value);

        // DataGridComboBoxColumn does not auto-derive SortMemberPath from its
        // SelectedValueBinding the way DataGridTextColumn does for Binding, so
        // we set it explicitly. Without this, header-click sorting on combo
        // columns silently does nothing.
        if (string.IsNullOrEmpty(column.SortMemberPath) && column.SelectedValueBinding is Binding binding && binding.Path?.Path is { Length: > 0 } path)
        {
            column.SortMemberPath = path;
        }

        RegisterValidationColumn(column, key);
    }

    private List<DbValidationOption> GetValidationOptions(string key)
    {
        var row = _project.DbCatalog.ValidationLists.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return row is null ? [] : DbValidationListSetting.SplitOptions(row.ValuesText);
    }

    private string ValidationText(string key, string value)
    {
        return GetValidationOptions(key).FirstOrDefault(option => option.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Text ?? value;
    }

    private string ValidationValue(string key, string textOrValue)
    {
        var options = GetValidationOptions(key);
        return options.FirstOrDefault(option =>
                   option.Value.Equals(textOrValue, StringComparison.OrdinalIgnoreCase)
                   || option.DisplayText.Equals(textOrValue, StringComparison.OrdinalIgnoreCase)
                   || option.Text.Equals(textOrValue, StringComparison.OrdinalIgnoreCase))?.Value
               ?? textOrValue;
    }

    private List<DbValidationListSetting> GetValidationSettings(params string[] prefixesOrKeys)
    {
        return _project.DbCatalog.ValidationLists
            .Where(row => prefixesOrKeys.Any(prefixOrKey =>
                row.Key.Equals(prefixOrKey, StringComparison.OrdinalIgnoreCase)
                || row.Key.StartsWith(prefixOrKey, StringComparison.OrdinalIgnoreCase)))
            .Select(row => row.Clone())
            .ToList();
    }

    private List<DbDataBlockNameSetting> GetDataBlockNameSettings()
    {
        if (_project.DbCatalog.DataBlockNames is null || _project.DbCatalog.DataBlockNames.Count == 0)
        {
            _project.DbCatalog.DataBlockNames = DbDataBlockNameDefaults.Create();
        }

        return _project.DbCatalog.DataBlockNames
            .Select(row => row.Clone())
            .ToList();
    }

    private void ApplyDataBlockNameSettings(IEnumerable<DbDataBlockNameSetting> rows)
    {
        _project.DbCatalog.DataBlockNames = rows
            .Where(row =>
                !string.IsNullOrWhiteSpace(row.DbName)
                || !string.IsNullOrWhiteSpace(row.DataBlockName)
                || !string.IsNullOrWhiteSpace(row.FileName))
            .Select(row => row.Clone())
            .ToList();

        if (_project.DbCatalog.DataBlockNames.Count == 0)
        {
            _project.DbCatalog.DataBlockNames = DbDataBlockNameDefaults.Create();
        }
    }

    private void ApplyValidationSettings(IEnumerable<DbValidationListSetting> rows)
    {
        foreach (var updated in rows)
        {
            updated.ValuesText = string.Join(Environment.NewLine, DbValidationListSetting.SplitOptions(updated.ValuesText).Select(option => option.DisplayText));
            var existing = _project.DbCatalog.ValidationLists.FirstOrDefault(row => row.Key.Equals(updated.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _project.DbCatalog.ValidationLists.Add(updated.Clone());
            }
            else
            {
                existing.Name = updated.Name;
                existing.ValuesText = updated.ValuesText;
            }
        }
    }

    private void ApplyAlarmSrOptions()
    {
        var yesNo = GetValidationOptions(DbValidationDefaults.DbAlmYesNo);

        foreach (var row in _project.DbCatalog.AlarmRows)
        {
            row.SrAlarmOptions = yesNo;
            if (row.SrAlarmOptions.Count == 0)
            {
                row.SrAlarmOptions =
                [
                    new("0", "No"),
                    new("1", "Yes")
                ];
            }
        }
    }

    private void ExportOnePlcToExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        _calculator.Recalculate(_project);
        _validationStateUpdater.Update(_project);

        var templatePath = FindExcelTemplate();
        if (templatePath is null)
        {
            ShowError("Excel template was not found: Configuration_Unit_List_Template.xlsm");
            return;
        }

        var activeDatabase = GetActivePlcDatabase();
        var dialog = new SaveFileDialog
        {
            Filter = "Excel macro workbook (*.xlsm)|*.xlsm|Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsm",
            FileName = $"{SanitizeFileName(activeDatabase?.Name ?? _project.ProjectName)}_export.xlsm"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _excelExporter.ExportOnePlc(_project, templatePath, dialog.FileName);
            ShowMessage($"Exported PLC to Excel: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExportDbText_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dbName = GetDbNameFromSender(sender);

        SaveActivePlcDatabase();
        var activeDatabase = GetActivePlcDatabase();
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx",
            FileName = $"{SanitizeFileName(activeDatabase?.Name ?? _project.ProjectName)}_{dbName.Replace(" ", "_", StringComparison.OrdinalIgnoreCase)}_text.xlsx"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _dbTextWorkbookService.Export(_project, dbName, dialog.FileName);
            ShowMessage($"{dbName} text exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExportAllDbText_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        var activeDatabase = GetActivePlcDatabase();
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx",
            FileName = $"{SanitizeFileName(activeDatabase?.Name ?? _project.ProjectName)}_all_db_text.xlsx"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _dbTextWorkbookService.ExportAll(_project, dialog.FileName);
            ShowMessage($"DB text workbook exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void DescriptionTextLists_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        SaveActivePlcDatabase();
        var dialog = new DescriptionTextListsDialog(_project, _descriptionTextListService)
        {
            Owner = EditorOwner
        };
        var accepted = dialog.ShowDialog() == true;
        if (accepted || dialog.Changed)
        {
            ApplyDescriptionTemplatesAndRefresh();
            LoadActivePlcDatabase();
            BindProject();
            MarkTiaSectionState("DB ALM", "Differs", "DB ALM description text lists changed.");
            MarkTiaSectionState("DB PAR", "Differs", "DB PAR description text lists changed.");
            ShowMessage("Descriptions text list mapping updated.");
        }
    }

    private void DescriptionTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        _project.DescriptionTemplates ??= new DescriptionTemplateCatalog();
        SaveActivePlcDatabase();
        var dialog = new DescriptionTemplatesDialog(_project.DescriptionTemplates, FindDescriptionTemplateUsages)
        {
            Owner = EditorOwner
        };
        if (dialog.ShowDialog() == true || dialog.Changed)
        {
            ApplyDescriptionTemplatesAndRefresh();
            MarkTiaSectionState("DB ALM", "Differs", "DB ALM description templates changed.");
            MarkTiaSectionState("DB PAR", "Differs", "DB PAR description templates changed.");
            ShowMessage("Description templates updated.");
        }
    }

    /// <summary>
    /// All ALM/PAR rows whose description currently references the given
    /// template key. The active database lives in _project.DbCatalog, so the
    /// stored copy of that database is skipped to avoid double counting.
    /// </summary>
    private List<DbStringRow> FindDescriptionTemplateUsages(string key)
    {
        var trimmed = (key ?? "").Trim();
        var result = new List<DbStringRow>();
        if (trimmed.Length == 0)
        {
            return result;
        }

        void AddMatches(IEnumerable<DbStringRow> rows)
        {
            result.AddRange(rows.Where(row =>
                (row.DescriptionTemplateKey ?? "").Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)));
        }

        AddMatches(_project.DbCatalog.AlarmRows);
        AddMatches(_project.DbCatalog.ParameterRows);
        foreach (var database in _project.PlcDatabases.Where(database => database.Id != _project.ActivePlcDatabaseId))
        {
            AddMatches(database.DbCatalog.AlarmRows);
            AddMatches(database.DbCatalog.ParameterRows);
        }

        return result;
    }

    private void SelectScadaDatabaseTreeItem(ScadaDatabase active)
    {
        foreach (var item in ScadaDatabaseTree.Items)
        {
            if (ReferenceEquals(item, active) && ScadaDatabaseTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeViewItem)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.BringIntoView();
                return;
            }
        }
    }

    private void OpenTagBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = FindTagBrowserExecutable()
                          ?? throw new FileNotFoundException("WinCC Tag Browser executable was not found.");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
            ShowMessage($"WinCC Tag Browser opened: {exePath}");
        }
        catch (Exception ex)
        {
            ShowError("Could not open WinCC Tag Browser: " + ex.Message);
        }
    }

    private static string? FindTagBrowserExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "tag_browser", "WinCC_Tag_Browser.exe"),
            Path.Combine(AppContext.BaseDirectory, "tag_browser", "dist", "WinCC_Tag_Browser.exe"),
            @"C:\Users\Mazda\Desktop\Program\CWC\tag_browser\dist\WinCC_Tag_Browser.exe"
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            candidates.Add(Path.Combine(current.FullName, "tag_browser", "dist", "WinCC_Tag_Browser.exe"));
            current = current.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private void ImportDescriptionTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _project.DescriptionTemplates ??= new DescriptionTemplateCatalog();
            var changed = _descriptionTemplateWorkbookService.Import(_project.DescriptionTemplates, dialog.FileName);
            changed += ApplyDescriptionTemplatesAndRefresh();
            ShowMessage($"Imported description templates: {changed} change(s).");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExportDescriptionTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx",
            FileName = $"{SanitizeFileName(_project.ProjectName)}_description_templates.xlsx"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _descriptionTemplateWorkbookService.Export(_project.DescriptionTemplates, dialog.FileName);
            ShowMessage($"Exported description templates: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ExportDescriptionTextLists_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx",
            Title = "Select TIA Portal exported text-list Excel to update"
        };

        if (!string.IsNullOrWhiteSpace(_project.DescriptionTextLists.WorkbookPath) && File.Exists(_project.DescriptionTextLists.WorkbookPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_project.DescriptionTextLists.WorkbookPath);
            dialog.FileName = Path.GetFileName(_project.DescriptionTextLists.WorkbookPath);
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "This will write current Plant Configurator descriptions into the selected TIA Portal exported Excel file.\n\nThe selected workbook will be updated in place. Continue?",
            "Update TIA Portal exported Excel",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            SaveActivePlcDatabase();
            var backupPath = dialog.FileName + ".bak";
            File.Copy(dialog.FileName, backupPath, overwrite: true);
            _project.DescriptionTextLists.TextLists = _descriptionTextListService.ImportWorkbook(dialog.FileName);
            _project.DescriptionTextLists.WorkbookPath = dialog.FileName;
            _project.DescriptionTextLists.SyncWithDatabases(_project.PlcDatabases);
            _descriptionTextListService.EnsureExportReferences(_project);
            var changed = _descriptionTextListService.PushDescriptionsIntoTextLists(_project);
            _descriptionTextListService.SaveWorkbook(_project, dialog.FileName);
            ShowMessage($"Updated TIA Portal exported text-list Excel: {changed} text value(s) written. Backup: {backupPath}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ActiveDbGroupNames_Click(object sender, RoutedEventArgs e)
    {
        switch (GetActiveTabKey())
        {
            case "DB DI": DbDiGroupNames_Click(sender, e); break;
            case "DB DO": DbDoGroupNames_Click(sender, e); break;
            case "DB MEAS": DbMeasGroupNames_Click(sender, e); break;
            case "DB ALM": DbAlmGroupNames_Click(sender, e); break;
            case "DB PAR": DbParGroupNames_Click(sender, e); break;
        }
    }

    private void ActiveDbExportText_Click(object sender, RoutedEventArgs e)
    {
        ExportDbText_Click(CreateTaggedButton(GetActiveTabKey()), e);
    }

    private void ActiveDbImportText_Click(object sender, RoutedEventArgs e)
    {
        ImportDbText_Click(CreateTaggedButton(GetActiveTabKey()), e);
    }

    private async void ActiveDbPull_Click(object sender, RoutedEventArgs e)
    {
        await RunDbPullFromActiveToolbarAsync();
    }

    private async void ActiveDbPush_Click(object sender, RoutedEventArgs e)
    {
        await RunDbPushFromActiveToolbarAsync();
    }

    private async Task RunDbPullFromActiveToolbarAsync()
    {
        var dbName = NormalizeDbName(GetActiveTabKey());
        if (dbName == "DB DI")
        {
            DbDiTiaPull_Click(this, new RoutedEventArgs());
            return;
        }

        await RunDbFamilyTiaOperationAsync(
            dbName,
            $"Pulling {dbName} from TIA...",
            service => service.PullDbAsync(_project, dbName),
            rebindAfterSuccess: true,
            progressTitle: $"{dbName} TIA pull",
            progressStatus: $"Exporting {dbName} sources from TIA and importing them into CWC...");
    }

    private async Task RunDbPushFromActiveToolbarAsync()
    {
        var dbName = NormalizeDbName(GetActiveTabKey());
        if (dbName == "DB DI")
        {
            DbDiTiaPush_Click(this, new RoutedEventArgs());
            return;
        }

        if (MessageBox.Show(this,
                $"Push {dbName} will generate {dbName} source files and import them into the mapped TIA PLC. Reset start values is skipped.",
                $"Push {dbName}",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDbFamilyTiaOperationAsync(
            dbName,
            $"Pushing {dbName} to TIA...",
            service => service.PushDbAsync(_project, dbName),
            rebindAfterSuccess: true,
            progressTitle: $"{dbName} TIA push",
            progressStatus: $"TIA Portal is minimized. Generating, importing and compiling {dbName}...",
            minimizeTiaPortal: true);
    }

    private string GetActiveTabKey()
    {
        return MainTabs.SelectedItem is TabItem tabItem ? GetTabKey(tabItem) : "";
    }

    private static FrameworkElement CreateTaggedButton(string tag)
    {
        return new Button { Tag = tag };
    }

    private async void ImportAllDbText_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SaveActivePlcDatabase();
            var changed = _dbTextWorkbookService.ImportAll(_project, dialog.FileName);
            changed += ApplyDescriptionTemplatesAndRefresh();
            SaveActivePlcDatabase();
            foreach (var dbName in new[] { "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR", "DB SEQUENCE" })
            {
                RefreshDbTextImportView(dbName);
            }

            await CheckTiaAfterImportAsync("DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR", "DB SEQUENCE");
            ShowMessage($"DB text workbook imported: {changed} cell(s) updated.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void ImportDbText_Click(object sender, RoutedEventArgs e)
    {
        if (!_projectOpen)
        {
            ShowMessage("No project is open.");
            return;
        }

        var dbName = GetDbNameFromSender(sender);

        var dialog = new OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            DefaultExt = "xlsx"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SaveActivePlcDatabase();
            var changed = _dbTextWorkbookService.Import(_project, dbName, dialog.FileName);
            changed += ApplyDescriptionTemplatesAndRefresh();
            SaveActivePlcDatabase();
            RefreshDbTextImportView(dbName);
            await CheckTiaAfterImportAsync(dbName);
            ShowMessage($"{dbName} text imported: {changed} cell(s) updated.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RefreshDbTextImportView(string dbName)
    {
        switch (NormalizeDbName(dbName))
        {
            case "DB DI":
                _dbDiView?.Refresh();
                DbDiGrid.Items.Refresh();
                LoadDbDiFilterItems();
                break;
            case "DB DO":
                _dbDoView?.Refresh();
                DbDoGrid.Items.Refresh();
                LoadDbDoFilterItems();
                break;
            case "DB MEAS":
                _dbMeasView?.Refresh();
                DbMeasGrid.Items.Refresh();
                LoadDbMeasFilterItems();
                break;
            case "DB AO":
                _dbAoRowsView?.Refresh();
                DbAoGrid.Items.Refresh();
                LoadDbAoFilterItems();
                break;
            case "DB ALM":
                _dbAlmView?.Refresh();
                DbAlmGrid.Items.Refresh();
                LoadDbAlmFilterItems();
                break;
            case "DB PAR":
                _dbParView?.Refresh();
                DbParGrid.Items.Refresh();
                LoadDbParFilterItems();
                break;
            case "DB SEQUENCE":
                UpdateSequenceReferenceOptions();
                _sequenceControlStatusView?.Refresh();
                _sequenceCommandView?.Refresh();
                SequenceControlStatusGrid.Items.Refresh();
                SequenceCommandGrid.Items.Refresh();
                LoadSequenceControlStatusFilterItems();
                LoadSequenceCommandFilterItems();
                break;
        }
    }

    private void ImportSequencePar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "TIA DB source (*.db)|*.db|All files (*.*)|*.*",
            FileName = "SEQ_PAR.db"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            RecordUndoSnapshot();
            var existingControlRows = _project.DbCatalog.Sequence.ControlStatusRows;
            var existingCommandRows = _project.DbCatalog.Sequence.CommandRows;
            var imported = _sequenceParser.Parse(dialog.FileName);
            imported.ControlStatusRows = existingControlRows;
            imported.CommandRows = existingCommandRows;
            imported.EnsureDefaults();
            _project.DbCatalog.Sequence = imported;
            RefreshSequenceXrefAndViews();
            SaveActivePlcDatabase();
            ShowMessage($"SEQ_PAR imported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RefreshSequenceXref_Click(object sender, RoutedEventArgs e)
    {
        RefreshSequenceXrefAndViews();
        ShowMessage("Sequence cross-reference counts refreshed from START/NSD/QSD/ESD configuration.");
    }

    private void RefreshSequenceXrefAndViews()
    {
        SequenceParDbParser.RefreshReferenceCounts(_project.DbCatalog.Sequence);
        BindSequenceViews();
        _sequenceControlStatusView?.Refresh();
        _sequenceCommandView?.Refresh();
        SequenceControlStatusGrid.Items.Refresh();
        SequenceCommandGrid.Items.Refresh();
    }

    private void UpdateSequenceReferenceOptions()
    {
        foreach (var view in new[] { SequenceStartView, SequenceNsdView, SequenceQsdView, SequenceEsdView })
        {
            view.ControlStatusRows = Array.Empty<DbStringRow>();
            view.CommandRows = Array.Empty<DbStringRow>();
            view.ControlStatusRows = _project.DbCatalog.Sequence.ControlStatusRows;
            view.CommandRows = _project.DbCatalog.Sequence.CommandRows;
        }
    }

    private void SequenceControlStatusInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DbStringRow row })
        {
            ShowSequenceReferenceInfo("Control status", row, isCommand: false);
        }
    }

    private void SequenceCommandInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DbStringRow row })
        {
            ShowSequenceReferenceInfo("Sequence command", row, isCommand: true);
        }
    }

    private void ConfigureSequenceControlStatusText_Click(object sender, RoutedEventArgs e)
    {
        ConfigureSequenceTextRow(sender, "Sequence control status", SequenceControlStatusGrid, UpdateSequenceControlStatusFilterItems);
    }

    private void ConfigureSequenceControlStatusPlcName_Click(object sender, RoutedEventArgs e)
    {
        ConfigureSequencePlcName(sender, "Sequence control status", SequenceControlStatusGrid, UpdateSequenceControlStatusFilterItems);
    }

    private void ConfigureSequenceCommandText_Click(object sender, RoutedEventArgs e)
    {
        ConfigureSequenceTextRow(sender, "Sequence command", SequenceCommandGrid, UpdateSequenceCommandFilterItems);
    }

    private void ConfigureSequenceCommandPlcName_Click(object sender, RoutedEventArgs e)
    {
        ConfigureSequencePlcName(sender, "Sequence command", SequenceCommandGrid, UpdateSequenceCommandFilterItems);
    }

    private void ConfigureSequenceTextRow(object sender, string title, DataGrid grid, Action refreshFilters)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[]
        {
            Field(nameof(DbStringRow.NameEnglish), "TEXT LIST NAME ENG", row.NameEnglish),
            Field(nameof(DbStringRow.NameOther), "TEXT LIST NAME OTHER", row.NameOther)
        };
        if (!ShowDbStringRowDialog(title, row, fields, DbTextLengthSettings.CreateDigitalInputDefaults(), "DB SEQUENCE"))
        {
            return;
        }

        refreshFilters();
        UpdateSequenceReferenceOptions();
        grid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"{title} text updated: ID {row.Id}.");
    }

    private void ConfigureSequencePlcName(object sender, string title, DataGrid grid, Action refreshFilters)
    {
        if (sender is not Button { DataContext: DbStringRow row })
        {
            return;
        }

        var fields = new[] { Field(nameof(DbStringRow.PlcName), "PLC NAME", row.PlcName) };
        if (!ShowDbStringRowDialog($"{title} PLC name", row, fields, DbTextLengthSettings.CreateParameterDefaults(), "DB SEQUENCE"))
        {
            return;
        }

        row.PlcStructure = "";
        refreshFilters();
        UpdateSequenceReferenceOptions();
        grid.Items.Refresh();
        MarkActiveTabTiaStatusDirty();
        ShowMessage($"{title} PLC name updated: ID {row.Id}.");
    }

    private void ShowSequenceReferenceInfo(string title, DbStringRow row, bool isCommand)
    {
        var idVariable = BuildSequenceCrossReferenceVariable(row, isCommand, "ID");
        var valVariable = BuildSequenceCrossReferenceVariable(row, isCommand, "VAL");
        var dialog = new SequenceCrossReferenceDialog(
            idVariable,
            valVariable,
            (member, selectedItem) => OpenSequenceCrossReferenceInTiaAsync(row, isCommand, member, selectedItem))
        {
            Owner = EditorOwner,
            Title = title + " info"
        };

        dialog.Loaded += async (_, _) => await LoadSequenceCrossReferenceAsync(dialog, row, isCommand);
        dialog.ShowDialog();
    }

    private string BuildSequenceCrossReferenceVariable(DbStringRow row, bool isCommand, string member)
    {
        var dataBlockName = GetSequenceReferenceDataBlockName(isCommand);
        return $"{dataBlockName}.{row.PlcName}.DATA.{member.ToUpperInvariant()}";
    }

    private string GetSequenceReferenceDataBlockName(bool isCommand)
    {
        var number = isCommand ? 7 : 6;
        var fallbackName = isCommand ? "SEQ_CMDs" : "CTRL_STATUS";
        var fallbackTokens = isCommand ? new[] { "SEQ_CMDS", "SEQ_CMD" } : new[] { "CTRL_STATUS", "CTRL_STS" };
        var row = _project.DbCatalog.DataBlockNames
            .Where(item => NormalizeDbName(item.DbName).Equals("DB SEQUENCE", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item => item.Number == number
                                    || fallbackTokens.Any(token => item.DataBlockName.Contains(token, StringComparison.OrdinalIgnoreCase)
                                                                   || item.FileName.Contains(token, StringComparison.OrdinalIgnoreCase)));
        if (row is null || string.IsNullOrWhiteSpace(row.DataBlockName))
        {
            return fallbackName;
        }

        var value = row.DataBlockName.Trim();
        if ((!isCommand && value.Equals("CTRL_STS", StringComparison.OrdinalIgnoreCase))
            || (isCommand && value.Equals("SEQ_CMD", StringComparison.OrdinalIgnoreCase)))
        {
            return fallbackName;
        }

        return value;
    }

    private TiaCrossReferenceRequest? CreateSequenceCrossReferenceRequest(DbStringRow row, bool isCommand, string member)
    {
        var binding = GetActiveTiaBinding();
        if (binding is null)
        {
            return null;
        }

        return new TiaCrossReferenceRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            VariablePath = BuildSequenceCrossReferenceVariable(row, isCommand, member),
            MaxItems = 80
        };
    }

    private async Task LoadSequenceCrossReferenceAsync(SequenceCrossReferenceDialog dialog, DbStringRow row, bool isCommand)
    {
        if (_tiaSessionService.CurrentSession is null)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Attach to TIA Portal first to scan cross-references.");
            dialog.SetCrossReferenceUnavailable("VAL", "Attach to TIA Portal first to scan cross-references.");
            return;
        }

        var idRequest = CreateSequenceCrossReferenceRequest(row, isCommand, "ID");
        var valRequest = CreateSequenceCrossReferenceRequest(row, isCommand, "VAL");
        if (idRequest is null || valRequest is null)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Active PLC database is not mapped to TIA.");
            dialog.SetCrossReferenceUnavailable("VAL", "Active PLC database is not mapped to TIA.");
            return;
        }

        dialog.SetCrossReferenceLoading("ID");
        dialog.SetCrossReferenceLoading("VAL");
        try
        {
            var idResult = await _tiaSessionService.GetCrossReferencesAsync(idRequest);
            dialog.SetCrossReferenceResult("ID", idResult);
            var valResult = await _tiaSessionService.GetCrossReferencesAsync(valRequest);
            dialog.SetCrossReferenceResult("VAL", valResult);
        }
        catch (Exception ex)
        {
            dialog.SetCrossReferenceUnavailable("ID", "Cross-reference scan failed: " + ex.Message);
            dialog.SetCrossReferenceUnavailable("VAL", "Cross-reference scan failed: " + ex.Message);
        }
    }

    private async Task OpenSequenceCrossReferenceInTiaAsync(DbStringRow row, bool isCommand, string member, TiaCrossReferenceItem? selectedItem)
    {
        var request = CreateSequenceCrossReferenceRequest(row, isCommand, member);
        if (_tiaSessionService.CurrentSession is null || request is null)
        {
            MessageBox.Show(this, "Attach and map TIA Portal first.", "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var result = await _tiaSessionService.OpenCrossReferenceAsync(new TiaCrossReferenceOpenRequest
            {
                PlcName = request.PlcName,
                DeviceName = request.DeviceName,
                SoftwareName = request.SoftwareName,
                VariablePath = request.VariablePath,
                PreferredBlockName = selectedItem?.BlockName ?? "",
                PreferredNetwork = selectedItem?.Network ?? ""
            });
            MessageBox.Show(this, result.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open TIA cross-reference: " + ex.Message, "TIA cross-reference", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CheckTiaAfterImportAsync(params string[] sections)
    {
        foreach (var section in sections)
        {
            MarkImportedSectionDiffers(section);
        }

        UpdateTiaVisualState();
        if (_tiaSessionService.CurrentSession is null)
        {
            return;
        }

        await RefreshAllTiaStatusAsync(showStatusMessage: true);
    }

    private void MarkImportedSectionDiffers(string section)
    {
        if (section is "DI" or "DO" or "AI" or "RTD" or "COUNTER")
        {
            MarkTiaSectionState("HW " + section, "Differs", $"{section} imported from Excel; TIA check required.");
            return;
        }

        if (section.StartsWith("DB ", StringComparison.OrdinalIgnoreCase))
        {
            MarkTiaSectionState(section, "Differs", $"{section} imported from Excel; TIA check required.");
        }
    }

    private bool FilterDbDiRow(object item)
    {
        return item is DbStringRow row
            && (!IsDbDiRepairViewActive() || row.DiNeedsRepair)
            && MatchesDbTextFilter(row, DbDiTextFilter.Text, GetDbDiFilterSpecs())
            && GetDbDiFilterSpecs().All(spec => MatchesDbDiFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterDbDoRow(object item)
    {
        return item is DbStringRow row
            && (!IsDbDoRepairViewActive() || row.DoNeedsRepair)
            && MatchesDbTextFilter(row, DbDoTextFilter.Text, GetDbDoFilterSpecs())
            && GetDbDoFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterDbMeasRow(object item)
    {
        return item is DbStringRow row
            && (!IsDbMeasRepairViewActive() || row.MeasNeedsRepair)
            && MatchesDbTextFilter(row, DbMeasTextFilter.Text, GetDbMeasFilterSpecs())
            && GetDbMeasFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterDbAoRow(object item)
    {
        return item is DbAoRow row
            && (!IsDbAoRepairViewActive() || row.AoNeedsRepair)
            && MatchesDbAoTextFilter(row, DbAoTextFilter.Text)
            && GetDbAoFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterDbAlmRow(object item)
    {
        return item is DbStringRow row
            && (!IsDbAlmRepairViewActive() || row.AlarmNeedsRepair)
            && MatchesDbTextFilter(row, DbAlmTextFilter.Text, GetDbAlmFilterSpecs())
            && GetDbAlmFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterDbParRow(object item)
    {
        return item is DbStringRow row
            && (!IsDbParRepairViewActive() || row.ParNeedsRepair)
            && MatchesDbTextFilter(row, DbParTextFilter.Text, GetDbParFilterSpecs())
            && GetDbParFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterSequenceControlStatusRow(object item)
    {
        return item is DbStringRow row
            && MatchesDbTextFilter(row, SequenceControlStatusTextFilter.Text, GetSequenceControlStatusFilterSpecs())
            && GetSequenceControlStatusFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool FilterSequenceCommandRow(object item)
    {
        return item is DbStringRow row
            && MatchesDbTextFilter(row, SequenceCommandTextFilter.Text, GetSequenceCommandFilterSpecs())
            && GetSequenceCommandFilterSpecs().All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private bool MatchesDbTextFilter(DbStringRow row, string? filterText, IEnumerable<DbFilterSpec> visibleSpecs)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        var needle = filterText.Trim();
        var values = visibleSpecs.Select(spec => spec.ValueSelector(row)).Concat(new[]
        {
            row.Id.ToString(),
            row.PlcStructure,
            row.PlcName,
            row.SystemNameEnglish,
            row.NameEnglish,
            row.SystemNameOther,
            row.NameOther,
            row.Unit,
            row.TrueText,
            row.ModuleName,
            row.ModulePort?.ToString() ?? string.Empty,
            row.ColorValue0,
            row.ColorValue1,
            row.AlarmConfig,
            row.AlarmInvert,
            row.AlarmId,
            row.ExtraColumn1,
            row.ExtraColumn2,
            row.LoggingEnable,
            row.LoggingTime,
            row.Type,
            row.AlarmControl,
            row.SrAlarm,
            row.AlarmClass,
            row.ReactToUnit,
            row.Spare,
            row.BlockingClass1,
            row.BlockingClass2,
            row.Delay,
            row.TimeBase,
            row.SourceType,
            row.SourceDigId,
            row.SourceAnaId,
            row.Used,
            row.InUse,
            row.ParameterType,
            row.IntegerValue,
            row.TimeValue,
            row.RealValue,
            row.MinValue,
            row.MaxValue,
            row.LimitLlEnabled.ToString(),
            row.LimitLoEnabled.ToString(),
            row.LimitHiEnabled.ToString(),
            row.LimitHhEnabled.ToString(),
            DisplayAlarmReference(row.AlarmErrId, "Id"),
            DisplayAlarmReference(row.AlarmErrId, "PlcStructure"),
            DisplayAlarmReference(row.AlarmErrId, "PlcName"),
            DisplayAlarmReference(row.AlarmLlId, "Id"),
            DisplayAlarmReference(row.AlarmLlId, "PlcStructure"),
            DisplayAlarmReference(row.AlarmLlId, "PlcName"),
            DisplayAlarmReference(row.AlarmLoId, "Id"),
            DisplayAlarmReference(row.AlarmLoId, "PlcStructure"),
            DisplayAlarmReference(row.AlarmLoId, "PlcName"),
            DisplayAlarmReference(row.AlarmHiId, "Id"),
            DisplayAlarmReference(row.AlarmHiId, "PlcStructure"),
            DisplayAlarmReference(row.AlarmHiId, "PlcName"),
            DisplayAlarmReference(row.AlarmHhId, "Id"),
            DisplayAlarmReference(row.AlarmHhId, "PlcStructure"),
            DisplayAlarmReference(row.AlarmHhId, "PlcName"),
            row.ParVal1Id,
            DisplayParameterReference(row.ParVal1Id, "PlcStructure"),
            DisplayParameterReference(row.ParVal1Id, "PlcName"),
            DisplayParameterReference(row.ParVal1Id, "Value"),
            row.ParVal2Id,
            DisplayParameterReference(row.ParVal2Id, "PlcStructure"),
            DisplayParameterReference(row.ParVal2Id, "PlcName"),
            DisplayParameterReference(row.ParVal2Id, "Value"),
            row.ParOffsetId,
            DisplayParameterReference(row.ParOffsetId, "PlcStructure"),
            DisplayParameterReference(row.ParOffsetId, "PlcName"),
            DisplayParameterReference(row.ParOffsetId, "Value"),
            row.ParScaleFactorId,
            DisplayParameterReference(row.ParScaleFactorId, "PlcStructure"),
            DisplayParameterReference(row.ParScaleFactorId, "PlcName"),
            DisplayParameterReference(row.ParScaleFactorId, "Value"),
            row.ParLlId,
            DisplayParameterReference(row.ParLlId, "PlcStructure"),
            DisplayParameterReference(row.ParLlId, "PlcName"),
            DisplayParameterReference(row.ParLlId, "Value"),
            row.ParLoId,
            DisplayParameterReference(row.ParLoId, "PlcStructure"),
            DisplayParameterReference(row.ParLoId, "PlcName"),
            DisplayParameterReference(row.ParLoId, "Value"),
            row.ParHiId,
            DisplayParameterReference(row.ParHiId, "PlcStructure"),
            DisplayParameterReference(row.ParHiId, "PlcName"),
            DisplayParameterReference(row.ParHiId, "Value"),
            row.ParHhId,
            DisplayParameterReference(row.ParHhId, "PlcStructure"),
            DisplayParameterReference(row.ParHhId, "PlcName"),
            DisplayParameterReference(row.ParHhId, "Value"),
            row.DescriptionEnglish,
            row.DescriptionOther,
            row.AlarmRepairSummary
        });

        return values.Any(value => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesDbAoTextFilter(DbAoRow row, string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        var needle = filterText.Trim();
        var values = new[]
        {
            row.AoSystemName,
            row.AoPlcName,
            row.AoNumber.ToString(),
            row.NameEnglish,
            row.NameOther,
            DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit),
            row.TrueText,
            row.ModuleName,
            row.ModulePort?.ToString() ?? string.Empty,
            row.Low.ToString(),
            row.High.ToString(),
            row.Offset.ToString(),
            row.HighLimit.ToString(),
            row.ParValLowId,
            DisplayParameterReference(row.ParValLowId, "PlcStructure"),
            DisplayParameterReference(row.ParValLowId, "PlcName"),
            DisplayParameterReference(row.ParValLowId, "Value"),
            row.ParValHighId,
            DisplayParameterReference(row.ParValHighId, "PlcStructure"),
            DisplayParameterReference(row.ParValHighId, "PlcName"),
            DisplayParameterReference(row.ParValHighId, "Value"),
            row.ParOffsetId,
            DisplayParameterReference(row.ParOffsetId, "PlcStructure"),
            DisplayParameterReference(row.ParOffsetId, "PlcName"),
            DisplayParameterReference(row.ParOffsetId, "Value"),
            row.ParScaleFactorId,
            DisplayParameterReference(row.ParScaleFactorId, "PlcStructure"),
            DisplayParameterReference(row.ParScaleFactorId, "PlcName"),
            DisplayParameterReference(row.ParScaleFactorId, "Value")
        };

        return values.Any(value => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadDbDiFilterItems()
    {
        _loadingDbDiFilters = true;
        foreach (var spec in GetDbDiFilterSpecs())
        {
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(_project.DbCatalog.DiRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), null);
        }

        _loadingDbDiFilters = false;
    }

    private void LoadDbDoFilterItems()
    {
        _loadingDbDoFilters = true;
        foreach (var spec in GetDbDoFilterSpecs())
        {
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(_project.DbCatalog.DoRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), null);
        }

        _loadingDbDoFilters = false;
    }

    private void LoadDbMeasFilterItems() => LoadDbFilterItems(GetDbMeasFilterSpecs(), _project.DbCatalog.MeasRows, ref _loadingDbMeasFilters);
    private void LoadDbAoFilterItems() => LoadDbAoFilterItems(_project.DbCatalog.AoRows);
    private void LoadDbAlmFilterItems() => LoadDbFilterItems(GetDbAlmFilterSpecs(), _project.DbCatalog.AlarmRows, ref _loadingDbAlmFilters);
    private void LoadDbParFilterItems() => LoadDbFilterItems(GetDbParFilterSpecs(), _project.DbCatalog.ParameterRows, ref _loadingDbParFilters);
    private void LoadSequenceControlStatusFilterItems() => LoadDbFilterItems(GetSequenceControlStatusFilterSpecs(), _project.DbCatalog.Sequence.ControlStatusRows, ref _loadingSequenceControlStatusFilters);
    private void LoadSequenceCommandFilterItems() => LoadDbFilterItems(GetSequenceCommandFilterSpecs(), _project.DbCatalog.Sequence.CommandRows, ref _loadingSequenceCommandFilters);

    private void LoadDbAoFilterItems(IEnumerable<DbAoRow> rows)
    {
        _loadingDbAoFilters = true;
        var rowList = rows.ToList();
        foreach (var spec in GetDbAoFilterSpecs())
        {
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(rowList.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), null);
        }

        _loadingDbAoFilters = false;
    }

    private static void LoadDbFilterItems(List<DbFilterSpec> specs, IEnumerable<DbStringRow> rows, ref bool loadingFlag)
    {
        loadingFlag = true;
        var rowList = rows.ToList();
        foreach (var spec in specs)
        {
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(rowList.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), null);
        }

        loadingFlag = false;
    }

    private void UpdateDbDiFilterItems()
    {
        _loadingDbDiFilters = true;
        foreach (var spec in GetDbDiFilterSpecs())
        {
            var currentValue = spec.Filter.SelectedItem as string;
            var availableRows = _project.DbCatalog.DiRows.Where(row => MatchesDbDiFiltersExcept(row, spec.Filter));
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(availableRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), currentValue);
        }

        _loadingDbDiFilters = false;
    }

    private void UpdateDbDoFilterItems()
    {
        _loadingDbDoFilters = true;
        foreach (var spec in GetDbDoFilterSpecs())
        {
            var currentValue = spec.Filter.SelectedItem as string;
            var availableRows = _project.DbCatalog.DoRows.Where(row => MatchesDbDoFiltersExcept(row, spec.Filter));
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(availableRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), currentValue);
        }

        _loadingDbDoFilters = false;
    }

    private void UpdateDbMeasFilterItems() => UpdateDbFilterItems(GetDbMeasFilterSpecs(), _project.DbCatalog.MeasRows, ref _loadingDbMeasFilters);
    private void UpdateDbAoFilterItems()
    {
        _loadingDbAoFilters = true;
        var specs = GetDbAoFilterSpecs();
        var rowList = _project.DbCatalog.AoRows.ToList();
        foreach (var spec in specs)
        {
            var currentValue = spec.Filter.SelectedItem as string;
            var availableRows = rowList.Where(row => specs.Where(other => !ReferenceEquals(other.Filter, spec.Filter)).All(other => MatchesDbFilter(other.Filter, other.ValueSelector(row))));
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(availableRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), currentValue);
        }

        _loadingDbAoFilters = false;
    }
    private void UpdateDbAlmFilterItems() => UpdateDbFilterItems(GetDbAlmFilterSpecs(), _project.DbCatalog.AlarmRows, ref _loadingDbAlmFilters);
    private void UpdateDbParFilterItems() => UpdateDbFilterItems(GetDbParFilterSpecs(), _project.DbCatalog.ParameterRows, ref _loadingDbParFilters);
    private void UpdateSequenceControlStatusFilterItems() => UpdateDbFilterItems(GetSequenceControlStatusFilterSpecs(), _project.DbCatalog.Sequence.ControlStatusRows, ref _loadingSequenceControlStatusFilters);
    private void UpdateSequenceCommandFilterItems() => UpdateDbFilterItems(GetSequenceCommandFilterSpecs(), _project.DbCatalog.Sequence.CommandRows, ref _loadingSequenceCommandFilters);

    private static void UpdateDbFilterItems(List<DbFilterSpec> specs, IEnumerable<DbStringRow> rows, ref bool loadingFlag)
    {
        loadingFlag = true;
        var rowList = rows.ToList();
        foreach (var spec in specs)
        {
            var currentValue = spec.Filter.SelectedItem as string;
            var availableRows = rowList.Where(row => specs.Where(other => !ReferenceEquals(other.Filter, spec.Filter)).All(other => MatchesDbFilter(other.Filter, other.ValueSelector(row))));
            SetDbFilterItems(spec.Filter, BuildDbFilterItems(availableRows.Select(spec.ValueSelector), spec.AllText, spec.SortAsNumber), currentValue);
        }

        loadingFlag = false;
    }

    private bool MatchesDbDiFiltersExcept(DbStringRow row, ComboBox excludedFilter)
    {
        return GetDbDiFilterSpecs()
            .Where(spec => !ReferenceEquals(spec.Filter, excludedFilter))
            .All(spec => MatchesDbDiFilter(spec.Filter, spec.ValueSelector(row)));
    }

    private static bool MatchesDbDiFilter(ComboBox filter, string value)
    {
        return MatchesDbFilter(filter, value);
    }

    private bool MatchesDbDoFiltersExcept(DbStringRow row, ComboBox excludedFilter)
    {
        return GetDbDoFilterSpecs()
            .Where(spec => !ReferenceEquals(spec.Filter, excludedFilter))
            .All(spec => MatchesDbFilter(spec.Filter, spec.ValueSelector(row)));
    }

    // Excel convention: a "(blank)" pseudo-item appears at the top of every
    // filter dropdown that has at least one empty cell in its column. Picking
    // it filters the grid to rows whose column value is empty/whitespace.
    private const string BlankFilterMarker = "(blank)";

    private static bool MatchesDbFilter(ComboBox filter, string value)
    {
        if (filter.SelectedIndex <= 0 || filter.Visibility != Visibility.Visible)
        {
            return true;
        }

        var selected = filter.SelectedItem as string;
        if (string.Equals(selected, BlankFilterMarker, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(value);
        }

        return string.Equals(selected, value, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildDbFilterItems(IEnumerable<string> values, string allText, bool sortAsNumber)
    {
        var valueList = values.ToList();
        var hasBlank = valueList.Any(value => string.IsNullOrWhiteSpace(value));

        var distinctValues = valueList
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var sortedValues = sortAsNumber
            ? distinctValues.OrderBy(value => int.TryParse(value, out var number) ? number : int.MaxValue).ThenBy(value => value).ToList()
            : distinctValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

        sortedValues.Insert(0, allText);
        if (hasBlank)
        {
            sortedValues.Insert(1, BlankFilterMarker);
        }
        return sortedValues;
    }

    private static void SetDbFilterItems(ComboBox filter, List<string> items, string? preferredValue)
    {
        filter.ItemsSource = items;
        filter.SelectedItem = preferredValue is not null && items.Contains(preferredValue, StringComparer.OrdinalIgnoreCase)
            ? preferredValue
            : items.FirstOrDefault();
    }

    private string DisplayValidation(string key, string value)
    {
        var rawValue = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "";
        }

        return GetValidationOptions(key).FirstOrDefault(option =>
                   option.Value.Equals(rawValue, StringComparison.OrdinalIgnoreCase)
                   || option.DisplayText.Equals(rawValue, StringComparison.OrdinalIgnoreCase)
                   || option.Text.Equals(rawValue, StringComparison.OrdinalIgnoreCase))?.Text
               ?? rawValue;
    }

    private string DisplayModuleName(string dbName, string value)
    {
        var rawValue = (value ?? "").Trim();
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleNumber))
        {
            return rawValue;
        }

        var rows = dbName.Equals("DI", StringComparison.OrdinalIgnoreCase)
            ? _project.ModuleCatalog.DigitalInputs
            : _project.ModuleCatalog.DigitalOutputs;
        var moduleName = rows.FirstOrDefault(row => row.ModuleNumber == moduleNumber)?.ModuleName;
        return string.IsNullOrWhiteSpace(moduleName) ? rawValue : moduleName;
    }

    private string DisplayAlarmReference(DbStringRow row, string field)
    {
        var rawAlarmId = (row.AlarmId ?? "").Trim();
        if (!IsEnabledNumber(row.AlarmConfig)
            || !int.TryParse(rawAlarmId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var alarmId)
            || alarmId < 0)
        {
            return "";
        }

        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return rawAlarmId;
        }

        var alarmRow = _project.DbCatalog.AlarmRows.FirstOrDefault(item => item.Id == alarmId);
        if (alarmRow is null)
        {
            return "";
        }

        return field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase)
            ? alarmRow.PlcStructure
            : field.Equals("PlcName", StringComparison.OrdinalIgnoreCase)
                ? alarmRow.PlcName
                : field.Equals("TechnicalName", StringComparison.OrdinalIgnoreCase)
                    ? $"{alarmRow.PlcStructure}.{alarmRow.PlcName}".TrimEnd('.')
                    : field.Equals("EnglishName", StringComparison.OrdinalIgnoreCase)
                    ? JoinNonEmpty(" - ", alarmRow.SystemNameEnglish, alarmRow.NameEnglish)
                    : "";
    }

    private string DisplayAlarmReference(string alarmId, string field)
    {
        var rawId = (alarmId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "";
        }

        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return rawId;
        }

        var row = _project.DbCatalog.AlarmRows.FirstOrDefault(item => item.Id == id);
        if (row is null)
        {
            return "";
        }

        return field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase)
            ? row.PlcStructure
            : field.Equals("PlcName", StringComparison.OrdinalIgnoreCase)
                ? row.PlcName
                : "";
    }

    private static string JoinNonEmpty(string separator, params string[] values)
    {
        return string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string DisplayParameterReference(string parameterId, string field)
    {
        var rawId = (parameterId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "";
        }

        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return rawId;
        }

        var row = _project.DbCatalog.ParameterRows.FirstOrDefault(item => item.Id == id);
        if (row is null)
        {
            return "";
        }

        if (field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase))
        {
            return row.PlcStructure;
        }

        if (field.Equals("PlcName", StringComparison.OrdinalIgnoreCase))
        {
            return row.PlcName;
        }

        if (field.Equals("Value", StringComparison.OrdinalIgnoreCase))
        {
            return (row.ParameterType ?? "").Trim() switch
            {
                "1" => row.IntegerValue,
                "2" => row.TimeValue,
                _ => row.RealValue
            };
        }

        return "";
    }

    private static string DisplayBooleanNumber(string value)
    {
        var rawValue = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "";
        }

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number == 0 ? "False" : "True";
        }

        return rawValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? "True"
            : rawValue.Equals("false", StringComparison.OrdinalIgnoreCase)
                ? "False"
                : rawValue;
    }

    private static bool IsEnabledNumber(string value)
    {
        var rawValue = (value ?? "").Trim();
        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return rawValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            || rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || rawValue.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private List<DbFilterSpec> GetDbDiFilterSpecs()
    {
        return
        [
            new(DbDiIdFilter, "All IDs", row => row.Id.ToString(), SortAsNumber: true),
            new(DbDiPlcStructureFilter, "All PLC structs", row => row.PlcStructure),
            new(DbDiPlcNameFilter, "All PLC definitions", row => row.PlcName),
            new(DbDiSystemEngFilter, "All system ENG", row => row.SystemNameEnglish),
            new(DbDiNameEngFilter, "All name ENG", row => row.NameEnglish),
            new(DbDiSystemOtherFilter, "All system other", row => row.SystemNameOther),
            new(DbDiNameOtherFilter, "All name other", row => row.NameOther),
            new(DbDiTrueFilter, "All used", row => row.TrueText),
            new(DbDiModuleNameFilter, "All modules", row => DisplayModuleName("DI", row.ModuleName)),
            new(DbDiModulePortFilter, "All ports", row => row.ModulePort?.ToString() ?? string.Empty, SortAsNumber: true),
            new(DbDiColor0Filter, "All color 0", row => DisplayValidation(DbValidationDefaults.DbDiColorValues, row.ColorValue0)),
            new(DbDiColor1Filter, "All color 1", row => DisplayValidation(DbValidationDefaults.DbDiColorValues, row.ColorValue1)),
            new(DbDiAlarmConfigFilter, "All alarm config", row => DisplayValidation(DbValidationDefaults.DbDiAlarmBoolean, row.AlarmConfig)),
            new(DbDiAlarmInvertFilter, "All alarm invert", row => DisplayValidation(DbValidationDefaults.DbDiAlarmBoolean, row.AlarmInvert)),
            new(DbDiAlarmIdFilter, "All alarm IDs", row => DisplayAlarmReference(row, "Id"), SortAsNumber: true),
            new(DbDiExtra1Filter, "All ALM PLC structs", row => DisplayAlarmReference(row, "PlcStructure")),
            new(DbDiExtra2Filter, "All ALM PLC names", row => DisplayAlarmReference(row, "PlcName")),
            new(DbDiXrefIdFilter, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbDiXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetDbDoFilterSpecs()
    {
        return
        [
            new(DbDoIdFilter, "All IDs", row => row.Id.ToString(), SortAsNumber: true),
            new(DbDoPlcStructureFilter, "All PLC structs", row => row.PlcStructure),
            new(DbDoPlcNameFilter, "All PLC definitions", row => row.PlcName),
            new(DbDoSystemEngFilter, "All system ENG", row => row.SystemNameEnglish),
            new(DbDoNameEngFilter, "All name ENG", row => row.NameEnglish),
            new(DbDoSystemOtherFilter, "All system other", row => row.SystemNameOther),
            new(DbDoNameOtherFilter, "All name other", row => row.NameOther),
            new(DbDoTrueFilter, "All used", row => row.TrueText),
            new(DbDoModuleNameFilter, "All modules", row => DisplayModuleName("DO", row.ModuleName)),
            new(DbDoModulePortFilter, "All ports", row => row.ModulePort?.ToString() ?? string.Empty, SortAsNumber: true),
            new(DbDoXrefIdFilter, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbDoXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetDbMeasFilterSpecs()
    {
        return
        [
            new(DbMeasPlcStructureFilter, "All PLC systems", row => row.PlcStructure),
            new(DbMeasIdFilter, "All IDs", row => row.Id.ToString(), SortAsNumber: true),
            new(DbMeasPlcNameFilter, "All PLC names", row => row.PlcName),
            new(DbMeasNameEngFilter, "All name ENG", row => row.NameEnglish),
            new(DbMeasNameOtherFilter, "All name other", row => row.NameOther),
            new(DbMeasUnitFilter, "All units", row => DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit)),
            new(DbMeasLoggingEnableFilter, "All logging", row => DisplayValidation(DbValidationDefaults.DbMeasLoggingEnable, row.LoggingEnable)),
            new(DbMeasLoggingTimeFilter, "All times", row => DisplayValidation(DbValidationDefaults.DbMeasLoggingTime, row.LoggingTime)),
            new(DbMeasTrueFilter, "All used", row => row.TrueText),
            new(DbMeasTypeFilter, "All types", row => row.Type),
            new(DbMeasModuleNameFilter, "All modules", row => FormatMeasModuleName(row)),
            new(DbMeasModulePortFilter, "All ports", row => row.ModulePort?.ToString() ?? string.Empty, SortAsNumber: true),
            new(DbMeasXrefMeasIdFilter, "All XREF MEAS_ID", row => row.CrossReferenceMeasIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbMeasXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetDbAlmFilterSpecs()
    {
        return
        [
            new(DbAlmUsedFilter, "All used", row => DisplayBooleanNumber(row.Used)),
            new(DbAlmPlcStructureFilter, "All PLC structs", row => row.PlcStructure),
            new(DbAlmIdFilter, "All IDs", row => row.Id.ToString(), SortAsNumber: true),
            new(DbAlmPlcNameFilter, "All PLC names", row => row.PlcName),
            new(DbAlmSystemEngFilter, "All system ENG", row => row.SystemNameEnglish),
            new(DbAlmNameEngFilter, "All text ENG", row => row.NameEnglish),
            new(DbAlmSystemOtherFilter, "All system other", row => row.SystemNameOther),
            new(DbAlmNameOtherFilter, "All text other", row => row.NameOther),
            new(DbAlmControlFilter, "All controls", row => DisplayValidation(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl)),
            new(DbAlmSrAlarmFilter, "All SR", row => DisplayValidation(DbValidationDefaults.DbAlmYesNo, row.SrAlarm)),
            new(DbAlmClassFilter, "All classes", row => DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass)),
            new(DbAlmReactToUnitFilter, "All react", row => DisplayValidation(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit)),
            new(DbAlmSpareFilter, "All spare", row => DisplayValidation(DbValidationDefaults.DbAlmAlarmClass, row.Spare)),
            new(DbAlmBlockingClass1Filter, "All blocking 1", row => DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1)),
            new(DbAlmBlockingClass2Filter, "All blocking 2", row => DisplayValidation(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2)),
            new(DbAlmDelayFilter, "All delays", row => row.Delay, SortAsNumber: true),
            new(DbAlmTimeBaseFilter, "All time base", row => DisplayValidation(DbValidationDefaults.DbAlmTimeBase, row.TimeBase)),
            new(DbAlmSourceTypeFilter, "All source types", row => row.SourceType),
            new(DbAlmSourceDigIdFilter, "All source DI", row => row.SourceDigId, SortAsNumber: true),
            new(DbAlmSourceAnaIdFilter, "All source AI", row => row.SourceAnaId, SortAsNumber: true),
            new(DbAlmDescriptionEnglishFilter, "All desc ENG", row => row.DescriptionEnglish),
            new(DbAlmDescriptionOtherFilter, "All desc other", row => row.DescriptionOther),
            new(DbAlmRepairFilter, "All repairs", row => row.AlarmRepairSummary),
            new(DbAlmXrefAlmIdFilter, "All XREF ALM_ID", row => row.CrossReferenceAlmIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbAlmXrefTempFilter, "All XREF TEMP", row => row.CrossReferenceTempCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbAlmXrefAlmFilter, "All XREF ALM", row => row.CrossReferenceAlmCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbAoFilterSpec> GetDbAoFilterSpecs()
    {
        return
        [
            new(DbAoUsedFilter, "All used", row => row.TrueText),
            new(DbAoNumberFilter, "All IDs", row => row.AoNumber.ToString(), SortAsNumber: true),
            new(DbAoSystemFilter, "All AO systems", row => row.AoSystemName),
            new(DbAoPlcNameFilter, "All PLC names", row => row.AoPlcName),
            new(DbAoNameEnglishFilter, "All name ENG", row => row.NameEnglish),
            new(DbAoNameOtherFilter, "All name other", row => row.NameOther),
            new(DbAoUnitFilter, "All units", row => DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit)),
            new(DbAoModuleNameFilter, "All modules", row => row.ModuleName),
            new(DbAoModulePortFilter, "All ports", row => row.ModulePort?.ToString() ?? string.Empty, SortAsNumber: true),
            new(DbAoParValLowIdFilter, "All low IDs", row => row.ParValLowId, SortAsNumber: true),
            new(DbAoParValLowStructFilter, "All low structs", row => DisplayParameterReference(row.ParValLowId, "PlcStructure")),
            new(DbAoParValLowNameFilter, "All low names", row => DisplayParameterReference(row.ParValLowId, "PlcName")),
            new(DbAoParValLowValueFilter, "All low values", row => DisplayParameterReference(row.ParValLowId, "Value")),
            new(DbAoParValHighIdFilter, "All high IDs", row => row.ParValHighId, SortAsNumber: true),
            new(DbAoParValHighStructFilter, "All high structs", row => DisplayParameterReference(row.ParValHighId, "PlcStructure")),
            new(DbAoParValHighNameFilter, "All high names", row => DisplayParameterReference(row.ParValHighId, "PlcName")),
            new(DbAoParValHighValueFilter, "All high values", row => DisplayParameterReference(row.ParValHighId, "Value")),
            new(DbAoParOffsetIdFilter, "All offset IDs", row => row.ParOffsetId, SortAsNumber: true),
            new(DbAoParOffsetStructFilter, "All offset structs", row => DisplayParameterReference(row.ParOffsetId, "PlcStructure")),
            new(DbAoParOffsetNameFilter, "All offset names", row => DisplayParameterReference(row.ParOffsetId, "PlcName")),
            new(DbAoParOffsetValueFilter, "All offset values", row => DisplayParameterReference(row.ParOffsetId, "Value")),
            new(DbAoParScaleIdFilter, "All scale IDs", row => row.ParScaleFactorId, SortAsNumber: true),
            new(DbAoParScaleStructFilter, "All scale structs", row => DisplayParameterReference(row.ParScaleFactorId, "PlcStructure")),
            new(DbAoParScaleNameFilter, "All scale names", row => DisplayParameterReference(row.ParScaleFactorId, "PlcName")),
            new(DbAoParScaleValueFilter, "All scale values", row => DisplayParameterReference(row.ParScaleFactorId, "Value")),
            new(DbAoXrefAnaCtrlIdFilter, "All XREF ANA_ID", row => row.CrossReferenceAnaCtrlIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbAoXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetDbParFilterSpecs()
    {
        return
        [
            new(DbParInUseFilter, "All in use", row => DisplayValidation(DbValidationDefaults.DbParInUse, row.InUse)),
            new(DbParIdFilter, "All IDs", row => row.Id.ToString(), SortAsNumber: true),
            new(DbParPlcStructureFilter, "All structs", row => row.PlcStructure),
            new(DbParPlcNameFilter, "All PLC names", row => row.PlcName),
            new(DbParNameEngFilter, "All name ENG", row => row.NameEnglish),
            new(DbParNameOtherFilter, "All name other", row => row.NameOther),
            new(DbParTypeFilter, "All types", row => DisplayValidation(DbValidationDefaults.DbParType, row.ParameterType)),
            new(DbParIntegerValueFilter, "All I_VAL", row => row.IntegerValue),
            new(DbParTimeValueFilter, "All T_VAL", row => row.TimeValue),
            new(DbParRealValueFilter, "All R_VAL", row => row.RealValue),
            new(DbParUnitFilter, "All units", row => DisplayValidation(DbValidationDefaults.DbMeasUnit, row.Unit)),
            new(DbParMinValueFilter, "All min", row => row.MinValue),
            new(DbParMaxValueFilter, "All max", row => row.MaxValue),
            new(DbParDescriptionEnglishFilter, "All desc ENG", row => row.DescriptionEnglish),
            new(DbParDescriptionOtherFilter, "All desc other", row => row.DescriptionOther),
            new(DbParXrefIdFilter, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbParXrefRValFilter, "All XREF R_VAL", row => row.CrossReferenceRValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbParXrefIValFilter, "All XREF I_VAL", row => row.CrossReferenceIValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(DbParXrefTValFilter, "All XREF T_VAL", row => row.CrossReferenceTValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetSequenceControlStatusFilterSpecs()
    {
        return
        [
            new(SequenceControlStatusIdFilter, "All IDs", row => row.Id.ToString(CultureInfo.InvariantCulture), SortAsNumber: true),
            new(SequenceControlStatusNameFilter, "All PLC names", row => row.PlcName),
            new(SequenceControlStatusEngFilter, "All ENG text list names", row => row.NameEnglish),
            new(SequenceControlStatusOtherFilter, "All other text list names", row => row.NameOther),
            new(SequenceControlStatusXrefIdFilter, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(SequenceControlStatusXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private List<DbFilterSpec> GetSequenceCommandFilterSpecs()
    {
        return
        [
            new(SequenceCommandIdFilter, "All IDs", row => row.Id.ToString(CultureInfo.InvariantCulture), SortAsNumber: true),
            new(SequenceCommandNameFilter, "All PLC names", row => row.PlcName),
            new(SequenceCommandEngFilter, "All ENG text list names", row => row.NameEnglish),
            new(SequenceCommandOtherFilter, "All other text list names", row => row.NameOther),
            new(SequenceCommandXrefIdFilter, "All XREF ID", row => row.CrossReferenceIdCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true),
            new(SequenceCommandXrefValFilter, "All XREF VAL", row => row.CrossReferenceValCount?.ToString(CultureInfo.InvariantCulture) ?? "", SortAsNumber: true)
        ];
    }

    private void SortDbDiById()
    {
        if (_dbDiView is null)
        {
            return;
        }

        _dbDiView.SortDescriptions.Clear();
        _dbDiView.SortDescriptions.Add(new SortDescription(nameof(DbStringRow.Id), ListSortDirection.Ascending));
    }

    private void SortDbDoById()
    {
        if (_dbDoView is null)
        {
            return;
        }

        _dbDoView.SortDescriptions.Clear();
        _dbDoView.SortDescriptions.Add(new SortDescription(nameof(DbStringRow.Id), ListSortDirection.Ascending));
    }

    private void SortDbMeasById() => SortDbViewById(_dbMeasView);
    private void SortDbAlmById() => SortDbViewById(_dbAlmView);
    private void SortDbParById() => SortDbViewById(_dbParView);
    private void SortSequenceControlStatusById() => SortDbViewById(_sequenceControlStatusView);
    private void SortSequenceCommandById() => SortDbViewById(_sequenceCommandView);

    private static void SortDbViewById(ICollectionView? view)
    {
        if (view is null)
        {
            return;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(DbStringRow.Id), ListSortDirection.Ascending));
    }

    private void ApplyDbDiColumnVisibility()
    {
        var selected = DbDiColumnSlicer.SelectedItems
            .OfType<ListBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = !selected.Contains("Nothing") && selected.Contains("Repair");
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showMapping = showAll || (!selected.Contains("Nothing") && selected.Contains("Mapping"));
        var showColoring = showAll || (!selected.Contains("Nothing") && selected.Contains("Coloring"));
        var showAlarmMapping = showAll || (!selected.Contains("Nothing") && selected.Contains("Alarm Mapping"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(showOtherLanguage, DbDiStatusSystemOtherColumn, DbDiStatusNameOtherColumn);
        SetColumnsVisibility(showMapping, DbDiModuleNameColumn, DbDiModulePortColumn);
        SetColumnsVisibility(showColoring, DbDiColor0Column, DbDiColor1Column);
        SetColumnsVisibility(showAlarmMapping, DbDiAlarmConfigColumn, DbDiAlarmInvertColumn, DbDiAlarmIdColumn, DbDiExtra1Column, DbDiExtra2Column);
        SetColumnsVisibility(showXref, DbDiXrefIdColumn, DbDiXrefValColumn);
        SetColumnsVisibility(showRepair, DbDiRepairColumn);
        SetControlsVisibility(showOtherLanguage, DbDiSystemOtherFilter, DbDiNameOtherFilter);
        SetControlsVisibility(showMapping, DbDiModuleNameFilter, DbDiModulePortFilter);
        SetControlsVisibility(showColoring, DbDiColor0Filter, DbDiColor1Filter);
        SetControlsVisibility(showAlarmMapping, DbDiAlarmConfigFilter, DbDiAlarmInvertFilter, DbDiAlarmIdFilter, DbDiExtra1Filter, DbDiExtra2Filter);
        SetControlsVisibility(showXref, DbDiXrefIdFilter, DbDiXrefValFilter);
        _dbDiView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbDiColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbDiRepairViewActive()
    {
        return DbDiColumnSlicer.SelectedItems
            .OfType<ListBoxItem>()
            .Any(item => string.Equals(item.Content?.ToString(), "Repair", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyDbDoColumnVisibility()
    {
        var selected = DbDoColumnSlicer.SelectedItems
            .OfType<ListBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = !selected.Contains("Nothing") && selected.Contains("Repair");
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showMapping = showAll || (!selected.Contains("Nothing") && selected.Contains("Mapping"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(showOtherLanguage, DbDoStatusSystemOtherColumn, DbDoStatusNameOtherColumn);
        SetColumnsVisibility(showMapping, DbDoModuleNameColumn, DbDoModulePortColumn);
        SetColumnsVisibility(showXref, DbDoXrefIdColumn, DbDoXrefValColumn);
        SetColumnsVisibility(showRepair, DbDoRepairColumn);
        SetControlsVisibility(showOtherLanguage, DbDoSystemOtherFilter, DbDoNameOtherFilter);
        SetControlsVisibility(showMapping, DbDoModuleNameFilter, DbDoModulePortFilter);
        SetControlsVisibility(showXref, DbDoXrefIdFilter, DbDoXrefValFilter);
        _dbDoView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbDoColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbDoRepairViewActive()
    {
        return DbDoColumnSlicer.SelectedItems
            .OfType<ListBoxItem>()
            .Any(item => string.Equals(item.Content?.ToString(), "Repair", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyDbMeasColumnVisibility()
    {
        var selected = SelectedSlicerItems(DbMeasColumnSlicer);
        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = !selected.Contains("Nothing") && selected.Contains("Repair");
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showSettings = showAll || (!selected.Contains("Nothing") && selected.Contains("Settings"));
        var showLimitConfiguration = showAll || (!selected.Contains("Nothing") && selected.Contains("Limit configuration"));
        var showParameterScaling = showAll || (!selected.Contains("Nothing") && selected.Contains("Parameter Scaling"));
        var showAlarmParameter = showAll || (!selected.Contains("Nothing") && selected.Contains("Alarm Parameters"));
        var showAlarmNumbers = showAll || (!selected.Contains("Nothing") && selected.Contains("Alarm numbers"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(showOtherLanguage, DbMeasNameOtherColumn);
        SetColumnsVisibility(showSettings, DbMeasUnitColumn, DbMeasLoggingEnableColumn, DbMeasLoggingTimeColumn, DbMeasTypeColumn, DbMeasModuleNameColumn, DbMeasModulePortColumn);
        SetColumnsVisibility(showLimitConfiguration, DbMeasLimitLlEnabledColumn, DbMeasLimitLoEnabledColumn, DbMeasLimitHiEnabledColumn, DbMeasLimitHhEnabledColumn);
        SetColumnsVisibility(showParameterScaling,
            DbMeasParVal1IdColumn, DbMeasParVal1StructColumn, DbMeasParVal1NameColumn, DbMeasParVal1ValueColumn,
            DbMeasParVal2IdColumn, DbMeasParVal2StructColumn, DbMeasParVal2NameColumn, DbMeasParVal2ValueColumn,
            DbMeasParOffsetIdColumn, DbMeasParOffsetStructColumn, DbMeasParOffsetNameColumn, DbMeasParOffsetValueColumn,
            DbMeasParScaleIdColumn, DbMeasParScaleStructColumn, DbMeasParScaleNameColumn, DbMeasParScaleValueColumn);
        SetColumnsVisibility(showAlarmParameter,
            DbMeasParLlIdColumn, DbMeasParLlStructColumn, DbMeasParLlNameColumn, DbMeasParLlValueColumn,
            DbMeasParLoIdColumn, DbMeasParLoStructColumn, DbMeasParLoNameColumn, DbMeasParLoValueColumn,
            DbMeasParHiIdColumn, DbMeasParHiStructColumn, DbMeasParHiNameColumn, DbMeasParHiValueColumn,
            DbMeasParHhIdColumn, DbMeasParHhStructColumn, DbMeasParHhNameColumn, DbMeasParHhValueColumn);
        SetColumnsVisibility(showAlarmNumbers,
            DbMeasAlarmErrIdColumn, DbMeasAlarmErrStructColumn, DbMeasAlarmErrNameColumn,
            DbMeasAlarmLlIdColumn, DbMeasAlarmLlStructColumn, DbMeasAlarmLlNameColumn,
            DbMeasAlarmLoIdColumn, DbMeasAlarmLoStructColumn, DbMeasAlarmLoNameColumn,
            DbMeasAlarmHiIdColumn, DbMeasAlarmHiStructColumn, DbMeasAlarmHiNameColumn,
            DbMeasAlarmHhIdColumn, DbMeasAlarmHhStructColumn, DbMeasAlarmHhNameColumn);
        SetColumnsVisibility(showXref, DbMeasXrefMeasIdColumn, DbMeasXrefValColumn);
        SetColumnsVisibility(showRepair, DbMeasRepairColumn);
        SetControlsVisibility(showOtherLanguage, DbMeasNameOtherFilter);
        SetControlsVisibility(showSettings, DbMeasUnitFilter, DbMeasLoggingEnableFilter, DbMeasLoggingTimeFilter, DbMeasTypeFilter, DbMeasModuleNameFilter, DbMeasModulePortFilter);
        SetControlsVisibility(showXref, DbMeasXrefMeasIdFilter, DbMeasXrefValFilter);
        _dbMeasView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbMeasColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbMeasRepairViewActive()
    {
        return SelectedSlicerItems(DbMeasColumnSlicer).Contains("Repair");
    }

    private void ApplyDbAoColumnVisibility()
    {
        var selected = SelectedSlicerItems(DbAoColumnSlicer);
        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = !selected.Contains("Nothing") && selected.Contains("Repair");
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showMapping = showAll || (!selected.Contains("Nothing") && selected.Contains("Mapping"));
        var showConfiguration = showAll || (!selected.Contains("Nothing") && selected.Contains("Configuration"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(showOtherLanguage, DbAoNameOtherColumn);
        SetColumnsVisibility(showMapping, DbAoModuleNameColumn, DbAoModulePortColumn);
        SetColumnsVisibility(showConfiguration,
            DbAoParValLowIdColumn, DbAoParValLowStructColumn, DbAoParValLowNameColumn, DbAoParValLowValueColumn,
            DbAoParValHighIdColumn, DbAoParValHighStructColumn, DbAoParValHighNameColumn, DbAoParValHighValueColumn,
            DbAoParOffsetIdColumn, DbAoParOffsetStructColumn, DbAoParOffsetNameColumn, DbAoParOffsetValueColumn,
            DbAoParScaleIdColumn, DbAoParScaleStructColumn, DbAoParScaleNameColumn, DbAoParScaleValueColumn);
        SetColumnsVisibility(showXref, DbAoXrefAnaCtrlIdColumn, DbAoXrefValColumn);
        SetColumnsVisibility(showRepair, DbAoRepairColumn);
        SetControlsVisibility(showOtherLanguage, DbAoNameOtherFilter);
        SetControlsVisibility(showMapping, DbAoModuleNameFilter, DbAoModulePortFilter);
        SetControlsVisibility(showConfiguration,
            DbAoParValLowIdFilter, DbAoParValLowStructFilter, DbAoParValLowNameFilter, DbAoParValLowValueFilter,
            DbAoParValHighIdFilter, DbAoParValHighStructFilter, DbAoParValHighNameFilter, DbAoParValHighValueFilter,
            DbAoParOffsetIdFilter, DbAoParOffsetStructFilter, DbAoParOffsetNameFilter, DbAoParOffsetValueFilter,
            DbAoParScaleIdFilter, DbAoParScaleStructFilter, DbAoParScaleNameFilter, DbAoParScaleValueFilter);
        SetControlsVisibility(showXref, DbAoXrefAnaCtrlIdFilter, DbAoXrefValFilter);
        _dbAoRowsView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbAoColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbAoRepairViewActive()
    {
        return SelectedSlicerItems(DbAoColumnSlicer).Contains("Repair");
    }

    private void ApplyDbAlmColumnVisibility()
    {
        var selected = SelectedSlicerItems(DbAlmColumnSlicer);
        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = IsDbAlmRepairViewActive(selected);
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showSettings = showAll || (!selected.Contains("Nothing") && selected.Contains("Settings"));
        var showSource = showAll || (!selected.Contains("Nothing") && selected.Contains("Source"));
        var showDescription = showAll || (!selected.Contains("Nothing") && selected.Contains("Description"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(!showRepair, DbAlmRepairMarkerColumn);
        SetColumnsVisibility(showRepair, DbAlmRepairColumn);
        SetColumnsVisibility(showOtherLanguage, DbAlmSystemOtherColumn, DbAlmNameOtherColumn);
        SetColumnsVisibility(showSettings, DbAlmControlColumn, DbAlmSrAlarmColumn, DbAlmClassColumn, DbAlmReactToUnitColumn, DbAlmSpareColumn, DbAlmBlockingClass1Column, DbAlmBlockingClass2Column, DbAlmDelayColumn, DbAlmTimeBaseColumn);
        SetColumnsVisibility(showSource, DbAlmSourceTypeColumn, DbAlmSourceDigIdColumn, DbAlmSourceAnaIdColumn);
        SetColumnsVisibility(showDescription, DbAlmDescriptionEnglishColumn, DbAlmDescriptionOtherColumn);
        SetColumnsVisibility(showXref, DbAlmXrefAlmIdColumn, DbAlmXrefTempColumn, DbAlmXrefAlmColumn);
        SetControlsVisibility(!showRepair, DbAlmRepairMarkerFilterSpacer);
        SetControlsVisibility(showRepair, DbAlmRepairFilter);
        SetControlsVisibility(showOtherLanguage, DbAlmSystemOtherFilter, DbAlmNameOtherFilter);
        SetControlsVisibility(showSettings, DbAlmControlFilter, DbAlmSrAlarmFilter, DbAlmClassFilter, DbAlmReactToUnitFilter, DbAlmSpareFilter, DbAlmBlockingClass1Filter, DbAlmBlockingClass2Filter, DbAlmDelayFilter, DbAlmTimeBaseFilter);
        SetControlsVisibility(showSource, DbAlmSourceTypeFilter, DbAlmSourceDigIdFilter, DbAlmSourceAnaIdFilter);
        SetControlsVisibility(showDescription, DbAlmDescriptionEnglishFilter, DbAlmDescriptionOtherFilter);
        SetControlsVisibility(showXref, DbAlmXrefAlmIdFilter, DbAlmXrefTempFilter, DbAlmXrefAlmFilter);
        _dbAlmView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbAlmColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbAlmRepairViewActive()
    {
        return IsDbAlmRepairViewActive(SelectedSlicerItems(DbAlmColumnSlicer));
    }

    private static bool IsDbAlmRepairViewActive(HashSet<string> selected)
    {
        return selected.Contains("Repair") && !selected.Contains("Nothing");
    }

    private void ApplyDbParColumnVisibility()
    {
        var selected = SelectedSlicerItems(DbParColumnSlicer);
        var showAll = selected.Count == 0 || (selected.Contains("All") && !selected.Contains("Nothing"));
        var showRepair = !selected.Contains("Nothing") && selected.Contains("Repair");
        var showOtherLanguage = showAll || (!selected.Contains("Nothing") && selected.Contains("Other language"));
        var showSettings = showAll || (!selected.Contains("Nothing") && selected.Contains("Settings"));
        var showDescription = showAll || (!selected.Contains("Nothing") && selected.Contains("Description"));
        var showXref = showAll || (!selected.Contains("Nothing") && selected.Contains("XRef"));

        SetColumnsVisibility(showOtherLanguage, DbParNameOtherColumn);
        SetColumnsVisibility(showSettings, DbParTypeColumn, DbParIntegerValueColumn, DbParTimeValueColumn, DbParRealValueColumn, DbParUnitColumn, DbParMinValueColumn, DbParMaxValueColumn);
        SetColumnsVisibility(showDescription, DbParDescriptionEnglishColumn, DbParDescriptionOtherColumn);
        SetColumnsVisibility(showXref, DbParXrefIdColumn, DbParXrefRValColumn, DbParXrefIValColumn, DbParXrefTValColumn);
        SetColumnsVisibility(showRepair, DbParRepairColumn);
        SetControlsVisibility(showOtherLanguage, DbParNameOtherFilter);
        SetControlsVisibility(showSettings, DbParTypeFilter, DbParIntegerValueFilter, DbParTimeValueFilter, DbParRealValueFilter, DbParUnitFilter, DbParMinValueFilter, DbParMaxValueFilter);
        SetControlsVisibility(showDescription, DbParDescriptionEnglishFilter, DbParDescriptionOtherFilter);
        SetControlsVisibility(showXref, DbParXrefIdFilter, DbParXrefRValFilter, DbParXrefIValFilter, DbParXrefTValFilter);
        _dbParView?.Refresh();
        Dispatcher.BeginInvoke(ResizeDbParColumnsToViewport, DispatcherPriority.Background);
    }

    private bool IsDbParRepairViewActive()
    {
        return SelectedSlicerItems(DbParColumnSlicer).Contains("Repair");
    }

    private void ApplySequenceColumnVisibility()
    {
        _sequenceControlStatusView?.Refresh();
        _sequenceCommandView?.Refresh();
        Dispatcher.BeginInvoke(ResizeSequenceControlStatusColumnsToViewport, DispatcherPriority.Background);
        Dispatcher.BeginInvoke(ResizeSequenceCommandColumnsToViewport, DispatcherPriority.Background);
    }

    private static HashSet<string> SelectedSlicerItems(ListBox slicer)
    {
        return slicer.SelectedItems
            .OfType<ListBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureDefaultSlicerSelection(ListBox slicer, string defaultItem)
    {
        if (slicer.SelectedItems.Count > 0)
        {
            return;
        }

        var item = slicer.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(listItem => string.Equals(listItem.Content?.ToString(), defaultItem, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private static void SetColumnsVisibility(bool isVisible, params DataGridColumn[] columns)
    {
        var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in columns)
        {
            column.Visibility = visibility;
        }
    }

    private static void SetControlsVisibility(bool isVisible, params UIElement[] elements)
    {
        var visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in elements)
        {
            element.Visibility = visibility;
        }
    }

    private void ResizeDbDiColumnsToViewport()
    {
        ResizeColumnsToViewport(DbDiGrid, GetDbDiColumnLayouts());
    }

    private void ResizeDbDoColumnsToViewport()
    {
        ResizeColumnsToViewport(DbDoGrid, GetDbDoColumnLayouts());
    }

    private void ResizeDbMeasColumnsToViewport()
    {
        ResizeColumnsToViewport(DbMeasGrid, GetDbMeasColumnLayouts());
    }

    private void ResizeDbAlmColumnsToViewport()
    {
        ResizeColumnsToViewport(DbAlmGrid, GetDbAlmColumnLayouts());
    }

    private void ResizeDbParColumnsToViewport()
    {
        ResizeColumnsToViewport(DbParGrid, GetDbParColumnLayouts());
    }

    private void ResizeDbAoColumnsToViewport()
    {
        ResizeColumnsToViewport(DbAoGrid, GetDbAoColumnLayouts());
    }

    private void ResizeSequenceControlStatusColumnsToViewport()
    {
        ResizeColumnsToViewport(SequenceControlStatusGrid, GetSequenceControlStatusColumnLayouts());
    }

    private void ResizeSequenceCommandColumnsToViewport()
    {
        ResizeColumnsToViewport(SequenceCommandGrid, GetSequenceCommandColumnLayouts());
    }

    private static void ResizeColumnsToViewport(DataGrid grid, IReadOnlyList<ColumnFilterLayout> layouts)
    {
        if (grid.ActualWidth <= 0)
        {
            return;
        }

        var visibleLayouts = layouts
            .Where(layout => layout.Column.Visibility == Visibility.Visible)
            .ToList();

        if (visibleLayouts.Count == 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, grid.ActualWidth - 24);
        var baseWidth = visibleLayouts.Sum(layout => layout.BaseWidth);
        var stretchLayouts = visibleLayouts.Where(layout => layout.Stretch).ToList();
        var extraPerStretchColumn = stretchLayouts.Count > 0
            ? Math.Max(0, availableWidth - baseWidth) / stretchLayouts.Count
            : 0;

        foreach (var layout in visibleLayouts)
        {
            var width = layout.BaseWidth + (layout.Stretch ? extraPerStretchColumn : 0);
            layout.Column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
            layout.FilterElement.Width = width;
        }
    }

    private static void SyncFilterWidthsToColumns(IReadOnlyList<ColumnFilterLayout> layouts)
    {
        foreach (var layout in layouts)
        {
            layout.FilterElement.Visibility = layout.Column.Visibility;
            var width = layout.Column.ActualWidth;
            if (width > 0 && Math.Abs(layout.FilterElement.Width - width) > 0.5)
            {
                layout.FilterElement.Width = width;
            }
        }

        foreach (var group in layouts.GroupBy(layout => layout.FilterElement.Parent))
        {
            if (group.Key is not Panel panel)
            {
                continue;
            }

            var ordered = group
                .OrderBy(layout => layout.Column.DisplayIndex)
                .Select(layout => layout.FilterElement)
                .ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                if (panel.Children.Contains(ordered[index]) && panel.Children.IndexOf(ordered[index]) != index)
                {
                    panel.Children.Remove(ordered[index]);
                    panel.Children.Insert(Math.Min(index, panel.Children.Count), ordered[index]);
                }
            }
        }
    }

    private static void ResizeDataGridColumnsToViewport(DataGrid grid)
    {
        if (grid.ActualWidth <= 0)
        {
            return;
        }

        var visibleColumns = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .ToList();
        if (visibleColumns.Count == 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, grid.ActualWidth - 24);
        var columnWidth = Math.Max(55, availableWidth / visibleColumns.Count);
        foreach (var column in visibleColumns)
        {
            column.Width = new DataGridLength(columnWidth, DataGridLengthUnitType.Pixel);
        }
    }

    private List<ColumnFilterLayout> GetDbDiColumnLayouts()
    {
        return
        [
            new(DbDiTrueColumn, DbDiTrueFilter, 70, false),
            new(DbDiIdColumn, DbDiIdFilter, 60, false),
            new(DbDiInfoColumn, DbDiInfoFilterSpacer, 52, false),
            new(DbDiRowConfigColumn, DbDiRowConfigFilterSpacer, 52, false),
            new(DbDiPlcStructureColumn, DbDiPlcStructureFilter, 170, true),
            new(DbDiPlcConfigColumn, DbDiPlcConfigFilterSpacer, 52, false),
            new(DbDiPlcNameColumn, DbDiPlcNameFilter, 190, true),
            new(DbDiStatusSystemEngColumn, DbDiSystemEngFilter, 160, true),
            new(DbDiStatusNameEngColumn, DbDiNameEngFilter, 160, true),
            new(DbDiStatusSystemOtherColumn, DbDiSystemOtherFilter, 160, true),
            new(DbDiStatusNameOtherColumn, DbDiNameOtherFilter, 160, true),
            new(DbDiModuleNameColumn, DbDiModuleNameFilter, 110, true),
            new(DbDiModulePortColumn, DbDiModulePortFilter, 80, false),
            new(DbDiColor0Column, DbDiColor0Filter, 90, false),
            new(DbDiColor1Column, DbDiColor1Filter, 90, false),
            new(DbDiAlarmConfigColumn, DbDiAlarmConfigFilter, 90, false),
            new(DbDiAlarmInvertColumn, DbDiAlarmInvertFilter, 90, false),
            new(DbDiAlarmIdColumn, DbDiAlarmIdFilter, 80, false),
            new(DbDiExtra1Column, DbDiExtra1Filter, 80, false),
            new(DbDiExtra2Column, DbDiExtra2Filter, 80, false),
            new(DbDiXrefIdColumn, DbDiXrefIdFilter, 80, false),
            new(DbDiXrefValColumn, DbDiXrefValFilter, 80, false)
        ];
    }

    private List<ColumnFilterLayout> GetDbDoColumnLayouts()
    {
        return
        [
            new(DbDoTrueColumn, DbDoTrueFilter, 70, false),
            new(DbDoIdColumn, DbDoIdFilter, 60, false),
            new(DbDoInfoColumn, DbDoInfoFilterSpacer, 52, false),
            new(DbDoRowConfigColumn, DbDoRowConfigFilterSpacer, 52, false),
            new(DbDoPlcStructureColumn, DbDoPlcStructureFilter, 170, true),
            new(DbDoPlcConfigColumn, DbDoPlcConfigFilterSpacer, 52, false),
            new(DbDoPlcNameColumn, DbDoPlcNameFilter, 190, true),
            new(DbDoStatusSystemEngColumn, DbDoSystemEngFilter, 160, true),
            new(DbDoStatusNameEngColumn, DbDoNameEngFilter, 160, true),
            new(DbDoStatusSystemOtherColumn, DbDoSystemOtherFilter, 160, true),
            new(DbDoStatusNameOtherColumn, DbDoNameOtherFilter, 160, true),
            new(DbDoModuleNameColumn, DbDoModuleNameFilter, 110, true),
            new(DbDoModulePortColumn, DbDoModulePortFilter, 80, false),
            new(DbDoXrefIdColumn, DbDoXrefIdFilter, 80, false),
            new(DbDoXrefValColumn, DbDoXrefValFilter, 80, false)
        ];
    }

    private List<ColumnFilterLayout> GetDbMeasColumnLayouts()
    {
        return
        [
            new(DbMeasTrueColumn, DbMeasTrueFilter, 70, false),
            new(DbMeasIdColumn, DbMeasIdFilter, 60, false),
            new(DbMeasInfoColumn, DbMeasInfoFilterSpacer, 52, false),
            new(DbMeasRowConfigColumn, DbMeasRowConfigFilterSpacer, 52, false),
            new(DbMeasPlcStructureColumn, DbMeasPlcStructureFilter, 170, true),
            new(DbMeasPlcConfigColumn, DbMeasPlcConfigFilterSpacer, 52, false),
            new(DbMeasPlcNameColumn, DbMeasPlcNameFilter, 190, true),
            new(DbMeasNameEngColumn, DbMeasNameEngFilter, 160, true),
            new(DbMeasNameOtherColumn, DbMeasNameOtherFilter, 160, true),
            new(DbMeasUnitColumn, DbMeasUnitFilter, 80, false),
            new(DbMeasLoggingEnableColumn, DbMeasLoggingEnableFilter, 110, false),
            new(DbMeasLoggingTimeColumn, DbMeasLoggingTimeFilter, 110, false),
            new(DbMeasTypeColumn, DbMeasTypeFilter, 80, false),
            new(DbMeasModuleNameColumn, DbMeasModuleNameFilter, 110, true),
            new(DbMeasModulePortColumn, DbMeasModulePortFilter, 90, false),
            new(DbMeasXrefMeasIdColumn, DbMeasXrefMeasIdFilter, 95, false),
            new(DbMeasXrefValColumn, DbMeasXrefValFilter, 80, false)
        ];
    }

    private List<ColumnFilterLayout> GetDbAoColumnLayouts()
    {
        return
        [
            new(DbAoTrueColumn, DbAoUsedFilter, 90, false),
            new(DbAoNumberColumn, DbAoNumberFilter, 70, false),
            new(DbAoInfoColumn, DbAoInfoFilterSpacer, 52, false),
            new(DbAoRowConfigColumn, DbAoRowConfigFilterSpacer, 52, false),
            new(DbAoSystemColumn, DbAoSystemFilter, 170, true),
            new(DbAoNameConfigColumn, DbAoNameConfigFilterSpacer, 52, false),
            new(DbAoPlcNameColumn, DbAoPlcNameFilter, 170, true),
            new(DbAoNameEnglishColumn, DbAoNameEnglishFilter, 160, true),
            new(DbAoNameOtherColumn, DbAoNameOtherFilter, 160, true),
            new(DbAoUnitColumn, DbAoUnitFilter, 90, false),
            new(DbAoModuleNameColumn, DbAoModuleNameFilter, 120, true),
            new(DbAoModulePortColumn, DbAoModulePortFilter, 90, false),
            new(DbAoParValLowIdColumn, DbAoParValLowIdFilter, 110, false),
            new(DbAoParValLowStructColumn, DbAoParValLowStructFilter, 170, true),
            new(DbAoParValLowNameColumn, DbAoParValLowNameFilter, 170, true),
            new(DbAoParValLowValueColumn, DbAoParValLowValueFilter, 100, false),
            new(DbAoParValHighIdColumn, DbAoParValHighIdFilter, 110, false),
            new(DbAoParValHighStructColumn, DbAoParValHighStructFilter, 170, true),
            new(DbAoParValHighNameColumn, DbAoParValHighNameFilter, 170, true),
            new(DbAoParValHighValueColumn, DbAoParValHighValueFilter, 100, false),
            new(DbAoParOffsetIdColumn, DbAoParOffsetIdFilter, 110, false),
            new(DbAoParOffsetStructColumn, DbAoParOffsetStructFilter, 170, true),
            new(DbAoParOffsetNameColumn, DbAoParOffsetNameFilter, 170, true),
            new(DbAoParOffsetValueColumn, DbAoParOffsetValueFilter, 100, false),
            new(DbAoParScaleIdColumn, DbAoParScaleIdFilter, 110, false),
            new(DbAoParScaleStructColumn, DbAoParScaleStructFilter, 170, true),
            new(DbAoParScaleNameColumn, DbAoParScaleNameFilter, 170, true),
            new(DbAoParScaleValueColumn, DbAoParScaleValueFilter, 100, false),
            new(DbAoXrefAnaCtrlIdColumn, DbAoXrefAnaCtrlIdFilter, 110, false),
            new(DbAoXrefValColumn, DbAoXrefValFilter, 80, false)
        ];
    }

    private List<ColumnFilterLayout> GetDbAlmColumnLayouts()
    {
        return
        [
            new(DbAlmRepairMarkerColumn, DbAlmRepairMarkerFilterSpacer, 8, false),
            new(DbAlmUsedColumn, DbAlmUsedFilter, 70, false),
            new(DbAlmIdColumn, DbAlmIdFilter, 70, false),
            new(DbAlmInfoColumn, DbAlmInfoFilterSpacer, 52, false),
            new(DbAlmRowConfigColumn, DbAlmRowConfigFilterSpacer, 52, false),
            new(DbAlmPlcStructureColumn, DbAlmPlcStructureFilter, 170, true),
            new(DbAlmPlcConfigColumn, DbAlmPlcConfigFilterSpacer, 52, false),
            new(DbAlmPlcNameColumn, DbAlmPlcNameFilter, 170, true),
            new(DbAlmSystemEngColumn, DbAlmSystemEngFilter, 160, true),
            new(DbAlmNameEngColumn, DbAlmNameEngFilter, 160, true),
            new(DbAlmSystemOtherColumn, DbAlmSystemOtherFilter, 160, true),
            new(DbAlmNameOtherColumn, DbAlmNameOtherFilter, 160, true),
            new(DbAlmControlColumn, DbAlmControlFilter, 95, false),
            new(DbAlmSrAlarmColumn, DbAlmSrAlarmFilter, 85, false),
            new(DbAlmClassColumn, DbAlmClassFilter, 120, true),
            new(DbAlmReactToUnitColumn, DbAlmReactToUnitFilter, 105, false),
            new(DbAlmSpareColumn, DbAlmSpareFilter, 70, false),
            new(DbAlmBlockingClass1Column, DbAlmBlockingClass1Filter, 120, true),
            new(DbAlmBlockingClass2Column, DbAlmBlockingClass2Filter, 120, true),
            new(DbAlmDelayColumn, DbAlmDelayFilter, 70, false),
            new(DbAlmTimeBaseColumn, DbAlmTimeBaseFilter, 80, false),
            new(DbAlmSourceTypeColumn, DbAlmSourceTypeFilter, 100, false),
            new(DbAlmSourceDigIdColumn, DbAlmSourceDigIdFilter, 90, false),
            new(DbAlmSourceAnaIdColumn, DbAlmSourceAnaIdFilter, 90, false),
            new(DbAlmDescriptionEnglishColumn, DbAlmDescriptionEnglishFilter, 160, true),
            new(DbAlmDescriptionOtherColumn, DbAlmDescriptionOtherFilter, 160, true),
            new(DbAlmRepairColumn, DbAlmRepairFilter, 160, true),
            new(DbAlmXrefAlmIdColumn, DbAlmXrefAlmIdFilter, 95, false),
            new(DbAlmXrefTempColumn, DbAlmXrefTempFilter, 90, false),
            new(DbAlmXrefAlmColumn, DbAlmXrefAlmFilter, 90, false)
        ];
    }

    private List<ColumnFilterLayout> GetDbParColumnLayouts()
    {
        return
        [
            new(DbParInUseColumn, DbParInUseFilter, 70, false),
            new(DbParIdColumn, DbParIdFilter, 70, false),
            new(DbParInfoColumn, DbParInfoFilterSpacer, 52, false),
            new(DbParRowConfigColumn, DbParRowConfigFilterSpacer, 52, false),
            new(DbParPlcStructureColumn, DbParPlcStructureFilter, 170, true),
            new(DbParPlcConfigColumn, DbParPlcConfigFilterSpacer, 52, false),
            new(DbParPlcNameColumn, DbParPlcNameFilter, 190, true),
            new(DbParNameEngColumn, DbParNameEngFilter, 160, true),
            new(DbParNameOtherColumn, DbParNameOtherFilter, 160, true),
            new(DbParTypeColumn, DbParTypeFilter, 90, false),
            new(DbParIntegerValueColumn, DbParIntegerValueFilter, 70, false),
            new(DbParTimeValueColumn, DbParTimeValueFilter, 80, false),
            new(DbParRealValueColumn, DbParRealValueFilter, 70, false),
            new(DbParUnitColumn, DbParUnitFilter, 70, false),
            new(DbParMinValueColumn, DbParMinValueFilter, 80, false),
            new(DbParMaxValueColumn, DbParMaxValueFilter, 80, false),
            new(DbParDescriptionEnglishColumn, DbParDescriptionEnglishFilter, 160, true),
            new(DbParDescriptionOtherColumn, DbParDescriptionOtherFilter, 160, true),
            new(DbParXrefIdColumn, DbParXrefIdFilter, 80, false),
            new(DbParXrefRValColumn, DbParXrefRValFilter, 90, false),
            new(DbParXrefIValColumn, DbParXrefIValFilter, 90, false),
            new(DbParXrefTValColumn, DbParXrefTValFilter, 90, false)
        ];
    }

    private List<ColumnFilterLayout> GetSequenceControlStatusColumnLayouts()
    {
        return
        [
            new(SequenceControlStatusIdColumn, SequenceControlStatusIdFilter, 70, false),
            new(SequenceControlStatusInfoColumn, SequenceControlStatusInfoFilterSpacer, 52, false),
            new(SequenceControlStatusTextConfigColumn, SequenceControlStatusTextConfigFilterSpacer, 52, false),
            new(SequenceControlStatusPlcConfigColumn, SequenceControlStatusPlcConfigFilterSpacer, 52, false),
            new(SequenceControlStatusNameColumn, SequenceControlStatusNameFilter, 190, true),
            new(SequenceControlStatusEngColumn, SequenceControlStatusEngFilter, 190, true),
            new(SequenceControlStatusOtherColumn, SequenceControlStatusOtherFilter, 190, true),
            new(SequenceControlStatusXrefIdColumn, SequenceControlStatusXrefIdFilter, 90, false),
            new(SequenceControlStatusXrefValColumn, SequenceControlStatusXrefValFilter, 90, false)
        ];
    }

    private List<ColumnFilterLayout> GetSequenceCommandColumnLayouts()
    {
        return
        [
            new(SequenceCommandIdColumn, SequenceCommandIdFilter, 70, false),
            new(SequenceCommandInfoColumn, SequenceCommandInfoFilterSpacer, 52, false),
            new(SequenceCommandTextConfigColumn, SequenceCommandTextConfigFilterSpacer, 52, false),
            new(SequenceCommandPlcConfigColumn, SequenceCommandPlcConfigFilterSpacer, 52, false),
            new(SequenceCommandNameColumn, SequenceCommandNameFilter, 190, true),
            new(SequenceCommandEngColumn, SequenceCommandEngFilter, 190, true),
            new(SequenceCommandOtherColumn, SequenceCommandOtherFilter, 190, true),
            new(SequenceCommandXrefIdColumn, SequenceCommandXrefIdFilter, 90, false),
            new(SequenceCommandXrefValColumn, SequenceCommandXrefValFilter, 90, false)
        ];
    }

    private void ResetAutoFillState()
    {
        if (_fillGrid is not null && ReferenceEquals(Mouse.Captured, _fillGrid))
        {
            Mouse.Capture(null);
        }

        DisposeDragFillAdorner();

        _fillGrid = null;
        _fillSourceRow = null;
        _fillTargetRow = null;
        _fillColumn = null;
        _fillProperty = null;
        _fillValue = null;
        _fillTemplateKey = null;
        _fillDragging = false;
    }

    private void FillDbGridRows(DataGrid grid, object sourceRow, object targetRow, DataGridColumn column, string value, string? templateKey)
    {
        try
        {
            if (!IsEditableSpreadsheetColumn(grid, column))
            {
                return;
            }

            CommitActiveEdits(grid);

            var rows = GetDbGridVisibleRows(grid);
            var sourceIndex = rows.FindIndex(row => ReferenceEquals(row, sourceRow));
            var targetIndex = rows.FindIndex(row => ReferenceEquals(row, targetRow));
            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }

            var startIndex = Math.Min(sourceIndex, targetIndex);
            var endIndex = Math.Max(sourceIndex, targetIndex);
            var changed = false;
            RecordUndoSnapshot();
            for (var index = startIndex; index <= endIndex; index++)
            {
                if (!string.IsNullOrWhiteSpace(templateKey))
                {
                    changed |= TrySetDbGridCellTemplateValue(rows[index], column, templateKey);
                    continue;
                }

                // Source is already valid — but downstream rows may have different
                // row types (DbAoRow vs DbStringRow length tables) so we still
                // coerce per-target. A null coercion silently skips that row.
                var coerced = CoercePastedValue(grid, column, rows[index], value, out _);
                if (coerced is not null)
                {
                    changed |= TrySetDbGridCellValue(rows[index], column, coerced);
                }
            }

            if (changed)
            {
                SafeRefresh(() => RefreshAfterDbGridExcelEdit(grid));
            }
        }
        catch (Exception ex)
        {
            ShowError("Auto-fill failed: " + ex.Message);
        }
    }

    private static string? GetDbColumnProperty(DataGridColumn column)
    {
        if (column is DataGridTextColumn textColumn && textColumn.Binding is Binding binding)
        {
            return binding.Path?.Path;
        }

        if (column is DataGridCheckBoxColumn checkBoxColumn && checkBoxColumn.Binding is Binding checkBoxBinding)
        {
            return checkBoxBinding.Path?.Path;
        }

        if (column is DataGridComboBoxColumn comboColumn)
        {
            if (comboColumn.SelectedValueBinding is Binding selectedValueBinding)
            {
                return selectedValueBinding.Path?.Path;
            }

            if (comboColumn.SelectedItemBinding is Binding selectedItemBinding)
            {
                return selectedItemBinding.Path?.Path;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            try
            {
                child = VisualTreeHelper.GetParent(child);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                return typed;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static DataGridCell? FindAutoFillCellFromEvent(DataGrid grid, object originalSource)
    {
        var dependencySource = originalSource as DependencyObject;
        var cell = FindVisualParent<DataGridCell>(dependencySource);
        if (cell is not null)
        {
            return cell;
        }

        var point = Mouse.GetPosition(grid);
        return grid.InputHitTest(point) is DependencyObject hit
            ? FindVisualParent<DataGridCell>(hit)
            : null;
    }

    private static bool IsNearCellBottomEdge(DataGridCell cell, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(cell);
        const double fillHandleSize = 10;
        return position.X >= cell.ActualWidth - fillHandleSize && position.Y >= cell.ActualHeight - fillHandleSize;
    }

    private void RecalculateAndRefreshSignalGrids()
    {
        try
        {
            _calculator.Recalculate(_project);
            _validationStateUpdater.Update(_project);
            ModuleNameDisplayConverter.SetMeasurementTypes(_project.AnalogInputs, _project.RtdInputs, _project.Counters, _project.Devices);
            // Row models implement INotifyPropertyChanged, so calculator/validation
            // mutations propagate to bound cells without rebuilding the grids.
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SyncPathFieldsToProject()
    {
        _project.Paths.PlcToConfigSourceFolder = _project.Paths.PlcToConfigSourceFolder.Trim();
        _project.Paths.RSourceFolder = _project.Paths.RSourceFolder.Trim();
        _project.Paths.OutputFolder = _project.Paths.OutputFolder.Trim();
        _project.Paths.TiaPortalExportFolder = _project.Paths.TiaPortalExportFolder.Trim();
    }

    private void EnsureGenerateOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_project.Paths.OutputFolder))
        {
            return;
        }

        var baseFolder = !string.IsNullOrWhiteSpace(_projectPath)
            ? Path.GetDirectoryName(_projectPath)
            : Environment.CurrentDirectory;
        _project.Paths.OutputFolder = Path.Combine(baseFolder ?? Environment.CurrentDirectory, "Generated");
    }

    private PlcDatabase? GetActivePlcDatabase()
    {
        return _project.PlcDatabases.FirstOrDefault(database => database.Id == _project.ActivePlcDatabaseId)
            ?? _project.PlcDatabases.FirstOrDefault();
    }

    private PlcDatabase? GetSelectedPlcDatabase()
    {
        return PlcDatabaseTree.SelectedItem as PlcDatabase ?? GetActivePlcDatabase();
    }

    private ScadaDatabase? GetActiveScadaDatabase()
    {
        return _project.ScadaDatabases.FirstOrDefault(database => database.Id == _project.ActiveScadaDatabaseId)
               ?? _project.ScadaDatabases.FirstOrDefault()
               ?? _project.Scada;
    }

    private ScadaDatabase? GetSelectedScadaDatabase()
    {
        return ScadaDatabaseTree.SelectedItem as ScadaDatabase ?? GetActiveScadaDatabase();
    }

    private static PlcDatabase ClonePlcDatabase(PlcDatabase source, string name)
    {
        var json = JsonSerializer.Serialize(source, CloneJsonOptions);
        var clone = JsonSerializer.Deserialize<PlcDatabase>(json, CloneJsonOptions)
            ?? throw new InvalidOperationException("Could not copy PLC database.");
        clone.Id = Guid.NewGuid().ToString("N");
        clone.Name = name;
        return clone;
    }

    private static ScadaDatabase CloneScadaDatabase(ScadaDatabase source, string name)
    {
        var json = JsonSerializer.Serialize(source, CloneJsonOptions);
        var clone = JsonSerializer.Deserialize<ScadaDatabase>(json, CloneJsonOptions)
                    ?? throw new InvalidOperationException("Could not copy SCADA database.");
        clone.Id = Guid.NewGuid().ToString("N");
        clone.Name = name;
        clone.IsActive = false;
        return clone;
    }

    private static void ReplacePlcDatabaseContent(PlcDatabase target, PlcDatabase source)
    {
        var targetId = target.Id;
        var targetName = target.Name;
        var digitalInputIds = target.DigitalInputs.Select(row => row.Id).ToList();
        var digitalOutputIds = target.DigitalOutputs.Select(row => row.Id).ToList();
        var analogInputIds = target.AnalogInputs.Select(row => row.Id).ToList();
        var rtdIds = target.RtdInputs.Select(row => row.Id).ToList();
        var counterIds = target.Counters.Select(row => row.Id).ToList();
        var analogOutputIds = target.AnalogOutputs.Select(row => row.Id).ToList();
        var dbDiIds = target.DbCatalog.DiRows.Select(row => row.Id).ToList();
        var dbDoIds = target.DbCatalog.DoRows.Select(row => row.Id).ToList();
        var dbMeasIds = target.DbCatalog.MeasRows.Select(row => row.Id).ToList();
        var dbAoIds = target.DbCatalog.AoRows.Select(row => row.AoNumber).ToList();
        var dbAlmIds = target.DbCatalog.AlarmRows.Select(row => row.Id).ToList();
        var dbParIds = target.DbCatalog.ParameterRows.Select(row => row.Id).ToList();
        var clone = ClonePlcDatabase(source, source.Name);
        PreserveIds(clone.DigitalInputs, digitalInputIds, (row, id) => row.Id = id);
        PreserveIds(clone.DigitalOutputs, digitalOutputIds, (row, id) => row.Id = id);
        PreserveIds(clone.AnalogInputs, analogInputIds, (row, id) => row.Id = id);
        PreserveIds(clone.RtdInputs, rtdIds, (row, id) => row.Id = id);
        PreserveIds(clone.Counters, counterIds, (row, id) => row.Id = id);
        PreserveIds(clone.AnalogOutputs, analogOutputIds, (row, id) => row.Id = id);
        PreserveIds(clone.DbCatalog.DiRows, dbDiIds, (row, id) => row.Id = id);
        PreserveIds(clone.DbCatalog.DoRows, dbDoIds, (row, id) => row.Id = id);
        PreserveIds(clone.DbCatalog.MeasRows, dbMeasIds, (row, id) => row.Id = id);
        PreserveIds(clone.DbCatalog.AoRows, dbAoIds, (row, id) => row.AoNumber = id);
        PreserveIds(clone.DbCatalog.AlarmRows, dbAlmIds, (row, id) => row.Id = id);
        PreserveIds(clone.DbCatalog.ParameterRows, dbParIds, (row, id) => row.Id = id);
        target.DigitalInputs = clone.DigitalInputs;
        target.DigitalOutputs = clone.DigitalOutputs;
        target.AnalogInputs = clone.AnalogInputs;
        target.RtdInputs = clone.RtdInputs;
        target.Counters = clone.Counters;
        target.AnalogOutputs = clone.AnalogOutputs;
        target.Devices = clone.Devices;
        target.ModuleCatalog = clone.ModuleCatalog;
        target.DbCatalog = clone.DbCatalog;
        target.Id = targetId;
        target.Name = targetName;
    }

    private static void PreserveIds<T>(IList<T> rows, IReadOnlyList<int> ids, Action<T, int> applyId)
    {
        var count = Math.Min(rows.Count, ids.Count);
        for (var index = 0; index < count; index++)
        {
            applyId(rows[index], ids[index]);
        }
    }

    private void SaveActivePlcDatabase()
    {
        var active = GetActivePlcDatabase();
        if (active is null)
        {
            return;
        }

        active.CaptureFrom(_project);
        _project.ActivePlcDatabaseId = active.Id;
    }

    private void SaveActiveScadaDatabase()
    {
        var active = GetActiveScadaDatabase();
        if (active is null)
        {
            return;
        }

        _project.Scada = active;
        _project.ActiveScadaDatabaseId = active.Id;
    }

    private void LoadActivePlcDatabase()
    {
        var active = GetActivePlcDatabase();
        if (active is null)
        {
            return;
        }

        active.ApplyTo(_project);
        _project.ActivePlcDatabaseId = active.Id;
    }

    private void LoadProjectDatabase()
    {
        var projectPath = FindWorkbookNearApp("PlantConfigurator.plantcfg");
        if (projectPath is null)
        {
            EnsureProjectDefaults();
            return;
        }

        try
        {
            _project = _projectFileService.Load(projectPath);
            _projectPath = projectPath;
            _project.ProjectFilePath = projectPath;
            EnsureProjectDefaults();
        }
        catch
        {
            _project = CwcProject.CreateEmpty();
            _projectPath = null;
            _project.ProjectFilePath = "";
            EnsureProjectDefaults();
        }
    }

    private void LoadExcelReferenceDataForTesting()
    {
        var workbookPath = FindWorkbookNearApp("Configuration_Unit_List.xlsm");
        if (workbookPath is null)
        {
            return;
        }

        TryLoadExcelReference(() => _project.ModuleCatalog = _excelImporter.ImportModuleCatalog(workbookPath));
        TryLoadExcelReference(() => _project.Devices = _excelImporter.ImportDeviceRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.DiRows = _excelImporter.ImportDbDiRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.DiSystemNameRows = _excelImporter.ImportDbDiSystemNameRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.DiColorSettings = _excelImporter.ImportDbDiColorValues(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.DoRows = _excelImporter.ImportDbDoRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.DoSystemNameRows = _excelImporter.ImportDbDoSystemNameRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.MeasRows = _excelImporter.ImportDbMeasRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.AoRows = _excelImporter.ImportDbAoRows(workbookPath));
        TryLoadExcelReference(() => _project.DbCatalog.ValidationLists = _excelImporter.ImportDbValidationLists(workbookPath));

        try
        {
            _calculator.Recalculate(_project);
            ApplyConfiguredColorPalette();
            SaveActivePlcDatabase();
        }
        catch
        {
            // Startup must stay usable even if reference data is incomplete while editing the workbook.
        }
    }

    private static void TryLoadExcelReference(Action loadAction)
    {
        try
        {
            loadAction();
        }
        catch
        {
            // Reference tables are loaded independently so one missing/edited table does not hide the others.
        }
    }

    private void EnsureProjectDefaults()
    {
        _project.Paths ??= new ProjectPaths();
        _project.ProjectFilePath = _projectPath ?? "";
        _project.TableDesign ??= new TableDesignSettings();
        _project.ActiveWorkspace = _project.ActiveWorkspace.Equals("SCADA", StringComparison.OrdinalIgnoreCase) ? "SCADA" : "PLC";
        _project.Scada ??= ScadaDatabase.CreateDefault();
        _project.Scada.EnsureDefaults();
        _project.ScadaDatabases ??= [];
        if (_project.ScadaDatabases.Count == 0)
        {
            _project.ScadaDatabases.Add(_project.Scada);
        }

        foreach (var scadaDatabase in _project.ScadaDatabases)
        {
            scadaDatabase.EnsureDefaults();
        }

        if (_project.ScadaDatabases.All(database => database.Id != _project.ActiveScadaDatabaseId))
        {
            var migrated = _project.ScadaDatabases.FirstOrDefault(database => ReferenceEquals(database, _project.Scada))
                           ?? _project.ScadaDatabases.First();
            _project.ActiveScadaDatabaseId = migrated.Id;
        }

        _project.Scada = _project.ScadaDatabases.First(database => database.Id == _project.ActiveScadaDatabaseId);
        _project.PlcDatabases ??= [];
        _project.TiaSync ??= new TiaSyncConfig();
        _project.DescriptionTextLists ??= new DescriptionTextListConfig();
        _project.DescriptionTemplates ??= new DescriptionTemplateCatalog();
        _project.AlmParExport ??= new AlmParExportConfig();
        MergeDefaultDescriptionTemplates();
        if (_project.PlcDatabases.Count == 0)
        {
            _project.PlcDatabases.Add(PlcDatabase.FromProject(_project, "PLC 1"));
        }

        if (_project.PlcDatabases.All(database => database.Id != _project.ActivePlcDatabaseId))
        {
            _project.ActivePlcDatabaseId = _project.PlcDatabases[0].Id;
        }

        LoadActivePlcDatabase();
        _project.ModuleCatalog ??= ModuleCatalog.CreateDefault();
        _project.DbCatalog ??= new DbCatalog();
        _project.DbCatalog.DiColorSettings ??= [];
        _project.DbCatalog.DiTextLengths ??= DbTextLengthSettings.CreateDigitalInputDefaults();
        _project.DbCatalog.DoTextLengths ??= DbTextLengthSettings.CreateDigitalOutputDefaults();
        _project.DbCatalog.MeasTextLengths ??= DbTextLengthSettings.CreateMeasDefaults();
        _project.DbCatalog.AoTextLengths ??= DbTextLengthSettings.CreateMeasDefaults();
        _project.DbCatalog.AlarmTextLengths ??= DbTextLengthSettings.CreateAlarmDefaults();
        _project.DbCatalog.ParameterTextLengths ??= DbTextLengthSettings.CreateParameterDefaults();
        _project.DbCatalog.ValidationLists ??= [];
        _project.DbCatalog.ConditionalColors ??= DbConditionalColorSettings.CreateDefaults();
        _project.DbCatalog.Sequence ??= SequenceCatalog.CreateDefault();
        _project.DbCatalog.Sequence.EnsureDefaults();
        _project.DbCatalog.TextLists ??= TextListCatalog.CreateDefault();
        _project.DbCatalog.TextLists.EnsureDefaults();
        foreach (var defaultList in DbValidationDefaults.Create())
        {
            if (_project.DbCatalog.ValidationLists.All(row => !row.Key.Equals(defaultList.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _project.DbCatalog.ValidationLists.Add(defaultList);
            }
        }

        NormalizeValidationLists();
        NormalizeValidationBackedValues();
        _project.DbCatalog.DoRows = _project.DbCatalog.DoRows.OrderBy(row => row.Id).Take(288).ToList();
        TrySeedSequenceFromTemplate();
        SequenceParDbParser.RefreshReferenceCounts(_project.DbCatalog.Sequence);
        ApplyAlarmSrOptions();
        _project.Devices ??= [];
        if (_project.Devices.Count == 0)
        {
            _project.Devices.AddRange(DeviceConfig.CreateDefaultRows());
        }
        SaveActivePlcDatabase();
        _project.TiaSync.SyncWithDatabases(_project.PlcDatabases, _project.DbCatalog);
        _project.DescriptionTextLists.SyncWithDatabases(_project.PlcDatabases);
        _project.AlmParExport.SyncWithDatabases(_project.PlcDatabases);
        var templateChanges = _descriptionTemplateService.ApplyTemplates(_project);
        if (templateChanges > 0)
        {
            // The text-based auto matching used to run silently; surface it so
            // a converted description is never a surprise.
            ShowToast($"Description templates: {templateChanges} field(s) linked or updated automatically.");
        }

        ApplyTableDesign();
    }

    private void MergeDefaultDescriptionTemplates()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Templates", "description_templates.csv"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Templates", "description_templates.csv"),
            Path.Combine(Environment.CurrentDirectory, "CwcConfigurator", "Templates", "description_templates.csv")
        };

        foreach (var candidate in candidates)
        {
            var path = Path.GetFullPath(candidate);
            if (File.Exists(path))
            {
                _descriptionTemplateService.MergeFromCsv(_project.DescriptionTemplates, path);
                return;
            }
        }
    }

    private void TrySeedSequenceFromTemplate()
    {
        if (_project.DbCatalog.Sequence.HasConfiguredSteps())
        {
            return;
        }

        foreach (var candidate in GetSequenceParTemplateCandidates())
        {
            var path = Path.GetFullPath(candidate);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                _project.DbCatalog.Sequence = _sequenceParser.Parse(path);
                return;
            }
            catch
            {
                return;
            }
        }
    }

    private static IEnumerable<string> GetSequenceParTemplateCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Templates", "SEQ_PAR.db");
        yield return Path.Combine(AppContext.BaseDirectory, "SEQ_PAR.db");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Templates", "SEQ_PAR.db");
        yield return Path.Combine(Environment.CurrentDirectory, "CwcConfigurator", "Templates", "SEQ_PAR.db");
        yield return @"C:\Users\Mazda\Desktop\New folder (14)\SEQ_PAR.db";
    }

    private string GetSequenceParSourceText()
    {
        var existing = GetSequenceParTemplateCandidates()
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
        return existing is null ? "SEQ_PAR source: not found" : $"SEQ_PAR source: {existing}";
    }

    private int ApplyDescriptionTemplatesAndRefresh()
    {
        var changed = _descriptionTemplateService.ApplyTemplates(_project);
        _dbAlmView?.Refresh();
        _dbParView?.Refresh();
        DbAlmGrid.Items.Refresh();
        DbParGrid.Items.Refresh();
        LoadDbAlmFilterItems();
        LoadDbParFilterItems();
        return changed;
    }

    private void NormalizeValidationLists()
    {
        foreach (var row in _project.DbCatalog.ValidationLists)
        {
            if (row.Key.Equals(DbValidationDefaults.DbAlmColorMode, StringComparison.OrdinalIgnoreCase))
            {
                row.Name = "DB ALM - coloring mode";
                row.ValuesText = "0 - Color by alarm class without USED condition" + Environment.NewLine + "1 - Color by alarm class only when USED is active";
                continue;
            }

            if (row.Key.Equals(DbValidationDefaults.DbAlmYesNo, StringComparison.OrdinalIgnoreCase))
            {
                row.Name = "DB ALM - No/Yes";
                row.ValuesText = "0 - No" + Environment.NewLine + "1 - Yes";
                continue;
            }

            row.ValuesText = string.Join(Environment.NewLine, DbValidationListSetting.SplitOptions(row.ValuesText).Select(option => option.DisplayText));
        }
    }

    private void NormalizeValidationBackedValues()
    {
        foreach (var row in _project.DbCatalog.DiRows)
        {
            row.ColorValue0 = ValidationValue(DbValidationDefaults.DbDiColorValues, row.ColorValue0);
            row.ColorValue1 = ValidationValue(DbValidationDefaults.DbDiColorValues, row.ColorValue1);
            row.AlarmConfig = ValidationValue(DbValidationDefaults.DbDiAlarmBoolean, row.AlarmConfig);
            row.AlarmInvert = ValidationValue(DbValidationDefaults.DbDiAlarmBoolean, row.AlarmInvert);
        }

        foreach (var row in _project.DbCatalog.MeasRows)
        {
            row.Unit = ValidationValue(DbValidationDefaults.DbMeasUnit, row.Unit);
            row.LoggingEnable = ValidationValue(DbValidationDefaults.DbMeasLoggingEnable, row.LoggingEnable);
            row.LoggingTime = ValidationValue(DbValidationDefaults.DbMeasLoggingTime, row.LoggingTime);
        }

        foreach (var row in _project.DbCatalog.AoRows)
        {
            if (row.AoPlcName.Equals(row.AoSystemName, StringComparison.OrdinalIgnoreCase))
            {
                row.AoPlcName = "";
            }

            row.Unit = ValidationValue(DbValidationDefaults.DbMeasUnit, row.Unit);
        }

        foreach (var row in _project.DbCatalog.AlarmRows)
        {
            row.Used = ValidationValue(DbValidationDefaults.DbParInUse, row.Used);
            row.AlarmControl = ValidationValue(DbValidationDefaults.DbAlmEnableDisable, row.AlarmControl);
            row.SrAlarm = ValidationValue(DbValidationDefaults.DbAlmYesNo, row.SrAlarm);
            row.AlarmClass = ValidationValue(DbValidationDefaults.DbAlmAlarmClass, row.AlarmClass);
            row.ReactToUnit = ValidationValue(DbValidationDefaults.DbAlmReactToUnit, row.ReactToUnit);
            row.Spare = ValidationValue(DbValidationDefaults.DbAlmAlarmClass, row.Spare);
            row.BlockingClass1 = ValidationValue(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass1);
            row.BlockingClass2 = ValidationValue(DbValidationDefaults.DbAlmBlockingClass, row.BlockingClass2);
            row.TimeBase = ValidationValue(DbValidationDefaults.DbAlmTimeBase, row.TimeBase);
        }

        foreach (var row in _project.DbCatalog.ParameterRows)
        {
            row.InUse = ValidationValue(DbValidationDefaults.DbParInUse, row.InUse);
            row.ParameterType = ValidationValue(DbValidationDefaults.DbParType, row.ParameterType);
            row.Unit = ValidationValue(DbValidationDefaults.DbMeasUnit, row.Unit);
        }
    }

    private static void FillDigitalDbFromConfiguration(List<DbStringRow> dbRows, IEnumerable<DigitalSignalConfig> configRows, bool includeInverted)
    {
        foreach (var dbRow in dbRows)
        {
            dbRow.TrueText = "";
            dbRow.ModuleName = "";
            dbRow.ModulePort = null;
        }

        foreach (var configRow in configRows)
        {
            var isUsed = configRow.Config == SignalConfigState.Enabled || (includeInverted && configRow.Config == SignalConfigState.Inverted);
            if (!isUsed)
            {
                continue;
            }

            var dbRow = dbRows.FirstOrDefault(row => row.Id == configRow.Id);
            if (dbRow is null)
            {
                continue;
            }

            dbRow.TrueText = "TRUE";
            dbRow.ModuleName = configRow.ModuleNumber.ToString(CultureInfo.InvariantCulture);
            dbRow.ModulePort = configRow.Port;
        }
    }

    private void FillMeasDbFromConfiguration()
    {
        foreach (var dbRow in _project.DbCatalog.MeasRows)
        {
            dbRow.TrueText = "";
            dbRow.Type = "";
            dbRow.ModuleName = "";
            dbRow.ModulePort = null;
        }

        FillMeasRows(_project.AnalogInputs, "AI");
        FillMeasRows(_project.RtdInputs, "RTD");
        FillMeasRows(_project.Counters, "COUNTER");

        foreach (var device in _project.Devices.Where(device => device.Config == SignalConfigState.Enabled))
        {
            for (var port = 0; port < 24; port++)
            {
                var dbRow = _project.DbCatalog.MeasRows.FirstOrDefault(row => row.Id == device.FirstValueId + port);
                if (dbRow is null)
                {
                    continue;
                }

                dbRow.TrueText = "TRUE";
                dbRow.Type = "DEVICE";
                dbRow.ModuleName = "0";
                dbRow.ModulePort = port + 1;
            }
        }
    }

    private void FillMeasRows(IEnumerable<AnalogSignalConfig> rows, string type)
    {
        foreach (var configRow in rows.Where(row => row.Config == SignalConfigState.Enabled))
        {
            var dbRow = _project.DbCatalog.MeasRows.FirstOrDefault(row => row.Id == configRow.Id);
            if (dbRow is null)
            {
                continue;
            }

            dbRow.TrueText = "TRUE";
            dbRow.Type = type;
            dbRow.ModuleName = configRow.ModuleName;
            dbRow.ModulePort = configRow.Port;
        }
    }

    private void FillMeasRows(IEnumerable<CounterConfig> rows, string type)
    {
        foreach (var configRow in rows.Where(row => row.Config == SignalConfigState.Enabled))
        {
            var dbRow = _project.DbCatalog.MeasRows.FirstOrDefault(row => row.Id == configRow.Id);
            if (dbRow is null)
            {
                continue;
            }

            dbRow.TrueText = "TRUE";
            dbRow.Type = type;
            dbRow.ModuleName = configRow.ModuleName;
            dbRow.ModulePort = 0;
        }
    }

    private void ApplyConfiguredColorPalette()
    {
        ColorValueToBrushConverter.SetConfiguredColors(_project.DbCatalog.DiColorSettings.Select(color => (color.Value.ToString(), color.Name, color.Color)));
    }

    private void ApplyConfiguredConditionalColors()
    {
        DbConditionalCellBrushConverter.SetSettings(_project.DbCatalog.ConditionalColors, _project.TableDesign?.ConditionalColoringEnabled ?? true);
    }

    private void FillAoDbFromConfiguration()
    {
        foreach (var dbRow in _project.DbCatalog.AoRows)
        {
            dbRow.TrueText = "";
            dbRow.ModuleName = "";
            dbRow.ModulePort = null;
        }

        foreach (var configRow in _project.AnalogOutputs.Where(row => row.Config == SignalConfigState.Enabled))
        {
            var dbRow = _project.DbCatalog.AoRows.FirstOrDefault(row => row.AoNumber == configRow.Id);
            if (dbRow is null)
            {
                continue;
            }

            dbRow.TrueText = "TRUE";
            dbRow.ModuleName = configRow.ModuleName;
            dbRow.ModulePort = configRow.Port;
            if (!string.IsNullOrWhiteSpace(configRow.SystemName))
            {
                dbRow.AoSystemName = configRow.SystemName;
            }

            if (!string.IsNullOrWhiteSpace(configRow.SignalName))
            {
                dbRow.AoPlcName = configRow.SignalName;
            }
        }
    }

    private void ApplyTableDesign()
    {
        var design = _project.TableDesign ?? new TableDesignSettings();
        var (row, alternate) = TableDesignPalette.Resolve(design.Layout);
        var rowBrush = BrushFromString(row, Brushes.White);
        var alternateBrush = BrushFromString(alternate, new SolidColorBrush(Color.FromRgb(246, 248, 250)));
        DbConditionalCellBrushConverter.SetTableBrushes(rowBrush, alternateBrush);
        foreach (var grid in GetSpreadsheetLikeGrids())
        {
            grid.AlternationCount = 2;
            grid.ClearValue(DataGrid.RowBackgroundProperty);
            grid.ClearValue(DataGrid.AlternatingRowBackgroundProperty);
        }
    }

    private void RefreshAllDataGrids()
    {
        foreach (var grid in GetSpreadsheetLikeGrids())
        {
            grid.Items.Refresh();
        }
    }

    private static Brush BrushFromString(string color, Brush fallback)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(color)!;
        }
        catch
        {
            return fallback;
        }
    }

    private void ValidateDbCellEdit(DataGridCellEditEndingEventArgs e, string dbName, DbTextLengthSettings settings)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.EditingElement is not TextBox textBox
            || e.Row.Item is not DbStringRow row)
        {
            return;
        }

        var propertyName = GetDbColumnProperty(e.Column);
        var maxLength = GetDbMaxTextLength(propertyName, settings);
        if (maxLength is null || textBox.Text.Length <= maxLength.Value)
        {
            return;
        }

        e.Cancel = true;
        textBox.Text = propertyName is null ? "" : GetObjectStringValue(row, propertyName);
        MessageBox.Show(this,
            $"{dbName}: predug tekst.{Environment.NewLine}ID: {row.Id}{Environment.NewLine}Polje: {e.Column.Header}{Environment.NewLine}Maksimalno znakova: {maxLength.Value}",
            "Predug tekst",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ValidateDbAoCellEdit(DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || e.EditingElement is not TextBox textBox
            || e.Row.Item is not DbAoRow row)
        {
            return;
        }

        var propertyName = GetDbColumnProperty(e.Column);
        var maxLength = GetDbAoMaxTextLength(propertyName, _project.DbCatalog.AoTextLengths);
        if (maxLength is null || textBox.Text.Length <= maxLength.Value)
        {
            return;
        }

        e.Cancel = true;
        textBox.Text = propertyName is null ? "" : GetDbAoStringValue(row, propertyName);
        MessageBox.Show(this,
            $"DB AO: predug tekst.{Environment.NewLine}AO: {row.AoNumber}{Environment.NewLine}Polje: {e.Column.Header}{Environment.NewLine}Maksimalno znakova: {maxLength.Value}",
            "Predug tekst",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private bool ValidateDbTextValues(string dbName, string systemEnglish, string nameEnglish, string systemOther, string nameOther, DbTextLengthSettings settings)
    {
        var checks = new[]
        {
            ("STATUS SYSTEM ENG", systemEnglish, settings.StatusSystemEnglishMax),
            ("STATUS NAME ENG", nameEnglish, settings.StatusNameEnglishMax),
            ("STATUS SYSTEM OTHER", systemOther, settings.StatusSystemOtherMax),
            ("STATUS NAME OTHER", nameOther, settings.StatusNameOtherMax)
        };

        foreach (var (fieldName, value, maxLength) in checks)
        {
            if (value.Length <= maxLength)
            {
                continue;
            }

            MessageBox.Show(this,
                $"{dbName}: predug tekst.{Environment.NewLine}Polje: {fieldName}{Environment.NewLine}Maksimalno znakova: {maxLength}{Environment.NewLine}Tekst: {value}",
                "Predug tekst",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private static int? GetDbMaxTextLength(string? propertyName, DbTextLengthSettings settings)
    {
        return propertyName switch
        {
            nameof(DbStringRow.SystemNameEnglish) => settings.StatusSystemEnglishMax,
            nameof(DbStringRow.NameEnglish) => settings.StatusNameEnglishMax,
            nameof(DbStringRow.SystemNameOther) => settings.StatusSystemOtherMax,
            nameof(DbStringRow.NameOther) => settings.StatusNameOtherMax,
            _ => null
        };
    }

    private static int? GetDbAoMaxTextLength(string? propertyName, DbTextLengthSettings settings)
    {
        return propertyName switch
        {
            nameof(DbAoRow.AoSystemName) => settings.StatusSystemEnglishMax,
            nameof(DbAoRow.AoPlcName) => settings.StatusNameEnglishMax,
            nameof(DbAoRow.NameEnglish) => settings.StatusNameEnglishMax,
            nameof(DbAoRow.NameOther) => settings.StatusNameOtherMax,
            _ => null
        };
    }

    private static string GetDbAoStringValue(DbAoRow row, string propertyName)
    {
        return propertyName switch
        {
            nameof(DbAoRow.AoSystemName) => row.AoSystemName,
            nameof(DbAoRow.AoPlcName) => row.AoPlcName,
            nameof(DbAoRow.NameEnglish) => row.NameEnglish,
            nameof(DbAoRow.NameOther) => row.NameOther,
            _ => ""
        };
    }

    private void ApplyDbDiSystemNamesToRows()
    {
        ApplyDbSystemNamesToRows(_project.DbCatalog.DiRows, _project.DbCatalog.DiSystemNameRows);
    }

    private void ApplyDbDoSystemNamesToRows()
    {
        ApplyDbSystemNamesToRows(_project.DbCatalog.DoRows, _project.DbCatalog.DoSystemNameRows);
    }

    private static void ApplyDbSystemNamesToRows(IReadOnlyList<DbStringRow> rows, IReadOnlyList<DbDiSystemNameRow> systemRows)
    {
        foreach (var row in rows)
        {
            var systemNumber = (row.Id / 32) + 1;
            var systemRow = systemRows.FirstOrDefault(item => item.SystemNumber == systemNumber);
            if (systemRow is not null)
            {
                row.PlcStructure = systemRow.SystemPlcShortName;
            }
        }
    }

    private static string? FindWorkbookNearApp(string fileName)
    {
        var directories = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var startDirectory in directories.Where(Directory.Exists))
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static string? FindExcelTemplate()
    {
        var templateNames = new[]
        {
            Path.Combine("Templates", "Configuration_Unit_List_Template.xlsm"),
            "Configuration_Unit_List_Template.xlsm",
            "Configuration_Unit_List.xlsm"
        };

        foreach (var templateName in templateNames)
        {
            var found = FindWorkbookNearApp(templateName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void ShowMessage(string message, bool toast = true)
    {
        StatusText.Text = message.Split(Environment.NewLine).FirstOrDefault() ?? "Ready";
        UpdateProjectPathText();
        UpdateUndoRedoButtons();
        if (toast)
        {
            ShowToast(StatusText.Text);
        }
    }

    private void UpdateProjectPathText()
    {
        ProjectPathText.Text = _projectOpen
            ? _projectPath ?? "Unsaved project"
            : "No project open";
        ProjectFileNameText.Text = _projectOpen
            ? GetCurrentProjectFileName()
            : "";
        UpdateStatusBarIndicators();
    }

    private void UpdateStatusBarIndicators()
    {
        if (StatusActiveDbText is null)
        {
            return;
        }

        if (!_projectOpen)
        {
            StatusActiveDbText.Text = "";
            StatusRowCountsText.Text = "";
            StatusDirtyText.Text = "";
            Title = "Plant Configurator";
            UpdateToolbarButtonStates(isDirty: false);
            return;
        }

        var activeDb = _project.PlcDatabases.FirstOrDefault(db => db.Id == _project.ActivePlcDatabaseId);
        StatusActiveDbText.Text = activeDb is null
            ? $"Workspace: {_project.ActiveWorkspace}"
            : $"Workspace: {_project.ActiveWorkspace} · DB: {activeDb.Name}";

        StatusRowCountsText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "DI {0} · DO {1} · AI {2} · RTD {3} · CNT {4} · AO {5}",
            _project.DigitalInputs.Count,
            _project.DigitalOutputs.Count,
            _project.AnalogInputs.Count,
            _project.RtdInputs.Count,
            _project.Counters.Count,
            _project.AnalogOutputs.Count);

        // Painting the cached dirty state keeps this method cheap (it runs after
        // almost every user action); the real check serializes the whole project,
        // so it is debounced onto an idle-priority timer.
        ApplyDirtyIndicator(_lastKnownDirty);
        ScheduleDirtyCheck();
    }

    private void ApplyDirtyIndicator(bool dirty)
    {
        StatusDirtyText.Text = dirty ? "● Unsaved changes" : "";
        Title = (dirty ? "● " : string.Empty) + GetCurrentProjectFileName() + " — Plant Configurator";
        UpdateToolbarButtonStates(dirty);
    }

    private void ScheduleDirtyCheck()
    {
        if (_dirtyCheckTimer is null)
        {
            _dirtyCheckTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _dirtyCheckTimer.Tick += (_, _) =>
            {
                _dirtyCheckTimer!.Stop();
                RefreshDirtyIndicator();
            };
        }

        _dirtyCheckTimer.Stop();
        _dirtyCheckTimer.Start();
    }

    private void RefreshDirtyIndicator()
    {
        if (!_projectOpen)
        {
            return;
        }

        var dirty = !_projectFileService.ComputeSnapshotHash(_project).AsSpan().SequenceEqual(_savedSnapshotHash);
        if (dirty == _lastKnownDirty)
        {
            return;
        }

        _lastKnownDirty = dirty;
        ApplyDirtyIndicator(dirty);
    }

    private string GetCurrentProjectFileName()
    {
        return string.IsNullOrWhiteSpace(_projectPath)
            ? "Unsaved project"
            : Path.GetFileName(_projectPath);
    }

    private void ShowError(string message)
    {
        // No toast: the modal error dialog is already in the user's face.
        ShowMessage(message, toast: false);
        MessageBox.Show(this, message, "Plant Configurator", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RecordUndoSnapshot()
    {
        if (_restoringSnapshot || !_projectOpen)
        {
            return;
        }

        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        var snapshot = CompressSnapshot(_projectFileService.Serialize(_project));
        if (_undoSnapshots.Count > 0 && _undoSnapshots.Peek().AsSpan().SequenceEqual(snapshot))
        {
            return;
        }

        _undoSnapshots.Push(snapshot);
        if (_undoSnapshots.Count > 50)
        {
            var items = _undoSnapshots.ToArray(); // newest-first (top of stack)
            _undoSnapshots.Clear();
            foreach (var item in items.Take(50).Reverse())
            {
                _undoSnapshots.Push(item);
            }
        }

        _redoSnapshots.Clear();
        UpdateUndoRedoButtons();
    }

    private void RestoreUndoSnapshot()
    {
        if (IsBusy || _undoSnapshots.Count == 0)
        {
            return;
        }

        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        _redoSnapshots.Push(CompressSnapshot(_projectFileService.Serialize(_project)));
        RestoreProjectSnapshot(DecompressSnapshot(_undoSnapshots.Pop()), "Undo applied.");
    }

    private void RestoreRedoSnapshot()
    {
        if (IsBusy || _redoSnapshots.Count == 0)
        {
            return;
        }

        SyncPathFieldsToProject();
        SaveActivePlcDatabase();
        _undoSnapshots.Push(CompressSnapshot(_projectFileService.Serialize(_project)));
        RestoreProjectSnapshot(DecompressSnapshot(_redoSnapshots.Pop()), "Redo applied.");
    }

    // Undo/redo snapshots are whole-project JSON; Brotli (fastest level) keeps the
    // 50-deep stacks at a few MB instead of hundreds for large projects.
    private static byte[] CompressSnapshot(string json)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static string DecompressSnapshot(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private void RestoreProjectSnapshot(string snapshot, string message)
    {
        try
        {
            _restoringSnapshot = true;
            _project = JsonSerializer.Deserialize<CwcProject>(snapshot, CloneJsonOptions)
                       ?? throw new InvalidOperationException("Could not restore project snapshot.");
            EnsureProjectDefaults();
            _projectOpen = true;
            BindProject();
            ShowMessage(message);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _restoringSnapshot = false;
            UpdateUndoRedoButtons();
        }
    }

    private void UpdateUndoRedoButtons()
    {
        if (ToolbarUndoButton is not null)
        {
            ToolbarUndoButton.IsEnabled = _projectOpen && _undoSnapshots.Count > 0;
        }
        if (ToolbarRedoButton is not null)
        {
            ToolbarRedoButton.IsEnabled = _projectOpen && _redoSnapshots.Count > 0;
        }
    }

    private void UpdateToolbarButtonStates(bool isDirty)
    {
        if (ToolbarSaveButton is not null)
        {
            ToolbarSaveButton.IsEnabled = _projectOpen && isDirty;
        }
        if (ToolbarValidateButton is not null)
        {
            ToolbarValidateButton.IsEnabled = _projectOpen;
        }
        if (ToolbarGenerateButton is not null)
        {
            ToolbarGenerateButton.IsEnabled = _projectOpen;
        }
        if (ToolbarTiaSyncButton is not null)
        {
            ToolbarTiaSyncButton.IsEnabled = _projectOpen;
        }
        if (ToolbarSettingsButton is not null)
        {
            ToolbarSettingsButton.IsEnabled = _projectOpen;
        }
        if (ToolbarFindButton is not null)
        {
            ToolbarFindButton.IsEnabled = _projectOpen;
        }
        if (ToolbarReplaceButton is not null)
        {
            ToolbarReplaceButton.IsEnabled = _projectOpen;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? "PlantConfigurator_Project" : fileName;
    }
}
