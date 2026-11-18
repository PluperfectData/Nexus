using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;

namespace Nexus.Service.Tests;

/// <summary>
/// A browser on the client's origin: it keeps cookies, and sends every unsafe request with a fresh CSRF token and the
/// client's <c>Origin</c>, as Pluperfect.Jwt's client does.
/// </summary>
public sealed partial class Browser(HttpClient client, CookieContainer cookies) : IDisposable
{
    public const string Password = "correct horse battery";

    public CookieContainer Cookies { get; } = cookies;

    public HttpClient Client { get; } = client;

    public Task<HttpResponseMessage> SignInAsync(string email, string password, bool remember = false) =>
        SendAsync(HttpMethod.Post, "/auth/sign-in", new { email, password, remember });

    public async Task<T> GetAsync<T>(string path) => (await Client.GetFromJsonAsync<T>(path, Json))!;

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool csrf = true, string origin = ServiceFactory.ClientOrigin)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (csrf)
        {
            var token = await Client.GetFromJsonAsync<JsonElement>("/auth/csrf");
            request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        }

        request.Headers.Add("Origin", origin);
        return await Client.SendAsync(request);
    }

    /// <summary>Sends a multipart form, such as one carrying a file, as the client's pages do.</summary>
    public async Task<HttpResponseMessage> SendFormAsync(HttpMethod method, string path, MultipartFormDataContent form)
    {
        using var request = new HttpRequestMessage(method, path) { Content = form };
        var token = await Client.GetFromJsonAsync<JsonElement>("/auth/csrf");
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        request.Headers.Add("Origin", ServiceFactory.ClientOrigin);
        return await Client.SendAsync(request);
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var response = await SendAsync(method, path, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public void Dispose() => Client.Dispose();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The problem detail an answer carries.</summary>
    public static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("detail", out var detail) ? detail.GetString() : null;

    /// <summary>The mail written to the pickup folder for an address, newest last.</summary>
    public static List<MimeMessage> MailTo(string directory, string email) =>
        !Directory.Exists(directory)
            ? []
            : [.. Directory.GetFiles(directory, "*.eml")
                .Select(path => (Written: File.GetLastWriteTimeUtc(path), Message: MimeMessage.Load(path)))
                .Where(mail => mail.Message.To.Mailboxes.Any(box => box.Address == email))
                .OrderBy(mail => mail.Written)
                .Select(mail => mail.Message)];

    /// <summary>The token after the <c>#</c> of the sign-in link in a mail.</summary>
    public static string LinkToken(MimeMessage mail) => LinkPattern().Match(mail.TextBody ?? string.Empty).Groups[1].Value;

    /// <summary>The mail the fake Postmark sent an address, oldest first.</summary>
    internal static List<JsonElement> PostmarkTo(FakePostmark postmark, string email) =>
        [.. postmark.Sent.Where(mail => mail.GetProperty("To").GetString() == email)];

    /// <summary>The token after the <c>#</c> of the sign-in link in a mail the fake Postmark sent.</summary>
    public static string LinkToken(JsonElement mail) => LinkPattern().Match(mail.GetProperty("TextBody").GetString() ?? string.Empty).Groups[1].Value;

    [GeneratedRegex(@"/sign-in/link#(\S+)")]
    private static partial Regex LinkPattern();
}
