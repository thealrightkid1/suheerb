using BushiBan.Web.Data;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BushiBan.Tests;

/// <summary>Runs services as if signed in to the Pasadena branch, and creates throwaway students so tests don't share balances.</summary>
public static class FinanceTestHelpers
{
    public static async Task<T> InPasadenaAsync<T>(this TestApp app, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var branchId = await db.Branches.Where(b => b.Code == "PAS").Select(b => b.Id).SingleAsync();
        using var _ = sp.GetRequiredService<ICurrentBranch>().Override(branchId);
        return await work(sp);
    }

    public static Task InPasadenaAsync(this TestApp app, Func<IServiceProvider, Task> work) =>
        app.InPasadenaAsync(async sp => { await work(sp); return true; });

    public static async Task<int> NewStudentAsync(this TestApp app, string first = "Test", int? courseId = null, decimal discount = 0)
    {
        return await app.InPasadenaAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var student = new Student
            {
                StudentNo = "T-" + Guid.NewGuid().ToString("N")[..10], FirstName = first, LastName = "Student" + Random.Shared.Next(1000, 9999),
                EnrollmentDate = new DateOnly(2026, 1, 1), CourseId = courseId, Discount = discount,
            };
            db.Students.Add(student);
            await db.SaveChangesAsync();
            return student.Id;
        });
    }

    public static Task<int> AccountIdAsync(this TestApp app, string code) =>
        app.InPasadenaAsync(sp => sp.GetRequiredService<AppDbContext>().DetailAccounts.Where(a => a.Code == code).Select(a => a.Id).SingleAsync());
}
