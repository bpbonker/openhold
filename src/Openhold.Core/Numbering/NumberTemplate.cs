using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Openhold.Core.Numbering;

/// <summary>
/// A numbering scheme template such as <c>EL-{seq:000000}</c> or <c>{field:Project}-{yy}-{seq:0000}</c>.
/// Shared by the server (which issues numbers) and the CAD add-ins (which preview them).
/// </summary>
/// <remarks>
/// Tokens: <c>{seq:000…}</c> zero-padded counter (exactly one required), <c>{check}</c> a check digit for the
/// counter (Damm algorithm: catches every single-digit typo and every swap of neighbouring digits), <c>{yyyy}</c>, <c>{yy}</c>,
/// <c>{site}</c>, and <c>{field:Name}</c> for user-supplied values such as a pick-list choice.
/// </remarks>
public sealed partial class NumberTemplate
{
    private readonly List<Token> _tokens;

    private NumberTemplate(string text, List<Token> tokens)
    {
        Text = text;
        _tokens = tokens;
    }

    public string Text { get; }

    public static NumberTemplate Parse(string text)
    {
        var tokens = new List<Token>();
        var position = 0;
        foreach (Match match in TokenPattern().Matches(text))
        {
            if (match.Index > position)
                tokens.Add(new LiteralToken(text[position..match.Index]));
            tokens.Add(ParseToken(match.Groups[1].Value, text));
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            tokens.Add(new LiteralToken(text[position..]));

        if (tokens.Count(t => t is CheckToken) > 1)
            throw new FormatException($"Numbering template '{text}' can have only one {{check}} digit.");
        var seqCount = tokens.Count(t => t is SequenceToken);
        if (seqCount != 1)
            throw new FormatException($"Numbering template '{text}' must contain exactly one {{seq:…}} token, found {seqCount}.");
        if (tokens.OfType<LiteralToken>().Any(t => t.Text.IndexOfAny(['{', '}']) >= 0))
            throw new FormatException($"Numbering template '{text}' has an unmatched brace.");
        if (tokens.OfType<LiteralToken>().Any(t => t.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new FormatException($"Numbering template '{text}' contains characters not allowed in file names.");

        return new NumberTemplate(text, tokens);
    }

    public string Format(long sequence, NumberContext context)
    {
        var builder = new StringBuilder();
        foreach (var token in _tokens)
        {
            builder.Append(token switch
            {
                LiteralToken literal => literal.Text,
                SequenceToken seq => sequence.ToString(new string('0', seq.Width), CultureInfo.InvariantCulture),
                CheckToken => Damm.CheckDigit(sequence.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
                YearToken { FourDigit: true } => context.Date.Year.ToString("0000", CultureInfo.InvariantCulture),
                YearToken => (context.Date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                SiteToken => context.SiteCode,
                FieldToken field => ResolveField(field.Name, context.Fields),
                _ => throw new InvalidOperationException($"Unknown token {token}."),
            });
        }
        return builder.ToString();
    }

    /// <summary>The names of the fields the template uses, in order.</summary>
    public IReadOnlyList<string> FieldNames => _tokens.OfType<FieldToken>().Select(f => f.Name).Distinct().ToList();

    /// <summary>
    /// The fields in runs that sit side by side with nothing between them, in template order: in
    /// <c>{field:Application}{field:Type}{field:Equipment}-{field:Area}-…</c> the first three are one run, so a
    /// single code such as MR03 fills all three.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> FieldRuns()
    {
        var runs = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var token in _tokens)
        {
            if (token is FieldToken field)
            {
                current.Add(field.Name);
                continue;
            }
            if (current.Count > 0)
                runs.Add(current);
            current = [];
        }
        if (current.Count > 0)
            runs.Add(current);
        return runs;
    }

    /// <summary>
    /// A pattern matching every number this template can produce, for checking a file's name: list fields match
    /// only their codes, free-text fields anything, the counter any digits at least as wide as its zeros.
    /// </summary>
    public Regex NamePattern(IReadOnlyList<Contracts.NumberingField> fields)
    {
        var pattern = new StringBuilder("^");
        foreach (var token in _tokens)
        {
            pattern.Append(token switch
            {
                LiteralToken literal => Regex.Escape(literal.Text),
                SequenceToken seq => $"[0-9]{{{seq.Width},}}",
                CheckToken => "[0-9]",
                YearToken { FourDigit: true } => "[0-9]{4}",
                YearToken => "[0-9]{2}",
                SiteToken => "[A-Za-z0-9]+",
                FieldToken field => FieldPattern(fields.FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase))),
                _ => "",
            });
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// What one field's value looks like: one of its codes (longest first, so "10" isn't read as "1"), or text of its
    /// format and length (any text when it has neither).
    /// </summary>
    public static string FieldPattern(Contracts.NumberingField? field)
    {
        if (field?.Choices is { Count: > 0 } choices)
            return "(?:" + string.Join("|", choices.Select(c => c.Code).OrderByDescending(c => c.Length).Select(Regex.Escape)) + ")";
        var characters = field?.Format switch
        {
            Contracts.NumberingFormats.Letters => "[A-Za-z]",
            Contracts.NumberingFormats.Digits => "[0-9]",
            Contracts.NumberingFormats.LettersAndDigits => "[A-Za-z0-9]",
            _ => ".",
        };
        return (field?.MinLength, field?.MaxLength) switch
        {
            (null, null) => characters + "+?",
            (var min, null) => $"{characters}{{{min},}}?",
            (var min, var max) => $"{characters}{{{min ?? 1},{max}}}?",
        };
    }

    /// <summary>The template's parts in order, for showing and editing a scheme part by part (see <see cref="SchemeParts"/>).</summary>
    public IReadOnlyList<TemplatePart> Parts() => _tokens.Select(token => token switch
    {
        LiteralToken literal => new TemplatePart(TemplatePartKind.Text, Text: literal.Text),
        SequenceToken seq => new TemplatePart(TemplatePartKind.Counter, Width: seq.Width),
        CheckToken => new TemplatePart(TemplatePartKind.CheckDigit),
        YearToken { FourDigit: true } => new TemplatePart(TemplatePartKind.Year, Width: 4),
        YearToken => new TemplatePart(TemplatePartKind.Year, Width: 2),
        SiteToken => new TemplatePart(TemplatePartKind.Site),
        FieldToken field => new TemplatePart(TemplatePartKind.Field, Text: field.Name),
        _ => throw new InvalidOperationException($"Unknown token {token}."),
    }).ToList();

    /// <summary>
    /// What the counter counts within: the number's text before the counter when that text varies (a field, the site
    /// or the year comes before it), so each area, project or year has its own 001, 002…; otherwise empty, meaning one
    /// counter for the whole scheme.
    /// </summary>
    public string CounterScope(NumberContext context)
    {
        var before = _tokens.TakeWhile(t => t is not SequenceToken).ToList();
        if (!before.Any(t => t is FieldToken or SiteToken or YearToken))
            return "";
        var builder = new StringBuilder();
        foreach (var token in before)
        {
            builder.Append(token switch
            {
                LiteralToken literal => literal.Text,
                YearToken { FourDigit: true } => context.Date.Year.ToString("0000", CultureInfo.InvariantCulture),
                YearToken => (context.Date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                SiteToken => context.SiteCode,
                FieldToken field => ResolveField(field.Name, context.Fields),
                _ => "",
            });
        }
        return builder.ToString().ToUpperInvariant();
    }

    private static string ResolveField(string name, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Numbering field '{name}' is required.");
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Numbering field '{name}' contains characters not allowed in file names.");
        return value.Trim();
    }

    private static Token ParseToken(string body, string template)
    {
        if (body.StartsWith("seq:", StringComparison.Ordinal))
        {
            var pad = body[4..];
            if (pad.Length is < 1 or > 18 || pad.Any(c => c != '0'))
                throw new FormatException($"Invalid sequence token '{{{body}}}' in '{template}'; use e.g. {{seq:000000}}.");
            return new SequenceToken(pad.Length);
        }
        if (body.StartsWith("field:", StringComparison.Ordinal) && body.Length > 6)
            return new FieldToken(body[6..]);
        return body switch
        {
            "yyyy" => new YearToken(FourDigit: true),
            "yy" => new YearToken(FourDigit: false),
            "site" => new SiteToken(),
            "check" => new CheckToken(),
            _ => throw new FormatException($"Unknown token '{{{body}}}' in numbering template '{template}'."),
        };
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenPattern();

    private abstract record Token;
    private sealed record LiteralToken(string Text) : Token;
    private sealed record SequenceToken(int Width) : Token;
    private sealed record YearToken(bool FourDigit) : Token;
    private sealed record SiteToken : Token;
    private sealed record CheckToken : Token;
    private sealed record FieldToken(string Name) : Token;
}

public sealed record NumberContext(DateTimeOffset Date, string SiteCode, IReadOnlyDictionary<string, string> Fields)
{
    public static NumberContext For(string siteCode, IReadOnlyDictionary<string, string>? fields = null) =>
        new(DateTimeOffset.UtcNow, siteCode, fields ?? new Dictionary<string, string>());
}
