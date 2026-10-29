using Openhold.Core.Properties;

namespace Openhold.Core.Tests;

public class RevisionsTests
{
    [Theory]
    [InlineData("A", "B")]
    [InlineData("H", "J")] // I is skipped
    [InlineData("Y", "AA")] // Z is skipped, so Y is the last single letter
    [InlineData("AY", "BA")]
    [InlineData("1B", "1C")]
    [InlineData("1H", "1J")]
    [InlineData("0", "1")]
    [InlineData("9", "10")]
    [InlineData("03", "04")]
    [InlineData("09", "10")]
    [InlineData("A.01", "A.02")]
    [InlineData("P1", "P2")]
    [InlineData("rev c", "rev d")]
    [InlineData("B-", "C-")]
    public void Next_steps_the_last_letter_or_number_run(string current, string expected) =>
        Assert.Equal(expected, Revisions.Next(current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_file_without_a_revision_starts_at_A(string? current) => Assert.Equal("A", Revisions.Next(current));

    [Fact]
    public void Skipped_letters_are_configurable()
    {
        Assert.Equal("I", Revisions.Next("H", new HashSet<char>()));
        Assert.Equal("Z", Revisions.Next("Y", new HashSet<char>()));
        Assert.Equal("AA", Revisions.Next("Z", new HashSet<char>()));
    }
}
