using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Tests;

/// <summary>
/// Hosts the real service over a SQLite file in a temporary data directory, with a fake source and push API. It runs
/// as Development, so cookies go over plain http, and mail lands as files in <see cref="MailDirectory"/>.
/// </summary>
public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public const string ClientOrigin = "http://localhost:6165";

    public ServiceFactory()
    {
        Directory.CreateDirectory(DataDirectory);
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "nexus-service-tests", Guid.NewGuid().ToString("N"));

    public string MailDirectory => Path.Combine(DataDirectory, "mail");

    /// <summary>How many files connections hold in the service's files directory.</summary>
    public int StoredFileCount => Directory.Exists(Path.Combine(DataDirectory, "files")) ? Directory.GetFiles(Path.Combine(DataDirectory, "files")).Length : 0;

    /// <summary>Every line the service logged, as written.</summary>
    internal LogCapture Logs { get; } = new();

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

    /// <summary>Adds a user, with a password when one is given.</summary>
    public async Task<User> AddUserAsync(string email, string? password = null, bool admin = false)
    {
        await using var db = NewContext();
        var user = new User { Email = email, IsAdmin = admin, LinkStamp = SignInLinks.NewStamp(), CreatedAt = DateTime.UtcNow };
        if (password is not null)
        {
            user.PasswordHash = Passwords.Hash(user, password);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A browser with its own cookies, signed out.</summary>
    public Browser NewBrowser()
    {
        var cookies = new System.Net.CookieContainer();
        var client = CreateDefaultClient(new Uri("http://localhost/"), new CookieContainerHandler(cookies));
        return new Browser(client, cookies);
    }

    /// <summary>A browser signed in as a new user with a password.</summary>
    public async Task<Browser> SignedInAsync(string email, bool admin = false)
    {
        await AddUserAsync(email, Browser.Password, admin);
        var browser = NewBrowser();
        (await browser.SignInAsync(email, Browser.Password)).EnsureSuccessStatusCode();
        return browser;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Nexus", "Server=unused");
        builder.UseSetting($"{ServiceSettings.Section}:{nameof(ServiceSettings.DataDirectory)}", DataDirectory);
        builder.UseSetting($"{ServiceSettings.Section}:{nameof(ServiceSettings.ClientOrigin)}", ClientOrigin);
        builder.UseSetting("Mail:PickupDirectory", MailDirectory);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<NexusDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NexusDbContext>>();
            services.AddDbContext<NexusDbContext>(options => options.UseSqlite(SqliteConnectionString));
            services.RemoveAll<SourceConnectors>();
            services.AddSingleton(new SourceConnectors([Source, new Nexus.Csv.CsvSource()]));
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
