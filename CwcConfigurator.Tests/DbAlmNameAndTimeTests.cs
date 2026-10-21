using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class DbAlmNameAndTimeTests
{
    [Theory]
    [InlineData("MV_CONTROL_MCB_FAULT", true)]
    [InlineData("_6_3_MV_CONTROL_MCB_FAULT", true)]
    [InlineData("", true)]
    [InlineData("6_3_MV_CONTROL_MCB_FAULT", false)]
    [InlineData("-FAULT", false)]
    [InlineData("MV FAULT", false)]
    public void PlcIdentifier_MustStartWithLetterOrUnderscore(string name, bool valid)
    {
        Assert.Equal(valid, ProjectValidator.IsValidPlcIdentifier(name));
    }

    [Fact]
    public void AlarmWithDigitLeadingPlcName_IsErrorThatBlocksPush()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmRows.Add(new DbStringRow { Id = 490, PlcName = "6_3_MV_CONTROL_MCB_FAULT" });

        var issues = new ProjectValidator().Validate(project)
            .Where(issue => issue.Table == "DB_ALM" && issue.Message.Contains("6_3_MV_CONTROL_MCB_FAULT"))
            .ToList();

        Assert.Single(issues);
        Assert.Equal("ERROR", issues[0].Severity);
    }

    [Theory]
    [InlineData("T#8S_500MS", "8500", "0")]
    [InlineData("T#1M_10S", "70000", "0")]
    [InlineData("T#1H_2M_3S_4MS", "3723004", "0")]
    [InlineData("T#500MS", "500", "0")]
    [InlineData("T#10S", "10", "1")]
    [InlineData("T#5M", "5", "2")]
    [InlineData("T#2H", "2", "3")]
    [InlineData("T#0MS", "0", "0")]
    public void SplitTime_CompoundValuesBecomeMilliseconds(string value, string delay, string timeBase)
    {
        TiaPortalExportImporter.SplitTime(value, out var actualDelay, out var actualTimeBase);

        Assert.Equal(delay, actualDelay);
        Assert.Equal(timeBase, actualTimeBase);
    }
}
