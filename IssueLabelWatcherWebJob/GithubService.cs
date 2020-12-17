using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
            this.Name = name;
            this.PRAlias = $"pr_{alias}";
            this.PRAfterVariableName = $"after_{repoAlias}_{this.PRAlias}";
            this.PRIncludeVariableName = $"include_{repoAlias}_{this.PRAlias}";
        }

        public string Alias { get; set; }
        public string AfterVariableName { get; set; }
        public string IncludeVariableName { get; set; }
        public string Name { get; set; }
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

    public class GithubLabelList
    {
        public GithubLabelList(string repoAlias)
        {
            this.AfterVariableName = $"labels_after_{repoAlias}";
            this.IncludeVariableName = $"labels_include_{repoAlias}";
            this.LabelByName = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
        }

        public string AfterVariableName { get; set; }
        public string IncludeVariableName { get; set; }
        public Dictionary<string, List<string>> LabelByName { get; set; }
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

        private async Task<List<GithubIssueListByRepo>> PrepareRepoList()
        {
            var repoList = new List<GithubIssueListByRepo>();
            var labelListByRepo = new Dictionary<string, GithubLabelList>();

            var sb = new StringBuilder();
            int i = 1;
            JObject variables = new JObject();
            var prefix = "query IssuesWithLabel($dryRun:Boolean!, $chunkSize:Int!";
            var chunkSize = 100u;
            var newVariableIndex = prefix.Length;
            sb.Append(prefix);
            sb.AppendLine(") {");
            RateLimit.AppendQuery(sb, i);

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

                var labelList = new GithubLabelList(repoAlias);
                labelListByRepo.Add(targetRepo.FullName, labelList);
                foreach (var targetLabel in targetRepo.TargetLabels)
                {
                    labelList.LabelByName.Add(targetLabel, new List<string>());
                }

                sb.Insert(newVariableIndex, $", ${labelList.AfterVariableName}:String, ${labelList.IncludeVariableName}:Boolean!");
                variables[labelList.AfterVariableName] = null;
                variables[labelList.IncludeVariableName] = true;

                AppendIndentedLine(sb, i++, string.Format("{0}: repository(owner:\"{1}\", name:\"{2}\") @include(if:${3}) {{",
                                                          repo.RepoAlias, targetRepo.Owner, targetRepo.Name, labelList.IncludeVariableName));
                AppendIndentedLine(sb, i++, string.Format("labels(first:$chunkSize, after:${0}) {{", labelList.AfterVariableName));
                AppendIndentedLine(sb, i, "...labelConnectionFields");
                AppendIndentedLine(sb, --i, "}"); //labels
                AppendIndentedLine(sb, --i, "}"); //repository
            }

            sb.AppendLine("}"); //query
            sb.AppendLine();
            sb.AppendLine("fragment labelFields on Label {");
            AppendIndentedLine(sb, i, "name");
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment labelConnectionFields on LabelConnection {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "...labelFields");
            AppendIndentedLine(sb, --i, "}"); //nodes
            AppendIndentedLine(sb, i++, "pageInfo {");
            AppendIndentedLine(sb, i, "endCursor");
            AppendIndentedLine(sb, i, "hasNextPage");
            AppendIndentedLine(sb, --i, "}"); //pageInfo
            sb.AppendLine("}"); //fragment

            var query = sb.ToString();
            _logger.LogDebug(query);

            variables["dryRun"] = true;
            variables["chunkSize"] = chunkSize;
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
                GraphQLResponse<JObject> labelsRequest;
                try
                {
                    labelsRequest = await _graphqlGithubClient.SendQueryAsync<JObject>(new GraphQLRequest
                    {
                        Query = query,
                        Variables = variables,
                    });
                }
                catch (GraphQLHttpRequestException e) when (chunkSize > 5 &&
                    (e.StatusCode == HttpStatusCode.BadGateway || e.StatusCode == HttpStatusCode.GatewayTimeout))
                {
                    chunkSize -= 5;
                    variables["chunkSize"] = chunkSize;
                    hasMorePages = true;
                    _logger.LogWarning("Labels query seems to have timed out ({StatusCode}), trying with smaller chunkSize ({ChunkSize})...", e.StatusCode, chunkSize);
                    Thread.Sleep(10000);
                    continue;
                }

                if (this.CheckForErrors(labelsRequest))
                {
                    break;
                }

                var rateLimitObject = labelsRequest.Data["rateLimit"];
                rateLimit = rateLimitObject.ToObject<RateLimit>();
                _logger.LogDebug("Rate limit {RateLimit}", rateLimitObject.ToString());

                foreach (var repo in repoList)
                {
                    var repoObject = labelsRequest.Data[repo.RepoAlias];
                    if (repoObject == null)
                    {
                        continue;
                    }

                    var labelList = labelListByRepo[repo.Repo.FullName];

                    var labelListResult = repoObject["labels"]?.ToObject<GraphQLLabelListResult>();
                    if (labelListResult != null)
                    {
                        variables[labelList.AfterVariableName] = labelListResult.PageInfo.EndCursor;
                        variables[labelList.IncludeVariableName] = labelListResult.PageInfo.HasNextPage;
                        hasMorePages |= labelListResult.PageInfo.HasNextPage;

                        foreach (var node in labelListResult.Nodes)
                        {
                            if (labelList.LabelByName.TryGetValue(node.Name, out var labels))
                            {
                                labels.Add(node.Name);
                            }
                        }
                    }
                }
            }

            foreach (var repo in repoList)
            {
                var labelList = labelListByRepo[repo.Repo.FullName];

                foreach (var targetLabel in repo.Repo.TargetLabels)
                {
                    var labels = labelList.LabelByName[targetLabel];

                    if (labels.Count == 0)
                    {
                        _logger.LogError("Label '{LabelName}' in repo '{Repo}' does not exist.", targetLabel, repo.Repo.FullName);
                        continue;
                    }

                    // Does GitHub even allow labels that only differ by case?
                    var actualLabelName = labels.FirstOrDefault(n => n == targetLabel);
                    if (actualLabelName == null)
                    {
                        actualLabelName = labels[0];
                        _logger.LogWarning("Label '{LabelName}' in repo '{Repo}' is wrong case, using '{NewLabelName}' ('{NewLabelChoices}')",
                                           targetLabel, repo.Repo.FullName, actualLabelName, string.Join("','", labels));
                    }

                    var label = new GithubIssueListLabel(actualLabelName, repo.RepoAlias)
                    {
                        WatchPullRequests = repo.Repo.WatchPullRequests,
                    };
                    repo.Labels.Add(label);
                }
            }

            return repoList;
        }

        public async Task<IGithubIssuesByRepo[]> GetRecentIssuesWithLabel(TimeSpan? timeFromNow)
        {
            var repoList = await this.PrepareRepoList();
            if (repoList == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            int i = 1;
            JObject variables = new JObject();
            var prefix = "query IssuesWithLabel($dryRun:Boolean!, $since:DateTime, $chunkSize:Int!";
            var since = timeFromNow.HasValue ? DateTime.UtcNow - timeFromNow : null;
            var chunkSize = Math.Min(_configuration.ChunkSize, 100u);
            var newVariableIndex = prefix.Length;
            sb.Append(prefix);
            sb.AppendLine(") {");
            RateLimit.AppendQuery(sb, i);

            foreach (var repo in repoList)
            {
                var targetRepo = repo.Repo;

                AppendIndentedLine(sb, i++, string.Format("{0}: repository(owner:\"{1}\", name:\"{2}\") {{", repo.RepoAlias, targetRepo.Owner, targetRepo.Name));

                if (repo.WatchPinned.Enabled)
                {
                    sb.Insert(newVariableIndex, $", ${repo.WatchPinned.AfterVariableName}:String, ${repo.WatchPinned.IncludeVariableName}:Boolean!");
                    variables[repo.WatchPinned.AfterVariableName] = null;
                    variables[repo.WatchPinned.IncludeVariableName] = true;

                    AppendIndentedLine(sb, i++, string.Format("pinnedIssues(first:$chunkSize, after:${0}) @include(if:${1}) {{", repo.WatchPinned.AfterVariableName, repo.WatchPinned.IncludeVariableName));
                    AppendIndentedLine(sb, i++, "...pinnedIssueConnectionFields");
                    AppendIndentedLine(sb, --i, "}"); //pinnedIssues
                }

                foreach (var label in repo.Labels)
                {
                    sb.Insert(newVariableIndex, $", ${label.AfterVariableName}:String, ${label.IncludeVariableName}:Boolean!");
                    variables[label.AfterVariableName] = null;
                    variables[label.IncludeVariableName] = true;

                    var issueFilter = $"{{ labels:[\"{label.Name}\"], states: [OPEN, CLOSED], since: $since }}";
                    AppendIndentedLine(sb, i++, string.Format("{0}: issues(filterBy:{1}, after:${2}, first:$chunkSize, orderBy: {3}) @include(if:${4}) {{",
                        label.Alias, issueFilter, label.AfterVariableName, "{ field:UPDATED_AT, direction:DESC }", label.IncludeVariableName));
                    AppendIndentedLine(sb, i, "...issueConnectionFields");
                    AppendIndentedLine(sb, --i, "}"); //issues

                    if (label.WatchPullRequests)
                    {

                        sb.Insert(newVariableIndex, $", ${label.PRAfterVariableName}:String, ${label.PRIncludeVariableName}:Boolean!");
                        variables[label.PRAfterVariableName] = null;
                        variables[label.PRIncludeVariableName] = true;

                        var prFilter = $"labels:[\"{label.Name}\"], states: [OPEN, CLOSED, MERGED]";
                        AppendIndentedLine(sb, i++, string.Format("{0}: pullRequests({1}, after:${2}, first:$chunkSize, orderBy: {3}) @include(if:${4}) {{",
                            label.PRAlias, prFilter, label.PRAfterVariableName, "{ field:UPDATED_AT, direction:DESC }", label.PRIncludeVariableName));
                        AppendIndentedLine(sb, i, "...prConnectionFields");
                        AppendIndentedLine(sb, --i, "}"); //pullRequests
                    }
                }
                AppendIndentedLine(sb, --i, "}"); //repository
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
            sb.AppendLine("fragment issueConnectionFields on IssueConnection {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "__typename");
            AppendIndentedLine(sb, i, "...issueFields");
            AppendIndentedLine(sb, --i, "}"); //nodes
            AppendIndentedLine(sb, i++, "pageInfo {");
            AppendIndentedLine(sb, i, "endCursor");
            AppendIndentedLine(sb, i, "hasNextPage");
            AppendIndentedLine(sb, --i, "}"); //pageInfo
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment pinnedIssueConnectionFields on PinnedIssueConnection {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i++, "issue {");
            AppendIndentedLine(sb, i, "__typename");
            AppendIndentedLine(sb, i, "...issueFields");
            AppendIndentedLine(sb, --i, "}"); //issue
            AppendIndentedLine(sb, --i, "}"); //nodes
            AppendIndentedLine(sb, i++, "pageInfo {");
            AppendIndentedLine(sb, i, "endCursor");
            AppendIndentedLine(sb, i, "hasNextPage");
            AppendIndentedLine(sb, --i, "}"); //pageInfo
            sb.AppendLine("}"); //fragment
            sb.AppendLine("fragment prConnectionFields on PullRequestConnection {");
            AppendIndentedLine(sb, i++, "nodes {");
            AppendIndentedLine(sb, i, "__typename");
            AppendIndentedLine(sb, i, "...prFields");
            AppendIndentedLine(sb, --i, "}"); //nodes
            AppendIndentedLine(sb, i++, "pageInfo {");
            AppendIndentedLine(sb, i, "endCursor");
            AppendIndentedLine(sb, i, "hasNextPage");
            AppendIndentedLine(sb, --i, "}"); //pageInfo
            sb.AppendLine("}"); //fragment

            var query = sb.ToString();
            _logger.LogDebug(query);

            variables["dryRun"] = true;
            variables["since"] = since;
            variables["chunkSize"] = chunkSize;
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
                GraphQLResponse<JObject> recentIssueRequest;
                try
                {
                    recentIssueRequest = await _graphqlGithubClient.SendQueryAsync<JObject>(new GraphQLRequest
                    {
                        Query = query,
                        Variables = variables,
                    });
                }
                catch (GraphQLHttpRequestException e) when (chunkSize > 5 &&
                    (e.StatusCode == HttpStatusCode.BadGateway || e.StatusCode == HttpStatusCode.GatewayTimeout))
                {
                    chunkSize -= 5;
                    variables["chunkSize"] = chunkSize;
                    hasMorePages = true;
                    _logger.LogWarning("Issues query seems to have timed out ({StatusCode}), trying with smaller chunkSize ({ChunkSize})...", e.StatusCode, chunkSize);
                    Thread.Sleep(10000);
                    continue;
                }

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
                    if (repoObject == null)
                    {
                        continue;
                    }

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

                    foreach (var labelAlias in repo.Labels)
                    {
                        var labelResult = repoObject[labelAlias.Alias]?.ToObject<GraphQLLabelResult>();
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

                        var prResult = repoObject[labelAlias.PRAlias]?.ToObject<GraphQLLabelResult>();
                        if (prResult != null)
                        {
                            variables[labelAlias.PRAfterVariableName] = prResult.PageInfo.EndCursor;
                            var prHasMorePages = prResult.PageInfo.HasNextPage;

                            foreach (var issue in prResult.Nodes)
                            {
                                // Workaround the lack of filtering for pull requests.
                                if (!since.HasValue || issue.UpdatedAt > since)
                                {
                                    ProcessIssue(issue, repo, "PR");
                                }
                                else
                                {
                                    prHasMorePages = false;
                                    break;
                                }
                            }

                            variables[labelAlias.PRIncludeVariableName] = prHasMorePages;
                            hasMorePages |= prHasMorePages;
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

            public static void AppendQuery(StringBuilder sb, int i)
            {
                AppendIndentedLine(sb, i++, "rateLimit(dryRun: $dryRun) {");
                AppendIndentedLine(sb, i, "cost");
                AppendIndentedLine(sb, i, "limit");
                AppendIndentedLine(sb, i, "nodeCount");
                AppendIndentedLine(sb, i, "remaining");
                AppendIndentedLine(sb, i, "resetAt");
                AppendIndentedLine(sb, i, "used");
                AppendIndentedLine(sb, --i, "}"); //rateLimit
            }
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

        public class GraphQLLabelListResult
        {
            public GraphQLIssueLabel[] Nodes { get; set; }
            public GraphQLPageInfo PageInfo { get; set; }
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
