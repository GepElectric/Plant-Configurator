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
}
