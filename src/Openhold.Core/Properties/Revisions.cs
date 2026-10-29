namespace Openhold.Core.Properties;

/// <summary>
/// Suggests the next revision for the explicit Revise action (Openhold never revises on its own). Works for the
/// common schemes without configuration by stepping the last letter or number run: A → B, Z → AA, 1B → 1C,
/// 03 → 04, A.01 → A.02, P1 → P2. Letters that read like digits are skipped, as ASME Y14.35 recommends.
/// </summary>
public static class Revisions
{
    /// <summary>I, O, Q, S, X and Z: easily confused with 1, 0, 5 and 2 or with markup, so not used as revision letters.</summary>
    public static readonly IReadOnlySet<char> DefaultSkippedLetters = new HashSet<char>("IOQSXZ");

    /// <summary>The first revision when a file has none yet.</summary>
    public const string First = "A";

    public static string Next(string? current, IReadOnlySet<char>? skippedLetters = null)
    {
        skippedLetters ??= DefaultSkippedLetters;
        var text = current?.Trim() ?? "";
        if (text.Length == 0)
            return First;

        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1]))
            end--;
        if (end == 0)
            return text + First;

        var digits = char.IsAsciiDigit(text[end - 1]);
        var start = end;
        while (start > 0 && (digits ? char.IsAsciiDigit(text[start - 1]) : char.IsAsciiLetter(text[start - 1])))
            start--;

        var run = text[start..end];
        var next = digits ? NextNumber(run) : NextLetters(run, skippedLetters);
        return text[..start] + next + text[end..];
    }

    private static string NextNumber(string run)
    {
        var value = (long.Parse(run, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value.PadLeft(run.Length, '0'); // keep leading zeros: 09 → 10, 001 → 002
    }

    private static string NextLetters(string run, IReadOnlySet<char> skipped)
    {
        var upper = char.IsUpper(run[^1]);
        var letters = run.ToUpperInvariant().ToCharArray();
        for (var i = letters.Length - 1; i >= 0; i--)
        {
            var next = Step(letters[i], skipped);
            if (next is { } letter)
            {
                letters[i] = letter;
                return Case(new string(letters), upper);
            }
            letters[i] = Step('@', skipped)!.Value; // past the last letter: restart at the first and carry into the letter before
        }
        return Case(Step('@', skipped)!.Value + new string(letters), upper); // all wrapped: Y → AA
    }

    /// <summary>The next allowed letter after <paramref name="letter"/>, or null past the end of the alphabet.</summary>
    private static char? Step(char letter, IReadOnlySet<char> skipped)
    {
        for (var c = (char)(letter + 1); c <= 'Z'; c++)
        {
            if (!skipped.Contains(c))
                return c;
        }
        return null;
    }

    private static string Case(string letters, bool upper) => upper ? letters : letters.ToLowerInvariant();
}
