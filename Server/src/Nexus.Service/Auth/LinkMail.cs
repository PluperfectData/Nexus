using System.Net;
using Nexus.Service.Storage;
using Pluperfect.Mail;

namespace Nexus.Service.Auth;

/// <summary>Mails sign-in and invite links. A link points at the client's sign-in page, with the token after the <c>#</c>.</summary>
public sealed class LinkMail(IMailer mailer, SignInLinks links, Instance instance)
{
    /// <summary>The address a link to sign <paramref name="user"/> in opens, good for <paramref name="lifetime"/>.</summary>
    public string LinkFor(User user, TimeSpan lifetime) => $"{instance.Client}/sign-in/link#{links.Issue(user, lifetime)}";

    public string InviteFor(User user) => LinkFor(user, SignInLinks.InviteLifetime);

    public Task SendSignInAsync(User user, CancellationToken ct) =>
        SendAsync(
            user,
            "Your Nexus sign-in link",
            "Open this link to sign in to Nexus. It works once, for 15 minutes.",
            LinkFor(user, SignInLinks.SignInLifetime),
            ct);

    /// <summary>Mails the user an invite link.</summary>
    /// <exception cref="MailDeliveryException">The instance has no mail set up, or the transport refused the message.</exception>
    public Task SendInviteAsync(User user, string link, CancellationToken ct) =>
        SendAsync(
            user,
            "You're invited to Nexus",
            "You have a Nexus account. Open this link to sign in for the first time. It works once, for 7 days.",
            link,
            ct);

    private Task SendAsync(User user, string subject, string lead, string link, CancellationToken ct)
    {
        if (!instance.MailReady)
        {
            throw new MailDeliveryException(InstanceMailer.NotSetUp);
        }

        var href = WebUtility.HtmlEncode(link);
        return mailer.SendAsync(
            new MailRequest
            {
                From = new MailAddress(instance.MailFrom!, instance.MailFromName),
                To = new MailAddress(user.Email),
                Subject = subject,
                HtmlBody = $"<p>{WebUtility.HtmlEncode(lead)}</p><p><a href=\"{href}\">Sign in to Nexus</a></p>"
                    + "<p>If you did not ask for this, you can ignore it.</p>",
                TextBody = $"{lead}\n\n{link}\n\nIf you did not ask for this, you can ignore it.",
            },
            ct);
    }
}
