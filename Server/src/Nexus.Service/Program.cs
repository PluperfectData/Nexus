using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus;
using Nexus.Push;
using Nexus.Schwab;
using Nexus.Service;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Jwt;
using Pluperfect.Mail;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ServiceSettings.Section).Get<ServiceSettings>() ?? new ServiceSettings();
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, settings.DataDirectory);

#if DEBUG
// `reset` sets aside the stored settings, so the next start opens the installer at its first step.
if (args is ["reset"])
{
    Environment.ExitCode = InstanceReset.Run(builder.Environment.IsDevelopment(), dataDirectory, builder.Configuration, Console.Out, TimeProvider.System);
    return;
}
#endif

const string PushClient = "push";

// Development runs over plain http, so its cookies drop the __Host- prefix and the Secure flag. Sessions and the
// antiforgery cookie take the same answer.
var secureOrigin = !builder.Environment.IsDevelopment();

// The proxy is the only peer, so its headers are believed from any address; the setting must stay off wherever the
// service's own port can be reached.
if (settings.Proxied)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
    });
}

// Each host owns its logger and closes its file when it stops, leaving the static Log alone. Sinks registered as
// ILogEventSink, such as a test's, join the console and the file.
builder.Services.AddSerilog(
    (services, logger) => DeveloperLog.Configure(logger, builder.Configuration, dataDirectory).ReadFrom.Services(services),
    preserveStaticLogger: true);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);

// The keys that encrypt stored secrets and mailed links live in the service's data directory, outside the database.
builder.Services.AddDataProtection()
    .SetApplicationName("Nexus")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

// What the installer and the Settings page set, and the configuration that may stand in for any of it.
builder.Services.AddSingleton(services => new InstanceFile(dataDirectory, services.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(builder.Configuration.GetSection(PluperfectMailOptions.Section).Get<PluperfectMailOptions>() ?? new PluperfectMailOptions());
builder.Services.AddSingleton<Instance>();
builder.Services.AddSingleton<IDatabaseHost, MySqlDatabaseHost>();
builder.Services.AddSingleton<InstallCode>();
builder.Services.AddSingleton<InstallAccess>();
builder.Services.AddSingleton<Installation>();
builder.Services.AddSingleton<OperatorLogWriter>();
builder.Services.AddDbContext<NexusDbContext>((services, options) =>
{
    services.GetRequiredService<IDatabaseHost>().Configure(
        options,
        services.GetRequiredService<Installation>().ConnectionString ?? throw new InvalidOperationException(InstallGate.NotSetUp));
    options.AddInterceptors(services.GetRequiredService<OperatorLogWriter>());
});
builder.Services.AddScoped<OperatorLog>();
builder.Services.AddScoped<LogPruning>();
builder.Services.AddHostedService<LogPruner>();

builder.Services.AddSingleton(builder.Configuration.GetSection(SchwabApp.Section).Get<SchwabApp>() ?? new SchwabApp());
builder.Services.AddHttpClient(OAuthApps.SchwabHttpClient);
builder.Services.AddScoped<OAuthApps>();
builder.Services.AddScoped<SignIns>();
builder.Services.AddSingleton(services => SourceConnectors.Defaults(new SchwabSource(
    services.GetRequiredService<IHttpClientFactory>().CreateClient(OAuthApps.SchwabHttpClient),
    services.GetRequiredService<TimeProvider>())));
builder.Services.AddSingleton(new ConnectionFiles(Path.Combine(dataDirectory, "files")));
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
builder.Services.AddScoped<SignInState>();
builder.Services.AddScoped<SignInMailer>();
builder.Services.AddScoped<SignInChecks>();
builder.Services.AddHostedService<SignInWatcher>();

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SignInThrottle>();
builder.Services.AddSingleton<SignInLinks>();
builder.Services.AddScoped<Sessions>();
builder.Services.AddScoped<LinkMail>();
builder.Services.AddScoped<Admins>();
builder.Services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
builder.Services.AddPluperfectJwt(builder.Configuration, allowDevelopmentKey: false, secureOrigin: secureOrigin);
builder.Services.AddOptions<PluperfectJwtOptions>().PostConfigure<InstanceFile>((options, file) =>
{
    if (string.IsNullOrWhiteSpace(options.SigningKey))
    {
        options.SigningKey = file.SigningKey;
    }
});
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AppKeyHandler>(AppKeyHandler.SchemeName, null);

builder.Services.AddHttpClient(InstanceMailer.HttpClientName, (services, client) =>
{
    client.BaseAddress = new Uri(InstanceMailer.BaseAddress);
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<PluperfectMailOptions>().TimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient(InstanceMailer.MailpitClientName, (services, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<PluperfectMailOptions>().TimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddSingleton<IMailer, InstanceMailer>();
builder.Services.AddSingleton<MailpitProbe>();

builder.Services.AddCors();
builder.Services.Replace(ServiceDescriptor.Singleton<ICorsPolicyProvider, InstanceCorsPolicy>());
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = (secureOrigin ? CookieTransport.HostPrefix : string.Empty) + "nxsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = secureOrigin ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddSingleton<ClientOriginFilter>();
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
        mvc.Filters.AddService<ClientOriginFilter>();
    })
    .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

var installation = app.Services.GetRequiredService<Installation>();
await installation.StartAsync(CancellationToken.None);

// `add-admin <email>` makes or restores an admin, prints a one-time sign-in link, and exits: an operator's way in,
// beside the installer's.
if (args is ["add-admin", var email])
{
    if (!installation.DatabaseReady)
    {
        Console.Error.WriteLine($"Nexus has no database it can open, so it cannot make an admin. {installation.DatabaseProblem ?? InstallGate.NotSetUp}");
        Environment.ExitCode = 1;
        return;
    }

    using var scope = app.Services.CreateScope();
    var link = await scope.ServiceProvider.GetRequiredService<Admins>().AddAsync(email, CancellationToken.None);
    Console.WriteLine($"{email} is an admin.");
    Console.WriteLine(link is null
        ? "The instance has no client address yet, so there is no sign-in link. Set it at /install."
        : $"This link signs them in once, within an hour:{Environment.NewLine}{link}");
    return;
}

// Before anything that reads the client's address or the scheme: the request log, antiforgery, and the install code.
if (settings.Proxied)
{
    app.UseForwardedHeaders();
}

app.UseSerilogRequestLogging(options =>
{
    // Without it the middleware writes to the static Log, which this host does not set.
    options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
    options.GetLevel = DeveloperLog.RequestLevel;
});

// Before authentication, so a refused preflight still carries CORS headers and the browser shows the real answer.
app.UseCors();
app.UseInstallGate();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try
{
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Program.LogStopped(app.Logger, ex);
    Environment.ExitCode = 1;
}

/// <summary>Exposed so WebApplicationFactory can find the entry point.</summary>
public partial class Program
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "Nexus stopped on an error it did not expect.")]
    internal static partial void LogStopped(Microsoft.Extensions.Logging.ILogger logger, Exception ex);
}
