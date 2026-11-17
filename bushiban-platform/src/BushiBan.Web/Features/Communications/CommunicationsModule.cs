using BushiBan.Web.Features.Communications;
using BushiBan.Web.Features.Communications.Messaging;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    static partial void RegisterCommunications(IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmailOptions>(config.GetSection("Email"));
        services.Configure<SmsOptions>(config.GetSection("Sms"));
        services.AddTransient<IEmailSender, SmtpEmailSender>();
        services.AddHttpClient<ISmsSender, TwilioSmsSender>(http =>
        {
            http.BaseAddress = new Uri(TwilioSmsSender.BaseAddress);
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<MessagingService>();
        services.AddScoped<CalendarFeed>();
    }
}
