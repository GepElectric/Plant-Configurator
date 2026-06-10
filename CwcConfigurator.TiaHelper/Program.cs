using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Types;

namespace CwcConfigurator.TiaHelper
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static TiaPortal _portal;
        private static Project _project;
        private static readonly object CrossReferenceCacheLock = new object();
        private static readonly Dictionary<string, CrossReferenceIndex> CrossReferenceCache = new Dictionary<string, CrossReferenceIndex>(StringComparer.OrdinalIgnoreCase);
        private static readonly object XrefProfileLock = new object();
        private static string _xrefProfileLogPath;

        private static int Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveTiaAssembly;

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                HelperRequest request = null;
                try
                {
                    request = Serializer.Deserialize<HelperRequest>(line);
                    var payload = HandleRequest(request);
                    WriteResponse(new HelperResponse
                    {
                        Id = request.Id,
                        Success = true,
                        Payload = payload
                    });
                }
                catch (Exception ex)
                {
                    WriteResponse(new HelperResponse
                    {
                        Id = request == null ? 0 : request.Id,
                        Success = false,
                        Error = FormatException(ex)
                    });
                }
            }

            Disconnect();
            return 0;
        }

        private static object HandleRequest(HelperRequest request)
        {
            switch (request.Command)
            {
                case "GetProcesses":
                    return new TiaProcessListResponse { Processes = GetProcesses() };
                case "Attach":
                    return Attach(GetInt(request.Payload, "ProcessId"));
                case "ExportSources":
                    return ExportSources(request.Payload);
                case "ImportSources":
                    return ImportSources(request.Payload);
                case "GetCrossReferences":
                    return GetCrossReferences(request.Payload);
                case "GetCrossReferencesBatch":
                    return GetCrossReferencesBatch(request.Payload);
                case "OpenCrossReference":
                    return OpenCrossReference(request.Payload);
                case "ExecutePlcControl":
                    return ExecutePlcControl(request.Payload);
                case "ImportProjectTexts":
                    return ImportProjectTexts(request.Payload);
                case "Disconnect":
                    Disconnect();
                    return new { };
                default:
                    throw new InvalidOperationException("Unknown TIA helper command: " + request.Command);
            }
        }

        private static List<TiaPortalProcessInfo> GetProcesses()
        {
            return TiaPortal.GetProcesses()
                .Select(CreateProcessInfo)
                .OrderBy(info => info.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.ProcessId)
                .ToList();
        }

        private static TiaAttachedSession Attach(int processId)
        {
            Disconnect();

            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0)
            {
                var visiblePortalProcesses = Process.GetProcessesByName("Siemens.Automation.Portal").Length;
                if (visiblePortalProcesses > 0)
                {
                    throw new InvalidOperationException(
                        "TIA Portal is running, but the Openness API cannot see an attachable instance." + Environment.NewLine + Environment.NewLine +
                        "Helper target: " + GetLoadedVersionLabel() + Environment.NewLine +
                        "Visible Portal processes: " + visiblePortalProcesses + Environment.NewLine + Environment.NewLine +
                        "Check that the app and TIA Portal use the same Windows privileges, matching TIA version, and allowed Openness access.");
                }

                throw new InvalidOperationException("No running TIA Portal instance found. Open the project in TIA Portal first.");
            }

            var process = processes.FirstOrDefault(candidate => candidate.Id == processId);
            if (process == null)
            {
                throw new InvalidOperationException("The selected TIA Portal instance is no longer running.");
            }

            _portal = process.Attach();
            _project = _portal.Projects.FirstOrDefault();
            if (_project == null)
            {
                Disconnect();
                throw new InvalidOperationException("No project is open in the attached TIA Portal instance.");
            }

            var session = new TiaAttachedSession
            {
                ProcessId = process.Id,
                ProjectName = _project.Name,
                ProjectPath = SafeProjectPath(_project),
                PortalPath = SafeFilePath(process.Path),
                PortalVersion = GetLoadedVersionLabel()
            };

            foreach (var device in _project.Devices)
            {
                foreach (var item in device.DeviceItems)
                {
                    DiscoverSoftware(device.Name, item, session.Plcs);
                }
            }

            if (session.Plcs.Count == 0)
            {
                Disconnect();
                throw new InvalidOperationException("No PLC software targets were found in the attached TIA project.");
            }

            return session;
        }

        private static void Disconnect()
        {
            ClearCrossReferenceCache();
            _project = null;
            if (_portal == null)
            {
                return;
            }

            _portal.Dispose();
            _portal = null;
        }

        private static TiaProjectTextsImportResult ImportProjectTexts(IDictionary<string, object> payload)
        {
            RequireAttachedProject();
            var workbookPath = GetString(payload, "WorkbookPath");
            if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            {
                throw new FileNotFoundException("Project texts workbook was not found.", workbookPath);
            }

            var method = _project.GetType().GetMethod("ImportProjectTexts", new[] { typeof(FileInfo), typeof(bool) });
            if (method == null)
            {
                throw new MissingMethodException("ImportProjectTexts is not available in the current TIA Openness binding.");
            }

            method.Invoke(_project, new object[] { new FileInfo(Path.GetFullPath(workbookPath)), true });
            return new TiaProjectTextsImportResult
            {
                Message = "Project texts imported to TIA Portal."
            };
        }

        private static TiaSourceTransferResult ExportSources(IDictionary<string, object> payload)
        {
            RequireAttachedProject();
            var result = new TiaSourceTransferResult();
            foreach (var plcPayload in GetDictionaryList(payload, "Plcs"))
            {
                var binding = TiaPlcBindingDto.FromPayload(plcPayload);
                var outputFolder = FirstNonEmpty(GetString(plcPayload, "OutputSubfolder"), GetString(payload, "OutputFolder"));
                Directory.CreateDirectory(outputFolder);

                var plcSoftware = FindPlcSoftware(binding);
                if (plcSoftware == null)
                {
                    result.Warnings.Add("Could not resolve PLC software for '" + binding.PlcName + "'.");
                    continue;
                }

                foreach (var artifactPayload in GetDictionaryList(plcPayload, "Artifacts"))
                {
                    var artifact = TiaSourceArtifactDto.FromPayload(artifactPayload);
                    try
                    {
                        result.Messages.Add(binding.PlcName + ": exporting " + artifact.TargetName + "...");
                        WriteTransferTrace(outputFolder, binding.PlcName + ": exporting " + artifact.TargetName);
                        var filePath = ExportSource(plcSoftware, artifact, outputFolder);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            result.Files.Add(filePath);
                            result.Messages.Add(binding.PlcName + ": exported " + artifact.TargetName + " -> " + Path.GetFileName(filePath));
                            WriteTransferTrace(outputFolder, binding.PlcName + ": exported " + artifact.TargetName);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(binding.PlcName + " / " + artifact.TargetName + ": " + FormatException(ex));
                        WriteTransferTrace(outputFolder, binding.PlcName + ": export failed " + artifact.TargetName + " - " + FormatException(ex));
                    }
                }
            }

            return result;
        }

        private static TiaSourceTransferResult ImportSources(IDictionary<string, object> payload)
        {
            RequireAttachedProject();
            var result = new TiaSourceTransferResult();
            foreach (var plcPayload in GetDictionaryList(payload, "Plcs"))
            {
                var binding = TiaPlcBindingDto.FromPayload(plcPayload);
                var plcSoftware = FindPlcSoftware(binding);
                if (plcSoftware == null)
                {
                    result.Warnings.Add("Could not resolve PLC software for '" + binding.PlcName + "'.");
                    continue;
                }

                var importedFiles = new List<string>();
                var explicitDataBlockNames = GetStringList(plcPayload, "ResetDataBlocks")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var traceFolder = GetImportTraceFolder(GetStringList(plcPayload, "SourceFiles"));
                WriteTransferTrace(traceFolder, binding.PlcName + ": import started");
                foreach (var sourceFile in GetStringList(plcPayload, "SourceFiles").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!File.Exists(sourceFile))
                        {
                            result.Warnings.Add(binding.PlcName + ": source file is missing: " + sourceFile);
                            continue;
                        }

                        plcSoftware = FindPlcSoftware(binding);
                        if (plcSoftware == null)
                        {
                            result.Warnings.Add(binding.PlcName + ": PLC software was not found before importing " + Path.GetFileName(sourceFile) + ".");
                            continue;
                        }

                        WriteTransferTrace(traceFolder, binding.PlcName + ": importing " + Path.GetFileName(sourceFile));
                        ImportSourceFile(plcSoftware, sourceFile);
                        WriteTransferTrace(traceFolder, binding.PlcName + ": imported " + Path.GetFileName(sourceFile));
                        importedFiles.Add(sourceFile);
                        result.Files.Add(sourceFile);
                        result.Messages.Add(binding.PlcName + ": imported " + Path.GetFileName(sourceFile));
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(binding.PlcName + " / " + Path.GetFileName(sourceFile) + ": " + FormatException(ex));
                        WriteTransferTrace(traceFolder, binding.PlcName + ": import failed " + Path.GetFileName(sourceFile) + " - " + FormatException(ex));
                    }
                }

                if (importedFiles.Count > 0)
                {
                    ClearCrossReferenceCache();
                    CompileDataBlocks(binding, binding.PlcName, explicitDataBlockNames, result, "after import", traceFolder);
                    result.Messages.Add(binding.PlcName + ": reset start values skipped; push imports sources only.");
                    WriteTransferTrace(traceFolder, binding.PlcName + ": reset start values skipped by configuration");
                }
                WriteTransferTrace(traceFolder, binding.PlcName + ": import completed");
            }

            return result;
        }

        private static TiaCrossReferenceResult GetCrossReferences(IDictionary<string, object> payload)
        {
            var totalSw = Stopwatch.StartNew();
            RequireAttachedProject();
            var binding = TiaPlcBindingDto.FromPayload(payload);
            var variablePath = GetString(payload, "VariablePath").Trim();
            var maxItems = Math.Max(1, GetOptionalInt(payload, "MaxItems", 50));
            var result = new TiaCrossReferenceResult { VariablePath = variablePath };
            if (string.IsNullOrWhiteSpace(variablePath))
            {
                result.Warnings.Add("Variable path is empty.");
                return result;
            }

            var plcSoftware = FindPlcSoftware(binding);
            if (plcSoftware == null)
            {
                result.Warnings.Add("Could not resolve PLC software for '" + binding.PlcName + "'.");
                return result;
            }

            var dataBlockName = variablePath.Split('.').FirstOrDefault();
            if (string.IsNullOrWhiteSpace(dataBlockName))
            {
                result.Warnings.Add("Could not resolve data block name from variable path.");
                return result;
            }

            var dataBlock = FindBlockRecursive(plcSoftware.BlockGroup, dataBlockName) as DataBlock;
            if (dataBlock == null)
            {
                result.Warnings.Add("Data block '" + dataBlockName + "' was not found in TIA.");
                return result;
            }

            var index = GetCrossReferenceIndex(binding, dataBlockName, dataBlock);
            var lookupSw = Stopwatch.StartNew();
            index.AppendResult(variablePath, result, maxItems);
            lookupSw.Stop();

            if (result.UsageCount == 0)
            {
                result.Warnings.Add("No cross-reference usage was returned by TIA for '" + variablePath + "'.");
            }

            totalSw.Stop();
            LogXrefProfile(string.Format(
                CultureInfo.InvariantCulture,
                "query  {0}: total={1}ms lookup={2}ms usage={3}",
                variablePath,
                totalSw.ElapsedMilliseconds,
                lookupSw.ElapsedMilliseconds,
                result.UsageCount));

            return result;
        }

        private static TiaCrossReferenceBatchResult GetCrossReferencesBatch(IDictionary<string, object> payload)
        {
            var batchSw = Stopwatch.StartNew();
            RequireAttachedProject();
            var binding = TiaPlcBindingDto.FromPayload(payload);
            var result = new TiaCrossReferenceBatchResult();

            var paths = new List<string>();
            if (payload != null && payload.ContainsKey("VariablePaths") && payload["VariablePaths"] is IEnumerable rawPaths)
            {
                foreach (var raw in rawPaths)
                {
                    if (raw == null) { continue; }
                    var s = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(s)) { paths.Add(s.Trim()); }
                }
            }

            if (paths.Count == 0)
            {
                result.Warnings.Add("No variable paths supplied.");
                return result;
            }

            var plcSoftware = FindPlcSoftware(binding);
            if (plcSoftware == null)
            {
                result.Warnings.Add("Could not resolve PLC software for '" + binding.PlcName + "'.");
                return result;
            }

            // Group queries by data block name. Each block triggers at most one
            // BuildCrossReferenceIndex; everything else is in-memory lookup.
            var byBlock = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var dbName = path.Split('.').FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(dbName))
                {
                    result.Results.Add(new TiaCrossReferenceBatchItem { VariablePath = path });
                    continue;
                }
                if (!byBlock.TryGetValue(dbName, out var list))
                {
                    list = new List<string>();
                    byBlock[dbName] = list;
                }
                list.Add(path);
            }

            foreach (var pair in byBlock)
            {
                var dataBlock = FindBlockRecursive(plcSoftware.BlockGroup, pair.Key) as DataBlock;
                if (dataBlock == null)
                {
                    foreach (var path in pair.Value)
                    {
                        result.Results.Add(new TiaCrossReferenceBatchItem { VariablePath = path });
                    }
                    if (result.Warnings.Count < 8)
                    {
                        result.Warnings.Add("Data block '" + pair.Key + "' was not found in TIA.");
                    }
                    continue;
                }

                var index = GetCrossReferenceIndex(binding, pair.Key, dataBlock);
                foreach (var path in pair.Value)
                {
                    var perPath = new TiaCrossReferenceResult { VariablePath = path };
                    index.AppendResult(path, perPath, 1);
                    result.Results.Add(new TiaCrossReferenceBatchItem
                    {
                        VariablePath = path,
                        UsageCount = perPath.UsageCount,
                        WriteCount = perPath.WriteCount
                    });
                }
            }

            batchSw.Stop();
            LogXrefProfile(string.Format(
                CultureInfo.InvariantCulture,
                "BATCH paths={0} blocks={1}: total={2}ms",
                paths.Count,
                byBlock.Count,
                batchSw.ElapsedMilliseconds));

            return result;
        }

        private static TiaCrossReferenceOpenResult OpenCrossReference(IDictionary<string, object> payload)
        {
            RequireAttachedProject();
            var binding = TiaPlcBindingDto.FromPayload(payload);
            var variablePath = GetString(payload, "VariablePath").Trim();
            var preferredBlockName = GetString(payload, "PreferredBlockName").Trim();
            var preferredNetwork = GetString(payload, "PreferredNetwork").Trim();
            var result = new TiaCrossReferenceOpenResult();
            var plcSoftware = FindPlcSoftware(binding);
            if (plcSoftware == null)
            {
                result.Message = "Could not resolve PLC software for '" + binding.PlcName + "'.";
                return result;
            }

            var sourceBlockName = variablePath.Split('.').FirstOrDefault() ?? "";
            var block = FindBlockRecursive(plcSoftware.BlockGroup, sourceBlockName);

            if (block == null)
            {
                result.Message = "No TIA block containing the variable was found.";
                return result;
            }

            var targetBlock = string.IsNullOrWhiteSpace(preferredBlockName)
                ? block
                : FindBlockRecursive(plcSoftware.BlockGroup, preferredBlockName) ?? block;

            targetBlock.ShowInEditor();
            Thread.Sleep(400);
            MaximizeTiaPortalWindows();
            result.OpenedBlock = true;
            result.InvokedCrossReferenceCommand = false;
            result.Message = "Opened '" + targetBlock.Name + "' in TIA Portal.";
            if (!string.IsNullOrWhiteSpace(preferredNetwork))
            {
                result.Message += " Network: " + preferredNetwork + ".";
            }

            if (!string.IsNullOrWhiteSpace(preferredBlockName))
            {
                result.Message += " Reference usage was found in '" + preferredBlockName + "'.";
            }

            return result;
        }

        private static TiaPlcControlResult ExecutePlcControl(IDictionary<string, object> payload)
        {
            RequireAttachedProject();
            var binding = TiaPlcBindingDto.FromPayload(payload);
            var command = GetString(payload, "Command").Trim();
            var result = new TiaPlcControlResult { Command = command };
            var plcSoftware = FindPlcSoftware(binding);
            if (plcSoftware == null)
            {
                result.Warnings.Add("Could not resolve PLC software for '" + binding.PlcName + "'.");
                return result;
            }

            var onlineProvider = FindOnlineProvider(binding, plcSoftware);
            if (onlineProvider == null)
            {
                result.Supported = false;
                result.Warnings.Add("TIA OnlineProvider is not available for '" + plcSoftware.Name + "'.");
                return result;
            }

            if (command.Equals("GoOnline", StringComparison.OrdinalIgnoreCase))
            {
                result.State = onlineProvider.GoOnline().ToString();
                result.Success = true;
                result.Messages.Add("TIA Portal was forced online for '" + plcSoftware.Name + "'.");
                return result;
            }

            if (command.Equals("GoOffline", StringComparison.OrdinalIgnoreCase))
            {
                onlineProvider.GoOffline();
                result.State = onlineProvider.State.ToString();
                result.Success = true;
                result.Messages.Add("TIA Portal was switched offline for '" + plcSoftware.Name + "'.");
                return result;
            }

            if (command.Equals("GetOnlineState", StringComparison.OrdinalIgnoreCase))
            {
                result.State = onlineProvider.State.ToString();
                result.Success = true;
                result.Messages.Add("Current TIA online state for '" + plcSoftware.Name + "' is " + result.State + ".");
                return result;
            }

            result.Supported = false;
            result.State = onlineProvider.State.ToString();
            result.Warnings.Add("Command '" + command + "' is not exposed by the installed TIA Openness V18 OnlineProvider. Available safe API commands are GoOnline, GoOffline and GetOnlineState.");
            return result;
        }

        private static OnlineProvider FindOnlineProvider(TiaPlcBindingDto binding, PlcSoftware plcSoftware)
        {
            var provider = plcSoftware.GetService<OnlineProvider>();
            if (provider != null)
            {
                return provider;
            }

            foreach (var device in _project.Devices)
            {
                foreach (var item in device.DeviceItems)
                {
                    provider = FindOnlineProviderRecursive(device.Name, item, binding);
                    if (provider != null)
                    {
                        return provider;
                    }
                }
            }

            return null;
        }

        private static OnlineProvider FindOnlineProviderRecursive(string deviceName, DeviceItem item, TiaPlcBindingDto binding)
        {
            var provider = item.GetService<OnlineProvider>();
            if (provider != null &&
                (deviceName.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Equals(binding.SoftwareName, StringComparison.OrdinalIgnoreCase)))
            {
                return provider;
            }

            foreach (var child in item.DeviceItems)
            {
                var found = FindOnlineProviderRecursive(deviceName, child, binding);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static IEnumerable<SourceObject> EnumerateSourceObjects(SourceObjectComposition sources)
        {
            if (sources == null)
            {
                yield break;
            }

            foreach (SourceObject source in sources)
            {
                yield return source;
                foreach (var child in EnumerateSourceObjects(source.Children))
                {
                    yield return child;
                }
            }
        }

        private static CrossReferenceIndex GetCrossReferenceIndex(TiaPlcBindingDto binding, string dataBlockName, DataBlock dataBlock)
        {
            var cacheKey = string.Join("|", new[]
            {
                SafeProjectPath(_project),
                binding.PlcName,
                binding.DeviceName,
                binding.SoftwareName,
                dataBlockName
            });

            lock (CrossReferenceCacheLock)
            {
                if (CrossReferenceCache.TryGetValue(cacheKey, out var cached))
                {
                    LogXrefProfile("cache-hit " + dataBlockName);
                    return cached;
                }
            }

            var sw = Stopwatch.StartNew();
            var built = BuildCrossReferenceIndex(dataBlock);
            sw.Stop();
            LogXrefProfile(string.Format(
                CultureInfo.InvariantCulture,
                "BUILD {0}: {1} ms (entries={2})",
                dataBlockName,
                sw.ElapsedMilliseconds,
                built.EntryCount));

            lock (CrossReferenceCacheLock)
            {
                if (!CrossReferenceCache.ContainsKey(cacheKey))
                {
                    CrossReferenceCache[cacheKey] = built;
                }

                return CrossReferenceCache[cacheKey];
            }
        }

        private static void LogXrefProfile(string message)
        {
            try
            {
                if (_xrefProfileLogPath == null)
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PlantConfigurator",
                        "logs");
                    Directory.CreateDirectory(dir);
                    _xrefProfileLogPath = Path.Combine(
                        dir,
                        "tia-xref-profile-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                }

                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}{2}",
                    DateTime.Now,
                    message,
                    Environment.NewLine);

                lock (XrefProfileLock)
                {
                    File.AppendAllText(_xrefProfileLogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private static CrossReferenceIndex BuildCrossReferenceIndex(DataBlock dataBlock)
        {
            var index = new CrossReferenceIndex();
            var crossReferenceService = dataBlock.GetService<CrossReferenceService>();
            if (crossReferenceService == null)
            {
                index.Warning = "TIA CrossReferenceService is not available for data block '" + dataBlock.Name + "'.";
                return index;
            }

            var root = crossReferenceService.GetCrossReferences(CrossReferenceFilter.ObjectsWithReferences);
            foreach (var source in EnumerateSourceObjects(root.Sources))
            {
                var entry = CreateCrossReferenceEntry(source);
                if (entry.UsageCount == 0)
                {
                    continue;
                }

                foreach (var key in GetCrossReferenceSourceKeys(source))
                {
                    index.Add(key, entry);
                }
            }

            return index;
        }

        private static IEnumerable<string> GetCrossReferenceSourceKeys(SourceObject source)
        {
            var candidates = new[]
            {
                source.Name,
                source.Path,
                source.Address,
                FirstNonEmpty(source.Path, "") + "." + FirstNonEmpty(source.Name, "")
            };

            foreach (var candidate in candidates)
            {
                var normalizedCandidate = NormalizeCrossReferenceText(candidate);
                if (string.IsNullOrWhiteSpace(normalizedCandidate))
                {
                    continue;
                }

                yield return normalizedCandidate;
            }
        }

        private static CrossReferenceEntry CreateCrossReferenceEntry(SourceObject source)
        {
            var entry = new CrossReferenceEntry();
            foreach (ReferenceObject reference in source.References)
            {
                var locationCount = 0;
                var writeCount = 0;
                foreach (Location location in reference.Locations)
                {
                    locationCount++;
                    var accessText = location.Access.ToString();
                    if (IsWriteAccess(accessText))
                    {
                        writeCount++;
                    }
                    entry.Items.Add(new TiaCrossReferenceItem
                    {
                        BlockName = FirstNonEmpty(reference.Name, reference.TypeName, source.Name),
                        Count = 1,
                        Network = ExtractNetwork(location.ReferenceLocation),
                        ReferenceLocation = FirstNonEmpty(location.ReferenceLocation, ""),
                        ReferenceType = location.ReferenceType.ToString(),
                        Access = accessText,
                        Address = FirstNonEmpty(location.Address, ""),
                        TypeName = FirstNonEmpty(location.TypeName, reference.TypeName),
                        Device = FirstNonEmpty(reference.Device, ""),
                        Path = FirstNonEmpty(reference.Path, ""),
                        Snippet = FormatCrossReferenceLocation(location)
                    });
                }

                if (locationCount == 0)
                {
                    locationCount = 1;
                    entry.Items.Add(new TiaCrossReferenceItem
                    {
                        BlockName = FirstNonEmpty(reference.Name, reference.TypeName, source.Name),
                        Count = 1,
                        Network = ExtractNetwork(reference.Path),
                        ReferenceLocation = FirstNonEmpty(reference.Path, reference.Name),
                        ReferenceType = "",
                        Access = "",
                        Address = FirstNonEmpty(reference.Address, ""),
                        TypeName = FirstNonEmpty(reference.TypeName, ""),
                        Device = FirstNonEmpty(reference.Device, ""),
                        Path = FirstNonEmpty(reference.Path, ""),
                        Snippet = FormatCrossReferenceReference(reference)
                    });
                }

                entry.UsageCount += locationCount;
                entry.WriteCount += writeCount;
            }

            return entry;
        }

        private static bool IsWriteAccess(string accessText)
        {
            if (string.IsNullOrEmpty(accessText))
            {
                return false;
            }
            // TIA Openness Access enum is typically Read/Write/ReadWrite; tolerate
            // mixed strings by checking for "write" anywhere, case-insensitive.
            return accessText.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClearCrossReferenceCache()
        {
            lock (CrossReferenceCacheLock)
            {
                CrossReferenceCache.Clear();
            }
        }

        private sealed class CrossReferenceIndex
        {
            private readonly Dictionary<string, List<CrossReferenceEntry>> _entriesByKey = new Dictionary<string, List<CrossReferenceEntry>>(StringComparer.OrdinalIgnoreCase);

            public string Warning { get; set; }

            public int EntryCount => _entriesByKey.Count;

            public void Add(string key, CrossReferenceEntry entry)
            {
                if (string.IsNullOrWhiteSpace(key) || entry == null || entry.UsageCount == 0)
                {
                    return;
                }

                if (!_entriesByKey.TryGetValue(key, out var entries))
                {
                    entries = new List<CrossReferenceEntry>();
                    _entriesByKey[key] = entries;
                }

                if (!entries.Contains(entry))
                {
                    entries.Add(entry);
                }
            }

            public void AppendResult(string variablePath, TiaCrossReferenceResult result, int maxItems)
            {
                if (!string.IsNullOrWhiteSpace(Warning))
                {
                    result.Warnings.Add(Warning);
                    return;
                }

                var normalizedVariable = NormalizeCrossReferenceText(variablePath);
                var matches = new List<CrossReferenceEntry>();
                var seen = new HashSet<CrossReferenceEntry>();
                if (_entriesByKey.TryGetValue(normalizedVariable, out var exactEntries))
                {
                    AddMatches(exactEntries, matches, seen);
                }

                if (matches.Count == 0)
                {
                    foreach (var pair in _entriesByKey)
                    {
                        if (pair.Key.EndsWith(normalizedVariable, StringComparison.OrdinalIgnoreCase) ||
                            normalizedVariable.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            AddMatches(pair.Value, matches, seen);
                        }
                    }
                }

                foreach (var entry in matches)
                {
                    result.UsageCount += entry.UsageCount;
                    result.WriteCount += entry.WriteCount;
                    if (result.Items.Count >= maxItems)
                    {
                        continue;
                    }

                    foreach (var item in entry.Items)
                    {
                        if (result.Items.Count >= maxItems)
                        {
                            break;
                        }

                        result.Items.Add(item);
                    }
                }
            }

            private static void AddMatches(IEnumerable<CrossReferenceEntry> entries, List<CrossReferenceEntry> matches, HashSet<CrossReferenceEntry> seen)
            {
                foreach (var entry in entries)
                {
                    if (seen.Add(entry))
                    {
                        matches.Add(entry);
                    }
                }
            }
        }

        private sealed class CrossReferenceEntry
        {
            public int UsageCount { get; set; }
            public int WriteCount { get; set; }
            public List<TiaCrossReferenceItem> Items { get; } = new List<TiaCrossReferenceItem>();
        }

        private static string FormatCrossReferenceReference(ReferenceObject reference)
        {
            return string.Join(" | ", new[]
            {
                reference.Name,
                reference.Path,
                reference.Address,
                reference.TypeName,
                reference.Device
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string FormatCrossReferenceLocation(Location location)
        {
            return string.Join(" | ", new[]
            {
                location.ReferenceLocation,
                location.ReferenceType.ToString(),
                location.Access.ToString(),
                location.Address,
                location.TypeName
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string ExtractNetwork(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var text = value.Trim();
            var nwIndex = text.IndexOf("NW", StringComparison.OrdinalIgnoreCase);
            if (nwIndex >= 0)
            {
                var index = nwIndex + 2;
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                var start = index;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }

                return index > start ? "NW" + text.Substring(start, index - start) : "";
            }

            var networkIndex = text.IndexOf("Network", StringComparison.OrdinalIgnoreCase);
            if (networkIndex < 0)
            {
                return "";
            }

            var numberStart = networkIndex + "Network".Length;
            while (numberStart < text.Length && !char.IsDigit(text[numberStart]))
            {
                numberStart++;
            }

            var numberEnd = numberStart;
            while (numberEnd < text.Length && char.IsDigit(text[numberEnd]))
            {
                numberEnd++;
            }

            return numberEnd > numberStart ? "NW" + text.Substring(numberStart, numberEnd - numberStart) : "";
        }

        private static string NormalizeCrossReferenceText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? ""
                : value.Replace("\"", "")
                    .Replace("'", "")
                    .Replace(" ", "")
                    .Replace("\\", ".")
                    .Replace("/", ".")
                    .Trim('.');
        }

        private static string ExportSource(PlcSoftware plcSoftware, TiaSourceArtifactDto artifact, string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(artifact.TargetName))
            {
                return "";
            }

            var fileName = FirstNonEmpty(artifact.FileName, artifact.TargetName + ".db");
            var filePath = Path.GetFullPath(Path.Combine(outputFolder, fileName));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            IGenerateSource sourceObject;
            if (artifact.Kind.Equals("PlcType", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(filePath).Equals(".udt", StringComparison.OrdinalIgnoreCase))
            {
                var type = FindTypeRecursive(plcSoftware.TypeGroup, artifact.TargetName);
                if (type == null)
                {
                    throw new FileNotFoundException("TIA PLC type '" + artifact.TargetName + "' was not found in PLC '" + plcSoftware.Name + "'.");
                }

                sourceObject = (IGenerateSource)type;
            }
            else
            {
                var block = FindBlockRecursive(plcSoftware.BlockGroup, artifact.TargetName);
                if (block == null)
                {
                    throw new FileNotFoundException("TIA block '" + artifact.TargetName + "' was not found in PLC '" + plcSoftware.Name + "'.");
                }

                sourceObject = (IGenerateSource)block;
            }

            plcSoftware.ExternalSourceGroup.GenerateSource(new[] { sourceObject }, new FileInfo(filePath));
            return filePath;
        }

        private static string GetImportTraceFolder(IList<string> sourceFiles)
        {
            var firstFile = sourceFiles.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file));
            var folder = string.IsNullOrWhiteSpace(firstFile) ? "" : Path.GetDirectoryName(Path.GetFullPath(firstFile));
            return string.IsNullOrWhiteSpace(folder) ? AppDomain.CurrentDomain.BaseDirectory : folder;
        }

        private static void WriteTransferTrace(string outputFolder, string message)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
                File.AppendAllText(
                    Path.Combine(outputFolder, "_tia_helper_trace.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static IList<string> ImportSourceFile(PlcSoftware plcSoftware, string filePath)
        {
            var sourceName = "cwc_sync_" + Path.GetFileNameWithoutExtension(filePath) + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            sourceName = SanitizeEngineeringName(sourceName);
            var source = plcSoftware.ExternalSourceGroup.ExternalSources.CreateFromFile(sourceName, Path.GetFullPath(filePath));
            try
            {
                return source.GenerateBlocksFromSource(GenerateBlockOption.KeepOnError)
                    .OfType<DataBlock>()
                    .Select(block => block.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                source.Delete();
            }
        }

        private static void CompileDataBlocks(TiaPlcBindingDto binding, string plcName, IEnumerable<string> dataBlockNames, TiaSourceTransferResult result, string phase, string traceFolder)
        {
            var blockNames = dataBlockNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blockNames.Count == 0)
            {
                result.Messages.Add(plcName + ": data block compile skipped " + phase + "; no explicit data blocks were provided.");
                return;
            }

            var compiledCount = 0;
            try
            {
                foreach (var blockName in blockNames)
                {
                    try
                    {
                        WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " started");
                        var plcSoftware = FindPlcSoftware(binding);
                        if (plcSoftware == null)
                        {
                            result.Warnings.Add(plcName + " / " + blockName + ": PLC software was not found for compile " + phase + ".");
                            WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " skipped; PLC software not found");
                            continue;
                        }

                        var block = FindBlockRecursive(plcSoftware.BlockGroup, blockName);
                        if (block == null)
                        {
                            result.Warnings.Add(plcName + " / " + blockName + ": data block was not found for compile " + phase + ".");
                            WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " skipped; block not found");
                            continue;
                        }

                        var compilable = block.GetService<ICompilable>();
                        if (compilable == null)
                        {
                            result.Warnings.Add(plcName + " / " + blockName + ": compile provider is not available.");
                            WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " skipped; compile provider unavailable");
                            continue;
                        }

                        var compileResult = compilable.Compile();
                        WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " completed with state " + compileResult.State);
                        compiledCount++;
                        result.Messages.Add(plcName + " / " + blockName + ": compile " + phase + " completed with state " + compileResult.State + " (" + compileResult.ErrorCount + " error(s), " + compileResult.WarningCount + " warning(s)).");
                        if (compileResult.ErrorCount > 0)
                        {
                            result.Warnings.Add(plcName + " / " + blockName + ": compile " + phase + " finished with " + compileResult.ErrorCount + " error(s). Check TIA Portal compile messages.");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(plcName + " / " + blockName + ": compile " + phase + " failed: " + FormatException(ex));
                        WriteTransferTrace(traceFolder, plcName + " / " + blockName + ": compile " + phase + " failed - " + FormatException(ex));
                    }
                }

                result.Messages.Add(plcName + ": compiled " + compiledCount + " explicit data block(s) " + phase + ".");
            }
            catch (Exception ex)
            {
                result.Warnings.Add(plcName + ": data block compile " + phase + " failed: " + FormatException(ex));
            }
        }

        private static void RestoreTiaPortalWindows()
        {
            var portalProcessIds = Process.GetProcessesByName("Siemens.Automation.Portal")
                .Select(process => process.Id)
                .ToHashSet();
            if (portalProcessIds.Count == 0)
            {
                return;
            }

            foreach (var windowHandle in EnumerateTopLevelWindows(portalProcessIds))
            {
                try
                {
                    ShowWindow(windowHandle, SwRestore);
                    SetForegroundWindow(windowHandle);
                }
                catch
                {
                }
            }
        }

        private static void MaximizeTiaPortalWindows()
        {
            var portalProcessIds = Process.GetProcessesByName("Siemens.Automation.Portal")
                .Select(process => process.Id)
                .ToHashSet();
            if (portalProcessIds.Count == 0)
            {
                return;
            }

            foreach (var windowHandle in EnumerateTopLevelWindows(portalProcessIds))
            {
                try
                {
                    ShowWindow(windowHandle, SwMaximize);
                    SetForegroundWindow(windowHandle);
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<IntPtr> EnumerateTopLevelWindows(HashSet<int> processIds)
        {
            var handles = new List<IntPtr>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var processId);
                if (processIds.Contains((int)processId) && IsWindowVisible(handle))
                {
                    handles.Add(handle);
                }

                return true;
            }, IntPtr.Zero);
            return handles;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SwRestore = 9;
        private const int SwMaximize = 3;

        private static PlcSoftware FindPlcSoftware(TiaPlcBindingDto binding)
        {
            foreach (var device in _project.Devices)
            {
                foreach (var item in device.DeviceItems)
                {
                    var plc = FindPlcSoftwareRecursive(device.Name, item, binding);
                    if (plc != null)
                    {
                        return plc;
                    }
                }
            }

            return null;
        }

        private static PlcSoftware FindPlcSoftwareRecursive(string deviceName, DeviceItem item, TiaPlcBindingDto binding)
        {
            var container = item.GetService<SoftwareContainer>();
            var plc = container == null ? null : container.Software as PlcSoftware;
            if (plc != null)
            {
                if (plc.Name.Equals(binding.SoftwareName, StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return plc;
                }
            }

            foreach (var child in item.DeviceItems)
            {
                var found = FindPlcSoftwareRecursive(deviceName, child, binding);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static PlcBlock FindBlockRecursive(PlcBlockSystemGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            var exact = group.Blocks.Find(targetName);
            if (exact != null)
            {
                return exact;
            }

            foreach (var subGroup in group.Groups)
            {
                var found = FindBlockRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockSystemGroup group)
        {
            if (group == null)
            {
                yield break;
            }

            foreach (PlcBlock block in group.Blocks)
            {
                yield return block;
            }

            foreach (var subGroup in group.Groups)
            {
                foreach (var block in EnumerateBlocks(subGroup))
                {
                    yield return block;
                }
            }
        }

        private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockUserGroup group)
        {
            if (group == null)
            {
                yield break;
            }

            foreach (PlcBlock block in group.Blocks)
            {
                yield return block;
            }

            foreach (var subGroup in group.Groups)
            {
                foreach (var block in EnumerateBlocks(subGroup))
                {
                    yield return block;
                }
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var count = 0;
            var index = 0;
            while (true)
            {
                index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += value.Length;
            }
        }

        private static string CreateSnippet(string text, string value)
        {
            var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return "";
            }

            var start = Math.Max(0, index - 80);
            var length = Math.Min(text.Length - start, value.Length + 160);
            return text.Substring(start, length)
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();
        }

        private static PlcBlock FindBlockRecursive(PlcBlockUserGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            var exact = group.Blocks.Find(targetName);
            if (exact != null)
            {
                return exact;
            }

            foreach (var subGroup in group.Groups)
            {
                var found = FindBlockRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static PlcType FindTypeRecursive(PlcTypeSystemGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            var exact = group.Types.Find(targetName);
            if (exact != null)
            {
                return exact;
            }

            foreach (var subGroup in group.Groups)
            {
                var found = FindTypeRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static PlcType FindTypeRecursive(PlcTypeUserGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            var exact = group.Types.Find(targetName);
            if (exact != null)
            {
                return exact;
            }

            foreach (var subGroup in group.Groups)
            {
                var found = FindTypeRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void DiscoverSoftware(string deviceName, DeviceItem item, IList<TiaDiscoveredPlc> plcs)
        {
            var container = item.GetService<SoftwareContainer>();
            var plc = container == null ? null : container.Software as PlcSoftware;
            if (plc != null)
            {
                plcs.Add(new TiaDiscoveredPlc
                {
                    DeviceName = deviceName,
                    SoftwareName = plc.Name,
                    Objects = DiscoverObjectsGroup(plc)
                });
            }

            foreach (var child in item.DeviceItems)
            {
                DiscoverSoftware(deviceName, child, plcs);
            }
        }

        private static List<TiaPlcObjectInfo> DiscoverObjectsGroup(PlcSoftware plc)
        {
            var objectsGroup = FindBlockGroupRecursive(plc.BlockGroup, "OBJECTS");
            return objectsGroup == null
                ? new List<TiaPlcObjectInfo>()
                : EnumerateObjectBlocks(objectsGroup, "OBJECTS").ToList();
        }

        private static PlcBlockUserGroup FindBlockGroupRecursive(PlcBlockSystemGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            foreach (var subGroup in group.Groups)
            {
                if (subGroup.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return subGroup;
                }

                var found = FindBlockGroupRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static PlcBlockUserGroup FindBlockGroupRecursive(PlcBlockUserGroup group, string targetName)
        {
            if (group == null)
            {
                return null;
            }

            foreach (var subGroup in group.Groups)
            {
                if (subGroup.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return subGroup;
                }

                var found = FindBlockGroupRecursive(subGroup, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static IEnumerable<TiaPlcObjectInfo> EnumerateObjectBlocks(PlcBlockUserGroup group, string path)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                yield return new TiaPlcObjectInfo
                {
                    Name = block.Name,
                    Path = path + "\\" + block.Name,
                    Kind = block.GetType().Name,
                    Number = block.Number.ToString(CultureInfo.InvariantCulture),
                    ProgrammingLanguage = block.ProgrammingLanguage.ToString()
                };
            }

            foreach (var subGroup in group.Groups)
            {
                var childPath = path + "\\" + subGroup.Name;
                foreach (var item in EnumerateObjectBlocks(subGroup, childPath))
                {
                    yield return item;
                }
            }
        }

        private static TiaPortalProcessInfo CreateProcessInfo(TiaPortalProcess process)
        {
            return new TiaPortalProcessInfo
            {
                ProcessId = process.Id,
                PortalPath = SafeFilePath(process.Path),
                ProjectPath = SafeFilePath(process.ProjectPath),
                ProjectName = ProjectNameFromPath(process.ProjectPath),
                ModeLabel = process.Mode.ToString(),
                AcquiredAtLabel = process.AcquisitionTime.ToLocalTime().ToString("g")
            };
        }

        private static string SafeProjectPath(Project project)
        {
            try
            {
                return project.Path == null ? "" : project.Path.FullName;
            }
            catch
            {
                return "";
            }
        }

        private static string SafeFilePath(FileInfo file)
        {
            try
            {
                return file == null ? "" : file.FullName;
            }
            catch
            {
                return "";
            }
        }

        private static string ProjectNameFromPath(FileInfo projectPath)
        {
            var fullPath = SafeFilePath(projectPath);
            return string.IsNullOrWhiteSpace(fullPath) ? "" : Path.GetFileNameWithoutExtension(fullPath);
        }

        private static int GetInt(IDictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key))
            {
                throw new InvalidOperationException("Missing helper payload value: " + key);
            }

            return Convert.ToInt32(payload[key]);
        }

        private static int GetOptionalInt(IDictionary<string, object> payload, string key, int defaultValue)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(payload[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string GetString(IDictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null)
            {
                return "";
            }

            return Convert.ToString(payload[key], CultureInfo.InvariantCulture) ?? "";
        }

        private static List<Dictionary<string, object>> GetDictionaryList(IDictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null)
            {
                return new List<Dictionary<string, object>>();
            }

            var values = payload[key] as IEnumerable;
            if (values == null || payload[key] is string)
            {
                return new List<Dictionary<string, object>>();
            }

            var result = new List<Dictionary<string, object>>();
            foreach (var value in values)
            {
                if (value is Dictionary<string, object> dictionary)
                {
                    result.Add(dictionary);
                }
            }

            return result;
        }

        private static List<string> GetStringList(IDictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null)
            {
                return new List<string>();
            }

            var values = payload[key] as IEnumerable;
            if (values == null || payload[key] is string)
            {
                return new List<string>();
            }

            var result = new List<string>();
            foreach (var value in values)
            {
                if (value != null)
                {
                    result.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }

            return result.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        private static void RequireAttachedProject()
        {
            if (_portal == null || _project == null)
            {
                throw new InvalidOperationException("Attach to TIA Portal first.");
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        }

        private static string SanitizeEngineeringName(string value)
        {
            var chars = value
                .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return "cwc_sync_source";
            }

            return char.IsDigit(sanitized[0]) ? "cwc_" + sanitized : sanitized;
        }

        private static void WriteResponse(HelperResponse response)
        {
            Console.WriteLine(Serializer.Serialize(response));
            Console.Out.Flush();
        }

        private static string FormatException(Exception exception)
        {
            var lines = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !lines.Contains(current.Message))
                {
                    lines.Add(current.Message);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string GetLoadedVersionLabel()
        {
            try
            {
                var version = typeof(TiaPortal).Assembly.GetName().Version;
                if (version != null && version.Major > 0)
                {
                    return "V" + version.Major;
                }
            }
            catch
            {
            }

            return "";
        }

        private static System.Reflection.Assembly ResolveTiaAssembly(object sender, ResolveEventArgs args)
        {
            var simpleName = new System.Reflection.AssemblyName(args.Name).Name;
            var path = Path.Combine(GetTiaApiDirectory(), simpleName + ".dll");
            return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
        }

        private static string GetTiaApiDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Siemens\Automation\Portal V18\PublicAPI\V18");
        }
    }

    public sealed class HelperRequest
    {
        public int Id { get; set; }
        public string Command { get; set; }
        public Dictionary<string, object> Payload { get; set; }
    }

    public sealed class HelperResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public object Payload { get; set; }
    }

    public sealed class TiaSourceTransferResult
    {
        public List<string> Files { get; set; }
        public List<string> Messages { get; set; }
        public List<string> Warnings { get; set; }

        public TiaSourceTransferResult()
        {
            Files = new List<string>();
            Messages = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class TiaCrossReferenceResult
    {
        public string VariablePath { get; set; }
        public int UsageCount { get; set; }
        public int WriteCount { get; set; }
        public List<TiaCrossReferenceItem> Items { get; set; }
        public List<string> Warnings { get; set; }

        public TiaCrossReferenceResult()
        {
            VariablePath = "";
            Items = new List<TiaCrossReferenceItem>();
            Warnings = new List<string>();
        }
    }

    public sealed class TiaCrossReferenceBatchItem
    {
        public string VariablePath { get; set; }
        public int UsageCount { get; set; }
        public int WriteCount { get; set; }

        public TiaCrossReferenceBatchItem()
        {
            VariablePath = "";
        }
    }

    public sealed class TiaCrossReferenceBatchResult
    {
        public List<TiaCrossReferenceBatchItem> Results { get; set; }
        public List<string> Warnings { get; set; }

        public TiaCrossReferenceBatchResult()
        {
            Results = new List<TiaCrossReferenceBatchItem>();
            Warnings = new List<string>();
        }
    }

    public sealed class TiaCrossReferenceItem
    {
        public string BlockName { get; set; }
        public int Count { get; set; }
        public string Network { get; set; }
        public string ReferenceLocation { get; set; }
        public string ReferenceType { get; set; }
        public string Access { get; set; }
        public string Address { get; set; }
        public string TypeName { get; set; }
        public string Device { get; set; }
        public string Path { get; set; }
        public string Snippet { get; set; }
    }

    public sealed class TiaCrossReferenceOpenResult
    {
        public bool OpenedBlock { get; set; }
        public bool InvokedCrossReferenceCommand { get; set; }
        public string Message { get; set; }
    }

    public sealed class TiaPlcControlResult
    {
        public bool Success { get; set; }
        public bool Supported { get; set; }
        public string Command { get; set; }
        public string State { get; set; }
        public List<string> Messages { get; set; }
        public List<string> Warnings { get; set; }

        public TiaPlcControlResult()
        {
            Supported = true;
            Command = "";
            State = "";
            Messages = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class TiaPlcBindingDto
    {
        public string PlcName { get; set; }
        public string DeviceName { get; set; }
        public string SoftwareName { get; set; }

        public static TiaPlcBindingDto FromPayload(IDictionary<string, object> payload)
        {
            return new TiaPlcBindingDto
            {
                PlcName = Get(payload, "PlcName"),
                DeviceName = Get(payload, "DeviceName"),
                SoftwareName = Get(payload, "SoftwareName")
            };
        }

        private static string Get(IDictionary<string, object> payload, string key)
        {
            return payload != null && payload.ContainsKey(key) && payload[key] != null
                ? Convert.ToString(payload[key], CultureInfo.InvariantCulture)
                : "";
        }
    }

    public sealed class TiaSourceArtifactDto
    {
        public string FileName { get; set; }
        public string TargetName { get; set; }
        public string Kind { get; set; }

        public static TiaSourceArtifactDto FromPayload(IDictionary<string, object> payload)
        {
            return new TiaSourceArtifactDto
            {
                FileName = Get(payload, "FileName"),
                TargetName = Get(payload, "TargetName"),
                Kind = FirstNonEmpty(Get(payload, "Kind"), "PlcBlock")
            };
        }

        private static string Get(IDictionary<string, object> payload, string key)
        {
            return payload != null && payload.ContainsKey(key) && payload[key] != null
                ? Convert.ToString(payload[key], CultureInfo.InvariantCulture)
                : "";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        }

        private static bool GetBool(IDictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null)
            {
                return false;
            }

            if (payload[key] is bool boolValue)
            {
                return boolValue;
            }

            return bool.TryParse(Convert.ToString(payload[key], CultureInfo.InvariantCulture), out var parsed) && parsed;
        }
    }

    public sealed class TiaProcessListResponse
    {
        public List<TiaPortalProcessInfo> Processes { get; set; }

        public TiaProcessListResponse()
        {
            Processes = new List<TiaPortalProcessInfo>();
        }
    }

    public sealed class TiaProjectTextsImportResult
    {
        public string Message { get; set; }
        public List<string> Warnings { get; set; }

        public TiaProjectTextsImportResult()
        {
            Warnings = new List<string>();
        }
    }

    public sealed class TiaDiscoveredPlc
    {
        public string DeviceName { get; set; }
        public string SoftwareName { get; set; }
        public List<TiaPlcObjectInfo> Objects { get; set; }

        public TiaDiscoveredPlc()
        {
            Objects = new List<TiaPlcObjectInfo>();
        }
    }

    public sealed class TiaPlcObjectInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Number { get; set; }
        public string ProgrammingLanguage { get; set; }
    }

    public sealed class TiaPortalProcessInfo
    {
        public int ProcessId { get; set; }
        public string PortalPath { get; set; }
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string ModeLabel { get; set; }
        public string AcquiredAtLabel { get; set; }
    }

    public sealed class TiaAttachedSession
    {
        public int ProcessId { get; set; }
        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }
        public string PortalPath { get; set; }
        public string PortalVersion { get; set; }
        public List<TiaDiscoveredPlc> Plcs { get; set; }

        public TiaAttachedSession()
        {
            Plcs = new List<TiaDiscoveredPlc>();
        }
    }
}
