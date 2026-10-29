namespace Openhold.Core.Numbering;

/// <summary>The kinds of part a number is built from.</summary>
public enum TemplatePartKind
{
    /// <summary>Fixed text, e.g. a prefix or a "-" between parts.</summary>
    Text,

    /// <summary>A value people pick or type, or that fills in from the folder or the parent assembly (see the scheme's fields).</summary>
    Field,

    /// <summary>The counter: <see cref="TemplatePart.Width"/> digits, zero-padded.</summary>
    Counter,

    /// <summary>A check digit for the counter.</summary>
    CheckDigit,

    /// <summary>The year, two or four digits (<see cref="TemplatePart.Width"/>).</summary>
    Year,

    /// <summary>The code of the site that issues the number.</summary>
    Site,
}

/// <summary>One part of a number: <see cref="Text"/> is the fixed text, or the field's name.</summary>
public sealed record TemplatePart(TemplatePartKind Kind, string? Text = null, int Width = 0);

/// <summary>
/// Schemes as administrators build them, part by part, turned into the template the numbering engine reads (and back
/// with <see cref="NumberTemplate.Parts"/>).
/// </summary>
public static class SchemeParts
{
    public static string ToTemplate(IEnumerable<TemplatePart> parts) => string.Concat(parts.Select(part => part.Kind switch
    {
        TemplatePartKind.Text => part.Text ?? "",
        TemplatePartKind.Field => $"{{field:{part.Text?.Trim()}}}",
        TemplatePartKind.Counter => $"{{seq:{new string('0', Math.Clamp(part.Width, 1, 18))}}}",
        TemplatePartKind.CheckDigit => "{check}",
        TemplatePartKind.Year => part.Width == 4 ? "{yyyy}" : "{yy}",
        TemplatePartKind.Site => "{site}",
        _ => throw new ArgumentOutOfRangeException(nameof(parts), part.Kind, "Unknown part."),
    }));

    /// <summary>Why a field name can't be used (empty, or has braces), or null when it can.</summary>
    public static string? FieldNameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Every value in the number needs a name, e.g. Area."
        : name.IndexOfAny(['{', '}']) >= 0 ? $"'{name}' can't contain {{ or }}."
        : null;
}
