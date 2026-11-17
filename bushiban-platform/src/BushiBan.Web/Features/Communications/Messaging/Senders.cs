using BushiBan.Web.Domain;

namespace BushiBan.Web.Features.Communications.Messaging;

/// <summary>What happened when a provider was asked to deliver one message.</summary>
public sealed record SendResult(MessageStatus Status, string? Error = null, string? ProviderMessageId = null)
{
    public static SendResult Sent(string? providerId = null) => new(MessageStatus.Sent, null, providerId);
    public static SendResult Failed(string error) => new(MessageStatus.Failed, error);
    public static SendResult NotConfigured(string what) => new(MessageStatus.NotConfigured, $"{what} isn't set up yet, so nothing was sent.");
}

public sealed record EmailMessage(string To, string? ToName, string Subject, string Body);

public sealed record SmsMessage(string To, string Body);

/// <summary>Delivers email. Never throws for delivery problems: returns a <see cref="SendResult"/> instead.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Delivers text messages. Never throws for delivery problems: returns a <see cref="SendResult"/> instead.</summary>
public interface ISmsSender
{
    bool IsConfigured { get; }
    Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default);
}

/// <summary>The "Email" section of appsettings.json.</summary>
public class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>The "Sms" section of appsettings.json (Twilio).</summary>
public class SmsOptions
{
    public string? Provider { get; set; } = "Twilio";
    public string? AccountSid { get; set; }
    public string? AuthToken { get; set; }
    public string? FromNumber { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken) && !string.IsNullOrWhiteSpace(FromNumber);
}
