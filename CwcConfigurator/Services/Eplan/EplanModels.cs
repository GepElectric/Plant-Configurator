namespace CwcConfigurator.Services.Eplan;

/// <summary>Hardware view of an EPLAN Electric P8 project read from a PXF export (.epj).</summary>
public sealed class EplanProject
{
    public string SourcePath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public IReadOnlyList<EplanPage> Pages { get; init; } = [];
    /// <summary>PLC stations: CPUs and distributed I/O stations (IM), in project order.</summary>
    public IReadOnlyList<EplanStation> Stations { get; init; } = [];
}

public sealed class EplanPage
{
    /// <summary>1-based position of the page in the whole project (= page number in a PDF of all schematics).</summary>
    public int GlobalNumber { get; init; }
    /// <summary>Higher-level function of the page, e.g. "1CJA01".</summary>
    public string Function { get; init; } = "";
    /// <summary>Page name inside its document, e.g. "22".</summary>
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>A PLC station: a CPU with its local modules, or a distributed I/O station (IM) that belongs to some CPU.</summary>
public sealed class EplanStation
{
    public string Function { get; init; } = "";
    /// <summary>Device tag of the CPU/IM without leading '-', e.g. "A101".</summary>
    public string HeadDeviceTag { get; init; } = "";
    public string HeadPartNumber { get; init; } = "";
    public string HeadType { get; init; } = "";
    public bool IsCpu { get; init; }
    public bool IsDistributed => !IsCpu;
    /// <summary>I/O modules in rack order (as drawn on the PLC CONFIGURATION page).</summary>
    public IReadOnlyList<EplanModule> Modules { get; init; } = [];
    public string DisplayName => $"={Function} -{HeadDeviceTag} {HeadType}".Trim();
}

public enum EplanModuleKind
{
    DigitalInput,
    DigitalOutput,
    AnalogInput,
    RtdInput,
    Counter,
    AnalogOutput
}

public sealed class EplanModule
{
    public string Function { get; init; } = "";
    /// <summary>Device tag without leading '-', e.g. "A111" — used as MODULE NAME.</summary>
    public string DeviceTag { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public string Type { get; init; } = "";
    public EplanModuleKind Kind { get; init; }
    /// <summary>Main channels; for a counter module the list holds its terminals and the module has no ports.</summary>
    public IReadOnlyList<EplanChannel> Channels { get; init; } = [];
}

public sealed class EplanChannel
{
    /// <summary>Channel number on the module (0-based). Null for counter modules.</summary>
    public int? Port { get; init; }
    /// <summary>Terminal (connection point designation) on the module, e.g. "2".</summary>
    public string Terminal { get; init; } = "";
    /// <summary>Connection point description, e.g. "DI1", "I0+", "M2+".</summary>
    public string ConnectionDescription { get; init; } = "";
    public string Address { get; init; } = "";
    /// <summary>Signal name from the schematic (path function text).</summary>
    public string SignalName { get; init; } = "";
    /// <summary>Device connected to the PLC terminal, e.g. "-1K12:14".</summary>
    public string ConnectedTo { get; init; } = "";
    public EplanPageRef? Schematic { get; init; }
    public EplanPageRef? Overview { get; init; }
}

/// <summary>Position on a schematic page: page within its document plus the path (column).</summary>
public sealed class EplanPageRef
{
    public string Function { get; init; } = "";
    public string Page { get; init; } = "";
    public int Column { get; init; }
    public int GlobalPage { get; init; }
    /// <summary>EPLAN cross-reference style, e.g. "=2CJA01/22.2".</summary>
    public string Display => $"={Function}/{Page}.{Column}";
}
