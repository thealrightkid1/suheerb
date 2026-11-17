using System.Globalization;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Communication;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Communications;

/// <summary>
/// Branch calendar (FullCalendar). Events are created, edited, moved and deleted with fetch POSTs that carry the
/// antiforgery token in the RequestVerificationToken header. All times are branch-local wall time: see <see cref="CalendarFeed"/>.
/// </summary>
[HasPermission(P.Calendar.View)]
public class CalendarController : Controller
{
    private readonly AppDbContext _db;
    private readonly CalendarFeed _feed;
    private readonly BranchClock _clock;

    public CalendarController(AppDbContext db, CalendarFeed feed, BranchClock clock) { _db = db; _feed = feed; _clock = clock; }

    public class EventInput
    {
        public int? Id { get; set; }
        public string? Title { get; set; }
        /// <summary>Branch-local, "yyyy-MM-dd" for all-day events or "yyyy-MM-ddTHH:mm[:ss]".</summary>
        public string? Start { get; set; }
        /// <summary>Exclusive end, same format as Start. Optional.</summary>
        public string? End { get; set; }
        public bool AllDay { get; set; }
        public EventCategory Category { get; set; } = EventCategory.Business;
        public string? Location { get; set; }
        public string? Description { get; set; }
    }

    public class MoveInput
    {
        public int Id { get; set; }
        public string? Start { get; set; }
        public string? End { get; set; }
        public bool AllDay { get; set; }
    }

    public class IdInput { public int Id { get; set; } }

    [HttpGet]
    public IActionResult Index()
    {
        ViewData["CanManage"] = User.HasPermission(P.Calendar.Manage);
        ViewData["BranchNow"] = _clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return View();
    }

    /// <summary>FullCalendar event source: GET /Calendar/Events?start=…&amp;end=…&amp;classes=true&amp;birthdays=false</summary>
    [HttpGet]
    public async Task<IActionResult> Events(string? start, string? end, bool classes = true, bool birthdays = false, CancellationToken ct = default)
    {
        if (ParseWallTime(start) is not { } from || ParseWallTime(end) is not { } to)
            return BadRequest(new { error = "Give a start and end date." });
        var items = await _feed.ItemsAsync(from, to, classes, birthdays, User.HasPermission(P.Calendar.Manage), ct);
        return Json(items.Select(ToJson));
    }

    [HttpPost, HasPermission(P.Calendar.Manage)]
    public async Task<IActionResult> Save([FromBody] EventInput input, CancellationToken ct)
    {
        CalendarEvent? entity;
        if (input.Id is int id)
        {
            entity = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (entity is null) return NotFound(new { error = "That event no longer exists. Refresh the calendar." });
        }
        else entity = new CalendarEvent();

        var problem = Apply(entity, input);
        if (problem is not null) return BadRequest(new { error = problem });
        if (entity.Id == 0) _db.CalendarEvents.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Json(ToJson(EventItem(entity)));
    }

    /// <summary>Drag or resize on the calendar.</summary>
    [HttpPost, HasPermission(P.Calendar.Manage)]
    public async Task<IActionResult> Move([FromBody] MoveInput input, CancellationToken ct)
    {
        var entity = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == input.Id, ct);
        if (entity is null) return NotFound(new { error = "That event no longer exists. Refresh the calendar." });
        var problem = ApplyTimes(entity, input.Start, input.End, input.AllDay);
        if (problem is not null) return BadRequest(new { error = problem });
        await _db.SaveChangesAsync(ct);
        return Json(ToJson(EventItem(entity)));
    }

    [HttpPost, HasPermission(P.Calendar.Manage)]
    public async Task<IActionResult> Delete([FromBody] IdInput input, CancellationToken ct)
    {
        var entity = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == input.Id, ct);
        if (entity is null) return NotFound(new { error = "That event no longer exists." });
        _db.CalendarEvents.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return Json(new { ok = true });
    }

    private static string? Apply(CalendarEvent e, EventInput input)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title)) return "Give the event a title.";
        if (title.Length > 200) return "The title is too long (200 characters at most).";
        if (input.Location?.Length > 200) return "The location is too long (200 characters at most).";
        if (input.Description?.Length > 2000) return "The description is too long (2,000 characters at most).";
        if (!CalendarFeed.Choosable.Contains(input.Category)) return "Choose a category.";
        var timeProblem = ApplyTimes(e, input.Start, input.End, input.AllDay);
        if (timeProblem is not null) return timeProblem;
        e.Title = title;
        e.Category = input.Category;
        e.Location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim();
        e.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        return null;
    }

    private static string? ApplyTimes(CalendarEvent e, string? startText, string? endText, bool allDay)
    {
        if (ParseWallTime(startText) is not { } start) return "Choose when the event starts.";
        DateTime? end = string.IsNullOrWhiteSpace(endText) ? null : ParseWallTime(endText);
        if (!string.IsNullOrWhiteSpace(endText) && end is null) return "The end time isn't a valid date.";
        if (allDay)
        {
            start = start.Date;
            end = end?.Date;
            if (end is not null && end <= start) end = null; // a one-day event
        }
        else if (end is not null && end < start) return "The event can't end before it starts.";
        e.Start = start;
        e.End = end;
        e.AllDay = allDay;
        return null;
    }

    /// <summary>
    /// Reads a date/time as branch-local wall time. Any offset (FullCalendar's UTC mode adds "Z") is ignored on
    /// purpose: the page shows wall time, so the clock reading is what the user meant.
    /// </summary>
    internal static DateTime? ParseWallTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d.DateTime, DateTimeKind.Unspecified)
            : null;
    }

    private static CalendarItem EventItem(CalendarEvent e) => new(
        "e" + e.Id, e.Title, e.AllDay ? e.Start.ToString("yyyy-MM-dd") : e.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
        e.End is { } en ? (e.AllDay ? en.ToString("yyyy-MM-dd") : en.ToString("yyyy-MM-ddTHH:mm:ss")) : null,
        e.AllDay, CalendarFeed.Colors.GetValueOrDefault(e.Category, "#0f766e"), true, "event", e.Category, e.Location, e.Description, e.Id);

    // FullCalendar reads id/title/start/end/allDay/color/editable; everything else lands in extendedProps.
    private static object ToJson(CalendarItem i) => new
    {
        id = i.Id, title = i.Title, start = i.Start, end = i.End, allDay = i.AllDay, color = i.Color, editable = i.Editable,
        classNames = new[] { "is-" + i.Kind },
        kind = i.Kind, category = i.Category.ToString(), location = i.Location, description = i.Description, eventId = i.EventId,
    };
}
