namespace Openhold.Core.Numbering;

/// <summary>
/// The Damm check digit. Unlike Luhn it catches every single-digit typo and every swap of two neighbouring digits
/// (including 09/90), and always gives a digit (never an X), so part numbers stay digits only.
/// </summary>
public static class Damm
{
    private static readonly byte[,] Table =
    {
        { 0, 3, 1, 7, 5, 9, 8, 6, 4, 2 },
        { 7, 0, 9, 2, 1, 5, 4, 8, 6, 3 },
        { 4, 2, 0, 6, 8, 7, 1, 3, 5, 9 },
        { 1, 7, 5, 0, 9, 8, 3, 4, 2, 6 },
        { 6, 1, 2, 3, 0, 4, 5, 9, 7, 8 },
        { 3, 6, 7, 4, 2, 0, 9, 5, 8, 1 },
        { 5, 8, 6, 9, 7, 2, 0, 1, 3, 4 },
        { 8, 9, 4, 5, 3, 6, 2, 0, 1, 7 },
        { 9, 4, 3, 8, 6, 1, 7, 2, 0, 5 },
        { 2, 5, 8, 1, 4, 3, 6, 7, 9, 0 },
    };

    /// <summary>The check digit for a string of digits.</summary>
    public static int CheckDigit(string digits)
    {
        var interim = 0;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                throw new ArgumentException($"'{digits}' isn't all digits.", nameof(digits));
            interim = Table[interim, c - '0'];
        }
        return interim;
    }

    /// <summary>True when the last digit is the right check digit for the ones before it.</summary>
    public static bool IsValid(string digitsWithCheck) =>
        digitsWithCheck.Length > 1 && digitsWithCheck.All(char.IsAsciiDigit) && CheckDigit(digitsWithCheck) == 0;
}
