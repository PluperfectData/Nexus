using Pluperfect.Mail;

namespace Nexus.Service;

/// <summary>
/// Sends through the transport the instance has when the message goes, so a change on the Settings page takes effect at
/// once: Postmark with the server token it holds, or Mailpit at the address it holds.
/// </summary>
public sealed class InstanceMailer(Instance instance, IHttpClientFactory http, ILoggerFactory logs) : IMailer
{
    public const string HttpClientName = "postmark";

    public const string MailpitClientName = "mailpit";

    public const string BaseAddress = "https://api.postmarkapp.com/";

    public const string NotSetUp = "This instance has no mail set up. An admin sets it up on the Settings page.";

    public async Task SendAsync(MailRequest request, CancellationToken cancellationToken = default)
    {
        switch (instance.Transport)
        {
            case MailTransport.Mailpit:
                using (var client = http.CreateClient(MailpitClientName))
                {
                    client.BaseAddress = MailpitMailer.BaseAddressFor(instance.MailpitUrl!);
                    await new MailpitMailer(client, logs.CreateLogger<MailpitMailer>()).SendAsync(request, cancellationToken);
                }

                return;
            case MailTransport.Postmark:
                using (var client = http.CreateClient(HttpClientName))
                {
                    client.DefaultRequestHeaders.Add("X-Postmark-Server-Token", instance.ServerToken);
                    await new PostmarkMailer(client, logs.CreateLogger<PostmarkMailer>()).SendAsync(request, cancellationToken);
                }

                return;
            default:
                throw new MailDeliveryException(NotSetUp);
        }
    }
}

/// <summary>Whether Mailpit answers at an address, before an admin comes to rely on the links it keeps.</summary>
public sealed class MailpitProbe(IHttpClientFactory http)
{
    public async Task<bool> AnswersAsync(string mailpitUrl, CancellationToken ct)
    {
        using var client = http.CreateClient(InstanceMailer.MailpitClientName);
        client.BaseAddress = MailpitMailer.BaseAddressFor(mailpitUrl);
        try
        {
            using var response = await client.GetAsync("api/v1/info", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
