using System.Globalization;
using System.Text.Json.Serialization;

namespace CwcConfigurator.Domain;

public sealed class CwcProject
{
    public int SchemaVersion { get; set; } = 2;
    [JsonIgnore]
    public string ProjectFilePath { get; set; } = "";
    public string ProjectName { get; set; } = "CWC Configuration Project";
    /// <summary>
    /// When true, project carries both ENG and OTHER language texts and the
    /// validator emits warnings for missing OTHER fields. When false, the
    /// repair evaluators skip every *Other rule.
    /// </summary>
    public bool Multilingual { get; set; } = true;
    public ProjectPaths Paths { get; set; } = new();
    public TableDesignSettings TableDesign { get; set; } = new();
    public string ActiveWorkspace { get; set; } = "PLC";
    public string ActivePlcDatabaseId { get; set; } = "";
    public string ActiveScadaDatabaseId { get; set; } = "";
    public List<PlcDatabase> PlcDatabases { get; set; } = [];
    public List<ScadaDatabase> ScadaDatabases { get; set; } = [];
    public ScadaDatabase Scada { get; set; } = ScadaDatabase.CreateDefault();

    public List<DigitalSignalConfig> DigitalInputs { get; set; } = [];
    public List<DigitalSignalConfig> DigitalOutputs { get; set; } = [];
    public List<AnalogSignalConfig> AnalogInputs { get; set; } = [];
    public List<AnalogSignalConfig> RtdInputs { get; set; } = [];
    public List<CounterConfig> Counters { get; set; } = [];
    public List<AnalogOutputConfig> AnalogOutputs { get; set; } = [];
    public List<DeviceConfig> Devices { get; set; } = [];

    public ModuleCatalog ModuleCatalog { get; set; } = new();
    public DbCatalog DbCatalog { get; set; } = new();
    public TiaSyncConfig TiaSync { get; set; } = new();
    public DescriptionTextListConfig DescriptionTextLists { get; set; } = new();
    public DescriptionTemplateCatalog DescriptionTemplates { get; set; } = new();
    public AlmParExportConfig AlmParExport { get; set; } = new();

    public static CwcProject CreateEmpty()
    {
        var project = new CwcProject();
        var database = PlcDatabase.CreateDefault("PLC 1");
        project.PlcDatabases.Add(database);
        project.ActivePlcDatabaseId = database.Id;
        database.ApplyTo(project);
        return project;
    }
}

public sealed class DescriptionTextListConfig
{
    public string WorkbookPath { get; set; } = "";
    public DescriptionTextListData TextLists { get; set; } = new();
    public List<DescriptionTextListMapping> Mappings { get; set; } = [];

    public void SyncWithDatabases(IEnumerable<PlcDatabase> databases)
    {
        var databaseList = databases.ToList();
        foreach (var database in databaseList)
        {
            GetOrCreate(database.Id, database.Name);
        }

        Mappings = Mappings
            .Where(mapping => databaseList.Any(database => database.Id == mapping.PlcDatabaseId))
            .ToList();
    }

    public DescriptionTextListMapping GetOrCreate(string plcDatabaseId, string plcName)
    {
        var mapping = Mappings.FirstOrDefault(item => item.PlcDatabaseId == plcDatabaseId);
        if (mapping is null)
        {
            mapping = new DescriptionTextListMapping
            {
                PlcDatabaseId = plcDatabaseId,
                PlcName = plcName
            };
            Mappings.Add(mapping);
        }

        mapping.PlcName = plcName;
        return mapping;
    }
}

public sealed class DescriptionTemplateEntry
{
    public string Key { get; set; } = "";
    public string Scope { get; set; } = "Both";
    public string Notes { get; set; } = "";
    public string TextEnglish { get; set; } = "";
    public string TextOther { get; set; } = "";
}

public sealed class DescriptionTemplateCatalog
{
    public List<DescriptionTemplateEntry> Templates { get; set; } = [];

    public IEnumerable<DescriptionTemplateEntry> ForScope(string scope)
    {
        return Templates
            .Where(template => template.Scope.Equals("Both", StringComparison.OrdinalIgnoreCase)
                               || template.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase))
            .OrderBy(template => template.Key, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class DescriptionTextListMapping
{
    public string PlcDatabaseId { get; set; } = "";
    public string PlcName { get; set; } = "";
    public string AlmDescriptionsList { get; set; } = "";
    public string ParDescriptionsList { get; set; } = "";
    public string SequenceControlStatusList { get; set; } = "";
    public string SequenceCommandList { get; set; } = "";
    public string AlmBlockingClassList { get; set; } = "";
    public string CabinetsList { get; set; } = "";
    public string PortConfigurationList { get; set; } = "";
    public string SeqCmdList { get; set; } = "";
    public string SeqConfigList { get; set; } = "";
}

public sealed class DescriptionTextListData
{
    public string SourcePath { get; set; } = "";
    public List<string> Languages { get; set; } = [];
    public List<string> AllListNames { get; set; } = [];
    public Dictionary<string, Dictionary<string, Dictionary<int, string>>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DescriptionTextReference> References { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Comments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetText(string listName, int id, string language)
    {
        if (string.IsNullOrWhiteSpace(listName)
            || string.IsNullOrWhiteSpace(language)
            || !Lists.TryGetValue(listName, out var languageMap)
            || !languageMap.TryGetValue(language, out var idMap)
            || !idMap.TryGetValue(id, out var text))
        {
            return "";
        }

        return text ?? "";
    }

    public void SetText(string listName, int id, string language, string text)
    {
        if (string.IsNullOrWhiteSpace(listName) || string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        if (!Lists.TryGetValue(listName, out var languageMap))
        {
            languageMap = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
            Lists[listName] = languageMap;
        }

        if (!languageMap.TryGetValue(language, out var idMap))
        {
            idMap = [];
            languageMap[language] = idMap;
        }

        idMap[id] = text ?? "";
        if (!AllListNames.Contains(listName, StringComparer.OrdinalIgnoreCase))
        {
            AllListNames.Add(listName);
            AllListNames.Sort(StringComparer.OrdinalIgnoreCase);
        }

        if (!Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            Languages.Add(language);
            Languages.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetReference(string listName, int id, string language, int row, int column)
    {
        References[BuildReferenceKey(listName, id, language)] = new DescriptionTextReference
        {
            ListName = listName,
            Id = id,
            Language = language,
            Row = row,
            Column = column
        };
    }

    public string GetComment(string listName)
    {
        return !string.IsNullOrWhiteSpace(listName) && Comments.TryGetValue(listName, out var comment)
            ? comment
            : "";
    }

    public void SetComment(string listName, string comment)
    {
        if (!string.IsNullOrWhiteSpace(listName) && !string.IsNullOrWhiteSpace(comment))
        {
            Comments[listName] = comment.Trim();
        }
    }

    public bool TryGetReference(string listName, int id, string language, out DescriptionTextReference reference)
    {
        return References.TryGetValue(BuildReferenceKey(listName, id, language), out reference!);
    }

    private static string BuildReferenceKey(string listName, int id, string language)
    {
        return $"{listName}|{id}|{language}".ToUpperInvariant();
    }
}

public sealed class DescriptionTextReference
{
    public string ListName { get; set; } = "";
    public int Id { get; set; }
    public string Language { get; set; } = "";
    public int Row { get; set; }
    public int Column { get; set; }
}

public sealed class AlmParExportConfig
{
    public string DocNumber { get; set; } = "";
    public string Author { get; set; } = "";
    public string DocDate { get; set; } = "";
    public string PlantFullName { get; set; } = "";
    public string CustomerBlock { get; set; } = "";
    public string FbNumber { get; set; } = "01";
    public string ExportLanguageCode { get; set; } = "en-US";
    public string ExportLanguage { get; set; } = "ENG";
    public bool ExportPdf { get; set; } = true;
    public bool ExportDocx { get; set; } = true;
    public string LastOutputPath { get; set; } = "";
    public List<AlmParExportPlcMapping> PlcMappings { get; set; } = [];

    public void SyncWithDatabases(IEnumerable<PlcDatabase> databases)
    {
        var databaseList = databases.ToList();
        foreach (var database in databaseList)
        {
            var mapping = PlcMappings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
            if (mapping is null)
            {
                mapping = new AlmParExportPlcMapping
                {
                    PlcDatabaseId = database.Id,
                    PlcName = database.Name
                };
                PlcMappings.Add(mapping);
            }

            mapping.PlcName = database.Name;
            if (!mapping.ExportAlm && !mapping.ExportPar)
            {
                mapping.ExportAlm = true;
                mapping.ExportPar = true;
            }
        }

        PlcMappings = PlcMappings
            .Where(mapping => databaseList.Any(database => database.Id == mapping.PlcDatabaseId))
            .OrderBy(mapping => mapping.PlcName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AlmParExportPlcMapping
{
    public string PlcDatabaseId { get; set; } = "";
    public string PlcName { get; set; } = "";
    public bool Include { get; set; } = true;
    public bool ExportAlm { get; set; } = true;
    public bool ExportPar { get; set; } = true;
    public string AlmExcludeSpec { get; set; } = "";
    public string ParExcludeSpec { get; set; } = "";
}

public sealed class TiaSyncConfig
{
    public bool Enabled { get; set; } = true;
    public TiaProjectBinding ProjectBinding { get; set; } = new();
    public List<TiaPlcBinding> PlcBindings { get; set; } = [];
    public List<TiaScadaBinding> ScadaBindings { get; set; } = [];

    public void SyncWithDatabases(IEnumerable<PlcDatabase> databases, DbCatalog? catalog)
    {
        var databaseList = databases.ToList();
        var hasSequenceDataBlocks = catalog?.DataBlockNames.Any(row =>
            row.DbName.Contains("SEQ", StringComparison.OrdinalIgnoreCase)
            || row.DbName.Contains("SEQUENCE", StringComparison.OrdinalIgnoreCase)) == true;
        foreach (var database in databaseList)
        {
            var binding = GetOrCreate(database.Id, database.Name);
            if (string.IsNullOrWhiteSpace(binding.DeviceName))
            {
                binding.DeviceName = database.Name;
            }

            if (string.IsNullOrWhiteSpace(binding.SoftwareName))
            {
                binding.SoftwareName = database.Name;
            }

            SyncArtifacts(binding, catalog);
            if (hasSequenceDataBlocks
                && !binding.IncludeSequenceInGlobalSync
                && binding.SectionStates.All(state => !state.Section.Equals("DB SEQUENCE", StringComparison.OrdinalIgnoreCase)))
            {
                binding.IncludeSequenceInGlobalSync = true;
            }
        }

        PlcBindings = PlcBindings
            .Where(binding => databaseList.Any(database => database.Id == binding.PlcDatabaseId))
            .ToList();
    }

    public TiaPlcBinding GetOrCreate(string plcDatabaseId, string plcName)
    {
        var binding = PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == plcDatabaseId);
        if (binding is null)
        {
            binding = new TiaPlcBinding
            {
                PlcDatabaseId = plcDatabaseId,
                PlcName = plcName,
                DeviceName = plcName,
                SoftwareName = plcName
            };
            PlcBindings.Add(binding);
        }

        binding.PlcName = plcName;
        return binding;
    }

    /// <summary>
    /// Keeps the SCADA database -> WinCC Unified device mapping in step with the project's
    /// SCADA databases (mirrors <see cref="SyncWithDatabases"/> for PLCs). Each SCADA database
    /// maps to one Unified device; pulling from another device means adding a SCADA database.
    /// </summary>
    public void SyncWithScadaDatabases(IEnumerable<ScadaDatabase> scadaDatabases)
    {
        var list = scadaDatabases.ToList();
        foreach (var database in list)
        {
            GetOrCreateScada(database.Id, database.Name);
        }

        ScadaBindings = ScadaBindings
            .Where(binding => list.Any(database => database.Id == binding.ScadaDatabaseId))
            .ToList();
    }

    public TiaScadaBinding GetOrCreateScada(string scadaDatabaseId, string scadaName)
    {
        var binding = ScadaBindings.FirstOrDefault(item => item.ScadaDatabaseId == scadaDatabaseId);
        if (binding is null)
        {
            binding = new TiaScadaBinding
            {
                ScadaDatabaseId = scadaDatabaseId,
                ScadaName = scadaName
            };
            ScadaBindings.Add(binding);
        }

        binding.ScadaName = scadaName;
        return binding;
    }

    private static void SyncArtifacts(TiaPlcBinding binding, DbCatalog? catalog)
    {
        AddGeneratedArtifact(binding, "FB_DI", "FB DI", "FB_DI_x.udt", "DT_DI_PLC", TiaArtifactKind.PlcType);
        AddGeneratedArtifact(binding, "FB_DO", "FB DO", "FB_DO_x.udt", "DT_DO_PLC", TiaArtifactKind.PlcType);
        AddGeneratedArtifact(binding, "FB_AI", "FB AI", "FB_AI_x.udt", "DT_AI_PLC", TiaArtifactKind.PlcType);
        AddGeneratedArtifact(binding, "FB_RTD", "FB RTD", "FB_RTD_x.udt", "DT_RTD_PLC", TiaArtifactKind.PlcType);
        AddGeneratedArtifact(binding, "FB_COUNTER", "FB COUNTER", "FB_COUNTER_x.udt", "DT_COUNTER_PLC", TiaArtifactKind.PlcType);
        AddGeneratedArtifact(binding, "FB_AO", "FB AO", "FB_AO_x.udt", "DT_AO_PLC", TiaArtifactKind.PlcType);

        if (catalog is null)
        {
            return;
        }

        AddDataBlockNameArtifacts(binding, catalog, "DB DI");
        AddDataBlockNameArtifacts(binding, catalog, "DB DO");
        AddDataBlockNameArtifacts(binding, catalog, "DB MEAS");
        AddDataBlockNameArtifacts(binding, catalog, "DB AO");
        AddDataBlockNameArtifacts(binding, catalog, "DB ALM");
        AddDataBlockNameArtifacts(binding, catalog, "DB PAR");
        AddDataBlockNameArtifacts(binding, catalog, "DB SEQUENCE");
        AddDataBlockNameArtifacts(binding, catalog, "DB SEQ");
    }

    private static void AddDataBlockNameArtifacts(TiaPlcBinding binding, DbCatalog catalog, string dbName)
    {
        foreach (var row in catalog.DataBlockNames.Where(item => item.DbName.Equals(dbName, StringComparison.OrdinalIgnoreCase)))
        {
            var key = $"{row.BlockKind}:{dbName}:{row.Number}:{row.DataBlockName}";
            var kind = row.BlockKind == DbDataBlockNameDefaults.ConfigurationKind
                ? TiaArtifactKind.PlcType
                : TiaArtifactKind.PlcBlock;
            AddGeneratedArtifact(binding, key, $"{dbName} {row.BlockKind} {row.Number}", row.FileName, row.DataBlockName, kind);
        }
    }

    private static void AddGeneratedArtifact(TiaPlcBinding binding, string key, string displayName, string sourceFileName, string targetName, TiaArtifactKind kind)
    {
        var artifact = binding.Artifacts.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            artifact = new TiaArtifactBinding
            {
                Key = key,
                DisplayName = displayName,
                SourceFileName = sourceFileName,
                TargetName = targetName,
                Kind = kind
            };
            binding.Artifacts.Add(artifact);
            return;
        }

        artifact.DisplayName = string.IsNullOrWhiteSpace(artifact.DisplayName) ? displayName : artifact.DisplayName;
        artifact.SourceFileName = string.IsNullOrWhiteSpace(artifact.SourceFileName) ? sourceFileName : artifact.SourceFileName;
        artifact.TargetName = string.IsNullOrWhiteSpace(artifact.TargetName) ? targetName : artifact.TargetName;
        artifact.Kind = kind;
    }
}

public sealed class TiaProjectBinding
{
    public string PortalVersion { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public int ProcessId { get; set; }
    public string PortalPath { get; set; } = "";
    public string LastAttachedAtUtc { get; set; } = "";
}

public sealed class TiaPlcBinding
{
    public string PlcDatabaseId { get; set; } = "";
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string SyncState { get; set; } = "Not mapped";
    public bool IncludeSequenceInGlobalSync { get; set; }
    public List<TiaSectionSyncState> SectionStates { get; set; } = [];
    public List<TiaArtifactBinding> Artifacts { get; set; } = [];
    public List<TiaPlcObjectEntry> Objects { get; set; } = [];
}

public sealed class TiaScadaBinding
{
    public string ScadaDatabaseId { get; set; } = "";
    public string ScadaName { get; set; } = "";
    /// <summary>WinCC Unified device the SCADA database is mapped to (e.g. "SCADA_PC").
    /// Empty = not mapped. Used by all Unified operations (set internal tag, screens, text lists).</summary>
    public string DeviceName { get; set; } = "";
    public string SyncState { get; set; } = "Not mapped";
}

public sealed class TiaPlcObjectEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Number { get; set; } = "";
    public string ProgrammingLanguage { get; set; } = "";
}

public sealed class TiaSectionSyncState
{
    public string Section { get; set; } = "";
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
    public string LastCheckedAtUtc { get; set; } = "";
}

public sealed class TiaArtifactBinding
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public TiaArtifactKind Kind { get; set; }
    public string SourceFileName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string LastRemoteFingerprint { get; set; } = "";
    public string LastLocalFingerprint { get; set; } = "";
    public string LastComparedAtUtc { get; set; } = "";
}

public enum TiaArtifactKind
{
    PlcBlock = 0,
    PlcType = 1,
    HmiTextList = 2
}

public sealed class ProjectPaths
{
    public string PlcToConfigSourceFolder { get; set; } = "";
    public string RSourceFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string TiaPortalExportFolder { get; set; } = "";
    public string UnifiedJsConnectorFolder { get; set; } = "";
    public string UnifiedJsConnectorServerPath { get; set; } = "";
}

public sealed class ScadaDatabase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "SCADA 1";
    public bool IsActive { get; set; }
    public string SourceTextPath { get; set; } = "";
    public string LastGeneratedPath { get; set; } = "";
    public string LastImportedAtUtc { get; set; } = "";
    public List<ScadaConfigVariable> Variables { get; set; } = [];
    public List<ScadaTagWrite> TagWrites { get; set; } = [];

    public static ScadaDatabase CreateDefault()
    {
        return new ScadaDatabase();
    }

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "SCADA";
        }

        Variables ??= [];
        TagWrites ??= [];
        var usedIds = new HashSet<int>();
        var nextId = 1;
        foreach (var variable in Variables)
        {
            if (variable.Id <= 0 || !usedIds.Add(variable.Id))
            {
                while (usedIds.Contains(nextId))
                {
                    nextId++;
                }

                variable.Id = nextId;
                usedIds.Add(variable.Id);
            }

            variable.Type = NormalizeType(variable.Type, variable.Value);
            variable.Section ??= "";
            variable.Name ??= "";
            variable.Value ??= "";
            variable.Tag ??= "";
            variable.Comment ??= "";
        }

        var usedTagIds = new HashSet<int>();
        var tagId = 1;
        foreach (var tagWrite in TagWrites)
        {
            if (tagWrite.Id <= 0 || !usedTagIds.Add(tagWrite.Id))
            {
                while (usedTagIds.Contains(tagId))
                {
                    tagId++;
                }

                tagWrite.Id = tagId;
                usedTagIds.Add(tagWrite.Id);
            }

            tagWrite.Tag ??= "";
            tagWrite.VariableName ??= "";
            tagWrite.Section ??= "";
            tagWrite.Type ??= "";
            tagWrite.Value ??= "";
            tagId = Math.Max(tagId + 1, tagWrite.Id + 1);
        }
    }

    public static string NormalizeType(string? type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.Trim().Equals("STRING", StringComparison.OrdinalIgnoreCase) ? "STRING" : "INT";
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "INT" : "STRING";
    }
}

public sealed class ScadaConfigVariable
{
    public int Id { get; set; }
    public string Section { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "INT";
    public string Value { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Comment { get; set; } = "";
    public int SourceLine { get; set; }
}

public sealed class ScadaTagWrite
{
    private string _type = "";
    private string _value = "";

    public int Id { get; set; }
    public string Section { get; set; } = "";
    public string Tag { get; set; } = "";
    public string VariableName { get; set; } = "";
    public string Type
    {
        get => LinkedVariable?.Type ?? _type;
        set
        {
            _type = value ?? "";
            if (LinkedVariable is not null)
            {
                LinkedVariable.Type = ScadaDatabase.NormalizeType(value, LinkedVariable.Value);
            }
        }
    }
    public string Value
    {
        get => LinkedVariable?.Value ?? _value;
        set
        {
            _value = value ?? "";
            if (LinkedVariable is not null)
            {
                LinkedVariable.Value = LinkedVariable.Type.Equals("INT", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeIntValue(value)
                    : value ?? "";
            }
        }
    }
    public int SourceLine { get; set; }

    [JsonIgnore]
    public ScadaConfigVariable? LinkedVariable { get; set; }

    private static string NormalizeIntValue(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "0";
    }
}

public sealed class ScadaSectionSummary
{
    public string Section { get; set; } = "";
    public int Variables { get; set; }
    public int IntValues { get; set; }
    public int StringValues { get; set; }
    public int TagWrites { get; set; }
}

public sealed class PlcDatabase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "PLC";
    [JsonIgnore]
    public bool IsActive { get; set; }
    [JsonIgnore]
    public string TiaSyncVisualState { get; set; } = "NotAttached";
    [JsonIgnore]
    public string TiaOnlineState { get; set; } = "";
    public List<DigitalSignalConfig> DigitalInputs { get; set; } = [];
    public List<DigitalSignalConfig> DigitalOutputs { get; set; } = [];
    public List<AnalogSignalConfig> AnalogInputs { get; set; } = [];
    public List<AnalogSignalConfig> RtdInputs { get; set; } = [];
    public List<CounterConfig> Counters { get; set; } = [];
    public List<AnalogOutputConfig> AnalogOutputs { get; set; } = [];
    public List<DeviceConfig> Devices { get; set; } = [];
    public ModuleCatalog ModuleCatalog { get; set; } = new();
    public DbCatalog DbCatalog { get; set; } = new();

    public static PlcDatabase CreateDefault(string name)
    {
        return new PlcDatabase
        {
            Name = name,
            ModuleCatalog = ModuleCatalog.CreateDefault(),
            DigitalInputs = SignalFactory.CreateDigitalRows(16, 16),
            DigitalOutputs = SignalFactory.CreateDigitalRows(16, 16),
            AnalogInputs = SignalFactory.CreateAnalogRows(6, 8),
            RtdInputs = SignalFactory.CreateAnalogRows(6, 8),
            Counters = SignalFactory.CreateCounterRows(4),
            AnalogOutputs = SignalFactory.CreateAnalogOutputRows(2, 4),
            Devices = DeviceConfig.CreateDefaultRows()
        };
    }

    public static PlcDatabase FromProject(CwcProject project, string name)
    {
        return new PlcDatabase
        {
            Name = name,
            DigitalInputs = project.DigitalInputs,
            DigitalOutputs = project.DigitalOutputs,
            AnalogInputs = project.AnalogInputs,
            RtdInputs = project.RtdInputs,
            Counters = project.Counters,
            AnalogOutputs = project.AnalogOutputs,
            Devices = project.Devices,
            ModuleCatalog = project.ModuleCatalog,
            DbCatalog = project.DbCatalog
        };
    }

    public void CaptureFrom(CwcProject project)
    {
        DigitalInputs = project.DigitalInputs;
        DigitalOutputs = project.DigitalOutputs;
        AnalogInputs = project.AnalogInputs;
        RtdInputs = project.RtdInputs;
        Counters = project.Counters;
        AnalogOutputs = project.AnalogOutputs;
        Devices = project.Devices;
        ModuleCatalog = project.ModuleCatalog;
        DbCatalog = project.DbCatalog;
    }

    public void ApplyTo(CwcProject project)
    {
        project.DigitalInputs = DigitalInputs;
        project.DigitalOutputs = DigitalOutputs;
        project.AnalogInputs = AnalogInputs;
        project.RtdInputs = RtdInputs;
        project.Counters = Counters;
        project.AnalogOutputs = AnalogOutputs;
        project.Devices = Devices;
        project.ModuleCatalog = ModuleCatalog;
        project.DbCatalog = DbCatalog;
    }
}

public sealed class ModuleCatalog
{
    public List<ModuleNameRow> DigitalInputs { get; set; } = [];
    public List<ModuleNameRow> DigitalOutputs { get; set; } = [];
    public List<ModuleNameRow> AnalogInputs { get; set; } = [];
    public List<ModuleNameRow> RtdInputs { get; set; } = [];
    public List<ModuleNameRow> Counters { get; set; } = [];
    public List<ModuleNameRow> AnalogOutputs { get; set; } = [];

    public static ModuleCatalog CreateDefault()
    {
        return new ModuleCatalog
        {
            DigitalInputs = CreateDefaultRows(16),
            DigitalOutputs = CreateDefaultRows(16),
            AnalogInputs = CreateDefaultRows(6),
            RtdInputs = CreateDefaultRows(6),
            Counters = CreateDefaultRows(4),
            AnalogOutputs = CreateDefaultRows(2)
        };
    }

    public static List<ModuleNameRow> CreateDefaultRows(int count)
    {
        return Enumerable.Range(0, count)
            .Select(moduleNumber => new ModuleNameRow { ModuleNumber = moduleNumber })
            .ToList();
    }
}

public sealed class ModuleNameRow
{
    public int ModuleNumber { get; set; }
    public string ModuleName { get; set; } = "";
}

public enum SignalConfigState
{
    Disabled = 0,
    Enabled = 1,
    Inverted = 2
}

public sealed class DigitalSignalConfig : ObservableModel, IValidationState
{
    private int _moduleNumber;
    private string _moduleName = "";
    private int _port;
    private SignalConfigState _config;
    private int _id;
    private string _structure = "";
    private string _statusNameDefinition = "";
    private int _calcConfig;
    private int _calcId;
    private string _structName = "";
    private string _signalName = "";
    private bool _hasValidationError;

    public int ModuleNumber { get => _moduleNumber; set => SetField(ref _moduleNumber, value); }
    public string ModuleName { get => _moduleName; set => SetField(ref _moduleName, value); }
    public int Port { get => _port; set => SetField(ref _port, value); }
    public SignalConfigState Config { get => _config; set => SetField(ref _config, value); }
    public int Id { get => _id; set => SetField(ref _id, value); }
    public string Structure { get => _structure; set => SetField(ref _structure, value); }
    public string StatusNameDefinition { get => _statusNameDefinition; set => SetField(ref _statusNameDefinition, value); }
    public int CalcConfig { get => _calcConfig; set => SetField(ref _calcConfig, value); }
    public int CalcId { get => _calcId; set => SetField(ref _calcId, value); }
    public string StructName { get => _structName; set => SetField(ref _structName, value); }
    public string SignalName { get => _signalName; set => SetField(ref _signalName, value); }
    public bool HasValidationError { get => _hasValidationError; set => SetField(ref _hasValidationError, value); }
}

public sealed class AnalogSignalConfig : ObservableModel, IValidationState
{
    private int _moduleNumber;
    private string _moduleName = "";
    private int _port;
    private SignalConfigState _config;
    private int _id;
    private string _structure = "";
    private string _measurementName = "";
    private double _filterTime;
    private SignalConfigState _customLimits;
    private double _lowLimit;
    private double _highLimit;
    private int _calcEnable;
    private int _calcConfig;
    private int _calcMeasurement;
    private string _signalName = "";
    private string _unit = "";
    private bool _hasValidationError;

    public int ModuleNumber { get => _moduleNumber; set => SetField(ref _moduleNumber, value); }
    public string ModuleName { get => _moduleName; set => SetField(ref _moduleName, value); }
    public int Port { get => _port; set => SetField(ref _port, value); }
    public SignalConfigState Config { get => _config; set => SetField(ref _config, value); }
    public int Id { get => _id; set => SetField(ref _id, value); }
    public string Structure { get => _structure; set => SetField(ref _structure, value); }
    public string MeasurementName { get => _measurementName; set => SetField(ref _measurementName, value); }
    public double FilterTime { get => _filterTime; set => SetField(ref _filterTime, value); }
    public SignalConfigState CustomLimits { get => _customLimits; set => SetField(ref _customLimits, value); }
    public double LowLimit { get => _lowLimit; set => SetField(ref _lowLimit, value); }
    public double HighLimit { get => _highLimit; set => SetField(ref _highLimit, value); }
    public int CalcEnable { get => _calcEnable; set => SetField(ref _calcEnable, value); }
    public int CalcConfig { get => _calcConfig; set => SetField(ref _calcConfig, value); }
    public int CalcMeasurement { get => _calcMeasurement; set => SetField(ref _calcMeasurement, value); }
    public string SignalName { get => _signalName; set => SetField(ref _signalName, value); }
    public string Unit { get => _unit; set => SetField(ref _unit, value); }
    public bool HasValidationError { get => _hasValidationError; set => SetField(ref _hasValidationError, value); }
}

public sealed class CounterConfig : ObservableModel, IValidationState
{
    private int _moduleNumber;
    private string _moduleName = "";
    private SignalConfigState _config;
    private int _id;
    private string _structure = "";
    private string _measurementName = "";
    private double _filterTime;
    private int _calcConfig;
    private int _calcMeasurement;
    private string _signalName = "";
    private string _unit = "";
    private bool _hasValidationError;

    public int ModuleNumber { get => _moduleNumber; set => SetField(ref _moduleNumber, value); }
    public string ModuleName { get => _moduleName; set => SetField(ref _moduleName, value); }
    public SignalConfigState Config { get => _config; set => SetField(ref _config, value); }
    public int Id { get => _id; set => SetField(ref _id, value); }
    public string Structure { get => _structure; set => SetField(ref _structure, value); }
    public string MeasurementName { get => _measurementName; set => SetField(ref _measurementName, value); }
    public double FilterTime { get => _filterTime; set => SetField(ref _filterTime, value); }
    public int CalcConfig { get => _calcConfig; set => SetField(ref _calcConfig, value); }
    public int CalcMeasurement { get => _calcMeasurement; set => SetField(ref _calcMeasurement, value); }
    public string SignalName { get => _signalName; set => SetField(ref _signalName, value); }
    public string Unit { get => _unit; set => SetField(ref _unit, value); }
    public bool HasValidationError { get => _hasValidationError; set => SetField(ref _hasValidationError, value); }
}

public sealed class AnalogOutputConfig : ObservableModel, IValidationState
{
    private int _moduleNumber;
    private string _moduleName = "";
    private int _port;
    private SignalConfigState _config;
    private int _id;
    private string _systemName = "";
    private string _signalName = "";
    private bool _hasValidationError;

    public int ModuleNumber { get => _moduleNumber; set => SetField(ref _moduleNumber, value); }
    public string ModuleName { get => _moduleName; set => SetField(ref _moduleName, value); }
    public int Port { get => _port; set => SetField(ref _port, value); }
    public SignalConfigState Config { get => _config; set => SetField(ref _config, value); }
    public int Id { get => _id; set => SetField(ref _id, value); }
    public string SystemName { get => _systemName; set => SetField(ref _systemName, value); }
    public string SignalName { get => _signalName; set => SetField(ref _signalName, value); }
    public bool HasValidationError { get => _hasValidationError; set => SetField(ref _hasValidationError, value); }
}

public sealed class DeviceConfig
{
    public SignalConfigState Config { get; set; }
    public string SentronName { get; set; } = "";
    public int FirstValueId { get; set; }
    public double PowerScalingFactor { get; set; }
    public double VoltageScaleFactor { get; set; }
    public double CurrentScalingFactor { get; set; }

    public static List<DeviceConfig> CreateDefaultRows()
    {
        return Enumerable.Range(1, 8)
            .Select(index => new DeviceConfig
            {
                Config = SignalConfigState.Disabled,
                SentronName = $"SENTRON_{index}",
                FirstValueId = 1000 + ((index - 1) * 32),
                PowerScalingFactor = 1,
                VoltageScaleFactor = 1,
                CurrentScalingFactor = 1
            })
            .ToList();
    }
}

public sealed class TableDesignSettings
{
    public string Layout { get; set; } = "Classic";
    public bool ConditionalColoringEnabled { get; set; } = true;
}

public sealed class DbCatalog
{
    public List<DbStringRow> DiRows { get; set; } = [];
    public List<DbDiSystemNameRow> DiSystemNameRows { get; set; } = [];
    public string DiStructTypePrefix { get; set; } = "DT_DIGITAL_INPUT_";
    public string DiSimulatorTypePrefix { get; set; } = "DT_SIMULATOR_DIGITAL_OUTPUT_";
    public List<DbColorSetting> DiColorSettings { get; set; } = [];
    public DbTextLengthSettings DiTextLengths { get; set; } = DbTextLengthSettings.CreateDigitalInputDefaults();
    public List<DbStringRow> DoRows { get; set; } = [];
    public List<DbDiSystemNameRow> DoSystemNameRows { get; set; } = [];
    public string DoStructTypePrefix { get; set; } = "DT_DIGITAL_OUTPUT_";
    public string DoSimulatorTypePrefix { get; set; } = "DT_SIMULATOR_DIGITAL_INPUTS_";
    public DbTextLengthSettings DoTextLengths { get; set; } = DbTextLengthSettings.CreateDigitalOutputDefaults();
    public List<DbStringRow> MeasRows { get; set; } = [];
    public List<DbDiSystemNameRow> MeasSystemNameRows { get; set; } = [];
    public string MeasStructTypePrefix { get; set; } = "DT_MEASUREMENT_SYSTEM_";
    public string MeasSimulatorTypePrefix { get; set; } = "DT_SIMULATOR_ANA_CTRL_";
    public DbTextLengthSettings MeasTextLengths { get; set; } = DbTextLengthSettings.CreateMeasDefaults();
    public List<DbAoRow> AoRows { get; set; } = [];
    public List<DbDiSystemNameRow> AoSystemNameRows { get; set; } = [];
    public string AoStructTypePrefix { get; set; } = "DT_ANA_CTRL_";
    public DbTextLengthSettings AoTextLengths { get; set; } = DbTextLengthSettings.CreateMeasDefaults();
    public List<DbStringRow> AlarmRows { get; set; } = [];
    public List<DbDiSystemNameRow> AlarmSystemNameRows { get; set; } = [];
    public string AlarmStructTypePrefix { get; set; } = "DT_ALM_SYSTEM_";
    public DbTextLengthSettings AlarmTextLengths { get; set; } = DbTextLengthSettings.CreateAlarmDefaults();
    public List<DbStringRow> ParameterRows { get; set; } = [];
    public List<DbDiSystemNameRow> ParameterSystemNameRows { get; set; } = [];
    public string ParameterStructTypePrefix { get; set; } = "DT_PAR_";
    public DbTextLengthSettings ParameterTextLengths { get; set; } = DbTextLengthSettings.CreateParameterDefaults();
    public List<DbValidationListSetting> ValidationLists { get; set; } = DbValidationDefaults.Create();
    public DbConditionalColorSettings ConditionalColors { get; set; } = DbConditionalColorSettings.CreateDefaults();
    public List<DbDataBlockNameSetting> DataBlockNames { get; set; } = DbDataBlockNameDefaults.Create();
    public SequenceCatalog Sequence { get; set; } = SequenceCatalog.CreateDefault();
    public TextListCatalog TextLists { get; set; } = TextListCatalog.CreateDefault();
}

public sealed class TextListCatalog
{
    public List<DbStringRow> AlmBlockingClassRows { get; set; } = [];
    public List<DbStringRow> CabinetRows { get; set; } = [];
    public List<DbStringRow> PortConfigurationRows { get; set; } = [];
    public List<DbStringRow> SeqCmdRows { get; set; } = [];
    public List<DbStringRow> SeqConfigRows { get; set; } = [];

    public static TextListCatalog CreateDefault()
    {
        var catalog = new TextListCatalog();
        catalog.EnsureDefaults();
        return catalog;
    }

    public void EnsureDefaults()
    {
        AlmBlockingClassRows = EnsureRows(AlmBlockingClassRows, 15, "ALM_BLOCKING_CLASS");
        CabinetRows = EnsureRows(CabinetRows, 9, "CABINET");
        PortConfigurationRows = EnsureRows(PortConfigurationRows, 3, "PORT_CONFIGURATION");
        SeqCmdRows = EnsureRows(SeqCmdRows, 2, "SEQ_CMD");
        SeqConfigRows = EnsureRows(SeqConfigRows, 7, "SEQ_CONFIG");
    }

    private static List<DbStringRow> EnsureRows(IEnumerable<DbStringRow>? rows, int count, string prefix)
    {
        var result = (rows ?? [])
            .Where(row => row.Id >= 0 && row.Id < count)
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .OrderBy(row => row.Id)
            .ToList();

        for (var id = 0; id < count; id++)
        {
            if (result.All(row => row.Id != id))
            {
                result.Add(new DbStringRow
                {
                    Id = id,
                    PlcName = $"{prefix}_{id:00}"
                });
            }
        }

        return result.OrderBy(row => row.Id).ToList();
    }
}

public sealed class SequenceCatalog
{
    public List<DbStringRow> ControlStatusRows { get; set; } = [];
    public List<DbStringRow> CommandRows { get; set; } = [];
    public List<SequenceDefinition> Sequences { get; set; } = [];

    public static SequenceCatalog CreateDefault()
    {
        var catalog = new SequenceCatalog
        {
            ControlStatusRows = CreateRows(64, "CTRL_STS"),
            CommandRows = CreateRows(32, "SEQ_CMD"),
            Sequences =
            [
                SequenceDefinition.Create("START", "START", 8),
                SequenceDefinition.Create("NSD", "NSD", 8),
                SequenceDefinition.Create("QSD", "QSD", 8),
                SequenceDefinition.Create("ESD", "ESD", 8)
            ]
        };
        catalog.EnsureDefaults();
        return catalog;
    }

    public void EnsureDefaults()
    {
        ControlStatusRows = EnsureRows(ControlStatusRows, 64, "CTRL_STS");
        CommandRows = EnsureRows(CommandRows, 32, "SEQ_CMD");
        EnsureSequence("START", "START", 8);
        EnsureSequence("NSD", "NSD", 8);
        EnsureSequence("QSD", "QSD", 8);
        EnsureSequence("ESD", "ESD", 8);
        Sequences = Sequences
            .Where(sequence => SequenceDefinition.VisibleKeys.Contains(sequence.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(sequence => SequenceDefinition.SortOrder(sequence.Key))
            .ToList();
        foreach (var sequence in Sequences)
        {
            sequence.EnsureDefaults();
        }
    }

    public SequenceDefinition GetOrCreateSequence(string key)
    {
        EnsureDefaults();
        return Sequences.First(sequence => sequence.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasConfiguredSteps()
    {
        return Sequences.Any(sequence => sequence.Steps.Any(step => step.StepNumber > 0 && !string.IsNullOrWhiteSpace(step.Name)));
    }

    private void EnsureSequence(string key, string title, int defaultLastStep)
    {
        var sequence = Sequences.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (sequence is null)
        {
            Sequences.Add(SequenceDefinition.Create(key, title, defaultLastStep));
        }
        else
        {
            sequence.Key = key;
            sequence.Title = title;
        }
    }

    private static List<DbStringRow> CreateRows(int count, string prefix)
    {
        return Enumerable.Range(0, count)
            .Select(id => new DbStringRow
            {
                Id = id,
                PlcStructure = "",
                PlcName = $"{prefix}_{id:00}",
                NameEnglish = "",
                NameOther = "",
                TrueText = "TRUE"
            })
            .ToList();
    }

    private static List<DbStringRow> EnsureRows(List<DbStringRow>? rows, int count, string prefix)
    {
        var result = rows?.OrderBy(row => row.Id).ToList() ?? [];
        for (var id = 0; id < count; id++)
        {
            var row = result.FirstOrDefault(item => item.Id == id);
            if (row is null)
            {
                result.Add(new DbStringRow { Id = id });
                row = result[^1];
            }

            row.PlcStructure = "";

            if (string.IsNullOrWhiteSpace(row.PlcName))
            {
                row.PlcName = $"{prefix}_{id:00}";
            }

            if (string.IsNullOrWhiteSpace(row.TrueText))
            {
                row.TrueText = "TRUE";
            }
        }

        return result
            .Where(row => row.Id >= 0 && row.Id < count)
            .OrderBy(row => row.Id)
            .ToList();
    }
}

public sealed class SequenceDefinition
{
    public static readonly string[] VisibleKeys = ["START", "NSD", "QSD", "ESD"];

    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public int LastStepNumber { get; set; }
    public List<SequenceStepConfig> Steps { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<SequenceStepConfig> VisibleSteps => Steps
        .Where(step => step.StepNumber > 0 && step.StepNumber <= Math.Clamp(LastStepNumber, 1, 10))
        .OrderBy(step => step.StepNumber);

    public static SequenceDefinition Create(string key, string title, int lastStepNumber)
    {
        var sequence = new SequenceDefinition
        {
            Key = key,
            Title = title,
            LastStepNumber = lastStepNumber
        };
        sequence.EnsureDefaults();
        return sequence;
    }

    public void EnsureDefaults()
    {
        if (LastStepNumber <= 0)
        {
            LastStepNumber = 1;
        }

        for (var stepNumber = 0; stepNumber <= 10; stepNumber++)
        {
            var step = Steps.FirstOrDefault(item => item.StepNumber == stepNumber);
            if (step is null)
            {
                Steps.Add(SequenceStepConfig.Create(stepNumber));
            }
            else
            {
                step.EnsureDefaults();
            }
        }

        Steps = Steps
            .Where(step => step.StepNumber >= 0 && step.StepNumber <= 10)
            .OrderBy(step => step.StepNumber)
            .ToList();
    }

    public static int SortOrder(string key)
    {
        var index = Array.FindIndex(VisibleKeys, item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }
}

public sealed class SequenceStepConfig
{
    public int StepNumber { get; set; }
    public string Name { get; set; } = "";
    public string NameOther { get; set; } = "";
    public int ConditionNumber { get; set; }
    public int CommandNumber { get; set; }
    public bool DurationCheck { get; set; }
    public string EnterDelayTime { get; set; } = "T#0MS";
    public string Duration { get; set; } = "T#0MS";
    public string ExitDelayTime { get; set; } = "T#0MS";
    public List<SequenceConditionSlot> Conditions { get; set; } = [];
    public int FirstCondition1 { get; set; }
    public int FirstCondition2 { get; set; }
    public int FirstCondition3 { get; set; }
    public int FirstCondition4 { get; set; }
    public int SecondCondition1 { get; set; }
    public int SecondCondition2 { get; set; }
    public int FinalCondition { get; set; }
    public List<SequenceCommandSlot> Commands { get; set; } = [];

    public static SequenceStepConfig Create(int stepNumber)
    {
        var step = new SequenceStepConfig { StepNumber = stepNumber };
        step.EnsureDefaults();
        return step;
    }

    public void EnsureDefaults()
    {
        ConditionNumber = NormalizeLogicCount(ConditionNumber);
        CommandNumber = NormalizeCommandCount(CommandNumber);
        Conditions = EnsureSlots(Conditions, index => new SequenceConditionSlot { Index = index });
        Commands = EnsureSlots(Commands, index => new SequenceCommandSlot { Index = index });
    }

    public static int NormalizeLogicCount(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        if (value <= 2)
        {
            return 2;
        }

        return value <= 4 ? 4 : 8;
    }

    public static int NormalizeCommandCount(int value)
    {
        return Math.Clamp(value <= 0 ? 1 : value, 1, 8);
    }

    public SequenceStepConfig CloneForStepNumber(int stepNumber)
    {
        var clone = new SequenceStepConfig
        {
            StepNumber = stepNumber,
            Name = Name,
            NameOther = NameOther,
            ConditionNumber = ConditionNumber,
            CommandNumber = CommandNumber,
            DurationCheck = DurationCheck,
            EnterDelayTime = EnterDelayTime,
            Duration = Duration,
            ExitDelayTime = ExitDelayTime,
            FirstCondition1 = FirstCondition1,
            FirstCondition2 = FirstCondition2,
            FirstCondition3 = FirstCondition3,
            FirstCondition4 = FirstCondition4,
            SecondCondition1 = SecondCondition1,
            SecondCondition2 = SecondCondition2,
            FinalCondition = FinalCondition,
            Conditions = Conditions.Select(slot => slot.Clone()).ToList(),
            Commands = Commands.Select(slot => slot.Clone()).ToList()
        };
        clone.EnsureDefaults();
        return clone;
    }

    public void CopyContentFrom(SequenceStepConfig source)
    {
        var stepNumber = StepNumber;
        var copy = source.CloneForStepNumber(stepNumber);
        Name = copy.Name;
        NameOther = copy.NameOther;
        ConditionNumber = copy.ConditionNumber;
        CommandNumber = copy.CommandNumber;
        DurationCheck = copy.DurationCheck;
        EnterDelayTime = copy.EnterDelayTime;
        Duration = copy.Duration;
        ExitDelayTime = copy.ExitDelayTime;
        FirstCondition1 = copy.FirstCondition1;
        FirstCondition2 = copy.FirstCondition2;
        FirstCondition3 = copy.FirstCondition3;
        FirstCondition4 = copy.FirstCondition4;
        SecondCondition1 = copy.SecondCondition1;
        SecondCondition2 = copy.SecondCondition2;
        FinalCondition = copy.FinalCondition;
        Conditions = copy.Conditions;
        Commands = copy.Commands;
    }

    public int GetFirstCondition(int index)
    {
        return index switch
        {
            1 => FirstCondition1,
            2 => FirstCondition2,
            3 => FirstCondition3,
            4 => FirstCondition4,
            _ => 0
        };
    }

    public void SetFirstCondition(int index, int value)
    {
        switch (index)
        {
            case 1: FirstCondition1 = value; break;
            case 2: FirstCondition2 = value; break;
            case 3: FirstCondition3 = value; break;
            case 4: FirstCondition4 = value; break;
        }
    }

    public int GetSecondCondition(int index)
    {
        return index == 1 ? SecondCondition1 : index == 2 ? SecondCondition2 : 0;
    }

    public void SetSecondCondition(int index, int value)
    {
        switch (index)
        {
            case 1: SecondCondition1 = value; break;
            case 2: SecondCondition2 = value; break;
        }
    }

    private static List<T> EnsureSlots<T>(List<T>? slots, Func<int, T> create)
        where T : ISequenceIndexedSlot
    {
        var result = slots?.OrderBy(slot => slot.Index).ToList() ?? [];
        for (var index = 1; index <= 8; index++)
        {
            var slot = result.FirstOrDefault(item => item.Index == index);
            if (slot is null)
            {
                result.Add(create(index));
            }
        }

        return result
            .Where(slot => slot.Index is >= 1 and <= 8)
            .OrderBy(slot => slot.Index)
            .ToList();
    }
}

public interface ISequenceIndexedSlot
{
    int Index { get; set; }
}

public sealed class SequenceConditionSlot : ISequenceIndexedSlot
{
    public int Index { get; set; }
    public int ControlStatusId { get; set; }
    public bool Invert { get; set; }

    public SequenceConditionSlot Clone()
    {
        return new SequenceConditionSlot
        {
            Index = Index,
            ControlStatusId = ControlStatusId,
            Invert = Invert
        };
    }
}

public sealed class SequenceCommandSlot : ISequenceIndexedSlot
{
    public int Index { get; set; }
    public int CommandId { get; set; }
    public bool On { get; set; }

    public SequenceCommandSlot Clone()
    {
        return new SequenceCommandSlot
        {
            Index = Index,
            CommandId = CommandId,
            On = On
        };
    }
}

public sealed class DbDataBlockNameSetting
{
    public int Number { get; set; }
    public string DbName { get; set; } = "";
    public string BlockKind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DataBlockName { get; set; } = "";
    public string DataTypeName { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public int LastId { get; set; }

    public DbDataBlockNameSetting Clone()
    {
        return new DbDataBlockNameSetting
        {
            Number = Number,
            DbName = DbName,
            BlockKind = BlockKind,
            FileName = FileName,
            DataBlockName = DataBlockName,
            DataTypeName = DataTypeName,
            SourceColumn = SourceColumn,
            LastId = LastId
        };
    }
}

public static class DbDataBlockNameDefaults
{
    public const string ConfigurationKind = "Configuration";
    public const string StringKind = "String";
    public const string GroupNamesKind = "Group names";

    public static List<DbDataBlockNameSetting> Create()
    {
        return
        [
            CreateConfiguration(1, "DB DI", "DB_DI.udt", "DI", "DT_DIGITAL_INPUTS_ALL"),
            CreateConfiguration(2, "DB DO", "DB_DO.udt", "DO", "DT_DIGITAL_OUTPUTS_ALL"),
            CreateConfiguration(3, "DB MEAS", "DB_MEAS.udt", "MEAS", "DT_MEASUREMENT_ALL"),
            CreateConfiguration(5, "DB ALM", "DB_ALM.udt", "ALM", "DT_ALM_ALL"),
            CreateConfiguration(6, "DB SEQUENCE", "CTRL_STATUS.db", "CTRL_STATUS", "DT_SEQUENCE_CONTROL_STATUS_ALL"),
            CreateConfiguration(7, "DB SEQUENCE", "SEQ_CMDs.db", "SEQ_CMDs", "DT_SEQUENCE_COMMAND_ALL"),
            CreateConfiguration(8, "DB PAR", "DB_PAR.udt", "PAR", "DT_PARAM_ALL"),
            CreateConfiguration(28, "DB SEQUENCE", "SEQ_PAR.db", "SEQ_PAR", "DT_START_SEQUENCE_PAR"),

            CreateString(1, "DB DI", "STRINGS_DI.db", "DI_SYSTEM_STRING_NAMES_ENG", "DT_DI_STRING_SYSTEM_NAMES", "STATUS SYSTEM ENG", 607),
            CreateString(2, "DB DI", "STRINGS_DI.db", "DI_STRING_NAMES_ENG", "DT_DI_STRING_NAMES", "STATUS NAME ENG", 607),
            CreateString(3, "DB DI", "STRINGS_DI.db", "DI_SYSTEM_STRING_NAMES_GER", "DT_DI_STRING_SYSTEM_NAMES", "STATUS SYSTEM OTHER", 607),
            CreateString(4, "DB DI", "STRINGS_DI.db", "DI_STRING_NAMES_GER", "DT_DI_STRING_NAMES", "STATUS NAME OTHER", 607),

            CreateString(1, "DB DO", "STRINGS_DO.db", "DO_SYSTEM_STRING_NAMES_ENG", "DT_DO_STRING_SYSTEM_NAMES", "STATUS SYSTEM ENG", 287),
            CreateString(2, "DB DO", "STRINGS_DO.db", "DO_STRING_NAMES_ENG", "DT_DO_STRING_NAMES", "STATUS NAME ENG", 287),
            CreateString(3, "DB DO", "STRINGS_DO.db", "DO_SYSTEM_STRING_NAMES_GER", "DT_DO_STRING_SYSTEM_NAMES", "STATUS SYSTEM OTHER", 287),
            CreateString(4, "DB DO", "STRINGS_DO.db", "DO_STRING_NAMES_GER", "DT_DO_STRING_NAMES", "STATUS NAME OTHER", 287),

            CreateString(1, "DB MEAS", "STRINGS_MEAS.db", "MEAS_STRING_NAMES_ENG", "DT_MEAS_STRING_NAMES", "MEAS NAME ENG", 287),
            CreateString(2, "DB MEAS", "STRINGS_MEAS.db", "MEAS_STRING_NAMES_GER", "DT_MEAS_STRING_NAMES", "MEAS NAME OTHER", 287),

            CreateString(1, "DB ALM", "STRINGS_ALM.db", "ALM_STRING_SYSTEMS_ENG_LANGUAGE", "DT_ALM_STRING_SYSTEM_NAMES", "ALARM SYSTEM ENG", 991),
            CreateString(2, "DB ALM", "STRINGS_ALM.db", "ALM_STRING_NAMES_ENG_LANGUGE", "DT_ALM_STRING_NAMES", "ALARM TEXT ENG", 991),
            CreateString(3, "DB ALM", "STRINGS_ALM.db", "ALM_STRING_SYSTEMS_GER_LANGUAGE", "DT_ALM_STRING_SYSTEM_NAMES", "ALARM SYSTEM OTHER", 991),
            CreateString(4, "DB ALM", "STRINGS_ALM.db", "ALM_STRING_NAMES_GER_LANGUGE", "DT_ALM_STRING_NAMES", "ALARM TEXT OTHER", 991),

            CreateString(1, "DB PAR", "STRINGS_PAR.db", "PAR_STRING_NAMES_ENG_LANGUGE", "DT_PAR_STRING_NAMES", "PARAMETER NAME ENG", 1125),
            CreateString(2, "DB PAR", "STRINGS_PAR.db", "PAR_STRING_NAMES_OTH_LANG", "DT_PAR_STRING_NAMES", "PARAMETER NAME OTHER", 1125),

            CreateGroupNames(2101, "DB DI", "GROUP_DI.db", "DI_GROUP_NAMES"),
            CreateGroupNames(2102, "DB DO", "GROUP_DO.db", "DO_GROUP_NAMES"),
            CreateGroupNames(2103, "DB MEAS", "GROUP_MEAS.db", "MEAS_GROUP_NAMES"),
            CreateGroupNames(2105, "DB ALM", "GROUP_ALM.db", "ALM_GROUP_NAMES"),
            CreateGroupNames(2108, "DB PAR", "GROUP_PAR.db", "PAR_GROUP_NAMES")
        ];
    }

    private static DbDataBlockNameSetting CreateConfiguration(int number, string dbName, string fileName, string dataBlockName, string dataTypeName)
    {
        return new DbDataBlockNameSetting
        {
            Number = number,
            DbName = dbName,
            BlockKind = ConfigurationKind,
            FileName = fileName,
            DataBlockName = dataBlockName,
            DataTypeName = dataTypeName,
            SourceColumn = "",
            LastId = 0
        };
    }

    private static DbDataBlockNameSetting CreateString(int number, string dbName, string fileName, string dataBlockName, string dataTypeName, string sourceColumn, int lastId)
    {
        return new DbDataBlockNameSetting
        {
            Number = number,
            DbName = dbName,
            BlockKind = StringKind,
            FileName = fileName,
            DataBlockName = dataBlockName,
            DataTypeName = dataTypeName,
            SourceColumn = sourceColumn,
            LastId = lastId
        };
    }

    private static DbDataBlockNameSetting CreateGroupNames(int number, string dbName, string fileName, string dataBlockName)
    {
        return new DbDataBlockNameSetting
        {
            Number = number,
            DbName = dbName,
            BlockKind = GroupNamesKind,
            FileName = fileName,
            DataBlockName = dataBlockName,
            SourceColumn = "Group names"
        };
    }
}

public sealed class DbConditionalColorSettings
{
    public string DbAlarmUsedColor { get; set; } = "#C8E6C9";
    public bool DbAlarmColorRequiresUsed { get; set; } = true;
    public string DbAlarmAlarmColor { get; set; } = "White";
    public string DbAlarmStartInterlockColor { get; set; } = "#FFF9C4";
    public string DbAlarmNormalShutdownColor { get; set; } = "#FFE0B2";
    public string DbAlarmQuickShutdownColor { get; set; } = "#FFCDD2";
    public string DbAlarmEmergencyShutdownColor { get; set; } = "#EF9A9A";
    public string DbParameterUsedColor { get; set; } = "#C8E6C9";
    public string DbParameterActiveValueColor { get; set; } = "#FFF9C4";

    public static DbConditionalColorSettings CreateDefaults()
    {
        return new DbConditionalColorSettings();
    }

    public DbConditionalColorSettings Clone()
    {
        return new DbConditionalColorSettings
        {
            DbAlarmUsedColor = DbAlarmUsedColor,
            DbAlarmColorRequiresUsed = DbAlarmColorRequiresUsed,
            DbAlarmAlarmColor = DbAlarmAlarmColor,
            DbAlarmStartInterlockColor = DbAlarmStartInterlockColor,
            DbAlarmNormalShutdownColor = DbAlarmNormalShutdownColor,
            DbAlarmQuickShutdownColor = DbAlarmQuickShutdownColor,
            DbAlarmEmergencyShutdownColor = DbAlarmEmergencyShutdownColor,
            DbParameterUsedColor = DbParameterUsedColor,
            DbParameterActiveValueColor = DbParameterActiveValueColor
        };
    }
}

public sealed class DbValidationListSetting
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string ValuesText { get; set; } = "";

    [JsonIgnore]
    public IReadOnlyList<string> Values => Options.Select(option => option.Value).ToList();

    [JsonIgnore]
    public IReadOnlyList<DbValidationOption> Options => SplitOptions(ValuesText);

    public DbValidationListSetting Clone()
    {
        return new DbValidationListSetting
        {
            Key = Key,
            Name = Name,
            ValuesText = ValuesText
        };
    }

    public static List<string> SplitValues(string valuesText)
    {
        return SplitOptions(valuesText)
            .Select(option => option.Value)
            .ToList();
    }

    public static List<DbValidationOption> SplitOptions(string valuesText)
    {
        var nextCode = 0;
        return valuesText
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ParseOption(line, ref nextCode))
            .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static DbValidationOption ParseOption(string line, ref int nextCode)
    {
        var separators = new[] { " - ", "\t", "|" };
        foreach (var separator in separators)
        {
            var parts = line.Split(separator, 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var parsedCode))
            {
                nextCode = Math.Max(nextCode, parsedCode + 1);
                return new DbValidationOption(parsedCode.ToString(), parts[1]);
            }
        }

        var code = nextCode++;
        return new DbValidationOption(code.ToString(), line);
    }
}

public sealed record DbValidationOption(string Value, string Text)
{
    public string DisplayText => $"{Value} - {Text}";
    public override string ToString() => DisplayText;
}

public static class DbValidationDefaults
{
    public const string DbDiColorValues = "DB_DI.ColorValues";
    public const string DbDiAlarmBoolean = "DB_DI.AlarmBoolean";
    public const string DbMeasLoggingEnable = "DB_MEAS.LoggingEnable";
    public const string DbMeasUnit = "DB_MEAS.Unit";
    public const string DbMeasLoggingTime = "DB_MEAS.LoggingTime";
    public const string DbMeasScope = "DB_MEAS.Scope";
    public const string DbAlmColorMode = "DB_ALM.ColorMode";
    public const string DbAlmEnableDisable = "DB_ALM.EnableDisable";
    public const string DbAlmYesNo = "DB_ALM.YesNo";
    public const string DbAlmTimeBase = "DB_ALM.TimeBase";
    public const string DbAlmAlarmClass = "DB_ALM.AlarmClass";
    public const string DbAlmBlockingClass = "DB_ALM.BlockingClass";
    public const string DbAlmReactToUnit = "DB_ALM.ReactToUnit";
    public const string DbParInUse = "DB_PAR.InUse";
    public const string DbParType = "DB_PAR.Type";
    public const string ListParProgress = "LIST_PAR.Progress";

    public static List<DbValidationListSetting> Create()
    {
        return
        [
            CreateRow(DbDiColorValues, "DB DI - COLOR VAL 0/1", "0 - NO COLOR", "1 - WHITE", "2 - YELLOW", "3 - RED", "4 - GREEN", "5 - BLUE"),
            CreateRow(DbDiAlarmBoolean, "DB DI - ALM CONFIG / ALM INVERT", "0 - FALSE", "1 - TRUE"),
            CreateRow(DbMeasLoggingEnable, "DB MEAS - Logging enable", "0 - FALSE", "1 - TRUE"),
            CreateRow(DbMeasUnit, "DB MEAS / DB AO - UNIT", "0 - -", "1 - V", "2 - A", "3 - W", "4 - Var", "5 - VA", "6 - kV", "7 - kA", "8 - kW", "9 - kVAr", "10 - kVA", "11 - MW", "12 - MVAr", "13 - MVA", "14 - Hz", "15 - %", "16 - °C", "17 - bar", "18 - mm2/s", "19 - mm", "20 - m", "21 - rpm"),
            CreateRow(DbMeasLoggingTime, "DB MEAS - Logging time (s)", "0 - 1 s", "1 - 2 s", "2 - 5 s", "3 - 10 s", "4 - 30 s", "5 - 1 min", "6 - 5 min"),
            CreateRow(DbMeasScope, "DB MEAS - source scope", "0 - Common", "1 - Unit 1", "2 - Unit 2", "3 - Unit 3", "4 - Intake"),
            CreateRow(DbAlmColorMode, "DB ALM - coloring mode", "0 - Color by alarm class without USED condition", "1 - Color by alarm class only when USED is active"),
            CreateRow(DbAlmEnableDisable, "DB ALM - Enable/Disable", "0 - Enable", "1 - Disable"),
            CreateRow(DbAlmYesNo, "DB ALM - No/Yes", "0 - No", "1 - Yes"),
            CreateRow(DbAlmTimeBase, "DB ALM - TIME base / source columns", "0 - MS", "1 - S", "2 - MIN", "3 - H"),
            CreateRow(DbAlmAlarmClass, "DB ALM - ALARM CLASS / SPARE", "0 - Alarm", "1 - Start interlock", "2 - Normal shutdown", "3 - Quick shutdown", "4 - Emergency shutdown"),
            CreateRow(DbAlmBlockingClass, "DB ALM - BLOCKING CLASS", "0 - No blocking", "1 - Digital input PS OFF", "2 - Analog input PS OFF", "3 - External signals PS OFF", "4 - Unit distrb. comm erorr", "5 - BFA distrb. comm erorr", "6 - MV distrb. comm erorr", "7 - GCB distrb. comm erorr", "8 - Intake distrb. comm erorr", "9 - Unit DC MCB trip", "10 - MV DC MCB trip", "11 - GCB DC MCB trip", "12 - Spare"),
            CreateRow(DbAlmReactToUnit, "DB ALM - REACT TO UNIT", "0 - 1", "1 - 2", "2 - 3", "3 - 1,2", "4 - 1,3", "5 - 2,3", "6 - 1,2,3"),
            CreateRow(DbParInUse, "DB PAR - IN USE", "0 - FALSE", "1 - TRUE"),
            CreateRow(DbParType, "DB PAR - PAR TYPE", "0 - REAL", "1 - INT", "2 - TIME"),
            CreateRow(ListParProgress, "LIST - PAR - progress", "0 - Pitanje/In progress", "1 - Not done", "2 - Done")
        ];
    }

    private static DbValidationListSetting CreateRow(string key, string name, params string[] values)
    {
        return new DbValidationListSetting
        {
            Key = key,
            Name = name,
            ValuesText = string.Join(Environment.NewLine, values)
        };
    }
}

public sealed class DbColorSetting
{
    public int Value { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";

    public DbColorSetting Clone()
    {
        return new DbColorSetting
        {
            Value = Value,
            Name = Name,
            Color = Color
        };
    }
}

public sealed class DbTextLengthSettings
{
    public int StatusSystemEnglishMax { get; set; }
    public int StatusNameEnglishMax { get; set; }
    public int StatusSystemOtherMax { get; set; }
    public int StatusNameOtherMax { get; set; }

    public static DbTextLengthSettings CreateDigitalInputDefaults()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = 32,
            StatusNameEnglishMax = 64,
            StatusSystemOtherMax = 32,
            StatusNameOtherMax = 64
        };
    }

    public static DbTextLengthSettings CreateDigitalOutputDefaults()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = 32,
            StatusNameEnglishMax = 72,
            StatusSystemOtherMax = 32,
            StatusNameOtherMax = 72
        };
    }

    public static DbTextLengthSettings CreateMeasDefaults()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = 48,
            StatusNameEnglishMax = 48,
            StatusSystemOtherMax = 48,
            StatusNameOtherMax = 48
        };
    }

    public static DbTextLengthSettings CreateAlarmDefaults()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = 32,
            StatusNameEnglishMax = 64,
            StatusSystemOtherMax = 32,
            StatusNameOtherMax = 64
        };
    }

    public static DbTextLengthSettings CreateParameterDefaults()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = 55,
            StatusNameEnglishMax = 55,
            StatusSystemOtherMax = 55,
            StatusNameOtherMax = 55
        };
    }

    public DbTextLengthSettings Clone()
    {
        return new DbTextLengthSettings
        {
            StatusSystemEnglishMax = StatusSystemEnglishMax,
            StatusNameEnglishMax = StatusNameEnglishMax,
            StatusSystemOtherMax = StatusSystemOtherMax,
            StatusNameOtherMax = StatusNameOtherMax
        };
    }
}

public sealed class DbDiSystemNameRow
{
    public int SystemNumber { get; set; }
    public string SystemPlcShortName { get; set; } = "";
    public string SystemPlcLongName { get; set; } = "";
    public string PlcVariableName { get; set; } = "";
    public string HmiSystemName { get; set; } = "";
    public string HmiSystemNameOther { get; set; } = "";
    public int NameLength { get; set; }
    public string SimulatorDataType { get; set; } = "";

    public DbDiSystemNameRow Clone()
    {
        return new DbDiSystemNameRow
        {
            SystemNumber = SystemNumber,
            SystemPlcShortName = SystemPlcShortName,
            SystemPlcLongName = SystemPlcLongName,
            PlcVariableName = PlcVariableName,
            HmiSystemName = HmiSystemName,
            HmiSystemNameOther = HmiSystemNameOther,
            NameLength = NameLength,
            SimulatorDataType = SimulatorDataType
        };
    }
}

public sealed class DbStringRow : IValidationState
{
    public int Id { get; set; }
    public string PlcStructure { get; set; } = "";
    public string PlcName { get; set; } = "";
    public string SystemNameEnglish { get; set; } = "";
    public string NameEnglish { get; set; } = "";
    public string SystemNameOther { get; set; } = "";
    public string NameOther { get; set; } = "";
    public string Unit { get; set; } = "";
    public string TrueText { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int? ModulePort { get; set; }
    public string ColorValue0 { get; set; } = "";
    public string ColorValue1 { get; set; } = "";
    public string AlarmConfig { get; set; } = "";
    public string AlarmInvert { get; set; } = "";
    public string AlarmId { get; set; } = "";
    public string ExtraColumn1 { get; set; } = "";
    public string ExtraColumn2 { get; set; } = "";
    public string LoggingEnable { get; set; } = "";
    public string LoggingTime { get; set; } = "";
    public string Type { get; set; } = "";
    public string AlarmControl { get; set; } = "";
    public string SrAlarm { get; set; } = "";
    public string AlarmClass { get; set; } = "";
    public string ReactToUnit { get; set; } = "";
    public string Spare { get; set; } = "";
    public string BlockingClass1 { get; set; } = "";
    public string BlockingClass2 { get; set; } = "";
    public string Delay { get; set; } = "";
    public string TimeBase { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceDigId { get; set; } = "";
    public string SourceAnaId { get; set; } = "";
    public string Used { get; set; } = "";
    public string InUse { get; set; } = "";
    public string ParameterType { get; set; } = "";
    public string IntegerValue { get; set; } = "";
    public string TimeValue { get; set; } = "";
    public string RealValue { get; set; } = "";
    public string MinValue { get; set; } = "";
    public string MaxValue { get; set; } = "";
    public bool LimitLlEnabled { get; set; }
    public bool LimitLoEnabled { get; set; }
    public bool LimitHiEnabled { get; set; }
    public bool LimitHhEnabled { get; set; }
    public string AlarmErrId { get; set; } = "";
    public string AlarmLlId { get; set; } = "";
    public string AlarmLoId { get; set; } = "";
    public string AlarmHiId { get; set; } = "";
    public string AlarmHhId { get; set; } = "";
    public string ParVal1Id { get; set; } = "";
    public string ParVal2Id { get; set; } = "";
    public string ParOffsetId { get; set; } = "";
    public string ParScaleFactorId { get; set; } = "";
    public string ParLlId { get; set; } = "";
    public string ParLoId { get; set; } = "";
    public string ParHiId { get; set; } = "";
    public string ParHhId { get; set; } = "";
    public string Ordinator { get; set; } = "";
    public string DescriptionEnglish { get; set; } = "";
    public string DescriptionOther { get; set; } = "";
    public string DescriptionTemplateKey { get; set; } = "";
    /// <summary>
    /// True after the user explicitly unlinked a template from this row.
    /// Stops the automatic text-based template matching from silently
    /// re-linking it (the restored text is identical to the template's).
    /// </summary>
    public bool DescriptionTemplateOptOut { get; set; }
    public bool DescriptionDirty { get; set; }
    public bool HasValidationError { get; set; }
    [JsonIgnore]
    public bool AlarmNeedsRepair => DbRepairEvaluator.AlarmNeedsRepair(this);
    [JsonIgnore]
    public string AlarmRepairSummary => DbRepairEvaluator.GetAlarmRepairSummary(this);
    [JsonIgnore]
    public bool DiNeedsRepair => Services.Validation.RepairSummaryHelper.HasWarnings(Services.Validation.DiRepairEvaluator.Evaluate(this));
    [JsonIgnore]
    public string DiRepairSummary => Services.Validation.RepairSummaryHelper.Summarize(Services.Validation.DiRepairEvaluator.Evaluate(this));
    [JsonIgnore]
    public bool DoNeedsRepair => Services.Validation.RepairSummaryHelper.HasWarnings(Services.Validation.DoRepairEvaluator.Evaluate(this, Services.Validation.ValidationOptions.Default));
    [JsonIgnore]
    public string DoRepairSummary => Services.Validation.RepairSummaryHelper.Summarize(Services.Validation.DoRepairEvaluator.Evaluate(this, Services.Validation.ValidationOptions.Default));
    [JsonIgnore]
    public bool MeasNeedsRepair => Services.Validation.RepairSummaryHelper.HasWarnings(Services.Validation.MeasRepairEvaluator.Evaluate(this));
    [JsonIgnore]
    public string MeasRepairSummary => Services.Validation.RepairSummaryHelper.Summarize(Services.Validation.MeasRepairEvaluator.Evaluate(this));
    [JsonIgnore]
    public bool ParNeedsRepair => Services.Validation.RepairSummaryHelper.HasWarnings(Services.Validation.ParRepairEvaluator.Evaluate(this));
    [JsonIgnore]
    public string ParRepairSummary => Services.Validation.RepairSummaryHelper.Summarize(Services.Validation.ParRepairEvaluator.Evaluate(this));
    // Warning rule changed from "total references > 1" to "writes > 1":
    // a tag can be read many times legitimately, but should only be written
    // from a single place. Total counts stay for informational display.
    [JsonIgnore]
    public bool CrossReferenceValWarning => CrossReferenceValWriteCount.GetValueOrDefault() > 1;
    [JsonIgnore]
    public bool CrossReferenceTempWarning => CrossReferenceTempWriteCount.GetValueOrDefault() > 1;
    [JsonIgnore]
    public int? CrossReferenceValWriteCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceTempWriteCount { get; set; }
    // Writes / Reads (total − writes). Single-write tags with many reads are
    // legitimate; only the write count drives the warning rule.
    [JsonIgnore]
    public string CrossReferenceValDisplay =>
        CrossReferenceValWriteCount.HasValue
            ? $"{CrossReferenceValWriteCount.Value} / {Math.Max(0, CrossReferenceValCount.GetValueOrDefault() - CrossReferenceValWriteCount.Value)}"
            : "";
    [JsonIgnore]
    public string CrossReferenceTempDisplay =>
        CrossReferenceTempWriteCount.HasValue
            ? $"{CrossReferenceTempWriteCount.Value} / {Math.Max(0, CrossReferenceTempCount.GetValueOrDefault() - CrossReferenceTempWriteCount.Value)}"
            : "";
    [JsonIgnore]
    public List<DbValidationOption> SrAlarmOptions { get; set; } = [];
    [JsonIgnore]
    public int? CrossReferenceIdCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceValCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceMeasIdCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceAlmIdCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceTempCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceAlmCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceRValCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceIValCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceTValCount { get; set; }
}

public static class DbRepairEvaluator
{
    public static bool AlarmNeedsRepair(DbStringRow row)
    {
        return GetAlarmRepairReasons(row).Count > 0;
    }

    public static string GetAlarmRepairSummary(DbStringRow row)
    {
        var reasons = GetAlarmRepairReasons(row);
        return reasons.Count == 0 ? "OK" : string.Join(", ", reasons);
    }

    public static IReadOnlyList<string> GetAlarmRepairReasons(DbStringRow row)
    {
        if (!IsTrue(row.Used))
        {
            return [];
        }

        var multilingual = Services.Validation.RepairContext.IsMultilingual;
        var reasons = new List<string>();
        AddIf(reasons, IsSpareLike(row.NameEnglish), "Name ENG");
        AddIf(reasons, multilingual && IsSpareLike(row.NameOther), "Name other");
        AddIf(reasons, IsSpareLike(row.DescriptionEnglish), "Description ENG");
        AddIf(reasons, multilingual && IsSpareLike(row.DescriptionOther), "Description other");
        AddIf(reasons, string.IsNullOrWhiteSpace(row.SystemNameEnglish), "System ENG");
        AddIf(reasons, multilingual && string.IsNullOrWhiteSpace(row.SystemNameOther), "System other");
        AddIf(reasons,
            !string.IsNullOrWhiteSpace(row.DescriptionTemplateKey)
            && (string.IsNullOrWhiteSpace(row.DescriptionEnglish) || (multilingual && string.IsNullOrWhiteSpace(row.DescriptionOther))),
            "Template");

        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsSpareLike(string text)
    {
        return string.IsNullOrWhiteSpace(text)
               || text.Contains("spare", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIf(List<string> reasons, bool condition, string reason)
    {
        if (condition)
        {
            reasons.Add(reason);
        }
    }

    private static bool IsTrue(string value)
    {
        var rawValue = (value ?? "").Trim();
        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return rawValue.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || rawValue.Equals("USED", StringComparison.OrdinalIgnoreCase)
               || rawValue.Equals("YES", StringComparison.OrdinalIgnoreCase)
               || rawValue.Equals("ENABLED", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DbAoRow
{
    public string AoSystemName { get; set; } = "";
    public string AoPlcName { get; set; } = "";
    public int AoNumber { get; set; }
    public string NameEnglish { get; set; } = "";
    public string NameOther { get; set; } = "";
    public string Unit { get; set; } = "";
    public string TrueText { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int? ModulePort { get; set; }
    public double Low { get; set; }
    public double High { get; set; }
    public double Offset { get; set; }
    public double HighLimit { get; set; }
    public string ParValLowId { get; set; } = "";
    public string ParValHighId { get; set; } = "";
    public string ParOffsetId { get; set; } = "";
    public string ParScaleFactorId { get; set; } = "";
    [JsonIgnore]
    public int? CrossReferenceAnaCtrlIdCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceValCount { get; set; }
    [JsonIgnore]
    public int? CrossReferenceValWriteCount { get; set; }
    [JsonIgnore]
    public bool CrossReferenceValWarning => CrossReferenceValWriteCount.GetValueOrDefault() > 1;
    [JsonIgnore]
    public string CrossReferenceValDisplay =>
        CrossReferenceValWriteCount.HasValue
            ? $"{CrossReferenceValWriteCount.Value} / {Math.Max(0, CrossReferenceValCount.GetValueOrDefault() - CrossReferenceValWriteCount.Value)}"
            : "";
    [JsonIgnore]
    public bool AoNeedsRepair => Services.Validation.RepairSummaryHelper.HasWarnings(Services.Validation.AoRepairEvaluator.Evaluate(this, Services.Validation.ValidationOptions.Default));
    [JsonIgnore]
    public string AoRepairSummary => Services.Validation.RepairSummaryHelper.Summarize(Services.Validation.AoRepairEvaluator.Evaluate(this, Services.Validation.ValidationOptions.Default));
}
