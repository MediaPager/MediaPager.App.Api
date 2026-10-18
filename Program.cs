using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MediaPager.App.Api.Controllers;
using MediaPager.App.Core.Services;
using NoBugsGiven.Quasar.Interop;

if (ApiRestartBootstrapper.IsBootstrapMode(args))
{
    await ApiRestartBootstrapper.RunAsync(args);
    return;
}

if (PluginWorkerCommand.IsWorkerMode(args))
{
    Environment.ExitCode = await PluginWorkerCommand.RunAsync(args);
    return;
}

if (ManagedToolsBootstrapCommand.IsBootstrapMode(args))
{
    Environment.ExitCode = await ManagedToolsBootstrapCommand.RunAsync(args);
    return;
}

if (ManagedPluginBuildSdkCommand.IsBootstrapMode(args))
{
    Environment.ExitCode = await ManagedPluginBuildSdkCommand.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var sourceAppSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
if (!File.Exists(sourceAppSettingsPath))
    sourceAppSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (File.Exists(sourceAppSettingsPath))
    builder.Configuration.AddJsonFile(sourceAppSettingsPath, optional: false, reloadOnChange: false);
// Load the environment prefix before resolving the persistent data root. Runtime-owned
// configuration copies live on the data volume, not in the application image.
builder.Configuration.AddEnvironmentVariables(prefix: "MEDIAPAGER_");
var bootstrapStorageLayout = new StorageLayout(builder.Configuration);
var bootstrapDataProtectionProvider = DataProtectionProvider.Create(
    new DirectoryInfo(bootstrapStorageLayout.DataProtectionKeysDirectory),
    dataProtection => dataProtection.SetApplicationName("MediaPager"));
Directory.CreateDirectory(bootstrapStorageLayout.ConfigDirectory);
var runtimeAppSettingsPath = Path.Combine(bootstrapStorageLayout.ConfigDirectory, "appsettings.json");
if (!File.Exists(runtimeAppSettingsPath))
    File.Copy(sourceAppSettingsPath, runtimeAppSettingsPath);
MigrateLegacyPluginSettings(runtimeAppSettingsPath, sourceAppSettingsPath);
var runtimeCommunityPluginsPath = Path.Combine(bootstrapStorageLayout.ConfigDirectory, "plugins.community.json");
if (!File.Exists(runtimeCommunityPluginsPath))
{
    var bundledCommunityList = Path.Combine(AppContext.BaseDirectory, "plugins.community.json");
    if (File.Exists(bundledCommunityList)) File.Copy(bundledCommunityList, runtimeCommunityPluginsPath);
    else await File.WriteAllTextAsync(runtimeCommunityPluginsPath, "{\"Plugins\":{\"List\":{\"Community\":[]}}}");
}
// Mutable values are overlays on the image defaults; prefixed environment values are
// added again afterward and therefore remain deployment overrides.
builder.Configuration.AddJsonFile(runtimeAppSettingsPath, optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile(runtimeCommunityPluginsPath, optional: false, reloadOnChange: true);
var databaseBootstrapPath = DatabaseBootstrapFile.PathFor(bootstrapStorageLayout);
builder.Configuration.AddJsonFile(databaseBootstrapPath, optional: true, reloadOnChange: false);
if (File.Exists(databaseBootstrapPath) && DatabaseBootstrapFile.ReadProtectedPassword(databaseBootstrapPath, bootstrapDataProtectionProvider) is { } bootPassword)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [SettingsConfigurationKeys.DbPassword] = bootPassword,
    });
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "plugins.official.json"), optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(prefix: "MEDIAPAGER_");

var storageLayout = new StorageLayout(builder.Configuration);
var scopeSecurityPathSetting = SettingsConfigurationKeys.ReadConfiguredValue(
    builder.Configuration, SettingsConfigurationKeys.AppScopeSecurityPath);
var scopeSecurityPath = storageLayout.Resolve(scopeSecurityPathSetting, Path.Combine("config", "scope-security.json"));
if (!File.Exists(scopeSecurityPath))
{
    var bundledScopeSecurityPath = Path.Combine(AppContext.BaseDirectory, "scope-security.json");
    if (!File.Exists(bundledScopeSecurityPath))
        throw new FileNotFoundException("The bundled scope-security catalog is missing.", bundledScopeSecurityPath);
    Directory.CreateDirectory(Path.GetDirectoryName(scopeSecurityPath)!);
    File.Copy(bundledScopeSecurityPath, scopeSecurityPath);
}
var scopeSecurityCatalog = ScopeSecurityCatalog.Load(scopeSecurityPath);
var databaseProvider = (SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbProvider) ?? "Sqlite")
    .Trim().ToLowerInvariant();
var configuredDatabasePath = SettingsConfigurationKeys.ReadEnvironmentValue(SettingsConfigurationKeys.DbPath)
    ?? builder.Configuration[SettingsConfigurationKeys.DbPath]
    ?? builder.Configuration["Auth:DatabasePath"];
var configuredConnectionString = builder.Configuration["Settings:Db:ConnectionString"];
if (string.IsNullOrWhiteSpace(configuredConnectionString))
    configuredConnectionString = Environment.GetEnvironmentVariable("MEDIAPAGER_ConnectionStrings__AuthDatabase");
if (string.IsNullOrWhiteSpace(configuredConnectionString))
{
    if (databaseProvider is "postgres" or "postgresql")
    {
        var configuredDbHost = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbHost);
        if (!string.IsNullOrWhiteSpace(configuredDbHost))
        {
            var postgres = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = configuredDbHost,
                Port = int.TryParse(SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbPort), out var port) ? port : 5432,
                Database = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbCatalog) ?? "mediapager",
                Username = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbUser) ?? "mediapager",
                Password = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbPassword) ?? "",
                Timeout = int.TryParse(SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbConnectionTimeout), out var timeout) ? timeout : 15,
            };
            var configuredSslMode = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.DbSslMode);
            if (!string.IsNullOrWhiteSpace(configuredSslMode) && Enum.TryParse<Npgsql.SslMode>(configuredSslMode, true, out var parsedSslMode))
                postgres.SslMode = parsedSslMode;
            configuredConnectionString = postgres.ConnectionString;
        }
    }
    if (string.IsNullOrWhiteSpace(configuredConnectionString))
        configuredConnectionString = builder.Configuration.GetConnectionString("AuthDatabase");
}
var persistentDataDirectory = storageLayout.DataRoot;
Directory.CreateDirectory(persistentDataDirectory);

switch (databaseProvider)
{
    case "sqlite":
    case "sqlite3":
        var sqliteOptions = new SqliteConnectionStringBuilder(configuredConnectionString ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(configuredDatabasePath))
            sqliteOptions.DataSource = storageLayout.Resolve(configuredDatabasePath, Path.Combine("db", "mediapager-auth.db"));
        else if (string.IsNullOrWhiteSpace(sqliteOptions.DataSource))
            sqliteOptions.DataSource = storageLayout.DatabasePath;

        var sqliteIsInMemory = sqliteOptions.Mode == SqliteOpenMode.Memory || sqliteOptions.DataSource == ":memory:";
        string? sqliteDatabasePath = null;
        if (!sqliteIsInMemory)
        {
            sqliteDatabasePath = Path.GetFullPath(sqliteOptions.DataSource);
            sqliteOptions.DataSource = sqliteDatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(sqliteDatabasePath)
                ?? throw new InvalidOperationException("The configured SQLite database path must include a directory."));
            Console.Error.WriteLine($"Auth database provider: SQLite ({sqliteDatabasePath})");
            var legacyDatabasePaths = new[]
            {
                Path.Combine(builder.Environment.ContentRootPath, "mediapager-auth.db"),
                Path.Combine(StorageLayout.DefaultDataRoot, "db", "mediapager-auth.db"),
            };
            foreach (var legacyDatabasePath in legacyDatabasePaths)
            {
                if (File.Exists(sqliteDatabasePath))
                    break;
                if (!File.Exists(legacyDatabasePath) ||
                    string.Equals(Path.GetFullPath(legacyDatabasePath), sqliteDatabasePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = legacyDatabasePath,
                        Mode = SqliteOpenMode.ReadOnly,
                    }.ToString());
                    using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = sqliteDatabasePath,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                    }.ToString());
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                    Console.Error.WriteLine($"Migrated the existing auth database from {legacyDatabasePath}.");
                }
                catch (SqliteException exception)
                {
                    Console.Error.WriteLine($"Could not migrate the existing auth database: {exception.Message}");
                    throw;
                }
            }
        }
        else
        {
            Console.Error.WriteLine("Auth database provider: SQLite (in-memory)");
        }

        var sqliteConnectionString = sqliteOptions.ToString();
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite(
            sqliteConnectionString,
            sqlite => sqlite.MigrationsAssembly(typeof(AuthDbContext).Assembly.GetName().Name)));
        break;

    case "postgres":
    case "postgresql":
        var legacySqlitePath = Environment.GetEnvironmentVariable("MEDIAPAGER_DB_PATH") ?? builder.Configuration["Auth:DatabasePath"];
        if (!string.IsNullOrWhiteSpace(legacySqlitePath))
            throw new InvalidOperationException("MEDIAPAGER_DB_PATH/Auth:DatabasePath only applies to SQLite; configure ConnectionStrings:AuthDatabase for PostgreSQL.");
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            throw new InvalidOperationException("Settings:Db connection details are required when the provider is PostgreSQL.");

        Console.Error.WriteLine("Auth database provider: PostgreSQL");
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(
            configuredConnectionString,
            postgres => postgres.MigrationsAssembly(typeof(Program).Assembly.GetName().Name)));
        break;

    default:
        throw new InvalidOperationException($"Unknown Database:Provider '{databaseProvider}'. Supported providers: Sqlite, PostgreSQL.");
}

var jwtIssuer = SettingsConfigurationKeys.ReadConfiguredValue(builder.Configuration, SettingsConfigurationKeys.AppIssuer) ?? "MediaPager.App.Api";
var configuredSigningKey = Environment.GetEnvironmentVariable("MEDIAPAGER_EKEY");
if (string.IsNullOrWhiteSpace(configuredSigningKey))
    configuredSigningKey = builder.Configuration["Settings:App:JwtSigningKey"] ?? builder.Configuration["Auth:SigningKey"];
byte[] signingKeyBytes;
if (!string.IsNullOrWhiteSpace(configuredSigningKey))
{
    signingKeyBytes = Encoding.UTF8.GetBytes(configuredSigningKey);
    if (signingKeyBytes.Length < 32)
        throw new InvalidOperationException("Auth:SigningKey must be at least 32 bytes.");
}
else
{
    // Keep tokens valid across restarts by storing a generated key in persistent app data.
    var signingKeyPath = builder.Configuration["Settings:Storage:Security:JwtSigningKeyPath"]
        ?? builder.Configuration["Auth:SigningKeyPath"];
    if (string.IsNullOrWhiteSpace(signingKeyPath))
        signingKeyPath = storageLayout.JwtSigningKeyPath;
    else
        signingKeyPath = storageLayout.Resolve(signingKeyPath, Path.Combine("keys", "signing.key"));
    Directory.CreateDirectory(Path.GetDirectoryName(signingKeyPath)!);
    if (File.Exists(signingKeyPath))
    {
        signingKeyBytes = await File.ReadAllBytesAsync(signingKeyPath);
    }
    else
    {
        signingKeyBytes = RandomNumberGenerator.GetBytes(64);
        await File.WriteAllBytesAsync(signingKeyPath, signingKeyBytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(signingKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.Error.WriteLine($"Auth:SigningKey is not configured; generated and stored one at {signingKeyPath}.");
    }
}
var signingKey = new SymmetricSecurityKey(signingKeyBytes);
builder.Services.AddSingleton(new AuthTokenConfiguration(jwtIssuer, signingKey));
builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtIssuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
                var securityStamp = context.Principal?.FindFirstValue("security_stamp");
                var mobileSessionId = context.Principal?.FindFirstValue("mobile_session_id");
                var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
                var user = userId is null ? null : await userManager.FindByIdAsync(userId);
                if (user is null || !user.IsActive || user.DeletedAt is not null ||
                    !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal))
                {
                    context.Fail("The account session is no longer valid.");
                    return;
                }

                if (mobileSessionId is not null)
                {
                    var database = context.HttpContext.RequestServices.GetRequiredService<AuthDbContext>();
                    var activeSession = await database.UserTokens.AsNoTracking().AnyAsync(token =>
                        token.UserId == user.Id &&
                        token.LoginProvider == AuthController.MobileSessionTokenProvider &&
                        token.Name == mobileSessionId);
                    if (!activeSession) context.Fail("The mobile device session was revoked.");
                }
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("CanInvite", policy => policy.RequireAssertion(context =>
        scopeSecurityCatalog.HasPermission(context.User.FindAll("scope").Select(claim => claim.Value), "users:invite")));
    options.AddPolicy("CanEditSettings", policy => policy.RequireAssertion(context =>
        scopeSecurityCatalog.HasPermission(context.User.FindAll("scope").Select(claim => claim.Value), "settings:edit")));
    options.AddPolicy("CanEditCatalogs", policy => policy.RequireAssertion(context =>
        scopeSecurityCatalog.HasPermission(context.User.FindAll("scope").Select(claim => claim.Value), "catalogs:edit")));
    options.AddPolicy("SuperAdmin", policy => policy.RequireAssertion(context =>
        context.User.FindAll("scope").Any(claim =>
            string.Equals(ScopeSecurityCatalog.CanonicalizeLegacy(claim.Value), "admin:super", StringComparison.OrdinalIgnoreCase))));
});

builder.Services.AddScoped<EmailSender>();
builder.Services.AddSingleton<IPluginEmailSender, PluginEmailSender>();
builder.Services.AddScoped<IRuntimeSettings, RuntimeSettingsService>();
builder.Services.AddSingleton(scopeSecurityCatalog);
builder.Services.AddScoped<GitSshPrivateKeyStore>();
builder.Services.AddSingleton<IGitPrivateKeyProvider, GitSshPrivateKeyProvider>();
builder.Services.AddSingleton<IDataProtectionProvider>(bootstrapDataProtectionProvider);
builder.Services.AddSingleton<IPluginSettingsValueProtector, PluginSettingsValueProtector>();
builder.Services.AddSingleton(storageLayout);
builder.Services.AddSingleton<StreamSessionState>();
builder.Services.AddSingleton<ArtworkStorage>();
builder.Services.AddSingleton<LocalFileSessionState>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<IPluginHost>(services => services.GetRequiredService<PluginRegistry>());
builder.Services.AddSingleton<PluginActivityStore>();
builder.Services.AddSingleton<IPluginSettingsRepository, PluginSettingsService>();
builder.Services.AddSingleton<ManagedToolDirectory>();
builder.Services.AddSingleton<ManagedToolsBootstrapState>();
builder.Services.AddHttpClient<FfmpegToolInstaller>(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(nameof(DocumentToolsetInstaller), (services, client) =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaPager/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    var token = services.GetRequiredService<IConfiguration>()["GitHubToken"];
    if (!string.IsNullOrWhiteSpace(token))
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
});
builder.Services.AddHttpClient<ManagedPluginBuildSdkProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IPluginBuildSdkProvider>(services =>
    services.GetRequiredService<ManagedPluginBuildSdkProvider>());
builder.Services.AddSingleton<PlaywrightBrowserInstaller>();
builder.Services.AddSingleton<ManagedToolsProvisioner>();
builder.Services.AddHostedService<ManagedToolsBootstrapService>();
builder.Services.AddSingleton<DocumentToolsetInstaller>();
builder.Services.AddHostedService<DocumentToolsBootstrapService>();
builder.Services.AddSingleton<IBrowserAutomationBroker, PlaywrightBrowserAutomationBroker>();
builder.Services.AddSingleton<IPluginWorkerFactory, PluginWorkerFactory>();
builder.Services.AddSingleton<PluginDeployer>();
builder.Services.AddSingleton<PluginInstaller>();
builder.Services.AddSingleton<PluginJobsService>();
builder.Services.AddSingleton<ApiRestartCoordinator>(services => new ApiRestartCoordinator(
    args,
    PluginDirectories.Community(builder.Configuration),
    string.Equals(Environment.GetEnvironmentVariable("MEDIAPAGER_TOOLS_BOOTSTRAP_EXTERNAL"), "true", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
    builder.Configuration.GetValue<bool>("Desktop:Mode"),
    services.GetRequiredService<IHostApplicationLifetime>(),
    services.GetRequiredService<ILogger<ApiRestartCoordinator>>()));
builder.Services.AddSingleton<ILibraryQuery, LibraryQuery>();
builder.Services.AddSingleton<ILibraryStore, LibraryStore>();
builder.Services.AddSingleton<IMediaToolBroker, FfmpegMediaToolBroker>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<GitHubPluginDiscovery>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IInteropCapabilityPolicy, MediaPagerInteropCapabilityPolicy>();
builder.Services.AddSingleton<IInteropCapability, PluginCatalogInteropCapability>();
builder.Services.AddQuasarInterop();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpClient("upstream");
builder.Services.AddHttpClient("stream")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapQuasarInterop();
app.MapFallbackToFile("index.html").AllowAnonymous();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await database.Database.MigrateAsync();
    var runtimeSettings = scope.ServiceProvider.GetRequiredService<IRuntimeSettings>();
    await runtimeSettings.RemoveDatabaseSettingsAsync();
    await runtimeSettings.SeedMissingFromConfigurationAsync();
    var storedSettings = await runtimeSettings.GetAllAsync();
    DatabaseBootstrapFile.Write(DatabaseBootstrapFile.PathFor(storageLayout), builder.Configuration,
        bootstrapDataProtectionProvider);
    builder.Configuration.AddInMemoryCollection(storedSettings.ToDictionary(
        pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase));
    // Deployment environment variables remain the final override over runtime DB values.
    builder.Configuration.AddEnvironmentVariables(prefix: "MEDIAPAGER_");
    storageLayout.Refresh();
    PlaywrightBrowserInstaller.ConfigureBrowserPath(storageLayout.ToolsDirectory);
    ArtworkStorage.MigrateLegacyDefaultArtwork(storageLayout.DataRoot);

    // Seed the catalog lookup tables (idempotent — insert only what's missing).
    if (!await database.CatalogTypes.AnyAsync())
    {
        var mediaTypes = new[]
        {
            new MediaType { Name = "Video", Slug = "video", Description = "Moving picture content (movies, TV shows)." },
            new MediaType { Name = "Audio", Slug = "audio", Description = "Audio-only content (music, podcasts, audiobooks)." },
            new MediaType { Name = "Book", Slug = "book", Description = "Written content (PDF, EPUB, etc.)." },
            new MediaType { Name = "Documents", Slug = "documents", Description = "Documents and office files." },
        };
        database.MediaTypes.AddRange(mediaTypes);
        await database.SaveChangesAsync();

        var bySlug = mediaTypes.ToDictionary(m => m.Slug, StringComparer.Ordinal);
        database.CatalogTypes.AddRange(
            new CatalogType { Name = "Movies", Slug = "movies", MediaTypeId = bySlug["video"].Id },
            new CatalogType { Name = "TV Shows", Slug = "tv-shows", MediaTypeId = bySlug["video"].Id },
            new CatalogType { Name = "Music", Slug = "music", MediaTypeId = bySlug["audio"].Id },
            new CatalogType { Name = "Podcasts", Slug = "podcasts", MediaTypeId = bySlug["audio"].Id },
            new CatalogType { Name = "Audiobooks", Slug = "audiobooks", MediaTypeId = bySlug["audio"].Id },
            new CatalogType { Name = "Books", Slug = "books", MediaTypeId = bySlug["book"].Id },
            new CatalogType { Name = "Docs", Slug = "docs", MediaTypeId = bySlug["documents"].Id });
        await database.SaveChangesAsync();
        app.Logger.LogInformation("Seeded catalog lookup tables.");
    }

    // Additive lookup seeding also upgrades existing databases that already have the original
    // catalog types; the initial all-types seed above runs only on a blank lookup table.
    var documentsMediaType = await database.MediaTypes
        .FirstOrDefaultAsync(type => type.Slug == "documents");
    if (documentsMediaType is null)
    {
        documentsMediaType = new MediaType
        {
            Name = "Documents",
            Slug = "documents",
            Description = "Documents and office files.",
        };
        database.MediaTypes.Add(documentsMediaType);
        await database.SaveChangesAsync();
    }
    if (!await database.CatalogTypes.AnyAsync(type => type.Slug == "docs"))
    {
        database.CatalogTypes.Add(new CatalogType
        {
            Name = "Docs",
            Slug = "docs",
            MediaTypeId = documentsMediaType.Id,
        });
        await database.SaveChangesAsync();
        app.Logger.LogInformation("Seeded Docs catalog type and Documents media type.");
    }

    var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var seedEmail = Environment.GetEnvironmentVariable("MEDIAPAGER_SEED_USER");
    if (string.IsNullOrWhiteSpace(seedEmail))
        seedEmail = builder.Configuration["Auth:SeedEmail"] ?? "admin@mediapager.local";
    // Only seed when the seed email is free AND no super-admin exists yet: an existing
    // super-admin may have changed their email (Profile → Account), which must not cause
    // a second super-admin (with a fresh temporary password) to be created on restart.
    var allUsers = await users.Users.AsNoTracking().ToListAsync();
    var hasSuperAdmin = allUsers.Any(user => user.GetScopes().Contains("admin:super"));
    if (!hasSuperAdmin && allUsers.All(user => !string.Equals(user.Email, seedEmail, StringComparison.OrdinalIgnoreCase)))
    {
        const string lowercase = "abcdefghijkmnopqrstuvwxyz";
        const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "!@$%*-_";
        const string alphabet = lowercase + uppercase + digits + symbols;
        var configuredSeedPassword = Environment.GetEnvironmentVariable("MEDIAPAGER_SEED_PASS");
        if (string.IsNullOrWhiteSpace(configuredSeedPassword))
            configuredSeedPassword = builder.Configuration["Auth:SeedPassword"];
        string seedPassword;
        if (!string.IsNullOrWhiteSpace(configuredSeedPassword))
        {
            seedPassword = configuredSeedPassword;
        }
        else
        {
            var passwordCharacters = new[]
                {
                    lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)],
                    uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)],
                    digits[RandomNumberGenerator.GetInt32(digits.Length)],
                    symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
                }
                .Concat(Enumerable.Range(0, 28).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]))
                .ToArray();
            for (var index = passwordCharacters.Length - 1; index > 0; index--)
            {
                var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
                (passwordCharacters[index], passwordCharacters[swapIndex]) = (passwordCharacters[swapIndex], passwordCharacters[index]);
            }

            seedPassword = new string(passwordCharacters);
        }

        var seedUser = new AppUser
        {
            UserName = seedEmail,
            Email = seedEmail,
            EmailConfirmed = true,
            Scopes = "admin:super admin:settings-edit",
        };
        var result = await users.CreateAsync(seedUser, seedPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Unable to seed the initial account: {string.Join("; ", result.Errors.Select(error => error.Description))}");
        if (string.IsNullOrWhiteSpace(configuredSeedPassword))
            app.Logger.LogWarning("Created initial super-admin {Email}. Temporary password: {Password}", seedEmail, seedPassword);
        else
            app.Logger.LogWarning("Created initial super-admin {Email} using the configured Auth:SeedPassword.", seedEmail);
    }
}

// Load plugins after migrations and seeds: plugin constructors and action descriptions may
// read their settings from the database as soon as the worker is started.
// Plugins install into two directories (the build-time deploy target and the on-demand
// deployer share one layout): official — shipped officials, deployed there on build,
// optional officials installed on demand — and community — recognized community plugins,
// installed on demand. appsettings keeps only which ids are Required; the metadata lists
// live in plugins.official.json / plugins.community.json.
var bundledOfficialDirectory = PluginDirectories.BundledOfficial(builder.Configuration);
var officialDirectory = PluginDirectories.Official(builder.Configuration);
var communityDirectory = PluginDirectories.Community(builder.Configuration);
Directory.CreateDirectory(officialDirectory);
Directory.CreateDirectory(communityDirectory);
ApiRestartBootstrapper.CleanupPendingUninstalls(
    communityDirectory,
    message => app.Logger.LogWarning("{PendingPluginRemoval}", message));

// Structural official tagging: reserve every official id (list, installed or not) plus
// the ids actually loaded, so a community plugin can never shadow an official one —
// even one that isn't part of this image's default set. Only ids named in
// Plugins:Required:Official load at boot: the directory holds every shipped official,
// Required decides which are active (and therefore visible in nav/settings/pickers).
var officialCatalog = PluginCatalog.ReadOfficial(builder.Configuration);
var requiredPluginSets = PluginCatalog.ReadRequired(builder.Configuration);
var communityCatalog = PluginCatalog.ReadCommunity(builder.Configuration);
var officialPluginIdsByAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var entry in officialCatalog)
    officialPluginIdsByAssembly[entry.Assembly ?? PluginInstaller.DeriveAssemblyName(entry.Repo)] = entry.Id;
var communityPluginIdsByAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var entry in communityCatalog)
    communityPluginIdsByAssembly[entry.Assembly ?? PluginInstaller.DeriveAssemblyName(entry.Repo)] = entry.Id;
var reservedPluginIds = new HashSet<string>(
    officialCatalog.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
var officialPlugins = PluginLoader.LoadDirectory(
    app.Services, bundledOfficialDirectory, official: true, requiredIds: requiredPluginSets.Official,
    pluginIdsByAssembly: officialPluginIdsByAssembly).ToList();
if (!string.Equals(Path.GetFullPath(bundledOfficialDirectory), Path.GetFullPath(officialDirectory),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
{
    officialPlugins.AddRange(PluginLoader.LoadDirectory(
        app.Services, officialDirectory, official: true, requiredIds: requiredPluginSets.Official,
        pluginIdsByAssembly: officialPluginIdsByAssembly));
}
foreach (var plugin in officialPlugins)
    reservedPluginIds.Add(plugin.Descriptor.Id);
var communityPlugins = PluginLoader.LoadDirectory(
    app.Services, communityDirectory, official: false, reservedPluginIds,
    pluginIdsByAssembly: communityPluginIdsByAssembly);
app.Services.GetRequiredService<PluginRegistry>().Reset(
    officialPlugins.Concat(communityPlugins),
    communityPlugins.Select(plugin => plugin.Descriptor.Id));
_ = scopeSecurityCatalog.ScopesFor(officialPlugins.Concat(communityPlugins));

app.Run();

static void MigrateLegacyPluginSettings(string appSettingsPath, string defaultsAppSettingsPath)
{
    var text = File.ReadAllText(appSettingsPath);
    var root = System.Text.Json.Nodes.JsonNode.Parse(
        text,
        nodeOptions: null,
        new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
        as System.Text.Json.Nodes.JsonObject;
    if (root is null) return;

    var changed = false;
    var settings = root["Settings"] as System.Text.Json.Nodes.JsonObject;
    var settingsPlugins = settings?["Plugins"] as System.Text.Json.Nodes.JsonObject;
    if (settingsPlugins?["Required"] is System.Text.Json.Nodes.JsonArray flatRequired)
    {
        // A previous official-plugin toggle wrote Required as one flat array instead of
        // Required:Official. Restore the shipped defaults and merge the explicitly enabled
        // ids so restarting the API doesn't silently turn an official plugin back off.
        System.Text.Json.Nodes.JsonObject? defaults = null;
        if (File.Exists(defaultsAppSettingsPath))
            defaults = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(defaultsAppSettingsPath),
                nodeOptions: null,
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as System.Text.Json.Nodes.JsonObject;
        var defaultsRequired = defaults?["Settings"]?["Plugins"]?["Required"] as System.Text.Json.Nodes.JsonObject;
        var official = CopyStringArray(defaultsRequired?["Official"]);
        var community = CopyStringArray(defaultsRequired?["Community"]);
        foreach (var node in flatRequired)
        {
            if (node is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var id) ||
                string.IsNullOrWhiteSpace(id)) continue;
            if (!ContainsId(official, id)) official.Add(id);
        }

        settings ??= new System.Text.Json.Nodes.JsonObject();
        root["Settings"] = settings;
        settingsPlugins ??= new System.Text.Json.Nodes.JsonObject();
        settings["Plugins"] = settingsPlugins;
        settingsPlugins["Required"] = new System.Text.Json.Nodes.JsonObject
        {
            ["Official"] = official,
            ["Community"] = community,
        };
        changed = true;
    }

    var plugins = root["Plugins"] as System.Text.Json.Nodes.JsonObject;
    var legacyRequired = plugins?["Required"] as System.Text.Json.Nodes.JsonObject;
    if (legacyRequired is not null)
    {
        settings ??= new System.Text.Json.Nodes.JsonObject();
        root["Settings"] = settings;
        settingsPlugins = settings["Plugins"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        settings["Plugins"] = settingsPlugins;
        if (settingsPlugins["Required"] is null)
            settingsPlugins["Required"] = legacyRequired.DeepClone();
        plugins!.Remove("Required");
        if (plugins.Count == 0) root.Remove("Plugins");
        changed = true;
    }

    if (!changed) return;
    var temp = $"{appSettingsPath}.{Guid.NewGuid():N}.tmp";
    File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    File.Move(temp, appSettingsPath, overwrite: true);

    static System.Text.Json.Nodes.JsonArray CopyStringArray(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonArray array
            ? (System.Text.Json.Nodes.JsonArray)array.DeepClone()
            : new System.Text.Json.Nodes.JsonArray();

    static bool ContainsId(System.Text.Json.Nodes.JsonArray array, string id) => array.Any(node =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var existing) &&
        string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
}
