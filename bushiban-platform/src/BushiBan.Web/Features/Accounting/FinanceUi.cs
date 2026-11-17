using System.Net;
using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Html;

namespace BushiBan.Web.Features.Accounting;

/// <summary>Small view helpers for the billing and accounting screens (status badges, money cells).</summary>
public static class FinanceUi
{
    public const string Stylesheet = "~/css/finance.css";
    public const string Script = "~/js/finance.js";

    public static IHtmlContent Badge(string css, string text) =>
        new HtmlString($"<span class=\"badge badge-status {css}\">{WebUtility.HtmlEncode(text)}</span>");

    public static IHtmlContent Badge(InvoiceStatus status, bool overdue = false) => overdue && status is InvoiceStatus.Open or InvoiceStatus.PartiallyPaid
        ? Badge("st-overdue", status == InvoiceStatus.PartiallyPaid ? "Overdue (part paid)" : "Overdue")
        : Badge(status switch
        {
            InvoiceStatus.Open => "st-open",
            InvoiceStatus.PartiallyPaid => "st-partial",
            InvoiceStatus.Paid => "st-paid",
            _ => "st-void",
        }, status.DisplayName());

    public static IHtmlContent Badge(VoucherStatus status) => Badge(status switch
    {
        VoucherStatus.Draft => "st-draft",
        VoucherStatus.Posted => "st-posted",
        _ => "st-reversed",
    }, status.DisplayName());

    public static IHtmlContent Badge(ContractStatus status) => Badge(status switch
    {
        ContractStatus.Active => "st-active",
        ContractStatus.Completed => "st-completed",
        ContractStatus.OnHold => "st-onhold",
        _ => "st-cancelled",
    }, status.DisplayName());

    /// <summary>Money for tables: blank for zero when <paramref name="blankZero"/> is set, red when negative.</summary>
    public static IHtmlContent Money(decimal value, bool blankZero = false) =>
        blankZero && value == 0 ? HtmlString.Empty
        : new HtmlString(value < 0 ? $"<span class=\"negative\">{WebUtility.HtmlEncode(Format.Money(value))}</span>" : WebUtility.HtmlEncode(Format.Money(value)));

    public static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "";
}
