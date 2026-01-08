using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Octokit;
using PRDigest.NET;
using System.Text;

if (args.Length == 0) return;

var startTime = TimeProvider.System.GetTimestamp();

var archivesDir = args[0];
var outputsDir = args[1];
if (args.Length == 3 && args[2] == "-g")
{
    // generate current day's PR markdown and HTML
    await SummarizeCurrentPullRequestAndCreate(archivesDir, outputsDir);
}

// convert all markdown files to HTML
await CreateHtml(archivesDir, outputsDir);

// end
var endTime = TimeProvider.System.GetTimestamp();
Console.WriteLine($"Total elapsed time: {TimeProvider.System.GetElapsedTime(startTime, endTime).TotalSeconds} seconds.");


async ValueTask SummarizeCurrentPullRequestAndCreate(string archivesDir, string outputsDir)
{
    // Target dotnet/runtime.
    const string OWNER = "dotnet";
    const string REPO = "runtime";
    const string FullRepo = $"{OWNER}/{REPO}";

    // 24-hour time range for the previous day.
    var currentDate = TimeProvider.System.GetUtcNow();
    var previousDate = currentDate.AddDays(-1);

    // Set time to 00:00:00 for both dates to cover the entire previous day.
    DateTimeOffset startTargetDate = new(previousDate.Year, previousDate.Month, previousDate.Day, 0, 0, 0, previousDate.Offset);
    DateTimeOffset endTargetDate = (new DateTimeOffset(currentDate.Year, currentDate.Month, currentDate.Day, 0, 0, 0, currentDate.Offset)).Add(TimeSpan.FromSeconds(-1));

    var year = $"{startTargetDate.Year:D4}";
    var month = $"{startTargetDate.Month:D2}";
    var day = $"{startTargetDate.Day:D2}";

    // Set up directories
    SetupDirectoryIfNotExists(archivesDir, outputsDir, year, month, day);

    // Check if summary already exists
    if (ExistSummaryForSpecifiedDate(archivesDir, year, month, day))
    {
        Console.WriteLine($"Summary for {startTargetDate:yyyy/MM/dd} already exists.");
        return;
    }

    // Get all merged pull requests.
    var pullRequestInfos = await GetAllPullRequestInfoAsync(startTargetDate, endTargetDate);
    if (pullRequestInfos.Length == 0)
    {
        Console.WriteLine($"There were no PRs merged into {FullRepo} between {startTargetDate:yyyy/MM/dd HH:mm:ss} and {endTargetDate:yyyy/MM/dd HH:mm:ss}.");
        return;
    }
    Console.WriteLine($"{pullRequestInfos.Length} pull requests into {FullRepo} were merged between {startTargetDate:yyyy/MM/dd HH:mm:ss} and {endTargetDate:yyyy/MM/dd HH:mm:ss}.");

    // Generate HTML content for each pull request using Anthropic API.
    var markdown = await SummarizePullRequestAsync(pullRequestInfos);
    if (string.IsNullOrEmpty(markdown)) return;

    // Save markdown and HTML files.
    var html = HtmlGenereator.GenerateHtmlFromMarkdown($"{year}年{month}月{day}日", markdown);
    var markdownTask = File.WriteAllTextAsync(Path.Combine(archivesDir, year, month, $"{day}.md"), markdown);
    var htmlTask = File.WriteAllTextAsync(Path.Combine(outputsDir, year, month, $"{day}.html"), html);
    await Task.WhenAll(markdownTask, htmlTask);
}

void SetupDirectoryIfNotExists(string archivesDir, string outputsDir, string year, string month, string day)
{
    // Set up archives directory
    if (!Directory.Exists(archivesDir))
    {
        Directory.CreateDirectory(archivesDir);
    }
    if (!Directory.Exists(Path.Combine(archivesDir, year)))
    {
        Directory.CreateDirectory(Path.Combine(archivesDir, year));
    }
    if (!Directory.Exists(Path.Combine(archivesDir, year, month)))
    {
        Directory.CreateDirectory(Path.Combine(archivesDir, year, month));
    }

    // Set up output directory
    if (!Directory.Exists(outputsDir))
    {
        Directory.CreateDirectory(outputsDir);
    }
    if (!Directory.Exists(Path.Combine(outputsDir, year)))
    {
        Directory.CreateDirectory(Path.Combine(outputsDir, year));
    }
    if (!Directory.Exists(Path.Combine(outputsDir, year, month)))
    {
        Directory.CreateDirectory(Path.Combine(outputsDir, year, month));
    }
}

bool ExistSummaryForSpecifiedDate(string archivesDir, string year, string month, string day)
{
    var summaryPath = Path.Combine(archivesDir, year, month, $"{day}.md");
    return File.Exists(summaryPath);
}

async ValueTask<PullRequestInfo[]> GetAllPullRequestInfoAsync(DateTimeOffset startTargetDate, DateTimeOffset endTargetDate)
{
    // Target dotnet/runtime.
    const string OWNER = "dotnet";
    const string REPO = "runtime";
    const string FullRepo = $"{OWNER}/{REPO}";

    // Create search request for merged pull requests in the specified date range
    var searchRequest = new SearchIssuesRequest()
    {
        Type = IssueTypeQualifier.PullRequest,
        Repos = [FullRepo],
        State = ItemState.Closed,
        Merged = DateRange.Between(startTargetDate, endTargetDate),
        Is = [IssueIsQualifier.Merged]
    };

    // Set up GitHubClient.
    var githubClient = new GitHubClient(new ProductHeaderValue("PR-Digest.NET"));
    var credentials = new Credentials(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    githubClient.Credentials = credentials;

    var searchIssueResult = await githubClient.Search.SearchIssues(searchRequest);
    if (searchIssueResult.Items.Count == 0)
    {
        return [];
    }

    var pullRequestInfos = new PullRequestInfo[searchIssueResult.Items.Count];
    for (var i = 0; i < searchIssueResult.Items.Count; i++)
    {
        var pr = searchIssueResult.Items[i];
        var pullRequestTask = githubClient.PullRequest.Get(OWNER, REPO, pr.Number);
        var filesTask = githubClient.PullRequest.Files(OWNER, REPO, pr.Number);
        var issueCommentsTask = githubClient.Issue.Comment.GetAllForIssue(OWNER, REPO, pr.Number);
        var reviewsTask = githubClient.PullRequest.Review.GetAll(OWNER, REPO, pr.Number);

        pullRequestInfos[i] = new PullRequestInfo
        {
            Issue = pr,
            PullRequest = await pullRequestTask,
            Files = await filesTask,
            IssueComments = await issueCommentsTask,
            Reviews = await reviewsTask,
        };
    }

    return pullRequestInfos;
}

async ValueTask<string> SummarizePullRequestAsync(PullRequestInfo[] pullRequestInfos)
{
    // Generate HTML content for each pull request using Anthropic API.
    try
    {
        // Configures ANTHROPIC_API_KEY.
        AnthropicClient anthropicClient = new();

        var markdownlBuilder = new StringBuilder();
        var tableOfContentsBuilder = new StringBuilder();
        tableOfContentsBuilder.AppendLine("### 目次 {#table-of-contents}");

        var index = 1;
        var separator = Environment.NewLine + "---" + Environment.NewLine;
        foreach (var pr in pullRequestInfos)
        {
            MessageCreateParams parameters = new()
            {
                MaxTokens = 1024,
                Model = Model.ClaudeHaiku4_5, // Claude Haiku 4.5
                System = new MessageCreateParamsSystem([new() { Text = PromptGenerator.SystemPrompt }]),
                Messages = [new() { Role = Role.User, Content = PromptGenerator.GeneratePrompt(pr) }],
            };

            var message = await anthropicClient
                .WithOptions(options => options with
                {
                    Timeout = TimeSpan.FromMinutes(5),
                    MaxRetries = 3,
                }
                )
                .Messages.Create(parameters);

            var llmOutput = "";
            foreach (var content in message.Content)
            {
                if (content.TryPickText(out var textBlock))
                {
                    llmOutput += textBlock.Text;
                }
            }

            tableOfContentsBuilder.AppendLine($"{index++}. [#{pr.Issue.Number} {pr.Issue.Title}](#{pr.Issue.Number})");

            var labels = pr.PullRequest.Labels;
            var labelText = labels.Count > 0 ?
                string.Join(" ", labels.Select(label => $"<span style=\"background-color: #{label.Color}; color: #000000; display: inline-block; padding: 0 7px; font-size:12px; font-weight:500; line-height:18px; border-radius:2em; border:1px solid transparent; white-space:nowrap; cursor:default;\">{label.Name}</span>")) :
                "指定なし";

            var prHeader = $$"""
### [#{{pr.Issue.Number}}]({{pr.Issue.HtmlUrl}}) {{pr.Issue.Title}} {#{{pr.Issue.Number}}}
- 作成者: [@{{pr.Issue.User.Login}}]({{pr.Issue.User.HtmlUrl}})
- 作成日時: {{pr.Issue.CreatedAt:yyyy年MM月dd日 HH:mm:ss}}(UTC)
- マージ日時: {{pr.PullRequest.MergedAt:yyyy年MM月dd日 HH:mm:ss}}(UTC)
- ラベル: {{labelText}}

""";
            markdownlBuilder.AppendLine(prHeader + llmOutput);
            markdownlBuilder.Append(separator);
        }

        return $"{tableOfContentsBuilder}{separator}{markdownlBuilder}";
    }
    catch (AnthropicRateLimitException)
    {
        Console.WriteLine("[ERROR] Anthropic API Rate limit exceeded.");
    }
    catch (AnthropicBadRequestException)
    {
        Console.WriteLine("[ERROR] Credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits.");
    }

    return "";
}

async ValueTask CreateHtml(string archivesDir, string outputsDir)
{
    // set up archives directory
    if (!Directory.Exists(archivesDir))
    {
        Directory.CreateDirectory(archivesDir);
    }

    // set up output directory
    if (!Directory.Exists(outputsDir))
    {
        Directory.CreateDirectory(outputsDir);
    }

    foreach (var yearDirs in Directory.GetDirectories(archivesDir))
    {
        var year = Path.GetFileName(yearDirs);
        if (!Directory.Exists(Path.Combine(outputsDir, year)))
        {
            Directory.CreateDirectory(Path.Combine(outputsDir, year));
        }

        foreach (var monthDirss in Directory.GetDirectories(yearDirs))
        {
            var month = Path.GetFileName(monthDirss);
            if (!Directory.Exists(Path.Combine(outputsDir, year, month)))
            {
                Directory.CreateDirectory(Path.Combine(outputsDir, year, month));
            }

            await Parallel.ForEachAsync(Directory.GetFiles(monthDirss, "*.md"), async (dayFiles, _) =>
            {
                var day = Path.GetFileNameWithoutExtension(dayFiles);
                var markdown = await File.ReadAllTextAsync(dayFiles);

                // ./yyyy/mm/dd.html
                var html = HtmlGenereator.GenerateHtmlFromMarkdown($"{year}年{month}月{day}日", markdown);
                await File.WriteAllTextAsync(Path.Combine(outputsDir, year, month, $"{day}.html"), html);
            });
        }
    }

    // set up index.html
    await File.WriteAllTextAsync(Path.Combine(outputsDir, "index.html"), HtmlGenereator.GenerateIndex(archivesDir, outputsDir));
}

internal sealed class PullRequestInfo
{
    public required Issue Issue { get; init; }

    public required PullRequest PullRequest { get; init; }

    public required IReadOnlyList<PullRequestFile> Files { get; init; }

    public required IReadOnlyList<IssueComment> IssueComments { get; init; }

    public required IReadOnlyList<PullRequestReview> Reviews { get; init; }
}

