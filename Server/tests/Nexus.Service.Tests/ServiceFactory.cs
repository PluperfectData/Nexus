using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Serilog.Core;

namespace Nexus.Service.Tests;

/// <summary>
/// Hosts the real service over a SQLite file in a temporary data directory, with a fake source and push API. It runs
/// as Development, so cookies go over plain http, and mail lands as files in <see cref="MailDirectory"/> unless the
/// factory sends it to <see cref="Postmark"/>. An installed factory starts as the installer leaves an instance: a
/// stored database, client address, and callback address, and an owner who is an admin.
/// </summary>
public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public const string ClientOrigin = "http://localhost:6165";

    public const string CallbackOrigin = "https://127.0.0.1:6164";

    public const string OwnerEmail = "owner@nexus.test";

    /// <summary>A request carrying this header reaches the service as one from this machine.</summary>
    public const string LocalHeader = "X-Test-Local";

    public static readonly DatabaseSettings StoredDatabase = new("db.nexus.test", 3306, "nexus", "nexus", "stored-password");

    private static readonly string[] ConfiguredKeys =
    [
        "ConnectionStrings:Nexus",
        "Jwt:SigningKey",
        "Mail:ServerToken",
        $"{ServiceSettings.Section}:{nameof(ServiceSettings.ClientOrigin)}",
        $"{ServiceSettings.Section}:{nameof(ServiceSettings.CallbackOrigin)}",
        $"{ServiceSettings.Section}:{nameof(ServiceSettings.MailFrom)}",
        $"{ServiceSettings.Section}:{nameof(ServiceSettings.MailFromName)}",
        $"{ServiceSettings.Section}:{nameof(ServiceSettings.LogDays)}",
        $"{SchwabApp.Section}:{nameof(SchwabApp.CallbackUrl)}",
        $"{SchwabApp.Section}:{nameof(SchwabApp.AppKey)}",
        $"{SchwabApp.Section}:{nameof(SchwabApp.AppSecret)}",
    ];

    private readonly bool _mailToFiles;

    public ServiceFactory(bool installed = true, bool mailToFiles = true)
    {
        _mailToFiles = mailToFiles;
        Directory.CreateDirectory(DataDirectory);
        using var db = NewContext();
        db.Database.EnsureCreated();
        Database = new SqliteDatabaseHost(SqliteConnectionString);
        if (installed)
        {
            var file = NewInstanceFile();
            file.SaveDatabase(StoredDatabase);
            file.SaveClientOrigin(ClientOrigin);
            file.SaveCallbackOrigin(CallbackOrigin);
            db.Users.Add(new User { Email = OwnerEmail, IsAdmin = true, LinkStamp = SignInLinks.NewStamp(), CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "nexus-service-tests", Guid.NewGuid().ToString("N"));

    public string MailDirectory => Path.Combine(DataDirectory, "mail");

    /// <summary>How many files connections hold in the service's files directory.</summary>
    public int StoredFileCount => Directory.Exists(Path.Combine(DataDirectory, "files")) ? Directory.GetFiles(Path.Combine(DataDirectory, "files")).Length : 0;

    /// <summary>Every line the service logged, as written.</summary>
    internal LogCapture Logs { get; } = new();

    internal FakeSource Source { get; } = new();

    internal FakePush Push { get; } = new();

    internal FakeSchwab Schwab { get; } = new();

    internal FakePostmark Postmark { get; } = new();

    /// <summary>Connectors the service reads through besides the usual ones, added before the service starts.</summary>
    internal List<ISourceConnector> MoreConnectors { get; } = [];

    /// <summary>The database the service opens, whatever it is told to connect to.</summary>
    internal SqliteDatabaseHost Database { get; }

    // Every context opens its own connection, since the worker runs beside the tests. Without pooling, no connection
    // holds the file open once its context is done, so the directory can be deleted.
    private string SqliteConnectionString => $"Data Source={Path.Combine(DataDirectory, "nexus.db")};Pooling=False";

    /// <summary>A context on the service's database, usable before the service starts.</summary>
    public NexusDbContext NewContext() => new(new DbContextOptionsBuilder<NexusDbContext>().UseSqlite(SqliteConnectionString).Options);

    /// <summary>The service's secrets, from the same keys, usable before the service starts.</summary>
    public Secrets NewSecrets() => new(NewProtection());

    /// <summary>The sender an instance from <see cref="WithPostmark"/> mails from.</summary>
    public const string MailSender = "nexus@acme.test";

    /// <summary>An installed factory whose mail goes through <see cref="Postmark"/> from the start, as a Release build's does.</summary>
    public static ServiceFactory WithPostmark()
    {
        var factory = new ServiceFactory(mailToFiles: false);
        factory.NewInstanceFile().SaveMail(FakePostmark.Token, MailSender, Instance.DefaultSenderName);
        return factory;
    }

    /// <summary>The service's stored settings, from the same file and keys, usable before the service starts.</summary>
    public InstanceFile NewInstanceFile() => new(DataDirectory, NewProtection());

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

    public async Task<int> OwnerIdAsync()
    {
        await using var db = NewContext();
        return await db.Users.Where(u => u.Email == OwnerEmail).Select(u => u.Id).SingleAsync();
    }

    /// <summary>A browser with its own cookies, signed out.</summary>
    public Browser NewBrowser()
    {
        var cookies = new CookieContainer();
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

        // Development reads this machine's user secrets, so each value an operator may configure is blanked here, and a
        // test that wants one sets it.
        foreach (var key in ConfiguredKeys)
        {
            builder.UseSetting(key, string.Empty);
        }

        builder.UseSetting($"{ServiceSettings.Section}:{nameof(ServiceSettings.DataDirectory)}", DataDirectory);
        builder.UseSetting("Mail:PickupDirectory", _mailToFiles ? MailDirectory : string.Empty);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(Logs);
            services.RemoveAll<IDatabaseHost>();
            services.AddSingleton<IDatabaseHost>(Database);
            services.RemoveAll<SourceConnectors>();
            services.AddSingleton(new SourceConnectors([Source, new Nexus.Csv.CsvSource(), new Nexus.Schwab.SchwabSource(new HttpClient(Schwab)), .. MoreConnectors]));

            // Tests run the sign-in checks themselves, at the moment they choose.
            services.Remove(services.Single(d => d.ImplementationType == typeof(SignInWatcher)));
            services.AddHttpClient(OAuthApps.SchwabHttpClient).ConfigurePrimaryHttpMessageHandler(() => Schwab);
            services.AddHttpClient(InstanceMailer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Postmark);
            services.RemoveAll<HttpRetryOptions>();
            services.AddSingleton(new HttpRetryOptions { MaxAttempts = 1 });
            services.RemoveAll<RetryPolicy>();
            services.AddSingleton(new RetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));
            services.AddHttpClient("push").ConfigurePrimaryHttpMessageHandler(() => Push);
            services.AddSingleton<IStartupFilter, PretendLocal>();
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

    private IDataProtectionProvider NewProtection() =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DataDirectory, "keys")), builder => builder.SetApplicationName("Nexus"));

    // The test server gives no remote address, so a request reaches the service from this machine only when it asks to.
    private sealed class PretendLocal : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, after) =>
            {
                if (http.Request.Headers.Remove(LocalHeader))
                {
                    http.Connection.RemoteIpAddress = IPAddress.Loopback;
                }

                return after(http);
            });
            next(app);
        };
    }
}

/// <summary>Opens the test's SQLite file whatever database the service is told to use, and refuses when told to.</summary>
internal sealed class SqliteDatabaseHost(string connectionString) : IDatabaseHost
{
    /// <summary>Why the next databases are refused, in the words a server would use, or null to open them.</summary>
    public string? Refusal { get; set; }

    /// <summary>Each connection string the service asked to open.</summary>
    public List<string> Prepared { get; } = [];

    public void Configure(DbContextOptionsBuilder options, string given) => options.UseSqlite(connectionString);

    public async Task PrepareAsync(string given, CancellationToken ct)
    {
        Prepared.Add(given);
        if (Refusal is { } refusal)
        {
            throw new DatabaseException(refusal);
        }

        await using var db = new NexusDbContext(new DbContextOptionsBuilder<NexusDbContext>().UseSqlite(connectionString).Options);
        await db.Database.EnsureCreatedAsync(ct);
    }
}
