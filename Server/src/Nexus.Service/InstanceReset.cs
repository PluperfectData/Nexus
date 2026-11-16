#if DEBUG
using System.Globalization;

namespace Nexus.Service;

/// <summary>
/// The <c>reset</c> command, compiled into Debug builds alone and refused outside Development. It sets aside what the
/// installer and the Settings page stored, so the next start opens the installer at its first step. The database, the
/// encryption keys in <c>data/keys</c>, and uploaded files stay.
/// </summary>
public static class InstanceReset
{
    private static readonly (string Key, string Effect)[] Configured =
    [
        ("ConnectionStrings:Nexus", "the installer skips its database step"),
        ("Nexus:ClientOrigin", "the installer skips the client's address"),
        ("Nexus:CallbackOrigin", "the callback address is fixed"),
        ("Mail:ServerToken", "Postmark's token is fixed"),
        ("Nexus:MailFrom", "the sender is fixed"),
        ("Nexus:MailFromName", "the sender's name is fixed"),
        ("Jwt:SigningKey", "the signing key is fixed"),
        ("Nexus:Schwab:AppKey", "every workspace signs in to Schwab through that app"),
        ("Nexus:LogDays", "how long the log keeps entries is fixed"),
    ];

    /// <summary>Answers the exit code, having said what it did.</summary>
    public static int Run(bool development, string dataDirectory, IConfiguration configuration, TextWriter output, TimeProvider time)
    {
        if (!development)
        {
            output.WriteLine("reset runs only in Development, so an instance in use cannot be reset.");
            return 1;
        }

        var stored = Path.Combine(dataDirectory, InstanceFile.FileName);
        if (File.Exists(stored))
        {
            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var aside = Path.Combine(dataDirectory, $"instance-before-reset-{stamp}.json");
            File.Move(stored, aside);
            output.WriteLine($"Set aside the stored settings in {aside}. Move it back to {stored} to undo the reset.");
        }
        else
        {
            output.WriteLine("Nexus has no stored settings to set aside.");
        }

        foreach (var (key, effect) in Configured)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                output.WriteLine($"{key} is set in this machine's configuration, so {effect}.");
            }
        }

        output.WriteLine("The database, data/keys, and data/files are kept. Start the service, then open /install on its client.");
        return 0;
    }
}
#endif
