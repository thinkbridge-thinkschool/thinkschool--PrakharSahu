using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// =================================================================================================
// A latency harness for the hot path.
//
// Deliberately small and dependency-free rather than a load-testing framework. What is being
// measured is the SERVER's per-request cost, and a heavyweight client adds its own scheduling
// noise to every sample. This sends requests, records how long each took, and reports percentiles.
//
// It is not a load test: no ramp, no think time, no sustained pressure. It answers "what does one
// request of this shape cost", which is the question a p99 target is about.
// =================================================================================================

var baseUrl = Environment.GetEnvironmentVariable("PERF_BASE_URL") ?? "http://127.0.0.1:5399";
var label = args.Length > 0 ? args[0] : "run";
var samples = int.TryParse(Environment.GetEnvironmentVariable("PERF_SAMPLES"), out var n) ? n : 2000;
var concurrency = int.TryParse(Environment.GetEnvironmentVariable("PERF_CONCURRENCY"), out var c) ? c : 8;

using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

static StringContent Json(object body) =>
    new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

// ---- Seed one work order, and give it labour ---------------------------------------------------
//
// Labour matters: it is an owned collection, so the number of rows the read materialises -- and
// the number of change-tracking entries EF builds -- scales with it. A work order with no labour
// would flatter the measurement.
Console.WriteLine($"[{label}] seeding...");

var customer = Guid.NewGuid();
var technician = Guid.NewGuid();

var raise = await client.PostAsync("/api/work-orders", Json(new
{
    customerId = customer,
    summary = "Chiller unit is not holding temperature",
    line = "Unit 4, Example Industrial Estate",
    city = "Testville",
    postcode = "TV1 9ZZ"
}));
raise.EnsureSuccessStatusCode();
var id = (await raise.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

await client.PostAsync($"/api/work-orders/{id}/triage", Json(new { priority = "High" }));

var start = DateTimeOffset.UtcNow.AddSeconds(2);
await client.PostAsync($"/api/work-orders/{id}/schedule", Json(new
{
    technicianId = technician,
    windowStart = start,
    windowEnd = start.AddHours(4)
}));

await Task.Delay(TimeSpan.FromSeconds(3));
await client.PostAsync($"/api/work-orders/{id}/start", null);

const int labourEntries = 8;
for (var i = 0; i < labourEntries; i++)
{
    await client.PostAsync($"/api/work-orders/{id}/labour", Json(new
    {
        technicianId = technician,
        minutes = 15,
        note = $"diagnostic pass {i + 1}"
    }));
}

var seeded = await client.GetAsync($"/api/work-orders/{id}");
seeded.EnsureSuccessStatusCode();
Console.WriteLine($"[{label}] seeded {id} with {labourEntries} labour entries");

// ---- Warm up -----------------------------------------------------------------------------------
//
// Not optional and not a way to make the numbers look better. The first requests pay for JIT, the
// EF model cache, the SQL query plan and the connection pool filling. Including them would report
// a p99 that describes the first second of a process's life rather than its steady state.
Console.WriteLine($"[{label}] warming up...");
for (var i = 0; i < 300; i++)
{
    await client.GetAsync($"/api/work-orders/{id}");
}

// ---- Measure -----------------------------------------------------------------------------------
//
// Three repetitions, and the MEDIAN is the answer.
//
// A p99 from a single run is the 1980th of 2000 samples -- one request, whose cost can be set by
// a garbage collection or a SQL Server checkpoint that had nothing to do with the code. The first
// version of this harness reported exactly that and made a change look like a regression it was
// not. Repeating and taking the median costs three seconds and removes the question.
const int repetitions = 3;

var runs = new List<double[]>();

for (var repetition = 1; repetition <= repetitions; repetition++)
{
    var timings = new double[samples];
    var index = -1;

    var workers = Enumerable.Range(0, concurrency).Select(async _ =>
    {
        while (true)
        {
            var slot = Interlocked.Increment(ref index);
            if (slot >= samples)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync($"/api/work-orders/{id}");
            await response.Content.ReadAsByteArrayAsync();   // a real client reads the body
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Hot path returned {(int)response.StatusCode}.");
            }

            timings[slot] = stopwatch.Elapsed.TotalMilliseconds;
        }
    });

    var wall = Stopwatch.StartNew();
    await Task.WhenAll(workers);
    wall.Stop();

    Array.Sort(timings);
    runs.Add(timings);

    Console.WriteLine(
        $"    run {repetition}:  p50 {Percentile(timings, 50),6:F2}   p95 {Percentile(timings, 95),6:F2}   "
        + $"p99 {Percentile(timings, 99),6:F2}   rps {samples / wall.Elapsed.TotalSeconds,6:F0}");
}

// Nearest-rank: a percentile is a real observed sample, not an interpolation between two. With
// 2000 samples the p99 is the 1980th slowest request -- one that actually happened.
static double Percentile(double[] sorted, double p)
{
    var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
}

static double Median(IEnumerable<double> values)
{
    var ordered = values.OrderBy(v => v).ToArray();
    return ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
}

Console.WriteLine();
Console.WriteLine($"  GET /api/work-orders/{{id}}   [{label}]");
Console.WriteLine($"  {samples} samples x {repetitions} runs   concurrency {concurrency}   labour rows {labourEntries}");
Console.WriteLine();
Console.WriteLine($"    p50   {Median(runs.Select(r => Percentile(r, 50))),8:F2} ms   (median of {repetitions} runs)");
Console.WriteLine($"    p95   {Median(runs.Select(r => Percentile(r, 95))),8:F2} ms");
Console.WriteLine($"    p99   {Median(runs.Select(r => Percentile(r, 99))),8:F2} ms");
Console.WriteLine($"    mean  {Median(runs.Select(r => r.Average())),8:F2} ms");
Console.WriteLine();
