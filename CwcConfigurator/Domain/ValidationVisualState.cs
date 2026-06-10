namespace CwcConfigurator.Domain;

public interface IValidationState
{
    bool HasValidationError { get; set; }
}
