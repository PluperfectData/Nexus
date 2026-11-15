using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nexus;
using Nexus.Push;
using Nexus.Service;
using Nexus.Service.Storage;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ServiceSettings.Section).Get<ServiceSettings>() ?? new ServiceSettings();
var connectionString = builder.Configuration.GetConnectionString("Nexus");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings:Nexus to a MySQL connection string.");
}

const string PushClient = "push";

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
// The keys that encrypt stored secrets live in the service's data directory, outside the database they protect.
builder.Services.AddDataProtection()
    .SetApplicationName("Nexus")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, settings.DataDirectory, "keys")));
builder.Services.AddDbContext<NexusDbContext>(options => options.UseMySql(connectionString, NexusDbContext.ServerVersion));
builder.Services.AddProblemDetails();
builder.Services.AddControllers().AddJsonOptions(json =>
    json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
    if (db.Database.IsMySql())
    {
        await db.Database.MigrateAsync();
    }

    // A run that a restart cut off starts again.
    await app.Services.GetRequiredService<PendingRuns>().RequeueAsync(db, CancellationToken.None);
}

app.MapControllers();

await app.RunAsync();

/// <summary>Exposed so WebApplicationFactory can find the entry point.</summary>
public partial class Program;
