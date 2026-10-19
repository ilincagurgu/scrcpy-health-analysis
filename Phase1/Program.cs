using Octokit;
using Microsoft.Extensions.Configuration;

public class Phase1
{
    private const string Owner = "Genymobile";
    private const string Repo = "scrcpy";

    private static readonly DateTimeOffset Since =
        new DateTimeOffset(2025, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until =
        new DateTimeOffset(2026, 10, 13, 23, 59, 59, TimeSpan.Zero);
    
    private const string Branch = "dev";

    private static readonly string[] BotSuffixes = { "[bot]", "bot" };

    public static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Phase1>()
            .Build();

        var client = new GitHubClient(new ProductHeaderValue("CapstoneHealthAnalysis"));
        client.Credentials = new Credentials(config["GitHubToken"]);

        var windowCommits = await FetchCommits(client, Since, Until);
        var allIssues = await FetchIssues(client);
        var allPulls = await FetchPulls(client);

        var issuesInWindow = allIssues
            .Where(i => i.PullRequest == null && i.CreatedAt >= Since && i.CreatedAt <= Until)
            .ToList();
        var pullsInWindow = allPulls
            .Where(p => p.CreatedAt >= Since && p.CreatedAt <= Until)
            .ToList();

        Console.WriteLine($"commits: {windowCommits.Count}");
        Console.WriteLine($"issues:  {issuesInWindow.Count}");
        Console.WriteLine($"PRs:     {pullsInWindow.Count}");

        var allCommits = await FetchCommits(client, null, null);
        Console.WriteLine($"commits (all time): {allCommits.Count}");

        // maintainer==top committer in the window
        var maintainer = windowCommits
            .Where(c => c.Author?.Login != null)
            .GroupBy(c => c.Author!.Login, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;
        Console.WriteLine($"maintainer (top committer): {maintainer}");
        
        //PR outcomes
        int merged = pullsInWindow.Count(p => p.MergedAt != null);
        int closedUnmerged = pullsInWindow.Count(p => p.MergedAt == null && p.ClosedAt != null);
        int open = pullsInWindow.Count(p => p.ClosedAt == null);
        Console.WriteLine($"PR outcomes: merged {merged}, closed unmerged {closedUnmerged}, open {open}");
        var mergedByOthers = pullsInWindow
            .Where(p => p.MergedAt != null &&
                        !string.Equals(p.User?.Login, maintainer, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"merged PRs not opened by the maintainer: {mergedByOthers.Count}");
        foreach (var p in mergedByOthers)
            Console.WriteLine($"  #{p.Number} by {p.User?.Login}");

        
        // closed PR, not flagged as Merged
        //treat such PR as landed if its author has a
        //commit in the branch within a week of the PR being closed
        var commitsByAuthor = new Dictionary<string, List<DateTimeOffset>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in allCommits)
        {
            var login = c.Author?.Login;
            var date = c.Commit?.Committer?.Date;
            if (login == null || date == null) continue;

            if (!commitsByAuthor.ContainsKey(login))
                commitsByAuthor[login] = new List<DateTimeOffset>();
            commitsByAuthor[login].Add(date.Value);
        }

        var landedWindow = TimeSpan.FromDays(7);
        var landedAnyway = new List<PullRequest>();

        foreach (var pr in pullsInWindow.Where(p => p.MergedAt == null && p.ClosedAt != null &&
                                                    !string.Equals(p.User?.Login, maintainer, StringComparison.OrdinalIgnoreCase)))
        {
            var login = pr.User?.Login;
            if (login == null) continue;
            if (!commitsByAuthor.TryGetValue(login, out var dates)) continue;

            var closed = pr.ClosedAt!.Value;
            if (dates.Any(d => d >= closed - landedWindow && d <= closed + landedWindow))
                landedAnyway.Add(pr);
        }

        Console.WriteLine($"closed-unmerged PRs whose author committed within " +
                          $"{landedWindow.TotalDays:0} days of closing: {landedAnyway.Count} " +
                          $"of {closedUnmerged}");
        foreach (var pr in landedAnyway.OrderBy(p => p.Number))
            Console.WriteLine($"  #{pr.Number} by {pr.User?.Login} closed {pr.ClosedAt:yyyy-MM-dd}");

        Console.WriteLine($"contributions accepted (merged + landed via commit): " +
                          $"{merged + landedAnyway.Count} of {pullsInWindow.Count} PRs opened");
        
        //official status
        var officialLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool collaboratorListAvailable = false;
        try
        {
            var collaborators = await client.Repository.Collaborator.GetAll(Owner, Repo);
            foreach (var c in collaborators)
                officialLogins.Add(c.Login);
            collaboratorListAvailable = true;
            Console.WriteLine($"collaborators: {officialLogins.Count}");
        }
        catch (ForbiddenException)
        {
            Console.WriteLine("collaborator list not public, official status unavailable");
        }

        var profiles = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        var emailToLogin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nameToLogin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var loginToAuthorNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in allCommits)
        {
            var login = c.Author?.Login;
            if (login == null) continue;

            var email = c.Commit?.Author?.Email;
            var name = c.Commit?.Author?.Name;

            var resolved = login;
            if (email != null && emailToLogin.TryGetValue(email, out var byEmail))
                resolved = byEmail;
            else if (name != null && nameToLogin.TryGetValue(name, out var byName))
                resolved = byName;

            if (email != null && !emailToLogin.ContainsKey(email))
                emailToLogin[email] = resolved;
            if (name != null && !nameToLogin.ContainsKey(name))
                nameToLogin[name] = resolved;

            if (name != null)
            {
                if (!loginToAuthorNames.ContainsKey(login))
                    loginToAuthorNames[login] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                loginToAuthorNames[login].Add(name);
            }

            var date = c.Commit?.Committer?.Date;
            if (date == null) continue;

            var p = GetOrCreate(profiles, resolved);
            p.RecordContribution(date.Value);
            if (date.Value >= Since && date.Value <= Until)
                p.WindowCommits++;
        }

        foreach (var i in allIssues.Where(x => x.PullRequest == null))
        {
            var login = i.User?.Login;
            if (login == null) continue;

            var p = GetOrCreate(profiles, login);
            p.RecordContribution(i.CreatedAt);
            if (i.CreatedAt >= Since && i.CreatedAt <= Until)
                p.WindowIssues++;
        }

        foreach (var pr in allPulls)
        {
            var login = pr.User?.Login;
            if (login == null) continue;

            var p = GetOrCreate(profiles, login);
            p.RecordContribution(pr.CreatedAt);

            if (pr.CreatedAt >= Since && pr.CreatedAt <= Until)
            {
                p.WindowPullsOpened++;
                if (pr.MergedAt != null) p.WindowPullsMerged++;
            }
        }

        AssignFirstContributionTypes(profiles, allCommits, allIssues, allPulls);

        //classify only the ones active in window
        var activeInWindow = profiles.Values
            .Where(p => p.WindowCommits > 0 || p.WindowPullsOpened > 0 || p.WindowIssues > 0)
            .ToList();

        Console.WriteLine($"fetching metadata for {activeInWindow.Count} active accounts " +
                          $"(of {profiles.Count} in the full history)");

        int done = 0;
        foreach (var p in activeInWindow)
        {
            try
            {
                var user = await client.User.Get(p.Login);
                p.AccountType = user.Type?.ToString() ?? "Unknown";
                p.DisplayName = user.Name;
            }
            catch (NotFoundException)
            {
                p.AccountType = "Deleted";
            }

            done++;
            if (done % 100 == 0) Console.WriteLine($"  {done}/{activeInWindow.Count}");
        }

        foreach (var p in profiles.Values)
            p.IsOfficial = officialLogins.Contains(p.Login);

        foreach (var p in activeInWindow)
        {
            p.IsBotByName = BotSuffixes.Any(s => p.Login.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            p.IsBotByType = p.AccountType == "Bot";
        }

        var bots = activeInWindow.Where(p => p.IsBotByName || p.IsBotByType).ToList();
        var humans = activeInWindow.Where(p => !p.IsBotByName && !p.IsBotByType).ToList();

        Console.WriteLine($"bots: {bots.Count}");
        foreach (var b in bots)
            Console.WriteLine($"  {b.Login} (name:{b.IsBotByName} type:{b.IsBotByType})");

        var orgs = activeInWindow.Where(p => p.AccountType == "Organization").ToList();
        Console.WriteLine($"organization accounts: {orgs.Count}");
        foreach (var o in orgs)
            Console.WriteLine($"  {o.Login}");

        var nameGroups = humans
            .Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
            .GroupBy(p => p.DisplayName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        Console.WriteLine($"display name collisions: {nameGroups.Count}");
        foreach (var g in nameGroups)
            Console.WriteLine($"  \"{g.Key}\": {string.Join(", ", g.Select(p => p.Login))}");

        var sharedCandidates = loginToAuthorNames.Where(kv => kv.Value.Count > 1).ToList();
        Console.WriteLine($"logins with multiple commit author names: {sharedCandidates.Count}");
        foreach (var kv in sharedCandidates)
            Console.WriteLine($"  {kv.Key}: {string.Join(" | ", kv.Value)}");

        var allEmails = allCommits
            .Where(c => c.Author?.Login != null && c.Commit?.Author?.Email != null)
            .Select(c => c.Commit.Author.Email)
            .ToList();
        var noreply = allEmails.Count(e => e.EndsWith("users.noreply.github.com",
                                                      StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"commit emails: {allEmails.Count}, noreply: {noreply}");

        
        string[] functionWords = { "team", "admin", "release", "support", "info",
            "contact", "staff", "office", "maintainer", "project", "group" };

        var functionLike = allCommits
            .Where(c => c.Commit?.Author != null)
            .Select(c => (Login: c.Author?.Login ?? "(no login)",
                Name: c.Commit.Author.Name ?? "",
                Email: c.Commit.Author.Email ?? ""))
            .Distinct()
            .Where(x => functionWords.Any(w =>
                x.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                x.Email.Split('@')[0].Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Console.WriteLine($"commit identities with function-like names: {functionLike.Count}");
        foreach (var x in functionLike)
            Console.WriteLine($"  {x.Login}: {x.Name} <{x.Email}>");
        //score as in the interim report
        int commitTotal = humans.Sum(p => p.WindowCommits);
        int prTotal = humans.Sum(p => p.WindowPullsOpened);
        int issueTotal = humans.Sum(p => p.WindowIssues);

        foreach (var p in humans)
        {
            p.CompositeScore =
                (commitTotal > 0 ? (double)p.WindowCommits / commitTotal : 0)
              + (prTotal > 0 ? (double)p.WindowPullsOpened / prTotal : 0)
              + (issueTotal > 0 ? (double)p.WindowIssues / issueTotal : 0);
        }

        WriteProfilesCsv(humans, "profiles.csv");
        WriteDistributionCsv(humans, "code_distribution.csv");
        Console.WriteLine();
        Console.WriteLine("code contributions (commits + merged PRs):");
        foreach (var p in humans.Where(p => p.CodeContributions > 0)
                                .OrderByDescending(p => p.CodeContributions))
        {
            Console.WriteLine($"  {p.Login,-20} {p.CodeContributions,5}  " +
                              $"(commits {p.WindowCommits}, merged PRs {p.WindowPullsMerged})");
        }

        Console.WriteLine();
        if (collaboratorListAvailable)
        {
            Console.WriteLine("official status:");
            foreach (var p in humans.Where(p => p.HasOfficialStatus))
                Console.WriteLine($"  {p.Login}");
        }
        else
        {
            Console.WriteLine("official status: not measurable, volume criterion only");
        }
    }
    private static Profile GetOrCreate(Dictionary<string, Profile> profiles, string login)
    {
        if (!profiles.TryGetValue(login, out var p))
        {
            p = new Profile { Login = login };
            profiles[login] = p;
        }
        return p;
    }

    private static void AssignFirstContributionTypes(
        Dictionary<string, Profile> profiles,
        IReadOnlyList<GitHubCommit> allCommits,
        IReadOnlyList<Issue> allIssues,
        IReadOnlyList<PullRequest> allPulls)
    {
        var earliest = new Dictionary<string, (DateTimeOffset Date, string Type)>(
            StringComparer.OrdinalIgnoreCase);

        void Consider(string? login, DateTimeOffset date, string type)
        {
            if (login == null) return;
            if (!earliest.TryGetValue(login, out var cur) || date < cur.Date)
                earliest[login] = (date, type);
        }

        foreach (var c in allCommits)
        {
            var date = c.Commit?.Committer?.Date;
            if (date != null) Consider(c.Author?.Login, date.Value, "commit");
        }
        foreach (var i in allIssues.Where(x => x.PullRequest == null))
            Consider(i.User?.Login, i.CreatedAt, "issue");
        foreach (var p in allPulls)
            Consider(p.User?.Login, p.CreatedAt, p.MergedAt != null ? "merged_pr" : "unmerged_pr");

        foreach (var kv in earliest)
            if (profiles.TryGetValue(kv.Key, out var profile))
                profile.FirstContributionType = kv.Value.Type;
    }

    private static async Task<IReadOnlyList<GitHubCommit>> FetchCommits(
        GitHubClient client, DateTimeOffset? since, DateTimeOffset? until)
    {
        var request = new CommitRequest { Sha = Branch };
        if (since.HasValue) request.Since = since.Value;
        if (until.HasValue) request.Until = until.Value;

        return await client.Repository.Commit.GetAll(Owner, Repo, request,
                                                     new ApiOptions { PageSize = 100 });
    }

    private static async Task<IReadOnlyList<Issue>> FetchIssues(GitHubClient client)
    {
        var request = new RepositoryIssueRequest { State = ItemStateFilter.All };
        return await client.Issue.GetAllForRepository(Owner, Repo, request,
                                                      new ApiOptions { PageSize = 100 });
    }

    private static async Task<IReadOnlyList<PullRequest>> FetchPulls(GitHubClient client)
    {
        var request = new PullRequestRequest { State = ItemStateFilter.All };
        return await client.PullRequest.GetAllForRepository(Owner, Repo, request,
                                                            new ApiOptions { PageSize = 100 });
    }

    private static void WriteProfilesCsv(IEnumerable<Profile> profiles, string path)
    {
        var lines = new List<string>
        {
            "login,display_name,account_type,window_commits,window_prs_opened," +
            "window_prs_merged,window_issues,code_contributions,composite_score," +
            "first_contribution,first_contribution_type,last_contribution," +
            "lifetime_contributions,official_status"
        };

        foreach (var p in profiles.OrderByDescending(p => p.CompositeScore))
        {
            lines.Add($"{p.Login},{Escape(p.DisplayName)},{p.AccountType}," +
                      $"{p.WindowCommits},{p.WindowPullsOpened},{p.WindowPullsMerged}," +
                      $"{p.WindowIssues},{p.CodeContributions}," +
                      $"{p.CompositeScore.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                      $"{p.FirstContribution:yyyy-MM-dd},{p.FirstContributionType}," +
                      $"{p.LastContribution:yyyy-MM-dd},{p.LifetimeContributions}," +
                      $"{p.HasOfficialStatus}");
        }

        File.WriteAllLines(path, lines);
        Console.WriteLine($"wrote {path} ({lines.Count - 1} rows)");
    }

    private static void WriteDistributionCsv(IEnumerable<Profile> profiles, string path)
    {
        var lines = new List<string> { "rank,login,code_contributions" };
        int rank = 1;
        foreach (var p in profiles.Where(p => p.CodeContributions > 0)
                                  .OrderByDescending(p => p.CodeContributions))
        {
            lines.Add($"{rank},{p.Login},{p.CodeContributions}");
            rank++;
        }
        File.WriteAllLines(path, lines);
        Console.WriteLine($"wrote {path} ({lines.Count - 1} rows)");
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') ? $"\"{s}\"" : s;
    }
}

public class Profile
{
    public string Login = "";
    public string? DisplayName;
    public string AccountType = "Unknown";

    public int WindowCommits;
    public int WindowPullsOpened;
    public int WindowPullsMerged;
    public int WindowIssues;

    public double CompositeScore;

    public DateTimeOffset? FirstContribution;
    public DateTimeOffset? LastContribution;
    public string? FirstContributionType;
    public int LifetimeContributions;

    public bool IsBotByName;
    public bool IsBotByType;
    public bool IsOfficial;

    public int CodeContributions => WindowCommits + WindowPullsMerged;
    public bool HasOfficialStatus => IsOfficial;

    public void RecordContribution(DateTimeOffset date)
    {
        LifetimeContributions++;
        if (FirstContribution == null || date < FirstContribution) FirstContribution = date;
        if (LastContribution == null || date > LastContribution) LastContribution = date;
    }
}