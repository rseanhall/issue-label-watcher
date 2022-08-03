using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace IssueLabelWatcherWebJob
{
    public interface IIlwService
    {
        Task FindAndNotifyAllLabelledIssues(IIlwIssuesState state);
        Task FindAndNotifyRecentLabelledIssues(IIlwIssuesState state);
    }

    public class IlwService : IIlwService
    {
        private readonly IEmailSender _emailSender;
        private readonly IGithubService _githubService;
        private readonly ILogger _logger;

        public IlwService(IEmailSender emailSender, IGithubService githubService, ILogger<IlwService> logger)
        {
            _emailSender = emailSender;
            _githubService = githubService;
            _logger = logger;
        }

        public Task FindAndNotifyAllLabelledIssues(IIlwIssuesState state)
        {
            return this.FindAndNotifyLabelledIssues(state, null);
        }

        public Task FindAndNotifyRecentLabelledIssues(IIlwIssuesState state)
        {
            var timeFromNow = state.LastRunTime.HasValue ? DateTime.UtcNow - state.LastRunTime.Value.AddHours(-1)
                                                         : TimeSpan.FromDays(1);
            return this.FindAndNotifyLabelledIssues(state, timeFromNow);
        }

        private async Task FindAndNotifyLabelledIssues(IIlwIssuesState state, TimeSpan? timeFromNow)
        {
            state.LastRunTime = DateTime.UtcNow;
            var issuesByRepos = await _githubService.GetRecentIssuesWithLabel(timeFromNow);
            if (issuesByRepos == null)
            {
                _logger.LogWarning("No results returned.");
            }
            else
            {
                this.CreateAndSendEmail(state, issuesByRepos);

                var sb = new StringBuilder();
                foreach (var issuesByRepo in issuesByRepos)
                {
                    var relevantIssues = issuesByRepo.Issues.Where(i => timeFromNow.HasValue || !i.IsAlreadyViewed).ToArray();
                    sb.AppendLine($"{issuesByRepo.Repo.Owner}/{issuesByRepo.Repo.Name}: {relevantIssues.Length}");

                    foreach (var issue in relevantIssues)
                    {
                        sb.AppendLine($"    {issue.Number} {issue.Status} {issue.IssueType} {issue.Title}");
                        sb.AppendLine($"        {issue.Url} {issue.IsAlreadyViewed}");
                    }
                }
                _logger.LogInformation(sb.ToString());
            }
        }

        private void CreateAndSendEmail(IIlwIssuesState state, IGithubIssuesByRepo[] issuesByRepos)
        {
            //https://developers.google.com/gmail/markup/reference/go-to-action#json-ld
            var total = 0;
            var fragments = new List<string>();
            string? singleIssueUrl = null;
            foreach (var issuesByRepo in issuesByRepos)
            {
                if (!state.IssuesByRepo.TryGetValue(issuesByRepo.Repo.FullName, out var stateIssues))
                {
                    stateIssues = new HashSet<string>();
                    state.IssuesByRepo.Add(issuesByRepo.Repo.FullName, stateIssues);
                }

                var issues = new List<IGithubIssue>();
                foreach (var issue in issuesByRepo.Issues)
                {
                    if (!stateIssues.Add(issue.Number))
                    {
                        issue.IsAlreadyViewed = true;
                    }

                    if (!issue.IsAlreadyViewed)
                    {
                        issues.Add(issue);
                    }
                }

                if (!issues.Any())
                {
                    continue;
                }

                total += issues.Count;
                issues.Sort((x, y) => y.UpdatedAt.CompareTo(x.UpdatedAt));

                var fragment = new StringBuilder();
                fragment.AppendLine("<p>");
                fragment.AppendLine($"<h3>{issuesByRepo.Repo.FullName}</h3>");
                fragment.AppendLine("<table>");
                foreach (var issue in issues)
                {
                    if (singleIssueUrl == null)
                    {
                        singleIssueUrl = issue.Url;
                    }
                    else
                    {
                        singleIssueUrl = string.Empty;
                    }

                    var labels = string.Join(", ", issue.Labels.Select(x => HttpUtility.HtmlEncode(x)));
                    var safeTitle = HttpUtility.HtmlEncode(issue.Title);
                    fragment.AppendLine($"<tr><td><a href=\"{issue.Url}\">{issue.Number}</a></td><td>{issue.IssueType}</td><td>{safeTitle}</td></tr>");
                    fragment.AppendLine($"<tr><td></td><td>{issue.Status}</td><td><i>{labels}</i></td></tr>");
                }
                fragment.AppendLine("</table>");

                fragments.Add(fragment.ToString());
            }

            if (!fragments.Any())
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("<html><body>");
            if (!string.IsNullOrEmpty(singleIssueUrl))
            {
                sb.AppendLine($"<script type=\"application/ld+json\">" +
                    "{" +
                    "  \"@context\": \"http://schema.org\"," +
                    "  \"@type\": \"EmailMessage\"," +
                    "  \"potentialAction\": {" +
                    "    \"@type\": \"ViewAction\"," +
                    $"    \"target\": \"{singleIssueUrl}\"," +
                    $"    \"url\": \"{singleIssueUrl}\"," +
                    "    \"name\": \"View Issue\"" +
                    "  }," +
                    "  \"description\": \"View the issue or PR on GitHub\"," +
                    "  \"publisher\": {" +
                    "    \"@type\": \"Organization\"," +
                    "    \"url\": \"https://github.com/rseanhall/issue-label-watcher\"," +
                    "    \"name\": \"IssueLabelWatcher\"" +
                    "  }" +
                    "}" +
                    "</script>");
                /*sb.AppendLine("<div itemscope itemtype='http://schema.org/EmailMessage'>");
                sb.AppendLine("<div itemprop='potentialAction' itemscope itemtype='http://schema.org/ViewAction'>");
                sb.AppendLine($"<link itemprop='url' href='{singleIssueUrl}'/>");
                sb.AppendLine("<meta itemprop='name' content='View Issue'/>");
                sb.AppendLine("</div>");
                sb.AppendLine("<meta itemprop='description' content='View the issue or PR on GitHub'/>");
                sb.AppendLine("</div>");*/
            }
            foreach (string fragment in fragments)
            {
                sb.Append(fragment);
            }
            sb.AppendLine($"<p><h6>IssueLabelWatcher v{HttpUtility.HtmlEncode(ThisAssembly.AssemblyInformationalVersion)}</h6>");
            sb.AppendLine("</body></html>");

            var subject = $"IssueLabelWatcher - {total} Issue{(total > 1 ? "s" : "")}";
            var htmlContent = sb.ToString();
            _emailSender.SendHtmlEmail(subject, htmlContent);
        }
    }
}
