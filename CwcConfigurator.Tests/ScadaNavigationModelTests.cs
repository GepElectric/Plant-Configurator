using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class ScadaNavigationModelTests
{
    private static ScadaDatabase MakeDb(params (string Name, string Value)[] vars)
    {
        var db = ScadaDatabase.CreateDefault();
        var id = 1;
        foreach (var (name, value) in vars)
        {
            db.Variables.Add(new ScadaConfigVariable { Id = id++, Name = name, Value = value, Type = "STRING", Section = "X" });
        }

        return db;
    }

    private static string Value(ScadaDatabase db, string name) =>
        db.Variables.First(v => v.Name == name).Value;

    [Fact]
    public void FromDatabase_ReadsButtonsAndScreens()
    {
        var db = MakeDb(
            ("MAIN_NAV_BUTTONS_ENTRY_1", "Intake"),
            ("SCREENS_Button_1_ENTRY_1", "Intake_Control"),
            ("SCREENS_Button_1_ENTRY_2", "Intake_HPU"),
            ("SCREENS_Button_1_ENTRY_3", ""),
            ("MAIN_NAV_BUTTONS_ENTRY_2", "Trends"),
            ("SCREENS_Button_2_ENTRY_1", "Trends"));

        var model = ScadaNavigationModel.FromDatabase(db);

        Assert.Equal(ScadaNavigationModel.MaxButtons, model.Buttons.Count);
        var b1 = model.Buttons.Single(b => b.Index == 1);
        Assert.Equal("Intake", b1.Name);
        Assert.Equal(new[] { "Intake_Control", "Intake_HPU" }, b1.Screens); // empty slot skipped
        var b2 = model.Buttons.Single(b => b.Index == 2);
        Assert.Equal("Trends", b2.Name);
        Assert.Single(b2.Screens);
        Assert.Equal("", model.Buttons.Single(b => b.Index == 3).Name);
    }

    [Fact]
    public void ApplyTo_PacksScreensAndClearsTrailing()
    {
        var db = ScadaDatabase.CreateDefault();
        var model = new ScadaNavigationModel();
        for (var i = 1; i <= ScadaNavigationModel.MaxButtons; i++)
        {
            model.Buttons.Add(new ScadaNavButton { Index = i });
        }

        var b1 = model.Buttons.Single(b => b.Index == 1);
        b1.Name = "Intake";
        b1.Screens.Add("A");
        b1.Screens.Add("");   // gap that must be removed
        b1.Screens.Add("B");

        model.ApplyTo(db);

        Assert.Equal("Intake", Value(db, "MAIN_NAV_BUTTONS_ENTRY_1"));
        Assert.Equal("A", Value(db, "SCREENS_Button_1_ENTRY_1"));
        Assert.Equal("B", Value(db, "SCREENS_Button_1_ENTRY_2")); // packed
        Assert.Equal("", Value(db, "SCREENS_Button_1_ENTRY_3"));  // cleared
        Assert.Equal("", Value(db, "MAIN_NAV_BUTTONS_ENTRY_2"));
    }

    [Fact]
    public void FromDatabase_Then_ApplyTo_RoundTrips()
    {
        var source = MakeDb(
            ("MAIN_NAV_BUTTONS_ENTRY_1", "Intake"),
            ("SCREENS_Button_1_ENTRY_1", "Intake_Control"),
            ("SCREENS_Button_1_ENTRY_2", "Intake_HPU"),
            ("SCREENS_Button_1_ENTRY_3", "Intake_PLC"),
            ("MAIN_NAV_BUTTONS_ENTRY_3", "Alarms"),
            ("SCREENS_Button_3_ENTRY_1", "Alarms_Intake"));

        var model = ScadaNavigationModel.FromDatabase(source);

        var target = ScadaDatabase.CreateDefault();
        model.ApplyTo(target);
        var roundTripped = ScadaNavigationModel.FromDatabase(target);

        var b1 = roundTripped.Buttons.Single(b => b.Index == 1);
        Assert.Equal("Intake", b1.Name);
        Assert.Equal(new[] { "Intake_Control", "Intake_HPU", "Intake_PLC" }, b1.Screens);
        var b3 = roundTripped.Buttons.Single(b => b.Index == 3);
        Assert.Equal("Alarms", b3.Name);
        Assert.Equal(new[] { "Alarms_Intake" }, b3.Screens);
    }
}
