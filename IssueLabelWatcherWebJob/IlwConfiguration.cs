using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace IssueLabelWatcherWebJob
{
    public interface ITargetRepo
    {
        string FullName { get; }
        string Owner { get; }
        string Name { get; }
        string[] TargetLabels { get; }
        bool WatchPinnedIssues { get; }
        bool WatchPullRequests { get; }
    }

    public interface IIlwConfiguration
    {
        uint ChunkSize { get; }
        bool EnableFindAllLabelledIssues { get; }
        string FindAllLabelledIssuesTiming { get; }
        string FindRecentLabelledIssuesTiming { get; }
        string GithubPersonalAccessToken { get; }
        uint LabelChunkSize { get; }
        bool RandomlyDelayFindRecentLabelledIssues { get; }
        ITargetRepo[] Repos { get; }
        string SmtpServer { get; }
        int? SmtpPort { get; }
        string SmtpFrom { get; }
        string SmtpTo { get; }
        string SmtpUsername { get; }
        string SmtpPassword { get; }
        string StorageAccountConnectionString { get; }
    }

    public class TargetRepo : ITargetRepo
    {
        public string FullName { get; set; }
        public string Owner { get; set; }
        public string Name { get; set; }
        public string[] TargetLabels { get; set; }
        public bool WatchPinnedIssues { get; set; }
        public bool WatchPullRequests { get; set; }

        public TargetRepo(string fullName, string owner, string name, string[] targetLabels, bool watchPinnedIssues, bool watchPullRequests)
        {
            this.FullName = fullName;
            this.Owner = owner;
            this.Name = name;
            this.TargetLabels = targetLabels;
            this.WatchPinnedIssues = watchPinnedIssues;
            this.WatchPullRequests = watchPullRequests;
        }
    }

    public class IlwConfiguration : IIlwConfiguration
    {
        public const string ChunkSizeKey = "ilw:ChunkSize";
        public const string EnableFindAllLabelledIssuesKey = "ilw:EnableFindAllLabelledIssues";
        public const string FindAllLabelledIssuesTimingKey = "ilw:FindAllLabelledIssuesTiming";
        public const string FindRecentLabelledIssuesTimingKey = "ilw:FindRecentLabelledIssuesTiming";
        public const string GithubPersonalAccessTokenKey = "ilw:GithubPersonalAccessToken";
        public const string LabelChunkSizeKey = "ilw:LabelChunkSize";
        public const string RandomlyDelayFindRecentLabelledIssuesKey = "ilw:RandomlyDelayFindRecentLabelledIssues";
        public const string ReposKey = "ilw:Repos";
        public const string RepoLabelsKeyFormat = "ilw:Repo:{0}:Labels";
        public const string RepoWatchPinnedLabelsKeyFormat = "ilw:Repo:{0}:WatchPinnedIssues";
        public const string RepoWatchPullRequestsKeyFormat = "ilw:Repo:{0}:WatchPullRequests";
        public const string SmtpServerKey = "ilw:SmtpServer";
        public const string SmtpPortKey = "ilw:SmtpPort";
        public const string SmtpFromKey = "ilw:SmtpFrom";
        public const string SmtpToKey = "ilw:SmtpTo";
        public const string SmtpUsernameKey = "ilw:SmtpUsername";
        public const string SmtpPasswordKey = "ilw:SmtpPassword";
        public const string StorageAccountConnectionStringKey = "AzureWebJobsStorage";

        private readonly ILogger _logger;

        public IlwConfiguration(IConfiguration configuration, ILogger<IlwConfiguration> logger)
        {
            _logger = logger;

            this.ChunkSize = configuration.GetValue<uint>(ChunkSizeKey, 10);
            this.EnableFindAllLabelledIssues = configuration.GetValue<bool?>(EnableFindAllLabelledIssuesKey) == true;
            this.FindAllLabelledIssuesTiming = configuration.GetValue(FindAllLabelledIssuesTimingKey, "0 0 0 * * *");
            this.FindRecentLabelledIssuesTiming = configuration.GetValue(FindRecentLabelledIssuesTimingKey, "00:15:00");
            this.GithubPersonalAccessToken = configuration.GetValue<string>(GithubPersonalAccessTokenKey);
            this.LabelChunkSize = configuration.GetValue<uint>(LabelChunkSizeKey, 10);
            this.RandomlyDelayFindRecentLabelledIssues = configuration.GetValue<bool?>(RandomlyDelayFindRecentLabelledIssuesKey) == true;
            this.SmtpServer = configuration.GetValue<string>(SmtpServerKey);
            this.SmtpPort = configuration.GetValue<int?>(SmtpPortKey);
            this.SmtpFrom = configuration.GetValue<string>(SmtpFromKey);
            this.SmtpTo = configuration.GetValue<string>(SmtpToKey);
            this.SmtpUsername = configuration.GetValue<string>(SmtpUsernameKey);
            this.SmtpPassword = configuration.GetValue<string>(SmtpPasswordKey);
            this.StorageAccountConnectionString = configuration.GetConnectionStringOrSetting(StorageAccountConnectionStringKey);

            var repos = new List<TargetRepo>();
            var repoStrings = configuration.GetValue<string>(ReposKey)
                                          ?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                          ?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (repoStrings != null)
            {
                foreach (var repoString in repoStrings)
                {
                    var repoTokens = repoString.Split(new char[] { '/' }, 2);
                    if (repoTokens.Length < 2) continue;

                    var labels = configuration.GetValue<string>(string.Format(RepoLabelsKeyFormat, repoString))
                                             ?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                             ?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var watchPinnedIssues = configuration.GetValue<bool?>(string.Format(RepoWatchPinnedLabelsKeyFormat, repoString));
                    var watchPullRequests = configuration.GetValue<bool?>(string.Format(RepoWatchPullRequestsKeyFormat, repoString));
                    repos.Add(new TargetRepo
                    (
                        repoString,
                        repoTokens[0],
                        repoTokens[1],
                        labels?.ToArray() ?? new string[0],
                        watchPinnedIssues.HasValue && watchPinnedIssues.Value,
                        watchPullRequests.HasValue && watchPullRequests.Value
                    ));
                }
            }
            this.Repos = repos.ToArray();

            PrintConfiguration();
        }

        private void PrintConfiguration()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Repositories: {this.Repos.Length}");
            foreach (var repo in this.Repos)
            {
                sb.AppendLine($"    {repo.Owner}/{repo.Name}: {repo.TargetLabels.Length}");
                foreach (var label in repo.TargetLabels)
                {
                    sb.AppendLine($"        {label}");
                }
            }
            _logger.LogInformation(sb.ToString());
        }

        public uint ChunkSize { get; }
        public bool EnableFindAllLabelledIssues { get; }
        public string FindAllLabelledIssuesTiming { get; }
        public string FindRecentLabelledIssuesTiming { get; }
        public string GithubPersonalAccessToken { get; }
        public uint LabelChunkSize { get; }
        public bool RandomlyDelayFindRecentLabelledIssues { get; }
        public ITargetRepo[] Repos { get; }
        public string SmtpServer { get; }
        public int? SmtpPort { get; }
        public string SmtpFrom { get; }
        public string SmtpTo { get; }
        public string SmtpUsername { get; }
        public string SmtpPassword { get; }
        public string StorageAccountConnectionString { get; }
    }
}
