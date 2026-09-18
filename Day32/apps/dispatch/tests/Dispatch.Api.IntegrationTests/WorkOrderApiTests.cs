using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dispatch.Api.IntegrationTests;

/// <summary>
/// The HTTP surface, exercised through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// These sit between the unit tests and the end-to-end test, and they exist to catch what neither
/// can. A unit test calls <c>WorkOrderService</c> directly, so it never sees routing, model
/// binding, JSON serialisation, or the mapping from a domain error to a status code — all of which
/// are real code that can be wrong. The end-to-end test covers one path only, because it is slow.
/// </para>
/// <para>
/// Every assertion here is about the <b>contract</b>: the status code, the body shape, the header.
/// Domain rules belong in the domain tests, and duplicating them here would mean two places to
/// change when a rule changes.
/// </para>
/// </remarks>
[Collection(nameof(DispatchApiCollection))]
public class WorkOrderApiTests(DispatchApiFactory factory)
{
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Technician = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private HttpClient Client => factory.CreateClient();

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<Guid> RaiseAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Customer,
            summary = "Chiller unit is not holding temperature",
            line = "Unit 4, Example Industrial Estate",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    // ---------------------------------------------------------------------------------------
    // The contract
    // ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Health_reports_healthy()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Raising_an_order_returns_201_with_a_Location_that_resolves()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;

        var response = await client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Customer,
            summary = "Chiller unit is not holding temperature",
            line = "Unit 4, Example Industrial Estate",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        // A Location header nobody can follow is a lie the compiler cannot catch. The unit tests
        // never see this, because they never build a URL.
        var followed = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [SkippableFact]
    public async Task An_unknown_id_is_404_not_an_exception()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.GetAsync($"/api/work-orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_malformed_guid_is_404_from_the_route_constraint()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        // `{id:guid}` means the route does not match at all, so this never reaches the handler.
        // Asserting it pins the constraint: drop it and this becomes a 500 from a parse failure.
        var response = await Client.GetAsync("/api/work-orders/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_wrong_state_transition_is_409_not_400()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);

        // The request is well-formed; the state is wrong. 400 would tell the caller to fix their
        // request, which is wrong advice — nothing about the request needs fixing.
        var response = await client.PostAsync($"/api/work-orders/{id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("work_order.wrong_status.start", body.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task A_domain_validation_failure_is_400_with_a_machine_readable_code()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Customer,
            summary = "",                       // the rule being broken
            line = "Unit 4",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()));
    }

    [SkippableFact]
    public async Task Malformed_json_is_400_rather_than_500()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);

        var response = await Client.PostAsync(
            "/api/work-orders",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Enums_cross_the_wire_as_names_not_numbers()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);

        await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "High" }));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");

        // JsonStringEnumConverter is one line in Program.cs and silently changes the public
        // contract if removed: "Triaged" would become 1, and every client would break.
        Assert.Equal("Triaged", body.GetProperty("status").GetString());
        Assert.Equal("High", body.GetProperty("priority").GetString());
    }

    // ---------------------------------------------------------------------------------------
    // Cross-module behaviour, over HTTP
    // ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Triage_derives_an_SLA_due_date_from_the_priority()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);

        var raisedAt = factory.Clock.UtcNow;
        await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "Emergency" }));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        var dueBy = body.GetProperty("dueBy").GetDateTimeOffset();

        // Emergency is four hours. The due date is derived from when the order was RAISED, not
        // from when it was triaged -- an order that sat in a queue for an hour does not get that
        // hour back.
        //
        // Note the response carries `dueBy` but not `raisedAt`, so this reads the raise time from
        // the clock the test controls. Worth knowing: a client cannot currently see when an order
        // was raised, only when it is due.
        Assert.Equal(TimeSpan.FromHours(4), dueBy - raisedAt);
    }

    [SkippableFact]
    public async Task Scheduling_reaches_the_Scheduling_module_and_comes_back_confirmed()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);
        var technician = Guid.NewGuid();     // fresh, so a rerun cannot collide

        await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "High" }));

        var start = factory.Clock.UtcNow.AddHours(2);
        var response = await client.PostAsync($"/api/work-orders/{id}/schedule", Json(new
        {
            technicianId = technician,
            windowStart = start,
            windowEnd = start.AddHours(2)
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task A_double_booking_is_compensated_back_to_triage_over_HTTP()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var technician = Guid.NewGuid();
        var start = factory.Clock.UtcNow.AddHours(5);

        var first = await RaiseAsync(client);
        await client.PostAsync($"/api/work-orders/{first}/triage", Json(new { priority = "High" }));
        await client.PostAsync($"/api/work-orders/{first}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));

        var second = await RaiseAsync(client);
        await client.PostAsync($"/api/work-orders/{second}/triage", Json(new { priority = "High" }));
        await client.PostAsync($"/api/work-orders/{second}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));

        // The compensating saga runs inside the request here, because the bus is in-process.
        // That is worth knowing: under a broker this assertion would need to poll.
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{second}");

        Assert.Equal("Triaged", body.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Completing_an_order_produces_an_invoice_in_Billing()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);
        var technician = Guid.NewGuid();

        await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "High" }));

        // The window must start in the FUTURE -- scheduling into the past is a clock-skew bug and
        // the aggregate refuses it. So book ahead, then move the clock to when the technician
        // actually arrives. This is why the factory overrides IClock.
        var start = factory.Clock.UtcNow.AddHours(1);
        await client.PostAsync($"/api/work-orders/{id}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));

        factory.Clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/start", null)).StatusCode);

        await client.PostAsync($"/api/work-orders/{id}/labour", Json(new
        {
            technicianId = technician, minutes = 90, note = "replaced the thermostat"
        }));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/complete", null)).StatusCode);

        // Billing is a different module with a different database schema, reached only by an
        // integration event. This is the assertion that proves the wiring, end to end, in process.
        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/invoices");
        var mine = invoices.EnumerateArray()
            .Where(i => i.GetProperty("workOrderId").GetGuid() == id)
            .ToArray();

        Assert.Single(mine);
    }

    [SkippableFact]
    public async Task Completing_with_no_labour_logged_is_refused()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);
        var technician = Guid.NewGuid();

        await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "High" }));
        var start = factory.Clock.UtcNow.AddHours(1);
        await client.PostAsync($"/api/work-orders/{id}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));
        factory.Clock.Advance(TimeSpan.FromMinutes(90));
        await client.PostAsync($"/api/work-orders/{id}/start", null);

        // The invariant the aggregate boundary was drawn around, asserted at the edge.
        var response = await client.PostAsync($"/api/work-orders/{id}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task Cancelling_releases_the_slot_so_somebody_else_can_book_it()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var technician = Guid.NewGuid();
        var start = factory.Clock.UtcNow.AddHours(9);

        var first = await RaiseAsync(client);
        await client.PostAsync($"/api/work-orders/{first}/triage", Json(new { priority = "High" }));
        await client.PostAsync($"/api/work-orders/{first}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));

        await client.PostAsync($"/api/work-orders/{first}/cancel",
            Json(new { reason = "customer called back and cancelled" }));

        // The identical window, for the identical technician. This only succeeds if Scheduling
        // heard the cancellation and released the row -- and it is also the case the filtered
        // unique index has to permit.
        var second = await RaiseAsync(client);
        await client.PostAsync($"/api/work-orders/{second}/triage", Json(new { priority = "High" }));
        await client.PostAsync($"/api/work-orders/{second}/schedule", Json(new
        {
            technicianId = technician, windowStart = start, windowEnd = start.AddHours(2)
        }));

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{second}");
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Cancelling_without_a_reason_is_refused()
    {
        Skip.IfNot(DispatchApiFactory.Configured, DispatchApiFactory.SkipReason);
        var client = Client;
        var id = await RaiseAsync(client);

        var response = await client.PostAsync($"/api/work-orders/{id}/cancel", Json(new { reason = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
