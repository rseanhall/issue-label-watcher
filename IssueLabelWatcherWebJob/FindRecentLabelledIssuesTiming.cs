using System;
using Microsoft.Azure.WebJobs.Extensions.Timers;
using NCrontab;

namespace IssueLabelWatcherWebJob
{
    public class FindRecentLabelledIssuesTiming : TimerSchedule
    {
        private bool _firstOccurence = true; 
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
            // Want to run immediately, but only after all initialization has happened.
            if (_firstOccurence)
            {
                _firstOccurence = false;
                return now;
            }

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
