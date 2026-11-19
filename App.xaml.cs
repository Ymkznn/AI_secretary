using System.Net.Http;
using System.Windows;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Agent.Providers;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Server;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiSecretary.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureAndMigrateLegacyData();
        LoadDotEnv();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(AppPaths.UserSettingsPath, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(config);
        sc.AddSingleton<AppSettingsService>();
        sc.AddSingleton<InteractionGateway>();
        sc.AddLogging(b => b.AddConsole());

        sc.AddHttpClient("EwsClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseDefaultCredentials = true,
                PreAuthenticate = true,
                DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials
            });

        sc.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials
            });

        foreach (var adapterType in typeof(ILlmProviderAdapter).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ILlmProviderAdapter).IsAssignableFrom(t)))
        {
            sc.AddSingleton(typeof(ILlmProviderAdapter), adapterType);
        }

        sc.AddHttpClient<IModelGateway, FoundryModelGateway>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials
            });
        sc.AddHttpClient("TtsClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseProxy = false
            });
        sc.AddSingleton<SqliteStore>();
        sc.AddSingleton<GuardAuditStore>();
        sc.AddSingleton<UiLocalizer>();
        sc.AddSingleton<StyleService>();
        sc.AddSingleton<TtsService>();
        sc.AddSingleton<SandboxService>();
        sc.AddSingleton<GuardService>();
        sc.AddSingleton<MailRuleEngine>();
        sc.AddSingleton<ContextSessionManager>();
        sc.AddSingleton<IMailBroker, EwsDirectMailBroker>();
        sc.AddSingleton<GmailSkillService>();
        sc.AddSingleton<AppLauncherService>();
        sc.AddSingleton<ScreenCaptureService>();
        sc.AddSingleton<ActivityMonitorService>();
        sc.AddSingleton<PageIntentPredictorService>();
        sc.AddSingleton<BrowserHostService>();
        sc.AddSingleton<BrowserFormBridgeService>();
        sc.AddSingleton<StandingWatcherService>();
        sc.AddSingleton<WorkflowCacheService>();
        sc.AddSingleton<WorkflowOptimizerAgent>();
        sc.AddSingleton<OfficeContextService>();
        sc.AddSingleton<CustomSkillService>();
        foreach (var skillType in typeof(IBasicSkill).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IBasicSkill).IsAssignableFrom(t)))
        {
            sc.AddSingleton(typeof(IBasicSkill), skillType);
        }
        sc.AddSingleton<SkillRegistry>();
        sc.AddSingleton<SkillRuntime>();
        sc.AddSingleton<WorkerAgent>();
        sc.AddSingleton<PlannerAgent>();
        sc.AddSingleton<HolidaySyncAgent>();
        sc.AddSingleton<MainAgent>();
        sc.AddSingleton<MailSyncService>();
        sc.AddSingleton<SecretaryController>();
        sc.AddSingleton<ApiServerHost>();
        sc.AddSingleton<MainWindow>();

        _services = sc.BuildServiceProvider();

        var skillRegistry = _services.GetRequiredService<SkillRegistry>();
        await skillRegistry.InitializeAsync();
        _services.GetRequiredService<GuardService>().BindSkillCatalog(skillRegistry);

        var localizer = _services.GetRequiredService<UiLocalizer>();
        await localizer.InitializeAsync(UiLocalizer.BuildCoreHotEntries(skillRegistry));

        await HolidayHelper.InitializeAsync(
            _services.GetRequiredService<SqliteStore>(),
            _services.GetRequiredService<HolidaySyncAgent>());

        _services.GetRequiredService<SkillRuntime>().ValidateAtStartup();
        var controller = _services.GetRequiredService<SecretaryController>();
        await controller.InitializeAsync();

        var apiHost = _services.GetRequiredService<ApiServerHost>();
        try
        {
            await apiHost.StartAsync();
        }
        catch (Exception ex)
        {
            _services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "ApiServerHost failed to start; HTTP API will be unavailable.");
        }

        bool isHeadless = e.Args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
        if (isHeadless)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }

        var mainWin = _services.GetRequiredService<MainWindow>();
        mainWin.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var apiHost = _services?.GetService<ApiServerHost>();
            if (apiHost is not null)
                apiHost.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        if (_services is not null)
        {
            try { _services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        base.OnExit(e);
    }

    private static void LoadDotEnv()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim().Trim('"');
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)) && !string.IsNullOrEmpty(val))
                {
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
        }
    }
}
