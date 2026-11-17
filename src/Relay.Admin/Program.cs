using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.Abstractions;
using Relay.Domain.Entities;
using Relay.Infrastructure;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__RelayDatabase")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings:RelayDatabase")
    ?? "Server=localhost,1433;Database=RelayDb;User Id=sa;Password=Your_Strong_Password123!;TrustServerCertificate=True;MultipleActiveResultSets=True;";

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:RelayDatabase"] = connectionString
    })
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddInfrastructure(config);
services.AddSingleton(TimeProvider.System);

var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    var command = args[0].ToLowerInvariant();
    var subcommand = args.Length > 1 ? args[1].ToLowerInvariant() : "";

    switch (command)
    {
        case "migrate":
            return await RunMigrateAsync(dbContext);

        case "bootstrap-users":
            return await RunBootstrapUsersAsync(dbContext, args[1..]);

        case "tenant":
            return subcommand switch
            {
                "create" => await RunTenantCreateAsync(dbContext, args[2..]),
                "list" => await RunTenantListAsync(dbContext),
                _ => PrintUnknownCommand("tenant", subcommand)
            };

        case "apikey":
            return subcommand switch
            {
                "create" => await RunApiKeyCreateAsync(dbContext, hasher, args[2..]),
                "list" => await RunApiKeyListAsync(dbContext, args[2..]),
                "revoke" => await RunApiKeyRevokeAsync(dbContext, args[2..]),
                _ => PrintUnknownCommand("apikey", subcommand)
            };

        default:
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown command: {command}");
            Console.ResetColor();
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {ex.Message}");
    Console.ResetColor();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("================================================================================");
    Console.WriteLine("Relay Production Administrative Tool (relay-admin)");
    Console.WriteLine("================================================================================");
    Console.WriteLine("Usage:");
    Console.WriteLine("  relay-admin migrate");
    Console.WriteLine("  relay-admin bootstrap-users [--api-identity <name>] [--worker-identity <name>] [--migration-identity <name>]");
    Console.WriteLine("  relay-admin tenant create --name <name> [--slug <slug>]");
    Console.WriteLine("  relay-admin tenant list");
    Console.WriteLine("  relay-admin apikey create --tenant-id <id> --name <name> [--expires-in-days <days>]");
    Console.WriteLine("  relay-admin apikey list --tenant-id <id>");
    Console.WriteLine("  relay-admin apikey revoke --prefix <prefix>");
    Console.WriteLine("================================================================================");
}

static int PrintUnknownCommand(string area, string sub)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Unknown {area} action: '{sub}'");
    Console.ResetColor();
    PrintUsage();
    return 1;
}

static async Task<int> RunMigrateAsync(RelayDbContext dbContext)
{
    Console.WriteLine("Applying pending Entity Framework Core migrations...");
    await dbContext.Database.MigrateAsync();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Database migrations successfully applied.");
    Console.ResetColor();
    return 0;
}

static async Task<int> RunBootstrapUsersAsync(RelayDbContext dbContext, string[] args)
{
    string apiIdentity = "id-relay-api";
    string workerIdentity = "id-relay-worker";
    string migrationIdentity = "id-relay-migration";

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--api-identity" && i + 1 < args.Length) apiIdentity = args[++i];
        else if (args[i] == "--worker-identity" && i + 1 < args.Length) workerIdentity = args[++i];
        else if (args[i] == "--migration-identity" && i + 1 < args.Length) migrationIdentity = args[++i];
    }

    var identityPattern = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9_\-]+$");
    if (!identityPattern.IsMatch(apiIdentity) || !identityPattern.IsMatch(workerIdentity) || !identityPattern.IsMatch(migrationIdentity))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error: Identity names must contain only alphanumeric characters, underscores, or hyphens.");
        Console.ResetColor();
        return 1;
    }

    Console.WriteLine($"Bootstrapping Azure SQL External Users for Managed Identities:");
    Console.WriteLine($"  API Identity:       {apiIdentity} (Roles: db_datareader, db_datawriter)");
    Console.WriteLine($"  Worker Identity:    {workerIdentity} (Roles: db_datareader, db_datawriter)");
    Console.WriteLine($"  Migration Identity: {migrationIdentity} (Role: db_owner)");

    try
    {
        // Execute DDL to provision external provider users and role memberships idempotently
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{apiIdentity}')
BEGIN
    CREATE USER [{apiIdentity}] FROM EXTERNAL PROVIDER;
END;
ALTER ROLE db_datareader ADD MEMBER [{apiIdentity}];
ALTER ROLE db_datawriter ADD MEMBER [{apiIdentity}];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{workerIdentity}')
BEGIN
    CREATE USER [{workerIdentity}] FROM EXTERNAL PROVIDER;
END;
ALTER ROLE db_datareader ADD MEMBER [{workerIdentity}];
ALTER ROLE db_datawriter ADD MEMBER [{workerIdentity}];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{migrationIdentity}')
BEGIN
    CREATE USER [{migrationIdentity}] FROM EXTERNAL PROVIDER;
END;
ALTER ROLE db_owner ADD MEMBER [{migrationIdentity}];";

        await dbContext.Database.ExecuteSqlRawAsync(sql);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Managed Identity SQL users and roles successfully provisioned in Azure SQL.");
        Console.ResetColor();
        return 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Note: Failed to execute CREATE USER ... FROM EXTERNAL PROVIDER: {ex.Message}");
        Console.WriteLine("If running against a local SQL Server container, external Entra ID users are not supported.");
        Console.WriteLine("In Azure SQL, ensure you are connected using the Entra SQL Administrator account.");
        Console.ResetColor();
        return 1;
    }
}

static async Task<int> RunTenantCreateAsync(RelayDbContext dbContext, string[] args)
{
    string? name = null;
    string? slug = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--slug" && i + 1 < args.Length) slug = args[++i];
    }

    if (string.IsNullOrWhiteSpace(name))
    {
        Console.WriteLine("Missing required option: --name");
        return 1;
    }

    slug ??= name.Trim().ToLowerInvariant().Replace(' ', '-');

    var tenant = new Tenant(Guid.NewGuid(), name.Trim(), slug.Trim(), DateTimeOffset.UtcNow);
    dbContext.Tenants.Add(tenant);
    await dbContext.SaveChangesAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Tenant successfully created:");
    Console.ResetColor();
    Console.WriteLine($"  ID:   {tenant.Id}");
    Console.WriteLine($"  Name: {tenant.Name}");
    Console.WriteLine($"  Slug: {tenant.Slug}");
    return 0;
}

static async Task<int> RunTenantListAsync(RelayDbContext dbContext)
{
    var tenants = await dbContext.Tenants.AsNoTracking().OrderBy(t => t.CreatedAtUtc).ToListAsync();
    Console.WriteLine($"Found {tenants.Count} tenants:");
    Console.WriteLine("--------------------------------------------------------------------------------");
    Console.WriteLine("{0,-38} {1,-25} {2,-20} {3}", "ID", "NAME", "SLUG", "CREATED (UTC)");
    Console.WriteLine("--------------------------------------------------------------------------------");
    foreach (var t in tenants)
    {
        Console.WriteLine("{0,-38} {1,-25} {2,-20} {3:u}", t.Id, t.Name, t.Slug, t.CreatedAtUtc);
    }
    return 0;
}

static async Task<int> RunApiKeyCreateAsync(RelayDbContext dbContext, IApiKeyHasher hasher, string[] args)
{
    Guid? tenantId = null;
    string? name = null;
    int? expiresInDays = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--tenant-id" && i + 1 < args.Length && Guid.TryParse(args[++i], out var tid)) tenantId = tid;
        else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--expires-in-days" && i + 1 < args.Length && int.TryParse(args[++i], out var days)) expiresInDays = days;
    }

    if (!tenantId.HasValue || string.IsNullOrWhiteSpace(name))
    {
        Console.WriteLine("Missing required options: --tenant-id <guid> --name <name>");
        return 1;
    }

    var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId.Value);
    if (tenant is null)
    {
        Console.WriteLine($"Tenant with ID '{tenantId.Value}' not found.");
        return 1;
    }

    var prefix = $"pk_{Guid.NewGuid():N}"[..12];
    var hash = hasher.GenerateApiKey("live", prefix, out var rawApiKey);

    var now = DateTimeOffset.UtcNow;
    var expiresAt = expiresInDays.HasValue ? now.AddDays(expiresInDays.Value) : (DateTimeOffset?)null;

    var key = new ApiKey(
        id: Guid.NewGuid(),
        tenantId: tenant.Id,
        name: name.Trim(),
        keyPrefix: prefix,
        keyHash: hash,
        createdAtUtc: now,
        expiresAtUtc: expiresAt);

    dbContext.ApiKeys.Add(key);
    await dbContext.SaveChangesAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("API Key successfully generated.");
    Console.ResetColor();
    Console.WriteLine($"  Key ID:     {key.Id}");
    Console.WriteLine($"  Prefix:     {key.KeyPrefix}");
    Console.WriteLine($"  Tenant:     {tenant.Name} ({tenant.Id})");
    Console.WriteLine($"  Expires:    {(expiresAt.HasValue ? expiresAt.Value.ToString("u") : "Never")}");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("================================================================================");
    Console.WriteLine("IMPORTANT: Copy this secret key now. You will NEVER be able to see it again!");
    Console.WriteLine("================================================================================");
    Console.ResetColor();
    Console.WriteLine($"  API Key: {rawApiKey}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("================================================================================");
    Console.ResetColor();
    return 0;
}

static async Task<int> RunApiKeyListAsync(RelayDbContext dbContext, string[] args)
{
    Guid? tenantId = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--tenant-id" && i + 1 < args.Length && Guid.TryParse(args[++i], out var tid)) tenantId = tid;
    }

    if (!tenantId.HasValue)
    {
        Console.WriteLine("Missing required option: --tenant-id <guid>");
        return 1;
    }

    var now = DateTimeOffset.UtcNow;
    var keys = await dbContext.ApiKeys
        .AsNoTracking()
        .Where(k => k.TenantId == tenantId.Value)
        .OrderByDescending(k => k.CreatedAtUtc)
        .ToListAsync();

    Console.WriteLine($"Found {keys.Count} API keys for Tenant {tenantId.Value}:");
    Console.WriteLine("--------------------------------------------------------------------------------------------------------");
    Console.WriteLine("{0,-15} {1,-20} {2,-10} {3,-22} {4}", "PREFIX", "NAME", "STATUS", "EXPIRES (UTC)", "LAST USED (UTC)");
    Console.WriteLine("--------------------------------------------------------------------------------------------------------");

    foreach (var k in keys)
    {
        var status = k.IsRevoked ? "Revoked" : k.IsExpired(now) ? "Expired" : "Active";
        Console.WriteLine("{0,-15} {1,-20} {2,-10} {3,-22} {4}",
            k.KeyPrefix,
            k.Name,
            status,
            k.ExpiresAtUtc.HasValue ? k.ExpiresAtUtc.Value.ToString("u") : "Never",
            k.LastUsedAtUtc.HasValue ? k.LastUsedAtUtc.Value.ToString("u") : "Never");
    }

    return 0;
}

static async Task<int> RunApiKeyRevokeAsync(RelayDbContext dbContext, string[] args)
{
    string? prefix = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--prefix" && i + 1 < args.Length) prefix = args[++i];
    }

    if (string.IsNullOrWhiteSpace(prefix))
    {
        Console.WriteLine("Missing required option: --prefix <prefix>");
        return 1;
    }

    var key = await dbContext.ApiKeys.FirstOrDefaultAsync(k => k.KeyPrefix == prefix.Trim());
    if (key is null)
    {
        Console.WriteLine($"API key with prefix '{prefix}' not found.");
        return 1;
    }

    if (key.IsRevoked)
    {
        Console.WriteLine($"API key with prefix '{prefix}' is already revoked.");
        return 0;
    }

    key.Revoke(DateTimeOffset.UtcNow);
    await dbContext.SaveChangesAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"API key with prefix '{prefix}' successfully revoked.");
    Console.ResetColor();
    return 0;
}
