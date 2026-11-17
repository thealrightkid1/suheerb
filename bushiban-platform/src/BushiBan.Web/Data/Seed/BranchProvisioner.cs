using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.Accounting;
using BushiBan.Web.Domain.Billing;
using BushiBan.Web.Domain.Communication;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Services;
using BushiBan.Web.Services.Forms;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Data.Seed;

/// <summary>
/// Gives a branch its starting setup: ranks, the Standard System, belt sizes, fee structure, programs,
/// chart of accounts, fiscal year, billing accounts, kiosk forms and message templates.
/// Safe to run repeatedly: each part is only added if the branch has none yet (deleted items stay deleted).
/// </summary>
public class BranchProvisioner
{
    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;

    public BranchProvisioner(AppDbContext db, ICurrentBranch branch) { _db = db; _branch = branch; }

    public async Task ProvisionAsync(int branchId, CancellationToken ct = default)
    {
        using var _ = _branch.Override(branchId);

        if (!await _db.Ranks.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var ranks = ReferenceData.StandardRanks.Select(r => new Rank { BranchId = branchId, Name = r.Name, Color = r.Color }).ToList();
            _db.Ranks.AddRange(ranks);
            var system = new RankingSystem
            {
                BranchId = branchId, Name = "Standard System", Description = "Full belt progression",
                Ranks = ranks.Select((r, i) => new RankingSystemRank { Rank = r, Index = i + 1, DurationInDays = 90 }).ToList(),
            };
            _db.RankingSystems.Add(system);
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.BeltSizes.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            _db.BeltSizes.AddRange(ReferenceData.BeltSizes.Select(b => new BeltSize { BranchId = branchId, Name = b.Name, SizeInInch = b.Inches }));
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.FeeStructures.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            _db.FeeStructures.Add(new FeeStructure { BranchId = branchId, Name = "Standard Martial Arts Fee", AdmissionFee = 50, SecurityFee = 100, MonthlyFee = 80 });
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.Courses.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var fee = await _db.FeeStructures.FirstOrDefaultAsync(f => f.BranchId == branchId, ct);
            var system = await _db.RankingSystems.FirstOrDefaultAsync(s => s.BranchId == branchId, ct);
            foreach (var p in ReferenceData.Programs)
            {
                var course = new Course { BranchId = branchId, Name = p.Name, Description = p.Description, MinimumTermMonths = p.TermMonths, FeeStructureId = fee?.Id };
                if (system is not null) course.RankingSystems.Add(new CourseRankingSystem { RankingSystemId = system.Id, Index = 1 });
                _db.Courses.Add(course);
            }
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.MainAccounts.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var mains = ReferenceData.MainAccounts.ToDictionary(m => m.Code, m => new MainAccount { BranchId = branchId, Code = m.Code, Name = m.Name, AccountType = m.Type });
            var subs = ReferenceData.SubAccounts.ToDictionary(s => s.Code, s => new SubAccount { BranchId = branchId, Code = s.Code, Name = s.Name, MainAccount = mains[Parent(s.Code)] });
            var details = ReferenceData.DetailAccounts.Select(d => new DetailAccount { BranchId = branchId, Code = d.Code, Name = d.Name, SubAccount = subs[Parent(d.Code)] });
            _db.MainAccounts.AddRange(mains.Values);
            _db.SubAccounts.AddRange(subs.Values);
            _db.DetailAccounts.AddRange(details);
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.FiscalYears.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var y = DateTime.UtcNow.Year;
            _db.FiscalYears.Add(new FiscalYear { BranchId = branchId, Name = $"FY {y}", StartDate = new DateOnly(y, 1, 1), EndDate = new DateOnly(y, 12, 31) });
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.AccountingSettings.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var byCode = await _db.DetailAccounts.Where(a => a.BranchId == branchId).ToDictionaryAsync(a => a.Code, a => a.Id, ct);
            int? Id(string code) => byCode.TryGetValue(code, out var id) ? id : null;
            _db.AccountingSettings.Add(new AccountingSettings
            {
                BranchId = branchId,
                ReceivableAccountId = Id("10-01-03-001"),
                TuitionIncomeAccountId = Id("40-01-01-001"),
                AdmissionIncomeAccountId = Id("40-01-02-001"),
                SecurityDepositAccountId = Id("20-01-01-001"),
                LateFeeIncomeAccountId = Id("40-01-04-001"),
                CashAccountId = Id("10-01-01-001"),
                BankAccountId = Id("10-01-02-001"),
            });
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.FormDefinitions.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            var courses = await _db.Courses.Where(c => c.BranchId == branchId).ToDictionaryAsync(c => c.Name, c => c.Id, ct);
            var order = 0;
            foreach (var seed in KioskForms.All())
            {
                var def = new FormDefinition
                {
                    BranchId = branchId, Name = seed.Name, Category = seed.Category, SortOrder = ++order,
                    CourseId = seed.ProgramName is not null && courses.TryGetValue(seed.ProgramName, out var cid) ? cid : null,
                };
                var version = new FormVersion { FormDefinition = def, VersionNumber = 1, SchemaJson = FormSchema.Serialize(seed.Fields), PublishedAt = DateTime.UtcNow };
                _db.FormVersions.Add(version);
                await _db.SaveChangesAsync(ct);
                def.PublishedVersionId = version.Id;
                await _db.SaveChangesAsync(ct);
            }
        }

        if (!await _db.MessageTemplates.IgnoreQueryFilters().AnyAsync(r => r.BranchId == branchId, ct))
        {
            _db.MessageTemplates.AddRange(
                new MessageTemplate { BranchId = branchId, Name = "Welcome to Bushi Ban", Channel = MessageChannel.Email, Subject = "Welcome to Bushi Ban {{BranchName}}!",
                    Body = "Hi {{FirstName}},\n\nWelcome to Bushi Ban International {{BranchName}}! We're excited to have you in our {{Program}} program.\n\nSee you on the mat,\nThe Bushi Ban team" },
                new MessageTemplate { BranchId = branchId, Name = "Trial class reminder", Channel = MessageChannel.SMS,
                    Body = "Hi {{FirstName}}, this is Bushi Ban {{BranchName}}. Just a reminder about your trial class. Reply if you need to reschedule!" },
                new MessageTemplate { BranchId = branchId, Name = "Payment reminder", Channel = MessageChannel.Email, Subject = "Your Bushi Ban account balance",
                    Body = "Hi {{FirstName}},\n\nThis is a friendly reminder that your account has a balance of {{Balance}}. Please stop by the front desk or reply to this email.\n\nThank you,\nBushi Ban {{BranchName}}" },
                new MessageTemplate { BranchId = branchId, Name = "We miss you", Channel = MessageChannel.SMS,
                    Body = "Hi {{FirstName}}, we haven't seen you at Bushi Ban {{BranchName}} in a while. We'd love to see you back in class this week!" },
                new MessageTemplate { BranchId = branchId, Name = "Belt promotion congratulations", Channel = MessageChannel.Email, Subject = "Congratulations on your new belt!",
                    Body = "Hi {{FirstName}},\n\nCongratulations on your promotion! Your hard work is paying off. Keep it up.\n\nBushi Ban {{BranchName}}" });
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string Parent(string code) => code[..code.LastIndexOf('-')];
}
