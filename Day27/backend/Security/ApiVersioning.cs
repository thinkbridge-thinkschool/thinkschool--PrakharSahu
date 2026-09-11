namespace QuotesApi.Security;

/// <summary>
/// Where the API version lives, and the one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The version is in the URL path</b>, not in a header or a query string. All three are
/// legitimate, and the path wins here for reasons that are operational rather than aesthetic:
/// </para>
/// <list type="bullet">
///   <item>It is visible in an access log, so "who is still on v1" is a query rather than a
///   guess.</item>
///   <item>It is part of the cache key, so a proxy cannot serve a v1 response to a v2 client.</item>
///   <item>It survives a curl pasted into a chat window. A header version does not, and the
///   person who pastes it gets a different answer from the person who wrote it.</item>
/// </list>
/// <para>
/// The cost is honest: the URL is no longer stable across versions, which is the objection people
/// raise on REST-purity grounds. That objection assumes a URI identifies a resource forever; in
/// practice it identifies a resource <em>and a contract</em>, and pretending otherwise is what
/// makes breaking changes land on clients who did not ask for them.
/// </para>
/// <para>
/// <b>This is a breaking change to every existing caller</b>, and it is the right moment to make
/// it. Before today the surface was implicitly v1 forever with nowhere to put a v2; every path
/// moves once, now, so that the next change does not have to.
/// </para>
/// </remarks>
public static class ApiVersioning
{
    /// <summary>The current version segment.</summary>
    public const string Version = "v1";

    /// <summary>Route prefix every versioned endpoint sits under.</summary>
    public const string Prefix = "/api/" + Version;

    /// <summary>
    /// Path scope for the refresh cookie.
    /// </summary>
    /// <remarks>
    /// Scoped to the auth group rather than to <c>/</c>. A cookie with <c>Path=/</c> is attached
    /// to every request the browser makes to this origin, including ones that have no use for it
    /// — so a single reflected-content flaw anywhere on the origin can reach it. Narrowing the
    /// path means the refresh token is only ever sent to the endpoints that redeem it.
    /// </remarks>
    public const string AuthCookiePath = Prefix + "/auth";
}
