using CwcConfigurator.Domain;
using CwcConfigurator.Services.TiaSync;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class PlcIpAddressTests
{
    [Fact]
    public void Attach_StoresTheIpOfTheTiaHardwareInTheMappedDatabase()
    {
        var project = new CwcProject();
        var intake = new PlcDatabase { Name = "Intake" };
        var unit = new PlcDatabase { Name = "Unit 1", IpAddress = "10.26.7.11" };
        project.PlcDatabases.AddRange([intake, unit]);
        project.TiaSync.PlcBindings.Add(new TiaPlcBinding { PlcDatabaseId = intake.Id, DeviceName = "ET 200SP station_4", SoftwareName = "Intake" });
        project.TiaSync.PlcBindings.Add(new TiaPlcBinding { PlcDatabaseId = unit.Id, DeviceName = "ET 200SP station_5", SoftwareName = "Unit" });
        var session = new TiaAttachedSession
        {
            Plcs =
            [
                new TiaDiscoveredPlc { DeviceName = "ET 200SP station_4", SoftwareName = "Intake", IpAddresses = ["X2|192.168.0.1", "PROFINET interface_1|10.26.7.13"] },
                new TiaDiscoveredPlc { DeviceName = "ET 200SP station_5", SoftwareName = "Unit", IpAddresses = ["PROFINET interface_1|10.26.7.11"] }
            ]
        };

        var changed = new TiaMappingService().StoreIpAddresses(project, session);

        Assert.Equal(["Intake: 10.26.7.13"], changed);                   // the PROFINET interface, the unit already had it
        Assert.Equal("10.26.7.13", intake.IpAddress);
        Assert.Equal(2, intake.IpAddresses.Count);
        Assert.Equal("10.26.7.11", unit.IpAddress);
    }
}
