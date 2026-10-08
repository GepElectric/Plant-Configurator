using CwcConfigurator.Domain;
using CwcConfigurator.Services.AlarmTesting;
using Xunit;

namespace CwcConfigurator.Tests;

public class AlarmTestPlanBuilderTests
{
    [Fact]
    public void Build_UsedDigitalAlarm_CreatesInputTestPointWithExpectedAlarm()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmSystemNameRows.Add(new DbDiSystemNameRow
        {
            SystemNumber = 0,
            SystemPlcShortName = "INTAKE"
        });
        project.DbCatalog.AlarmRows.Add(new DbStringRow
        {
            Id = 17,
            PlcStructure = "INTAKE",
            PlcName = "GATE_FAIL",
            Used = "1",
            SourceType = "1",
            SourceDigId = "42"
        });
        project.DbCatalog.DiRows.Add(new DbStringRow
        {
            Id = 42,
            PlcName = "GATE_CLOSED"
        });
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            Id = 42,
            ModuleNumber = 3,
            Port = 7,
            Config = SignalConfigState.Enabled
        });

        var plan = new AlarmTestPlanBuilder().Build(project);

        var testCase = Assert.Single(plan.Cases);
        Assert.Equal(AlarmTestStatus.Ready, testCase.Status);
        Assert.Equal(AlarmTestSourceKind.DigitalInput, testCase.SourceKind);
        Assert.Equal("DI:42:3:7", testCase.TestPointId);
        Assert.Equal("SIMULATOR.DI.MODUL[3].CH[7]", testCase.SimulatorPath);
        Assert.Equal("DB30.DBX6.7", testCase.SimulatorAddressLabel);
        Assert.Equal("TRUE", testCase.TriggerValue);
        Assert.Equal("FALSE", testCase.ResetValue);

        var expectedAlarm = Assert.Single(testCase.ExpectedAlarms);
        Assert.Equal(17, expectedAlarm.AlarmId);
        Assert.Equal("ALM.INTAKE.GATE_FAIL.ALM", expectedAlarm.AlarmBoolPath);
        Assert.Equal("DB5.DBX410.4", expectedAlarm.AlarmBoolAddressLabel);
    }

    [Fact]
    public void Build_UnusedAlarm_IsExcluded()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmRows.Add(new DbStringRow
        {
            Id = 1,
            PlcStructure = "INTAKE",
            PlcName = "UNUSED",
            Used = "0",
            SourceDigId = "1"
        });

        var plan = new AlarmTestPlanBuilder().Build(project);

        Assert.Empty(plan.Cases);
    }
}
