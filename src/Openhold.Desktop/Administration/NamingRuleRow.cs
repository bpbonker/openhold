using System.ComponentModel;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Desktop.Administration;

/// <summary>Choices for the numbering editor.</summary>
public static class NamingChoices
{
    public static IReadOnlyList<Choice> FillFrom { get; } =
    [
        new("", "Picked or typed"),
        new(NumberingSources.Folder, "A folder above"),
        new(NumberingSources.Parent, "The parent assembly"),
    ];

    /// <summary>A number the scheme could issue (the first, or a later one), with a sample value for each field.</summary>
    public static string Example(NumberingScheme scheme, int offset = 0)
    {
        try
        {
            var template = NumberTemplate.Parse(scheme.Template);
            var fields = template.FieldNames.ToDictionary(name => name,
                name => Sample(scheme.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new NumberingField(name)),
                StringComparer.OrdinalIgnoreCase);
            return template.Format(scheme.StartAt + offset, new NumberContext(DateTimeOffset.Now, "AU", fields));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return scheme.Template;
        }
    }

    /// <summary>A value that fits the field: its default, its first code, or text of its format and length.</summary>
    public static string Sample(NumberingField field)
    {
        if ((field.Default ?? field.Choices?.FirstOrDefault()?.Code) is { Length: > 0 } given)
            return given;
        if (field.From == NumberingSources.Parent)
            return "PARENT";
        var length = field.MinLength ?? field.MaxLength;
        var sample = field.Format switch
        {
            NumberingFormats.Digits => "1234567890",
            NumberingFormats.Letters => "ABCDEFGHJK",
            NumberingFormats.LettersAndDigits => "A1B2C3D4E5",
            _ => field.Name.ToUpperInvariant(),
        };
        if (length is { } n)
            sample = string.Concat(Enumerable.Repeat(sample, n / Math.Max(1, sample.Length) + 1))[..n];
        else if (field.Format is not null)
            sample = sample[..3];
        return field.MaxLength is { } max && sample.Length > max ? sample[..max] : sample;
    }
}

/// <summary>
/// One row of the Naming Rules overview: a category, where it applies and how its new files are named. The schemes
/// and the "required" switch edit the category itself, so Save Changes keeps them.
/// </summary>
public sealed class NamingRuleRow : INotifyPropertyChanged
{
    private readonly Func<string, NumberingScheme?> _scheme;

    public NamingRuleRow(CategoryModel category, Func<string, NumberingScheme?> scheme)
    {
        Category = category;
        _scheme = scheme;
        category.PropertyChanged += (_, _) =>
        {
            foreach (var name in new[] { nameof(AppliesTo), nameof(LooksLike), nameof(FillsIn) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CategoryModel Category { get; }

    public string AppliesTo
    {
        get
        {
            if (Category.Manual)
                return "Only when an administrator chooses it";
            var types = string.IsNullOrWhiteSpace(Category.Extensions) ? "Any file" : Category.Extensions;
            var folders = Category.Folders.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return folders.Length == 0 ? $"{types}, anywhere" : $"{types} in {string.Join(", ", folders)}";
        }
    }

    public string LooksLike
    {
        get
        {
            var own = _scheme(Category.NumberingSchemeId);
            var child = _scheme(Category.ChildNumberingSchemeId);
            return (own, child) switch
            {
                (null, null) => "Named freely",
                (not null, null) => NamingChoices.Example(own),
                (null, not null) => $"Inside an assembly: {NamingChoices.Example(child)}; otherwise named freely",
                _ => $"{NamingChoices.Example(own!)}{Environment.NewLine}Inside an assembly: {NamingChoices.Example(child!)}",
            };
        }
    }

    public string FillsIn => string.Join(Environment.NewLine, new[] { Category.NumberingSchemeId, Category.ChildNumberingSchemeId }
        .Select(id => _scheme(id)).OfType<NumberingScheme>().DistinctBy(s => s.Id).SelectMany(Describe));

    /// <summary>Fields grouped by where they come from, in template order, e.g. "Application, Type, Area: from the folders".</summary>
    private static IEnumerable<string> Describe(NumberingScheme scheme)
    {
        var template = NumberTemplate.Parse(scheme.Template);
        if (template.FieldNames.Count == 0)
            return ["Nothing: the next number is taken"];
        return template.FieldNames
            .Select(name => (Name: name, How: scheme.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is { } definition
                ? NamingRules.Describe(definition) : "typed"))
            .GroupBy(f => f.How)
            .Select(g => $"{string.Join(", ", g.Select(f => f.Name))}: {g.Key}");
    }
}
