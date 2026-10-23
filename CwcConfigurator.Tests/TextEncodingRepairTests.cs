using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class TextEncodingRepairTests
{
    [Theory]
    [InlineData("Feil ved mÃ¥ling av klaffelukeposisjon", "Feil ved måling av klaffelukeposisjon")]
    [InlineData("Feil ved mÃƒÂ¥ling av klaffelukeposisjon", "Feil ved måling av klaffelukeposisjon")]
    [InlineData("NÃƒÂ¸dstopp knapp trykket (-S301)", "Nødstopp knapp trykket (-S301)")]
    [InlineData("GrÃ¼ÃŸe", "Grüße")]
    public void Fix_RepairsMisreadUtf8(string broken, string expected)
    {
        Assert.Equal(expected, TextEncodingRepair.Fix(broken));
    }

    [Theory]
    [InlineData("Grüße aus Österreich")]
    [InlineData("Nødstopp — måling")]
    [InlineData("Ändern")]
    [InlineData("Plain ASCII text")]
    [InlineData("")]
    public void Fix_LeavesCorrectTexts(string text)
    {
        Assert.Equal(text, TextEncodingRepair.Fix(text));
    }
}
