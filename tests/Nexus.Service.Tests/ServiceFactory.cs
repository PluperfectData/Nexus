using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Storage;

namespace Nexus.Service.Tests;

/// <summary>Hosts the real service over a SQLite file in a temporary data directory, with a fake source and push API.</summary>
public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public ServiceFactory()
    {
        Directory.CreateDirectory(DataDirectory);
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "nexus-service-tests", Guid.NewGuid().ToString("N"));

    internal FakeSource Source { get; } = new();

    internal FakePush Push { get; } = new();

    // Every context opens its own connection, since the worker runs beside the tests. Without pooling, no connection
    // holds the file open once its context is done, so the directory can be deleted.
    private string SqliteConnectionString => $"Data Source={Path.Combine(DataDirectory, "nexus.db")};Pooling=False";

    /// <summary>A context on the service's database, usable before the service starts.</summary>
    public NexusDbContext NewContext() => new(new DbContextOptionsBuilder<NexusDbContext>().UseSqlite(SqliteConnectionString).Options);

    /// <summary>The service's secrets, from the same keys, usable before the service starts.</summary>
    public Secrets NewSecrets() =>
        new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DataDirectory, "keys")), builder => builder.SetApplicationName("Nexus")));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Nexus", "Server=unused");
        builder.UseSetting($"{ServiceSettings.Section}:{nameof(ServiceSettings.DataDirectory)}", DataDirectory);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<NexusDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NexusDbContext>>();
            services.AddDbContext<NexusDbContext>(options => options.UseSqlite(SqliteConnectionString));
            services.RemoveAll<SourceConnectors>();
            services.AddSingleton(new SourceConnectors([Source]));
            services.RemoveAll<HttpRetryOptions>();
            services.AddSingleton(new HttpRetryOptions { MaxAttempts = 1 });
            services.RemoveAll<RetryPolicy>();
            services.AddSingleton(new RetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));
            services.AddHttpClient("push").ConfigurePrimaryHttpMessageHandler(() => Push);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
