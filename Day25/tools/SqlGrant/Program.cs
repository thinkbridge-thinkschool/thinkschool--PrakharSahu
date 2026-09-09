using Microsoft.Data.SqlClient;

// =============================================================================================
// Grant a managed identity a database user, and prove which principal is connected.
//
//   dotnet run --project tools/SqlGrant -- <sql-fqdn> <database> <identity-name> <client-id>
//
// ---------------------------------------------------------------------------------------------
// WHY THIS TOOL EXISTS
//
// Everything else about the API-to-SQL path is infrastructure: the server refuses passwords, the
// app carries an identity, the identity holds a token. None of that lets the app read a row.
//
// A login (server level) and a user (database level) are different objects, and only the first
// is reachable from ARM. The second requires a statement executed INSIDE the database:
//
//   CREATE USER [<identity>] FROM EXTERNAL PROVIDER;
//
// Day 24 stopped here, because the documented way to run it needs sqlcmd and sqlcmd was not
// installed. Requiring a separate install for one statement is a poor dependency on a machine
// that already has the .NET SDK, so this does the same job with no new prerequisites.
//
// ---------------------------------------------------------------------------------------------
// THE PART THAT IS NOT OBVIOUS: `FROM EXTERNAL PROVIDER` OFTEN CANNOT WORK
//
// That syntax asks the SQL server to look the name up in Entra on your behalf. To do that the
// server needs its own identity plus the Directory Readers role, which is a tenant-wide grant a
// subscription owner frequently cannot make - and the failure is a permissions error that names
// SQL rather than the directory.
//
// The alternative needs no directory access at all. A managed identity's SQL "SID" is simply its
// CLIENT ID as a byte array, so the user can be created from a value already in hand:
//
//   CREATE USER [<identity>] WITH SID = 0x<client-id-bytes>, TYPE = E;
//
// `TYPE = E` means "external user". The two forms produce an identical user; the second just does
// not ask the server to resolve a name it may not be allowed to resolve. This tool tries the
// readable form first and falls back, reporting which one worked rather than hiding it.
// =============================================================================================

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: SqlGrant <sql-fqdn> <database> <identity-name> <identity-client-id>");
    return 2;
}

var (fqdn, database, identityName, clientId) = (args[0], args[1], args[2], args[3]);

if (!Guid.TryParse(clientId, out var clientGuid))
{
    Console.Error.WriteLine($"'{clientId}' is not a GUID. Expected the identity's CLIENT id.");
    return 2;
}

// Identity names come from Bicep and are known-safe, but this runs T-SQL built by string
// concatenation, so the one character that could break out of a bracketed identifier is
// neutralised the way T-SQL expects - by doubling it.
var safeName = identityName.Replace("]", "]]");

// Little-endian GUID bytes are exactly what SQL Server expects for TYPE = E. Guid.ToByteArray()
// already produces that layout, which is why no reordering happens here.
var sid = "0x" + Convert.ToHexString(clientGuid.ToByteArray());

// `Authentication=Active Directory Default` runs the DefaultAzureCredential chain, which picks up
// the signed-in Azure CLI session. No password is passed, and there is none to pass - the server
// has azureADOnlyAuthentication enabled.
var connectionString = new SqlConnectionStringBuilder
{
    DataSource = $"tcp:{fqdn},1433",
    InitialCatalog = database,
    Encrypt = SqlConnectionEncryptOption.Mandatory,
    TrustServerCertificate = false,
    ConnectTimeout = 120,          // a serverless database may be resuming from auto-pause
    Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault
}.ConnectionString;

Console.WriteLine($"Connecting to {fqdn}/{database} as the signed-in Entra principal...");

await using var connection = new SqlConnection(connectionString);

try
{
    await connection.OpenAsync();
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"\nCould not connect: {ex.Message}\n");

    // Three distinct causes produce errors that all read like an authentication failure. Naming
    // which one this is saves the next person the same twenty minutes.
    if (ex.Message.Contains("is not allowed to access the server"))
    {
        Console.Error.WriteLine("This is a FIREWALL refusal, not an auth failure - the connection was");
        Console.Error.WriteLine("rejected before credentials were considered. The template admits Azure");
        Console.Error.WriteLine("services only; scripts/grant-sql.sh opens a temporary rule for this");
        Console.Error.WriteLine("machine's IP and removes it afterwards. Run the grant through that script.");
    }
    else if (ex.Message.Contains("Login failed"))
    {
        Console.Error.WriteLine("The connection reached the server and the principal was refused, so the");
        Console.Error.WriteLine("signed-in identity is probably not this server's Entra administrator.");
    }
    else
    {
        Console.Error.WriteLine("A serverless database may still be resuming from auto-pause; retrying");
        Console.Error.WriteLine("usually resolves that.");
    }

    return 1;
}

// Prove who we are before changing anything. If this says something unexpected, every statement
// afterwards would apply to the wrong database or as the wrong principal.
await using (var who = new SqlCommand("SELECT SUSER_SNAME(), DB_NAME();", connection))
await using (var reader = await who.ExecuteReaderAsync())
{
    if (await reader.ReadAsync())
    {
        Console.WriteLine($"  connected as : {reader.GetString(0)}");
        Console.WriteLine($"  database     : {reader.GetString(1)}");
    }
}

Console.WriteLine($"\nGranting [{identityName}]...");

// Idempotent: dropping first means re-running after a redeployment does not fail on an existing
// user, and a user with a stale SID (the identity was recreated) is replaced rather than left
// silently pointing at a principal that no longer exists.
var drop = $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @n) DROP USER [{safeName}];";

var roles = string.Join(" ", new[]
{
    $"ALTER ROLE db_datareader ADD MEMBER [{safeName}];",
    $"ALTER ROLE db_datawriter ADD MEMBER [{safeName}];",
    $"ALTER ROLE db_ddladmin ADD MEMBER [{safeName}];"
});

async Task RunAsync(string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    if (sql.Contains("@n")) command.Parameters.AddWithValue("@n", identityName);
    await command.ExecuteNonQueryAsync();
}

try
{
    await RunAsync(drop);

    string method;
    try
    {
        // The readable form. Needs the server to be allowed to read the directory.
        await RunAsync($"CREATE USER [{safeName}] FROM EXTERNAL PROVIDER;");
        method = "FROM EXTERNAL PROVIDER";
    }
    catch (SqlException ex)
    {
        Console.WriteLine($"  FROM EXTERNAL PROVIDER refused ({ex.Number}): {ex.Message.Split('\n')[0]}");
        Console.WriteLine("  falling back to WITH SID, which needs no directory access.");

        await RunAsync($"CREATE USER [{safeName}] WITH SID = {sid}, TYPE = E;");
        method = "WITH SID";
    }

    await RunAsync(roles);

    Console.WriteLine($"  created via  : {method}");
    Console.WriteLine("  roles        : db_datareader, db_datawriter, db_ddladmin");
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"\nGrant failed: {ex.Message}");
    return 1;
}

// Read the result back out of the database rather than trusting that the statements succeeded.
// A grant that "ran without error" and produced no principal is the failure worth catching.
await using (var verify = new SqlCommand(
    """
    SELECT p.name, p.type_desc, CONVERT(varchar(100), p.sid, 1),
           STRING_AGG(r.name, ', ')
    FROM sys.database_principals p
    LEFT JOIN sys.database_role_members m ON m.member_principal_id = p.principal_id
    LEFT JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
    WHERE p.name = @n
    GROUP BY p.name, p.type_desc, CONVERT(varchar(100), p.sid, 1);
    """, connection))
{
    verify.Parameters.AddWithValue("@n", identityName);
    await using var reader = await verify.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        Console.Error.WriteLine("\nThe user does not exist after the grant. Nothing was actually applied.");
        return 1;
    }

    Console.WriteLine("\n=== Verified in sys.database_principals ===");
    Console.WriteLine($"  name  : {reader.GetString(0)}");
    Console.WriteLine($"  type  : {reader.GetString(1)}  (EXTERNAL_USER means an Entra principal)");
    Console.WriteLine($"  sid   : {reader.GetString(2)}");
    Console.WriteLine($"  roles : {(reader.IsDBNull(3) ? "<none>" : reader.GetString(3))}");

    var actualSid = reader.GetString(2);
    if (!actualSid.Equals(sid, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"\n  NOTE: the stored SID differs from the client id bytes ({sid}).");
        Console.WriteLine("  That is expected when FROM EXTERNAL PROVIDER resolved the name itself.");
    }
}

Console.WriteLine("\nDone. The managed identity can now read and write this database with no password.");
return 0;
