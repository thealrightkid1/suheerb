using System.Security.Claims;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Communication;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Communications.Messaging;

public enum AudienceKind { Students = 1, Leads = 2 }

/// <summary>Who a bulk message goes to. Every filter is optional.</summary>
public class AudienceFilter
{
    public AudienceKind Kind { get; set; } = AudienceKind.Students;
    public int? CourseId { get; set; }
    public int? RankId { get; set; }
    /// <summary>"active" (default), "inactive" or "all".</summary>
    public string? Status { get; set; } = "active";
    public AgeGroup? AgeGroup { get; set; }
    public ProspectPhase? Phase { get; set; }
    public LeadSource? Source { get; set; }
}

public sealed record MessageDraft(MessageChannel Channel, string? Subject, string Body);

public sealed record AudiencePreview(int Total, int Reachable, int Unsubscribed, int NoAddress, int Limit)
{
    public bool OverLimit => Reachable > Limit;
}

public sealed record SingleSendOutcome(bool Attempted, MessageStatus? Status, string Message, int? MessageLogId = null);

public sealed record BulkSendOutcome(Guid? BatchId, int Sent, int NotConfigured, int Failed, int Unsubscribed, int NoAddress, string? Error = null)
{
    public int Logged => Sent + NotConfigured + Failed;
}

/// <summary>A student or lead as far as messaging is concerned.</summary>
public sealed record MessageRecipient(int? StudentId, int? LeadId, string FirstName, string LastName, string? Email, string? Phone,
    bool EmailOptIn, bool SmsOptIn, string? Program)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
    public string? E164 => PhoneNumbers.ToE164(Phone);
    public string? EmailAddress => IsValidEmail(Email) ? Email!.Trim() : null;

    /// <summary>The address to use on this channel, or why we can't use it.</summary>
    public (string? Address, SkipReason? Skip) AddressFor(MessageChannel channel)
    {
        var address = channel == MessageChannel.Email ? EmailAddress : E164;
        if (address is null) return (null, SkipReason.NoAddress);
        var optedIn = channel == MessageChannel.Email ? EmailOptIn : SmsOptIn;
        return optedIn ? (address, null) : (address, SkipReason.Unsubscribed);
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var a) && a.Address == email.Trim();
}

public enum SkipReason { NoAddress = 1, Unsubscribed = 2 }

/// <summary>
/// Sends email and SMS to students and leads: fills in templates, respects unsubscribes, logs every attempt
/// to MessageLog and adds an Email/SMS activity to the person's timeline. Works the same when no provider
/// is set up: the message is logged as "Not sent: provider not set up".
/// </summary>
public class MessagingService
{
    /// <summary>Largest bulk send allowed in one go. Larger audiences must be split with filters.</summary>
    public const int MaxBulkRecipients = 1000;
    /// <summary>Bulk sends are saved every this many messages so a long send never holds a huge transaction.</summary>
    public const int BatchSize = 25;
    public const int MaxSmsLength = 1600;
    public const int MaxEmailLength = 50_000;

    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly ISmsSender _sms;
    private readonly ICurrentBranch _branch;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<MessagingService> _log;

    public MessagingService(AppDbContext db, IEmailSender email, ISmsSender sms, ICurrentBranch branch, IHttpContextAccessor http, ILogger<MessagingService> log)
    {
        _db = db; _email = email; _sms = sms; _branch = branch; _http = http; _log = log;
    }

    public bool EmailConfigured => _email.IsConfigured;
    public bool SmsConfigured => _sms.IsConfigured;

    /// <summary>Checks a draft before sending. Returns a plain-English problem, or null when it's fine.</summary>
    public static string? Validate(MessageDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Body)) return "Write a message first.";
        if (draft.Channel == MessageChannel.Email)
        {
            if (string.IsNullOrWhiteSpace(draft.Subject)) return "Emails need a subject.";
            if (draft.Subject.Length > 300) return "The subject is too long (300 characters at most).";
            if (draft.Body.Length > MaxEmailLength) return "The email is too long.";
        }
        else if (draft.Body.Length > MaxSmsLength) return $"Text messages can be at most {MaxSmsLength:N0} characters.";
        return null;
    }

    // ----- Recipients -----

    public Task<MessageRecipient?> StudentAsync(int id, CancellationToken ct = default) =>
        StudentRecipients(_db.Students.Where(s => s.Id == id)).FirstOrDefaultAsync(ct);

    public Task<MessageRecipient?> LeadAsync(int id, CancellationToken ct = default) =>
        LeadRecipients(_db.Leads.Where(l => l.Id == id)).FirstOrDefaultAsync(ct);

    /// <summary>Students and leads matching a name, email or phone, for the "send to one person" picker.</summary>
    public async Task<List<MessageRecipient>> SearchAsync(string term, int take = 15, CancellationToken ct = default)
    {
        term = term.Trim();
        if (term.Length < 2) return new();
        var students = await StudentRecipients(_db.Students.Where(s => (s.FirstName + " " + s.LastName).Contains(term)
                || (s.Email1 != null && s.Email1.Contains(term)) || (s.Phone1 != null && s.Phone1.Contains(term)) || s.StudentNo.Contains(term))
            .OrderBy(s => s.FirstName).ThenBy(s => s.LastName).Take(take)).ToListAsync(ct);
        var leads = await LeadRecipients(_db.Leads.Where(l => l.ConvertedStudentId == null && ((l.FirstName + " " + l.LastName).Contains(term)
                || (l.Email1 != null && l.Email1.Contains(term)) || (l.Phone1 != null && l.Phone1.Contains(term))))
            .OrderBy(l => l.FirstName).ThenBy(l => l.LastName).Take(take)).ToListAsync(ct);
        return students.Concat(leads).Take(take).ToList();
    }

    public async Task<List<MessageRecipient>> AudienceAsync(AudienceFilter f, CancellationToken ct = default)
    {
        if (f.Kind == AudienceKind.Leads)
        {
            var q = _db.Leads.Where(l => l.ConvertedStudentId == null);
            q = f.Status switch { "inactive" => q.Where(l => !l.IsActive), "all" => q, _ => q.Where(l => l.IsActive) };
            if (f.CourseId is int c) q = q.Where(l => l.CourseId == c);
            if (f.RankId is int r) q = q.Where(l => l.RankId == r);
            if (f.AgeGroup is { } ag) q = q.Where(l => l.AgeGroup == ag);
            if (f.Phase is { } ph) q = q.Where(l => l.ProspectPhase == ph);
            if (f.Source is { } src) q = q.Where(l => l.Source == src);
            return await LeadRecipients(q).ToListAsync(ct);
        }
        var s = _db.Students.AsQueryable();
        s = f.Status switch { "inactive" => s.Where(x => !x.IsActive), "all" => s, _ => s.Where(x => x.IsActive) };
        if (f.CourseId is int sc) s = s.Where(x => x.CourseId == sc);
        if (f.RankId is int sr) s = s.Where(x => x.RankId == sr);
        if (f.AgeGroup is { } sag) s = s.Where(x => x.AgeGroup == sag);
        return await StudentRecipients(s).ToListAsync(ct);
    }

    public async Task<AudiencePreview> PreviewAsync(AudienceFilter filter, MessageChannel channel, CancellationToken ct = default)
    {
        var people = await AudienceAsync(filter, ct);
        var skips = people.Select(p => p.AddressFor(channel).Skip).ToList();
        return new AudiencePreview(people.Count, skips.Count(s => s is null), skips.Count(s => s == SkipReason.Unsubscribed),
            skips.Count(s => s == SkipReason.NoAddress), MaxBulkRecipients);
    }

    private static IQueryable<MessageRecipient> StudentRecipients(IQueryable<Student> q) =>
        q.Select(s => new MessageRecipient(s.Id, null, s.FirstName, s.LastName, s.Email1, s.Phone1, s.IsEmail1Subscriber, s.IsPhone1Subscriber,
            s.Course != null ? s.Course.Name : null));

    // Leads have no unsubscribe flags: they asked us to contact them.
    private static IQueryable<MessageRecipient> LeadRecipients(IQueryable<Lead> q) =>
        q.Select(l => new MessageRecipient(null, l.Id, l.FirstName, l.LastName, l.Email1, l.Phone1, true, true,
            l.Course != null ? l.Course.Name : null));

    // ----- Rendering -----

    /// <summary>Merge values for one person. Balance is the total still owed on the student's open invoices.</summary>
    public async Task<Dictionary<string, string?>> MergeValuesAsync(MessageRecipient person, CancellationToken ct = default)
    {
        var balances = person.StudentId is int id ? await BalancesAsync(new[] { id }, ct) : new Dictionary<int, decimal>();
        return MergeValues(person, await BranchNameAsync(ct), balances);
    }

    private static Dictionary<string, string?> MergeValues(MessageRecipient p, string branchName, IReadOnlyDictionary<int, decimal> balances) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = p.FirstName,
            ["LastName"] = p.LastName,
            ["BranchName"] = branchName,
            ["Program"] = p.Program,
            ["Balance"] = p.StudentId is int id ? Format.Money(balances.TryGetValue(id, out var b) ? b : 0m) : null,
        };

    private async Task<string> BranchNameAsync(CancellationToken ct) =>
        _branch.BranchId is int b ? await _db.Branches.Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "" : "";

    /// <summary>Open balance per student: invoice lines minus payments, for invoices that are open or partly paid.</summary>
    public async Task<Dictionary<int, decimal>> BalancesAsync(IReadOnlyCollection<int> studentIds, CancellationToken ct = default)
    {
        if (studentIds.Count == 0) return new();
        var open = await _db.Invoices
            .Where(i => studentIds.Contains(i.StudentId) && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.PartiallyPaid))
            .Select(i => new { i.StudentId, Lines = i.Lines.Select(l => l.Amount).ToList(), Paid = i.Allocations.Select(a => a.Amount).ToList() })
            .ToListAsync(ct);
        return open.GroupBy(i => i.StudentId).ToDictionary(g => g.Key, g => g.Sum(i => i.Lines.Sum() - i.Paid.Sum()));
    }

    // ----- Sending -----

    public async Task<SingleSendOutcome> SendToStudentAsync(int studentId, MessageDraft draft, CancellationToken ct = default)
    {
        var person = await StudentAsync(studentId, ct);
        return person is null ? new(false, null, "That student wasn't found.") : await SendAsync(person, draft, ct);
    }

    public async Task<SingleSendOutcome> SendToLeadAsync(int leadId, MessageDraft draft, CancellationToken ct = default)
    {
        var person = await LeadAsync(leadId, ct);
        return person is null ? new(false, null, "That lead wasn't found.") : await SendAsync(person, draft, ct);
    }

    public async Task<SingleSendOutcome> SendAsync(MessageRecipient person, MessageDraft draft, CancellationToken ct = default)
    {
        if (Validate(draft) is { } problem) return new(false, null, problem);
        var what = draft.Channel == MessageChannel.Email ? "email" : "text messages";
        var (address, skip) = person.AddressFor(draft.Channel);
        if (skip == SkipReason.NoAddress)
            return new(false, null, draft.Channel == MessageChannel.Email
                ? $"{person.FullName} has no valid email address on file, so nothing was sent."
                : $"{person.FullName} has no valid mobile number on file, so nothing was sent.");
        if (skip == SkipReason.Unsubscribed)
            return new(false, null, $"{person.FullName} has unsubscribed from {what}, so nothing was sent.");

        var values = await MergeValuesAsync(person, ct);
        var log = await DeliverAsync(person, address!, draft, values, batchId: null, ct);
        await _db.SaveChangesAsync(ct);

        var channel = draft.Channel == MessageChannel.Email ? "Email" : "Text message";
        var message = log.Status switch
        {
            MessageStatus.Sent => $"{channel} sent to {person.FullName}.",
            MessageStatus.NotConfigured => $"{channel} to {person.FullName} was saved in the history but not sent: {(draft.Channel == MessageChannel.Email ? "email" : "text messaging")} isn't set up yet.",
            _ => $"{channel} to {person.FullName} failed: {log.Error}",
        };
        return new(true, log.Status, message, log.Id);
    }

    public async Task<BulkSendOutcome> SendBulkAsync(AudienceFilter filter, MessageDraft draft, CancellationToken ct = default)
    {
        if (Validate(draft) is { } problem) return new(null, 0, 0, 0, 0, 0, problem);

        var people = await AudienceAsync(filter, ct);
        var targets = new List<(MessageRecipient Person, string Address)>();
        int unsubscribed = 0, noAddress = 0;
        foreach (var p in people)
        {
            var (address, skip) = p.AddressFor(draft.Channel);
            if (skip == SkipReason.NoAddress) noAddress++;
            else if (skip == SkipReason.Unsubscribed) unsubscribed++;
            else targets.Add((p, address!));
        }

        if (targets.Count == 0)
            return new(null, 0, 0, 0, unsubscribed, noAddress, "Nobody in this group can receive this message (they've unsubscribed or have no address on file).");
        if (targets.Count > MaxBulkRecipients)
            return new(null, 0, 0, 0, unsubscribed, noAddress,
                $"This group has {targets.Count:N0} recipients. One send can go to at most {MaxBulkRecipients:N0} people. Narrow the filters (for example by program or rank) and send in parts.");

        var branchName = await BranchNameAsync(ct);
        var usesBalance = (draft.Subject + draft.Body).Contains("Balance", StringComparison.OrdinalIgnoreCase);
        var balances = usesBalance && filter.Kind == AudienceKind.Students
            ? await BalancesAsync(targets.Select(t => t.Person.StudentId!.Value).ToList(), ct)
            : new Dictionary<int, decimal>();

        var batchId = Guid.NewGuid();
        int sent = 0, notConfigured = 0, failed = 0;
        foreach (var chunk in targets.Chunk(BatchSize))
        {
            foreach (var (person, address) in chunk)
            {
                var log = await DeliverAsync(person, address, draft, MergeValues(person, branchName, balances), batchId, ct);
                switch (log.Status)
                {
                    case MessageStatus.Sent: sent++; break;
                    case MessageStatus.NotConfigured: notConfigured++; break;
                    default: failed++; break;
                }
            }
            await _db.SaveChangesAsync(ct);
            // Nothing else is tracked during a bulk send; clearing keeps memory flat for large groups.
            _db.ChangeTracker.Clear();
        }
        _log.LogInformation("Bulk {Channel} batch {Batch}: {Sent} sent, {NotConfigured} not configured, {Failed} failed, {Skipped} skipped",
            draft.Channel, batchId, sent, notConfigured, failed, unsubscribed + noAddress);
        return new(batchId, sent, notConfigured, failed, unsubscribed, noAddress);
    }

    /// <summary>Sends one message and adds (but doesn't save) its MessageLog row and timeline activity.</summary>
    private async Task<MessageLog> DeliverAsync(MessageRecipient person, string address, MessageDraft draft,
        IReadOnlyDictionary<string, string?> values, Guid? batchId, CancellationToken ct)
    {
        var subject = draft.Channel == MessageChannel.Email ? TemplateRenderer.Render(draft.Subject, values).Trim() : null;
        var body = TemplateRenderer.Render(draft.Body, values).Trim();

        SendResult result;
        try
        {
            result = draft.Channel == MessageChannel.Email
                ? await _email.SendAsync(new EmailMessage(address, person.FullName, subject!, body), ct)
                : await _sms.SendAsync(new SmsMessage(address, body), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Senders shouldn't throw, but one bad message must never stop a bulk send.
            _log.LogError(ex, "Unexpected error sending {Channel} to {Address}", draft.Channel, address);
            result = SendResult.Failed("Unexpected error: " + ex.Message);
        }

        var now = DateTime.UtcNow;
        var log = new MessageLog
        {
            Channel = draft.Channel, To = Truncate(address, 200)!, Subject = Truncate(subject, 300), Body = body,
            Status = result.Status, Error = Truncate(result.Error, 1000),
            SentAt = result.Status == MessageStatus.Sent ? now : null,
            StudentId = person.StudentId, LeadId = person.LeadId, BatchId = batchId,
        };
        _db.MessageLogs.Add(log);

        var label = draft.Channel == MessageChannel.Email ? "Email" : "Text message";
        var outcome = result.Status switch
        {
            MessageStatus.Sent => "sent",
            MessageStatus.NotConfigured => "not sent (provider not set up)",
            _ => "failed",
        };
        var summary = draft.Channel == MessageChannel.Email ? $"“{subject}”" : $"“{Truncate(body, 300)}”";
        _db.Activities.Add(new Activity
        {
            StudentId = person.StudentId, LeadId = person.LeadId,
            Type = draft.Channel == MessageChannel.Email ? ActivityType.Email : ActivityType.SMS,
            Body = Truncate($"{label} {outcome} to {address}: {summary}" + (result.Status == MessageStatus.Failed ? $" — {result.Error}" : ""), 4000)!,
            CompletedAt = now,
            UserId = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier),
        });
        return log;
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
