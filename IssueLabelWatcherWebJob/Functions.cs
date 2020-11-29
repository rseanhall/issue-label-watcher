using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Timers;
using NCrontab;

namespace IssueLabelWatcherWebJob
{
    public class Functions
    {
        const string IlwBlobName = "issue-label-watcher/state";
        private readonly IIlwService _ilwService;

        public Functions(IIlwService ilwService)
        {
            _ilwService = ilwService;
        }

        [Singleton("IssuesLock", SingletonScope.Host)]
        [return: Blob(IlwBlobName, FileAccess.Write)]
        public async Task<string> FindRecentLabelledIssues([TimerTrigger(typeof(FindRecentLabelledIssuesTiming), RunOnStartup = true, UseMonitor = false)] TimerInfo timerInfo, [Blob(IlwBlobName, FileAccess.Read)] string stateJson)
        {
            var state = new IlwState();
            state.Load(stateJson);
            await _ilwService.FindAndNotifyRecentLabelledIssues(state, TimeSpan.FromDays(1));
            return state.Save();
        }

        [Disable(typeof(DisableFindAllLabelledIssues))]
        [Singleton("IssuesLock", SingletonScope.Host)]
        [return: Blob(IlwBlobName, FileAccess.Write)]
        public async Task<string> FindAllLabelledIssues([TimerTrigger("%" + IlwConfiguration.FindAllLabelledIssuesTimingKey + "%", UseMonitor = false)] TimerInfo timerInfo, [Blob(IlwBlobName, FileAccess.Read)] string stateJson)
        {
            var state = new IlwState();
            state.Load(stateJson);
            await _ilwService.FindAndNotifyAllLabelledIssues(state);
            return state.Save();
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
                var diff = (int)Math.Min(int.MaxValue, (result - now).Ticks / 2);
                result = result.AddTicks(_random.Next(diff));
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
