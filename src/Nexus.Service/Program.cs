using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nexus;
using Nexus.Push;
using Nexus.Service;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Jwt;
using Pluperfect.Mail;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ServiceSettings.Section).Get<ServiceSettings>() ?? new ServiceSettings();
var connectionString = builder.Configuration.GetConnectionString("Nexus");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings:Nexus to a MySQL connection string.");
}

const string PushClient = "push";

// Development runs over plain http, so its cookies drop the __Host- prefix and the Secure flag. Sessions and the
// antiforgery cookie take the same answer.
var secureOrigin = !builder.Environment.IsDevelopment();

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(SourceConnectors.Databases());
builder.Services.AddSingleton(HttpRetryOptions.Default);
builder.Services.AddSingleton(RetryPolicy.Default);
builder.Services.AddHttpClient(PushClient);
builder.Services.AddTransient(services => new PushDestination(
    services.GetRequiredService<IHttpClientFactory>().CreateClient(PushClient),
    services.GetRequiredService<HttpRetryOptions>(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton<PendingRuns>();
builder.Services.AddScoped<SyncRunner>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddScoped<Access>();
builder.Services.AddScoped<ConnectionMaker>();
builder.Services.AddScoped<SyncMaker>();

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SignInThrottle>();
builder.Services.AddSingleton<SignInLinks>();
builder.Services.AddSingleton<SetupCode>();
builder.Services.AddScoped<Sessions>();
builder.Services.AddScoped<LinkMail>();
builder.Services.AddScoped<Admins>();
builder.Services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
builder.Services.AddPluperfectJwt(builder.Configuration, allowDevelopmentKey: builder.Environment.IsDevelopment(), secureOrigin: secureOrigin);
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AppKeyHandler>(AppKeyHandler.SchemeName, null);
builder.Services.AddPluperfectMail(builder.Configuration);

// The keys that encrypt stored secrets and mailed links live in the service's data directory, outside the database.
builder.Services.AddDataProtection()
    .SetApplicationName("Nexus")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, settings.DataDirectory, "keys")));
builder.Services.AddDbContext<NexusDbContext>(options => options.UseMySql(connectionString, NexusDbContext.ServerVersion));

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.WithOrigins(settings.ClientOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton(new AllowedOriginRegistry([settings.ClientOrigin]));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = (secureOrigin ? CookieTransport.HostPrefix : string.Empty) + "nxsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = secureOrigin ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddScoped<OriginFilter>();
builder.Services.AddScoped<BearerAwareAntiforgery>();

// Deny by default: a route is open to anyone only with [AllowAnonymous]. The fallback and the admin policy take only
// a signed-in user's session; an app's key reaches only the routes that name one of the two app policies.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(NexusClaims.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(NexusClaims.Admin, NexusClaims.True))
    .AddPolicy(AppKeyHandler.AppPolicy, policy => policy.AddAuthenticationSchemes(AppKeyHandler.SchemeName).RequireClaim(AppKeyHandler.AppClaim))
    .AddPolicy(AppKeyHandler.UserOrAppPolicy, policy => policy
        .AddAuthenticationSchemes(Pluperfect.Jwt.ServiceCollectionExtensions.Scheme, AppKeyHandler.SchemeName)
        .RequireAuthenticatedUser());

builder.Services.AddProblemDetails();
builder.Services
    .AddControllers(mvc =>
    {
        mvc.Filters.AddService<BearerAwareAntiforgery>();
        mvc.Filters.AddService<OriginFilter>();
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
    if (db.Database.IsMySql())
    {
        await db.Database.MigrateAsync();
    }

    // `add-admin <email>` makes or restores an admin, prints a one-time sign-in link, and exits.
    if (args is ["add-admin", var email])
    {
        var link = await scope.ServiceProvider.GetRequiredService<Admins>().AddAsync(email, CancellationToken.None);
        Console.WriteLine($"{email} is an admin. This link signs them in once, within an hour:");
        Console.WriteLine(link);
        return;
    }

    await scope.ServiceProvider.GetRequiredService<Admins>().AnnounceSetupAsync(CancellationToken.None);

    // A run that a restart cut off starts again.
    await app.Services.GetRequiredService<PendingRuns>().RequeueAsync(db, CancellationToken.None);
}

// Before authentication, so a refused preflight still carries CORS headers and the browser shows the real answer.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();

/// <summary>Exposed so WebApplicationFactory can find the entry point.</summary>
public partial class Program;
