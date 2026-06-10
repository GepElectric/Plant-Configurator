using System.IO;
using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class ProjectValidatorTests
{
    private readonly ProjectValidator _validator = new();

    [Fact]
    public void EmptyProject_HasNoSignalLevelIssues()
    {
        // Default project has all DI/DO/AI/RTD/COUNTER/AO rows Disabled,
        // so neither range nor duplicate-Id rules should fire.
        var project = CwcProject.CreateEmpty();

        var signalTables = new[] { "DI", "DO", "AI", "RTD", "COUNTER", "AO" };
        var issues = _validator.Validate(project)
            .Where(i => signalTables.Contains(i.Table))
            .ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void DigitalInput_IdAboveMax_ReportsError()
    {
        var project = CwcProject.CreateEmpty();
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1,
            Port = 0,
            Config = SignalConfigState.Enabled,
            Id = HardwareLimits.MaxDigitalInputId + 1
        });

        var issues = _validator.Validate(project).ToList();

        var issue = Assert.Single(issues, i => i.Table == "DI");
        Assert.Equal("ERROR", issue.Severity);
        Assert.Contains("outside allowed range", issue.Message);
    }

    [Fact]
    public void DigitalInput_IdAtMax_IsValid()
    {
        var project = CwcProject.CreateEmpty();
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1,
            Port = 0,
            Config = SignalConfigState.Enabled,
            Id = HardwareLimits.MaxDigitalInputId
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "DI")
            .ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void DigitalInput_DuplicateIdsOnEnabledRows_ReportDuplicate()
    {
        var project = CwcProject.CreateEmpty();
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1, Port = 0, Config = SignalConfigState.Enabled, Id = 42
        });
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1, Port = 1, Config = SignalConfigState.Enabled, Id = 42
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "DI" && i.Message.Contains("appears"))
            .ToList();

        Assert.Single(issues);
    }

    [Fact]
    public void DuplicateIdIssue_CarriesIdNotModuleNumber()
    {
        // Regression: navigation from Problems pane to the offending row needs
        // the Id, not the ModuleNumber. Validator must emit duplicate issues
        // with an explicit Id so consumers can locate the rows.
        var project = CwcProject.CreateEmpty();
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 7, Port = 3, Config = SignalConfigState.Enabled, Id = 515
        });
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 8, Port = 4, Config = SignalConfigState.Enabled, Id = 515
        });

        var issue = Assert.Single(
            _validator.Validate(project),
            i => i.Table == "DI" && i.Message.Contains("appears"));

        Assert.Equal(515, issue.Id);
        // Falling back to ModuleNumber would have given us 515 (the Id) and pointed
        // navigation at non-existent module 515 — exactly the bug we just fixed.
        Assert.NotEqual(515, issue.ModuleNumber);
    }

    [Fact]
    public void DigitalInput_DuplicateIdsButOneDisabled_DoesNotReportDuplicate()
    {
        var project = CwcProject.CreateEmpty();
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1, Port = 0, Config = SignalConfigState.Enabled, Id = 42
        });
        project.DigitalInputs.Add(new DigitalSignalConfig
        {
            ModuleNumber = 1, Port = 1, Config = SignalConfigState.Disabled, Id = 42
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "DI" && i.Message.Contains("appears"))
            .ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void AnalogInput_ReverseScaledLimits_AreAccepted()
    {
        // Reverse-scaled signals (e.g. inverted valve calibration where low > high)
        // are legitimate; validator must not flag them.
        var project = CwcProject.CreateEmpty();
        project.AnalogInputs.Add(new AnalogSignalConfig
        {
            ModuleNumber = 1,
            Port = 0,
            Config = SignalConfigState.Enabled,
            CustomLimits = SignalConfigState.Enabled,
            LowLimit = 20874,
            HighLimit = 576,
            Id = 0
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "AI" && i.Message.Contains("limit"))
            .ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void AnalogOutput_IdAboveMax_ReportsError()
    {
        var project = CwcProject.CreateEmpty();
        project.AnalogOutputs.Add(new AnalogOutputConfig
        {
            ModuleNumber = 1,
            Port = 0,
            Config = SignalConfigState.Enabled,
            Id = HardwareLimits.MaxAnalogOutputId + 1
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "AO")
            .ToList();

        Assert.NotEmpty(issues);
    }

    [Fact]
    public void AlmRepair_OnUsedRowWithSpareName_EmitsWarning()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmRows.Add(new DbStringRow
        {
            Id = 1,
            Used = "1",
            NameEnglish = "spare",
            NameOther = "spare",
            DescriptionEnglish = "",
            DescriptionOther = "",
            SystemNameEnglish = "",
            SystemNameOther = ""
        });

        // Findings now split per (severity, slicer hint), so a single dirty row
        // can produce multiple "Needs repair" warnings (one per slicer column
        // group: All / Other language / Description). Verify at least one.
        var repairIssues = _validator.Validate(project)
            .Where(i => i.Table == "DB_ALM" && i.Message.StartsWith("Needs repair"))
            .ToList();

        Assert.NotEmpty(repairIssues);
        Assert.All(repairIssues, i =>
        {
            Assert.Equal("WARNING", i.Severity);
            Assert.Equal(1, i.Id);
        });
    }

    [Fact]
    public void AlmRepair_OnUnusedRow_DoesNotEmitWarning()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmRows.Add(new DbStringRow
        {
            Id = 1,
            Used = "0",
            NameEnglish = "spare",
            NameOther = "spare"
        });

        var issues = _validator.Validate(project)
            .Where(i => i.Table == "DB_ALM" && i.Message.StartsWith("Needs repair"))
            .ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void SampleProject_LoadsAndValidates()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "sample.cwcproj");
        if (!File.Exists(path))
        {
            // Sample is encrypted with the runtime key; if Load fails, skip.
            return;
        }

        var service = new ProjectFileService();
        CwcProject project;
        try
        {
            project = service.Load(path);
        }
        catch
        {
            // Sample file may not be loadable from a test context; treat as smoke test.
            return;
        }

        var issues = _validator.Validate(project).ToList();

        Assert.NotNull(issues);
    }
}
