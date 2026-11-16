using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Auth;

/// <summary>
/// The one-time code that claims an instance with no users. The service makes one when it starts with no users and
/// logs it, so only someone who can read the service's log can make the first admin. It lives in memory: a restart
/// makes another.
/// </summary>
public sealed class SetupCode
{
    // No 0, O, 1, I, or L, which read alike in a log.
    private const string Letters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly Lock _gate = new();
    private string? _code;

    /// <summary>Makes a code, and answers it, or answers null when one is already waiting.</summary>
    public string? Issue()
    {
        lock (_gate)
        {
            if (_code is not null)
            {
                return null;
            }

            var picks = RandomNumberGenerator.GetBytes(12);
            var chars = picks.Select(b => Letters[b % Letters.Length]).ToArray();
            _code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
            return _code;
        }
    }

    /// <summary>Spends the code if <paramref name="offered"/> is it, ignoring case, spaces, and dashes.</summary>
    public bool TrySpend(string? offered)
    {
        lock (_gate)
        {
            if (_code is null || offered is null)
            {
                return false;
            }

            var given = Encoding.UTF8.GetBytes(Normalize(offered));
            var wanted = Encoding.UTF8.GetBytes(Normalize(_code));
            if (!CryptographicOperations.FixedTimeEquals(given, wanted))
            {
                return false;
            }

            _code = null;
            return true;
        }
    }

    private static string Normalize(string code) =>
        new([.. code.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
}
