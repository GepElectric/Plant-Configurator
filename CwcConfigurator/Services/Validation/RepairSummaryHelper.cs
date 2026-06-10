namespace CwcConfigurator.Services.Validation;

/// <summary>
/// Folds the per-row finding sequence into the (bool, string) pair the
/// repair-column UI needs. Mirrors <see cref="DbRepairEvaluator.GetAlarmRepairSummary"/>
/// but takes severity-aware findings.
/// </summary>
public static class RepairSummaryHelper
{
    /// <summary>
    /// Any finding (Warning or Error) makes the row "need repair" — used by
    /// the slicer-driven filter so Error-severity issues (xref writes &gt; 1)
    /// also show up in the Repair view.
    /// </summary>
    public static bool HasFindings(IEnumerable<RepairFinding> findings)
    {
        foreach (var _ in findings)
        {
            return true;
        }
        return false;
    }

    // Backwards-compatible alias; older callers still expect HasWarnings.
    public static bool HasWarnings(IEnumerable<RepairFinding> findings) => HasFindings(findings);

    public static string Summarize(IEnumerable<RepairFinding> findings)
    {
        var parts = findings
            .Select(f => f.Severity == IssueSeverity.Error ? "[E] " + f.Reason : f.Reason)
            .ToList();
        return parts.Count == 0 ? "OK" : string.Join(", ", parts);
    }
}
