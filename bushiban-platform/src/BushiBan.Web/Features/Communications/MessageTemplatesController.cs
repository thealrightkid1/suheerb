using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Communication;
using BushiBan.Web.Features.Communications.Messaging;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Communications;

/// <summary>Reusable email and SMS wording. Not a CrudController because the body needs a large text box and a live preview.</summary>
[HasPermission(P.Communications.ManageTemplates)]
public class MessageTemplatesController : Controller
{
    private readonly AppDbContext _db;
    public MessageTemplatesController(AppDbContext db) => _db = db;

    public class TemplateInput
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "Give the template a name."), StringLength(120)] public string Name { get; set; } = "";
        public MessageChannel Channel { get; set; } = MessageChannel.Email;
        [StringLength(300)] public string? Subject { get; set; }
        [Required(ErrorMessage = "Write the message."), StringLength(MessagingService.MaxEmailLength)] public string Body { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? q, MessageChannel? channel, int page = 1, CancellationToken ct = default)
    {
        var query = _db.MessageTemplates.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => t.Name.Contains(q) || (t.Subject != null && t.Subject.Contains(q)) || t.Body.Contains(q));
        if (channel is { } ch) query = query.Where(t => t.Channel == ch);
        ViewData["q"] = q;
        ViewData["channel"] = channel;
        return View(await query.OrderBy(t => t.Name).ToPagedAsync(page, 25, ct));
    }

    [HttpGet]
    public IActionResult Create(MessageChannel channel = MessageChannel.Email) => View("Form", new TemplateInput { Channel = channel });

    [HttpPost]
    public async Task<IActionResult> Create(TemplateInput input, CancellationToken ct)
    {
        Validate(input);
        if (!ModelState.IsValid) return View("Form", input);
        var template = new MessageTemplate();
        Apply(template, input);
        _db.MessageTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        this.Success($"Template \"{template.Name}\" created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var t = await _db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        return View("Form", new TemplateInput { Id = t.Id, Name = t.Name, Channel = t.Channel, Subject = t.Subject, Body = t.Body, IsActive = t.IsActive });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, TemplateInput input, CancellationToken ct)
    {
        var t = await _db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        input.Id = id;
        Validate(input);
        if (!ModelState.IsValid) return View("Form", input);
        Apply(t, input);
        await _db.SaveChangesAsync(ct);
        this.Success($"Template \"{t.Name}\" saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var t = await _db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        _db.MessageTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        this.Success($"Template \"{t.Name}\" deleted.");
        return RedirectToAction(nameof(Index));
    }

    private void Validate(TemplateInput input)
    {
        if (input.Channel == MessageChannel.Email && string.IsNullOrWhiteSpace(input.Subject))
            ModelState.AddModelError(nameof(input.Subject), "Email templates need a subject.");
        if (input.Channel == MessageChannel.SMS && input.Body?.Length > MessagingService.MaxSmsLength)
            ModelState.AddModelError(nameof(input.Body), $"Text messages can be at most {MessagingService.MaxSmsLength:N0} characters.");
    }

    private static void Apply(MessageTemplate t, TemplateInput input)
    {
        t.Name = input.Name.Trim();
        t.Channel = input.Channel;
        t.Subject = input.Channel == MessageChannel.Email ? input.Subject?.Trim() : null;
        t.Body = input.Body.Trim();
        t.IsActive = input.IsActive;
    }
}
