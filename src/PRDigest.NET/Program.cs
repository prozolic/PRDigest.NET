
// Set up GithubClient.
using Octokit;

var githubClient = new GitHubClient(new ProductHeaderValue("PR-Digest.NET"));
var credentials = new Credentials(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
githubClient.Credentials = credentials;

// 24-hour time range for the previous day.
var currentDate = TimeProvider.System.GetUtcNow();
var previousDate = currentDate.AddDays(-1);

// Set time to 00:00:00 for both dates to cover the entire previous day.
DateTimeOffset startTargetDate = new (previousDate.Year, previousDate.Month, previousDate.Day, previousDate.Hour, previousDate.Minute, previousDate.Second, TimeSpan.Zero);
DateTimeOffset endTargetDate = new (currentDate.Year, currentDate.Month, currentDate.Day, currentDate.Hour, currentDate.Minute, currentDate.Second, TimeSpan.Zero);

// Target dotnet/runtime.
string owner = "dotnet";
string repo = "runtime";

// Create search request for merged pull requests in the specified date range
var searchRequest = new SearchIssuesRequest()
{
    Type = IssueTypeQualifier.PullRequest,
    Repos = [$"{owner}/{repo}"],
    State = ItemState.Closed,
    Merged = DateRange.Between(startTargetDate, endTargetDate),
    Is = [IssueIsQualifier.Merged]
};

var searchIssueResult = await githubClient.Search.SearchIssues(searchRequest);

if (searchIssueResult.Items.Count == 0)
{
    Console.WriteLine($"There were no PRs merged between {previousDate:yyyy/MM/dd} and {currentDate:yyyy/MM/dd}.");
    return;
}

Console.WriteLine($"{searchIssueResult.Items.Count} pull requests were merged between {previousDate:yyyy/MM/dd} and {currentDate:yyyy/MM/dd}.");
