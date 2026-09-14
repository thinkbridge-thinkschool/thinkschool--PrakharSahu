namespace QuotesApi.SharedKernel.Security;

/// <summary>
/// The names of the rate-limit policies, and only the names.
/// </summary>
/// <remarks>
/// <para>
/// The policies themselves are configured by the host (<c>ApiHardening.AddApiHardening</c>),
/// because permit counts and window sizes are a deployment decision. But an endpoint has to
/// name the policy it wants, and the Identity module's auth group genuinely needs the tight
/// one — login is where credential stuffing lands.
/// </para>
/// <para>
/// Two constants in the shared kernel is the smallest thing that can cross that gap. The
/// alternative, letting a module reference the host, would invert the composition arrow and
/// make every module depend on the thing that is supposed to depend on them.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>Credential endpoints. Deliberately tight.</summary>
    public const string Auth = "auth";

    /// <summary>Everything else. Loose enough that normal use never notices.</summary>
    public const string Global = "global";
}
