using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dispatch.Api.IntegrationTests;

/// <summary>
/// The security controls, asserted rather than audited.
/// </summary>
/// <remarks>
/// <para>
/// Day 27 proved these with a shell script run by hand. Run by hand means run once: the value of
/// a control is that it is still there in six months, and nothing in a script that nobody runs
/// enforces that. These execute on every push, in CI, alongside everything else.
/// </para>
/// <para>
/// Each test names the finding it came from. Four of them failed before Day 31.
/// </para>
/// </remarks>
[Collection(nameof(DispatchApiCollection))]
public class SecurityTests(DispatchApiFactory factory)
{
    private HttpClient Client => factory.CreateClient();

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [SkippableTheory]
    [InlineData("X-Content-Type-Options")]
    [InlineData("X-Frame-Options")]
    [InlineData("Content-Security-Policy")]
    [InlineData("Referrer-Policy")]
    [InlineData("Cross-Origin-Resource-Policy")]
    public async Task Security_headers_are_present(string header)
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.GetAsync("/health");

        Assert.True(
            response.Headers.Contains(header),
            $"{header} is missing. It was missing on every response before Day 31.");
    }

    [SkippableFact]
    public async Task Security_headers_are_present_on_error_responses_too()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        // The reason UseSecurityHeaders is registered first. A 404 is exactly the kind of
        // response that misses headers added late in the pipeline, and exactly the kind an
        // attacker is probing for.
        var response = await Client.GetAsync($"/api/work-orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [SkippableFact]
    public async Task The_server_banner_is_not_advertised()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.GetAsync("/health");

        Assert.False(
            response.Headers.Contains("Server"),
            "The Server header names the stack, which narrows the set of CVEs worth trying.");
    }

    [SkippableFact]
    public async Task An_oversized_summary_is_refused_by_the_domain_not_by_the_database()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        // The Day 31 finding. The Summary column is nvarchar(500) and the domain had no length
        // check, so anything longer sailed through validation and blew up at SQL Server -- a 500
        // that told the caller the server was broken when their request was simply too big.
        var response = await Client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Guid.NewGuid(),
            summary = new string('x', 5_000),
            line = "Unit 4",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("work_order.summary_too_long", body.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task An_oversized_address_line_is_a_400_rather_than_a_500()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        // `address.line_too_long` was never classified in the status-code mapping, so it hit the
        // deliberately-fatal default and surfaced as a 500. Found by the security audit.
        var response = await Client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Guid.NewGuid(),
            summary = "Chiller unit is not holding temperature",
            line = new string('x', 5_000),
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_incomplete_address_is_a_400_rather_than_a_500()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Guid.NewGuid(),
            summary = "Chiller unit is not holding temperature",
            line = "",
            city = "",
            postcode = ""
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_body_past_the_Kestrel_limit_is_refused()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        // 1 MB against a 64 KB cap. Before Day 31 the limit was Kestrel's 30 MB default, so this
        // was read in full, allocated, and only then rejected by validation -- a cheap way to
        // make the server do expensive work.
        var response = await Client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Guid.NewGuid(),
            summary = new string('x', 1_000_000),
            line = "Unit 4",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        // 413 when Kestrel refuses it outright, 400 when model binding gives up first. Either is
        // the correct answer; a 500 is not, and neither is a 201.
        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"Expected the oversized body to be refused, got {(int)response.StatusCode}.");
    }
}
