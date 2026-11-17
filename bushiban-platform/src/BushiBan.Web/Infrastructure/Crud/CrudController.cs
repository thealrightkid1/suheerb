using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using BushiBan.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Infrastructure.Crud;

/// <summary>A column on the generic list page.</summary>
public record CrudColumn<T>(string Header, Func<T, object?> Value, string? Css = null);

/// <summary>An editable field on the generic form. Type is worked out from the property; Options makes it a dropdown.</summary>
public record CrudField(string Property, string? Label = null, string? Help = null, Func<Task<List<SelectListItem>>>? Options = null, bool ReadOnlyOnEdit = false);

/// <summary>
/// List / create / edit / delete for simple setup tables (ranks, belt sizes, accounts…).
/// Subclasses describe columns and fields; this class does the rest, including permissions, search, paging,
/// branch scoping (via the DbContext) and soft delete with "still in use" checks.
/// </summary>
public abstract class CrudController<T> : Controller where T : AuditableEntity, new()
{
    protected readonly AppDbContext Db;
    protected CrudController(AppDbContext db) => Db = db;

    protected abstract string Title { get; }
    protected abstract string Singular { get; }
    protected abstract string ViewPermission { get; }
    protected abstract string CreatePermission { get; }
    protected abstract string EditPermission { get; }
    protected abstract string DeletePermission { get; }
    protected abstract IEnumerable<CrudColumn<T>> Columns { get; }
    protected abstract IEnumerable<CrudField> Fields { get; }
    protected virtual string? Intro => null;
    protected virtual IQueryable<T> Query() => Db.Set<T>();
    protected virtual IQueryable<T> Search(IQueryable<T> q, string term) => q;
    protected virtual IOrderedQueryable<T> Sort(IQueryable<T> q) => q.OrderBy(x => x.Id);
    /// <summary>Return an error message to block deletion (e.g. "Used by 3 students").</summary>
    protected virtual Task<string?> CanDeleteAsync(T entity) => Task.FromResult<string?>(null);
    /// <summary>Extra validation; add errors to ModelState.</summary>
    protected virtual Task ValidateAsync(T entity, bool isNew) => Task.CompletedTask;
    /// <summary>Last chance to fill derived values (codes etc.) before saving.</summary>
    protected virtual Task BeforeSaveAsync(T entity, bool isNew) => Task.CompletedTask;

    private Task<bool> Can(string permission) => Task.FromResult(User.HasPermission(permission));

    private CrudPageModel Page() => new()
    {
        Title = Title, Singular = Singular, Intro = Intro, Controller = ControllerContext.ActionDescriptor.ControllerName,
        CanCreate = User.HasPermission(CreatePermission), CanEdit = User.HasPermission(EditPermission), CanDelete = User.HasPermission(DeletePermission),
    };

    [HttpGet]
    public virtual async Task<IActionResult> Index(string? q, int page = 1)
    {
        if (!await Can(ViewPermission)) return Forbid();
        var query = Query();
        if (!string.IsNullOrWhiteSpace(q)) query = Search(query, q.Trim());
        var paged = await Sort(query).ToPagedAsync(page, 50);
        var model = Page();
        model.Search = q;
        model.Headers = Columns.Select(c => c.Header).ToList();
        model.Rows = paged.Items.Select(e => new CrudRow(e.Id, Columns.Select(c => Display(c.Value(e))).ToList())).ToList();
        model.Paging = new PagedList<int>(Array.Empty<int>(), paged.Total, paged.Page, paged.PageSize);
        return View("~/Views/Shared/Crud/Index.cshtml", model);
    }

    [HttpGet]
    public virtual async Task<IActionResult> Create()
    {
        if (!await Can(CreatePermission)) return Forbid();
        return await FormView(new T(), isNew: true);
    }

    [HttpPost]
    public virtual async Task<IActionResult> Create(IFormCollection form)
    {
        if (!await Can(CreatePermission)) return Forbid();
        var entity = new T();
        Bind(entity, form, isNew: true);
        await ValidateAsync(entity, true);
        if (!ModelState.IsValid) return await FormView(entity, true);
        await BeforeSaveAsync(entity, true);
        Db.Add(entity);
        if (!await TrySave()) return await FormView(entity, true);
        this.Success($"{Singular} created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public virtual async Task<IActionResult> Edit(int id)
    {
        if (!await Can(EditPermission)) return Forbid();
        var entity = await Query().FirstOrDefaultAsync(e => e.Id == id);
        return entity is null ? NotFound() : await FormView(entity, isNew: false);
    }

    [HttpPost]
    public virtual async Task<IActionResult> Edit(int id, IFormCollection form)
    {
        if (!await Can(EditPermission)) return Forbid();
        var entity = await Db.Set<T>().FirstOrDefaultAsync(e => e.Id == id);
        if (entity is null) return NotFound();
        Bind(entity, form, isNew: false);
        await ValidateAsync(entity, false);
        if (!ModelState.IsValid) return await FormView(entity, false);
        await BeforeSaveAsync(entity, false);
        if (!await TrySave()) return await FormView(entity, false);
        this.Success($"{Singular} saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public virtual async Task<IActionResult> Delete(int id)
    {
        if (!await Can(DeletePermission)) return Forbid();
        var entity = await Db.Set<T>().FirstOrDefaultAsync(e => e.Id == id);
        if (entity is null) return NotFound();
        var blocked = await CanDeleteAsync(entity);
        if (blocked is not null) { this.Error($"Can't delete: {blocked}"); return RedirectToAction(nameof(Index)); }
        Db.Remove(entity);
        await Db.SaveChangesAsync();
        this.Success($"{Singular} deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> TrySave()
    {
        try { await Db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                                           || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            ModelState.AddModelError("", $"A {Singular.ToLowerInvariant()} with that name or code already exists.");
            return false;
        }
    }

    private async Task<IActionResult> FormView(T entity, bool isNew)
    {
        var model = Page();
        model.IsNew = isNew;
        model.Id = entity.Id;
        foreach (var f in Fields)
        {
            var prop = typeof(T).GetProperty(f.Property) ?? throw new InvalidOperationException($"{typeof(T).Name} has no {f.Property}");
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var display = prop.GetCustomAttribute<DisplayAttribute>();
            var field = new CrudFieldModel
            {
                Name = f.Property,
                Label = f.Label ?? display?.GetName() ?? EnumHelpers.Humanize(f.Property),
                Help = f.Help,
                Required = prop.GetCustomAttribute<RequiredAttribute>() is not null || (type.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null && type != typeof(bool)),
                ReadOnly = !isNew && f.ReadOnlyOnEdit,
                MaxLength = prop.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength,
                Value = prop.GetValue(entity),
                Kind = f.Options is not null ? "select"
                    : type == typeof(bool) ? "bool"
                    : type.IsEnum ? "select"
                    : type == typeof(DateOnly) ? "date"
                    : type == typeof(TimeOnly) ? "time"
                    : type == typeof(int) || type == typeof(decimal) || type == typeof(double) ? "number"
                    : "text",
                Step = type == typeof(decimal) ? "0.01" : "1",
            };
            if (f.Options is not null)
            {
                field.Options = await f.Options();
                foreach (var o in field.Options) o.Selected = o.Value == field.Value?.ToString();
            }
            else if (type.IsEnum)
            {
                field.Options = Enum.GetValues(type).Cast<Enum>().Select(v => new SelectListItem(v.DisplayName(), Convert.ToInt32(v).ToString(), v.Equals(field.Value))).ToList();
            }
            model.Fields.Add(field);
        }
        return View("~/Views/Shared/Crud/Form.cshtml", model);
    }

    private void Bind(T entity, IFormCollection form, bool isNew)
    {
        foreach (var f in Fields)
        {
            if (!isNew && f.ReadOnlyOnEdit) continue;
            var prop = typeof(T).GetProperty(f.Property)!;
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var nullable = !prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) is not null;
            var raw = form[f.Property].ToString();
            if (type == typeof(bool)) { prop.SetValue(entity, form[f.Property].Any(v => v == "true")); continue; }
            raw = raw.Trim();
            if (raw.Length == 0)
            {
                if (nullable) prop.SetValue(entity, null);
                else ModelState.AddModelError(f.Property, $"{f.Label ?? EnumHelpers.Humanize(f.Property)} is required.");
                continue;
            }
            try
            {
                object value = type == typeof(string) ? raw
                    : type.IsEnum ? Enum.ToObject(type, int.Parse(raw))
                    : type == typeof(DateOnly) ? DateOnly.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)
                    : type == typeof(TimeOnly) ? TimeOnly.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)
                    : Convert.ChangeType(raw, type, System.Globalization.CultureInfo.InvariantCulture);
                prop.SetValue(entity, value);
            }
            catch (FormatException)
            {
                ModelState.AddModelError(f.Property, $"{f.Label ?? EnumHelpers.Humanize(f.Property)} isn't valid.");
            }
        }
        // Run data-annotation checks ([Required], [StringLength], [Range]) for the bound fields.
        var ctx = new ValidationContext(entity);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(entity, ctx, results, validateAllProperties: true);
        foreach (var r in results)
            foreach (var m in r.MemberNames.Where(m => Fields.Any(f => f.Property == m)))
                if (!ModelState.ContainsKey(m) || ModelState[m]!.Errors.Count == 0) ModelState.AddModelError(m, r.ErrorMessage ?? "Invalid value.");
    }

    private static string Display(object? v) => v switch
    {
        null => "",
        bool b => b ? "✓" : "",
        decimal d => d.ToString("N2"),
        DateOnly d => Format.Date(d),
        Enum e => e.DisplayName(),
        _ => v.ToString() ?? "",
    };
}

public class CrudRow
{
    public CrudRow(int id, List<string> cells) { Id = id; Cells = cells; }
    public int Id { get; }
    public List<string> Cells { get; }
}

public class CrudFieldModel
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Help { get; set; }
    public string Kind { get; set; } = "text";
    public string Step { get; set; } = "1";
    public bool Required { get; set; }
    public bool ReadOnly { get; set; }
    public int? MaxLength { get; set; }
    public object? Value { get; set; }
    public List<SelectListItem> Options { get; set; } = new();
}

public class CrudPageModel
{
    public string Title { get; set; } = "";
    public string Singular { get; set; } = "";
    public string? Intro { get; set; }
    public string Controller { get; set; } = "";
    public bool CanCreate { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public string? Search { get; set; }
    public List<string> Headers { get; set; } = new();
    public List<CrudRow> Rows { get; set; } = new();
    public PagedList<int>? Paging { get; set; }
    public bool IsNew { get; set; }
    public int Id { get; set; }
    public List<CrudFieldModel> Fields { get; set; } = new();
}
