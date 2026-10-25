using Octokit;
using Microsoft.Extensions.Configuration;

public class Metrics
{
    private const string Owner = "Genymobile";
    private const string Repo = "scrcpy";
    private const string Branch = "dev";

    private static readonly DateTimeOffset Since =
        new DateTimeOffset(2025, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until =
        new DateTimeOffset(2026, 10, 13, 23, 59, 59, TimeSpan.Zero);

    // for closed-unmerged prs (limit)
    private static readonly TimeSpan LandedMargin = TimeSpan.FromDays(7);
    private static readonly string[] BotSuffixes = { "[bot]", "bot" };

    public static async Task Main()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Metrics>().Build();
        var client = new GitHubClient(new ProductHeaderValue("CapstoneHealthMetrics"));
        client.Credentials = new Credentials(config["GitHubToken"]);
        var paging = new ApiOptions { PageSize = 100 };

        Console.WriteLine("fetching commits");
        var commits = await client.Repository.Commit.GetAll(Owner, Repo,
            new CommitRequest { Sha = Branch, Since = Since - LandedMargin, Until = Until }, paging);

        Console.WriteLine("fetching issues and PRs");
        var issuesAndPulls = await client.Issue.GetAllForRepository(Owner, Repo,
            new RepositoryIssueRequest { State = ItemStateFilter.All }, paging);
        var pulls = await client.PullRequest.GetAllForRepository(Owner, Repo,
            new PullRequestRequest { State = ItemStateFilter.All }, paging);

        Console.WriteLine("fetching comments");
        var comments = await client.Issue.Comment.GetAllForRepository(Owner, Repo,
            new IssueCommentRequest { Since = Since }, paging);

        Console.WriteLine("fetching review comments");
        var reviewComments = await client.PullRequest.ReviewComment.GetAllForRepository(Owner, Repo,
            new PullRequestReviewCommentRequest { Since = Since }, paging);

        var windowCommits = commits
            .Where(c => InWindow(c.Commit?.Committer?.Date))
            .ToList();
        var windowIssues = issuesAndPulls
            .Where(i => i.PullRequest == null && InWindow(i.CreatedAt))
            .ToList();
        var windowPulls = pulls.Where(p => InWindow(p.CreatedAt)).ToList();
        var windowComments = comments
            .Where(c => InWindow(c.CreatedAt) && !IsBot(c.User?.Login))
            .ToList();

        // checking maintainer again as top committer
        var maintainer = windowCommits
            .Select(CommitAuthor)
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;
        Console.WriteLine($"maintainer (top committer): {maintainer}");
        Console.WriteLine();

        var months = MonthsInWindow().ToList();
        var commitsPerMonth = windowCommits
            .GroupBy(c => Month(c.Commit.Committer.Date))
            .ToDictionary(g => g.Key, g => g.Count());
        var committersPerMonth = windowCommits
            .GroupBy(c => Month(c.Commit.Committer.Date))
            .ToDictionary(g => g.Key,
                          g => g.Select(CommitAuthor).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var issuesPerMonth = windowIssues
            .GroupBy(i => Month(i.CreatedAt))
            .ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine("month      commits  committers  issues");
        foreach (var m in months)
        {
            Console.WriteLine($"{m}  {commitsPerMonth.GetValueOrDefault(m),7}  " +
                              $"{committersPerMonth.GetValueOrDefault(m),10}  " +
                              $"{issuesPerMonth.GetValueOrDefault(m),6}");
        }
        Console.WriteLine("(first and last months are not full as time window is 13 to 13 oct)");
        WriteMonthlyCsv(months, commitsPerMonth, committersPerMonth, issuesPerMonth, "metrics_monthly.csv");

        var peakMonth = months.OrderByDescending(m => commitsPerMonth.GetValueOrDefault(m)).First();
        int distinctCommitters = windowCommits.Select(CommitAuthor)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Console.WriteLine();
        Console.WriteLine($"commit frequency: {windowCommits.Count} commits, " +
                          $"{windowCommits.Count / 12.0:0.#} per month, " +
                          $"peak {commitsPerMonth.GetValueOrDefault(peakMonth)} in {peakMonth}");
        Console.WriteLine($"committers: {distinctCommitters} distinct, " +
                          $"{committersPerMonth.Values.DefaultIfEmpty(0).Min()}-" +
                          $"{committersPerMonth.Values.DefaultIfEmpty(0).Max()} per month");
        Console.WriteLine($"issues opened: {windowIssues.Count}, {windowIssues.Count / 12.0:0.#} per month");
        
        int labelled = windowIssues.Count(i => i.Labels != null && i.Labels.Count > 0);
        Console.WriteLine($"  issues with at least one label: {labelled} of {windowIssues.Count} " +
                          $"({Pct(labelled, windowIssues.Count)})");
        
        //thread participation
        var threadAuthor = issuesAndPulls
            .Where(i => i.User != null)
            .ToDictionary(i => i.Number, i => i.User.Login);

        var contributors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in windowCommits) contributors.Add(CommitAuthor(c));
        foreach (var i in windowIssues) if (i.User != null) contributors.Add(i.User.Login);
        foreach (var p in windowPulls) if (p.User != null) contributors.Add(p.User.Login);
        contributors.RemoveWhere(IsBot);
        contributors.Remove(maintainer);
        var helpedOthers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allCommenters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int maintainerCommentsOnOthers = 0;
        int othersCommentsOnOthers = 0;

        foreach (var c in windowComments)
        {
            var login = c.User?.Login;
            var number = ThreadNumber(c.HtmlUrl);
            if (login == null || number == null) continue;
            if (!threadAuthor.TryGetValue(number.Value, out var author)) continue;
            allCommenters.Add(login);
            if (string.Equals(login, author, StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(login, maintainer, StringComparison.OrdinalIgnoreCase))
                maintainerCommentsOnOthers++;
            else
            {
                othersCommentsOnOthers++;
                helpedOthers.Add(login);
            }
        }

        int participating = contributors.Count(helpedOthers.Contains);
        int commentOnly = allCommenters.Count(l => !contributors.Contains(l)
            && !string.Equals(l, maintainer, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine($"thread participation: {participating} of {contributors.Count} contributors " +
                          $"({Pct(participating, contributors.Count)}) commented on someone else's thread");
        Console.WriteLine($"  comments on others' threads: maintainer {maintainerCommentsOnOthers}, " +
                          $"everyone else {othersCommentsOnOthers}");
        Console.WriteLine($"  accounts that only commented (no commit, issue or PR): {commentOnly}");

        //process metrics
        var commentsByThread = comments
            .Where(c => c.User != null && !IsBot(c.User.Login))
            .Select(c => (Number: ThreadNumber(c.HtmlUrl), Comment: c))
            .Where(x => x.Number != null)
            .GroupBy(x => x.Number!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Comment).OrderBy(c => c.CreatedAt).ToList());

        var responseTimes = new List<double>();
        int noResponse = 0;
        int firstResponseByMaintainer = 0;

        foreach (var issue in windowIssues)
        {
            var author = issue.User?.Login;
            commentsByThread.TryGetValue(issue.Number, out var threadComments);

            var first = threadComments?.FirstOrDefault(c =>
                !string.Equals(c.User.Login, author, StringComparison.OrdinalIgnoreCase));

            if (first == null)
            {
                noResponse++;
                continue;
            }

            responseTimes.Add((first.CreatedAt - issue.CreatedAt).TotalHours);
            if (string.Equals(first.User.Login, maintainer, StringComparison.OrdinalIgnoreCase))
                firstResponseByMaintainer++;
        }
        Console.WriteLine();
        Console.WriteLine($"issue response time: median {FormatHours(Median(responseTimes))} " +
                          $"({responseTimes.Count} issues answered)");
        Console.WriteLine($"  never answered: {noResponse} of {windowIssues.Count} " +
                          $"({Pct(noResponse, windowIssues.Count)})");
        Console.WriteLine($"  first response by the maintainer: {firstResponseByMaintainer} of " +
                          $"{responseTimes.Count} ({Pct(firstResponseByMaintainer, responseTimes.Count)})");

        var closedIssues = windowIssues.Where(i => i.ClosedAt != null).ToList();
        var resolutionTimes = closedIssues
            .Select(i => (i.ClosedAt!.Value - i.CreatedAt).TotalHours)
            .ToList();

        Console.WriteLine($"issue resolution duration: median {FormatHours(Median(resolutionTimes))} " +
                          $"({closedIssues.Count} closed)");
        Console.WriteLine($"  still open: {windowIssues.Count - closedIssues.Count}");

        // self-merged prs
        var mergedPulls = windowPulls.Where(p => p.MergedAt != null).ToList();
        int selfMerged = 0;
        foreach (var p in mergedPulls)
        {
            var full = await client.PullRequest.Get(Owner, Repo, p.Number);
            if (full.MergedBy != null && full.User != null &&
                string.Equals(full.MergedBy.Login, full.User.Login, StringComparison.OrdinalIgnoreCase))
                selfMerged++;
        }

        int closedUnmerged = windowPulls.Count(p => p.MergedAt == null && p.ClosedAt != null);
        int open = windowPulls.Count(p => p.ClosedAt == null);

        var commitDatesByAuthor = commits
            .Where(c => c.Commit?.Committer?.Date != null)
            .GroupBy(CommitAuthor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Commit.Committer.Date).ToList(),
                          StringComparer.OrdinalIgnoreCase);

        var landed = windowPulls
            .Where(p => p.MergedAt == null && p.ClosedAt != null && p.User != null)
            .Where(p => !string.Equals(p.User.Login, maintainer, StringComparison.OrdinalIgnoreCase))
            .Where(p => commitDatesByAuthor.TryGetValue(p.User.Login, out var dates) &&
                        dates.Any(d => Math.Abs((d - p.ClosedAt!.Value).TotalDays) <= LandedMargin.TotalDays))
            .ToList();
        
        bool IsMaintainer(string? login) =>
            string.Equals(login, maintainer, StringComparison.OrdinalIgnoreCase);

        var pullAuthor = windowPulls
            .Where(p => p.User != null)
            .ToDictionary(p => p.Number, p => p.User.Login);

        var reviewOnWindowPulls = reviewComments
            .Select(r => (Number: ThreadNumber(r.PullRequestUrl), Reviewer: r.User?.Login))
            .Where(x => x.Number != null && pullAuthor.ContainsKey(x.Number.Value))
            .Select(x => (Number: x.Number!.Value, x.Reviewer, Author: pullAuthor[x.Number!.Value]))
            .ToList();

        int maintainerPullCount = windowPulls.Count(p => IsMaintainer(p.User?.Login));
        int outsidePullCount = windowPulls.Count(p => p.User != null && !IsMaintainer(p.User.Login));

        var onMaintainerPulls = reviewOnWindowPulls.Where(x => IsMaintainer(x.Author)).ToList();
        var onOutsidePulls = reviewOnWindowPulls.Where(x => !IsMaintainer(x.Author)).ToList();

        int outsidePullsReviewed = onOutsidePulls.Select(x => x.Number).Distinct().Count();
        int outsideByMaintainer = onOutsidePulls.Count(x => IsMaintainer(x.Reviewer));
        int outsideByAuthor = onOutsidePulls.Count(x =>
            string.Equals(x.Reviewer, x.Author, StringComparison.OrdinalIgnoreCase));
        int outsideByOthers = onOutsidePulls.Count - outsideByMaintainer - outsideByAuthor;
        int maintainerPullsByOthers = onMaintainerPulls.Count(x => !IsMaintainer(x.Reviewer));

        Console.WriteLine();
        Console.WriteLine($"self merge rate: {selfMerged} of {mergedPulls.Count} merged PRs " +
                          $"({Pct(selfMerged, mergedPulls.Count)})");
        Console.WriteLine($"  PR outcomes: {mergedPulls.Count} merged, {closedUnmerged} closed unmerged, {open} open");
        Console.WriteLine($"  closed unmerged with an author commit within {LandedMargin.TotalDays:0} days " +
                          $"(maintainer's own PRs excluded): {landed.Count} of {closedUnmerged}");
        Console.WriteLine($"  review comments: {reviewOnWindowPulls.Count} on {windowPulls.Count} PRs");
        Console.WriteLine($"    on maintainer's PRs: {onMaintainerPulls.Count} on {maintainerPullCount} PRs, " +
                          $"{maintainerPullsByOthers} of them written by someone else");
        Console.WriteLine($"    on outside PRs: {onOutsidePulls.Count} on {outsidePullCount} PRs; " +
                          $"{outsidePullsReviewed} of {outsidePullCount} received any review comment");
        Console.WriteLine($"    on outside PRs, written by: maintainer {outsideByMaintainer}, " +
                          $"PR author {outsideByAuthor}, others {outsideByOthers}");
        Console.WriteLine("    maintainer PRs with the most review comments:");
        foreach (var g in onMaintainerPulls.GroupBy(x => x.Number)
                     .OrderByDescending(g => g.Count())
                     .Take(5))
        {
            int byOthers = g.Count(x => !IsMaintainer(x.Reviewer));
            int reviewers = g.Where(x => !IsMaintainer(x.Reviewer))
                .Select(x => x.Reviewer)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Console.WriteLine($"      #{g.Key}: {g.Count()} comments, {byOthers} by others, " +
                              $"{reviewers} distinct reviewers");
        }

        Console.WriteLine("    outside PRs with the most review comments:");
        foreach (var g in onOutsidePulls.GroupBy(x => x.Number)
                     .OrderByDescending(g => g.Count())
                     .Take(5))
        {
            Console.WriteLine($"      #{g.Key}: {g.Count()} comments");
        }
    }

    private static bool InWindow(DateTimeOffset? date) =>
        date != null && date.Value >= Since && date.Value <= Until;

    private static bool IsBot(string? login) =>
        login != null && BotSuffixes.Any(s => login.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    // unlinked commits have no GitHub login => fallback to git author name
    private static string CommitAuthor(GitHubCommit c) =>
        c.Author?.Login ?? $"(git) {c.Commit?.Author?.Name}";

    //pull issue/pr number from url
    private static int? ThreadNumber(string? url)
    {
        if (url == null) return null;
        var parts = url.Split('/', '#');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if ((parts[i] == "issues" || parts[i] == "pull" || parts[i] == "pulls") &&
                int.TryParse(parts[i + 1], out var n))
                return n;
        }
        return null;
    }

    private static string Month(DateTimeOffset d) => d.ToString("yyyy-MM");

    private static IEnumerable<string> MonthsInWindow()
    {
        var m = new DateTime(Since.Year, Since.Month, 1);
        var last = new DateTime(Until.Year, Until.Month, 1);
        while (m <= last)
        {
            yield return m.ToString("yyyy-MM");
            m = m.AddMonths(1);
        }
    }
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string FormatHours(double hours)
    {
        if (double.IsNaN(hours)) return "n/a";
        return hours < 48 ? $"{hours:0.#} hours" : $"{hours / 24:0.#} days";
    }

    private static string Pct(int part, int whole) =>
        whole == 0 ? "n/a" : $"{100.0 * part / whole:0.#}%";

    private static void WriteMonthlyCsv(List<string> months,
                                        Dictionary<string, int> commits,
                                        Dictionary<string, int> committers,
                                        Dictionary<string, int> issues,
                                        string path)
    {
        var lines = new List<string> { "month,commits,committers,issues_opened" };
        foreach (var m in months)
            lines.Add($"{m},{commits.GetValueOrDefault(m)},{committers.GetValueOrDefault(m)},{issues.GetValueOrDefault(m)}");
        File.WriteAllLines(path, lines);
        Console.WriteLine($"wrote {path}");
    }
}
