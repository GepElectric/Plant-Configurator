using System.IO;

namespace CwcConfigurator.Services;

public sealed class ExternalFileService
{
    public void EnsureExists(string filePath, string actionName)
    {
        if (File.Exists(filePath))
        {
            return;
        }

        throw new FileNotFoundException($"External file does not exist. Action '{actionName}' was stopped.", filePath);
    }
}
