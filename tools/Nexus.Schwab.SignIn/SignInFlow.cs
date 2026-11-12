using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Nexus.Schwab.SignIn;

/// <summary>Signs in to Schwab in the browser and saves the tokens to this project's user secrets.</summary>
internal static class SignInFlow
{
    public const string AppKey = "Schwab:AppKey";
    public const string AppSecret = "Schwab:AppSecret";
    public const string CallbackUrl = "Schwab:CallbackUrl";
    public const string AccessToken = "Schwab:AccessToken";
    public const string RefreshToken = "Schwab:RefreshToken";
    public const string ExpiresAt = "Schwab:ExpiresAt";
    public const string SignedInAt = "Schwab:SignedInAt";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var assembly = typeof(SignInFlow).Assembly;
        var id = assembly.GetCustomAttribute<UserSecretsIdAttribute>()!.UserSecretsId;
        var secrets = new ConfigurationBuilder().AddUserSecrets(assembly, optional: true).Build();

        var missing = new[] { AppKey, AppSecret, CallbackUrl }.Where(key => string.IsNullOrWhiteSpace(secrets[key])).ToList();
        if (missing.Count > 0)
        {
            foreach (var key in missing)
            {
                await Console.Error.WriteLineAsync($"{key} is not set. Set it with: dotnet user-secrets set {key} \"<value>\" --id {id}");
            }

            return 1;
        }

        var callbackText = secrets[CallbackUrl]!.Trim();
        if (!Uri.TryCreate(callbackText, UriKind.Absolute, out var callback)
            || callback.Scheme != Uri.UriSchemeHttps
            || callback.Host != "127.0.0.1")
        {
            await Console.Error.WriteLineAsync(
                $"{CallbackUrl} must be https://127.0.0.1, with an optional port, and match the callback URL on your Schwab app.");
            return 1;
        }

        using var http = new HttpClient();
        var oauth = new SchwabSource(http).Spec.OAuth!;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var authorize = string.Create(
            CultureInfo.InvariantCulture,
            $"{oauth.AuthorizeUrl}?response_type=code&client_id={Uri.EscapeDataString(secrets[AppKey]!.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(callbackText)}&state={state}");

        using var certificate = CallbackListener.CreateCertificate();
        TcpListener listener;
        try
        {
            listener = CallbackListener.Listen(callback);
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Port {callback.Port} on 127.0.0.1 cannot be opened: {ex.Message}"));
            return 1;
        }

        using var listening = listener;
        var arrival = CallbackListener.WaitAsync(listener, callback, certificate, ct);

        Console.WriteLine("Opening Schwab's sign-in page. The browser will warn about the certificate on 127.0.0.1; continue past it.");
        if (!TryOpen(authorize))
        {
            Console.WriteLine($"Open this address in a browser: {authorize}");
        }

        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await arrival;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("No sign-in arrived. Run the tool again.");
            return 1;
        }

        if (query.GetValueOrDefault("state") != state)
        {
            await Console.Error.WriteLineAsync("The redirect did not come from this sign-in. Run the tool again.");
            return 1;
        }

        if (query.GetValueOrDefault("code") is not { Length: > 0 } code)
        {
            await Console.Error.WriteLineAsync($"Schwab did not sign you in: {query.GetValueOrDefault("error") ?? "no reason given"}.");
            return 1;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl)
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        $"{Uri.EscapeDataString(secrets[AppKey]!.Trim())}:{Uri.EscapeDataString(secrets[AppSecret]!.Trim())}"))),
            },
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", callbackText),
            ]),
        };

        using var response = await http.SendAsync(request, ct);
        using var body = ParseOrNull(await response.Content.ReadAsStringAsync(ct));
        var root = body?.RootElement ?? default;
        if (!response.IsSuccessStatusCode)
        {
            var reason = Text(root, "error_description") ?? Text(root, "error") ?? "no reason given";
            await Console.Error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Schwab refused the sign-in ({(int)response.StatusCode}): {reason}."));
            return 1;
        }

        if (Text(root, "access_token") is not { } access
            || Text(root, "refresh_token") is not { } refresh
            || !root.TryGetProperty("expires_in", out var lifetime)
            || !lifetime.TryGetInt64(out var seconds))
        {
            await Console.Error.WriteLineAsync("Schwab's token response is missing a token or its lifetime.");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        SecretsFile.Save(
            PathHelper.GetSecretsPathFromSecretsId(id),
            new Dictionary<string, string>
            {
                [AccessToken] = access,
                [RefreshToken] = refresh,
                [ExpiresAt] = now.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture),
                [SignedInAt] = now.ToString("O", CultureInfo.InvariantCulture),
            });

        Console.WriteLine(string.Create(
            CultureInfo.CurrentCulture,
            $"Signed in. The tokens are in user secrets {id}. Schwab ends this sign-in after 7 days, at {now.AddDays(7).ToLocalTime():f}."));
        return 0;
    }

    private static JsonDocument? ParseOrNull(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryOpen(string url)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
