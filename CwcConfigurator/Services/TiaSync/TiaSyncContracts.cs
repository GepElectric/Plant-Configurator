namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaDiscoveredPlc
{
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public List<TiaPlcObjectInfo> Objects { get; set; } = [];

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(SoftwareName) || SoftwareName == DeviceName
            ? DeviceName
            : $"{DeviceName} / {SoftwareName}";
        return Objects.Count > 0 ? $"{label} (OBJECTS: {Objects.Count})" : label;
    }
}

public sealed class TiaPlcObjectInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Number { get; set; } = "";
    public string ProgrammingLanguage { get; set; } = "";
}

public sealed class TiaPortalProcessInfo
{
    public int ProcessId { get; set; }
    public string PortalPath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ModeLabel { get; set; } = "";
    public string AcquiredAtLabel { get; set; } = "";

    /// <summary>TIA version ("V18"/"V21") this process belongs to. Stamped by the
    /// session service from the helper that discovered it; drives helper routing on attach.</summary>
    public string Version { get; set; } = "";

    public override string ToString()
    {
        var project = string.IsNullOrWhiteSpace(ProjectName) ? "(no open project)" : ProjectName;
        var versionTag = string.IsNullOrWhiteSpace(Version) ? "" : $" [{Version}]";
        return $"PID {ProcessId}{versionTag} | {project}" + (string.IsNullOrWhiteSpace(ModeLabel) ? "" : $" | {ModeLabel}");
    }
}

public sealed class TiaAttachedSession
{
    public int ProcessId { get; set; }
    public string ProjectName { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string PortalPath { get; set; } = "";
    public string PortalVersion { get; set; } = "";
    public List<TiaDiscoveredPlc> Plcs { get; set; } = [];
}

public interface ITiaSessionService
{
    TiaAttachedSession? CurrentSession { get; }
    Task<IList<TiaPortalProcessInfo>> GetRunningPortalProcessesAsync();
    Task<TiaAttachedSession> AttachToRunningPortalAsync(int processId, string portalVersion);
    Task DisconnectAsync();
    Task<TiaSourceTransferResult> ExportSourcesAsync(IEnumerable<TiaPlcSourceExportRequest> plcs, string outputFolder);
    Task<TiaSourceTransferResult> ImportSourcesAsync(IEnumerable<TiaPlcSourceImportRequest> plcs);
    Task<TiaCrossReferenceResult> GetCrossReferencesAsync(TiaCrossReferenceRequest request);
    Task<TiaCrossReferenceOpenResult> OpenCrossReferenceAsync(TiaCrossReferenceOpenRequest request);
    Task<TiaPlcControlResult> ExecutePlcControlAsync(TiaPlcControlRequest request);
    Task<TiaProjectTextsImportResult> ImportProjectTextsAsync(string workbookPath);
    Task<TiaUnifiedScreensResult> GetUnifiedScreensAsync(string deviceName);
    Task<TiaTextListExportResult> ExportTextListsAsync(string outputFolder, string deviceName);
    Task<TiaTextListImportResult> ImportTextListsAsync(string inputFolder, string deviceName);
    Task<TiaUnifiedDevicesResult> GetUnifiedDevicesAsync();
    Task<TiaUnifiedScriptResult> ExportUnifiedScriptAsync(string outputFolder, string deviceName, string moduleName);
    Task<TiaUnifiedScriptResult> ImportUnifiedScriptAsync(string inputFolder, string deviceName);
    Task<TiaUnifiedScriptResult> ExportUnifiedGraphicsAsync(string outputFolder, string deviceName);
    Task<TiaTextListTypesResult> UpdateTextListTypesAsync(string inputFolder, IReadOnlyList<string> listNames, string comment);
    Task<TiaTextListTypesResult> ExportTextListTypeTextsAsync(string outputFile, IReadOnlyList<string> listNames, bool inWork);
    Task<TiaTextListTypesResult> ImportTextListTypeTextsAsync(string? inputFile, IReadOnlyList<string> listNames, string comment);
}

/// <summary>New versions of text lists that are library types (project library), created from exported documents.</summary>
public sealed class TiaTextListTypesResult
{
    public bool Success { get; set; }
    public List<string> Updated { get; set; } = [];
    public List<string> NotTypes { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaSourceArtifactRequest
{
    public string FileName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Kind { get; set; } = "PlcBlock";
}

public sealed class TiaPlcSourceExportRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string OutputSubfolder { get; set; } = "";
    public List<TiaSourceArtifactRequest> Artifacts { get; set; } = [];
}

public sealed class TiaPlcSourceImportRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public List<string> SourceFiles { get; set; } = [];
    public List<string> ResetDataBlocks { get; set; } = [];
}

public sealed class TiaSourceTransferResult
{
    public List<string> Files { get; set; } = [];
    public List<string> Messages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaProjectTextsImportResult
{
    public string Message { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaUnifiedScreensResult
{
    public List<string> Screens { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaTextListExportResult
{
    public string HmiSoftwareName { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Export / import of a WinCC Unified script module (e.g. AUX_FUNCTIONS) through the TIA helper.</summary>
public sealed class TiaUnifiedScriptResult
{
    public string HmiSoftwareName { get; set; } = "";
    public bool Success { get; set; }
    public List<string> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaUnifiedDevicesResult
{
    public List<string> Devices { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaTextListImportResult
{
    public string HmiSoftwareName { get; set; } = "";
    public bool Success { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaCrossReferenceRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string VariablePath { get; set; } = "";
    public int MaxItems { get; set; } = 50;
}

public sealed class TiaCrossReferenceOpenRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string VariablePath { get; set; } = "";
    public string PreferredBlockName { get; set; } = "";
    public string PreferredNetwork { get; set; } = "";
}

public sealed class TiaCrossReferenceResult
{
    public string VariablePath { get; set; } = "";
    public int UsageCount { get; set; }
    public int WriteCount { get; set; }
    public List<TiaCrossReferenceItem> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaCrossReferenceBatchRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public List<string> VariablePaths { get; set; } = [];
}

public sealed class TiaCrossReferenceBatchResult
{
    public List<TiaCrossReferenceBatchItem> Results { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class TiaCrossReferenceBatchItem
{
    public string VariablePath { get; set; } = "";
    public int UsageCount { get; set; }
    public int WriteCount { get; set; }
}

public sealed class TiaCrossReferenceItem
{
    public string BlockName { get; set; } = "";
    public int Count { get; set; }
    public string Network { get; set; } = "";
    public string ReferenceLocation { get; set; } = "";
    public string ReferenceType { get; set; } = "";
    public string Access { get; set; } = "";
    public string Address { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Device { get; set; } = "";
    public string Path { get; set; } = "";
    public string Snippet { get; set; } = "";
}

public sealed class TiaCrossReferenceOpenResult
{
    public bool OpenedBlock { get; set; }
    public bool InvokedCrossReferenceCommand { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TiaPlcControlRequest
{
    public string PlcName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SoftwareName { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> DataBlockNames { get; set; } = [];
}

public sealed class TiaPlcControlResult
{
    public bool Success { get; set; }
    public bool Supported { get; set; } = true;
    public string Command { get; set; } = "";
    public string State { get; set; } = "";
    public List<string> Messages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public string ToOutputText()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Command))
        {
            lines.Add("Command: " + Command);
        }

        if (!string.IsNullOrWhiteSpace(State))
        {
            lines.Add("State: " + State);
        }

        lines.AddRange(Messages);
        if (Warnings.Count > 0)
        {
            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(warning => "- " + warning));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class TiaLiveSyncResult
{
    public string Message { get; set; } = "";
    public string SnapshotFolder { get; set; } = "";
    public int FileCount { get; set; }
    public int ImportedRows { get; set; }
    public List<string> Messages { get; } = [];
    public List<string> Warnings { get; } = [];

    public string ToOutputText()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Message))
        {
            lines.Add(Message);
        }

        if (!string.IsNullOrWhiteSpace(SnapshotFolder))
        {
            lines.Add("Snapshot: " + SnapshotFolder);
        }

        lines.AddRange(Messages);
        if (Warnings.Count > 0)
        {
            lines.Add("Warnings:");
            lines.AddRange(Warnings.Select(warning => "- " + warning));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
