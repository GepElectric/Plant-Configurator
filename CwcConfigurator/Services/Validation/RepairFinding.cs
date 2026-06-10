namespace CwcConfigurator.Services.Validation;

/// <summary>
/// A single rule violation found by a per-row repair evaluator.
/// Translated to a <see cref="ProjectIssue"/> by <see cref="ProjectValidator"/>.
/// </summary>
/// <summary>
/// <paramref name="SlicerHint"/> tells the Problems-pane navigation which
/// column-slicer item to activate when the user double-clicks the issue.
/// Empty/null falls back to "Repair".
/// </summary>
public sealed record RepairFinding(
    IssueSeverity Severity,
    string Reason,
    string? SlicerHint = null,
    string? FieldName = null);
