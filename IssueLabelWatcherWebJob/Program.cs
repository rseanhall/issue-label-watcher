using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IssueLabelWatcherWebJob
{
    public class Program
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        public static async Task Main()
        {
            var builder = new HostBuilder()
#if DEBUG
                .UseEnvironment("development")
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
                .ConfigureServices(s =>
                {
                    s.AddSingleton<IIlwConfiguration, IlwConfiguration>();
                    s.AddSingleton<INameResolver, IlwNameResolver>();
                    s.AddSingleton<IEmailSender, SmtpEmailSender>();
                    s.AddSingleton<IGithubService, GithubService>();
                    s.AddSingleton<IIlwStateService, IlwStateService>();
                    s.AddSingleton<IIlwService, IlwService>();
                });

            var host = builder.Build();
            ServiceProvider = host.Services;
            using (host)
            {
                await host.RunAsync();
            }
        }
    }
}
