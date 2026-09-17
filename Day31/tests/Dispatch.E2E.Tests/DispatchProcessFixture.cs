using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace Dispatch.E2E.Tests;

/// <summary>
/// Starts the real application as a separate operating-system process and talks to it over a
/// real socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this proves that the integration tests cannot.</b> <c>WebApplicationFactory</c> boots
/// the application in the test process and replaces the transport with an in-memory pipe. That
/// covers routing, binding and composition -- but the application never opens a port, never
/// serialises a byte onto a socket, and its <c>Main</c> never runs. Kestrel's own configuration,
/// the host's startup sequence, and anything that only exists in the published output are all
/// invisible there.
/// </para>
/// <para>
/// There is exactly <b>one</b> of these, deliberately. It is slow -- a process launch, a database
/// per run, and a real wait for a scheduled window to open -- and slow tests that multiply stop
/// being run. The pyramid's job is to make this one unnecessary for anything a cheaper test can
/// answer.
/// </para>
/// </remarks>
public sealed class DispatchProcessFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"dispatch_e2e_{Guid.NewGuid():N}";
    private Process? _process;

    public HttpClient Client { get; private set; } = null!;

    public static string? MasterConnectionString =>
        Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL");

    public static bool Configured => !string.IsNullOrWhiteSpace(MasterConnectionString);

    public const string SkipReason =
        "Set DISPATCH_TEST_SQL to a SQL Server connection string to run the end-to-end test.";

    private string ConnectionString =>
        new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = _databaseName }
            .ConnectionString;

    /// <summary>A port the OS says is free right now.</summary>
    /// <remarks>
    /// Asking the OS for port 0 and reading back what it assigned avoids the hard-coded-port
    /// collision that makes a suite fail only when something else happens to be listening.
    /// </remarks>
    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        await using (var connection = new SqlConnection(MasterConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        var dll = LocateApiAssembly();
        var port = FreePort();

        var startInfo = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ConnectionStrings__Dispatch"] = ConnectionString;
        // Migrations have not run against this database yet; the application applies them at
        // startup only if told to. See DISPATCH_MIGRATE_ON_STARTUP in Program.cs.
        startInfo.Environment["DISPATCH_MIGRATE_ON_STARTUP"] = "true";

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Could not start the Dispatch API process.");

        // Drained on background threads. A child process whose stdout buffer fills stops running,
        // and the symptom is a hang with no error -- the failure mode this whole codebase keeps
        // learning to distrust.
        _ = Task.Run(() => _process.StandardOutput.ReadToEnd());
        _ = Task.Run(() => _process.StandardError.ReadToEnd());

        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The Dispatch API exited during startup with code {_process.ExitCode}.");
            }

            try
            {
                var response = await Client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The Dispatch API never became healthy.");
    }

    /// <summary>
    /// Finds the published API assembly by walking up to the repository root.
    /// </summary>
    /// <remarks>
    /// The ProjectReference guarantees it has been built; it does not put it anywhere predictable
    /// relative to this test's output directory. Searching is more robust than a relative path
    /// that breaks the first time somebody changes a target framework or a configuration.
    /// </remarks>
    private static string LocateApiAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("Dispatch.sln*").Length == 0)
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the solution root.");
        }

        var candidates = Directory.GetFiles(
            Path.Combine(directory.FullName, "src", "Dispatch.Api"),
            "Dispatch.Api.dll",
            SearchOption.AllDirectories);

        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
               ?? throw new InvalidOperationException(
                   "Dispatch.Api.dll was not found. Build the solution first.");
    }

    public async Task DisposeAsync()
    {
        if (!Configured)
        {
            return;
        }

        Client?.Dispose();

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();

        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }
}
