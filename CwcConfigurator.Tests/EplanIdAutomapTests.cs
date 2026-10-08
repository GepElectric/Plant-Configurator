using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using Xunit;

namespace CwcConfigurator.Tests;

public class EplanIdAutomapTests
{
    private static PlcDatabase Database()
    {
        var db = PlcDatabase.CreateDefault("Unit 1");
        db.DbCatalog.DiRows =
        [
            new DbStringRow { Id = 1, NameEnglish = "Local control active (-S103)" },
            new DbStringRow { Id = 2, NameEnglish = "Step-up transformer Buchholz alarm" },
            new DbStringRow { Id = 3, NameEnglish = "Circuit breaker spring charged (+H02)" },
            new DbStringRow { Id = 4, NameEnglish = "Generator heaters on" }
        ];
        db.DbCatalog.MeasRows = [new DbStringRow { Id = 7, NameEnglish = "HPU system pressure" }];
        return db;
    }

    private static DigitalSignalConfig Di(PlcDatabase db, int module, int port, string signal, string connected = "")
    {
        var row = db.DigitalInputs.Single(r => r.ModuleNumber == module && r.Port == port);
        row.EplanSignalName = signal;
        row.EplanConnectedTo = connected;
        return row;
    }

    [Fact]
    public void Propose_ExactNameIgnoringCaseAndPunctuation()
    {
        var db = Database();
        Di(db, 0, 0, "STEP-UP TRANSFORMER BUCHHOLZ ALARM");

        var p = EplanIdAutomap.Propose(db).Single();

        Assert.Equal(2, p.Proposed!.Id);
        Assert.Equal(AutomapMatch.Exact, p.Match);
    }

    [Fact]
    public void Propose_LikelyWhenWordsOverlap()
    {
        var db = Database();
        Di(db, 0, 1, "CIRCUIT BREAKER SPRING CHARGED (+H02)");
        Di(db, 0, 2, "LOCAL CONTROL", "-S103:13");          // device tag of the DB name is the connected device

        var proposals = EplanIdAutomap.Propose(db);

        var cb = proposals.Single(p => p.Port == 1);
        Assert.Equal(3, cb.Proposed!.Id);
        var local = proposals.Single(p => p.Port == 2);
        Assert.Equal(1, local.Proposed!.Id);
        Assert.Equal(AutomapMatch.Likely, local.Match);
    }

    [Theory]
    [InlineData("ESD RESET", "ESD RESET (-2K02)", 1.0)]               // device tag in brackets does not count
    [InlineData("HPU PUMP 1 ON", "HPU Pump 2 ON (-2K25)", 0.0)]       // different numbers never match
    public void Similarity_BracketsAndNumbers(string eplan, string db, double expected)
    {
        Assert.Equal(expected, EplanIdAutomap.Similarity(eplan, "", db), 3);
    }

    [Fact]
    public void Propose_NothingForSpareOrUnknownSignals()
    {
        var db = Database();
        Di(db, 0, 3, "SPARE");
        Di(db, 0, 4, "BATTERY CONNECTED");

        var proposals = EplanIdAutomap.Propose(db);

        Assert.All(proposals, p => Assert.Null(p.Proposed));
        Assert.Equal("spare in the schematic", proposals.Single(p => p.Port == 3).Note);
    }

    [Fact]
    public void Propose_OneSignalGoesToOneChannelOnly()
    {
        var db = Database();
        Di(db, 0, 5, "GENERATOR HEATERS ON");
        Di(db, 1, 5, "GENERATOR HEATERS ON");

        var proposals = EplanIdAutomap.Propose(db).OrderBy(p => p.ModuleNumber).ToList();

        Assert.Equal(4, proposals[0].Proposed!.Id);
        Assert.Equal(AutomapMatch.Likely, proposals[0].Match);   // same name twice -> must be checked
        Assert.Null(proposals[1].Proposed);
    }

    [Fact]
    public void Propose_AnalogInputsUseDbMeas()
    {
        var db = Database();
        var ai = db.AnalogInputs.Single(r => r.ModuleNumber == 0 && r.Port == 0);
        ai.EplanSignalName = "HPU SYSTEM PRESSURE";

        var p = EplanIdAutomap.Propose(db).Single();

        Assert.Equal(EplanModuleKind.AnalogInput, p.Kind);
        Assert.Equal(7, p.Proposed!.Id);
    }

    [Fact]
    public void Apply_WritesOnlyHardwareIds()
    {
        var db = Database();
        var row = Di(db, 0, 0, "STEP-UP TRANSFORMER BUCHHOLZ ALARM");
        var dbRowsBefore = db.DbCatalog.DiRows.Select(r => (r.Id, r.NameEnglish, r.ModuleName, r.ModulePort)).ToList();

        var changed = EplanIdAutomap.Apply(EplanIdAutomap.Propose(db));

        Assert.Equal(1, changed);
        Assert.Equal(2, row.Id);
        Assert.Equal(dbRowsBefore, db.DbCatalog.DiRows.Select(r => (r.Id, r.NameEnglish, r.ModuleName, r.ModulePort)).ToList());
    }
}
