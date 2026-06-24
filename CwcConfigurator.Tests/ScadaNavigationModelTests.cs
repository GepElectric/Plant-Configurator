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
    public void FromDatabase_ReadsButtonAndCards()
    {
        var db = MakeDb(
            ("MAIN_NAV_BUTTONS_ENTRY_1", "Intake"),
            ("MAIN_NAV_BTTN_1_TEXT_LANGUAGE_1", "Intake"),
            ("MAIN_NAV_BTTN_1_TEXT_LANGUAGE_2", "Fellesanlegg"),
            ("MAIN_NAV_BTTN_1_GRAPHIC_LIST_ID", "1"),
            ("MAIN_NAV_SCREEN_SHAPE_1", "2"),
            ("SIDE_CONTENT_DEFAULT_SCREENS_ENTRY_1", "SIDE_CONTENT_Button_1"),
            ("SCREENS_Button_1_ENTRY_1", "Intake_Control"),
            ("Button_1_TOP_NAV_CARD_1_TEXT_LANGUAGE_1", "Control"),
            ("Button_1_TOP_NAV_CARD_1_TEXT_LANGUAGE_2", "Energi"),
            ("Button_1_TOP_NAV_CARD_1_GRAPHIC_LIST_ID", "19"),
            ("SCREENS_Button_1_ENTRY_2", "Intake_HPU"),
            ("SCREENS_Button_1_ENTRY_3", "")); // empty -> not a card

        var model = ScadaNavigationModel.FromDatabase(db);
        var b1 = model.Buttons.Single(b => b.Index == 1);

        Assert.Equal("Intake", b1.Identity);
        Assert.Equal("Intake", b1.LabelEn);
        Assert.Equal("Fellesanlegg", b1.LabelOther);
        Assert.Equal("1", b1.IconId);
        Assert.Equal(2, b1.LayoutShape);
        Assert.Equal("SIDE_CONTENT_Button_1", b1.SideContentScreen);
        Assert.Equal(2, b1.Cards.Count);
        Assert.Equal("Intake_Control", b1.Cards[0].Screen);
        Assert.Equal("Control", b1.Cards[0].LabelEn);
        Assert.Equal("19", b1.Cards[0].IconId);
        Assert.Equal("Intake_HPU", b1.Cards[1].Screen);
    }

    [Fact]
    public void ApplyTo_PacksCardsAndClearsTrailing()
    {
        var db = ScadaDatabase.CreateDefault();
        var model = new ScadaNavigationModel();
        for (var i = 1; i <= ScadaNavigationModel.MaxButtons; i++)
        {
            model.Buttons.Add(new ScadaNavButton { Index = i });
        }

        var b1 = model.Buttons.Single(b => b.Index == 1);
        b1.Identity = "Intake";
        b1.LabelEn = "Intake";
        b1.IconId = "1";
        b1.LayoutShape = 3;
        b1.Cards.Add(new ScadaTopNavCard { Screen = "A", LabelEn = "Ca", IconId = "7" });
        b1.Cards.Add(new ScadaTopNavCard { Screen = "", LabelEn = "gap" });    // dropped (no screen)
        b1.Cards.Add(new ScadaTopNavCard { Screen = "B", LabelEn = "Cb", IconId = "8" });

        model.ApplyTo(db);

        Assert.Equal("Intake", Value(db, "MAIN_NAV_BUTTONS_ENTRY_1"));
        Assert.Equal("1", Value(db, "MAIN_NAV_BTTN_1_GRAPHIC_LIST_ID"));
        Assert.Equal("3", Value(db, "MAIN_NAV_SCREEN_SHAPE_1"));
        Assert.Equal("A", Value(db, "SCREENS_Button_1_ENTRY_1"));
        Assert.Equal("Ca", Value(db, "Button_1_TOP_NAV_CARD_1_TEXT_LANGUAGE_1"));
        Assert.Equal("7", Value(db, "Button_1_TOP_NAV_CARD_1_GRAPHIC_LIST_ID"));
        Assert.Equal("B", Value(db, "SCREENS_Button_1_ENTRY_2")); // packed
        Assert.Equal("", Value(db, "SCREENS_Button_1_ENTRY_3"));  // cleared
        Assert.Equal("0", Value(db, "Button_1_TOP_NAV_CARD_3_GRAPHIC_LIST_ID")); // cleared icon
    }

    [Fact]
    public void RoundTrips_ThroughDatabase()
    {
        var source = MakeDb(
            ("MAIN_NAV_BUTTONS_ENTRY_2", "Trends"),
            ("MAIN_NAV_BTTN_2_TEXT_LANGUAGE_1", "Trends"),
            ("MAIN_NAV_BTTN_2_GRAPHIC_LIST_ID", "2"),
            ("MAIN_NAV_SCREEN_SHAPE_2", "1"),
            ("SCREENS_Button_2_ENTRY_1", "Trends"),
            ("Button_2_TOP_NAV_CARD_1_TEXT_LANGUAGE_1", "Trend"),
            ("Button_2_TOP_NAV_CARD_1_GRAPHIC_LIST_ID", "5"));

        var model = ScadaNavigationModel.FromDatabase(source);
        var target = ScadaDatabase.CreateDefault();
        model.ApplyTo(target);
        var again = ScadaNavigationModel.FromDatabase(target);

        var b2 = again.Buttons.Single(b => b.Index == 2);
        Assert.Equal("Trends", b2.Identity);
        Assert.Equal("Trends", b2.LabelEn);
        Assert.Equal("2", b2.IconId);
        Assert.Equal(1, b2.LayoutShape);
        Assert.Single(b2.Cards);
        Assert.Equal("Trends", b2.Cards[0].Screen);
        Assert.Equal("Trend", b2.Cards[0].LabelEn);
        Assert.Equal("5", b2.Cards[0].IconId);
    }
}
