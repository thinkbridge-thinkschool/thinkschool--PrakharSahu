using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace QuotesApi.Security;

/// <summary>
/// Input limits enforced over <see cref="ReadOnlySpan{T}"/>, without allocating.
/// </summary>
/// <remarks>
/// <para>
/// Every method here takes a span and returns a verdict or writes into a caller-supplied buffer.
/// Nothing allocates on the validation path, and that is a security property rather than a
/// micro-optimisation.
/// </para>
/// <para>
/// <b>Why allocation matters when the input is hostile.</b> Validation runs on data an attacker
/// chose, before any limit has been applied — it is the first code to touch the request body. A
/// validator that allocates proportionally to its input gives the attacker a lever on the
/// server's memory: <c>text.Trim().ToLowerInvariant().Split(' ')</c> on a 10 MB string allocates
/// several copies of 10 MB, and a few hundred concurrent requests turn a validation routine into
/// the denial-of-service it was meant to prevent. Spans are views over memory that already
/// exists, so the cost of rejecting a large input is the same as rejecting a small one.
/// </para>
/// <para>
/// The limits themselves are the point; the span is how they are applied without the check
/// becoming the vulnerability.
/// </para>
/// </remarks>
public static class TextGuard
{
    /// <summary>Longest accepted quote text. Generous for a quotation, far under any DoS threshold.</summary>
    public const int MaxTextLength = 1_000;

    /// <summary>Longest accepted author name.</summary>
    public const int MaxAuthorLength = 120;

    /// <summary>Longest accepted email address. RFC 5321 puts the practical ceiling here.</summary>
    public const int MaxEmailLength = 254;

    // -------------------------------------------------------------------------------------------
    // SearchValues<char> — the memory primitive that makes an allow-list fast.
    //
    // Built once, at startup. Internally it picks a strategy from the set it was given: a bitmap
    // for a small ASCII set like this one, a probabilistic filter for larger sets. The result is
    // a vectorised IndexOfAnyExcept that examines several characters per instruction instead of
    // branching once per character.
    //
    // The naive alternative — `text.Any(c => allowed.Contains(c))` — allocates an enumerator and
    // a closure, and runs a delegate call per character. On a hot write path fed by untrusted
    // input, that difference is the difference between a validator and a bottleneck.
    // -------------------------------------------------------------------------------------------

    /// <summary>Characters permitted in quote text and author names.</summary>
    /// <remarks>
    /// An ALLOW-list, not a deny-list, and the direction is the whole point. A deny-list has to
    /// enumerate every dangerous character, and is wrong the moment somebody invents a new
    /// encoding or a downstream parser treats an innocuous byte specially. An allow-list is wrong
    /// only by being too strict, which surfaces as a user complaint rather than as an incident.
    /// </remarks>
    private static readonly SearchValues<char> AllowedText = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyz" +
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
        "0123456789" +
        " .,'\"-?()");

    /// <summary>Why a value was rejected. Deliberately coarse — see <see cref="Describe"/>.</summary>
    public enum Rejection
    {
        None = 0,
        Empty,
        TooLong,
        ControlCharacter,
        DisallowedCharacter
    }

    /// <summary>
    /// Validates a text field against its length and character limits. Allocates nothing.
    /// </summary>
    public static Rejection Validate(ReadOnlySpan<char> value, int maxLength)
    {
        // Length FIRST, before anything walks the input. Rejecting a 10 MB body should cost one
        // comparison, not a scan of ten million characters — otherwise the validator's own work
        // is the attack.
        if (value.Length > maxLength) return Rejection.TooLong;

        var trimmed = value.Trim();          // a re-slice, not a copy
        if (trimmed.IsEmpty) return Rejection.Empty;

        // Control characters are checked separately from the allow-list so the caller can tell
        // "you used a semicolon" from "you embedded a NUL", which are different conversations.
        foreach (var c in trimmed)
        {
            if (char.IsControl(c)) return Rejection.ControlCharacter;
        }

        // IndexOfAnyExcept returns the first position NOT in the set, or -1 when every character
        // is allowed. One vectorised pass.
        return trimmed.IndexOfAnyExcept(AllowedText) >= 0
            ? Rejection.DisallowedCharacter
            : Rejection.None;
    }

    /// <summary>
    /// A message safe to return to the caller.
    /// </summary>
    /// <remarks>
    /// Deliberately does not echo the offending input. Reflecting attacker-controlled text into a
    /// response is how a JSON API ends up as a reflected-XSS vector for whatever renders it, and
    /// it tells a prober exactly which byte tripped the filter — free feedback for building a
    /// payload that does not.
    /// </remarks>
    public static string Describe(Rejection rejection, string field, int maxLength) => rejection switch
    {
        Rejection.Empty => $"{field} is required.",
        Rejection.TooLong => $"{field} must be {maxLength} characters or fewer.",
        Rejection.ControlCharacter => $"{field} contains control characters.",
        Rejection.DisallowedCharacter =>
            $"{field} may contain letters, numbers, spaces and . , ' \" - ? ( ) only.",
        _ => string.Empty
    };

    /// <summary>
    /// Trims and collapses runs of whitespace into single spaces, writing into
    /// <paramref name="destination"/>. Returns false if the buffer is too small.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normalising before storage matters for more than tidiness: <c>"Ada  Lovelace"</c> and
    /// <c>"Ada Lovelace"</c> are different strings and the same author, and a uniqueness check
    /// that does not normalise can be bypassed with a second space.
    /// </para>
    /// <para>
    /// The caller supplies the buffer, which is what keeps this allocation-free — for anything
    /// under a kilobyte that is a <c>stackalloc</c> at the call site. Returning a new string here
    /// would allocate on every request and defeat the purpose of taking a span at all.
    /// </para>
    /// </remarks>
    public static bool TryNormalise(ReadOnlySpan<char> value, Span<char> destination, out int written)
    {
        written = 0;
        var trimmed = value.Trim();
        if (trimmed.Length > destination.Length) return false;

        var lastWasSpace = false;
        foreach (var c in trimmed)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastWasSpace) continue;        // collapse the run

            // Any whitespace becomes a plain space, so a tab or a non-breaking space cannot be
            // used to produce two values that look identical and compare differently.
            destination[written++] = isSpace ? ' ' : c;
            lastWasSpace = isSpace;
        }

        return true;
    }

    /// <summary>
    /// Compares two secrets in time independent of where they first differ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>a == b</c> on strings returns as soon as it finds a mismatch, so the time it takes
    /// leaks how many leading characters were correct. Given enough samples that turns guessing a
    /// 32-character token from 62^32 attempts into roughly 62 × 32 — a timing side channel, and a
    /// practical one over a local network.
    /// </para>
    /// <para>
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// always examines every byte. The length check before it is not a leak: the length of a token
    /// is not secret, and comparing different-length inputs cannot be constant-time anyway.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool FixedTimeEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length) return false;
        if (left.IsEmpty) return true;

        // Encode into stack buffers rather than calling Encoding.UTF8.GetBytes, which allocates
        // two arrays whose contents are the secret being compared - and which then sit in the heap
        // until a GC happens to zero them.
        var maxBytes = Encoding.UTF8.GetMaxByteCount(left.Length);
        if (maxBytes > 512) return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left.ToString()),
            Encoding.UTF8.GetBytes(right.ToString()));

        Span<byte> leftBytes = stackalloc byte[maxBytes];
        Span<byte> rightBytes = stackalloc byte[maxBytes];

        var leftWritten = Encoding.UTF8.GetBytes(left, leftBytes);
        var rightWritten = Encoding.UTF8.GetBytes(right, rightBytes);

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                leftBytes[..leftWritten], rightBytes[..rightWritten]);
        }
        finally
        {
            // Wipe the stack copies. The frame is reused by the next call on this thread, and
            // leaving a secret there is a needless window.
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
