using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Auth;

/// <summary>
/// The code that lets a page on another machine use the installer. The service writes it to its log, so only someone
/// who can read the log can set the instance up or make an admin. It lives in memory: a restart makes another.
/// </summary>
public sealed class InstallCode(TimeProvider time)
{
    /// <summary>What the code follows in an <c>Authorization: Bearer</c> header.</summary>
    public const string Lead = "nxi_";

    // No 0, O, 1, I, or L, which read alike in a log.
    private const string Letters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly TimeSpan AnnounceGap = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private string? _code;
    private DateTimeOffset? _expires;
    private DateTimeOffset? _announced;

    /// <summary>The code waiting, or a new one, which lasts for <paramref name="lifetime"/> or, when null, until cleared.</summary>
    public string Issue(TimeSpan? lifetime)
    {
        lock (_gate)
        {
            if (LiveCode() is { } live)
            {
                return live;
            }

            var picks = RandomNumberGenerator.GetBytes(12);
            var chars = picks.Select(b => Letters[b % Letters.Length]).ToArray();
            _code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
            _expires = lifetime is { } span ? time.GetUtcNow() + span : null;
            return _code;
        }
    }

    /// <summary>Whether <paramref name="offered"/> is the live code, ignoring case, spaces, and dashes.</summary>
    public bool Matches(string? offered)
    {
        lock (_gate)
        {
            if (LiveCode() is not { } live || offered is null)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Normalize(offered)), Encoding.UTF8.GetBytes(Normalize(live)));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _code = null;
            _expires = null;
        }
    }

    /// <summary>Whether the code may be written to the log now: at most once a minute, so asking cannot flood it.</summary>
    public bool MayAnnounce()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_announced is { } last && now - last < AnnounceGap)
            {
                return false;
            }

            _announced = now;
            return true;
        }
    }

    private string? LiveCode() => _code is not null && (_expires is null || _expires > time.GetUtcNow()) ? _code : null;

    private static string Normalize(string code) =>
        new([.. code.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
}

public enum InstallAccessResult
{
    Allowed,
    NeedsCode,
    WrongCode,
    LockedOut,
}

/// <summary>
/// Who may use the installer: a page on the machine the service runs on, or a page holding the install code. Wrong
/// codes are throttled, as wrong passwords are.
/// </summary>
public sealed class InstallAccess(InstallCode code, SignInThrottle throttle)
{
    // One budget for every try, since the code, not an address, is what is being guessed.
    private const string ThrottleKey = "install";

    public InstallAccessResult Check(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer " + InstallCode.Lead, StringComparison.Ordinal))
        {
            if (throttle.IsLockedOut(ThrottleKey))
            {
                return InstallAccessResult.LockedOut;
            }

            if (code.Matches(header[("Bearer " + InstallCode.Lead).Length..]))
            {
                return InstallAccessResult.Allowed;
            }

            throttle.RecordFailure(ThrottleKey);
            return InstallAccessResult.WrongCode;
        }

        return IsLocal(http) ? InstallAccessResult.Allowed : InstallAccessResult.NeedsCode;
    }

    /// <summary>
    /// Whether the request comes from this machine, directly: a loopback connection with no proxy's forwarding headers,
    /// addressed to a loopback name, and, from a browser, sent by a page on a loopback name. The last two keep another
    /// site open in a local browser out, including one that points its own name at this machine.
    /// </summary>
    public static bool IsLocal(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            return false;
        }

        var headers = http.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP"))
        {
            return false;
        }

        if (!IsLoopbackName($"http://{http.Request.Host.Value}"))
        {
            return false;
        }

        var origin = headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || IsLoopbackName(origin);
    }

    private static bool IsLoopbackName(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
