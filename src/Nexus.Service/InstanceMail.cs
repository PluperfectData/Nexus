using Microsoft.Extensions.Options;
using Pluperfect.Mail;

namespace Nexus.Service;

/// <summary>
/// Sends through Pluperfect.Mail's Postmark transport with the server token the instance holds when the message goes,
/// so a token set on the Settings page takes effect at once. A folder in configuration writes mail to files instead.
/// </summary>
public sealed class InstanceMailer(Instance instance, PluperfectMailOptions options, IHttpClientFactory http, ILoggerFactory logs) : IMailer
{
    public const string HttpClientName = "postmark";

    public const string BaseAddress = "https://api.postmarkapp.com/";

    public const string NotSetUp = "This instance has no mail set up. An admin sets it up on the Settings page.";

    public async Task SendAsync(MailRequest request, CancellationToken cancellationToken = default)
    {
        switch (instance.Transport)
        {
            case MailTransport.Files:
                await new FileMailer(Options.Create(options), logs.CreateLogger<FileMailer>()).SendAsync(request, cancellationToken);
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
