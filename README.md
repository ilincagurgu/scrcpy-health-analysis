These are the scripts used for Capstone Software Development, used to analyse the project health of Genymobile/scrcpy.
This code was written with the assistance of GitHub Copilot JetBrains Rider plugin (the autocomplete function). Every suggestion was checked before deciding if it is to be kept or dismissed.

The scripts are also explained in the project report.
They look at the scrcpy repository between 13 October 2025 and 13 October 2026: who contributes, how contributors can be grouped, and how issues and pull requests get handled.
All three are built on Octokit.NET.

## What's in here

- **Phase1** fetches the commits on `dev`, all issues and pull requests, filters out bots and duplicate accounts, and writes `profiles.csv` and `code_distribution.csv`.
- **Phase2** reads `profiles.csv` and puts every contributor into a category (Frequent/Key, Dormant, Peripheral, Regular, plus a newcomer flag). It also prints the retention numbers. It doesn't call the API.
- **Metrics** computes the activity and process metrics (commits and committers per month, issues opened, thread participation, issue response time, issue resolution duration, self-merge rate) and writes `metrics_monthly.csv`.
