using System.Reflection;

namespace IssueLabelWatcherWebJob
{
    public class DisableFindAllLabelledIssues
    {
        private readonly IIlwConfiguration _configuration;

        public DisableFindAllLabelledIssues(IIlwConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool IsDisabled(MethodInfo methodInfo)
        {
            return !_configuration.EnableFindAllLabelledIssues;
        }
    }
}
