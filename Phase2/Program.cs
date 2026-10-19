using System.Globalization;

public class Phase2
{
    // continuation of phase 1
    private const int KeyThreshold = 10;

    private const string DefaultInputPath = "profiles.csv";
    private static readonly DateTimeOffset Since =
        new DateTimeOffset(2025, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until =
        new DateTimeOffset(2026, 10, 13, 23, 59, 59, TimeSpan.Zero);

    private static readonly DateTimeOffset DormantCutoff = Until.AddDays(-90);
    private static readonly DateTimeOffset NewcomerCutoff = Until.AddMonths(-3);

    public static void Main(string[] args)
    {
        var inputPath = args.Length > 0 ? args[0] : DefaultInputPath;
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"{inputPath} not found. Pass the path to Phase 1's profiles.csv, " +
                              "or copy it next to this program.");
            return;
        }

        var rows = File.ReadAllLines(inputPath).Skip(1).Select(ParseRow).ToList();
        foreach (var r in rows)
            r.Category = Classify(r);

        Console.WriteLine($"contributors in window: {rows.Count}");
        Console.WriteLine();
        Console.WriteLine("categories:");
        foreach (var g in rows.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-14} {g.Count(),4}  ({100.0 * g.Count() / rows.Count:0.#}%)");

        //newcomers
        var newcomersRecent = rows.Where(r => r.FirstContribution >= NewcomerCutoff).ToList();
        var newcomersWindow = rows.Where(r => r.FirstContribution >= Since).ToList();
        Console.WriteLine();
        Console.WriteLine($"newcomers, last 3 months: {newcomersRecent.Count}");
        Console.WriteLine($"newcomers, full window:   {newcomersWindow.Count}");
        Console.WriteLine();
        Console.WriteLine("newcomers by role:");
        foreach (var g in newcomersWindow.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-14} {g.Count(),4}");
        
        Console.WriteLine();
        Console.WriteLine("retention by first contribution type (full-window newcomers):");
        Console.WriteLine($"  {"type",-14} {"total",6} {"returned",9} {"rate",7}");

        foreach (var g in newcomersWindow.GroupBy(r => r.FirstContributionType)
                                         .OrderByDescending(g => g.Count()))
        {
            int returned = g.Count(r => r.LastContribution > r.FirstContribution);
            double rate = (double)returned / g.Count();
            Console.WriteLine($"  {g.Key,-14} {g.Count(),6} {returned,9} {rate,7:P0}");
        }

        int totalReturned = newcomersWindow.Count(r => r.LastContribution > r.FirstContribution);
        Console.WriteLine($"  {"overall",-14} {newcomersWindow.Count,6} {totalReturned,9} " +
                          $"{(double)totalReturned / newcomersWindow.Count,7:P0}");

        //one time contributors
        int oneTime = rows.Count(r => r.LifetimeContributions == 1);
        Console.WriteLine();
        Console.WriteLine($"accounts with exactly one lifetime contribution: {oneTime} " +
                          $"({100.0 * oneTime / rows.Count:0.#}%)");

        // share of activity for #1 contributor
        var top = rows.OrderByDescending(r => r.CodeContributions).First();
        int codeTotal = rows.Sum(r => r.CodeContributions);
        Console.WriteLine($"top account {top.Login}: {top.CodeContributions} of {codeTotal} " +
                          $"code contributions ({100.0 * top.CodeContributions / codeTotal:0.#}%)");

        WriteCategoriesCsv(rows, "categories.csv");
    }

    private static string Classify(Row r)
    {
        if (r.CodeContributions >= KeyThreshold)
            return "Frequent/Key";

        if (r.LifetimeContributions >= 2
            && r.FirstContribution < Since
            && r.LastContribution < DormantCutoff)
            return "Dormant";

        if (r.WindowCommits == 0 && r.WindowPullsMerged == 0
            && (r.WindowIssues > 0 || r.WindowPullsOpened > 0))
            return "Peripheral";

        if (r.WindowCommits > 0 || r.WindowPullsMerged > 0)
            return "Regular";

        return "Unclassified";
    }

    private static Row ParseRow(string line)
    {
        var f = SplitCsv(line);
        return new Row
        {
            Login = f[0],
            DisplayName = f[1],
            AccountType = f[2],
            WindowCommits = int.Parse(f[3]),
            WindowPullsOpened = int.Parse(f[4]),
            WindowPullsMerged = int.Parse(f[5]),
            WindowIssues = int.Parse(f[6]),
            CodeContributions = int.Parse(f[7]),
            CompositeScore = double.Parse(f[8], CultureInfo.InvariantCulture),
            FirstContribution = DateTimeOffset.Parse(f[9], CultureInfo.InvariantCulture),
            FirstContributionType = f[10],
            LastContribution = DateTimeOffset.Parse(f[11], CultureInfo.InvariantCulture),
            LifetimeContributions = int.Parse(f[12])
        };
    }
    
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static void WriteCategoriesCsv(IEnumerable<Row> rows, string path)
    {
        var lines = new List<string>
        {
            "login,category,newcomer_3mo,newcomer_window,first_contribution," +
            "first_contribution_type,last_contribution,lifetime_contributions," +
            "window_commits,window_prs_opened,window_prs_merged,window_issues," +
            "code_contributions,composite_score"
        };

        foreach (var r in rows.OrderByDescending(r => r.CompositeScore))
        {
            lines.Add($"{r.Login},{r.Category}," +
                      $"{r.FirstContribution >= NewcomerCutoff}," +
                      $"{r.FirstContribution >= Since}," +
                      $"{r.FirstContribution:yyyy-MM-dd},{r.FirstContributionType}," +
                      $"{r.LastContribution:yyyy-MM-dd},{r.LifetimeContributions}," +
                      $"{r.WindowCommits},{r.WindowPullsOpened},{r.WindowPullsMerged}," +
                      $"{r.WindowIssues},{r.CodeContributions}," +
                      $"{r.CompositeScore.ToString("F6", CultureInfo.InvariantCulture)}");
        }

        File.WriteAllLines(path, lines);
        Console.WriteLine();
        Console.WriteLine($"wrote {path} ({lines.Count - 1} rows)");
    }
}

public class Row
{
    public string Login = "";
    public string DisplayName = "";
    public string AccountType = "";
    public int WindowCommits;
    public int WindowPullsOpened;
    public int WindowPullsMerged;
    public int WindowIssues;
    public int CodeContributions;
    public double CompositeScore;
    public DateTimeOffset FirstContribution;
    public string FirstContributionType = "";
    public DateTimeOffset LastContribution;
    public int LifetimeContributions;
    public string Category = "";
}