# Developer guide

How the code is organised, and the conventions every screen follows. Read this before adding a feature.

## Stack

- ASP.NET Core 8 MVC with Razor views, rendered on the server.
- EF Core 8 on **SQL Server** in production. **SQLite** is supported for zero-setup local runs and tests.
- ASP.NET Core Identity for sign-in. Permissions are stored as user claims.
- Bootstrap 5.3, Bootstrap Icons, Tom Select (searchable dropdowns), Chart.js, Signature Pad and FullCalendar. All are vendored in `wwwroot/lib`, so the app needs no CDN.

## Folder layout

```
src/BushiBan.Web/
  Domain/           Entities: one file per area (People, Academics, Billing, Accounting…)
  Data/             AppDbContext, migrations, seed data (Seed/)
  Security/         Permissions catalog + presets, [HasPermission] attribute
  Services/         Cross-cutting services: CurrentBranch, NumberSequenceService, BranchClock,
                    AccountingService, LookupService, Forms/ (form schema + mapper)
  Infrastructure/   Generic CRUD controller, navigation (sidebar), helpers, view components
  Features/<Area>/  Controllers, services and view models for one feature area
  Views/<Controller>/  Razor views (standard MVC locations)
  wwwroot/          css/site.css, js/site.js, lib/ (vendored libraries)
tests/BushiBan.Tests/  Integration tests against the real app with in-memory SQLite
```

## Rules the whole app relies on

1. **Branch isolation is automatic.** Every entity implementing `IBranchScoped` (via `BranchEntity`) is filtered to the current branch by a global query filter, and `BranchId` is stamped on insert.
   - Never filter by branch by hand. Never call `IgnoreQueryFilters()` in a controller unless the screen is explicitly cross-branch and admin-only.
   - Background jobs and kiosks set the branch with `ICurrentBranch.Override(branchId)`.
2. **Deletes are soft.** `db.Remove(entity)` on an `AuditableEntity` sets `IsDeleted` instead of erasing the row, and the row disappears from every query. Child-only rows that inherit from `Entity` (voucher lines, schedule rows) are deleted for real.
3. **Everything is audited.** `SaveChanges` writes an `AuditLog` row per change: who, when, and which fields went from what to what. You don't need to do anything for this.
4. **Numbers are never reused.** Student, voucher, invoice and receipt numbers come from `NumberSequenceService`.
5. **The books always balance.** Create vouchers only through `AccountingService.CreateAsync`, which checks that debits equal credits and that the fiscal year isn't closed. Posted vouchers are never edited, only reversed with `ReverseAsync`.
6. **Permissions are checked on the server.** Put `[HasPermission(Permissions.X.Y)]` on every controller or action. Hide buttons in views with `User.HasPermission(...)`, but always enforce on the server. Everything requires sign-in unless it's marked `[AllowAnonymous]`.
7. **Dates use the branch's time zone.** Use `BranchClock.Today` and `BranchClock.Now`, never `DateTime.Now`. Store `DateOnly` for calendar dates, `TimeOnly` for times of day, and UTC `DateTime` for timestamps.

## Screen conventions

- **Titles and breadcrumbs:** set `ViewData["Title"]`. Optionally set `ViewData["Breadcrumbs"] = new List<(string, string?)> { ("Students", Url.Action("Index")), ("Edit", null) }`.
- **Header buttons:** put them in `@section Actions { … }`. Page scripts go in `@section Scripts`, extra CSS in `@section Head`.
- **Lists:** a `.card` holding a filter form (`card-header`) and a `table-responsive` table. Show an empty-state row with `<td class="empty">`. Page with `query.ToPagedAsync(page)` and `<partial name="_Pager" model="(paged.Page, paged.Pages, paged.Total)" />`.
- **Forms:** `.card.form-card`, split into sections with `<h2 class="form-section-title">`. Add `data-search` to a `<select>` to make it searchable. Use `asp-validation-for` or the `is-invalid` class for errors.
- **Deletes:** `<partial name="_DeleteButton" model='("Controller", id, "this thing")' />`. It confirms first and posts.
- **Messages after saving:** `this.Success("Saved.")` or `this.Error("…")`, then redirect.
- **Formatting:** `Format.Money`, `Format.Date`, `Format.DateTime`, `Format.Time`, and `enumValue.DisplayName()`.
- **Dropdown data:** `LookupService` (programs, ranks in belt order, belt sizes, cities grouped by state, accounts, staff).
- **Simple setup tables:** subclass `CrudController<T>` (see `Features/Setup/SetupControllers.cs`). You get list, search, paging, create, edit and soft delete, with permissions and an "is it still in use?" check.
- **Plain language:** labels and messages say what happened and what to do next. No error codes.

## Adding a feature area

1. Put controllers and services in `Features/<Area>/` and views in `Views/<Controller>/`.
2. Register services by implementing the matching partial method in `Features/<Area>/<Area>Module.cs`:
   ```csharp
   namespace BushiBan.Web.Infrastructure;
   public static partial class ModuleRegistration
   {
       static partial void RegisterBilling(IServiceCollection services, IConfiguration config)
       {
           services.AddScoped<BillingService>();
       }
   }
   ```
3. Add the screen to the sidebar in `Infrastructure/Navigation.cs`, with its permission.
4. Add tests in `tests/BushiBan.Tests/<Area>Tests.cs` using the `TestApp` fixture: `await app.SignInAsync("manager@bushiban.local")`, then `client.PostFormAsync(formPage, url, fields)`.

## Database changes

Change the entity in `Domain/`, then from `src/BushiBan.Web`:

```bash
dotnet ef migrations add <Name> -o Data/Migrations
```

Migrations apply automatically at start-up (`Database:MigrateOnStartup`). SQLite runs use `EnsureCreated`, so delete the local `.db` file after a model change.

## Demo accounts (Development only)

| Email | Password | Role |
| --- | --- | --- |
| admin@bushiban.local | Admin!2026 | System admin, every branch |
| manager@bushiban.local | Demo!2026 | Manager, Pasadena |
| accountant@bushiban.local | Demo!2026 | Accountant, Pasadena |
| frontdesk@bushiban.local | Demo!2026 | Receptionist, Pasadena |
| kiosk.attendance@bushiban.local | Demo!2026 | Attendance kiosk, Pasadena |
| kiosk.registration@bushiban.local | Demo!2026 | Registration kiosk, Pasadena |
