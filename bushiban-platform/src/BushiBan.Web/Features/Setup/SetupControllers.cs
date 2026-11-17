using BushiBan.Web.Data;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.Organization;
using BushiBan.Web.Infrastructure.Crud;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Setup;

public class RanksController : CrudController<Rank>
{
    public RanksController(AppDbContext db) : base(db) { }
    protected override string Title => "Ranks";
    protected override string Singular => "Rank";
    protected override string? Intro => "Belt ranks are defined once and reused across ranking systems.";
    protected override string ViewPermission => P.Ranks.View;
    protected override string CreatePermission => P.Ranks.Create;
    protected override string EditPermission => P.Ranks.Edit;
    protected override string DeletePermission => P.Ranks.Delete;
    protected override IQueryable<Rank> Search(IQueryable<Rank> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<Rank> Sort(IQueryable<Rank> q) =>
        q.OrderBy(r => Db.RankingSystemRanks.Where(s => s.RankId == r.Id).Min(s => (int?)s.Index) ?? 999).ThenBy(r => r.Name);
    protected override IEnumerable<CrudColumn<Rank>> Columns => new CrudColumn<Rank>[]
    {
        new("Rank", r => r.Name),
        new("In systems", r => Db.RankingSystemRanks.Count(s => s.RankId == r.Id && !s.RankingSystem!.IsDeleted)),
        new("Students", r => Db.Students.Count(s => s.RankId == r.Id)),
        new("Leads", r => Db.Leads.Count(s => s.RankId == r.Id)),
        new("Active", r => r.IsActive),
    };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(Rank.Name), "Rank name"),
        new(nameof(Rank.Color), "Belt colour", "Hex colour for the belt badge, e.g. #198754 for green."),
        new(nameof(Rank.IsActive), "Active"),
    };
    protected override async Task<string?> CanDeleteAsync(Rank r)
    {
        var students = await Db.Students.CountAsync(s => s.RankId == r.Id);
        var systems = await Db.RankingSystemRanks.CountAsync(s => s.RankId == r.Id && !s.RankingSystem!.IsDeleted);
        return students + systems == 0 ? null : $"it's used by {students} student(s) and {systems} ranking system(s).";
    }
}

public class BeltSizesController : CrudController<BeltSize>
{
    public BeltSizesController(AppDbContext db) : base(db) { }
    protected override string Title => "Belt sizes";
    protected override string Singular => "Belt size";
    protected override string ViewPermission => P.BeltSizes.View;
    protected override string CreatePermission => P.BeltSizes.Create;
    protected override string EditPermission => P.BeltSizes.Edit;
    protected override string DeletePermission => P.BeltSizes.Delete;
    protected override IQueryable<BeltSize> Search(IQueryable<BeltSize> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<BeltSize> Sort(IQueryable<BeltSize> q) => q.OrderBy(b => b.SizeInInch);
    protected override IEnumerable<CrudColumn<BeltSize>> Columns => new CrudColumn<BeltSize>[]
    {
        new("Belt", b => b.Name), new("Size (inches)", b => b.SizeInInch), new("Active", b => b.IsActive),
    };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(BeltSize.Name)), new(nameof(BeltSize.SizeInInch), "Belt length (inches)"), new(nameof(BeltSize.IsActive), "Active"),
    };
    protected override async Task<string?> CanDeleteAsync(BeltSize b)
    {
        var n = await Db.Students.CountAsync(s => s.BeltSizeId == b.Id) + await Db.Leads.CountAsync(s => s.BeltSizeId == b.Id);
        return n == 0 ? null : $"{n} student(s) or lead(s) use it.";
    }
}

public class FeeStructuresController : CrudController<FeeStructure>
{
    public FeeStructuresController(AppDbContext db) : base(db) { }
    protected override string Title => "Fee structures";
    protected override string Singular => "Fee structure";
    protected override string? Intro => "Standard prices. Programs use a fee structure; each student's contract can still be priced individually.";
    protected override string ViewPermission => P.FeeStructures.View;
    protected override string CreatePermission => P.FeeStructures.Manage;
    protected override string EditPermission => P.FeeStructures.Manage;
    protected override string DeletePermission => P.FeeStructures.Manage;
    protected override IQueryable<FeeStructure> Search(IQueryable<FeeStructure> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<FeeStructure> Sort(IQueryable<FeeStructure> q) => q.OrderBy(b => b.Name);
    protected override IEnumerable<CrudColumn<FeeStructure>> Columns => new CrudColumn<FeeStructure>[]
    {
        new("Name", b => b.Name), new("Admission fee", b => b.AdmissionFee.ToString("C2")),
        new("Security fee", b => b.SecurityFee.ToString("C2")), new("Monthly fee", b => b.MonthlyFee.ToString("C2")), new("Active", b => b.IsActive),
    };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(FeeStructure.Name), "Fee structure name"), new(nameof(FeeStructure.AdmissionFee)),
        new(nameof(FeeStructure.SecurityFee), "Security deposit", "Refundable deposit, held as a liability."),
        new(nameof(FeeStructure.MonthlyFee)), new(nameof(FeeStructure.IsActive), "Active"),
    };
    protected override async Task<string?> CanDeleteAsync(FeeStructure f)
    {
        var n = await Db.Courses.CountAsync(c => c.FeeStructureId == f.Id);
        return n == 0 ? null : $"{n} program(s) use it.";
    }
}

public class CountriesController : CrudController<Country>
{
    public CountriesController(AppDbContext db) : base(db) { }
    protected override string Title => "Countries";
    protected override string Singular => "Country";
    protected override string ViewPermission => P.Locations.View;
    protected override string CreatePermission => P.Locations.Manage;
    protected override string EditPermission => P.Locations.Manage;
    protected override string DeletePermission => P.Locations.Manage;
    protected override IQueryable<Country> Search(IQueryable<Country> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<Country> Sort(IQueryable<Country> q) => q.OrderBy(b => b.Name);
    protected override IEnumerable<CrudColumn<Country>> Columns => new CrudColumn<Country>[]
    { new("Country", c => c.Name), new("States", c => Db.States.Count(s => s.CountryId == c.Id)), new("Active", c => c.IsActive) };
    protected override IEnumerable<CrudField> Fields => new CrudField[] { new(nameof(Country.Name)), new(nameof(Country.IsActive), "Active") };
    protected override async Task<string?> CanDeleteAsync(Country c) =>
        await Db.States.AnyAsync(s => s.CountryId == c.Id) ? "it still has states." : null;
}

public class StatesController : CrudController<State>
{
    public StatesController(AppDbContext db) : base(db) { }
    protected override string Title => "States";
    protected override string Singular => "State";
    protected override string ViewPermission => P.Locations.View;
    protected override string CreatePermission => P.Locations.Manage;
    protected override string EditPermission => P.Locations.Manage;
    protected override string DeletePermission => P.Locations.Manage;
    protected override IQueryable<State> Query() => Db.States.Include(s => s.Country);
    protected override IQueryable<State> Search(IQueryable<State> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<State> Sort(IQueryable<State> q) => q.OrderBy(b => b.Name);
    protected override IEnumerable<CrudColumn<State>> Columns => new CrudColumn<State>[]
    { new("State", c => c.Name), new("Country", c => c.Country?.Name), new("Cities", c => Db.Cities.Count(x => x.StateId == c.Id)), new("Active", c => c.IsActive) };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(State.Name)),
        new(nameof(State.CountryId), "Country", Options: async () => await Db.Countries.OrderBy(c => c.Name).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToListAsync()),
        new(nameof(State.IsActive), "Active"),
    };
    protected override async Task<string?> CanDeleteAsync(State s) =>
        await Db.Cities.AnyAsync(c => c.StateId == s.Id) ? "it still has cities." : null;
}

public class CitiesController : CrudController<City>
{
    public CitiesController(AppDbContext db) : base(db) { }
    protected override string Title => "Cities";
    protected override string Singular => "City";
    protected override string ViewPermission => P.Locations.View;
    protected override string CreatePermission => P.Locations.Manage;
    protected override string EditPermission => P.Locations.Manage;
    protected override string DeletePermission => P.Locations.Manage;
    protected override IQueryable<City> Query() => Db.Cities.Include(s => s.State);
    protected override IQueryable<City> Search(IQueryable<City> q, string t) => q.Where(r => r.Name.Contains(t) || r.State!.Name.Contains(t));
    protected override IOrderedQueryable<City> Sort(IQueryable<City> q) => q.OrderBy(b => b.State!.Name).ThenBy(b => b.Name);
    protected override IEnumerable<CrudColumn<City>> Columns => new CrudColumn<City>[]
    { new("City", c => c.Name), new("State", c => c.State?.Name), new("Active", c => c.IsActive) };
    protected override IEnumerable<CrudField> Fields => new CrudField[]
    {
        new(nameof(City.Name)),
        new(nameof(City.StateId), "State", Options: async () => await Db.States.OrderBy(c => c.Name).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToListAsync()),
        new(nameof(City.IsActive), "Active"),
    };
    protected override async Task<string?> CanDeleteAsync(City c)
    {
        var n = await Db.Students.IgnoreQueryFilters().CountAsync(s => s.CityId == c.Id && !s.IsDeleted)
              + await Db.Branches.CountAsync(b => b.CityId == c.Id);
        return n == 0 ? null : $"{n} student(s) or branch(es) are in this city.";
    }
}

public class SchoolsController : CrudController<School>
{
    public SchoolsController(AppDbContext db) : base(db) { }
    protected override string Title => "Schools";
    protected override string Singular => "School";
    protected override string? Intro => "A school groups branches under one brand and legal entity.";
    protected override string ViewPermission => P.Schools.View;
    protected override string CreatePermission => P.Schools.Manage;
    protected override string EditPermission => P.Schools.Manage;
    protected override string DeletePermission => P.Schools.Manage;
    protected override IQueryable<School> Search(IQueryable<School> q, string t) => q.Where(r => r.Name.Contains(t));
    protected override IOrderedQueryable<School> Sort(IQueryable<School> q) => q.OrderBy(b => b.Name);
    protected override IEnumerable<CrudColumn<School>> Columns => new CrudColumn<School>[]
    { new("School", c => c.Name), new("Legal name", c => c.LegalName), new("Branches", c => Db.Branches.Count(b => b.SchoolId == c.Id)), new("Active", c => c.IsActive) };
    protected override IEnumerable<CrudField> Fields => new CrudField[] { new(nameof(School.Name)), new(nameof(School.LegalName)), new(nameof(School.IsActive), "Active") };
    protected override async Task<string?> CanDeleteAsync(School s) =>
        await Db.Branches.AnyAsync(b => b.SchoolId == s.Id) ? "it still has branches." : null;
}
