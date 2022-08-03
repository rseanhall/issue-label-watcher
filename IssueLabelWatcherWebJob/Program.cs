using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IssueLabelWatcherWebJob
{
    public class Program
    {
        public const string ApplicationUserAgent = $"issue-label-watcher-{ThisAssembly.AssemblyInformationalVersion}";

        public static IServiceProvider? ServiceProvider { get; private set; }

        public static async Task Main()
        {
            var builder = new HostBuilder()
#if DEBUG
                .UseEnvironment("development")
                .UseConsoleLifetime()
#endif
                .ConfigureWebJobs(b =>
                {
                    b.AddAzureStorageCoreServices();
                    b.AddAzureStorage();
                    b.AddTimers();
                })
                .ConfigureLogging((context, b) =>
                {
                    b.AddConsole();

                    string instrumentationKey = context.Configuration["APPINSIGHTS_INSTRUMENTATIONKEY"];
                    if (!string.IsNullOrEmpty(instrumentationKey))
                    {
                        b.AddApplicationInsightsWebJobs(o => o.InstrumentationKey = instrumentationKey);
                    }

                    if (context.HostingEnvironment.IsDevelopment() || !string.IsNullOrEmpty(context.Configuration["ilw:DebugLogging"]))
                    {
                        b.AddFilter(nameof(IssueLabelWatcherWebJob), LogLevel.Debug);
                    }
                })
                .ConfigureServices((context, s) =>
                {
                    s.AddSingleton<IIlwConfiguration, IlwConfiguration>();
                    s.AddSingleton<INameResolver, IlwNameResolver>();
                    s.AddSingleton<IEmailSender, EmailSender>();
                    s.AddSingleton<IGithubService, GithubService>();
                    s.AddSingleton<IIlwStateService, IlwStateService>();
                    s.AddSingleton<IIlwService, IlwService>();
                    s.AddSingleton<SmtpEmailSender>();
                    s.AddGoogleServices();

                    s.Configure<SingletonOptions>(o =>
                    {
                        o.LockPeriod = TimeSpan.FromMinutes(1);
                    });
                });

            var host = builder.Build();
            using (host)
            {
                ServiceProvider = host.Services;

                var logger = ServiceProvider.GetRequiredService<ILogger<IlwConfiguration>>();
                var ilwConfiguration = ServiceProvider.GetRequiredService<IIlwConfiguration>();
                ilwConfiguration!.PrintConfiguration(logger);

                var googleCredentialService = ServiceProvider.GetRequiredService<IGoogleCredentialService>();
                await googleCredentialService.Initialize(default);

                await host.RunAsync();
            }
        }
    }
}
