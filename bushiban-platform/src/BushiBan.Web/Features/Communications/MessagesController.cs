using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Features.Communications.Messaging;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Communications;

[HasPermission(P.Communications.View + "|" + P.Communications.Send)]
public class MessagesController : Controller
{
    private readonly AppDbContext _db;
    private readonly MessagingService _messaging;
    private readonly BranchClock _clock;
    private readonly LookupService _lookups;

    public MessagesController(AppDbContext db, MessagingService messaging, BranchClock clock, LookupService lookups)
    {
        _db = db; _messaging = messaging; _clock = clock; _lookups = lookups;
    }

    // ----- History -----

    [HttpGet]
    public async Task<IActionResult> Index(MessageHistoryFilter f, int page = 1, CancellationToken ct = default)
    {
        var q = _db.MessageLogs.AsQueryable();
        if (f.Channel is { } ch) q = q.Where(m => m.Channel == ch);
        if (f.Status is { } st) q = q.Where(m => m.Status == st);
        if (f.Batch is { } batch) q = q.Where(m => m.BatchId == batch);
        if (f.From is { } from) { var utc = ToUtc(from); q = q.Where(m => m.CreatedAt >= utc); }
        if (f.To is { } to) { var utc = ToUtc(to.AddDays(1)); q = q.Where(m => m.CreatedAt < utc); }
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(m => m.To.Contains(t)
                || (m.Student != null && (m.Student.FirstName + " " + m.Student.LastName).Contains(t))
                || (m.Lead != null && (m.Lead.FirstName + " " + m.Lead.LastName).Contains(t)));
        }

        var paged = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Select(m => new MessageRow(m.Id, m.Channel, m.To,
                m.Student != null ? m.Student.FirstName + " " + m.Student.LastName : m.Lead != null ? m.Lead.FirstName + " " + m.Lead.LastName : null,
                m.StudentId, m.LeadId, m.Subject, m.Body, m.Status, m.Error, m.CreatedAt, m.CreatedBy, m.BatchId))
            .ToPagedAsync(page, 25, ct);

        // Show who sent each message by name, with one query for the whole page.
        var senders = paged.Items.Select(r => r.CreatedBy).Where(n => n != null).Distinct().ToList();
        var names = await _db.Users.Where(u => senders.Contains(u.UserName))
            .Select(u => new { u.UserName, u.DisplayName }).ToDictionaryAsync(u => u.UserName!, u => u.DisplayName ?? u.UserName!, ct);

        var since = DateTime.UtcNow.AddDays(-30);
        var totals = await _db.MessageLogs.Where(m => m.CreatedAt >= since).GroupBy(m => m.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return View(new MessageHistoryModel
        {
            Filter = f,
            Paged = paged,
            SenderNames = names,
            Clock = _clock,
            Last30Days = totals,
            EmailConfigured = _messaging.EmailConfigured,
            SmsConfigured = _messaging.SmsConfigured,
            CanSend = User.HasPermission(P.Communications.Send),
        });
    }

    private DateTime ToUtc(DateOnly branchDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(branchDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), _clock.TimeZone);

    // ----- Compose -----

    [HttpGet, HasPermission(P.Communications.Send)]
    public async Task<IActionResult> Compose(int? studentId, int? leadId, MessageChannel? channel, int? templateId, CancellationToken ct = default)
    {
        var input = new ComposeInput { Channel = channel ?? MessageChannel.Email, TemplateId = templateId };
        MessageRecipient? person = null;
        if (studentId is int sid) person = await _messaging.StudentAsync(sid, ct);
        else if (leadId is int lid) person = await _messaging.LeadAsync(lid, ct);
        if (person is not null) { input.Mode = "one"; input.Recipient = RecipientKey(person); }
        else if (studentId is not null || leadId is not null) this.Error("That person wasn't found in this branch.");

        if (templateId is int tid && await _db.MessageTemplates.FirstOrDefaultAsync(t => t.Id == tid && t.IsActive, ct) is { } template)
        {
            input.Channel = template.Channel;
            input.Subject = template.Subject;
            input.Body = template.Body;
        }
        return View(await ComposeModelAsync(input, person, ct));
    }

    [HttpPost, HasPermission(P.Communications.Send)]
    public async Task<IActionResult> Compose(ComposeInput input, CancellationToken ct = default)
    {
        var draft = new MessageDraft(input.Channel, input.Subject, input.Body ?? "");
        MessageRecipient? person = null;

        if (input.Mode == "group")
        {
            var outcome = await _messaging.SendBulkAsync(input.Audience, draft, ct);
            if (outcome.BatchId is null)
            {
                ModelState.AddModelError("", outcome.Error!);
                return View(await ComposeModelAsync(input, null, ct));
            }
            this.Success(BulkSummary(input.Channel, outcome));
            return RedirectToAction(nameof(Index), new { batch = outcome.BatchId });
        }

        person = await ResolveAsync(input.Recipient, ct);
        if (person is null)
        {
            ModelState.AddModelError(nameof(input.Recipient), "Choose who to send this to.");
            return View(await ComposeModelAsync(input, null, ct));
        }
        var result = await _messaging.SendAsync(person, draft, ct);
        if (!result.Attempted)
        {
            ModelState.AddModelError("", result.Message);
            return View(await ComposeModelAsync(input, person, ct));
        }
        if (result.Status == MessageStatus.Failed) this.Error(result.Message); else this.Success(result.Message);
        if (!string.IsNullOrEmpty(input.ReturnUrl) && Url.IsLocalUrl(input.ReturnUrl)) return LocalRedirect(input.ReturnUrl);
        return RedirectToAction(nameof(Index));
    }

    private static string BulkSummary(MessageChannel channel, BulkSendOutcome o)
    {
        var what = channel == MessageChannel.Email ? "email" : "text message";
        var parts = new List<string>();
        if (o.Sent > 0) parts.Add($"{o.Sent:N0} sent");
        if (o.NotConfigured > 0) parts.Add($"{o.NotConfigured:N0} saved but not sent because {(channel == MessageChannel.Email ? "email" : "text messaging")} isn't set up yet");
        if (o.Failed > 0) parts.Add($"{o.Failed:N0} failed");
        if (o.Unsubscribed > 0) parts.Add($"{o.Unsubscribed:N0} skipped (unsubscribed)");
        if (o.NoAddress > 0) parts.Add($"{o.NoAddress:N0} skipped (no address on file)");
        return $"Group {what}: {string.Join(", ", parts)}.";
    }

    /// <summary>Live "N recipients, M will be skipped" count for the group composer.</summary>
    [HttpGet, HasPermission(P.Communications.Send)]
    public async Task<IActionResult> Preview([FromQuery(Name = "Audience")] AudienceFilter audience, MessageChannel channel = MessageChannel.Email, CancellationToken ct = default)
    {
        var p = await _messaging.PreviewAsync(audience, channel, ct);
        return Json(new
        {
            total = p.Total, reachable = p.Reachable, unsubscribed = p.Unsubscribed, noAddress = p.NoAddress,
            skipped = p.Unsubscribed + p.NoAddress, limit = p.Limit, overLimit = p.OverLimit,
        });
    }

    /// <summary>Search for the "send to one person" picker.</summary>
    [HttpGet, HasPermission(P.Communications.Send)]
    public async Task<IActionResult> Recipients(string? q, CancellationToken ct = default)
    {
        var people = await _messaging.SearchAsync(q ?? "", ct: ct);
        return Json(people.Select(RecipientJson));
    }

    /// <summary>Merge values for one person, so the composer can preview exactly what they'll receive.</summary>
    [HttpGet, HasPermission(P.Communications.Send)]
    public async Task<IActionResult> MergeValues(string? recipient, CancellationToken ct = default)
    {
        var person = await ResolveAsync(recipient, ct);
        return person is null ? NotFound() : Json(await _messaging.MergeValuesAsync(person, ct));
    }

    private Task<MessageRecipient?> ResolveAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 3 || key[1] != ':' || !int.TryParse(key[2..], out var id)) return Task.FromResult<MessageRecipient?>(null);
        return key[0] switch
        {
            's' => _messaging.StudentAsync(id, ct),
            'l' => _messaging.LeadAsync(id, ct),
            _ => Task.FromResult<MessageRecipient?>(null),
        };
    }

    internal static string RecipientKey(MessageRecipient p) => p.StudentId is int s ? $"s:{s}" : $"l:{p.LeadId}";

    internal static object RecipientJson(MessageRecipient p) => new
    {
        value = RecipientKey(p),
        text = p.FullName,
        kind = p.StudentId is null ? "Lead" : "Student",
        email = p.EmailAddress, emailRaw = p.Email, emailOptIn = p.EmailOptIn,
        phone = p.E164, phoneRaw = p.Phone, smsOptIn = p.SmsOptIn,
    };

    private async Task<ComposeModel> ComposeModelAsync(ComposeInput input, MessageRecipient? person, CancellationToken ct)
    {
        var templates = await _db.MessageTemplates.Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new TemplateOption(t.Id, t.Name, t.Channel, t.Subject, t.Body)).ToListAsync(ct);
        return new ComposeModel
        {
            Input = input,
            Person = person,
            Templates = templates,
            Courses = await _lookups.CoursesAsync(input.Audience.CourseId, activeOnly: false),
            Ranks = await _lookups.RanksAsync(input.Audience.RankId),
            EmailConfigured = _messaging.EmailConfigured,
            SmsConfigured = _messaging.SmsConfigured,
        };
    }
}

public class MessageHistoryFilter
{
    public MessageChannel? Channel { get; set; }
    public MessageStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Q { get; set; }
    public Guid? Batch { get; set; }
    public bool Any => Channel is not null || Status is not null || From is not null || To is not null || !string.IsNullOrWhiteSpace(Q) || Batch is not null;
}

public sealed record MessageRow(int Id, MessageChannel Channel, string To, string? Recipient, int? StudentId, int? LeadId,
    string? Subject, string Body, MessageStatus Status, string? Error, DateTime CreatedAt, string? CreatedBy, Guid? BatchId);

public class MessageHistoryModel
{
    public MessageHistoryFilter Filter { get; init; } = new();
    public PagedList<MessageRow> Paged { get; init; } = default!;
    public IReadOnlyDictionary<string, string> SenderNames { get; init; } = new Dictionary<string, string>();
    public BranchClock Clock { get; init; } = default!;
    public IReadOnlyDictionary<MessageStatus, int> Last30Days { get; init; } = new Dictionary<MessageStatus, int>();
    public bool EmailConfigured { get; init; }
    public bool SmsConfigured { get; init; }
    public bool CanSend { get; init; }
    public int Count(MessageStatus s) => Last30Days.TryGetValue(s, out var n) ? n : 0;
}

public class ComposeInput
{
    /// <summary>"one" (a single student or lead) or "group" (an audience).</summary>
    public string Mode { get; set; } = "group";
    public MessageChannel Channel { get; set; } = MessageChannel.Email;
    /// <summary>"s:12" for student 12, "l:7" for lead 7.</summary>
    public string? Recipient { get; set; }
    public int? TemplateId { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public AudienceFilter Audience { get; set; } = new();
    public string? ReturnUrl { get; set; }
}

public sealed record TemplateOption(int Id, string Name, MessageChannel Channel, string? Subject, string Body);

public class ComposeModel
{
    public ComposeInput Input { get; init; } = new();
    public MessageRecipient? Person { get; init; }
    public List<TemplateOption> Templates { get; init; } = new();
    public List<SelectListItem> Courses { get; init; } = new();
    public List<SelectListItem> Ranks { get; init; } = new();
    public bool EmailConfigured { get; init; }
    public bool SmsConfigured { get; init; }
}
