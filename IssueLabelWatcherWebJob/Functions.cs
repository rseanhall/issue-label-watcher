using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;

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
        public async Task FindRecentLabelledIssues([TimerTrigger(typeof(FindRecentLabelledIssuesTiming), UseMonitor = false)] TimerInfo timerInfo)
        {
            var state = await _ilwStateService.LoadIssues();
            await _ilwService.FindAndNotifyRecentLabelledIssues(state);
            await _ilwStateService.SaveIssues(state);
        }

        [Disable(typeof(DisableFindAllLabelledIssues))]
        [Singleton(SingletonScopeName, SingletonScope.Host)]
        public async Task FindAllLabelledIssues([TimerTrigger("%" + IlwConfiguration.FindAllLabelledIssuesTimingKey + "%", UseMonitor = false)] TimerInfo timerInfo)
        {
            var state = await _ilwStateService.LoadIssues();
            await _ilwService.FindAndNotifyAllLabelledIssues(state);
            await _ilwStateService.SaveIssues(state);
        }
    }
}
