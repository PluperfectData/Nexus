using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nexus.Schwab.SignIn;

/// <summary>Serves HTTPS on 127.0.0.1 until the browser brings back Schwab's redirect.</summary>
internal static class CallbackListener
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);

    public static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));

        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));

        // Windows TLS cannot use the ephemeral key CreateSelfSigned makes, so the certificate goes through PKCS #12.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    public static TcpListener Listen(Uri callback)
    {
        var listener = new TcpListener(IPAddress.Loopback, callback.Port);
        try
        {
            listener.Start();
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>The redirect's query parameters, once a request to the callback path carries a code or an error.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> WaitAsync(
        TcpListener listener,
        Uri callback,
        X509Certificate2 certificate,
        CancellationToken ct)
    {
        var arrived = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                var accept = listener.AcceptTcpClientAsync(stop.Token).AsTask();
                if (await Task.WhenAny(accept, arrived.Task) != accept)
                {
                    return await arrived.Task;
                }

                connections.Add(ServeAsync(await accept, callback, certificate, arrived, stop.Token));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(connections);
        }
    }

    private static async Task ServeAsync(
        TcpClient client,
        Uri callback,
        X509Certificate2 certificate,
        TaskCompletionSource<IReadOnlyDictionary<string, string>> arrived,
        CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectionTimeout);

                var tls = new SslStream(client.GetStream());
                await using (tls)
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                    if (await ReadTargetAsync(tls, timeout.Token) is not { } target)
                    {
                        return;
                    }

                    var requested = new Uri(callback, target);
                    var query = Parse(requested.Query);
                    if (requested.AbsolutePath != callback.AbsolutePath || !(query.ContainsKey("code") || query.ContainsKey("error")))
                    {
                        await RespondAsync(tls, "404 Not Found", "Not found.", timeout.Token);
                        return;
                    }

                    await RespondAsync(
                        tls,
                        "200 OK",
                        query.ContainsKey("code") ? "Signed in to Schwab. You can close this tab." : "Schwab did not sign you in. See the terminal.",
                        timeout.Token);
                    arrived.TrySetResult(query);
                }
            }
            catch (Exception ex) when (
                ex is IOException or AuthenticationException or SocketException or OperationCanceledException or UriFormatException)
            {
                // A browser that rejects the certificate drops the connection, then reconnects after its warning.
            }
        }
    }

    private static async Task<string?> ReadTargetAsync(SslStream tls, CancellationToken ct)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        while (received.Count < MaxRequestBytes)
        {
            var read = await tls.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return null;
            }

            received.AddRange(buffer[..read]);
            var text = Encoding.ASCII.GetString(received.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var requestLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
                return requestLine.Length == 3 && requestLine[1].StartsWith('/') ? requestLine[1] : null;
            }
        }

        return null;
    }

    private static Dictionary<string, string> Parse(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            values[name] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return values;
    }

    private static async Task RespondAsync(SslStream tls, string status, string message, CancellationToken ct)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Schwab sign-in</title><p>{WebUtility.HtmlEncode(message)}</p>";
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n";
        await tls.WriteAsync(Encoding.UTF8.GetBytes(head + html), ct);
        await tls.FlushAsync(ct);
    }
}
