namespace CwcConfigurator.Services.Validation;

/// <summary>
/// Toggles applied to a single validator run. The expensive cross-reference
/// rules are off by default — they rely on data populated by the TIA xref
/// scan which can take minutes for large projects.
/// </summary>
public sealed class ValidationOptions
{
    public bool IncludeCrossReferenceChecks { get; init; }

    public static ValidationOptions Default => new();
}
