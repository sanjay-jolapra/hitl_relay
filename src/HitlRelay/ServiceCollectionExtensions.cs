using HitlRelay.Channels.Email;
using HitlRelay.Channels.Slack;
using HitlRelay.Channels.Teams;
using HitlRelay.Channels.Test;
using HitlRelay.Channels.Telegram;
using HitlRelay.Channels.WhatsApp;
using HitlRelay.Core;
using HitlRelay.Data;
using HitlRelay.Models;
using HitlRelay.Sample;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.EntityFrameworkCore;

namespace HitlRelay;

public static class ServiceCollectionExtensions
{
    /// <summary>Shared core: options, database, services, queue and background workers. No channel code.</summary>
    public static IServiceCollection AddHitlCore(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HitlOptions>(config.GetSection(HitlOptions.Section));
        var connection = config[$"{HitlOptions.Section}:ConnectionString"] ?? new HitlOptions().ConnectionString;

        services.AddDbContext<HitlDbContext>(o => o.UseSqlite(connection));
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<AuditService>();
        services.AddScoped<AdapterRegistry>();
        services.AddScoped<HitlCompletion>();
        services.AddScoped<HitlService>();
        services.AddScoped<HitlReplyHandler>();

        services.AddSingleton<InboundQueue>();
        services.AddSingleton<HitlScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<HitlScheduler>());
        services.AddHostedService<InboundProcessor>();
        return services;
    }

    /// <summary>The five channel adapters, their options and endpoints' dependencies.</summary>
    public static IServiceCollection AddHitlChannels(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SlackOptions>(config.GetSection("Channels:Slack"));
        services.Configure<TelegramOptions>(config.GetSection("Channels:Telegram"));
        services.Configure<WhatsAppOptions>(config.GetSection("Channels:WhatsApp"));
        services.Configure<TeamsOptions>(config.GetSection("Channels:Teams"));
        services.Configure<EmailOptions>(config.GetSection("Channels:Email"));

        AddAdapter<SlackAdapter>(services, ChannelKeys.Slack);
        AddAdapter<TelegramAdapter>(services, ChannelKeys.Telegram);
        AddAdapter<WhatsAppAdapter>(services, ChannelKeys.WhatsApp);
        AddAdapter<EmailAdapter>(services, ChannelKeys.Email);
        AddAdapter<TeamsAdapter>(services, ChannelKeys.Teams);

        services.AddHostedService<TelegramWebhookRegistrar>();

        // "test" loopback channel for the test console: no vendor account needed (only when Hitl:EnableDemo is on).
        if (config.GetValue<bool>("Hitl:EnableDemo"))
            AddAdapter<TestAdapter>(services, TestAdapter.Key);

        // Teams: Bot Framework authentication reads MicrosoftAppId/Password/Type/TenantId from this section.
        services.AddSingleton<BotFrameworkAuthentication>(sp =>
            new ConfigurationBotFrameworkAuthentication(config.GetSection("Channels:Teams")));
        // CloudAdapter has two public constructors, which DI cannot choose between, so pick one explicitly.
        services.AddSingleton<CloudAdapter>(sp => new CloudAdapter(
            sp.GetRequiredService<BotFrameworkAuthentication>(),
            sp.GetRequiredService<ILogger<CloudAdapter>>()));
        services.AddTransient<TeamsHitlBot>();
        services.AddScoped<ChannelConnectionStore>();

        // E-mail: SMTP (MailKit) or Microsoft Graph, chosen by Channels:Email:Provider.
        services.AddSingleton<EmailTokenService>();
        services.AddHttpClient<GraphEmailSender>();
        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<IEmailSender>(sp =>
            string.Equals(config["Channels:Email:Provider"], "Graph", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<GraphEmailSender>()
                : sp.GetRequiredService<SmtpEmailSender>());
        return services;
    }

    /// <summary>Sample workflow engine acting as the host's IWorkflowResumer.</summary>
    public static IServiceCollection AddSampleWorkflow(this IServiceCollection services)
    {
        services.AddScoped<SampleWorkflowEngine>();
        services.AddScoped<IWorkflowResumer>(sp => sp.GetRequiredService<SampleWorkflowEngine>());
        return services;
    }

    // Each adapter is a typed HttpClient (retry/429 handling lives in HttpHelpers) and registered by channel key.
    private static void AddAdapter<T>(IServiceCollection services, string channelKey) where T : class, IHitlChannelAdapter
    {
        if (typeof(T) == typeof(TeamsAdapter) || typeof(T) == typeof(EmailAdapter))
            services.AddScoped<T>();
        else
            services.AddHttpClient<T>();
        services.AddSingleton(new AdapterRegistration(channelKey, typeof(T)));
    }

    private static void TryAddSingleton<T>(this IServiceCollection services, T instance) where T : class
    {
        if (!services.Any(d => d.ServiceType == typeof(T))) services.AddSingleton(instance);
    }
}
