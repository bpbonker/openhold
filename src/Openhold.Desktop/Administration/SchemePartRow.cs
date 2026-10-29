using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;

namespace Openhold.Desktop.Administration;

/// <summary>The kinds of part an administrator builds a number from, as offered in the scheme builder.</summary>
public enum SchemePartType
{
    FixedText,
    PickList,
    Typed,
    FolderCode,
    ParentNumber,
    Counter,
    CheckDigit,
    Year,
    Site,
}

/// <summary>One code a pick list allows and what it means; the grid adds new ones, so it needs a parameterless constructor.</summary>
public sealed class CodeRow
{
    public string? Code { get; set; }
    public string? Meaning { get; set; }
}

/// <summary>
/// One part of a numbering scheme in the builder: fixed text, a value people pick, type or that fills in by itself, the
/// counter, a check digit, the year or the site. The parts in order make the scheme's template and fields.
/// </summary>
public sealed class SchemePartRow : INotifyPropertyChanged
{
    private SchemePartType _type;
    private string _text = "";
    private string _name = "";
    private string? _label;
    private string? _default;
    private string _format = "";
    private string _minLength = "";
    private string _maxLength = "";
    private int _width = 3;
    private bool _fourDigitYear = true;

    public SchemePartRow(SchemePartType type)
    {
        _type = type;
        Codes.CollectionChanged += (_, _) => Changed(nameof(Summary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IReadOnlyList<(SchemePartType Type, string Name, string Description)> Types { get; } =
    [
        (SchemePartType.FixedText, "Fixed text", "The same text in every number: a prefix such as DOC, or a separator such as -"),
        (SchemePartType.PickList, "Pick list", "One of a list of codes you define, e.g. 05 = Hydraulics"),
        (SchemePartType.Typed, "Typed value", "Text people type, with rules: letters or digits, and how long"),
        (SchemePartType.FolderCode, "Code of a folder above", "Taken from a folder named \"CODE - Name\" above where the file is saved"),
        (SchemePartType.ParentNumber, "Parent assembly's number", "The number of the assembly the file is made in, for numbering parts under it"),
        (SchemePartType.Counter, "Counter", "The running number: 001, 002, 003… counted separately for each different text before it"),
        (SchemePartType.CheckDigit, "Check digit", "One digit worked out from the counter that catches typing mistakes"),
        (SchemePartType.Year, "Year", "The year the number is issued, 26 or 2026"),
        (SchemePartType.Site, "Site code", "The code of the office that issues the number, e.g. AU"),
    ];

    public static IReadOnlyList<Choice> Formats { get; } =
    [
        new("", "Any text"),
        new(NumberingFormats.Letters, "Letters only"),
        new(NumberingFormats.Digits, "Digits only"),
        new(NumberingFormats.LettersAndDigits, "Letters and digits"),
    ];

    public SchemePartType Type
    {
        get => _type;
        set => Set(ref _type, value);
    }

    public string Text { get => _text; set => Set(ref _text, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string? Label { get => _label; set => Set(ref _label, value); }
    public string? Default { get => _default; set => Set(ref _default, value); }
    public string Format { get => _format; set => Set(ref _format, value); }
    public string MinLength { get => _minLength; set => Set(ref _minLength, value); }
    public string MaxLength { get => _maxLength; set => Set(ref _maxLength, value); }
    public int Width { get => _width; set => Set(ref _width, value); }
    public bool FourDigitYear { get => _fourDigitYear; set => Set(ref _fourDigitYear, value); }
    public ObservableCollection<CodeRow> Codes { get; } = [];

    public bool IsField => Type is SchemePartType.PickList or SchemePartType.Typed or SchemePartType.FolderCode or SchemePartType.ParentNumber;

    /// <summary>What the part is, in the list of parts: e.g. Area, "-", Counter.</summary>
    public string Title => Type switch
    {
        SchemePartType.FixedText => Text.Length == 0 ? "(no text yet)" : $"“{Text}”",
        _ when IsField => Name.Length == 0 ? "(no name yet)" : Label is { Length: > 0 } label && label != Name ? $"{Name} – {label}" : Name,
        _ => TypeName,
    };

    public string TypeName => Types.First(t => t.Type == Type).Name;

    /// <summary>The part's settings in a few words, e.g. "Pick list · 6 codes" or "Counter · 3 digits".</summary>
    public string Summary => Type switch
    {
        SchemePartType.FixedText => "Fixed text",
        SchemePartType.PickList => $"Pick list · {Codes.Count(c => !string.IsNullOrWhiteSpace(c.Code))} codes{DefaultNote}",
        SchemePartType.Typed => $"Typed · {Rules}{DefaultNote}",
        SchemePartType.FolderCode => (Codes.Count(c => !string.IsNullOrWhiteSpace(c.Code)) is var count and > 0 ? $"From a folder above · {count} codes" : "From a folder above") + DefaultNote,
        SchemePartType.ParentNumber => "From the parent assembly",
        SchemePartType.Counter => $"Counter · {Width} digits",
        SchemePartType.Year => FourDigitYear ? "Year · 4 digits" : "Year · 2 digits",
        _ => TypeName,
    };

    private string DefaultNote => string.IsNullOrWhiteSpace(Default) ? "" : $" · default {Default.Trim()}";

    private string Rules
    {
        get
        {
            var what = Formats.First(f => f.Id == Format).Name.ToLowerInvariant();
            var (min, max) = (Parse(MinLength), Parse(MaxLength));
            var length = (min, max) switch
            {
                (null, null) => "",
                ({ } a, { } b) when a == b => $", {a} long",
                (null, { } b) => $", up to {b} long",
                ({ } a, null) => $", at least {a} long",
                ({ } a, { } b) => $", {a} to {b} long",
            };
            return what + length;
        }
    }

    public static int? Parse(string text) => int.TryParse(text.Trim(), out var value) ? value : null;

    public TemplatePart ToTemplatePart() => Type switch
    {
        SchemePartType.FixedText => new TemplatePart(TemplatePartKind.Text, Text),
        SchemePartType.Counter => new TemplatePart(TemplatePartKind.Counter, Width: Width),
        SchemePartType.CheckDigit => new TemplatePart(TemplatePartKind.CheckDigit),
        SchemePartType.Year => new TemplatePart(TemplatePartKind.Year, Width: FourDigitYear ? 4 : 2),
        SchemePartType.Site => new TemplatePart(TemplatePartKind.Site),
        _ => new TemplatePart(TemplatePartKind.Field, Name.Trim()),
    };

    /// <summary>The field this part defines (null for parts that aren't values).</summary>
    public NumberingField? ToField()
    {
        if (!IsField)
            return null;
        var codes = Type is SchemePartType.PickList or SchemePartType.FolderCode
            ? Codes.Where(c => !string.IsNullOrWhiteSpace(c.Code)).Select(c => new NumberingChoice(c.Code!.Trim(), c.Meaning?.Trim() ?? "")).ToList()
            : [];
        var typed = Type == SchemePartType.Typed;
        return new NumberingField(Name.Trim(), string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(), codes.Count > 0 ? codes : null, null,
            Type switch { SchemePartType.FolderCode => NumberingSources.Folder, SchemePartType.ParentNumber => NumberingSources.Parent, _ => null },
            Type == SchemePartType.ParentNumber || string.IsNullOrWhiteSpace(Default) ? null : Default.Trim(),
            typed && Format.Length > 0 ? Format : null, typed ? Parse(MinLength) : null, typed ? Parse(MaxLength) : null);
    }

    /// <summary>A scheme's parts, from its template and fields.</summary>
    public static List<SchemePartRow> From(NumberingScheme scheme)
    {
        var rows = new List<SchemePartRow>();
        foreach (var part in NumberTemplate.Parse(scheme.Template).Parts())
        {
            var field = part.Kind == TemplatePartKind.Field
                ? scheme.Fields.FirstOrDefault(f => string.Equals(f.Name, part.Text, StringComparison.OrdinalIgnoreCase)) ?? new NumberingField(part.Text!)
                : null;
            var row = new SchemePartRow(part.Kind switch
            {
                TemplatePartKind.Text => SchemePartType.FixedText,
                TemplatePartKind.Counter => SchemePartType.Counter,
                TemplatePartKind.CheckDigit => SchemePartType.CheckDigit,
                TemplatePartKind.Year => SchemePartType.Year,
                TemplatePartKind.Site => SchemePartType.Site,
                _ => field!.From switch
                {
                    NumberingSources.Folder => SchemePartType.FolderCode,
                    NumberingSources.Parent => SchemePartType.ParentNumber,
                    _ => field.Choices is { Count: > 0 } ? SchemePartType.PickList : SchemePartType.Typed,
                },
            })
            {
                Text = part.Kind == TemplatePartKind.Text ? part.Text ?? "" : "",
                Width = part.Kind == TemplatePartKind.Counter ? part.Width : 3,
                FourDigitYear = part.Kind != TemplatePartKind.Year || part.Width == 4,
            };
            if (field is not null)
            {
                row.Name = field.Name;
                row.Label = field.Label;
                row.Default = field.Default;
                row.Format = field.Format ?? "";
                row.MinLength = field.MinLength?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
                row.MaxLength = field.MaxLength?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
                foreach (var choice in field.Choices ?? [])
                    row.Codes.Add(new CodeRow { Code = choice.Code, Meaning = choice.Meaning });
            }
            rows.Add(row);
        }
        return rows;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Changed(name!);
        Changed(nameof(Title));
        Changed(nameof(Summary));
        if (name == nameof(Type))
        {
            Changed(nameof(IsField));
            Changed(nameof(TypeName));
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
