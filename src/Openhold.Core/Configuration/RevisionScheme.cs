using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Openhold.Core.Properties;

namespace Openhold.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<RevisionPartKind>))]
public enum RevisionPartKind
{
    Letters,
    Number,
}

/// <summary>
/// One part of a revision, e.g. the "1" or the "A" in "1A". <see cref="Start"/> is the first value and sets
/// the width of numbers ("01" gives 01, 02 … 10). <see cref="Skip"/> lists letters never used (e.g. "IOQSXZ").
/// <see cref="Separator"/> is written before the part, e.g. "." in "A.01".
/// </summary>
public sealed record RevisionPart(RevisionPartKind Kind, string Start, string Skip = "", string Separator = "");

/// <summary>
/// A revision scheme an administrator defines, e.g. letters (A, B, C…), numbers (01, 02…), or a number and a
/// letter (1A, 1B … 2A). Revising steps one part and resets the parts after it: the last part is a minor
/// revision (1A → 1B), an earlier part a major one (1B → 2A). Openhold never revises on its own; this only
/// suggests the next value when a person chooses Revise.
/// </summary>
public sealed record RevisionScheme(string Id, string Name, IReadOnlyList<RevisionPart> Parts)
{
    /// <summary>The first revision, e.g. "A" or "1A".</summary>
    [JsonIgnore]
    public string First => string.Concat(Parts.Select(p => p.Separator + Normalize(p.Start, p)));

    /// <summary>
    /// The revision after <paramref name="current"/>, stepping part <paramref name="level"/> (default: the last,
    /// a minor revision) and resetting the parts after it. A blank revision gets <see cref="First"/>. A value that
    /// doesn't fit the scheme, e.g. one from before the scheme was set up, is stepped by its last letter or number run.
    /// </summary>
    public string Next(string? current, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(current))
            return First;
        var step = level ?? Parts.Count - 1;
        if (step < 0 || step >= Parts.Count)
            throw new ArgumentOutOfRangeException(nameof(level), $"This scheme has {Parts.Count} part(s).");
        if (!TryParse(current.Trim(), out var values))
            return Revisions.Next(current, SkipSet(Parts[^1]));

        var builder = new StringBuilder();
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            var value = i < step ? values[i] : i == step ? StepPart(values[i], part) : Normalize(part.Start, part);
            builder.Append(part.Separator).Append(value);
        }
        return builder.ToString();
    }

    /// <summary>Splits a revision into its parts' values; false if it doesn't fit the scheme.</summary>
    public bool TryParse(string revision, out string[] values)
    {
        values = new string[Parts.Count];
        var position = 0;
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            if (!revision.AsSpan(position).StartsWith(part.Separator, StringComparison.OrdinalIgnoreCase))
                return false;
            position += part.Separator.Length;
            var start = position;
            while (position < revision.Length && (part.Kind == RevisionPartKind.Number ? char.IsAsciiDigit(revision[position]) : char.IsAsciiLetter(revision[position])))
                position++;
            if (position == start)
                return false;
            values[i] = revision[start..position];
        }
        return position == revision.Length;
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
            yield return "it needs an id and a name.";
        if (Parts.Count == 0)
            yield return "it needs at least one part.";
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            var valid = part.Kind == RevisionPartKind.Number
                ? part.Start.Length is > 0 and <= 9 && part.Start.All(char.IsAsciiDigit)
                : part.Start.Length > 0 && part.Start.All(char.IsAsciiLetter) && !part.Start.ToUpperInvariant().Any(SkipSet(part).Contains);
            if (!valid)
                yield return $"part {i + 1} starts at '{part.Start}', which isn't a valid {(part.Kind == RevisionPartKind.Number ? "number" : "letter")}.";
            if (i > 0 && part.Separator.Length == 0 && part.Kind == Parts[i - 1].Kind)
                yield return $"parts {i} and {i + 1} are both {part.Kind.ToString().ToLowerInvariant()}, so they need a separator between them.";
            if (part.Separator.Any(c => char.IsLetterOrDigit(c) || Path.GetInvalidFileNameChars().Contains(c)))
                yield return $"part {i + 1}'s separator '{part.Separator}' must be punctuation such as '.' or '-'.";
        }
    }

    private static string StepPart(string value, RevisionPart part)
    {
        if (part.Kind == RevisionPartKind.Number)
        {
            var next = (long.Parse(value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            return next.PadLeft(Math.Max(value.Length, part.Start.Length), '0');
        }
        return Revisions.Next(value, SkipSet(part));
    }

    private static string Normalize(string start, RevisionPart part) =>
        part.Kind == RevisionPartKind.Letters ? start.ToUpperInvariant() : start;

    private static HashSet<char> SkipSet(RevisionPart part) => [.. part.Skip.ToUpperInvariant()];
}
