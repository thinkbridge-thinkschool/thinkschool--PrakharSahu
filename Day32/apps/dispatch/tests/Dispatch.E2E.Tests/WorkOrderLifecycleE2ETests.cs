using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Dispatch.E2E.Tests;

/// <summary>
/// One end-to-end test: a work order from reported to invoiced, against the real binary.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a single test covering the whole money path, rather than a suite. The value of an
/// end-to-end test is that it exercises the real process, the real socket and the real database
/// at once; the cost is that it is slow and fails for reasons that have nothing to do with the
/// code. One of them is a smoke alarm. Ten of them are a reason people stop running the suite.
/// </para>
/// <para>
/// Everything narrower is covered below it: the aggregate's rules in the domain tests, the
/// cross-module flows in the application tests, the HTTP contract in the integration tests, the
/// booking race against a real database in the concurrency tests. If this test fails and none of
/// those do, the fault is in the wiring rather than the logic -- which is exactly the information
/// an end-to-end test is for.
/// </para>
/// </remarks>
public class WorkOrderLifecycleE2ETests(DispatchProcessFixture fixture)
    : IClassFixture<DispatchProcessFixture>
{
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [SkippableFact]
    public async Task A_work_order_goes_from_reported_to_invoiced_over_real_HTTP()
    {
        Skip.IfNot(DispatchProcessFixture.Configured, DispatchProcessFixture.SkipReason);

        var client = fixture.Client;
        var technician = Guid.NewGuid();

        // ---- 1. A customer reports a fault -------------------------------------------------
        var raise = await client.PostAsync("/api/work-orders", Json(new
        {
            customerId = Customer,
            summary = "Chiller unit is not holding temperature",
            line = "Unit 4, Example Industrial Estate",
            city = "Testville",
            postcode = "TV1 9ZZ"
        }));

        Assert.Equal(HttpStatusCode.Created, raise.StatusCode);
        var id = (await raise.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // ---- 2. The state machine refuses to skip triage ------------------------------------
        var premature = await client.PostAsync($"/api/work-orders/{id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);

        // ---- 3. Triage, which derives the SLA ----------------------------------------------
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/triage",
                Json(new { priority = "High" }))).StatusCode);

        // ---- 4. Book a technician ----------------------------------------------------------
        //
        // The window has to start in the future -- the aggregate refuses to schedule into the
        // past -- and work cannot start until it opens. This process has a real clock, so the
        // test genuinely waits. Three seconds is the price of proving the rule end to end.
        var start = DateTimeOffset.UtcNow.AddSeconds(3);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/schedule", Json(new
            {
                technicianId = technician,
                windowStart = start,
                windowEnd = start.AddHours(2)
            }))).StatusCode);

        var scheduled = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        Assert.Equal("Scheduled", scheduled.GetProperty("status").GetString());

        // ---- 5. Too early -------------------------------------------------------------------
        var tooEarly = await client.PostAsync($"/api/work-orders/{id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(
            "work_order.window_not_open",
            (await tooEarly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // ---- 6. The technician arrives ------------------------------------------------------
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/start", null)).StatusCode);

        // ---- 7. Work is done ----------------------------------------------------------------
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/labour", Json(new
            {
                technicianId = technician,
                minutes = 90,
                note = "replaced the thermostat"
            }))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/work-orders/{id}/complete", null)).StatusCode);

        var completed = await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(90, completed.GetProperty("totalLabourMinutes").GetInt32());
        Assert.True(completed.GetProperty("isBillable").GetBoolean());

        // ---- 8. Billing invoiced it ---------------------------------------------------------
        //
        // A different module, a different schema, reached only by an integration event -- and in
        // this process the whole chain ran for real: domain event, publisher, handler, second
        // DbContext, second commit.
        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/invoices");
        var invoice = invoices.EnumerateArray()
            .Single(i => i.GetProperty("workOrderId").GetGuid() == id);

        Assert.True(invoice.GetProperty("total").GetDecimal() > 0);
        Assert.Equal("GBP", invoice.GetProperty("currency").GetString());
    }
}
