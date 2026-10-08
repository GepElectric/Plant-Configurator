using System.Text.RegularExpressions;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Eplan;

/// <summary>One EPLAN channel placed on a hardware row of a PLC database.</summary>
public sealed class EplanPlannedChannel
{
    public EplanModuleKind Kind { get; init; }
    public int ModuleNumber { get; init; }
    public string ModuleName { get; init; } = "";
    public int? Port { get; init; }
    public string StationFunction { get; init; } = "";
    public EplanChannel Channel { get; init; } = new();
}

public sealed class EplanPlannedModule
{
    public EplanModuleKind Kind { get; init; }
    public int ModuleNumber { get; init; }
    public string ModuleName { get; init; } = "";
    public EplanModule Module { get; init; } = new();
    public string StationFunction { get; init; } = "";
}

/// <summary>What one CPU (with its distributed stations) writes into one PLC database.</summary>
public sealed class EplanMappingPlan
{
    public string CpuFunction { get; init; } = "";
    public IReadOnlyList<EplanPlannedModule> Modules { get; init; } = [];
    public IReadOnlyList<EplanPlannedChannel> Channels { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class EplanApplyResult
{
    public int ChannelsWritten { get; init; }
    public int RowsCleared { get; init; }
    public IReadOnlyList<string> ModuleNameChanges { get; init; } = [];
}

/// <summary>
/// Maps EPLAN stations onto Plant Configurator hardware tables.
/// Module numbers are counted per module kind from 0: first the CPU's own rack, then each assigned distributed
/// station in the chosen order. Only the EPLAN documentation columns and MODULE NAMES are written; IDs, CONFIG and
/// signal names that belong to the PLC program are never touched.
/// </summary>
public static class EplanHardwareMapper
{
    /// <summary>Table capacity per kind: modules × ports (as created by <see cref="PlcDatabase.CreateDefault"/>).</summary>
    public static (int Modules, int Ports) Capacity(EplanModuleKind kind) => kind switch
    {
        EplanModuleKind.DigitalInput => (16, 16),
        EplanModuleKind.DigitalOutput => (16, 16),
        EplanModuleKind.AnalogInput => (6, 8),
        EplanModuleKind.RtdInput => (6, 8),
        EplanModuleKind.Counter => (4, 0),
        EplanModuleKind.AnalogOutput => (2, 4),
        _ => (0, 0)
    };

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    // ---------------------------------------------------------------- automap

    /// <summary>
    /// First proposal, to be checked by a person: keeps every previous decision that still fits the project,
    /// assigns each new distributed station to the CPU whose function starts with the same unit digit
    /// (KKS: =0BFA01 → =0CJA01, =1MKV10 → =1CJA01) and each CPU to the PLC database whose name carries that unit number.
    /// </summary>
    public static List<EplanCpuAssignment> AutoMap(EplanProject project, IReadOnlyList<PlcDatabase> databases,
        IReadOnlyList<EplanCpuAssignment>? previous = null)
    {
        var cpus = project.Stations.Where(s => s.IsCpu).ToList();
        var distributed = project.Stations.Where(s => s.IsDistributed).Select(s => s.Function).ToList();
        var result = new List<EplanCpuAssignment>();

        foreach (var cpu in cpus)
        {
            var old = previous?.FirstOrDefault(a => a.CpuFunction == cpu.Function);
            result.Add(new EplanCpuAssignment
            {
                CpuFunction = cpu.Function,
                PlcDatabaseId = old is not null && databases.Any(d => d.Id == old.PlcDatabaseId)
                    ? old.PlcDatabaseId
                    : SuggestDatabase(cpu.Function, databases, result)?.Id ?? "",
                DistributedFunctions = old?.DistributedFunctions.Where(distributed.Contains).ToList() ?? []
            });
        }

        var taken = result.SelectMany(a => a.DistributedFunctions).ToHashSet();
        foreach (var station in distributed.Where(f => !taken.Contains(f)))
        {
            var unit = UnitOf(station);
            var candidates = result.Where(a => unit is not null && UnitOf(a.CpuFunction) == unit).ToList();
            if (candidates.Count == 1)
            {
                candidates[0].DistributedFunctions.Add(station);
            }
        }

        return result;
    }

    private static PlcDatabase? SuggestDatabase(string cpuFunction, IReadOnlyList<PlcDatabase> databases, List<EplanCpuAssignment> used)
    {
        var unit = UnitOf(cpuFunction);
        if (unit is null)
        {
            return null;
        }

        var free = databases.Where(d => used.All(a => a.PlcDatabaseId != d.Id)).ToList();
        var byNumber = free.Where(d => Digits.Matches(d.Name).Any(m => int.Parse(m.Value) == unit)).ToList();
        return byNumber.Count == 1 ? byNumber[0] : null;
    }

    /// <summary>Unit number = first digit of the KKS function (=1CJA01 → 1, =0BFA01 → 0).</summary>
    public static int? UnitOf(string function) =>
        function.Length > 0 && char.IsDigit(function[0]) ? function[0] - '0' : null;

    // ---------------------------------------------------------------- plan

    public static EplanMappingPlan BuildPlan(EplanProject project, EplanCpuAssignment assignment)
    {
        var warnings = new List<string>();
        var stations = new List<EplanStation>();
        var cpu = project.Stations.FirstOrDefault(s => s.Function == assignment.CpuFunction);
        if (cpu is null)
        {
            warnings.Add($"CPU ={assignment.CpuFunction} is not in the EPLAN project.");
        }
        else
        {
            stations.Add(cpu);
        }

        foreach (var function in assignment.DistributedFunctions)
        {
            var station = project.Stations.FirstOrDefault(s => s.Function == function);
            if (station is null)
            {
                warnings.Add($"Distributed station ={function} is not in the EPLAN project.");
            }
            else
            {
                stations.Add(station);
            }
        }

        var allModules = stations.SelectMany(s => s.Modules.Select(m => (Station: s, Module: m))).ToList();
        var duplicateTags = allModules.GroupBy(x => x.Module.DeviceTag).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var modules = new List<EplanPlannedModule>();
        var channels = new List<EplanPlannedChannel>();
        foreach (var kindGroup in allModules.GroupBy(x => x.Module.Kind))
        {
            var kind = kindGroup.Key;
            var (maxModules, maxPorts) = Capacity(kind);
            var number = 0;
            foreach (var (station, module) in kindGroup)
            {
                if (number >= maxModules)
                {
                    warnings.Add($"{KindName(kind)}: -{module.DeviceTag} (={station.Function}) skipped, the table holds only {maxModules} modules.");
                    continue;
                }

                // the same tag can exist in two stations (=1CJA01-A111 and =1MKV10-A111): keep names unique
                var name = duplicateTags.Contains(module.DeviceTag) ? $"{station.Function}-{module.DeviceTag}" : module.DeviceTag;
                modules.Add(new EplanPlannedModule { Kind = kind, ModuleNumber = number, ModuleName = name, Module = module, StationFunction = station.Function });
                // a counter is one row per module: take its best documented terminal
                var moduleChannels = kind == EplanModuleKind.Counter
                    ? module.Channels.OrderBy(c => c.SignalName.Length == 0).Take(1)
                    : module.Channels;
                foreach (var channel in moduleChannels)
                {
                    if (kind != EplanModuleKind.Counter && channel.Port is not { } port)
                    {
                        continue;
                    }

                    if (kind != EplanModuleKind.Counter && channel.Port >= maxPorts)
                    {
                        warnings.Add($"{KindName(kind)}: -{module.DeviceTag} port {channel.Port} skipped, the table holds only {maxPorts} ports.");
                        continue;
                    }

                    channels.Add(new EplanPlannedChannel
                    {
                        Kind = kind,
                        ModuleNumber = number,
                        ModuleName = name,
                        Port = kind == EplanModuleKind.Counter ? null : channel.Port,
                        StationFunction = station.Function,
                        Channel = channel
                    });
                }

                number++;
            }
        }

        return new EplanMappingPlan
        {
            CpuFunction = assignment.CpuFunction,
            Modules = modules.OrderBy(m => m.Kind).ThenBy(m => m.ModuleNumber).ToList(),
            Channels = channels.OrderBy(c => c.Kind).ThenBy(c => c.ModuleNumber).ThenBy(c => c.Port).ToList(),
            Warnings = warnings
        };
    }

    public static string KindName(EplanModuleKind kind) => kind switch
    {
        EplanModuleKind.DigitalInput => "DI",
        EplanModuleKind.DigitalOutput => "DO",
        EplanModuleKind.AnalogInput => "AI",
        EplanModuleKind.RtdInput => "RTD",
        EplanModuleKind.Counter => "COUNTER",
        EplanModuleKind.AnalogOutput => "AO",
        _ => kind.ToString()
    };

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Writes the plan into a PLC database: EPLAN columns of every hardware row are replaced (rows the plan does not
    /// reach are cleared) and MODULE NAMES get the EPLAN device tags. Missing rows are created.
    /// </summary>
    public static EplanApplyResult Apply(EplanMappingPlan plan, PlcDatabase database)
    {
        var cleared = 0;
        cleared += ClearAll(database.DigitalInputs);
        cleared += ClearAll(database.DigitalOutputs);
        cleared += ClearAll(database.AnalogInputs);
        cleared += ClearAll(database.RtdInputs);
        cleared += ClearAll(database.Counters);
        cleared += ClearAll(database.AnalogOutputs);

        var nameChanges = new List<string>();
        foreach (var module in plan.Modules)
        {
            var catalog = CatalogFor(database.ModuleCatalog, module.Kind);
            var row = catalog.FirstOrDefault(r => r.ModuleNumber == module.ModuleNumber);
            if (row is null)
            {
                row = new ModuleNameRow { ModuleNumber = module.ModuleNumber };
                catalog.Add(row);
            }

            if (row.ModuleName != module.ModuleName)
            {
                nameChanges.Add($"{KindName(module.Kind)} {module.ModuleNumber}: '{row.ModuleName}' → '{module.ModuleName}'");
                row.ModuleName = module.ModuleName;
            }
        }

        var written = 0;
        foreach (var c in plan.Channels)
        {
            var target = FindOrCreateRow(database, c);
            Set(target, c.Channel);
            written++;
        }

        return new EplanApplyResult { ChannelsWritten = written, RowsCleared = cleared, ModuleNameChanges = nameChanges };
    }

    private static List<ModuleNameRow> CatalogFor(ModuleCatalog catalog, EplanModuleKind kind) => kind switch
    {
        EplanModuleKind.DigitalInput => catalog.DigitalInputs,
        EplanModuleKind.DigitalOutput => catalog.DigitalOutputs,
        EplanModuleKind.AnalogInput => catalog.AnalogInputs,
        EplanModuleKind.RtdInput => catalog.RtdInputs,
        EplanModuleKind.Counter => catalog.Counters,
        _ => catalog.AnalogOutputs
    };

    private static object FindOrCreateRow(PlcDatabase database, EplanPlannedChannel c)
    {
        var port = c.Port ?? 0;
        switch (c.Kind)
        {
            case EplanModuleKind.DigitalInput:
            case EplanModuleKind.DigitalOutput:
            {
                var rows = c.Kind == EplanModuleKind.DigitalInput ? database.DigitalInputs : database.DigitalOutputs;
                var row = rows.FirstOrDefault(r => r.ModuleNumber == c.ModuleNumber && r.Port == port);
                if (row is null)
                {
                    row = new DigitalSignalConfig { ModuleNumber = c.ModuleNumber, Port = port };
                    rows.Add(row);
                }

                row.ModuleName = c.ModuleName;
                return row;
            }
            case EplanModuleKind.AnalogInput:
            case EplanModuleKind.RtdInput:
            {
                var rows = c.Kind == EplanModuleKind.AnalogInput ? database.AnalogInputs : database.RtdInputs;
                var row = rows.FirstOrDefault(r => r.ModuleNumber == c.ModuleNumber && r.Port == port);
                if (row is null)
                {
                    row = new AnalogSignalConfig { ModuleNumber = c.ModuleNumber, Port = port };
                    rows.Add(row);
                }

                row.ModuleName = c.ModuleName;
                return row;
            }
            case EplanModuleKind.Counter:
            {
                var row = database.Counters.FirstOrDefault(r => r.ModuleNumber == c.ModuleNumber);
                if (row is null)
                {
                    row = new CounterConfig { ModuleNumber = c.ModuleNumber };
                    database.Counters.Add(row);
                }

                row.ModuleName = c.ModuleName;
                return row;
            }
            default:
            {
                var row = database.AnalogOutputs.FirstOrDefault(r => r.ModuleNumber == c.ModuleNumber && r.Port == port);
                if (row is null)
                {
                    row = new AnalogOutputConfig { ModuleNumber = c.ModuleNumber, Port = port };
                    database.AnalogOutputs.Add(row);
                }

                row.ModuleName = c.ModuleName;
                return row;
            }
        }
    }

    private static void Set(object row, EplanChannel channel)
    {
        var signal = channel.SignalName;
        var connected = channel.ConnectedTo;
        int? pdf = channel.Schematic?.GlobalPage is > 0 ? channel.Schematic.GlobalPage : null;
        var page = channel.Schematic?.Display ?? "";
        switch (row)
        {
            case DigitalSignalConfig d:
                d.EplanSignalName = signal; d.EplanConnectedTo = connected; d.EplanPdfPage = pdf; d.EplanSchematicPage = page;
                break;
            case AnalogSignalConfig a:
                a.EplanSignalName = signal; a.EplanConnectedTo = connected; a.EplanPdfPage = pdf; a.EplanSchematicPage = page;
                break;
            case CounterConfig k:
                k.EplanSignalName = signal; k.EplanConnectedTo = connected; k.EplanPdfPage = pdf; k.EplanSchematicPage = page;
                break;
            case AnalogOutputConfig o:
                o.EplanSignalName = signal; o.EplanConnectedTo = connected; o.EplanPdfPage = pdf; o.EplanSchematicPage = page;
                break;
        }
    }

    private static int ClearAll<T>(IEnumerable<T> rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            switch (row)
            {
                case DigitalSignalConfig d when HasEplan(d.EplanSignalName, d.EplanConnectedTo, d.EplanPdfPage, d.EplanSchematicPage):
                    d.EplanSignalName = ""; d.EplanConnectedTo = ""; d.EplanPdfPage = null; d.EplanSchematicPage = ""; count++;
                    break;
                case AnalogSignalConfig a when HasEplan(a.EplanSignalName, a.EplanConnectedTo, a.EplanPdfPage, a.EplanSchematicPage):
                    a.EplanSignalName = ""; a.EplanConnectedTo = ""; a.EplanPdfPage = null; a.EplanSchematicPage = ""; count++;
                    break;
                case CounterConfig k when HasEplan(k.EplanSignalName, k.EplanConnectedTo, k.EplanPdfPage, k.EplanSchematicPage):
                    k.EplanSignalName = ""; k.EplanConnectedTo = ""; k.EplanPdfPage = null; k.EplanSchematicPage = ""; count++;
                    break;
                case AnalogOutputConfig o when HasEplan(o.EplanSignalName, o.EplanConnectedTo, o.EplanPdfPage, o.EplanSchematicPage):
                    o.EplanSignalName = ""; o.EplanConnectedTo = ""; o.EplanPdfPage = null; o.EplanSchematicPage = ""; count++;
                    break;
            }
        }

        return count;
    }

    private static bool HasEplan(string signal, string connected, int? pdf, string page) =>
        signal.Length > 0 || connected.Length > 0 || pdf is not null || page.Length > 0;
}
