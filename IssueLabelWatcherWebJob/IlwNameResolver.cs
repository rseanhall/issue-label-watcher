using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;

namespace IssueLabelWatcherWebJob
{
    public class IlwNameResolver : INameResolver
    {
        private readonly IConfiguration _configuration;
        private readonly IIlwConfiguration _ilwConfiguration;

        public IlwNameResolver(IConfiguration configuration, IIlwConfiguration ilwConfiguration)
        {
            _configuration = configuration;
            _ilwConfiguration = ilwConfiguration;
        }

        public string Resolve(string name)
        {
            switch (name)
            {
                case IlwConfiguration.FindAllLabelledIssuesTimingKey:
                    return _ilwConfiguration.FindAllLabelledIssuesTiming;
                case IlwConfiguration.FindRecentLabelledIssuesTimingKey:
                    return _ilwConfiguration.FindRecentLabelledIssuesTiming;
                default:
                    return _configuration.GetValue<string>(name);
            }
        }
    }
}
