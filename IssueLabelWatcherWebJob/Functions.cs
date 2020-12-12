using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Timers;
using NCrontab;

namespace IssueLabelWatcherWebJob
{
    public class Functions
    {
        private const string SingletonScopeName = "IssuesLock";
        private readonly IIlwService _ilwService;
        private readonly IIlwStateService _ilwStateService;

        public Functions(IIlwService ilwService, IIlwStateService ilwStateService)
        {
            _ilwService = ilwService;
            _ilwStateService = ilwStateService;
        }

        [Singleton(SingletonScopeName, SingletonScope.Host)]
        public async Task FindRecentLabelledIssues([TimerTrigger(typeof(FindRecentLabelledIssuesTiming), RunOnStartup = true, UseMonitor = false)] TimerInfo timerInfo)
        {
            var state = await _ilwStateService.Load();
            await _ilwService.FindAndNotifyRecentLabelledIssues(state, TimeSpan.FromDays(1));
            await _ilwStateService.Save(state);
        }

        [Disable(typeof(DisableFindAllLabelledIssues))]
        [Singleton(SingletonScopeName, SingletonScope.Host)]
        public async Task FindAllLabelledIssues([TimerTrigger("%" + IlwConfiguration.FindAllLabelledIssuesTimingKey + "%", UseMonitor = false)] TimerInfo timerInfo)
        {
            var state = await _ilwStateService.Load();
            await _ilwService.FindAndNotifyAllLabelledIssues(state);
            await _ilwStateService.Save(state);
        }
    }

    public class DisableFindAllLabelledIssues
    {
        private readonly IIlwConfiguration _configuration;

        public DisableFindAllLabelledIssues(IIlwConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool IsDisabled(System.Reflection.MethodInfo methodInfo)
        {
            return !_configuration.EnableFindAllLabelledIssues;
        }
    }

    public class FindRecentLabelledIssuesTiming : TimerSchedule
    {
        private readonly TimerSchedule _inner;
        private readonly Random _random;

        public FindRecentLabelledIssuesTiming()
        {
            var configuration = (IIlwConfiguration)Program.ServiceProvider.GetService(typeof(IIlwConfiguration));
            var timing = configuration.FindRecentLabelledIssuesTiming;
            var parseOptions = CreateParseOptions(timing);
            var crontabSchedule = CrontabSchedule.TryParse(timing, parseOptions);

            if (crontabSchedule != null)
            {
                _inner = new CronSchedule(crontabSchedule);
            }
            else
            {
                var interval = TimeSpan.Parse(timing);
                _inner = new ConstantSchedule(interval);
            }

            if (configuration.RandomlyDelayFindRecentLabelledIssues)
            {
                _random = new Random();
            }
        }

        public override bool AdjustForDST => _inner.AdjustForDST;

        public override DateTime GetNextOccurrence(DateTime now)
        {
            var result = _inner.GetNextOccurrence(now);
            if (_random != null)
            {
                var range = (result - now).Ticks / 2;
                var delay = (long)(_random.NextDouble() * range);
                result = result.AddTicks(delay);
            }

            return result;
        }

        private static CrontabSchedule.ParseOptions CreateParseOptions(string cronExpression)
        {
            var options = new CrontabSchedule.ParseOptions
            {
                IncludingSeconds = HasSeconds(cronExpression),
            };

            return options;
        }

        private static bool HasSeconds(string cronExpression)
        {
            return cronExpression.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 5;
        }
    }
}
