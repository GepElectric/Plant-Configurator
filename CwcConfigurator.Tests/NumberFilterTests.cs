using CwcConfigurator.Services.Simulation;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class NumberFilterTests
{
    [Theory]
    [InlineData("", 7, true)]
    [InlineData("  ", 7, true)]
    [InlineData("3", 3, true)]
    [InlineData("3", 4, false)]
    [InlineData("1-4", 4, true)]
    [InlineData("1-4", 5, false)]
    [InlineData("4-1", 2, true)]
    [InlineData("5-", 500, true)]
    [InlineData("5-", 4, false)]
    [InlineData("1,3,7-9", 8, true)]
    [InlineData("1, 3; 7-9", 5, false)]
    [InlineData("abc", 5, true)]
    public void Matches_NumbersRangesAndLists(string filter, int value, bool expected)
    {
        Assert.Equal(expected, NumberFilter.Matches(filter, value));
    }

    [Theory]
    [InlineData("DI_A1", 3, "DI_A1 +A1", true)]
    [InlineData("a1", 3, "DI_A1 +A1", true)]
    [InlineData("A2", 3, "DI_A1 +A1", false)]
    [InlineData("3", 3, "DI_A1 +A1", true)]
    [InlineData("A2,3", 3, "DI_A1 +A1", true)]
    public void Matches_ModuleNameText(string filter, int value, string name, bool expected)
    {
        Assert.Equal(expected, NumberFilter.Matches(filter, value, name));
    }
}
