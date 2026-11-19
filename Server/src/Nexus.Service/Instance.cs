using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Pluperfect.Mail;

namespace Nexus.Service;

/// <summary>A MySQL database, as the installer and the Settings page take it.</summary>
public sealed record DatabaseSettings(string Host, int Port, string Name, string User, string Password)
{
    public const int DefaultPort = 3306;

    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = (uint)Port,
        Database = Name,
        UserID = User,
        Password = Password,
    }.ConnectionString;

    /// <summary>Whether both name the same database on the same server, whatever their passwords.</summary>
    public bool SamePlace(DatabaseSettings other) =>
        string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) && Port == other.Port && Name == other.Name;

    /// <summary>The database a connection string names, with no password, or null when MySQL cannot read the string.</summary>
    public static DatabaseSettings? Describe(string connectionString)
    {
        try
        {
            var parts = new MySqlConnectionStringBuilder(connectionString);
            return new DatabaseSettings(parts.Server, (int)parts.Port, parts.Database, parts.UserID, string.Empty);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public enum MailTransport
{
    None,
    Postmark,
    Mailpit,
}

/// <summary>How the pages chose to send mail. An instance with no choice yet uses what it has.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MailChoice>))]
public enum MailChoice
{
    Postmark,
    Mailpit,
    Off,
}

/// <summary>
/// What the installer and the Settings page set, kept in <c>instance.json</c> in the data folder. The secrets in it are
/// encrypted with the service's Data Protection keys, which live beside it in <c>keys</c>.
/// </summary>
public sealed class InstanceFile
{
    public const string FileName = "instance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Lock _gate = new();
    private Stored _stored;

    public InstanceFile(string dataDirectory, IDataProtectionProvider provider)
    {
        _path = Path.Combine(dataDirectory, FileName);
        _protector = provider.CreateProtector("Nexus.Instance");
        _stored = Load(_path);
    }

    public string? ClientOrigin => _stored.ClientOrigin;

    public string? CallbackOrigin => _stored.CallbackOrigin;

    public string? MailFrom => _stored.Mail?.From;

    public string? MailFromName => _stored.Mail?.FromName;

    public string? ServerToken => Reveal(_stored.Mail?.ServerToken);

    public bool HasServerToken => _stored.Mail?.ServerToken is not null;

    public string? MailpitUrl => _stored.Mail?.MailpitUrl;

    public MailChoice? MailChoice => _stored.Mail?.Choice;

    public int? LogDays => _stored.LogDays;

    /// <summary>The stored database without its password, or null when none is stored.</summary>
    public DatabaseSettings? DatabasePlace =>
        _stored.Database is { } stored ? new DatabaseSettings(stored.Host, stored.Port, stored.Name, stored.User, string.Empty) : null;

    /// <summary>The stored database, or null when none is stored or the service's keys cannot decrypt its password.</summary>
    public DatabaseSettings? Database =>
        _stored.Database is { } stored && Reveal(stored.Password) is { } password
            ? new DatabaseSettings(stored.Host, stored.Port, stored.Name, stored.User, password)
            : null;

    /// <summary>The key sessions are signed with, made and kept the first time it is asked for.</summary>
    public string SigningKey
    {
        get
        {
            lock (_gate)
            {
                if (Reveal(_stored.SigningKey) is { } key)
                {
                    return key;
                }

                var made = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64));
                Save(_stored with { SigningKey = _protector.Protect(made) });
                return made;
            }
        }
    }

    public void SaveDatabase(DatabaseSettings database) =>
        Change(stored => stored with
        {
            Database = new StoredDatabase(database.Host, database.Port, database.Name, database.User, _protector.Protect(database.Password)),
        });

    public void SaveClientOrigin(string origin) => Change(stored => stored with { ClientOrigin = origin });

    public void SaveCallbackOrigin(string? origin) => Change(stored => stored with { CallbackOrigin = origin });

    /// <summary>Sends mail through Postmark, keeping the sender, and the token when one is given; a null token keeps the one stored.</summary>
    public void SavePostmark(string? serverToken, string? from, string fromName) =>
        ChangeMail(mail => mail with
        {
            ServerToken = serverToken is null ? mail.ServerToken : _protector.Protect(serverToken),
            From = from,
            FromName = fromName,
            Choice = Service.MailChoice.Postmark,
        });

    /// <summary>Sends mail to Mailpit. A null address keeps the one stored, and a null sender keeps the stored sender.</summary>
    public void SaveMailpit(string? mailpitUrl, string? from, string fromName) =>
        ChangeMail(mail => mail with
        {
            MailpitUrl = mailpitUrl ?? mail.MailpitUrl,
            From = from ?? mail.From,
            FromName = fromName,
            Choice = Service.MailChoice.Mailpit,
        });

    /// <summary>Sends no mail, keeping what each transport had, so choosing one again asks for nothing.</summary>
    public void TurnMailOff() => ChangeMail(mail => mail with { Choice = Service.MailChoice.Off });

    private void ChangeMail(Func<StoredMail, StoredMail> change) =>
        Change(stored => stored with { Mail = change(stored.Mail ?? new StoredMail(null, null, Instance.DefaultSenderName)) });

    public void SaveLogDays(int days) => Change(stored => stored with { LogDays = days });

    private void Change(Func<Stored, Stored> change)
    {
        lock (_gate)
        {
            Save(change(_stored));
        }
    }

    // Written beside the target and moved over it, so the file holds the old settings or the new, never part of either.
    private void Save(Stored next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        _stored = next;
    }

    private string? Reveal(string? stored)
    {
        if (stored is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static Stored Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Stored();
        }

        try
        {
            return JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path), Json) ?? new Stored();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{path} cannot be read ({ex.Message}). Nexus does not write over it. Fix the file, or move it out of the data folder to set the instance up again.",
                ex);
        }
    }

    internal sealed record Stored
    {
        public StoredDatabase? Database { get; init; }

        public string? ClientOrigin { get; init; }

        public string? CallbackOrigin { get; init; }

        public StoredMail? Mail { get; init; }

        public string? SigningKey { get; init; }

        public int? LogDays { get; init; }
    }

    internal sealed record StoredDatabase(string Host, int Port, string Name, string User, string Password);

    internal sealed record StoredMail(string? ServerToken, string? From, string FromName, string? MailpitUrl = null, MailChoice? Choice = null);
}

/// <summary>
/// The instance's settings as the service uses them. Each comes from configuration when an operator set it there, and
/// otherwise from what the installer and the Settings page stored.
/// </summary>
public sealed class Instance(
    InstanceFile file,
    ServiceSettings settings,
    PluperfectMailOptions mail,
    IConfiguration configuration)
{
    public const string CallbackPath = "/oauth/callback";

    public const string DefaultSenderName = "Nexus";

    public const int DefaultLogDays = 30;

    public const int MinLogDays = 1;

    public const int MaxLogDays = 365;

    // A sender for mail to Mailpit, which reaches no one.
    private const string MailpitSender = "nexus@localhost";

    public InstanceFile File => file;

    public string? ConfiguredConnectionString => Given(configuration.GetConnectionString("Nexus"));

    public string? ClientOrigin => Given(settings.ClientOrigin)?.TrimEnd('/') ?? file.ClientOrigin;

    public bool ClientOriginConfigured => Given(settings.ClientOrigin) is not null;

    /// <summary>The client's origin, once the instance knows it.</summary>
    public string Client => ClientOrigin ?? throw new InvalidOperationException("Nexus has no client address yet. Set one at /install.");

    public string? CallbackOrigin => Given(settings.CallbackOrigin)?.TrimEnd('/') ?? file.CallbackOrigin;

    public bool CallbackOriginConfigured => Given(settings.CallbackOrigin) is not null;

    /// <summary>Where providers send a browser back after signing in, or null until the address is set.</summary>
    public string? CallbackUrl => CallbackOrigin is { } origin ? origin + CallbackPath : null;

    public string? ServerToken => Given(mail.ServerToken) ?? file.ServerToken;

    public bool ServerTokenConfigured => Given(mail.ServerToken) is not null;

    /// <summary>The address mail comes from, as configured or stored.</summary>
    public string? Sender => Given(settings.MailFrom) ?? file.MailFrom;

    /// <summary>The address a message is sent from: the sender, or a placeholder for mail to Mailpit.</summary>
    public string? MailFrom => Sender ?? (Transport == MailTransport.Mailpit ? MailpitSender : null);

    public bool MailFromConfigured => Given(settings.MailFrom) is not null;

    public string MailFromName => Given(settings.MailFromName) ?? file.MailFromName ?? DefaultSenderName;

    public bool MailFromNameConfigured => Given(settings.MailFromName) is not null;

    /// <summary>Where mail to Mailpit goes, as configured or stored.</summary>
    public string? MailpitUrl => Given(mail.MailpitUrl)?.TrimEnd('/') ?? file.MailpitUrl;

    public bool MailpitConfigured => Given(mail.MailpitUrl) is not null;

    /// <summary>Where a person reads mail sent to Mailpit: Mailpit's own address unless configuration names another.</summary>
    public string? MailpitInbox => Given(settings.MailpitInbox)?.TrimEnd('/') ?? MailpitUrl;

    public MailChoice? MailChoice => file.MailChoice;

    private bool PostmarkReady => ServerToken is not null && Sender is not null;

    /// <summary>
    /// A Postmark token and sender in configuration fix the transport. Otherwise the pages' choice decides, and with no
    /// choice yet, stored Postmark settings and then a configured Mailpit.
    /// </summary>
    public MailTransport Transport =>
        ServerTokenConfigured && MailFromConfigured
            ? MailTransport.Postmark
            : file.MailChoice switch
            {
                Service.MailChoice.Postmark => PostmarkReady ? MailTransport.Postmark : MailTransport.None,
                Service.MailChoice.Mailpit => MailpitUrl is not null ? MailTransport.Mailpit : MailTransport.None,
                Service.MailChoice.Off => MailTransport.None,
                _ => PostmarkReady ? MailTransport.Postmark : MailpitConfigured ? MailTransport.Mailpit : MailTransport.None,
            };

    /// <summary>Whether the transport is one configuration fixes, which the pages cannot change.</summary>
    public bool TransportConfigured => ServerTokenConfigured && MailFromConfigured;

    public bool MailReady => Transport != MailTransport.None;

    /// <summary>How many days the operator log keeps an entry.</summary>
    public int LogDays => Math.Clamp(settings.LogDays ?? file.LogDays ?? DefaultLogDays, MinLogDays, MaxLogDays);

    public bool LogDaysConfigured => settings.LogDays is not null;

    private static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class Origins
{
    /// <summary>
    /// The value as an origin, scheme and authority only, or null when it is not an http or https address with nothing
    /// after its host.
    /// </summary>
    public static string? Parse(string? value, bool httpsOnly = false)
    {
        var text = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp);
        var bare = uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
        return schemeOk && bare ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}
