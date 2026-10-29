using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Core.Tests;

public class SchemePartsTests
{
    [Fact]
    public void A_scheme_built_part_by_part_reads_back_the_same()
    {
        TemplatePart[] parts =
        [
            new(TemplatePartKind.Field, "Project"),
            new(TemplatePartKind.Text, "-"),
            new(TemplatePartKind.Field, "Area"),
            new(TemplatePartKind.Text, "-"),
            new(TemplatePartKind.Counter, Width: 3),
            new(TemplatePartKind.Text, "."),
            new(TemplatePartKind.Year, Width: 2),
            new(TemplatePartKind.Site),
            new(TemplatePartKind.CheckDigit),
        ];

        var template = SchemeParts.ToTemplate(parts);

        Assert.Equal("{field:Project}-{field:Area}-{seq:000}.{yy}{site}{check}", template);
        Assert.Equal(parts, NumberTemplate.Parse(template).Parts());
    }

    [Theory]
    [InlineData(null, null, null, "anything at all", true)]
    [InlineData(NumberingFormats.Digits, 2, 2, "05", true)]
    [InlineData(NumberingFormats.Digits, 2, 2, "5", false)]
    [InlineData(NumberingFormats.Digits, null, null, "5A", false)]
    [InlineData(NumberingFormats.Letters, 1, 3, "ABC", true)]
    [InlineData(NumberingFormats.Letters, 1, 3, "ABCD", false)]
    [InlineData(NumberingFormats.LettersAndDigits, null, 4, "MR03", true)]
    [InlineData(NumberingFormats.LettersAndDigits, null, null, "MR-03", false)]
    public void Typed_values_follow_their_field_rules(string? format, int? min, int? max, string value, bool fits)
    {
        var field = new NumberingField("Code", Format: format, MinLength: min, MaxLength: max);

        Assert.Equal(fits, field.Problem(value) is null);
        // A name checked at check-in follows the same rules.
        var pattern = NumberTemplate.Parse("X-{field:Code}-{seq:00}").NamePattern([field]);
        Assert.Equal(fits, pattern.IsMatch($"X-{value}-01"));
    }

    [Fact]
    public void A_pick_list_allows_only_its_codes()
    {
        var field = new NumberingField("Area", "Area", [new("01", "Frame"), new("02", "Mast")]);

        Assert.Null(field.Problem("02"));
        Assert.Equal("Area must be one of: 01 (Frame), 02 (Mast).", field.Problem("03"));
    }
}
