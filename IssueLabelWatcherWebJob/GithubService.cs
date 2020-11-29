using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
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
    }

    public class GithubIssueListLabel
    {
        public GithubIssueListLabel(string name, string repoAlias)
        {
            this.Alias = GithubService.GetGraphQLAlias(name);
            this.AfterVariableName = $"after_{repoAlias}_{this.Alias}";
            this.IncludeVariableName = $"include_{repoAlias}_{this.Alias}";
        }

        public string Alias { get; set; }
        public string AfterVariableName { get; set; }
        public string IncludeVariableName { get; set; }
    }

    public class GithubService : IGithubService
    {
        private readonly IIlwConfiguration _configuration;
        private readonly GraphQLHttpClient _graphqlGithubClient;

        public GithubService(IIlwConfiguration configuration)
        {
            _configuration = configuration;

            _graphqlGithubClient = new GraphQLHttpClient("https://api.github.com/graphql", new NewtonsoftJsonSerializer());
            _graphqlGithubClient.HttpClient.DefaultRequestHeaders.Add("Authorization", $"bearer {_configuration.GithubPersonalAccessToken}");
            _graphqlGithubClient.HttpClient.DefaultRequestHeaders.Add("User-Agent", $"issue-label-watcher-{ThisAssembly.AssemblyInformationalVersion}");
        }

        private bool CanMakeGraphQLRequests(RateLimit rateLimit)
        {
            return rateLimit.Cost < rateLimit.Remaining;
        }

        public async Task<IGithubIssuesByRepo[]> GetRecentIssuesWithLabel(TimeSpan? timeFromNow)
        {
            var repoList = new List<GithubIssueListByRepo>();

            var sb = new StringBuilder();
            int i = 1;
            JObject variables = new JObject();
            var prefix = "query IssuesWithLabel($dryRun:Boolean!, $since:DateTime";
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
                var repo = new GithubIssueListByRepo
                {
                    Issues = new List<GithubIssue>(),
                    Labels = new List<GithubIssueListLabel>(),
                    Repo = targetRepo,
                    RepoAlias = GetGraphQLAlias($"{targetRepo.Owner}_{targetRepo.Name}"),
                };
                repoList.Add(repo);

                AppendIndentedLine(sb, i++, string.Format("{0}: repository(owner:\"{1}\", name:\"{2}\") {{", repo.RepoAlias, targetRepo.Owner, targetRepo.Name));

                foreach (var targetLabel in targetRepo.TargetLabels)
                {
                    var label = new GithubIssueListLabel(targetLabel, repo.RepoAlias);
                    repo.Labels.Add(label);

                    sb.Insert(newVariableIndex, $", ${label.AfterVariableName}:String, ${label.IncludeVariableName}:Boolean!");
                    variables[label.AfterVariableName] = null;
                    variables[label.IncludeVariableName] = true;

                    var issueFilter = $"{{ labels:[\"{targetLabel}\"], states: [OPEN, CLOSED], since: $since }}";
                    AppendIndentedLine(sb, i++, string.Format("{0}: issues(filterBy:{1}, after:${2}, first:100, orderBy: {3}) @include(if:${4}) {{",
                        label.Alias, issueFilter, label.AfterVariableName, "{ field:UPDATED_AT, direction:DESC }", label.IncludeVariableName));
                    AppendIndentedLine(sb, i++, "nodes {");
                    AppendIndentedLine(sb, i, "number");
                    AppendIndentedLine(sb, i, "state");
                    AppendIndentedLine(sb, i, "title");
                    AppendIndentedLine(sb, i, "updatedAt");
                    AppendIndentedLine(sb, i, "url");
                    AppendIndentedLine(sb, i, "viewerSubscription");
                    AppendIndentedLine(sb, i++, "labels(first:100) {");
                    AppendIndentedLine(sb, i++, "nodes {");
                    AppendIndentedLine(sb, i, "name");
                    AppendIndentedLine(sb, --i, "}"); //issues/nodes/labels/nodes
                    AppendIndentedLine(sb, --i, "}"); //issues/nodes/labels
                    AppendIndentedLine(sb, --i, "}"); //issues/nodes
                    AppendIndentedLine(sb, i++, "pageInfo {");
                    AppendIndentedLine(sb, i, "endCursor");
                    AppendIndentedLine(sb, i, "hasNextPage");
                    AppendIndentedLine(sb, --i, "}"); //issues/pageInfo
                    AppendIndentedLine(sb, --i, "}"); //issues
                }
                AppendIndentedLine(sb, --i, "}"); //repository
            }
            sb.AppendLine("}"); //query

            var query = sb.ToString();
            variables["dryRun"] = true;
            variables["since"] = timeFromNow.HasValue ? DateTime.UtcNow - timeFromNow : null;
            var rateLimitRequest = await _graphqlGithubClient.SendQueryAsync<RateLimitGraphQLRequest>(new GraphQLRequest
            {
                Query = query,
                Variables = variables,
            });

            var rateLimit = rateLimitRequest.Data.RateLimit;
            variables["dryRun"] = false;

            var hasMorePages = true;
            while (hasMorePages)
            {
                hasMorePages = false;

                if (!this.CanMakeGraphQLRequests(rateLimit))
                {
                    return null;
                }

                var recentIssueRequest = await _graphqlGithubClient.SendQueryAsync<JObject>(new GraphQLRequest
                {
                    Query = query,
                    Variables = variables,
                });

                rateLimit = recentIssueRequest.Data["rateLimit"].ToObject<RateLimit>();
                foreach (var repo in repoList)
                {
                    var repoObject = recentIssueRequest.Data[repo.RepoAlias];
                    if (repoObject == null)
                    {
                        continue;
                    }

                    foreach (var labelAlias in repo.Labels)
                    {
                        var labelResult = repoObject[labelAlias.Alias]?.ToObject<GraphQLLabelResult>();
                        if (labelResult == null)
                        {
                            continue;
                        }

                        variables[labelAlias.AfterVariableName] = labelResult.PageInfo.EndCursor;
                        variables[labelAlias.IncludeVariableName] = labelResult.PageInfo.HasNextPage;
                        hasMorePages |= labelResult.PageInfo.HasNextPage;

                        foreach (var issue in labelResult.Nodes)
                        {
                            var newIssue = new GithubIssue
                            {
                                IsAlreadyViewed = issue.ViewerSubscription != "UNSUBSCRIBED",
                                Labels = issue.Labels.Nodes.Select(n => n.Name).ToArray(),
                                Number = issue.Number,
                                Repo = repo.Repo,
                                Status = issue.State,
                                Title = issue.Title,
                                UpdatedAt = issue.UpdatedAt,
                                Url = issue.Url,
                            };

                            repo.Issues.Add(newIssue);
                        }
                    }
                }
            }

            var results = repoList.Select(x => new GithubIssuesByRepo { Repo = x.Repo, Issues = x.Issues.ToArray() }).ToArray();
            return results;
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
            public GraphQLIssue[] Nodes { get; set; }
            public GraphQLPageInfo PageInfo { get; set; }
        }

        public class GraphQLPageInfo
        {
            public string EndCursor { get; set; }
            public bool HasNextPage { get; set; }
        }

        public class GraphQLIssue
        {
            public GraphQLIssueLabelNodes Labels { get; set; }
            public string Number { get; set; }
            public string State { get; set; }
            public string Title { get; set; }
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
    }
}
