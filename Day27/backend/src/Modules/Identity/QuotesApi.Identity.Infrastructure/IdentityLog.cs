namespace QuotesApi.Identity.Infrastructure;

/// <summary>
/// A logging category for this module's endpoints. Carries no behaviour.
/// </summary>
/// <remarks>
/// <c>ILogger&lt;T&gt;</c> derives its category name from <c>T</c>, and the endpoint classes are
/// static so they cannot be used as a type argument. Before the split every endpoint logged as
/// <c>ILogger&lt;Program&gt;</c> — one category for the whole application, which meant a log
/// filter could not single out authentication without also silencing everything else.
/// Now each module logs under its own name.
/// </remarks>
public sealed class IdentityLog;
