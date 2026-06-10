using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaLiveSyncService(ITiaSessionService sessionService)
{
    private readonly TiaMappingService _mappingService = new();
    private readonly TiaSourceGenerator _generator = new();
    private readonly TiaPortalExportImporter _importer = new();
    private static readonly string[] KnownSections = ["HW DI", "HW DO", "HW AI", "HW RTD", "HW COUNTER", "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR", "DB SEQUENCE"];

    public async Task<TiaLiveSyncResult> PullAsync(CwcProject project)
    {
        var session = RequireSession();
        ValidateMapping(project, session);

        var originalActiveId = project.ActivePlcDatabaseId;
        var snapshotRoot = CreateSnapshotFolder(project, "pull");
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        Directory.CreateDirectory(remoteRoot);

        var exportRequests = BuildExportRequests(project, remoteRoot);
        var transfer = await sessionService.ExportSourcesAsync(exportRequests, remoteRoot);
        var result = new TiaLiveSyncResult
        {
            SnapshotFolder = snapshotRoot,
            FileCount = transfer.Files.Count
        };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        try
        {
            foreach (var binding in project.TiaSync.PlcBindings.OrderBy(item => item.PlcName, StringComparer.OrdinalIgnoreCase))
            {
                var database = project.PlcDatabases.FirstOrDefault(item => item.Id == binding.PlcDatabaseId);
                if (database is null)
                {
                    continue;
                }

                var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
                database.ApplyTo(project);
                project.ActivePlcDatabaseId = database.Id;

                var files = _importer.Scan(plcFolder);
                if (files.Count == 0)
                {
                    result.Warnings.Add($"No supported TIA export files were found for '{binding.PlcName}'.");
                    continue;
                }

                var typeDefaultImported = 0;
                foreach (var familyName in new[] { "DB DI", "DB DO", "DB MEAS", "DB ALM", "DB PAR" })
                {
                    typeDefaultImported += _importer.ImportDbDefaultValuesFromFolder(project.DbCatalog, familyName, plcFolder);
                }

                var importResult = _importer.Import(project, files);
                var typeNameImported = 0;
                foreach (var familyName in new[] { "DB DI", "DB DO", "DB MEAS", "DB ALM", "DB PAR" })
                {
                    typeNameImported += _importer.ImportDbTypeNamesFromFolder(project.DbCatalog, familyName, plcFolder);
                }

                if (binding.IncludeSequenceInGlobalSync && HasSequenceDataBlocks(project.DbCatalog))
                {
                    var sequenceImported = ImportSequenceFromFolder(project.DbCatalog, plcFolder, "DB SEQUENCE");
                    importResult.Add(new TiaPortalImportFile("SEQUENCE_CONFIG", "DB SEQUENCE configuration", "SEQ_PAR.db", plcFolder, exists: true), sequenceImported);
                }

                database.CaptureFrom(project);
                result.ImportedRows += importResult.ImportedCount + typeDefaultImported + typeNameImported;
                result.Messages.Add($"{binding.PlcName}: imported {typeDefaultImported} UDT default row(s), {importResult.ImportedCount} DB override row(s) and {typeNameImported} UDT name row(s) from {files.Count} file(s).");
                foreach (var message in importResult.Messages)
                {
                    result.Messages.Add("  " + message);
                }

                binding.SyncState = "Pulled from TIA";
                SetAllSectionStates(binding, "InSync", "Pulled from TIA.");
                UpdatePulledFingerprints(binding, plcFolder);
            }
        }
        finally
        {
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"Pull completed: {result.ImportedRows} row(s), {result.FileCount} file(s).";
        return result;
    }

    public async Task<TiaLiveSyncResult> PushAsync(CwcProject project)
    {
        var session = RequireSession();
        ValidateMapping(project, session);

        var originalActiveId = project.ActivePlcDatabaseId;
        var originalOutputFolder = project.Paths.OutputFolder;
        var snapshotRoot = CreateSnapshotFolder(project, "push");
        var pushRoot = Path.Combine(snapshotRoot, "push");
        Directory.CreateDirectory(pushRoot);

        var importRequests = new List<TiaPlcSourceImportRequest>();
        var result = new TiaLiveSyncResult
        {
            SnapshotFolder = snapshotRoot
        };

        try
        {
            foreach (var binding in project.TiaSync.PlcBindings.OrderBy(item => item.PlcName, StringComparer.OrdinalIgnoreCase))
            {
                var database = project.PlcDatabases.FirstOrDefault(item => item.Id == binding.PlcDatabaseId);
                if (database is null)
                {
                    continue;
                }

                database.ApplyTo(project);
                project.ActivePlcDatabaseId = database.Id;
                var plcFolder = Path.Combine(pushRoot, SanitizeName(binding.PlcName));
                project.Paths.OutputFolder = plcFolder;

                var generation = _generator.GenerateAll(project);
                if (!generation.Success)
                {
                    var issues = string.Join(Environment.NewLine, generation.Issues.Select(issue => $"{issue.Severity} {issue.Table}: {issue.Message}"));
                    throw new InvalidOperationException($"Cannot push '{binding.PlcName}' because generation has validation errors:{Environment.NewLine}{issues}");
                }

                var sourceFiles = generation.CreatedFiles
                    .Where(IsTiaSourceFile)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sourceFiles.Count == 0)
                {
                    result.Warnings.Add($"No TIA source files were generated for '{binding.PlcName}'.");
                    continue;
                }

                result.FileCount += sourceFiles.Count;
                result.Messages.Add($"{binding.PlcName}: generated {sourceFiles.Count} source file(s).");
                importRequests.Add(new TiaPlcSourceImportRequest
                {
                    PlcName = binding.PlcName,
                    DeviceName = binding.DeviceName,
                    SoftwareName = binding.SoftwareName,
                    SourceFiles = sourceFiles,
                    ResetDataBlocks = database.DbCatalog.DataBlockNames
                        .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
                        .Where(item => binding.IncludeSequenceInGlobalSync || !IsSequenceDbName(item.DbName))
                        .Select(item => item.DataBlockName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                });
                UpdatePushedFingerprints(binding, sourceFiles);
            }

            var transfer = await sessionService.ImportSourcesAsync(importRequests);
            result.Messages.AddRange(transfer.Messages);
            result.Warnings.AddRange(transfer.Warnings);
            foreach (var binding in project.TiaSync.PlcBindings)
            {
                binding.SyncState = "Pushed to TIA";
                SetAllSectionStates(binding, "InSync", "Pushed to TIA.");
            }
        }
        finally
        {
            project.Paths.OutputFolder = originalOutputFolder;
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"Push completed: {result.FileCount} source file(s) sent to TIA.";
        return result;
    }

    public async Task<TiaLiveSyncResult> PullDbDiAsync(CwcProject project)
    {
        return await PullDbFamilyAsync(project, "DB DI");
    }

    public async Task<TiaLiveSyncResult> CheckDbDiAsync(CwcProject project)
    {
        return await CheckDbFamilyAsync(project, "DB DI", CreateDbSelection("DB DI"));
    }

    public async Task<TiaLiveSyncResult> PushDbDiAsync(CwcProject project)
    {
        return await PushDbFamilyAsync(project, "DB DI", CreateDbSelection("DB DI"));
    }

    public async Task<TiaLiveSyncResult> PullDbAsync(CwcProject project, string dbName)
    {
        return await PullDbFamilyAsync(project, NormalizeDbName(dbName));
    }

    public async Task<TiaLiveSyncResult> CheckDbAsync(CwcProject project, string dbName)
    {
        var normalizedDbName = NormalizeDbName(dbName);
        return await CheckDbFamilyAsync(project, normalizedDbName, CreateDbSelection(normalizedDbName));
    }

    public async Task<TiaLiveSyncResult> PushDbAsync(CwcProject project, string dbName)
    {
        var normalizedDbName = NormalizeDbName(dbName);
        return await PushDbFamilyAsync(project, normalizedDbName, CreateDbSelection(normalizedDbName));
    }

    public async Task<TiaLiveSyncResult> CheckHwSectionAsync(CwcProject project, string section)
    {
        return await CheckHwSectionCoreAsync(project, NormalizeHwSection(section));
    }

    public async Task<TiaLiveSyncResult> PullHwSectionAsync(CwcProject project, string section)
    {
        return await PullHwSectionCoreAsync(project, NormalizeHwSection(section));
    }

    public async Task<TiaLiveSyncResult> PushHwSectionAsync(CwcProject project, string section)
    {
        return await PushHwSectionCoreAsync(project, NormalizeHwSection(section));
    }

    public async Task<TiaLiveSyncResult> CheckHwSectionsAsync(CwcProject project)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var sections = new[] { "DI", "DO", "AI", "RTD", "COUNTER" };
        var snapshotRoot = CreateSnapshotFolder(project, "check_HW_all");
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var transfer = await sessionService.ExportSourcesAsync([CreateHwExportRequest(binding, plcFolder)], remoteRoot);
        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot, FileCount = transfer.Files.Count };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        var hwFile = FindSourceFile(plcFolder, "HW_ID_s.db")
                     ?? throw new FileNotFoundException("Exported HW_ID_s.db was not found.", Path.Combine(plcFolder, "HW_ID_s.db"));

        foreach (var section in sections)
        {
            var clone = CloneProject(project);
            var cloneDatabase = clone.PlcDatabases.FirstOrDefault(item => item.Id == database.Id)
                                ?? throw new InvalidOperationException("Could not clone active PLC database.");
            cloneDatabase.ApplyTo(clone);
            clone.ActivePlcDatabaseId = cloneDatabase.Id;
            var imported = _importer.ImportHwIdsSection(clone, hwFile, section);
            cloneDatabase.CaptureFrom(clone);

            var inSync = HwSectionFingerprint(database, section).Equals(HwSectionFingerprint(cloneDatabase, section), StringComparison.Ordinal);
            SetSectionState(binding, "HW " + section, inSync ? "InSync" : "Differs", inSync ? $"HW {section} is in sync with TIA." : $"HW {section} differs from TIA.");
            result.ImportedRows += imported;
            result.Messages.Add($"{binding.PlcName} / HW {section}: {(inSync ? "in sync" : "differs")}.");
        }

        binding.SyncState = BindingHasDifferences(binding) ? "HW differs" : "HW in sync";
        result.Message = BindingHasDifferences(binding) ? "HW_ID_s differs from TIA." : "HW_ID_s is in sync with TIA.";
        return result;
    }

    private TiaAttachedSession RequireSession()
    {
        return sessionService.CurrentSession
               ?? throw new InvalidOperationException("Attach to TIA Portal first.");
    }

    private void ValidateMapping(CwcProject project, TiaAttachedSession session)
    {
        project.TiaSync ??= new TiaSyncConfig();
        project.TiaSync.SyncWithDatabases(project.PlcDatabases, project.DbCatalog);
        var issues = _mappingService.Validate(project, session);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues));
        }
    }

    private async Task<TiaLiveSyncResult> PullDbFamilyAsync(CwcProject project, string dbName)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var snapshotRoot = CreateSnapshotFolder(project, "pull_" + SanitizeName(dbName));
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var exportRequest = new TiaPlcSourceExportRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            OutputSubfolder = plcFolder,
            Artifacts = CreatePullArtifactsForDb(database.DbCatalog, dbName)
        };
        var transfer = await sessionService.ExportSourcesAsync([exportRequest], remoteRoot);
        var result = new TiaLiveSyncResult
        {
            SnapshotFolder = snapshotRoot,
            FileCount = transfer.Files.Count
        };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            if (dbName.Equals("DB AO", StringComparison.OrdinalIgnoreCase))
            {
                var imported = _importer.ImportAoFromFolder(project.DbCatalog, plcFolder);
                database.CaptureFrom(project);
                result.ImportedRows = imported;
                result.Messages.Add($"{binding.PlcName} / {dbName}: imported {imported} AO row(s) from ANA_CTRL source/type files.");
            }
            else if (IsSequenceDbName(dbName))
            {
                var imported = ImportSequenceFromFolder(project.DbCatalog, plcFolder, dbName);
                database.CaptureFrom(project);
                result.ImportedRows = imported;
                result.Messages.Add($"{binding.PlcName} / {dbName}: imported SEQ_PAR configuration and DB 6/7 reference rows.");
            }
            else
            {
                var files = _importer.Scan(plcFolder);
                var typeDefaultImported = _importer.ImportDbDefaultValuesFromFolder(project.DbCatalog, dbName, plcFolder);
                var importResult = _importer.Import(project, files);
                var typeNameImported = _importer.ImportDbTypeNamesFromFolder(project.DbCatalog, dbName, plcFolder);
                database.CaptureFrom(project);
                result.ImportedRows = importResult.ImportedCount + typeDefaultImported + typeNameImported;
                result.Messages.Add($"{binding.PlcName} / {dbName}: imported {typeDefaultImported} UDT default row(s), {importResult.ImportedCount} DB override row(s) and {typeNameImported} UDT name row(s) from {files.Count} file(s).");
                foreach (var message in importResult.Messages)
                {
                    result.Messages.Add("  " + message);
                }
            }

            binding.SyncState = $"{dbName} pulled";
            SetSectionState(binding, dbName, "InSync", $"{dbName} was pulled from TIA.");
            UpdatePulledFingerprints(binding, plcFolder);
        }
        finally
        {
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = IsSequenceDbName(dbName)
            ? $"{dbName} pull completed: SEQ_PAR plus Control status / Sequence commands updated ({result.ImportedRows} row(s), {result.FileCount} file(s))."
            : $"{dbName} pull completed: {result.ImportedRows} row(s), {result.FileCount} file(s).";
        return result;
    }

    private async Task<TiaLiveSyncResult> CheckDbFamilyAsync(CwcProject project, string dbName, GenerationSelection selection)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var snapshotRoot = CreateSnapshotFolder(project, "check_" + SanitizeName(dbName));
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var localRoot = Path.Combine(snapshotRoot, "local");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);
        Directory.CreateDirectory(localRoot);

        var exportRequest = new TiaPlcSourceExportRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            OutputSubfolder = plcFolder,
            Artifacts = CreatePullArtifactsForDb(database.DbCatalog, dbName)
        };
        var transfer = await sessionService.ExportSourcesAsync([exportRequest], remoteRoot);
        var result = new TiaLiveSyncResult
        {
            SnapshotFolder = snapshotRoot,
            FileCount = transfer.Files.Count
        };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        if (dbName.Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            var clone = CloneProject(project);
            var cloneDatabase = clone.PlcDatabases.FirstOrDefault(item => item.Id == database.Id)
                                ?? throw new InvalidOperationException("Could not clone active PLC database.");
            cloneDatabase.ApplyTo(clone);
            clone.ActivePlcDatabaseId = cloneDatabase.Id;
            result.ImportedRows = _importer.ImportAoFromFolder(clone.DbCatalog, plcFolder);
            cloneDatabase.CaptureFrom(clone);

            var aoInSync = DbFamilyFingerprint(database.DbCatalog, dbName)
                .Equals(DbFamilyFingerprint(cloneDatabase.DbCatalog, dbName), StringComparison.Ordinal);
            binding.SyncState = aoInSync ? $"{dbName} in sync" : $"{dbName} differs";
            SetSectionState(binding, dbName, aoInSync ? "InSync" : "Differs", aoInSync ? $"{dbName} is in sync with TIA." : $"{dbName} differs from TIA.");
            result.Messages.Add($"{binding.PlcName} / {dbName}: compared AO rows from ANA_CTRL source/type files.");
            result.Message = aoInSync
                ? $"{dbName} is in sync with TIA."
                : $"{dbName} differs from TIA.";
            return result;
        }

        if (IsSequenceDbName(dbName))
        {
            var clone = CloneProject(project);
            var cloneDatabase = clone.PlcDatabases.FirstOrDefault(item => item.Id == database.Id)
                                ?? throw new InvalidOperationException("Could not clone active PLC database.");
            cloneDatabase.ApplyTo(clone);
            clone.ActivePlcDatabaseId = cloneDatabase.Id;
            result.ImportedRows = ImportSequenceFromFolder(clone.DbCatalog, plcFolder, dbName);
            cloneDatabase.CaptureFrom(clone);

            var sequenceInSync = DbFamilyFingerprint(database.DbCatalog, dbName)
                .Equals(DbFamilyFingerprint(cloneDatabase.DbCatalog, dbName), StringComparison.Ordinal);
            binding.SyncState = sequenceInSync ? $"{dbName} in sync" : $"{dbName} differs";
            SetSectionState(binding, "DB SEQUENCE", sequenceInSync ? "InSync" : "Differs", sequenceInSync ? "DB SEQUENCE is in sync with TIA." : "DB SEQUENCE differs from TIA.");
            result.Messages.Add($"{binding.PlcName} / {dbName}: compared sequence configuration.");
            result.Message = sequenceInSync
                ? $"{dbName} is in sync with TIA."
                : $"{dbName} differs from TIA.";
            return result;
        }

        var originalOutputFolder = project.Paths.OutputFolder;
        var originalActiveId = project.ActivePlcDatabaseId;
        List<string> sourceFiles;
        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            project.Paths.OutputFolder = localRoot;
            var generation = _generator.GenerateSelected(project, selection);
            if (!generation.Success)
            {
                var issues = string.Join(Environment.NewLine, generation.Issues.Select(issue => $"{issue.Severity} {issue.Table}: {issue.Message}"));
                throw new InvalidOperationException($"Cannot check '{binding.PlcName} / {dbName}' because generation has validation errors:{Environment.NewLine}{issues}");
            }

            var sourceFileNames = GetSourceFileNamesForDb(database.DbCatalog, dbName);
            sourceFiles = generation.CreatedFiles
                .Where(IsTiaSourceFile)
                .Where(path => sourceFileNames.Contains(Path.GetFileName(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            project.Paths.OutputFolder = originalOutputFolder;
            RestoreActiveDatabase(project, originalActiveId);
        }

        var comparison = CompareSourceFiles(sourceFiles, plcFolder, database.DbCatalog, dbName);
        var inSync = comparison.Differences.Count == 0;
        binding.SyncState = inSync ? $"{dbName} in sync" : $"{dbName} differs";
        SetSectionState(binding, dbName, inSync ? "InSync" : "Differs", inSync ? $"{dbName} is in sync with TIA." : $"{dbName} differs from TIA.");
        result.Messages.Add($"{binding.PlcName} / {dbName}: compared {comparison.ComparedCount} source file(s).");
        foreach (var difference in comparison.Differences.Take(12))
        {
            result.Messages.Add("  " + difference);
        }

        if (comparison.Differences.Count > 12)
        {
            result.Messages.Add($"  ... {comparison.Differences.Count - 12} more difference(s).");
        }

        result.Message = inSync
            ? $"{dbName} is in sync with TIA."
            : $"{dbName} differs from TIA.";
        return result;
    }

    private async Task<TiaLiveSyncResult> PushDbFamilyAsync(CwcProject project, string dbName, GenerationSelection selection)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var originalOutputFolder = project.Paths.OutputFolder;
        var snapshotRoot = CreateSnapshotFolder(project, "push_" + SanitizeName(dbName));
        var pushRoot = Path.Combine(snapshotRoot, "push");
        var plcFolder = Path.Combine(pushRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var result = new TiaLiveSyncResult
        {
            SnapshotFolder = snapshotRoot
        };

        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            project.Paths.OutputFolder = plcFolder;

            var generation = _generator.GenerateSelected(project, selection);
            if (!generation.Success)
            {
                var issues = string.Join(Environment.NewLine, generation.Issues.Select(issue => $"{issue.Severity} {issue.Table}: {issue.Message}"));
                throw new InvalidOperationException($"Cannot push '{binding.PlcName} / {dbName}' because generation has validation errors:{Environment.NewLine}{issues}");
            }

            var sourceFileNames = GetSourceFileNamesForDb(database.DbCatalog, dbName);
            var sourceFiles = generation.CreatedFiles
                .Where(IsTiaSourceFile)
                .Where(path => sourceFileNames.Contains(Path.GetFileName(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sourceFiles.Count == 0)
            {
                result.Warnings.Add($"{binding.PlcName} / {dbName}: no generated TIA source files found.");
            }
            else
            {
                var transfer = await sessionService.ImportSourcesAsync([
                    new TiaPlcSourceImportRequest
                    {
                        PlcName = binding.PlcName,
                        DeviceName = binding.DeviceName,
                        SoftwareName = binding.SoftwareName,
                        SourceFiles = sourceFiles,
                        ResetDataBlocks = GetConfigurationDataBlockNamesForDb(database.DbCatalog, dbName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    }
                ]);
                result.FileCount = sourceFiles.Count;
                result.Messages.Add($"{binding.PlcName} / {dbName}: generated and pushed {sourceFiles.Count} source file(s).");
                result.Messages.AddRange(transfer.Messages);
                result.Warnings.AddRange(transfer.Warnings);
                binding.SyncState = $"{dbName} pushed";
                SetSectionState(binding, dbName, "InSync", $"{dbName} was pushed to TIA.");
                UpdatePushedFingerprints(binding, sourceFiles);
            }
        }
        finally
        {
            project.Paths.OutputFolder = originalOutputFolder;
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"{dbName} push completed: {result.FileCount} source file(s) sent to TIA.";
        return result;
    }

    private async Task<TiaLiveSyncResult> PullHwSectionCoreAsync(CwcProject project, string section)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var snapshotRoot = CreateSnapshotFolder(project, "pull_HW_" + section);
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var transfer = await sessionService.ExportSourcesAsync([CreateHwExportRequest(binding, plcFolder)], remoteRoot);
        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot, FileCount = transfer.Files.Count };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            var hwFile = FindSourceFile(plcFolder, "HW_ID_s.db")
                         ?? throw new FileNotFoundException("Exported HW_ID_s.db was not found.", Path.Combine(plcFolder, "HW_ID_s.db"));
            result.ImportedRows = _importer.ImportHwIdsSection(project, hwFile, section);
            database.CaptureFrom(project);
            binding.SyncState = $"HW {section} pulled";
            SetSectionState(binding, "HW " + section, "InSync", $"HW {section} was pulled from TIA.");
            UpdatePulledFingerprints(binding, plcFolder);
        }
        finally
        {
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"HW {section} pull completed: {result.ImportedRows} value(s), {result.FileCount} file(s).";
        return result;
    }

    private async Task<TiaLiveSyncResult> CheckHwSectionCoreAsync(CwcProject project, string section)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var snapshotRoot = CreateSnapshotFolder(project, "check_HW_" + section);
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var transfer = await sessionService.ExportSourcesAsync([CreateHwExportRequest(binding, plcFolder)], remoteRoot);
        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot, FileCount = transfer.Files.Count };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        var clone = CloneProject(project);
        var cloneDatabase = clone.PlcDatabases.FirstOrDefault(item => item.Id == database.Id)
                            ?? throw new InvalidOperationException("Could not clone active PLC database.");
        cloneDatabase.ApplyTo(clone);
        clone.ActivePlcDatabaseId = cloneDatabase.Id;
        var hwFile = FindSourceFile(plcFolder, "HW_ID_s.db")
                     ?? throw new FileNotFoundException("Exported HW_ID_s.db was not found.", Path.Combine(plcFolder, "HW_ID_s.db"));
        result.ImportedRows = _importer.ImportHwIdsSection(clone, hwFile, section);
        cloneDatabase.CaptureFrom(clone);

        var inSync = HwSectionFingerprint(database, section).Equals(HwSectionFingerprint(cloneDatabase, section), StringComparison.Ordinal);
        binding.SyncState = inSync ? $"HW {section} in sync" : $"HW {section} differs";
        SetSectionState(binding, "HW " + section, inSync ? "InSync" : "Differs", inSync ? $"HW {section} is in sync with TIA." : $"HW {section} differs from TIA.");
        result.Message = inSync ? $"HW {section} is in sync with TIA." : $"HW {section} differs from TIA.";
        result.Messages.Add($"{binding.PlcName} / HW {section}: compared HW_ID_s.db, {result.ImportedRows} imported comparison value(s).");
        return result;
    }

    private async Task<TiaLiveSyncResult> PushHwSectionCoreAsync(CwcProject project, string section)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var originalOutputFolder = project.Paths.OutputFolder;
        var snapshotRoot = CreateSnapshotFolder(project, "push_HW_" + section);
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var pushRoot = Path.Combine(snapshotRoot, "push");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);
        Directory.CreateDirectory(pushRoot);

        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot };
        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;

            var export = await sessionService.ExportSourcesAsync([CreateHwExportRequest(binding, plcFolder)], remoteRoot);
            result.Messages.AddRange(export.Messages);
            result.Warnings.AddRange(export.Warnings);
            var remoteHwFile = FindSourceFile(plcFolder, "HW_ID_s.db");

            project.Paths.OutputFolder = pushRoot;
            var generation = _generator.GenerateHwIdBlock(project, pushRoot);
            if (!generation.Success)
            {
                var issues = string.Join(Environment.NewLine, generation.Issues.Select(issue => $"{issue.Severity} {issue.Table}: {issue.Message}"));
                throw new InvalidOperationException($"Cannot push HW {section} because generation has validation errors:{Environment.NewLine}{issues}");
            }

            var localHwFile = generation.CreatedFiles.FirstOrDefault(file => Path.GetFileName(file).Equals("HW_ID_s.db", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(localHwFile))
            {
                throw new InvalidOperationException("Cannot push HW " + section + " because generated HW_ID_s.db was not found.");
            }

            var mergedHwFile = Path.Combine(pushRoot, "HW_ID_s_merged.db");
            MergeHwSection(remoteHwFile, localHwFile, mergedHwFile, section);

            var transfer = await sessionService.ImportSourcesAsync([
                new TiaPlcSourceImportRequest
                {
                    PlcName = binding.PlcName,
                    DeviceName = binding.DeviceName,
                    SoftwareName = binding.SoftwareName,
                    SourceFiles = [mergedHwFile],
                    ResetDataBlocks = ["HW_ID_s"]
                }
            ]);

            result.FileCount = 1;
            result.Messages.Add($"{binding.PlcName} / HW {section}: pushed HW_ID_s.db section.");
            result.Messages.AddRange(transfer.Messages);
            result.Warnings.AddRange(transfer.Warnings);
            binding.SyncState = $"HW {section} pushed";
            SetSectionState(binding, "HW " + section, "InSync", $"HW {section} was pushed to TIA.");
            UpdatePushedFingerprints(binding, [mergedHwFile]);
        }
        finally
        {
            project.Paths.OutputFolder = originalOutputFolder;
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"HW {section} push completed: HW_ID_s.db sent to TIA.";
        return result;
    }

    public async Task<TiaLiveSyncResult> PullActiveDatabaseAsync(CwcProject project)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var snapshotRoot = CreateSnapshotFolder(project, "pull_" + SanitizeName(database.Name));
        var remoteRoot = Path.Combine(snapshotRoot, "remote");
        var plcFolder = Path.Combine(remoteRoot, SanitizeName(binding.PlcName));
        Directory.CreateDirectory(plcFolder);

        var transfer = await sessionService.ExportSourcesAsync([BuildExportRequest(binding, database.DbCatalog, plcFolder)], remoteRoot);
        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot, FileCount = transfer.Files.Count };
        result.Messages.AddRange(transfer.Messages);
        result.Warnings.AddRange(transfer.Warnings);

        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            var files = _importer.Scan(plcFolder);
            var importResult = _importer.Import(project, files);
            database.CaptureFrom(project);
            binding.SyncState = "Pulled from TIA";
            SetAllSectionStates(binding, "InSync", "Pulled from TIA.");
            result.ImportedRows = importResult.ImportedCount;
            result.Messages.AddRange(importResult.Messages);
            UpdatePulledFingerprints(binding, plcFolder);
        }
        finally
        {
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"{database.Name} pull completed: {result.ImportedRows} row(s), {result.FileCount} file(s).";
        return result;
    }

    public async Task<TiaLiveSyncResult> PushActiveDatabaseAsync(CwcProject project)
    {
        var session = RequireSession();
        var (database, binding) = RequireActiveMappedBinding(project, session);
        var originalActiveId = project.ActivePlcDatabaseId;
        var originalOutputFolder = project.Paths.OutputFolder;
        var snapshotRoot = CreateSnapshotFolder(project, "push_" + SanitizeName(database.Name));
        var pushRoot = Path.Combine(snapshotRoot, "push");
        Directory.CreateDirectory(pushRoot);

        var result = new TiaLiveSyncResult { SnapshotFolder = snapshotRoot };
        try
        {
            database.ApplyTo(project);
            project.ActivePlcDatabaseId = database.Id;
            project.Paths.OutputFolder = pushRoot;
            var generation = _generator.GenerateAll(project);
            if (!generation.Success)
            {
                var issues = string.Join(Environment.NewLine, generation.Issues.Select(issue => $"{issue.Severity} {issue.Table}: {issue.Message}"));
                throw new InvalidOperationException($"Cannot push '{database.Name}' because generation has validation errors:{Environment.NewLine}{issues}");
            }

            var sourceFiles = generation.CreatedFiles.Where(IsTiaSourceFile).ToList();
            var transfer = await sessionService.ImportSourcesAsync([
                new TiaPlcSourceImportRequest
                {
                    PlcName = binding.PlcName,
                    DeviceName = binding.DeviceName,
                    SoftwareName = binding.SoftwareName,
                    SourceFiles = sourceFiles,
                    ResetDataBlocks = database.DbCatalog.DataBlockNames
                        .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.DataBlockName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Concat(["HW_ID_s"])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                }
            ]);

            result.FileCount = sourceFiles.Count;
            result.Messages.AddRange(transfer.Messages);
            result.Warnings.AddRange(transfer.Warnings);
            binding.SyncState = "Pushed to TIA";
            SetAllSectionStates(binding, "InSync", "Pushed to TIA.");
            UpdatePushedFingerprints(binding, sourceFiles);
        }
        finally
        {
            project.Paths.OutputFolder = originalOutputFolder;
            RestoreActiveDatabase(project, originalActiveId);
        }

        result.Message = $"{database.Name} push completed: {result.FileCount} source file(s) sent to TIA.";
        return result;
    }

    private (PlcDatabase Database, TiaPlcBinding Binding) RequireActiveMappedBinding(CwcProject project, TiaAttachedSession session)
    {
        project.TiaSync ??= new TiaSyncConfig();
        project.TiaSync.SyncWithDatabases(project.PlcDatabases, project.DbCatalog);
        var database = project.PlcDatabases.FirstOrDefault(item => item.Id == project.ActivePlcDatabaseId)
                       ?? project.PlcDatabases.FirstOrDefault()
                       ?? throw new InvalidOperationException("PLC database is missing.");
        var binding = project.TiaSync.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id)
                      ?? throw new InvalidOperationException($"TIA mapping for '{database.Name}' is missing.");
        var issues = _mappingService.Validate(project, session)
            .Where(issue => issue.Contains(database.Name, StringComparison.OrdinalIgnoreCase)
                            || issue.Contains(binding.PlcName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues));
        }

        return (database, binding);
    }

    private static TiaPlcSourceExportRequest CreateHwExportRequest(TiaPlcBinding binding, string plcFolder)
    {
        return new TiaPlcSourceExportRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            OutputSubfolder = plcFolder,
            Artifacts = [BlockArtifact("HW_ID_s", "HW_ID_s.db")]
        };
    }

    private static TiaPlcSourceExportRequest BuildExportRequest(TiaPlcBinding binding, DbCatalog catalog, string plcFolder)
    {
        return new TiaPlcSourceExportRequest
        {
            PlcName = binding.PlcName,
            DeviceName = binding.DeviceName,
            SoftwareName = binding.SoftwareName,
            OutputSubfolder = plcFolder,
            Artifacts = CreatePullArtifacts(catalog, binding.IncludeSequenceInGlobalSync)
        };
    }

    private static string NormalizeHwSection(string section)
    {
        var value = section.Trim().ToUpperInvariant();
        return value is "DI" or "DO" or "AI" or "RTD" or "COUNTER"
            ? value
            : throw new NotSupportedException($"HW section '{section}' is not supported.");
    }

    private static string NormalizeDbName(string dbName)
    {
        var value = NormalizeDbNameText(dbName);
        return value is "DB DI" or "DB DO" or "DB MEAS" or "DB AO" or "DB ALM" or "DB PAR" or "DB SEQUENCE"
            ? value
            : throw new NotSupportedException($"DB family '{dbName}' is not supported.");
    }

    private static GenerationSelection CreateDbSelection(string dbName)
    {
        return NormalizeDbName(dbName) switch
        {
            "DB DI" => new GenerationSelection { DbDiConfiguration = true, DbDiStrings = true, DbDiGroupNames = true },
            "DB DO" => new GenerationSelection { DbDoConfiguration = true, DbDoStrings = true, DbDoGroupNames = true },
            "DB MEAS" => new GenerationSelection { DbMeasConfiguration = true, DbMeasStrings = true, DbMeasGroupNames = true },
            "DB AO" => new GenerationSelection { DbAoConfiguration = true },
            "DB ALM" => new GenerationSelection { DbAlmConfiguration = true, DbAlmStrings = true, DbAlmGroupNames = true },
            "DB PAR" => new GenerationSelection { DbParConfiguration = true, DbParStrings = true, DbParGroupNames = true },
            "DB SEQUENCE" => new GenerationSelection(),
            _ => throw new NotSupportedException($"DB family '{dbName}' is not supported.")
        };
    }

    private static void MergeHwSection(string? remoteHwFile, string localHwFile, string outputFile, string section)
    {
        var localLines = File.ReadAllLines(localHwFile).ToList();
        if (string.IsNullOrWhiteSpace(remoteHwFile) || !File.Exists(remoteHwFile))
        {
            File.Copy(localHwFile, outputFile, overwrite: true);
            return;
        }

        var remoteLines = File.ReadAllLines(remoteHwFile).ToList();
        var localSectionLines = localLines.Where(line => IsHwSectionAssignment(line, section)).ToList();
        var outputLines = remoteLines.Where(line => !IsHwSectionAssignment(line, section)).ToList();
        var beginIndex = outputLines.FindIndex(line => line.Trim().Equals("BEGIN", StringComparison.OrdinalIgnoreCase));
        if (beginIndex < 0)
        {
            File.Copy(localHwFile, outputFile, overwrite: true);
            return;
        }

        outputLines.InsertRange(beginIndex + 1, localSectionLines);
        File.WriteAllLines(outputFile, outputLines);
    }

    private static bool IsHwSectionAssignment(string line, string section)
    {
        var trimmed = line.TrimStart();
        if (section.Equals("COUNTER", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.StartsWith("\"COUNTER\".", StringComparison.OrdinalIgnoreCase)
                   || trimmed.StartsWith("COUNTER.", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static List<TiaPlcSourceExportRequest> BuildExportRequests(CwcProject project, string remoteRoot)
    {
        return project.TiaSync.PlcBindings
            .OrderBy(item => item.PlcName, StringComparer.OrdinalIgnoreCase)
            .Select(binding =>
            {
                var database = project.PlcDatabases.FirstOrDefault(item => item.Id == binding.PlcDatabaseId);
                return BuildExportRequest(binding, database?.DbCatalog ?? project.DbCatalog, Path.Combine(remoteRoot, SanitizeName(binding.PlcName)));
            })
            .ToList();
    }

    private static List<TiaSourceArtifactRequest> CreatePullArtifacts(DbCatalog catalog)
    {
        return CreatePullArtifacts(catalog, includeSequence: true);
    }

    private static List<TiaSourceArtifactRequest> CreatePullArtifacts(DbCatalog catalog, bool includeSequence)
    {
        var artifacts = new List<TiaSourceArtifactRequest>
        {
            BlockArtifact("HW_ID_s", "HW_ID_s.db"),
            BlockArtifact("ANA_CTRL", "ANA_CTRL.db")
        };
        artifacts.AddRange(CreateAoPullArtifacts(catalog));

        var rows = GetDataBlockNameRowsWithDefaults(catalog);

        foreach (var row in rows.Where(item => !string.IsNullOrWhiteSpace(item.DataBlockName)))
        {
            if (!includeSequence && IsSequenceDbName(row.DbName))
            {
                continue;
            }

            var targetName = row.DataBlockName.Trim();
            var fileName = row.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase)
                ? targetName + ".db"
                : targetName + ".db";
            artifacts.Add(BlockArtifact(targetName, fileName));
            if (row.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            {
                artifacts.AddRange(CreateDbFamilyTypeArtifacts(catalog, NormalizeDbNameText(row.DbName)));
            }
        }

        return artifacts
            .GroupBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TiaSourceArtifactRequest> CreatePullArtifactsForDb(DbCatalog catalog, string dbName)
    {
        if (NormalizeDbName(dbName).Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAoPullArtifacts(catalog);
        }

        return GetDataBlockNameRowsWithDefaults(catalog)
            .Where(item => NormalizeDbNameText(item.DbName).Equals(dbName, StringComparison.OrdinalIgnoreCase)
                           || (IsSequenceDbName(dbName) && IsSequenceDataBlockRow(item)))
            .Where(item => !string.IsNullOrWhiteSpace(item.DataBlockName))
            .Select(item => BlockArtifact(item.DataBlockName.Trim(), item.DataBlockName.Trim() + ".db"))
            .Concat(CreateDbFamilyTypeArtifacts(catalog, dbName))
            .GroupBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> GetSourceFileNamesForDb(DbCatalog catalog, string dbName)
    {
        if (NormalizeDbName(dbName).Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            return ["DB_AO.udt"];
        }

        return GetDataBlockNameRowsWithDefaults(catalog)
            .Where(item => NormalizeDbNameText(item.DbName).Equals(dbName, StringComparison.OrdinalIgnoreCase)
                           || (IsSequenceDbName(dbName) && IsSequenceDataBlockRow(item)))
            .Select(item => item.FileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetConfigurationDataBlockNamesForDb(DbCatalog catalog, string dbName)
    {
        if (NormalizeDbName(dbName).Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            return ["ANA_CTRL"];
        }

        return GetDataBlockNameRowsWithDefaults(catalog)
            .Where(item => NormalizeDbNameText(item.DbName).Equals(dbName, StringComparison.OrdinalIgnoreCase)
                           || (IsSequenceDbName(dbName) && IsSequenceDataBlockRow(item)))
            .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.DataBlockName)
            .Where(name => !string.IsNullOrWhiteSpace(name));
    }

    private static CwcProject CloneProject(CwcProject project)
    {
        var json = JsonSerializer.Serialize(project);
        return JsonSerializer.Deserialize<CwcProject>(json)
               ?? throw new InvalidOperationException("Could not clone project for TIA comparison.");
    }

    private static string DbFamilyFingerprint(DbCatalog catalog, string dbName)
    {
        object payload = dbName.ToUpperInvariant() switch
        {
            "DB DI" => new
            {
                Rows = catalog.DiRows
                    .OrderBy(row => row.Id)
                    .Select(row => new
                    {
                        row.Id,
                        row.PlcStructure,
                        row.PlcName,
                        row.SystemNameEnglish,
                        row.NameEnglish,
                        row.SystemNameOther,
                        row.NameOther,
                        row.TrueText,
                        row.ModuleName,
                        row.ModulePort,
                        row.ColorValue0,
                        row.ColorValue1,
                        row.AlarmConfig,
                        row.AlarmInvert,
                        row.AlarmId
                    }),
                Groups = catalog.DiSystemNameRows
                    .OrderBy(row => row.SystemNumber)
                    .Select(row => new
                    {
                        row.SystemNumber,
                        row.SystemPlcShortName,
                        row.PlcVariableName,
                        row.SystemPlcLongName,
                        row.HmiSystemName,
                        row.HmiSystemNameOther
                    })
            },
            "DB AO" => new
            {
                Rows = catalog.AoRows
                    .OrderBy(row => row.AoNumber)
                    .Select(row => new
                    {
                        row.AoNumber,
                        row.AoSystemName,
                        row.AoPlcName,
                        row.NameEnglish,
                        row.NameOther,
                        row.Unit,
                        row.TrueText,
                        row.ModuleName,
                        row.ModulePort,
                        row.ParValLowId,
                        row.ParValHighId,
                        row.ParOffsetId,
                        row.ParScaleFactorId
                    }),
                Groups = catalog.AoSystemNameRows
                    .OrderBy(row => row.SystemNumber)
                    .Select(row => new
                    {
                        row.SystemNumber,
                        row.SystemPlcShortName,
                        row.PlcVariableName,
                        row.SystemPlcLongName,
                        row.HmiSystemName,
                        row.HmiSystemNameOther
                    })
            },
            "DB SEQUENCE" => new
            {
                ControlStatusRows = catalog.Sequence.ControlStatusRows
                    .OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.PlcStructure, row.PlcName, row.NameEnglish, row.NameOther }),
                CommandRows = catalog.Sequence.CommandRows
                    .OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.PlcStructure, row.PlcName, row.NameEnglish, row.NameOther }),
                Sequences = catalog.Sequence.Sequences
                    .OrderBy(sequence => SequenceDefinition.SortOrder(sequence.Key))
                    .Select(sequence => new
                    {
                        sequence.Key,
                        sequence.LastStepNumber,
                        Steps = sequence.Steps.OrderBy(step => step.StepNumber).Select(step => new
                        {
                            step.StepNumber,
                            step.Name,
                            step.NameOther,
                            step.ConditionNumber,
                            step.CommandNumber,
                            step.DurationCheck,
                            step.EnterDelayTime,
                            step.Duration,
                            step.ExitDelayTime,
                            Conditions = step.Conditions.OrderBy(slot => slot.Index).Select(slot => new { slot.Index, slot.ControlStatusId, slot.Invert }),
                            step.FirstCondition1,
                            step.FirstCondition2,
                            step.FirstCondition3,
                            step.FirstCondition4,
                            step.SecondCondition1,
                            step.SecondCondition2,
                            step.FinalCondition,
                            Commands = step.Commands.OrderBy(slot => slot.Index).Select(slot => new { slot.Index, slot.CommandId, slot.On })
                        })
                    })
            },
            _ => throw new NotSupportedException($"{dbName} comparison is not implemented yet.")
        };

        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static string HwSectionFingerprint(PlcDatabase database, string section)
    {
        object payload = section.ToUpperInvariant() switch
        {
            "DI" => database.DigitalInputs
                .OrderBy(row => row.ModuleNumber)
                .ThenBy(row => row.Port)
                .Select(row => new { row.ModuleNumber, row.Port, row.Id, row.Config }),
            "DO" => database.DigitalOutputs
                .OrderBy(row => row.ModuleNumber)
                .ThenBy(row => row.Port)
                .Select(row => new { row.ModuleNumber, row.Port, row.Id, row.Config }),
            "AI" => database.AnalogInputs
                .OrderBy(row => row.ModuleNumber)
                .ThenBy(row => row.Port)
                .Select(row => new { row.ModuleNumber, row.Port, row.Id, row.Config, row.CustomLimits, row.LowLimit, row.HighLimit, row.FilterTime }),
            "RTD" => database.RtdInputs
                .OrderBy(row => row.ModuleNumber)
                .ThenBy(row => row.Port)
                .Select(row => new { row.ModuleNumber, row.Port, row.Id, row.Config, row.CustomLimits, row.LowLimit, row.HighLimit, row.FilterTime }),
            "COUNTER" => database.Counters
                .OrderBy(row => row.ModuleNumber)
                .Select(row => new { row.ModuleNumber, row.Id, row.Config, row.FilterTime }),
            _ => throw new NotSupportedException($"HW section '{section}' comparison is not implemented.")
        };

        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static void SetSectionState(TiaPlcBinding binding, string section, string state, string message)
    {
        var item = binding.SectionStates.FirstOrDefault(row => row.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new TiaSectionSyncState { Section = section };
            binding.SectionStates.Add(item);
        }

        item.State = state;
        item.Message = message;
        item.LastCheckedAtUtc = UtcStamp();
    }

    private static void SetAllSectionStates(TiaPlcBinding binding, string state, string message)
    {
        foreach (var section in KnownSections)
        {
            SetSectionState(binding, section, state, $"{section}: {message}");
        }
    }

    private static bool BindingHasDifferences(TiaPlcBinding binding)
    {
        return binding.SectionStates.Any(row => row.State.Equals("Differs", StringComparison.OrdinalIgnoreCase));
    }

    private SourceComparisonResult CompareSourceFiles(IReadOnlyList<string> localFiles, string remoteRoot, DbCatalog catalog, string dbName)
    {
        var differences = new List<string>();
        var compared = 0;
        var comparedEffectiveDb = false;
        foreach (var localFile in localFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(localFile);
            if (!comparedEffectiveDb && IsDbConfigurationUdtFile(fileName, catalog, dbName))
            {
                compared++;
                comparedEffectiveDb = true;
                if (!EffectiveDbConfigurationHash(catalog, dbName, Path.GetDirectoryName(localFile) ?? "", includeDbOverrides: false)
                        .Equals(EffectiveDbConfigurationHash(catalog, dbName, remoteRoot, includeDbOverrides: true), StringComparison.Ordinal))
                {
                    differences.Add($"{fileName}: effective UDT variables / DB values differ");
                }

                continue;
            }

            var remoteFile = FindSourceFile(remoteRoot, fileName);
            if (remoteFile is null)
            {
                differences.Add($"{fileName}: missing in TIA export");
                continue;
            }

            compared++;
            if (!NormalizedFileHash(localFile).Equals(NormalizedFileHash(remoteFile), StringComparison.Ordinal))
            {
                differences.Add($"{fileName}: content differs");
            }
        }

        return new SourceComparisonResult(compared, differences);
    }

    private string EffectiveDbConfigurationHash(DbCatalog sourceCatalog, string dbName, string folder, bool includeDbOverrides)
    {
        var clone = CloneCatalog(sourceCatalog);
        if (NormalizeDbName(dbName).Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            if (includeDbOverrides)
            {
                _importer.ImportAoFromFolder(clone, folder);
            }

            return StableHash(JsonSerializer.Serialize(clone.AoRows.OrderBy(row => row.AoNumber)));
        }

        _importer.ImportDbDefaultValuesFromFolder(clone, dbName, folder);
        if (includeDbOverrides)
        {
            var files = _importer.Scan(folder);
            _importer.Import(new CwcProject { DbCatalog = clone }, files);
        }

        return StableHash(NormalizeDbName(dbName) switch
        {
            "DB DI" => JsonSerializer.Serialize(clone.DiRows.OrderBy(row => row.Id)),
            "DB DO" => JsonSerializer.Serialize(clone.DoRows.OrderBy(row => row.Id)),
            "DB MEAS" => JsonSerializer.Serialize(clone.MeasRows.OrderBy(row => row.Id)),
            "DB ALM" => JsonSerializer.Serialize(clone.AlarmRows.OrderBy(row => row.Id)),
            "DB PAR" => JsonSerializer.Serialize(clone.ParameterRows.OrderBy(row => row.Id)),
            _ => ""
        });
    }

    private static DbCatalog CloneCatalog(DbCatalog catalog)
    {
        return JsonSerializer.Deserialize<DbCatalog>(JsonSerializer.Serialize(catalog)) ?? new DbCatalog();
    }

    private static string StableHash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static bool IsDbConfigurationUdtFile(string fileName, DbCatalog catalog, string dbName)
    {
        if (!Path.GetExtension(fileName).Equals(".udt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (NormalizeDbName(dbName).Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Equals("DB_AO.udt", StringComparison.OrdinalIgnoreCase);
        }

        return GetDataBlockNameRowsWithDefaults(catalog)
            .Where(item => NormalizeDbNameText(item.DbName).Equals(dbName, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            .Any(item => fileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizedFileHash(string path)
    {
        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private static TiaSourceArtifactRequest BlockArtifact(string targetName, string fileName)
    {
        return new TiaSourceArtifactRequest
        {
            TargetName = targetName,
            FileName = fileName,
            Kind = TiaArtifactKind.PlcBlock.ToString()
        };
    }

    private static int ImportSequenceFromFolder(DbCatalog catalog, string folder, string dbName)
    {
        var sourceFile = GetDataBlockNameRowsWithDefaults(catalog)
            .Where(IsSequenceDataBlockRow)
            .Where(item => item.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            .Where(item => IsSequenceParRow(item))
            .Select(item => FindSourceFile(folder, item.DataBlockName.Trim() + ".db")
                            ?? FindSourceFile(folder, item.FileName))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        sourceFile ??= Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.db", SearchOption.AllDirectories)
                .FirstOrDefault(path => IsSequenceParFileName(Path.GetFileNameWithoutExtension(path)))
            : null;
        var existingControlRows = catalog.Sequence.ControlStatusRows;
        var existingCommandRows = catalog.Sequence.CommandRows;
        var imported = sourceFile is null
            ? catalog.Sequence
            : new SequenceParDbParser().Parse(sourceFile);
        imported.ControlStatusRows = existingControlRows;
        imported.CommandRows = existingCommandRows;
        imported.EnsureDefaults();
        var referenceRows = 0;
        referenceRows += ImportSequenceReferenceRows(catalog, imported.ControlStatusRows, folder, number: 6, fallbackTokens: ["CTRL_STATUS", "CTRL_STS"]);
        referenceRows += ImportSequenceReferenceRows(catalog, imported.CommandRows, folder, number: 7, fallbackTokens: ["SEQ_CMDS", "SEQ_CMD"]);
        if (sourceFile is null && referenceRows == 0)
        {
            throw new FileNotFoundException($"{dbName} source files were not found in TIA export folder. Expected SEQ_PAR plus DB 6 CTRL_STATUS and/or DB 7 SEQ_CMDs.", folder);
        }

        catalog.Sequence = imported;
        return imported.Sequences.Sum(sequence => sequence.VisibleSteps.Count()) + referenceRows;
    }

    private static int ImportSequenceReferenceRows(DbCatalog catalog, IList<DbStringRow> rows, string folder, int number, string[] fallbackTokens)
    {
        var sourceFile = FindSequenceReferenceSourceFile(catalog, folder, number, fallbackTokens);
        if (sourceFile is null)
        {
            return 0;
        }

        return new SequenceParDbParser().ImportReferenceStructureNames(rows, sourceFile);
    }

    private static string? FindSequenceReferenceSourceFile(DbCatalog catalog, string folder, int number, string[] fallbackTokens)
    {
        var configured = GetDataBlockNameRowsWithDefaults(catalog)
            .Where(IsSequenceDataBlockRow)
            .Where(item => item.Number == number
                           || fallbackTokens.Any(token => item.DataBlockName.Contains(token, StringComparison.OrdinalIgnoreCase)
                                                          || item.FileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(item => FindSourceFile(folder, item.DataBlockName.Trim() + ".db")
                            ?? FindSourceFile(folder, item.FileName))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.db", SearchOption.AllDirectories)
                .FirstOrDefault(path => fallbackTokens.Any(token => Path.GetFileNameWithoutExtension(path).Contains(token, StringComparison.OrdinalIgnoreCase)))
            : null;
    }

    private static bool IsSequenceParRow(DbDataBlockNameSetting item)
    {
        return item.Number == 28
               || item.DataBlockName.Contains("SEQ_PAR", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("SEQ_PAR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSequenceDataBlockRow(DbDataBlockNameSetting item)
    {
        return IsSequenceDbName(item.DbName)
               || item.Number is 6 or 7 or 28
               || item.DataBlockName.Contains("CTRL_STATUS", StringComparison.OrdinalIgnoreCase)
               || item.DataBlockName.Contains("CTRL_STS", StringComparison.OrdinalIgnoreCase)
               || item.DataBlockName.Contains("SEQ_CMDS", StringComparison.OrdinalIgnoreCase)
               || item.DataBlockName.Contains("SEQ_CMD", StringComparison.OrdinalIgnoreCase)
               || item.DataBlockName.Contains("SEQ_PAR", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("CTRL_STATUS", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("CTRL_STS", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("SEQ_CMDS", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("SEQ_CMD", StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains("SEQ_PAR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSequenceParFileName(string fileNameWithoutExtension)
    {
        return fileNameWithoutExtension.Equals("SEQ_PAR", StringComparison.OrdinalIgnoreCase)
               || fileNameWithoutExtension.Contains("SEQ_PAR", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DbDataBlockNameSetting> GetDataBlockNameRowsWithDefaults(DbCatalog catalog)
    {
        var rows = catalog.DataBlockNames is { Count: > 0 }
            ? catalog.DataBlockNames.Select(row => row.Clone()).ToList()
            : [];
        foreach (var row in rows.Where(IsSequenceDataBlockRow))
        {
            NormalizeSequenceDataBlockName(row);
        }

        foreach (var defaultRow in DbDataBlockNameDefaults.Create())
        {
            if (rows.Any(row => row.Number == defaultRow.Number
                                && NormalizeDbNameText(row.DbName).Equals(NormalizeDbNameText(defaultRow.DbName), StringComparison.OrdinalIgnoreCase)
                                && row.BlockKind.Equals(defaultRow.BlockKind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            rows.Add(defaultRow);
        }

        return rows;
    }

    private static void NormalizeSequenceDataBlockName(DbDataBlockNameSetting row)
    {
        if (row.Number == 6 && IsOldOrEmptySequenceName(row, "CTRL_STS"))
        {
            row.DataBlockName = "CTRL_STATUS";
            row.FileName = "CTRL_STATUS.db";
        }
        else if (row.Number == 7 && IsOldOrEmptySequenceName(row, "SEQ_CMD"))
        {
            row.DataBlockName = "SEQ_CMDs";
            row.FileName = "SEQ_CMDs.db";
        }
    }

    private static bool IsOldOrEmptySequenceName(DbDataBlockNameSetting row, string oldName)
    {
        return string.IsNullOrWhiteSpace(row.DataBlockName)
               || row.DataBlockName.Equals(oldName, StringComparison.OrdinalIgnoreCase)
               || row.FileName.Equals(oldName + ".db", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSequenceDataBlocks(DbCatalog catalog)
    {
        return catalog.DataBlockNames.Any(item => IsSequenceDbName(item.DbName) && !string.IsNullOrWhiteSpace(item.DataBlockName));
    }

    private static bool IsSequenceDbName(string dbName)
    {
        var value = NormalizeDbNameText(dbName);
        return value is "DB SEQUENCE" or "DB SEQ" or "DB SEQ PAR";
    }

    private static string NormalizeDbNameText(string dbName)
    {
        var value = string.Join(" ", (dbName ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return value is "SEQUENCE" or "DB SEQ" or "DB SEQ PAR" or "DB SEQUENCE PAR" ? "DB SEQUENCE" : value;
    }

    private static List<TiaSourceArtifactRequest> CreateAoPullArtifacts(DbCatalog catalog)
    {
        var artifacts = new List<TiaSourceArtifactRequest>
        {
            BlockArtifact("ANA_CTRL", "ANA_CTRL.db"),
            TypeArtifact("DT_ANA_CTRL_ALL", "DB_AO__DT_ANA_CTRL_ALL.udt")
        };

        foreach (var typeName in GetAoPullTypeNames(catalog))
        {
            artifacts.Add(TypeArtifact(typeName, $"DB_AO__{SanitizeName(typeName)}.udt"));
        }

        return artifacts
            .GroupBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<TiaSourceArtifactRequest> CreateDbFamilyTypeArtifacts(DbCatalog catalog, string dbName)
    {
        var normalized = NormalizeDbName(dbName);
        if (normalized.Equals("DB AO", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAoPullArtifacts(catalog).Where(item => item.Kind.Equals(TiaArtifactKind.PlcType.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        var artifacts = new List<TiaSourceArtifactRequest>();
        var configRows = GetDataBlockNameRowsWithDefaults(catalog)
            .Where(row => NormalizeDbNameText(row.DbName).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                          || (IsSequenceDbName(normalized) && IsSequenceDataBlockRow(row)))
            .Where(row => row.BlockKind.Equals(DbDataBlockNameDefaults.ConfigurationKind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var row in configRows.Where(row => !string.IsNullOrWhiteSpace(row.DataTypeName)))
        {
            artifacts.Add(TypeArtifact(row.DataTypeName.Trim(), FirstNonEmpty(row.FileName, row.DataTypeName.Trim() + ".udt")));
        }

        foreach (var typeName in GetDbFamilyGroupTypeNames(catalog, normalized))
        {
            artifacts.Add(TypeArtifact(typeName, $"{SanitizeName(typeName)}.udt"));
        }

        return artifacts
            .GroupBy(item => item.TargetName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<string> GetDbFamilyGroupTypeNames(DbCatalog catalog, string dbName)
    {
        var normalized = NormalizeDbName(dbName);
        return normalized switch
        {
            "DB DI" => GetSystemTypeNames(catalog.DiSystemNameRows, catalog.DiRows, catalog.DiStructTypePrefix, "DI"),
            "DB DO" => GetSystemTypeNames(catalog.DoSystemNameRows, catalog.DoRows, catalog.DoStructTypePrefix, "DO"),
            "DB MEAS" => GetSystemTypeNames(catalog.MeasSystemNameRows, catalog.MeasRows, catalog.MeasStructTypePrefix, "MEAS"),
            "DB ALM" => GetSystemTypeNames(catalog.AlarmSystemNameRows, catalog.AlarmRows, catalog.AlarmStructTypePrefix, "ALM"),
            "DB PAR" => GetSystemTypeNames(catalog.ParameterSystemNameRows, catalog.ParameterRows, catalog.ParameterStructTypePrefix, "PAR"),
            _ => []
        };
    }

    private static IEnumerable<string> GetSystemTypeNames(IEnumerable<DbDiSystemNameRow> systemRows, IReadOnlyList<DbStringRow> rows, string typePrefix, string fallbackPrefix)
    {
        var systems = systemRows.OrderBy(row => row.SystemNumber).ToList();
        if (systems.Count > 0)
        {
            foreach (var row in systems)
            {
                var typeName = FirstNonEmpty(
                    row.SystemPlcLongName,
                    string.IsNullOrWhiteSpace(row.SystemPlcShortName) ? "" : JoinPrefixAndName(typePrefix, row.SystemPlcShortName),
                    string.IsNullOrWhiteSpace(row.PlcVariableName) ? "" : JoinPrefixAndName(typePrefix, row.PlcVariableName));
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    yield return typeName.Trim();
                }
            }

            yield break;
        }

        var chunkSize = normalizedChunkSize(fallbackPrefix);
        var totalGroups = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)chunkSize));
        for (var index = 0; index < totalGroups; index++)
        {
            var first = rows.Skip(index * chunkSize).FirstOrDefault();
            var memberName = FirstNonEmpty(first?.PlcStructure ?? "", $"{fallbackPrefix}_{index + 1}");
            yield return JoinPrefixAndName(typePrefix, memberName);
        }

        static int normalizedChunkSize(string prefix)
        {
            return prefix.Equals("MEAS", StringComparison.OrdinalIgnoreCase) ? 24 : 32;
        }
    }

    private static string JoinPrefixAndName(string prefix, string name)
    {
        var cleanPrefix = (prefix ?? "").Trim().TrimEnd('_');
        var cleanName = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanPrefix))
        {
            return cleanName;
        }

        return string.IsNullOrWhiteSpace(cleanName) ? cleanPrefix : $"{cleanPrefix}_{cleanName}";
    }

    private static IEnumerable<string> GetAoPullTypeNames(DbCatalog catalog)
    {
        var prefix = string.IsNullOrWhiteSpace(catalog.AoStructTypePrefix)
            ? "DT_ANA_CTRL_"
            : catalog.AoStructTypePrefix.Trim();
        var fallback = prefix.TrimEnd('_') + "_MECH";
        yield return fallback;

        foreach (var row in catalog.AoSystemNameRows)
        {
            var typeName = FirstNonEmpty(
                row.SystemPlcLongName,
                string.IsNullOrWhiteSpace(row.SystemPlcShortName) ? "" : prefix.TrimEnd('_') + "_" + row.SystemPlcShortName.Trim(),
                string.IsNullOrWhiteSpace(row.PlcVariableName) ? "" : prefix.TrimEnd('_') + "_" + row.PlcVariableName.Trim());
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                yield return typeName.Trim();
            }
        }
    }

    private static TiaSourceArtifactRequest TypeArtifact(string targetName, string fileName)
    {
        return new TiaSourceArtifactRequest
        {
            TargetName = targetName,
            FileName = fileName,
            Kind = TiaArtifactKind.PlcType.ToString()
        };
    }

    private static string CreateSnapshotFolder(CwcProject project, string direction)
    {
        var baseFolder = GetProjectTiaSyncRoot(project);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(baseFolder, $"{timestamp}_{direction}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string GetProjectTiaSyncRoot(CwcProject project)
    {
        var projectFolder = !string.IsNullOrWhiteSpace(project.ProjectFilePath)
            ? Path.GetDirectoryName(project.ProjectFilePath)
            : null;

        var baseFolder = !string.IsNullOrWhiteSpace(projectFolder)
            ? projectFolder
            : Path.Combine(Path.GetTempPath(), "PlantConfigurator");

        var folder = Path.Combine(baseFolder, "TIA_SYNC");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void RestoreActiveDatabase(CwcProject project, string originalActiveId)
    {
        var database = project.PlcDatabases.FirstOrDefault(item => item.Id == originalActiveId)
                       ?? project.PlcDatabases.FirstOrDefault();
        if (database is null)
        {
            return;
        }

        database.ApplyTo(project);
        project.ActivePlcDatabaseId = database.Id;
    }

    private static bool IsTiaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".udt", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateRemoteFingerprints(TiaPlcBinding binding, string plcFolder)
    {
        foreach (var artifact in binding.Artifacts)
        {
            var file = FindSourceFile(plcFolder, artifact.TargetName + ".db")
                       ?? FindSourceFile(plcFolder, artifact.SourceFileName);
            if (file is null)
            {
                continue;
            }

            artifact.LastRemoteFingerprint = FileHash(file);
            artifact.LastComparedAtUtc = UtcStamp();
        }
    }

    private static void UpdateLocalFingerprints(TiaPlcBinding binding, IReadOnlyList<string> sourceFiles)
    {
        foreach (var artifact in binding.Artifacts)
        {
            var file = sourceFiles.FirstOrDefault(path =>
                Path.GetFileName(path).Equals(artifact.SourceFileName, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                continue;
            }

            artifact.LastLocalFingerprint = FileHash(file);
            artifact.LastComparedAtUtc = UtcStamp();
        }
    }

    private static void UpdatePulledFingerprints(TiaPlcBinding binding, string plcFolder)
    {
        UpdateRemoteFingerprints(binding, plcFolder);
        foreach (var artifact in binding.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.LastRemoteFingerprint))
            {
                continue;
            }

            artifact.LastLocalFingerprint = artifact.LastRemoteFingerprint;
            artifact.LastComparedAtUtc = UtcStamp();
        }
    }

    private static void UpdatePushedFingerprints(TiaPlcBinding binding, IReadOnlyList<string> sourceFiles)
    {
        UpdateLocalFingerprints(binding, sourceFiles);
        foreach (var artifact in binding.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.LastLocalFingerprint))
            {
                continue;
            }

            artifact.LastRemoteFingerprint = artifact.LastLocalFingerprint;
            artifact.LastComparedAtUtc = UtcStamp();
        }
    }

    private static string? FindSourceFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string FileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string UtcStamp()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    private static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "PLC" : sanitized;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private sealed record SourceComparisonResult(int ComparedCount, List<string> Differences);
}
