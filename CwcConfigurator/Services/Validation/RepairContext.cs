namespace CwcConfigurator.Services.Validation;

/// <summary>
/// Process-wide flags consumed by repair evaluators. Held statically because
/// the row-level computed properties (e.g. <c>DbStringRow.AlarmRepairSummary</c>)
/// are evaluated by data binding and cannot accept context parameters.
/// MainWindow keeps this in sync with the currently-open project.
/// </summary>
public static class RepairContext
{
    /// <summary>
    /// True when the project carries both ENG and OTHER language texts.
    /// When false, evaluators skip *Other rules so monolingual projects
    /// don't fire warnings for fields the user intentionally leaves blank.
    /// </summary>
    public static bool IsMultilingual { get; set; } = true;
}
