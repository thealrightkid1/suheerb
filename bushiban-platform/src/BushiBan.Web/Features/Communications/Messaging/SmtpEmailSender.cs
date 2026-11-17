using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BushiBan.Web.Features.Communications.Messaging;

/// <summary>Sends email through any SMTP server (Office 365, Google Workspace, SendGrid SMTP…) using MailKit.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> log)
    {
        _options = options.Value;
        _log = log;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured) return SendResult.NotConfigured("Email");

        MailboxAddress to;
        try { to = new MailboxAddress(message.ToName ?? "", message.To); }
        catch (ParseException) { return SendResult.Failed($"\"{message.To}\" isn't a valid email address."); }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName ?? "", _options.FromAddress!));
        mime.To.Add(to);
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            TextBody = message.Body,
            HtmlBody = "<div style=\"font-family:Arial,sans-serif;font-size:14px;line-height:1.5\">"
                       + WebUtility.HtmlEncode(message.Body).Replace("\r\n", "\n").Replace("\n", "<br>") + "</div>",
        }.ToMessageBody();

        try
        {
            using var smtp = new SmtpClient { Timeout = 30_000 };
            var security = _options.UseStartTls ? SecureSocketOptions.StartTls
                : _options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto;
            await smtp.ConnectAsync(_options.Host!, _options.Port, security, ct);
            if (!string.IsNullOrWhiteSpace(_options.User))
                await smtp.AuthenticateAsync(_options.User, _options.Password ?? "", ct);
            // Called explicitly: MailKit's own SendResult type would clash with ours under "using MailKit".
            var response = await MailKit.IMailTransportExtensions.SendAsync(smtp, mime, ct);
            await smtp.DisconnectAsync(true, ct);
            return response.RejectedRecipients.Count > 0
                ? SendResult.Failed($"The email server refused {message.To}: {response.ResponseText}")
                : SendResult.Sent(response.ResponseText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Email to {To} failed", message.To);
            return SendResult.Failed("The email server didn't accept the message: " + ex.Message);
        }
    }
}
