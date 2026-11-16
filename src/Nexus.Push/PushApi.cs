using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Nexus.Push;

/// <summary>The push API's routes, each answer checked and each failure classified.</summary>
internal sealed class PushApi
{
    private const int MaxDetailLength = 500;

    private static readonly MediaTypeHeaderValue JsonType = new("application/json");

    private readonly HttpRetry _retry;
    private readonly Uri _root;
    private readonly Uri _sources;
    private readonly string _key;

    private PushApi(HttpRetry retry, Uri root, string key)
    {
        _retry = retry;
        _root = root;
        _sources = new Uri(root.AbsoluteUri.TrimEnd('/') + "/sources/");
        _key = key;
    }

    /// <summary>The push API at the settings' address, with their key, or a <c>ConfigInvalid</c> failure.</summary>
    public static PushApi From(ConnectorConfig config, HttpRetry retry)
    {
        var url = config.Values.GetValueOrDefault(PushDestination.UrlSetting)?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var root) || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                "The push API address must be an http or https address, such as http://localhost:6161/ingest.");
        }

        var key = config.Values.GetValueOrDefault(PushDestination.KeySetting)?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, "A push API key is required.");
        }

        return new PushApi(retry, new Uri(root.AbsoluteUri.TrimEnd('/')), key);
    }

    /// <summary>Asks the push API to name the key, which fails unless the key is one it made.</summary>
    public async Task<PushKey> CheckKeyAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, _root, null, ct).ConfigureAwait(false);
        await EnsureAsync(response, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var named = JsonDocument.Parse(body);
            var root = named.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && root.TryGetProperty("prefix", out var prefix) && prefix.ValueKind == JsonValueKind.String)
            {
                return new PushKey(name.GetString()!, prefix.GetString()!);
            }
        }
        catch (JsonException)
        {
        }

        throw new ConnectorException(
            ConnectorErrorKind.ConfigInvalid,
            $"The address {_root} took the key but did not name it, so it is not a push API. Check the push API address.");
    }

    /// <summary>Makes the table, or adds the columns it lacks.</summary>
    public async Task DefineAsync(string table, byte[] schema, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, Table(table, null), schema, ct).ConfigureAwait(false);
        await EnsureAsync(response, table, ct).ConfigureAwait(false);
    }

    /// <summary>Starts a replacement of the table with these columns, or answers false when there is no such table.</summary>
    public async Task<bool> ReplaceAsync(string table, byte[] schema, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Table(table, "replace"), schema, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureAsync(response, table, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sends one batch of rows to the table, or to its replacement.</summary>
    public async Task BatchAsync(string table, byte[] batch, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Table(table, "batches"), batch, ct).ConfigureAwait(false);
        await EnsureAsync(response, table, ct).ConfigureAwait(false);
    }

    /// <summary>Swaps the table's replacement in, or confirms the batches sent to it.</summary>
    public async Task CommitAsync(string table, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Table(table, "commit"), null, ct).ConfigureAwait(false);
        await EnsureAsync(response, table, ct).ConfigureAwait(false);
    }

    private Uri Table(string table, string? action) =>
        new(_sources, Uri.EscapeDataString(table) + (action is null ? string.Empty : "/" + action));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, byte[]? body, CancellationToken ct) =>
        _retry.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = JsonType;
                }

                return request;
            },
            ct);

    private async Task EnsureAsync(HttpResponseMessage response, string? table, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await DetailAsync(response, ct).ConfigureAwait(false);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"The push API refused the key. {detail}"),
            HttpStatusCode.Forbidden => new ConnectorException(ConnectorErrorKind.ConfigInvalid, detail),
            HttpStatusCode.NotFound => new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"No push API answered at {_root}. Check the push API address."),
            HttpStatusCode.Conflict => new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"'{table}' cannot take this stream's columns: {detail} A full refresh replaces the table."),
            _ => new ConnectorException(ConnectorErrorKind.SourceError, $"The push API answered {(int)response.StatusCode}: {detail}"),
        };
    }

    // A problem answer's detail, else its title, else the start of the body.
    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var problem = JsonDocument.Parse(body);
            if (problem.RootElement.ValueKind == JsonValueKind.Object
                && (problem.RootElement.TryGetProperty("detail", out var text) || problem.RootElement.TryGetProperty("title", out text))
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? response.StatusCode.ToString()
            : body.Length > MaxDetailLength ? body[..MaxDetailLength] : body;
    }
}
