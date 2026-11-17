using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Features.Attendance;
using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Academics;

/// <summary>
/// Designs the registration kiosk's forms. Edits go into a draft version; publishing makes the draft the version the
/// kiosk shows. Published versions never change, so every submission keeps the exact wording that was signed.
/// </summary>
[HasPermission(P.FormBuilder.Manage)]
public class FormBuilderController : Controller
{
    private readonly AppDbContext _db;
    private readonly LookupService _lookups;
    private readonly KioskFormService _kioskForms;

    public FormBuilderController(AppDbContext db, LookupService lookups, KioskFormService kioskForms)
    {
        _db = db; _lookups = lookups; _kioskForms = kioskForms;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var rows = await _db.FormDefinitions.OrderBy(f => f.SortOrder).ThenBy(f => f.Name).Select(f => new FormRow(
                f.Id, f.Name, f.Category, f.IsActive, f.Course != null ? f.Course.Name : null,
                _db.FormVersions.Where(v => v.Id == f.PublishedVersionId).Select(v => (int?)v.VersionNumber).FirstOrDefault(),
                _db.FormVersions.Where(v => v.Id == f.PublishedVersionId).Select(v => v.PublishedAt).FirstOrDefault(),
                f.DraftVersionId != null,
                _db.FormSubmissions.Count(s => s.FormDefinitionId == f.Id)))
            .ToListAsync();
        return View(rows);
    }

    /// <summary>Saves the kiosk order after drag-and-drop. <paramref name="ids"/> are form ids, top to bottom.</summary>
    [HttpPost]
    public async Task<IActionResult> SaveOrder(List<int>? ids)
    {
        if (ids is null || ids.Count == 0) return BadRequest(new { ok = false, message = "Nothing to reorder." });
        var forms = await _db.FormDefinitions.Where(f => ids.Contains(f.Id)).ToListAsync();
        foreach (var form in forms) form.SortOrder = ids.IndexOf(form.Id) + 1;
        // Forms not in the list (shouldn't happen) go after the ones that were.
        var others = await _db.FormDefinitions.Where(f => !ids.Contains(f.Id)).OrderBy(f => f.SortOrder).ToListAsync();
        for (var i = 0; i < others.Count; i++) others[i].SortOrder = ids.Count + i + 1;
        await _db.SaveChangesAsync();
        return Json(new { ok = true, message = "Order saved. The kiosk shows forms in this order." });
    }

    [HttpGet]
    public async Task<IActionResult> Create() => View(await FillCreate(new FormCreateModel()));

    [HttpPost]
    public async Task<IActionResult> Create(FormCreateModel model)
    {
        if (!Enum.IsDefined(model.Category)) ModelState.AddModelError(nameof(model.Category), "Pick a category.");
        if (model.CourseId is int cid && !await _db.Courses.AnyAsync(c => c.Id == cid)) ModelState.AddModelError(nameof(model.CourseId), "Pick a program from the list.");
        string schema = FormSchema.Serialize(FormSchemaRules.Starter());
        if (model.CloneFromId is int source)
        {
            var from = await _db.FormDefinitions.Where(f => f.Id == source).Select(f => new { f.DraftVersionId, f.PublishedVersionId }).FirstOrDefaultAsync();
            var versionId = from?.DraftVersionId ?? from?.PublishedVersionId;
            var json = versionId is null ? null : await _db.FormVersions.Where(v => v.Id == versionId).Select(v => v.SchemaJson).FirstOrDefaultAsync();
            if (json is null) ModelState.AddModelError(nameof(model.CloneFromId), "That form has nothing to copy yet.");
            else schema = json;
        }
        if (!ModelState.IsValid) return View(await FillCreate(model));

        var order = await _db.FormDefinitions.MaxAsync(f => (int?)f.SortOrder) ?? 0;
        var definition = new FormDefinition
        {
            Name = model.Name.Trim(), Category = model.Category, CourseId = model.CourseId, IsActive = true, SortOrder = order + 1,
        };
        var draft = new FormVersion { FormDefinition = definition, VersionNumber = 0, SchemaJson = schema };
        _db.FormVersions.Add(draft);
        await _db.SaveChangesAsync();
        definition.DraftVersionId = draft.Id;
        await _db.SaveChangesAsync();
        this.Success("Form created. Add your fields, save the draft, then publish it to put it on the kiosk.");
        return RedirectToAction(nameof(Edit), new { id = definition.Id });
    }

    private async Task<FormCreateModel> FillCreate(FormCreateModel model)
    {
        model.Courses = await _lookups.CoursesAsync(model.CourseId);
        model.Forms = await _db.FormDefinitions.OrderBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new SelectListItem(f.Name, f.Id.ToString(), f.Id == model.CloneFromId)).ToListAsync();
        return model;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var page = await LoadEditPage(id);
        return page is null ? NotFound() : View(page);
    }

    /// <summary>Saves the form's settings (name, category, program, shown on kiosk). Fields are saved separately with SaveDraft.</summary>
    [HttpPost]
    public async Task<IActionResult> Edit(int id, [Bind(Prefix = "Settings")] FormSettingsModel settings)
    {
        var definition = await _db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound();
        if (!Enum.IsDefined(settings.Category)) ModelState.AddModelError("Settings.Category", "Pick a category.");
        if (settings.CourseId is int cid && !await _db.Courses.AnyAsync(c => c.Id == cid)) ModelState.AddModelError("Settings.CourseId", "Pick a program from the list.");
        if (!ModelState.IsValid)
        {
            var page = (await LoadEditPage(id))!;
            page.Settings = settings;
            return View(page);
        }
        definition.Name = settings.Name.Trim();
        definition.Category = settings.Category;
        definition.CourseId = settings.CourseId;
        definition.IsActive = settings.IsActive;
        await _db.SaveChangesAsync();
        this.Success(settings.IsActive || definition.PublishedVersionId is null ? "Settings saved." : "Settings saved. This form is now hidden from the kiosk.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<FormEditPage?> LoadEditPage(int id)
    {
        var definition = await _db.FormDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return null;
        var versions = await _db.FormVersions.Where(v => v.FormDefinitionId == id)
            .OrderByDescending(v => v.PublishedAt == null).ThenByDescending(v => v.VersionNumber)
            .Select(v => new FormVersionRow(v.Id, v.VersionNumber, v.PublishedAt, v.UpdatedAt ?? v.CreatedAt, v.Id == definition.DraftVersionId,
                v.Id == definition.PublishedVersionId, _db.FormSubmissions.Count(s => s.FormVersionId == v.Id)))
            .ToListAsync();
        // Old drafts that were replaced or discarded are hidden.
        versions = versions.Where(v => v.PublishedAt is not null || v.IsDraft).ToList();
        var workingId = definition.DraftVersionId ?? definition.PublishedVersionId;
        return new FormEditPage
        {
            Id = id,
            Settings = new FormSettingsModel { Name = definition.Name, Category = definition.Category, CourseId = definition.CourseId, IsActive = definition.IsActive },
            Courses = await _lookups.CoursesAsync(definition.CourseId),
            // Re-serialised so the JSON embedded in the page is always HTML-safe.
            SchemaJson = FormSchema.Serialize(FormSchema.Parse(workingId is null ? null : await _db.FormVersions.Where(v => v.Id == workingId).Select(v => v.SchemaJson).FirstOrDefaultAsync())),
            HasDraft = definition.DraftVersionId is not null,
            DraftVersionId = definition.DraftVersionId,
            PublishedVersion = versions.FirstOrDefault(v => v.IsLive)?.VersionNumber,
            Versions = versions,
            Submissions = versions.Sum(v => v.Submissions),
        };
    }

    /// <summary>Stores the editor's fields as the draft (creating the draft version if there isn't one). Published versions are never touched.</summary>
    [HttpPost]
    public async Task<IActionResult> SaveDraft(int id, [FromBody] SaveDraftRequest? request)
    {
        var definition = await _db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound(new { ok = false, message = "This form no longer exists." });
        var fields = request?.Fields ?? new List<FormField>();
        var problems = FormSchemaRules.Normalize(fields);
        if (problems.Count > 0)
            return BadRequest(new { ok = false, message = "The draft wasn't saved. Please fix the problems listed.", problems });

        var draft = definition.DraftVersionId is int draftId
            ? await _db.FormVersions.FirstOrDefaultAsync(v => v.Id == draftId && v.FormDefinitionId == id && v.PublishedAt == null)
            : null;
        if (draft is null)
        {
            draft = new FormVersion { FormDefinitionId = id, VersionNumber = 0 };
            _db.FormVersions.Add(draft);
        }
        draft.SchemaJson = FormSchema.Serialize(fields);
        await _db.SaveChangesAsync();
        if (definition.DraftVersionId != draft.Id)
        {
            definition.DraftVersionId = draft.Id;
            await _db.SaveChangesAsync();
        }
        return Json(new { ok = true, message = "Draft saved.", draftVersionId = draft.Id, fields });
    }

    [HttpPost]
    public async Task<IActionResult> DiscardDraft(int id)
    {
        var definition = await _db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound();
        if (definition.DraftVersionId is int draftId)
        {
            var draft = await _db.FormVersions.FirstOrDefaultAsync(v => v.Id == draftId && v.PublishedAt == null);
            if (draft is not null) _db.FormVersions.Remove(draft);
            definition.DraftVersionId = null;
            await _db.SaveChangesAsync();
            this.Success(definition.PublishedVersionId is null ? "Draft discarded." : "Draft discarded. The editor now shows the published version.");
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>The draft becomes the published version the kiosk shows. It can't be changed after this.</summary>
    [HttpPost]
    public async Task<IActionResult> Publish(int id)
    {
        var definition = await _db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound();
        var draft = definition.DraftVersionId is int draftId
            ? await _db.FormVersions.FirstOrDefaultAsync(v => v.Id == draftId && v.FormDefinitionId == id && v.PublishedAt == null)
            : null;
        if (draft is null)
        {
            this.Error("There's no draft to publish. Change some fields and save the draft first.");
            return RedirectToAction(nameof(Edit), new { id });
        }
        var fields = FormSchema.Parse(draft.SchemaJson);
        var problems = FormSchemaRules.Normalize(fields);
        if (problems.Count > 0 || !fields.Any(f => f.IsInput))
        {
            this.Error(problems.Count > 0 ? problems[0].Message : "Add at least one question before publishing.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var latest = await _db.FormVersions.Where(v => v.FormDefinitionId == id && v.PublishedAt != null).MaxAsync(v => (int?)v.VersionNumber) ?? 0;
        draft.SchemaJson = FormSchema.Serialize(fields);
        draft.VersionNumber = latest + 1;
        draft.PublishedAt = DateTime.UtcNow;
        definition.PublishedVersionId = draft.Id;
        definition.DraftVersionId = null;
        await _db.SaveChangesAsync();
        this.Success(definition.IsActive
            ? $"Version {draft.VersionNumber} is now live on the registration kiosk."
            : $"Version {draft.VersionNumber} is published. Turn on \"Show on kiosk\" to make it available.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Shows a version exactly as the kiosk would (same layout and field renderer), without submitting.</summary>
    [HttpGet]
    public async Task<IActionResult> Preview(int id, int? version)
    {
        var definition = await _db.FormDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound();
        var versionId = version ?? definition.DraftVersionId ?? definition.PublishedVersionId;
        var v = versionId is null ? null : await _db.FormVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.FormDefinitionId == id);
        if (v is null) return NotFound();
        var fields = FormSchema.Parse(v.SchemaJson);
        var model = new KioskFormView
        {
            FormId = id, Name = definition.Name, Category = definition.Category, Fields = fields, Preview = true,
            PreviewLabel = v.PublishedAt is null ? "Preview · draft" : $"Preview · version {v.VersionNumber}",
            Cities = fields.Any(f => f.Type == "city") ? await _kioskForms.CitiesAsync() : new(),
        };
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var definition = await _db.FormDefinitions.FirstOrDefaultAsync(f => f.Id == id);
        if (definition is null) return NotFound();
        _db.FormDefinitions.Remove(definition);
        await _db.SaveChangesAsync();
        this.Success($"\"{definition.Name}\" deleted. Its past submissions are kept.");
        return RedirectToAction(nameof(Index));
    }
}
