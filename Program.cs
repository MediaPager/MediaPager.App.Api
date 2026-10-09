using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MediaPager.App.Api.Controllers;
using MediaPager.App.Core.Services;

var builder = WebApplication.CreateBuilder(args);
// Plugin metadata lists, extracted so appsettings.json stays a thin Required-by-id file.
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "plugins.official.json"), optional: false, reloadOnChange: false);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "plugins.community.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(prefix: "MEDIAPAGER_");

var configuredDatabasePath = Environment.GetEnvironmentVariable("MEDIAPAGER_DB_PATH");
if (string.IsNullOrWhiteSpace(configuredDatabasePath))
    configuredDatabasePath = builder.Configuration["Auth:DatabasePath"];
var configuredConnectionString = builder.Configuration.GetConnectionString("AuthDatabase");
var windowsAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
if (string.IsNullOrWhiteSpace(windowsAppData))
    windowsAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
var defaultDatabaseDirectory = OperatingSystem.IsWindows()
    ? Path.Combine(windowsAppData, "MediaPager", "db")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MediaPager", "db");
var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
    ? string.IsNullOrWhiteSpace(configuredConnectionString)
        ? Path.Combine(defaultDatabaseDirectory, "mediapager.db")
        : new SqliteConnectionStringBuilder(configuredConnectionString).DataSource
    : configuredDatabasePath;
if (string.IsNullOrWhiteSpace(databasePath))
    throw new InvalidOperationException("The configured auth database path is empty.");
databasePath = Path.GetFullPath(databasePath);
var databaseDirectory = Path.GetDirectoryName(databasePath)
    ?? throw new InvalidOperationException("The configured auth database path must include a directory.");
Directory.CreateDirectory(databaseDirectory);

Console.Error.WriteLine($"Auth database: {databasePath}");
var legacyDatabasePaths = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, "mediapager-auth.db"),
    Path.Combine(defaultDatabaseDirectory, "mediapager-auth.db"),
};
foreach (var legacyDatabasePath in legacyDatabasePaths)
{
    if (File.Exists(databasePath))
        break;
    if (!File.Exists(legacyDatabasePath) ||
        string.Equals(Path.GetFullPath(legacyDatabasePath), databasePath, StringComparison.OrdinalIgnoreCase))
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
            DataSource = databasePath,
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

var jwtIssuer = builder.Configuration["Auth:Issuer"] ?? "MediaPager.App.Api";
var configuredSigningKey = Environment.GetEnvironmentVariable("MEDIAPAGER_EKEY");
if (string.IsNullOrWhiteSpace(configuredSigningKey))
    configuredSigningKey = builder.Configuration["Auth:SigningKey"];
byte[] signingKeyBytes;
if (!string.IsNullOrWhiteSpace(configuredSigningKey))
{
    signingKeyBytes = Encoding.UTF8.GetBytes(configuredSigningKey);
    if (signingKeyBytes.Length < 32)
        throw new InvalidOperationException("Auth:SigningKey must be at least 32 bytes.");
}
else
{
    // Keep tokens valid across restarts by storing a generated key next to the database.
    var signingKeyPath = Path.Combine(databaseDirectory, "signing.key");
    if (File.Exists(signingKeyPath))
    {
        signingKeyBytes = await File.ReadAllBytesAsync(signingKeyPath);
    }
    else
    {
        signingKeyBytes = RandomNumberGenerator.GetBytes(64);
        await File.WriteAllBytesAsync(signingKeyPath, signingKeyBytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(signingKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.Error.WriteLine("Auth:SigningKey is not configured; generated and stored one next to the database.");
    }
}
var signingKey = new SymmetricSecurityKey(signingKeyBytes);
builder.Services.AddSingleton(new AuthTokenConfiguration(jwtIssuer, signingKey));
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
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
                var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
                var user = userId is null ? null : await userManager.FindByIdAsync(userId);
                if (user is null || !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal))
                    context.Fail("The account session is no longer valid.");
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("CanInvite", policy => policy.RequireAssertion(context =>
        context.User.HasClaim("scope", "admin:super") || context.User.HasClaim("scope", "admin:can-invite")));
    options.AddPolicy("CanEditSettings", policy => policy.RequireAssertion(context =>
        context.User.HasClaim("scope", "admin:super") || context.User.HasClaim("scope", "admin:settings-edit")));
    options.AddPolicy("CanEditCatalogs", policy => policy.RequireAssertion(context =>
        context.User.HasClaim("scope", "admin:super") || context.User.HasClaim("scope", "admin:catalogs-edit")));
    options.AddPolicy("SuperAdmin", policy => policy.RequireAssertion(context =>
        context.User.HasClaim("scope", "admin:super")));
});

builder.Services.AddScoped<EmailSender>();
builder.Services.AddScoped<IRuntimeSettings, RuntimeSettingsService>();
builder.Services.AddSingleton<StreamSessionState>();
builder.Services.AddSingleton<ArtworkStorage>();
builder.Services.AddSingleton<LocalFileSessionState>();
builder.Services.AddSingleton<PluginRegistry>();
builder.Services.AddSingleton<IPluginHost>(services => services.GetRequiredService<PluginRegistry>());
builder.Services.AddSingleton<PluginActivityStore>();
builder.Services.AddSingleton<IPluginSettingsStore, PluginSettingsService>();
builder.Services.AddSingleton<PluginDeployer>();
builder.Services.AddSingleton<PluginInstaller>();
builder.Services.AddSingleton<PluginJobsService>();
builder.Services.AddSingleton<ILibraryQuery, LibraryQuery>();
builder.Services.AddSingleton<ILibraryStore, LibraryStore>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<GitHubPluginDiscovery>(client => client.Timeout = TimeSpan.FromSeconds(30));
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

// Plugins install into two directories (the build-time deploy target and the on-demand
// deployer share one layout): official — shipped officials, deployed there on build,
// optional officials installed on demand — and community — recognized community plugins,
// installed on demand. appsettings keeps only which ids are Required; the metadata lists
// live in plugins.official.json / plugins.community.json.
var officialDirectory = PluginDirectories.Official(builder.Configuration);
var communityDirectory = PluginDirectories.Community(builder.Configuration);
Directory.CreateDirectory(officialDirectory);
Directory.CreateDirectory(communityDirectory);

// Structural official tagging: reserve every official id (list, installed or not) plus
// the ids actually loaded, so a community plugin can never shadow an official one —
// even one that isn't part of this image's default set. Only ids named in
// Plugins:Required:Official load at boot: the directory holds every shipped official,
// Required decides which are active (and therefore visible in nav/settings/pickers).
var officialCatalog = PluginCatalog.ReadOfficial(builder.Configuration);
var requiredPluginSets = PluginCatalog.ReadRequired(builder.Configuration);
var reservedPluginIds = new HashSet<string>(
    officialCatalog.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
var officialPlugins = PluginLoader.LoadDirectory(
    app.Services, officialDirectory, official: true, requiredIds: requiredPluginSets.Official);
foreach (var plugin in officialPlugins)
    reservedPluginIds.Add(plugin.Descriptor.Id);
var communityPlugins = PluginLoader.LoadDirectory(
    app.Services, communityDirectory, official: false, reservedPluginIds);
app.Services.GetRequiredService<PluginRegistry>().Reset(
    officialPlugins.Concat(communityPlugins),
    communityPlugins.Select(plugin => plugin.Descriptor.Id));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await database.Database.MigrateAsync();

    // Seed the catalog lookup tables (idempotent — insert only what's missing).
    if (!await database.CatalogTypes.AnyAsync())
    {
        var mediaTypes = new[]
        {
            new MediaType { Name = "Video", Slug = "video", Description = "Moving picture content (movies, TV shows)." },
            new MediaType { Name = "Audio", Slug = "audio", Description = "Audio-only content (music, podcasts, audiobooks)." },
            new MediaType { Name = "Book", Slug = "book", Description = "Written content (PDF, EPUB, etc.)." },
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
            new CatalogType { Name = "Books", Slug = "books", MediaTypeId = bySlug["book"].Id });
        await database.SaveChangesAsync();
        app.Logger.LogInformation("Seeded catalog lookup tables.");
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

app.Run();
