using Openhold.Core.Configuration;

namespace Openhold.Core.Tests;

public sealed class VaultConfigurationTests
{
    private static RevisionScheme Scheme(string id) => DefaultConfiguration.Create().RevisionScheme(id)!;

    [Theory]
    [InlineData("letters", null, "A")]
    [InlineData("letters", "A", "B")]
    [InlineData("letters", "H", "J")]
    [InlineData("letters", "Y", "AA")]
    [InlineData("number-letter", "", "1A")]
    [InlineData("number-letter", "1A", "1B")]
    [InlineData("number-letter", "1H", "1J")]
    [InlineData("numbers", "01", "02")]
    [InlineData("numbers", "09", "10")]
    [InlineData("letter-number", "A.01", "A.02")]
    public void Minor_revision_steps_the_last_part(string scheme, string? current, string expected) =>
        Assert.Equal(expected, Scheme(scheme).Next(current));

    [Theory]
    [InlineData("number-letter", "1C", "2A")]
    [InlineData("letter-number", "A.07", "B.01")]
    [InlineData("letter-number", "H.03", "J.01")]
    public void Major_revision_steps_the_first_part_and_restarts_the_rest(string scheme, string current, string expected) =>
        Assert.Equal(expected, Scheme(scheme).Next(current, level: 0));

    [Fact]
    public void A_revision_from_before_the_scheme_still_gets_a_sensible_next_value()
    {
        // "Letters" scheme, but the drawing already says 1B: step its last letter rather than refuse.
        Assert.Equal("1C", Scheme("letters").Next("1B"));
    }

    [Fact]
    public void Default_configuration_is_valid()
    {
        Assert.Empty(DefaultConfiguration.Create().Validate());
    }

    [Fact]
    public void Validation_explains_what_is_wrong()
    {
        var configuration = DefaultConfiguration.Create();
        var broken = configuration with
        {
            Categories = [.. configuration.Categories, new CategoryDefinition("x", "X", LifecycleId: "missing")],
            Lifecycles = [.. configuration.Lifecycles, new LifecycleDefinition("bad", "Bad",
                [new LifecycleState("Locked first", Locked: true)],
                [new LifecycleTransition("Locked first", "Nowhere", Approvals: [new ApprovalRule("nobody")])])],
            RevisionSchemes = [.. configuration.RevisionSchemes, new RevisionScheme("two", "Two numbers",
                [new RevisionPart(RevisionPartKind.Number, "1"), new RevisionPart(RevisionPartKind.Number, "1")])],
        };

        var problems = broken.Validate();

        Assert.Contains(problems, p => p.Contains("lifecycle 'missing'"));
        Assert.Contains(problems, p => p.Contains("can't be locked"));
        Assert.Contains(problems, p => p.Contains("'Nowhere', which isn't one of its states"));
        Assert.Contains(problems, p => p.Contains("group 'nobody'"));
        Assert.Contains(problems, p => p.Contains("need a separator"));
    }

    [Theory]
    [InlineData("$/Library", "$/Library/*", true)]
    [InlineData("$/Library/Buyout/Motors", "$/Library/*", true)]
    [InlineData("$/Libraryish", "$/Library/*", false)]
    [InlineData("$/Projects/PRJ01/Buyout", "*/Buyout", true)]
    [InlineData("$/projects/prj01", "$/Projects/*", true)]
    public void Folder_patterns_match_vault_paths(string path, string pattern, bool expected) =>
        Assert.Equal(expected, Wildcard.IsMatch(path, pattern));

    [Fact]
    public void New_files_get_the_first_automatic_category_that_matches()
    {
        var configuration = DefaultConfiguration.Create();

        Assert.Equal("engineering", configuration.CategoryForNewFile("$/Electrical", "PANEL.DWG")!.Id);
        Assert.Equal("document", configuration.CategoryForNewFile("$/Electrical", "manual.pdf")!.Id);
        Assert.Equal("document", configuration.CategoryForNewFile("$/Buyout", "motor.stp")!.Id);
    }

    [Fact]
    public void A_new_vault_imposes_no_numbering()
    {
        var configuration = DefaultConfiguration.Create();

        Assert.All(configuration.Categories, c => Assert.Null(c.NumberingSchemeId));
        Assert.Null(configuration.CatalogueNumberingSchemeId);
        Assert.Null(configuration.ItemNumberingSchemeFor("sensors"));
        Assert.Equal("my-parts", (configuration with { CatalogueNumberingSchemeId = "my-parts" }).ItemNumberingSchemeFor("sensors"));
    }
}
