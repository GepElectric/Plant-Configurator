using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using CwcConfigurator.Services.Validation;
using Xunit;

namespace CwcConfigurator.Tests;

public class RepairEvaluatorTests
{
    private static DbStringRow UsedDiRow(Action<DbStringRow>? configure = null)
    {
        // DB DI uses TrueText, not Used, for the "USED" flag — same as the grid binding.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            SystemNameEnglish = "DI_SYS_ENG",
            SystemNameOther = "DI_SYS_OTHER",
            NameEnglish = "Door open",
            NameOther = "Vrata otvorena",
            ColorValue0 = "3 - RED",
            ColorValue1 = "4 - GREEN"
        };
        configure?.Invoke(row);
        return row;
    }

    [Fact]
    public void Di_UsedRowFullyPopulated_NoFindings()
    {
        RepairContext.IsMultilingual = true;
        Assert.Empty(DiRepairEvaluator.Evaluate(UsedDiRow()));
    }

    [Fact]
    public void Di_Monolingual_SkipsOtherLanguageFindings()
    {
        // Project flagged as monolingual — *Other fields are intentionally blank;
        // evaluator must not warn about them.
        var row = UsedDiRow(r =>
        {
            r.NameOther = "";
            r.SystemNameOther = "";
        });

        RepairContext.IsMultilingual = false;
        try
        {
            var findings = DiRepairEvaluator.Evaluate(row).ToList();
            Assert.DoesNotContain(findings, f => f.Reason.Contains("OTHER"));
        }
        finally
        {
            RepairContext.IsMultilingual = true;
        }
    }

    [Fact]
    public void Di_Multilingual_WarnsOnMissingOtherLanguage()
    {
        var row = UsedDiRow(r =>
        {
            r.NameOther = "";
            r.SystemNameOther = "";
        });

        RepairContext.IsMultilingual = true;
        var findings = DiRepairEvaluator.Evaluate(row).ToList();
        Assert.Contains(findings, f => f.Reason.Contains("Name OTHER"));
        Assert.Contains(findings, f => f.Reason.Contains("System name OTHER"));
    }

    [Fact]
    public void Di_UnusedRowWithSpareNames_StillNoFindings()
    {
        var row = UsedDiRow(r =>
        {
            r.TrueText = "";
            r.NameEnglish = "spare";
            r.SystemNameEnglish = "";
            r.ColorValue0 = "0 - NO COLOR";
        });

        Assert.Empty(DiRepairEvaluator.Evaluate(row));
    }

    [Fact]
    public void Di_UsedFlagComesFromTrueText_NotUsedField()
    {
        // Regression: DB DI grid binds the USED cell to TrueText.
        // A row with Used="" but TrueText="TRUE" MUST still be evaluated.
        var row = new DbStringRow
        {
            Used = "",            // grid never writes here
            TrueText = "TRUE",    // what the user actually toggled
            NameEnglish = "spare",
            ColorValue0 = "0 - NO COLOR",
            ColorValue1 = ""
        };

        var findings = DiRepairEvaluator.Evaluate(row).ToList();
        Assert.NotEmpty(findings);
    }

    [Fact]
    public void Di_UsedRowWithNoColors_TwoWarnings()
    {
        var row = UsedDiRow(r =>
        {
            r.ColorValue0 = "0 - NO COLOR";
            r.ColorValue1 = "";
        });

        var findings = DiRepairEvaluator.Evaluate(row).ToList();
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(IssueSeverity.Warning, f.Severity));
    }

    [Fact]
    public void Di_UsedRowWithSameColorBothSides_EmitsEqualWarning()
    {
        var row = UsedDiRow(r =>
        {
            r.ColorValue0 = "3 - RED";
            r.ColorValue1 = "3 - RED";
        });

        var findings = DiRepairEvaluator.Evaluate(row).ToList();
        Assert.Single(findings, f => f.Reason.Contains("Color 0 == Color 1"));
    }

    [Fact]
    public void Di_BothColorsNoColor_DoesNotDoubleFireEqualWarning()
    {
        var row = UsedDiRow(r =>
        {
            r.ColorValue0 = "0 - NO COLOR";
            r.ColorValue1 = "0 - NO COLOR";
        });

        var findings = DiRepairEvaluator.Evaluate(row).ToList();
        // Two "undefined" warnings, no "equal" warning on top.
        Assert.Equal(2, findings.Count);
        Assert.DoesNotContain(findings, f => f.Reason.Contains("== Color"));
    }

    [Fact]
    public void Do_XrefValMultipleReadsButOneWrite_NoError()
    {
        // A tag legitimately read from many places but written from one — that's fine.
        // Rule changed: only the WRITE count matters.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            SystemNameEnglish = "DO_SYS",
            SystemNameOther = "DO_SYS",
            NameEnglish = "Vent on",
            NameOther = "Ventil",
            CrossReferenceValCount = 5,
            CrossReferenceValWriteCount = 1
        };

        Assert.Empty(DoRepairEvaluator.Evaluate(row, ValidationOptions.Default));
    }

    [Fact]
    public void Do_XrefValMultipleWrites_Error()
    {
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            SystemNameEnglish = "DO_SYS",
            SystemNameOther = "DO_SYS",
            NameEnglish = "Vent on",
            NameOther = "Ventil",
            CrossReferenceValCount = 4,
            CrossReferenceValWriteCount = 2
        };

        var error = Assert.Single(
            DoRepairEvaluator.Evaluate(row, ValidationOptions.Default),
            f => f.Severity == IssueSeverity.Error);
        Assert.Contains("VAL writes", error.Reason);
    }

    [Fact]
    public void Do_XrefDataAbsent_NoError()
    {
        // Until "XRef update" has been run, write count is null — must not fire.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            SystemNameEnglish = "DO_SYS",
            SystemNameOther = "DO_SYS",
            NameEnglish = "Vent",
            NameOther = "Vent"
        };

        Assert.Empty(DoRepairEvaluator.Evaluate(row, ValidationOptions.Default));
    }

    [Fact]
    public void Meas_DuplicateAlarmIdAcrossEnabledSlots_Errors()
    {
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Speed",
            NameOther = "Brzina",
            Unit = "rpm",
            LimitHhEnabled = true,
            LimitHiEnabled = true,
            LimitLoEnabled = true,
            AlarmHhId = "42",
            AlarmHiId = "42",
            AlarmLoId = "13"
        };

        var findings = MeasRepairEvaluator.Evaluate(row).ToList();
        var error = Assert.Single(findings, f => f.Severity == IssueSeverity.Error);
        Assert.Contains("Alarm ID 42", error.Reason);
        Assert.Contains("HH", error.Reason);
        Assert.Contains("HI", error.Reason);
    }

    [Fact]
    public void Meas_DuplicateAlarmIdInDisabledSlots_DoesNotError()
    {
        // Regression: spare MEAS rows have AlarmXxId="0" on every slot but all
        // Limit*Enabled flags are false. The uniqueness check must ignore disabled
        // slots; otherwise every spare row triggers ERROR "ID 0 used in multiple slots".
        var spareRow = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Spare_270",
            NameOther = "Spare_270",
            Unit = "rpm",
            LimitLlEnabled = false,
            LimitLoEnabled = false,
            LimitHiEnabled = false,
            LimitHhEnabled = false,
            AlarmErrId = "0",
            AlarmLlId = "0",
            AlarmLoId = "0",
            AlarmHiId = "0",
            AlarmHhId = "0",
            ParLlId = "0",
            ParLoId = "0",
            ParHiId = "0",
            ParHhId = "0"
        };

        var findings = MeasRepairEvaluator.Evaluate(spareRow).ToList();
        Assert.DoesNotContain(findings, f => f.Reason.Contains("used in multiple slots"));
    }

    [Fact]
    public void Meas_AlarmIdInDisabledSlot_DoesNotConflictWithEnabledSlot()
    {
        // HI is enabled with alarm 42; HH is disabled but still has alarm 42 as
        // leftover data. That's not a conflict because HH isn't active.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Temp",
            NameOther = "Temp",
            Unit = "°C",
            LimitHiEnabled = true,
            LimitHhEnabled = false,
            AlarmHiId = "42",
            AlarmHhId = "42"
        };

        var findings = MeasRepairEvaluator.Evaluate(row).ToList();
        Assert.DoesNotContain(findings, f => f.Reason.Contains("Alarm ID"));
    }

    [Fact]
    public void Meas_ErrIsAlwaysEvaluated_EvenWithoutLimitEnableFlag()
    {
        // ERR has no Limit*Enabled equivalent; it's always considered active.
        // If ERR shares an ID with an enabled HI slot, that's a real conflict.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Pressure",
            NameOther = "Tlak",
            Unit = "bar",
            LimitHiEnabled = true,
            AlarmErrId = "99",
            AlarmHiId = "99"
        };

        var error = Assert.Single(
            MeasRepairEvaluator.Evaluate(row),
            f => f.Severity == IssueSeverity.Error && f.Reason.Contains("Alarm ID 99"));
        Assert.Contains("ERR", error.Reason);
        Assert.Contains("HI", error.Reason);
    }

    [Fact]
    public void Meas_DuplicateAlarmLimitParameter_Errors()
    {
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Temp",
            NameOther = "Temp",
            Unit = "°C",
            LimitHhEnabled = true,
            LimitHiEnabled = true,
            ParHhId = "7",
            ParHiId = "7"
        };

        var error = Assert.Single(
            MeasRepairEvaluator.Evaluate(row),
            f => f.Severity == IssueSeverity.Error && f.Reason.Contains("Parameter"));
        Assert.Contains("Par HH", error.Reason);
        Assert.Contains("Par HI", error.Reason);
    }

    [Fact]
    public void Meas_ZeroIsTreatedAsUnassigned()
    {
        // Even with all limits enabled, ID "0" means "no alarm/parameter assigned"
        // and must not trigger duplicate detection.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Voltage",
            NameOther = "Napon",
            Unit = "V",
            LimitLlEnabled = true,
            LimitLoEnabled = true,
            LimitHiEnabled = true,
            LimitHhEnabled = true,
            AlarmErrId = "0",
            AlarmLlId = "0",
            AlarmLoId = "0",
            AlarmHiId = "0",
            AlarmHhId = "0"
        };

        var findings = MeasRepairEvaluator.Evaluate(row).ToList();
        Assert.DoesNotContain(findings, f => f.Reason.Contains("used in multiple slots"));
    }

    [Fact]
    public void Meas_ScalingParameterReuse_DoesNotError()
    {
        // ParOffsetId/ParScaleFactorId/ParVal1Id/ParVal2Id are NOT checked
        // for uniqueness on purpose — sharing scaling parameters is legal.
        var row = new DbStringRow
        {
            TrueText = "TRUE",
            NameEnglish = "Speed",
            NameOther = "Speed",
            Unit = "rpm",
            ParOffsetId = "100",
            ParScaleFactorId = "100",
            ParVal1Id = "100"
        };

        Assert.Empty(MeasRepairEvaluator.Evaluate(row));
    }

    [Fact]
    public void Ao_UsedDeterminedByTrueText()
    {
        var unused = new DbAoRow { TrueText = "", NameEnglish = "spare", Unit = "" };
        Assert.Empty(AoRepairEvaluator.Evaluate(unused, ValidationOptions.Default));

        var used = new DbAoRow { TrueText = "TRUE", NameEnglish = "", NameOther = "", Unit = "" };
        var findings = AoRepairEvaluator.Evaluate(used, ValidationOptions.Default).ToList();
        Assert.Equal(3, findings.Count); // Name ENG + Name OTHER + Unit
        Assert.All(findings, f => Assert.Equal(IssueSeverity.Warning, f.Severity));
    }

    [Fact]
    public void Ao_XrefValMultipleWrites_Error()
    {
        var row = new DbAoRow
        {
            TrueText = "TRUE",
            NameEnglish = "Valve setpoint",
            NameOther = "Setpoint",
            Unit = "%",
            CrossReferenceValCount = 3,
            CrossReferenceValWriteCount = 2
        };

        Assert.Single(
            AoRepairEvaluator.Evaluate(row, ValidationOptions.Default),
            f => f.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Ao_XrefValOneWrite_NoError()
    {
        var row = new DbAoRow
        {
            TrueText = "TRUE",
            NameEnglish = "Valve setpoint",
            NameOther = "Setpoint",
            Unit = "%",
            CrossReferenceValCount = 4,
            CrossReferenceValWriteCount = 1
        };

        Assert.Empty(AoRepairEvaluator.Evaluate(row, ValidationOptions.Default));
    }

    [Fact]
    public void Alm_XrefTempMultipleWrites_Error()
    {
        var row = new DbStringRow
        {
            Used = "1",
            NameEnglish = "Overtemp",
            NameOther = "Pregrijavanje",
            SystemNameEnglish = "OVT",
            SystemNameOther = "OVT",
            DescriptionEnglish = "desc",
            DescriptionOther = "opis",
            CrossReferenceTempCount = 6,
            CrossReferenceTempWriteCount = 4
        };

        Assert.Single(
            AlmRepairEvaluator.Evaluate(row, ValidationOptions.Default),
            f => f.Severity == IssueSeverity.Error && f.Reason.Contains("TEMP"));
    }

    [Fact]
    public void Alm_XrefTempOneWrite_NoError()
    {
        var row = new DbStringRow
        {
            Used = "1",
            NameEnglish = "Overtemp",
            NameOther = "Pregrijavanje",
            SystemNameEnglish = "OVT",
            SystemNameOther = "OVT",
            DescriptionEnglish = "desc",
            DescriptionOther = "opis",
            CrossReferenceTempCount = 5,
            CrossReferenceTempWriteCount = 1
        };

        Assert.DoesNotContain(
            AlmRepairEvaluator.Evaluate(row, ValidationOptions.Default),
            f => f.Reason.Contains("TEMP"));
    }

    [Fact]
    public void Par_UsesInUseField_NotUsed()
    {
        var emptyInUseRow = new DbStringRow
        {
            Used = "1",
            InUse = "0",
            NameEnglish = "spare",
            Unit = ""
        };
        Assert.Empty(ParRepairEvaluator.Evaluate(emptyInUseRow));

        var inUseRow = new DbStringRow
        {
            Used = "0",
            InUse = "1",
            NameEnglish = "spare",
            NameOther = "Some",
            Unit = "",
            DescriptionEnglish = "desc",
            DescriptionOther = "opis"
        };
        var findings = ParRepairEvaluator.Evaluate(inUseRow).ToList();
        // Should flag Name ENG (spare) + Unit
        Assert.Equal(2, findings.Count);
    }
}
