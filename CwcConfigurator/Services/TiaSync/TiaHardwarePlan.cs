using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;

namespace CwcConfigurator.Services.TiaSync;

/// <summary>A station as read from TIA (ReadHardware).</summary>
public sealed class TiaHardwareResult
{
    public string DeviceName { get; set; } = "";
    public bool Found { get; set; }
    public string TypeIdentifier { get; set; } = "";
    public List<string> Devices { get; set; } = [];
    public List<TiaHardwareItem> Items { get; set; } = [];
    public List<TiaHardwareInterface> Interfaces { get; set; } = [];
}

public sealed class TiaHardwareItem
{
    public string Name { get; set; } = "";
    public string TypeIdentifier { get; set; } = "";
    public int Position { get; set; }
    public int Depth { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = [];
}

public sealed class TiaHardwareInterface
{
    public string Item { get; set; } = "";
    public string Address { get; set; } = "";
    public string PnDeviceName { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string IoSystem { get; set; } = "";
}

/// <summary>What BuildHardware did (or would do): one row per step.</summary>
public sealed class TiaHardwareBuildResult
{
    public bool Applied { get; set; }
    public List<TiaHardwareStep> Steps { get; set; } = [];
}

public sealed class TiaHardwareStep
{
    public string Item { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>OK, Exists, Planned, Done, Conflict, Error.</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>Request of BuildHardware, completed by the user.</summary>
public sealed class TiaHardwareBuildRequest
{
    public bool Apply { get; set; }
    public string DeviceName { get; set; } = "";
    public TiaHardwareModuleRequest Head { get; set; } = new();
    public TiaHardwareModuleRequest? BusAdapter { get; set; }
    public List<TiaHardwareModuleRequest> Modules { get; set; } = [];
    public string Ip { get; set; } = "";
    public string PnDeviceName { get; set; } = "";
    public string Subnet { get; set; } = "";
    public string IoSystem { get; set; } = "";
    public string IoControllerDevice { get; set; } = "";
    public int PnDeviceNumber { get; set; }
}

public sealed class TiaHardwareModuleRequest
{
    public string Name { get; set; } = "";
    public string Order { get; set; } = "";
    public string Version { get; set; } = "";
    public int Slot { get; set; }
    /// <summary>"1" new potential group (light BaseUnit), "0" from the left module, "" not set.</summary>
    public string PotentialGroup { get; set; } = "";
}

/// <summary>
/// The hardware plan of an EPLAN station for TIA: what EPLAN knows (order numbers, rack order) completed with what
/// the project knows (IP address, TIA device of the PLC database) and what the user enters (firmware versions,
/// PROFINET name, subnet, potential groups). Kept in the project, so it is filled once.
/// </summary>
public static class TiaHardwarePlanner
{
    /// <summary>Key of a station: EPLAN function and head device tag.</summary>
    public static string Key(EplanStation station) => $"{station.Function}|{station.HeadDeviceTag}";

    /// <summary>The saved plan of the station, completed with the EPLAN data and the project defaults.</summary>
    public static HardwareStationSettings PlanFor(CwcProject project, EplanStation station)
    {
        var key = Key(station);
        var saved = project.HardwareConfig.Stations.FirstOrDefault(item => item.Key == key);
        var plan = saved ?? new HardwareStationSettings { Key = key };

        // The PLC database of the CPU (EPLAN import assignment) gives its TIA device and IP address.
        var assignment = project.EplanImport.Assignments.FirstOrDefault(item =>
            item.CpuFunction.Equals(station.Function, StringComparison.OrdinalIgnoreCase)
            || item.DistributedFunctions.Contains(station.Function, StringComparer.OrdinalIgnoreCase));
        var database = assignment is null ? null : project.PlcDatabases.FirstOrDefault(item => item.Id == assignment.PlcDatabaseId);
        var binding = database is null ? null : project.TiaSync.PlcBindings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
        if (station.IsCpu)
        {
            if (plan.DeviceName.Length == 0)
            {
                plan.DeviceName = binding?.DeviceName is { Length: > 0 } device ? device : $"Station {station.HeadDeviceTag}";
            }

            if (plan.Ip.Length == 0 && database?.IpAddress is { Length: > 0 } ip)
            {
                plan.Ip = ip;
            }

            if (plan.IoSystem.Length == 0)
            {
                plan.IoSystem = "PROFINET IO-System";
            }
        }
        else
        {
            if (plan.DeviceName.Length == 0)
            {
                plan.DeviceName = $"Station {station.HeadDeviceTag}";
            }

            if (plan.IoControllerDevice.Length == 0 && binding?.DeviceName is { Length: > 0 } controller)
            {
                plan.IoControllerDevice = controller;
            }
        }

        if (plan.HeadName.Length == 0)
        {
            plan.HeadName = binding?.SoftwareName is { Length: > 0 } software && station.IsCpu ? software : station.HeadDeviceTag;
        }

        if (plan.HeadOrder.Length == 0)
        {
            plan.HeadOrder = FormatOrder(station.HeadPartNumber);
        }

        if (plan.PnDeviceName.Length == 0)
        {
            plan.PnDeviceName = PnName(plan.HeadName);
        }

        if (plan.Subnet.Length == 0)
        {
            plan.Subnet = "PN/IE_1";
        }

        // Modules in rack order: slot 2, 3… after the CPU / interface module (ET 200SP, S7-1500).
        var modules = new List<HardwareModuleSettings>();
        for (var index = 0; index < station.Modules.Count; index++)
        {
            var module = station.Modules[index];
            var existing = plan.Modules.FirstOrDefault(item => item.Tag == module.DeviceTag);
            modules.Add(existing ?? new HardwareModuleSettings
            {
                Tag = module.DeviceTag,
                Type = module.Type,
                Order = FormatOrder(module.PartNumber),
                Slot = plan.HeadSlot + 1 + index,
                NewPotentialGroup = index == 0 || module.PartNumber.Contains("+2D", StringComparison.OrdinalIgnoreCase)
            });
            modules[^1].Type = module.Type;
        }

        plan.Modules = modules;
        return plan;
    }

    /// <summary>Saves the plan of a station in the project (replacing the one before).</summary>
    public static void Save(CwcProject project, HardwareStationSettings plan)
    {
        project.HardwareConfig.Stations.RemoveAll(item => item.Key == plan.Key);
        project.HardwareConfig.Stations.Add(plan);
    }

    /// <summary>"6ES7131-6BH01-0BA0+BU15-P16+A0+2D" → "6ES7 131-6BH01-0BA0" (TIA order number, without the BaseUnit).</summary>
    public static string FormatOrder(string partNumber)
    {
        var order = (partNumber ?? "").Split('+')[0].Trim();
        if (order.StartsWith("SIE.", StringComparison.OrdinalIgnoreCase))
        {
            order = order[4..];
        }

        return order.Length > 4 && order[4] != ' ' && order.StartsWith("6E", StringComparison.OrdinalIgnoreCase) ? order[..4] + " " + order[4..] : order;
    }

    /// <summary>PROFINET device name: lower case letters, digits, '-' and '.'.</summary>
    public static string PnName(string name)
    {
        var text = new string((name ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>What the user still has to fill in before the station can be built.</summary>
    public static List<string> Missing(HardwareStationSettings plan, bool isCpu)
    {
        var missing = new List<string>();
        if (plan.DeviceName.Trim().Length == 0)
        {
            missing.Add("TIA device (station) name");
        }

        if (plan.HeadOrder.Trim().Length == 0)
        {
            missing.Add("order number of the CPU / interface module");
        }

        if (plan.HeadVersion.Trim().Length == 0)
        {
            missing.Add("firmware version of the CPU / interface module");
        }

        if (plan.Ip.Trim().Length > 0 && !IsIpv4(plan.Ip.Trim()))
        {
            missing.Add($"a valid IP address (not '{plan.Ip}')");
        }
        else if (plan.Ip.Trim().Length == 0)
        {
            missing.Add("IP address");
        }

        if (plan.PnDeviceName.Trim().Length == 0)
        {
            missing.Add("PROFINET device name");
        }

        if (!isCpu && plan.IoControllerDevice.Trim().Length == 0)
        {
            missing.Add("TIA device of the IO controller (CPU)");
        }

        if (!isCpu && plan.PnDeviceNumber is < 101 or > 110)
        {
            missing.Add("PROFINET device number of the distributed station (101–110)");
        }

        if (plan.BusAdapterOrder.Trim().Length > 0 && plan.BusAdapterVersion.Trim().Length == 0)
        {
            missing.Add("firmware version of the bus adapter");
        }

        foreach (var module in plan.Modules.Where(module => module.Include))
        {
            if (module.Order.Trim().Length == 0)
            {
                missing.Add($"order number of -{module.Tag}");
            }

            if (module.Version.Trim().Length == 0)
            {
                missing.Add($"firmware version of -{module.Tag}");
            }

            if (module.Slot <= 0)
            {
                missing.Add($"slot of -{module.Tag}");
            }
        }

        foreach (var slot in plan.Modules.Where(module => module.Include).GroupBy(module => module.Slot).Where(group => group.Count() > 1))
        {
            missing.Add($"one module per slot (slot {slot.Key}: {string.Join(", ", slot.Select(module => "-" + module.Tag))})");
        }

        return missing;
    }

    /// <summary>Four numbers 0–255 separated by dots.</summary>
    public static bool IsIpv4(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length is > 0 and <= 3 && part.All(char.IsDigit) && int.Parse(part) <= 255);
    }

    /// <summary>The helper request of a completed plan.</summary>
    public static TiaHardwareBuildRequest Request(HardwareStationSettings plan, bool isCpu, bool apply) => new()
    {
        Apply = apply,
        DeviceName = plan.DeviceName.Trim(),
        Head = new TiaHardwareModuleRequest { Name = plan.HeadName.Trim(), Order = plan.HeadOrder.Trim(), Version = plan.HeadVersion.Trim(), Slot = plan.HeadSlot },
        BusAdapter = plan.BusAdapterOrder.Trim().Length == 0 ? null
            : new TiaHardwareModuleRequest { Name = "BusAdapter", Order = plan.BusAdapterOrder.Trim(), Version = plan.BusAdapterVersion.Trim(), Slot = plan.BusAdapterSlot },
        Modules = plan.Modules.Where(module => module.Include).Select(module => new TiaHardwareModuleRequest
        {
            Name = $"{module.Type} -{module.Tag}".Trim(),
            Order = module.Order.Trim(),
            Version = module.Version.Trim(),
            Slot = module.Slot,
            PotentialGroup = module.NewPotentialGroup ? "1" : "0"
        }).ToList(),
        Ip = plan.Ip.Trim(),
        PnDeviceName = plan.PnDeviceName.Trim(),
        Subnet = plan.Subnet.Trim(),
        IoSystem = isCpu ? plan.IoSystem.Trim() : "",
        IoControllerDevice = isCpu ? "" : plan.IoControllerDevice.Trim(),
        PnDeviceNumber = isCpu ? 0 : plan.PnDeviceNumber
    };

    /// <summary>Distributed stations of the same IO controller that use the same PROFINET device number.</summary>
    public static List<string> DuplicateDeviceNumbers(CwcProject project, HardwareStationSettings plan) =>
        plan.PnDeviceNumber <= 0 ? []
            : project.HardwareConfig.Stations.Where(other => other.Key != plan.Key && other.PnDeviceNumber == plan.PnDeviceNumber
                                                             && other.IoControllerDevice.Equals(plan.IoControllerDevice, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.DeviceName).ToList();

    /// <summary>
    /// Fills empty firmware versions (and the head's) from the station already in TIA: the item in the same slot with
    /// the same order number; the bus adapter in slot 127.
    /// </summary>
    public static int TakeVersions(HardwareStationSettings plan, TiaHardwareResult tia)
    {
        var filled = 0;
        string? VersionAt(int slot, string order)
        {
            var item = tia.Items.FirstOrDefault(entry => IsSlotItem(entry) && entry.Position == slot);
            if (item is null)
            {
                return null;
            }

            var (itemOrder, version) = Split(item.TypeIdentifier);
            return order.Length == 0 || Same(itemOrder, order) ? version : null;
        }

        if (plan.HeadVersion.Length == 0 && VersionAt(plan.HeadSlot, plan.HeadOrder) is { Length: > 0 } head)
        {
            plan.HeadVersion = head;
            filled++;
        }

        if (tia.Items.FirstOrDefault(entry => IsSlotItem(entry) && entry.Position == plan.BusAdapterSlot) is { } adapter
            && plan.BusAdapterOrder.Length == 0)
        {
            (plan.BusAdapterOrder, plan.BusAdapterVersion) = Split(adapter.TypeIdentifier);
            filled++;
        }

        foreach (var module in plan.Modules.Where(module => module.Version.Length == 0))
        {
            if (VersionAt(module.Slot, module.Order) is { Length: > 0 } version)
            {
                module.Version = version;
                filled++;
            }
        }

        if (tia.Interfaces.FirstOrDefault() is { } network)
        {
            if (network.Subnet.Length > 0 && plan.Subnet == "PN/IE_1")
            {
                plan.Subnet = network.Subnet;
            }

            if (network.IoSystem.Length > 0 && plan.IoSystem == "PROFINET IO-System")
            {
                plan.IoSystem = network.IoSystem;
            }
        }

        return filled;
    }

    /// <summary>A plugged module of the station (not the rack, not a sub-item): ET 200SP lists them next to the rack.</summary>
    public static bool IsSlotItem(TiaHardwareItem item) =>
        item.Depth <= 1 && item.TypeIdentifier.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase);

    /// <summary>"OrderNumber:6ES7 131-6BH01-0BA0/V0.0" → ("6ES7 131-6BH01-0BA0", "V0.0").</summary>
    public static (string Order, string Version) Split(string typeIdentifier)
    {
        var text = (typeIdentifier ?? "").Replace("OrderNumber:", "", StringComparison.OrdinalIgnoreCase);
        var slash = text.LastIndexOf('/');
        return slash < 0 ? (text.Trim(), "") : (text[..slash].Trim(), text[(slash + 1)..].Trim());
    }

    private static bool Same(string a, string b) => a.Replace(" ", "").Equals(b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}
