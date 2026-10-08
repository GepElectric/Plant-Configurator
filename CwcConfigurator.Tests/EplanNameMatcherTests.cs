using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using Xunit;

namespace CwcConfigurator.Tests;

public class EplanNameMatcherTests
{
    private readonly EplanNameMatcher _matcher = new();

    [Theory]
    [InlineData("GENERATOR CIRCUIT BREAKER OPEN", "Generator CB open")]                    // abbreviation
    [InlineData("SPEED FASTER", "Speed up")]                                                 // synonym
    [InlineData("HPU EMERGENCY SHUTDOWN VALVE B ON", "HPU ESD valve B ON (-2K32)")]       // ESD, letter identifier
    [InlineData("VOLTAGE HIGHER", "Voltage raise")]                                          // both sides to the same word
    [InlineData("CIRCUIT BREAKER OPEN (+H01)", "0AKA01 - Circuit breaker open (-Q1)")]       // KKS code, location, tags
    [InlineData("GENERATOR BEARING D.E. TEMPERATURE", "DE generator bearing temperature")]  // D.E. and word order
    [InlineData("GENERATOR VOLTAGE MEASUREMENT MCBS OPEN", "Generator voltage meas MCB open")]
    public void Exact_WhenTheWordsMeanTheSame(string schematic, string db)
    {
        Assert.Equal(EplanNameMatcher.Exact, _matcher.Similarity(schematic, "", db));
    }

    [Theory]
    [InlineData("SYNCHRONIZATION ATUOMATIC", "Synchronization automatic")]
    [InlineData("GENERATOR PTOTECTION ALARM", "Generator protection alarm")]
    [InlineData("BYPASS TORQUE SWITCH", "Bypass torque swtich")]
    public void Typos_AreLikelyNotExact(string schematic, string db)
    {
        Assert.Equal(0.95, _matcher.Similarity(schematic, "", db), 3);
    }

    [Theory]
    [InlineData("HPU PUMP 1 ON", "HPU Pump 2 ON")]
    [InlineData("HPU EMERGENCY SHUTDOWN VALVE B ON", "ESD valve A ON (-2K31)")]
    [InlineData("LUBRIFICATION UNIT FLOWMETER DE1", "Kaplan wicket gate position")]
    public void Unrelated_ScoresBelowThreshold(string schematic, string db)
    {
        Assert.True(_matcher.Similarity(schematic, "", db) < EplanIdAutomap.LikelyThreshold);
    }

    [Fact]
    public void LearnedPair_IsExact()
    {
        var matcher = new EplanNameMatcher(learned: [new EplanLearnedName { SchematicName = "NORMAL/SERVICE OPERATION", DbName = "Unit in service mode" }]);

        Assert.Equal(EplanNameMatcher.Exact, matcher.Similarity("NORMAL / SERVICE OPERATION", "", "Unit in service mode"));
        Assert.True(_matcher.Similarity("NORMAL / SERVICE OPERATION", "", "Unit in service mode") < 1);
    }

    [Fact]
    public void ProjectDictionary_AddsOwnWords()
    {
        var matcher = new EplanNameMatcher([new EplanSynonym { Words = "LOU", Means = "LUBRICATION OIL UNIT" }]);

        Assert.Equal(EplanNameMatcher.Exact, matcher.Similarity("LOU PUMP ON", "", "Lubrication oil unit pump ON"));
    }

    [Fact]
    public void Learn_RemembersOnlyLinksThatWereNotExact()
    {
        var config = new EplanImportConfig();
        var exact = new AutomapProposal { Kind = EplanModuleKind.DigitalInput, Row = new DigitalSignalConfig(), EplanSignal = "A", Proposed = new AutomapCandidate(1, "A"), Match = AutomapMatch.Exact };
        var manual = new AutomapProposal { Kind = EplanModuleKind.DigitalInput, Row = new DigitalSignalConfig(), EplanSignal = "ABC", Proposed = new AutomapCandidate(2, "BCA"), Match = AutomapMatch.Likely };

        Assert.Equal(1, EplanIdAutomap.Learn(config, [exact, manual]));
        Assert.Equal(0, EplanIdAutomap.Learn(config, [manual]));      // not twice
        var learned = Assert.Single(config.LearnedNames);
        Assert.Equal("ABC", learned.SchematicName);
        Assert.Equal("BCA", learned.DbName);
    }
}
