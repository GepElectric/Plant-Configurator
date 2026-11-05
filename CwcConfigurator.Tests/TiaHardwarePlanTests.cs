using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.TiaSync;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class TiaHardwarePlanTests
{
    private static EplanStation Station() => new()
    {
        Function = "0LPY01",
        HeadDeviceTag = "A601",
        HeadPartNumber = "6ES7512-1DK01-0AB0",
        HeadType = "CPU 1512SP-1 PN",
        IsCpu = true,
        Modules =
        [
            new EplanModule { DeviceTag = "A611", PartNumber = "6ES7131-6BH01-0BA0", Type = "DI 16x24VDC ST" },
            new EplanModule { DeviceTag = "A612", PartNumber = "6ES7131-6BH01-0BA0+BU15-P16+A0+2B", Type = "DI 16x24VDC ST" },
            new EplanModule { DeviceTag = "A631", PartNumber = "6ES7132-6BH01-0BA0+BU15-P16+A0+2D", Type = "DQ 16x24VDC ST" }
        ]
    };

    private static CwcProject Project()
    {
        var project = new CwcProject();
        var intake = new PlcDatabase { Name = "Intake", IpAddress = "10.26.7.13" };
        project.PlcDatabases.Add(intake);
        project.EplanImport.Assignments.Add(new EplanCpuAssignment { CpuFunction = "0LPY01", PlcDatabaseId = intake.Id });
        project.TiaSync.PlcBindings.Add(new TiaPlcBinding { PlcDatabaseId = intake.Id, DeviceName = "ET 200SP station_4", SoftwareName = "Intake" });
        return project;
    }

    [Fact]
    public void Plan_TakesEplanAndProject_AndAsksForWhatIsMissing()
    {
        var project = Project();
        var plan = TiaHardwarePlanner.PlanFor(project, Station());

        Assert.Equal(("ET 200SP station_4", "Intake", "10.26.7.13", "intake"), (plan.DeviceName, plan.HeadName, plan.Ip, plan.PnDeviceName));
        Assert.Equal("6ES7 512-1DK01-0AB0", plan.HeadOrder);
        Assert.Equal([2, 3, 4], plan.Modules.Select(module => module.Slot));
        Assert.Equal("6ES7 132-6BH01-0BA0", plan.Modules[2].Order);                     // without the BaseUnit
        Assert.Equal([true, false, true], plan.Modules.Select(module => module.NewPotentialGroup)); // first module, "+2D"

        var missing = TiaHardwarePlanner.Missing(plan, isCpu: true);
        Assert.Contains("firmware version of the CPU / interface module", missing);
        Assert.Contains("firmware version of -A611", missing);

        // Versions read from the station in TIA (same slot and order number).
        var tia = new TiaHardwareResult
        {
            Found = true,
            Items =
            [
                new TiaHardwareItem { Depth = 1, Position = 1, TypeIdentifier = "OrderNumber:6ES7 512-1DM03-0AB0/V3.0" }, // another CPU: not taken
                new TiaHardwareItem { Depth = 1, Position = 127, TypeIdentifier = "OrderNumber:6ES7 193-6AR00-0AA0/V0.0" },
                new TiaHardwareItem { Depth = 1, Position = 2, TypeIdentifier = "OrderNumber:6ES7 131-6BH01-0BA0/V0.0" },
                new TiaHardwareItem { Depth = 1, Position = 3, TypeIdentifier = "OrderNumber:6ES7 131-6BH01-0BA0/V0.0" },
                new TiaHardwareItem { Depth = 1, Position = 4, TypeIdentifier = "OrderNumber:6ES7 132-6BH01-0BA0/V0.0" }
            ],
            Interfaces = [new TiaHardwareInterface { Subnet = "PN/IE_3", IoSystem = "PROFINET IO-System" }]
        };
        TiaHardwarePlanner.TakeVersions(plan, tia);
        Assert.Equal("", plan.HeadVersion);
        Assert.Equal(("6ES7 193-6AR00-0AA0", "V0.0"), (plan.BusAdapterOrder, plan.BusAdapterVersion));
        Assert.All(plan.Modules, module => Assert.Equal("V0.0", module.Version));
        Assert.Equal("PN/IE_3", plan.Subnet);
        Assert.Equal(["firmware version of the CPU / interface module"], TiaHardwarePlanner.Missing(plan, true));

        plan.HeadVersion = "V3.0";
        Assert.Empty(TiaHardwarePlanner.Missing(plan, true));
        var request = TiaHardwarePlanner.Request(plan, isCpu: true, apply: false);
        Assert.Equal(("6ES7 512-1DK01-0AB0", "V3.0", 1), (request.Head.Order, request.Head.Version, request.Head.Slot));
        Assert.Equal(["1", "0", "1"], request.Modules.Select(module => module.PotentialGroup));
        Assert.Equal("PROFINET IO-System", request.IoSystem);

        // Kept in the project: the next time the filled values are there.
        TiaHardwarePlanner.Save(project, plan);
        Assert.Equal("V3.0", TiaHardwarePlanner.PlanFor(project, Station()).HeadVersion);
    }

    [Fact]
    public void Missing_ReportsBadIpAndTwoModulesInOneSlot()
    {
        var plan = TiaHardwarePlanner.PlanFor(Project(), Station());
        plan.Ip = "10.26.7";
        plan.Modules[1].Slot = 2;
        var missing = TiaHardwarePlanner.Missing(plan, true);
        Assert.Contains(missing, text => text.StartsWith("a valid IP address"));
        Assert.Contains(missing, text => text.StartsWith("one module per slot (slot 2"));
    }

    [Fact]
    public void DistributedStation_NeedsItsOwnProfinetDeviceNumber101To110()
    {
        var project = Project();
        var station = Station();
        var im = new EplanStation { Function = "0LPY02", HeadDeviceTag = "A700", HeadPartNumber = "6ES7155-6AU01-0BN0", HeadType = "IM 155-6 PN ST", IsCpu = false, Modules = station.Modules };
        var plan = TiaHardwarePlanner.PlanFor(project, im);
        plan.IoControllerDevice = "ET 200SP station_4";
        plan.HeadVersion = "V4.2";
        plan.Ip = "10.26.7.21";
        plan.Modules.ForEach(module => module.Version = "V0.0");
        Assert.Contains("PROFINET device number of the distributed station (101–110)", TiaHardwarePlanner.Missing(plan, isCpu: false));

        plan.PnDeviceNumber = 101;
        Assert.Empty(TiaHardwarePlanner.Missing(plan, isCpu: false));
        Assert.Equal(101, TiaHardwarePlanner.Request(plan, isCpu: false, apply: false).PnDeviceNumber);

        TiaHardwarePlanner.Save(project, new HardwareStationSettings { Key = "other", DeviceName = "Station A800", IoControllerDevice = "ET 200SP station_4", PnDeviceNumber = 101 });
        Assert.Equal(["Station A800"], TiaHardwarePlanner.DuplicateDeviceNumbers(project, plan));
    }

    [Fact]
    public void ChannelTemplates_LearnedFromTiaAndSentPerChannel()
    {
        Dictionary<string, string> Channel(int number, string range) => new()
        {
            ["#Number"] = number.ToString(), ["#Type"] = "Analog", ["#IoType"] = "Input",
            ["OperatingType"] = "3", ["OperatingRange"] = range, ["Smoothing"] = "1", ["ChannelAddress"] = "!ro"
        };
        var tia = new TiaHardwareResult
        {
            Found = true,
            Items =
            [
                new TiaHardwareItem { Name = "AI 4xI 2-/4-wire ST_1", TypeIdentifier = "OrderNumber:6ES7 134-6GD01-0BA1/V1.0", Position = 3, Depth = 1 },
                new TiaHardwareItem { Name = "AI 4xI 2-/4-wire ST_1", Depth = 2, Channels = [Channel(0, "8"), Channel(1, "8"), Channel(2, "9")] },
                new TiaHardwareItem { Name = "DI 16x24VDC ST_1", Depth = 2, Channels = [new() { ["#Number"] = "0", ["#Type"] = "Digital", ["#IoType"] = "Input", ["InputDelay"] = "4" }] }
            ]
        };

        var templates = new List<ChannelTemplate>();
        var added = TiaHardwarePlanner.LearnTemplates(tia, templates);
        Assert.Equal(2, added.Count);                                         // two different ranges; digital ignored
        Assert.All(added, template => Assert.Equal("Input", template.IoType));
        Assert.DoesNotContain("ChannelAddress", added[0].Attributes.Keys);  // identity / read-only skipped
        Assert.Empty(TiaHardwarePlanner.LearnTemplates(tia, templates));      // known ones are not repeated

        added[0].Name = "4-20 mA 2-wire";
        var plan = new HardwareStationSettings
        {
            DeviceName = "S1",
            Modules =
            [
                new HardwareModuleSettings
                {
                    Tag = "A111", Type = "AI 4xI", Order = "6ES7 134-6GD01-0BA1", Slot = 3, Include = true,
                    Channels = [new() { Number = 0, Template = "4-20 mA 2-wire" }, new() { Number = 1 }, new() { Number = 2, Template = "unknown" }]
                }
            ]
        };
        var request = TiaHardwarePlanner.Request(plan, true, false, templates);
        var channel = Assert.Single(request.Modules[0].Channels);
        Assert.Equal(0, channel.Number);
        Assert.Equal("8", channel.Attributes["OperatingRange"]);
    }
}
