using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IssueLabelWatcherWebJob
{
    public interface IGithubIssue
    {
        ITargetRepo Repo { get; }
        bool IsAlreadyViewed { get; set; }
        string IssueType { get; }
        string[] Labels { get; }
        string Number { get; }
        string Status { get; }
        string Title { get; }
        DateTime UpdatedAt { get; }
        string Url { get; }
    }

    public interface IGithubIssuesByRepo
    {
        ITargetRepo Repo { get; }
        IGithubIssue[] Issues { get; }
    }

    public interface IGithubService
    {
        Task<IGithubIssuesByRepo[]> GetRecentIssuesWithLabel(TimeSpan? timeFromNow);
    }

    public class GithubIssue : IGithubIssue
    {
        public ITargetRepo Repo { get; set; }
        public bool IsAlreadyViewed { get; set; }
        public string IssueType { get; set; }
        public string[] Labels { get; set; }
        public string Number { get; set; }
        public string Status { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Url { get; set; }
    }

    public class GithubIssuesByRepo : IGithubIssuesByRepo
    {
        public ITargetRepo Repo { get; set; }
        public IGithubIssue[] Issues { get; set; }
    }

    public class GithubIssueListByRepo
    {
        public ITargetRepo Repo { get; set; }
        public string RepoAlias { get; set; }
        public List<GithubIssueListLabel> Labels { get; set; }
        public List<GithubIssue> Issues { get; set; }
        public GithubIssueListWatchPinned WatchPinned { get; set; }
    }

    public class GithubIssueListLabel
    {
        public GithubIssueListLabel(string name, string repoAlias)
        {
            var alias = GithubService.GetGraphQLAlias(name);
            this.Alias = $"issue_{alias}";
            this.AfterVariableName = $"after_{repoAlias}_{this.Alias}";
            this.IncludeVariableName = $"include_{repoAlias}_{this.Alias}";
            this.PRAlias = $"pr_{alias}";
            this.PRAfterVariableName = $"after_{repoAlias}_{this.PRAlias}";
            this.PRIncludeVariableName = $"include_{repoAlias}_{this.PRAlias}";
        }

        public string Alias { get; set; }
        public string AfterVariableName { get; set; }
        public string IncludeVariableName { get; set; }
        public string PRAlias { get; set; }
        public string PRAfterVariableName { get; set; }
        public string PRIncludeVariableName { get; set; }
        public bool WatchPullRequests { get; set; }
    }

    public class GithubIssueListWatchPinned
    {
        public GithubIssueListWatchPinned(bool enabled, string repoAlias)
        {
            this.Enabled = enabled;
            this.AfterVariableName = $"pinned_after_{repoAlias}";
            this.IncludeVariableName = $"pinned_include_{repoAlias}";
        }

        public bool Enabled { get; set; }
        public string AfterVariableName { get; set; }
        public string IncludeVariableName { get; set; }
    }

    public class GithubService : IGithubService
    {
        private readonly IIlwConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly GraphQLHttpClient _graphqlGithubClient;

        public GithubService(IIlwConfiguration configuration, ILogger<GithubService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            _graphqlGithubClient = new GraphQLHttpClient("https://api.github.com/graphql", new NewtonsoftJsonSerializer());
            _graphqlGithubClient.HttpClient.DefaultRequestHeaders.Add("Authorization", $"bearer {_configuration.GithubPersonalAccessToken}");
            _graphqlGithubClient.HttpClient.DefaultRequestHeaders.Add("User-Agent", $"issue-label-watcher-{ThisAssembly.AssemblyInformationalVersion}");
            _graphqlGithubClient.HttpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.elektra-preview+json");
        }

        private bool CanMakeGraphQLRequests(RateLimit rateLimit)
        {
            var result = rateLimit.Cost < rateLimit.Remaining;

            if (!result)
            {
                _logger.LogWarning("Rate limit reached {RateLimit}", JObject.FromObject(rateLimit).ToString());
            }

            return result;
        }

        private bool CheckForErrors(IGraphQLResponse response)
        {
            if (response.Errors == null || response.Errors.Length == 0)
            {
                return false;
            }

            _logger.LogError(string.Join(Environment.NewLine, response.Errors.Select(e => JObject.FromObject(e).ToString())));
            return true;
        }

        public async Task<IGithubIssuesByRepo[]> GetRecentIssuesWithLabel(TimeSpan? timeFromNow)
        {
            var repoList = new List<GithubIssueListByRepo>();

            var sb = new StringBuilder();
            int i = 1;
            JObject variables = new JObject();
            var prefix = "query IssuesWithLabel($dryRun:Boolean!";
            var since = timeFromNow.HasValue ? (DateTime.UtcNow - timeFromNow.Value).ToString("o") : null;
            var newVariableIndex = prefix.Length;
            sb.Append(prefix);
            sb.AppendLine(") {");
            AppendIndentedLine(sb, i++, "rateLimit(dryRun: $dryRun) {");
            AppendIndentedLine(sb, i, "cost");
            AppendIndentedLine(sb, i, "limit");
            AppendIndentedLine(sb, i, "nodeCount");
            AppendIndentedLine(sb, i, "remaining");
            AppendIndentedLine(sb, i, "resetAt");
            AppendIndentedLine(sb, i, "used");
            AppendIndentedLine(sb, --i, "}"); //rateLimit
            foreach (var targetRepo in _configuration.Repos)
            {
                var repoAlias = GetGraphQLAlias($"{targetRepo.Owner}_{targetRepo.Name}");
                var repo = new GithubIssueListByRepo
                {
                    Issues = new List<GithubIssue>(),
                    Labels = new List<GithubIssueListLabel>(),
                    Repo = targetRepo,
                    RepoAlias = repoAlias,
                    WatchPinned = new GithubIssueListWatchPinned(targetRepo.WatchPinnedIssues, repoAlias),
                };
                repoList.Add(repo);

                if (repo.WatchPinned.Enabled)
                {
                    sb.Insert(newVariableIndex, $", ${repo.WatchPinned.AfterVariableName}:String, ${repo.WatchPinned.IncludeVariableName}:Boolean!");
                    variables[repo.WatchPinned.AfterVariableName] = null;
                    variables[repo.WatchPinned.IncludeVariableName] = true;

                    AppendIndentedLine(sb, i++, string.Format("{0}: repository(owner:\"{1}\", name:\"{2}\") {{", repo.RepoAlias, targetRepo.Owner, targetRepo.Name));
                    AppendIndentedLine(sb, i++, string.Format("pinnedIssues(first:100, after:${0}) @include(if:${1}) {{", repo.WatchPinned.AfterVariableName, repo.WatchPinned.IncludeVariableName));
                    AppendIndentedLine(sb, i++, "nodes {");
                    AppendIndentedLine(sb, i++, "issue {");
                    AppendIndentedLine(sb, i, "...issueFields");
                    AppendIndentedLine(sb, --i, "}"); //pinnedIssues/nodes/issue
                    AppendIndentedLine(sb, --i, "}"); //pinnedIssues/nodes
                    AppendIndentedLine(sb, i++, "pageInfo {");
                    AppendIndentedLine(sb, i, "endCursor");
                    AppendIndentedLine(sb, i, "hasNextPage");
                    AppendIndentedLine(sb, --i, "}"); //pinnedIssues/pageInfo
                    AppendIndentedLine(sb, --i, "}"); //pinnedIssues
                    AppendIndentedLine(sb, --i, "}"); //repository
                }

                foreach (var targetLabel in targetRepo.TargetLabels)
                {
                    var label = new GithubIssueListLabel(targetLabel, repo.RepoAlias)
                    {
                        WatchPullRequests = targetRepo.WatchPullRequests,
                    };
                    repo.Labels.Add(label);

                    sb.Insert(newVariableIndex, $", ${label.AfterVariableName}:String, ${label.IncludeVariableName}:Boolean!");
                    variables[label.AfterVariableName] = null;
                    variables[label.IncludeVariableName] = true;

                    var baseQuery = $"repo:\\\"{targetRepo.FullName}\\\" label:\\\"{targetLabel}\\\" updated:>{since} sort:updated-desc";
                    AppendIndentedLine(sb, i++, string.Format("{0}: search(type:ISSUE, query:\"is:issue {1}\", after:${2}, first:100) @include(if:${3}) {{",
                        label.Alias, baseQuery, label.AfterVariableName, label.IncludeVariableName));
                    AppendIndentedLine(sb, i, "...searchFields");
                    AppendIndentedLine(sb, --i, "}"); //search

                    if (label.WatchPullRequests)
                    {

                        sb.Insert(newVariableIndex, $", ${label.PRAfterVariableName}:String, ${label.PRIncludeVariableName}:Boolean!");
                        variables[label.PRAfterVariableName] = null;
                        variables[label.PRIncludeVariableName] = true;

                        AppendIndentedLine(sb, i++, string.Format("{0}: search(type:ISSUE, query:\"is:pr {1}\", after:${2}, first:100) @include(if:${3}) {{",
                            label.PRAlias, baseQuery, label.PRAfterVariableName, label.PRIncludeVariableName));
                        AppendIndentedLine(sb, i, "...searchFields");
                        AppendIndentedLine(sb, --i, "}"); //search
                    }
                }
            }
            sb.AppendLine("}"); //query
            sb.AppendLine();
            sb.AppendLine("fragment issueFields on Issue {");
            AppendIndentedLine(sb, i, "number");
            AppendIndentedLine(sb, i, "issueState: state");
            AppendIndentedLine(sb, i, "title");
            AppendIndentedLine(sb, i, "updatedAt");
            AppendIndentedLine(sb, i, "url");
            AppendIndentedLine(sb, i, "viewerSubscription");
            AppendIndentedLine(sb, i++, "labels(first:100) {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "name");
            AppendIndentedLine(sb, --i, "}"); //labels/nodes
            AppendIndentedLine(sb, --i, "}"); //labels
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment prFields on PullRequest {");
            AppendIndentedLine(sb, i, "number");
            AppendIndentedLine(sb, i, "pRState: state");
            AppendIndentedLine(sb, i, "title");
            AppendIndentedLine(sb, i, "updatedAt");
            AppendIndentedLine(sb, i, "url");
            AppendIndentedLine(sb, i, "viewerSubscription");
            AppendIndentedLine(sb, i++, "labels(first:100) {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "name");
            AppendIndentedLine(sb, --i, "}"); //labels/nodes
            AppendIndentedLine(sb, --i, "}"); //labels
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment srFields on SearchResultItem {");
            AppendIndentedLine(sb, i, "typeName: __typename");
            AppendIndentedLine(sb, i, "...issueFields");
            AppendIndentedLine(sb, i, "...prFields");
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment searchFields on SearchResultItemConnection {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "...srFields");
            AppendIndentedLine(sb, --i, "}"); //nodes
            AppendIndentedLine(sb, i++, "pageInfo {");
            AppendIndentedLine(sb, i, "endCursor");
            AppendIndentedLine(sb, i, "hasNextPage");
            AppendIndentedLine(sb, --i, "}"); //pageInfo
            sb.AppendLine("}"); //fragment

            var query = sb.ToString();
            _logger.LogDebug(query);

            variables["dryRun"] = true;
            var rateLimitRequest = await _graphqlGithubClient.SendQueryAsync<RateLimitGraphQLRequest>(new GraphQLRequest
            {
                Query = query,
                Variables = variables,
            });

            if (this.CheckForErrors(rateLimitRequest))
            {
                return null;
            }

            var rateLimit = rateLimitRequest.Data.RateLimit;
            variables["dryRun"] = false;

            var hasMorePages = true;
            while (hasMorePages)
            {
                hasMorePages = false;

                if (!this.CanMakeGraphQLRequests(rateLimit))
                {
                    break;
                }

                _logger.LogDebug(variables.ToString());
                var recentIssueRequest = await _graphqlGithubClient.SendQueryAsync<JObject>(new GraphQLRequest
                {
                    Query = query,
                    Variables = variables,
                });

                if (this.CheckForErrors(recentIssueRequest))
                {
                    break;
                }

                var rateLimitObject = recentIssueRequest.Data["rateLimit"];
                rateLimit = rateLimitObject.ToObject<RateLimit>();
                _logger.LogDebug("Rate limit {RateLimit}", rateLimitObject.ToString());

                foreach (var repo in repoList)
                {
                    var repoObject = recentIssueRequest.Data[repo.RepoAlias];
                    if (repoObject != null)
                    {
                        var pinnedIssueResult = repoObject["pinnedIssues"]?.ToObject<GraphQLPinnedIssueResult>();
                        if (pinnedIssueResult != null)
                        {
                            variables[repo.WatchPinned.AfterVariableName] = pinnedIssueResult.PageInfo.EndCursor;
                            variables[repo.WatchPinned.IncludeVariableName] = pinnedIssueResult.PageInfo.HasNextPage;
                            hasMorePages |= pinnedIssueResult.PageInfo.HasNextPage;

                            foreach (var node in pinnedIssueResult.Nodes)
                            {
                                ProcessIssue(node.Issue, repo, "Pinned");
                            }
                        }
                    }

                    foreach (var labelAlias in repo.Labels)
                    {
                        var labelResult = recentIssueRequest.Data[labelAlias.Alias]?.ToObject<GraphQLLabelResult>();
                        if (labelResult != null)
                        {
                            variables[labelAlias.AfterVariableName] = labelResult.PageInfo.EndCursor;
                            variables[labelAlias.IncludeVariableName] = labelResult.PageInfo.HasNextPage;
                            hasMorePages |= labelResult.PageInfo.HasNextPage;

                            foreach (var issue in labelResult.Nodes)
                            {
                                ProcessIssue(issue, repo);
                            }
                        }

                        var prResult = recentIssueRequest.Data[labelAlias.PRAlias]?.ToObject<GraphQLLabelResult>();
                        if (prResult != null)
                        {
                            variables[labelAlias.PRAfterVariableName] = prResult.PageInfo.EndCursor;
                            variables[labelAlias.PRIncludeVariableName] = prResult.PageInfo.HasNextPage;
                            hasMorePages |= prResult.PageInfo.HasNextPage;

                            foreach (var issue in prResult.Nodes)
                            {
                                ProcessIssue(issue, repo, "PR");
                            }
                        }
                    }
                }
            }

            var results = repoList.Select(x => new GithubIssuesByRepo { Repo = x.Repo, Issues = x.Issues.ToArray() }).ToArray();
            return results;
        }

        private static void ProcessIssue(GraphQLIssueOrPullRequest issue, GithubIssueListByRepo repo, string issueType = "Issue")
        {
            var newIssue = new GithubIssue
            {
                IsAlreadyViewed = issue.ViewerSubscription != "UNSUBSCRIBED",
                IssueType = issueType,
                Labels = issue.Labels.Nodes.Select(n => n.Name).ToArray(),
                Number = issue.Number,
                Repo = repo.Repo,
                Status = issue.TypeName == "PullRequest" ? issue.PRState : issue.IssueState,
                Title = issue.Title,
                UpdatedAt = issue.UpdatedAt,
                Url = issue.Url,
            };

            repo.Issues.Add(newIssue);
        }

        private static void AppendIndentedLine(StringBuilder sb, int i, string s)
        {
            sb.AppendLine(new string(' ', i * 2) + s);
        }

        public static string GetGraphQLAlias(string s)
        {
            return Regex.Replace(s, "[^a-zA-Z0-9_]", "_");
        }

        public class RateLimitGraphQLRequest
        {
            public RateLimit RateLimit { get; set; }
        }

        public class RateLimit
        {
            public int Cost { get; set; }
            public int Limit { get; set; }
            public int NodeCount { get; set; }
            public int Remaining { get; set; }
            public string ResetAt { get; set; }
            public int Used { get; set; }
        }

        public class GraphQLLabelResult
        {
            public GraphQLIssueOrPullRequest[] Nodes { get; set; }
            public GraphQLPageInfo PageInfo { get; set; }
        }

        public class GraphQLPageInfo
        {
            public string EndCursor { get; set; }
            public bool HasNextPage { get; set; }
        }

        public class GraphQLIssueOrPullRequest
        {
            public string IssueState { get; set; }
            public GraphQLIssueLabelNodes Labels { get; set; }
            public string Number { get; set; }
            public string PRState { get; set; }
            public string Title { get; set; }
            public string TypeName { get; set; }
            public DateTime UpdatedAt { get; set; }
            public string Url { get; set; }
            public string ViewerSubscription { get; set; }
        }

        public class GraphQLIssueLabelNodes
        {
            public GraphQLIssueLabel[] Nodes { get; set; }
        }

        public class GraphQLIssueLabel
        {
            public string Name { get; set; }
        }

        public class GraphQLPinnedIssueResult
        {
            public GraphQLPinnedIssue[] Nodes { get; set; }
            public GraphQLPageInfo PageInfo { get; set; }
        }

        public class GraphQLPinnedIssue
        {
            public GraphQLIssueOrPullRequest Issue { get; set; }
        }
    }
}
