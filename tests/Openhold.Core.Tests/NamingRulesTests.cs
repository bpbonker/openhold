using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Core.Tests;

public sealed class NamingRulesTests
{
    private static IReadOnlyList<NumberingChoice> Codes(params string[] codes) => codes.Select(c => new NumberingChoice(c, c)).ToList();

    // A detailed project numbering, as an administrator might set it up: equipment code from the machine's
    // folder, area from the area folder, the component type picked, the version defaulting to 00.
    private static readonly NumberingScheme Models = new("models", "Project models",
        "{field:Application}{field:Type}{field:Equipment}-{field:Area}-{seq:000}{field:Component}-{field:Version}", false, TimeSpan.FromDays(1), true)
    {
        Fields =
        [
            new("Application", Choices: Codes("A", "B", "C"), From: NumberingSources.Folder),
            new("Type", Choices: Codes("M", "E", "H"), From: NumberingSources.Folder),
            new("Equipment", Choices: Codes([.. Enumerable.Range(1, 25).Select(n => n.ToString("00"))]), From: NumberingSources.Folder),
            new("Area", Choices: Codes("00", "01", "02", "05", "07", "10", "50"), From: NumberingSources.Folder),
            new("Component", Choices: Codes("A", "P", "W")),
            new("Version", Choices: Codes("00", "01", "02"), Default: "00"),
        ],
    };

    private static readonly NumberingScheme Children = new("children", "Parts of an assembly", "{field:Parent}-{seq:000}", false, TimeSpan.FromDays(1), true)
    {
        Fields = [new("Parent", From: NumberingSources.Parent)],
    };

    private static string? Value(IReadOnlyList<SuggestedField> fields, string name) => fields.Single(f => f.Field.Name == name).Value;

    [Fact]
    public void Folder_codes_fill_the_fields_top_down_and_one_code_can_fill_several()
    {
        var fields = NamingRules.Fill(Models, "$/Models/AM01 - Conveyor/02 - Frame/Infeed", null);

        Assert.Equal(("A", "M", "01", "02"), (Value(fields, "Application"), Value(fields, "Type"), Value(fields, "Equipment"), Value(fields, "Area")));
        Assert.All(fields.Where(f => f.Field.From == NumberingSources.Folder), f => Assert.Equal(NumberingSources.Folder, f.Source));
        Assert.Null(Value(fields, "Component"));                                      // picked by the person
        Assert.Equal(("00", "default"), (Value(fields, "Version"), fields.Single(f => f.Field.Name == "Version").Source));
    }

    [Fact]
    public void A_folder_that_fits_no_field_is_skipped_and_an_unknown_code_fills_nothing()
    {
        var fields = NamingRules.Fill(Models, "$/Models/AX99 - Prototype/Sandbox", null);

        Assert.All(fields.Where(f => f.Field.From == NumberingSources.Folder), f => Assert.Null(f.Value));
    }

    [Fact]
    public void Parts_take_the_number_of_the_assembly_they_are_made_in()
    {
        var fields = NamingRules.Fill(Children, "$/Models/AM01 - Conveyor/02 - Frame", "AM01-02-001A-00 - Frame assembly.iam");

        Assert.Equal(("AM01-02-001A-00", NumberingSources.Parent), (Value(fields, "Parent"), fields.Single().Source));
    }

    [Fact]
    public void The_category_of_the_save_location_picks_the_scheme()
    {
        var configuration = DefaultConfiguration.Create() with
        {
            Categories =
            [
                new CategoryDefinition("part", "Project part", NumberingSchemeId: "children", Extensions: [".ipt"], Folders: ["$/Models/*"], EnforceNaming: true),
                new CategoryDefinition("assembly", "Project assembly", NumberingSchemeId: "models", Extensions: [".iam"], Folders: ["$/Models/*"]),
            ],
        };

        var part = NamingRules.Suggest(configuration, [Models, Children], "$/Models/AM01 - Conveyor/02 - Frame", "new.ipt", "AM01-02-001A-00.iam");
        var assembly = NamingRules.Suggest(configuration, [Models, Children], "$/Models/AM01 - Conveyor/02 - Frame", "new.iam");

        Assert.Equal(("part", "children", true, true), (part.CategoryId, part.Scheme?.Id, part.Enforced, part.Complete));
        Assert.Equal(("assembly", "models", false, false), (assembly.CategoryId, assembly.Scheme?.Id, assembly.Enforced, assembly.Complete));
    }

    [Fact]
    public void Files_made_inside_an_assembly_can_use_their_own_scheme()
    {
        var configuration = DefaultConfiguration.Create() with
        {
            Categories = [new CategoryDefinition("model", "Project model", NumberingSchemeId: "models", Extensions: [".ipt", ".iam"], Folders: ["$/Models/*"],
                EnforceNaming: true, ChildNumberingSchemeId: "children")],
        };

        var alone = NamingRules.Suggest(configuration, [Models, Children], "$/Models/AM01 - Conveyor/02 - Frame", "new.ipt");
        var inside = NamingRules.Suggest(configuration, [Models, Children], "$/Models/AM01 - Conveyor/02 - Frame", "new.ipt", "AM01-02-001A-00.iam");

        Assert.Equal("models", alone.Scheme?.Id);
        Assert.Equal(("children", true), (inside.Scheme?.Id, inside.Enforced));
        Assert.Equal(["models", "children"], NamingRules.SchemesOf(configuration.Categories[0]));
    }

    [Theory]
    [InlineData("AM01-02-001A-00.iam", true)]
    [InlineData("AM01-02-001A-00 - Frame assembly.iam", true)]
    [InlineData("am01-02-1234P-01.ipt", true)]           // codes in any case; the counter may outgrow its zeros
    [InlineData("AM01-02-01A-00.iam", false)]             // counter narrower than 000
    [InlineData("AQ01-02-001A-00.iam", false)]            // Q isn't a type
    [InlineData("Bracket.ipt", false)]
    public void A_name_follows_the_scheme_only_if_the_scheme_could_have_issued_it(string name, bool follows)
    {
        Assert.Equal(follows, NamingRules.Follows(Models, name));
    }

    [Fact]
    public void Starter_schemes_are_valid_templates()
    {
        foreach (var scheme in StarterSchemes.All)
        {
            var template = NumberTemplate.Parse(scheme.Template);
            Assert.All(scheme.Fields, f => Assert.Contains(f.Name, template.FieldNames));
            Assert.False(string.IsNullOrWhiteSpace(scheme.Description));
        }
    }
}
