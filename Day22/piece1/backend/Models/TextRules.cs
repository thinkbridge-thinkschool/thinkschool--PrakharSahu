using System.Linq;
using System.Text.RegularExpressions;

namespace QuotesApi.Models;

/// <summary>
/// The "no special characters" rule shared by the two free-text fields on a quote.
///
/// Written as an allow-list rather than a block-list: a block-list of "!@#$" only stops the
/// four characters somebody thought of, while an allow-list stops everything nobody thought
/// of too (backticks, angle brackets, emoji, control characters).
/// </summary>
public static partial class TextRules
{
    /// <summary>Punctuation that ordinary prose needs, shown to the user in error messages.</summary>
    public const string AllowedPunctuationHint = ". , ' \" - ? ( )";

    /// <summary>
    /// Anything that is not a letter, a digit, whitespace, or one of the punctuation marks above.
    /// <c>\p{L}</c> and <c>\p{N}</c> are Unicode categories, so accented names such as "Brontë"
    /// pass while "!@#$" does not. Note that '!' is deliberately outside the allowed set.
    /// </summary>
    [GeneratedRegex(@"[^\p{L}\p{N}\s.,'""\-?()]", RegexOptions.CultureInvariant)]
    private static partial Regex DisallowedPattern();

    /// <summary>
    /// Returns null when the value is clean, otherwise a sentence naming the offending
    /// characters. Distinct and in first-seen order, so "a###b" reports '#' once.
    /// </summary>
    public static string? DescribeViolation(string value, string fieldLabel)
    {
        var offenders = DisallowedPattern()
            .Matches(value)
            .Select(match => match.Value)
            .Distinct()
            .ToArray();

        if (offenders.Length == 0)
            return null;

        return $"{fieldLabel} cannot contain {string.Join(' ', offenders)}. "
             + $"Use letters, numbers, spaces and {AllowedPunctuationHint} only.";
    }
}
