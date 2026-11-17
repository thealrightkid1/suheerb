using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BushiBan.Web.Features.Communications.Messaging;

/// <summary>
/// Sends SMS through Twilio's REST API (no SDK needed): POST /2010-04-01/Accounts/{sid}/Messages.json
/// with Basic auth AccountSid:AuthToken. Registered as a typed HttpClient.
/// </summary>
public sealed class TwilioSmsSender : ISmsSender
{
    public const string BaseAddress = "https://api.twilio.com/";

    private readonly HttpClient _http;
    private readonly SmsOptions _options;
    private readonly ILogger<TwilioSmsSender> _log;

    public TwilioSmsSender(HttpClient http, IOptions<SmsOptions> options, ILogger<TwilioSmsSender> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured) return SendResult.NotConfigured("Text messaging");

        var sid = _options.AccountSid!.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{Uri.EscapeDataString(sid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = message.To,
                ["From"] = _options.FromNumber!.Trim(),
                ["Body"] = message.Body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{_options.AuthToken!.Trim()}")));

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var (twilioSid, error) = Parse(json);
            if (response.IsSuccessStatusCode) return SendResult.Sent(twilioSid);
            _log.LogWarning("Twilio rejected SMS to {To}: {Status} {Body}", message.To, (int)response.StatusCode, json);
            return SendResult.Failed("Twilio didn't send the message: " + (error ?? response.ReasonPhrase ?? "unknown error"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "SMS to {To} failed", message.To);
            return SendResult.Failed("Couldn't reach Twilio: " + ex.Message);
        }
    }

    private static (string? Sid, string? Error) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("sid"), Get("message"));
        }
        catch (JsonException) { return (null, null); }
    }
}
