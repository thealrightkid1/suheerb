using BushiBan.Web.Infrastructure;
using BushiBan.Web.Security;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Mvc;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Features.Billing;

/// <summary>Billing overview: this month's numbers, who owes what (aging), and the monthly billing and late-fee runs.</summary>
[HasPermission(P.Billing.View)]
public class BillingController : Controller
{
    private readonly BillingService _billing;
    private readonly BranchClock _clock;

    public BillingController(BillingService billing, BranchClock clock) { _billing = billing; _clock = clock; }

    public async Task<IActionResult> Index()
    {
        ViewData["Today"] = _clock.Today;
        return View(await _billing.OverviewAsync(_clock.Today));
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> RunMonthly()
    {
        var today = _clock.Today;
        try
        {
            var r = await _billing.RunMonthlyBillingAsync(today);
            var done = r.InvoicesCreated == 0
                ? $"Monthly billing is up to date through {today:MMMM yyyy}. No new invoices were needed."
                : $"Monthly billing done: {r.InvoicesCreated} invoice(s) for {Format.Money(r.Total)}.";
            if (r.ContractsCompleted > 0) done += $" {r.ContractsCompleted} contract(s) reached the end of their term and were marked completed.";
            this.Success(done);
            if (r.Problems.Count > 0) this.Error($"{r.Problems.Count} contract(s) couldn't be billed: " + string.Join(" · ", r.Problems.Take(5)) + (r.Problems.Count > 5 ? " …" : ""));
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            this.Error(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, HasPermission(P.Billing.Manage)]
    public async Task<IActionResult> ApplyLateFees()
    {
        try
        {
            var r = await _billing.ApplyLateFeesAsync(_clock.Today);
            this.Success(r.FeesApplied == 0
                ? "No late fees were due. Every unpaid tuition invoice is either within its grace period or already has a late fee."
                : $"{r.FeesApplied} late fee(s) added for {Format.Money(r.Total)}.");
            if (r.Problems.Count > 0) this.Error(string.Join(" · ", r.Problems.Take(5)));
        }
        catch (Exception ex) when (ex is BillingException or AccountingException)
        {
            this.Error(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }
}
