using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Octokit;
using PRDigest.NET;
using System.Runtime.InteropServices;
using System.Text;
using Anthropic.Models.Messages.Batches;

if (args.Length == 0) return;

var startTime = TimeProvider.System.GetTimestamp();

var archivesDir = args[0];
var outputsDir = args[1];
var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };

// Summarization settings shared by the Messages API and the Message Batches API.
// Thinking is turned off with "between_tools" (Sonnet 5.5 rejects "disabled"), so max_tokens covers only the summary.
const int SummaryMaxTokens = 1024;
const Model summaryModel = Model.ClaudeSonnet5_5; // Claude Sonnet 5.5

if (args.Length == 3 && args[2] == "-g")
{
    // generate current day's PR markdown and HTML
    await SummarizeCurrentPullRequestAndCreate(archivesDir, outputsDir);
}

// Convert all markdown files to HTML. Every archive is parsed and analyzed exactly once in this
// pass, and everything below (RSS, label pages, monthly pages) works from the returned analyses.
var analyzedArchives = await CreateHtml(archivesDir, outputsDir);

// (Re)create RSS feed from the latest analyzed archives
await CreateRss(outputsDir, analyzedArchives);

// (Re)create the label index page and per-label PR list pages
await CreateLabelPageHtml(outputsDir, analyzedArchives);

// (Re)create the monthly digest page for every month
await CreateMonthlyPageHtml(outputsDir, analyzedArchives);

// (Re)create the privacy policy page linked from every page's footer
await CreatePrivacyPolicyHtml(outputsDir);

// end
var endTime = TimeProvider.System.GetTimestamp();
Console.WriteLine($"Total elapsed time: {TimeProvider.System.GetElapsedTime(startTime, endTime).TotalSeconds} seconds.");


async ValueTask SummarizeCurrentPullRequestAndCreate(string archivesDir, string outputsDir)
{
    // Target dotnet/runtime.
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
        Console.WriteLine($"There were no PRs merged into {Constants.FullRepository} between {startTargetDate:yyyy/MM/dd HH:mm:ss} and {endTargetDate:yyyy/MM/dd HH:mm:ss}.");
        return;
    }
    Console.WriteLine($"{pullRequestInfos.Length} pull requests into {Constants.FullRepository} were merged between {startTargetDate:yyyy/MM/dd HH:mm:ss} and {endTargetDate:yyyy/MM/dd HH:mm:ss}.");

    // Generate HTML content for each pull request using Anthropic API.
    // Many PRs (weekdays: 16-56) go through the Message Batches API at half the price.
    // A few PRs (mostly weekends) are summarized one by one: the saving is too small to wait for a batch.
    const int BatchThreshold = 20;
    var markdown = pullRequestInfos.Length >= BatchThreshold
        ? await SummarizePullRequestWithBatchAsync(pullRequestInfos)
        : await SummarizePullRequestAsync(pullRequestInfos);
    if (string.IsNullOrEmpty(markdown)) return;

    // Save markdown and HTML files.
    var html = HtmlGenerator.GenerateHtmlFromMarkdown(year, month, day, markdown);
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
    // Create search request for merged pull requests in the specified date range
    var searchRequest = new SearchIssuesRequest()
    {
        Type = IssueTypeQualifier.PullRequest,
        Repos = [Constants.FullRepository],
        State = ItemState.Closed,
        Merged = DateRange.Between(startTargetDate, endTargetDate),
        Is = [IssueIsQualifier.Merged]
    };

    // Set up GitHubClient.
    var githubClient = new GitHubClient(new ProductHeaderValue("PR-Digest.NET"));
    var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (string.IsNullOrEmpty(githubToken))
    {
        throw new InvalidOperationException("GitHub token is not set.");
    }

    var credentials = new Credentials(githubToken);
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
        var pullRequestTask = githubClient.PullRequest.Get(Constants.Owner, Constants.Repository, pr.Number);
        var filesTask = githubClient.PullRequest.Files(Constants.Owner, Constants.Repository, pr.Number);
        var issueCommentsTask = githubClient.Issue.Comment.GetAllForIssue(Constants.Owner, Constants.Repository, pr.Number);
        var reviewsTask = githubClient.PullRequest.Review.GetAll(Constants.Owner, Constants.Repository, pr.Number);

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
    var llmOutputs = new string?[pullRequestInfos.Length];
    await SummarizeSequentiallyAsync(CreateAnthropicClient(), pullRequestInfos, llmOutputs);
    return CreateMarkdown(pullRequestInfos, llmOutputs);
}

async ValueTask<string> SummarizePullRequestWithBatchAsync(PullRequestInfo[] pullRequestInfos)
{
    // Most batches end within an hour, but collect-prs.yml caps the job (timeout-minutes),
    // so give up after this rather than publish a digest built from a partly finished batch.
    var batchTimeout = TimeSpan.FromMinutes(40);
    var pollingInterval = TimeSpan.FromSeconds(30);

    var client = CreateAnthropicClient();

    // custom_id must match ^[a-zA-Z0-9_-]{1,64}$.
    var indexByCustomId = new Dictionary<string, int>(pullRequestInfos.Length);
    var requests = new Request[pullRequestInfos.Length];

    for (var i = 0; i < pullRequestInfos.Length; i++)
    {
        var customId = $"pr-{pullRequestInfos[i].Issue.Number}";
        indexByCustomId[customId] = i;
        requests[i] = new Request
        {
            CustomID = customId,
            Params = new Params
            {
                MaxTokens = SummaryMaxTokens,
                Model = summaryModel,
                Thinking = new ThinkingConfigBetweenTools(),
                System = new ParamsSystem([new TextBlockParam { Text = PromptGenerator.SystemPrompt }]),
                Messages = [new() { Role = Role.User, Content = PromptGenerator.GeneratePrompt(pullRequestInfos[i]) }],
            },
        };
    }

    var llmOutputs = new string?[pullRequestInfos.Length];
    try
    {
        var batch = await client.Messages.Batches.Create(new BatchCreateParams { Requests = requests });
        Console.WriteLine($"[INFO] Batch {batch.ID} created with {requests.Length} requests.");

        var startTime = TimeProvider.System.GetTimestamp();
        while (batch.ProcessingStatus != ProcessingStatus.Ended)
        {
            if (TimeProvider.System.GetElapsedTime(startTime) > batchTimeout)
            {
                // Cancel so that the requests not yet processed are neither run nor billed, then fail the run:
                // no markdown is written, so the day can be summarized again from scratch.
                Console.WriteLine($"[ERROR] Batch {batch.ID} did not end within {batchTimeout.TotalMinutes} minutes. Canceling it.");
                await client.Messages.Batches.Cancel(batch.ID);
                throw new TimeoutException($"Batch {batch.ID} did not end within {batchTimeout.TotalMinutes} minutes.");
            }

            await Task.Delay(pollingInterval);
            batch = await client.Messages.Batches.Retrieve(batch.ID);

            // request_counts other than processing stay 0 until the whole batch ends, so only processing is worth logging here.
            if (batch.ProcessingStatus != ProcessingStatus.Ended)
            {
                Console.WriteLine($"[INFO] Batch {batch.ID} is still processing {batch.RequestCounts.Processing} requests.");
            }
        }

        // Processing time as measured by the API (ended_at - created_at), independent of the polling interval.
        var counts = batch.RequestCounts;
        var processingTime = batch.EndedAt is { } endedAt ? $"{(endedAt - batch.CreatedAt).TotalSeconds:F0}s" : "unknown";
        Console.WriteLine($"[INFO] Batch {batch.ID} ended. processing-time:{processingTime} succeeded:{counts.Succeeded} errored:{counts.Errored} canceled:{counts.Canceled} expired:{counts.Expired}");

        await foreach (var response in client.Messages.Batches.ResultsStreaming(batch.ID))
        {
            if (!indexByCustomId.TryGetValue(response.CustomID, out var i)) continue;

            if (response.Result.TryPickSucceeded(out var succeeded))
            {
                var message = succeeded.Message;
                Console.WriteLine($"[INFO] #{pullRequestInfos[i].Issue.Number} input-token:{message.Usage.InputTokens} output-token:{message.Usage.OutputTokens}");
                llmOutputs[i] = ExtractText(message);
            }
            else if (response.Result.TryPickErrored(out var errored))
            {
                Console.WriteLine($"[WARN] #{pullRequestInfos[i].Issue.Number} errored in batch: {errored.Error.Error}");
            }
            else if (response.Result.TryPickExpired(out _))
            {
                Console.WriteLine($"[WARN] #{pullRequestInfos[i].Issue.Number} expired in batch.");
            }
            else if (response.Result.TryPickCanceled(out _))
            {
                Console.WriteLine($"[WARN] #{pullRequestInfos[i].Issue.Number} canceled in batch.");
            }
        }
    }
    catch (AnthropicRateLimitException rle)
    {
        Console.WriteLine($"[ERROR] AnthropicRateLimitException: {rle.StatusCode}");
        throw;
    }
    catch (AnthropicBadRequestException bre)
    {
        Console.WriteLine($"[ERROR] AnthropicBadRequestException: {bre.StatusCode}");
        throw;
    }

    // Fills only the entries the batch did not produce (requests that errored in it).
    await SummarizeSequentiallyAsync(client, pullRequestInfos, llmOutputs);
    return CreateMarkdown(pullRequestInfos, llmOutputs);
}

async ValueTask SummarizeSequentiallyAsync(IAnthropicClient client, PullRequestInfo[] pullRequestInfos, string?[] llmOutputs)
{
    var totalInputTokensPerMinute = 0L;

    try
    {
        for (var i = 0; i < pullRequestInfos.Length; i++)
        {
            if (llmOutputs[i] is not null) continue;

            var pr = pullRequestInfos[i];
            MessageCreateParams parameters = new()
            {
                MaxTokens = SummaryMaxTokens,
                Model = summaryModel,
                Thinking = new ThinkingConfigBetweenTools(),
                System = new MessageCreateParamsSystem([new() { Text = PromptGenerator.SystemPrompt }]),
                Messages = [new() { Role = Role.User, Content = PromptGenerator.GeneratePrompt(pr) }],
            };

            var message = await client.Messages.Create(parameters);

            Console.WriteLine($"[INFO] #{pr.Issue.Number} input-token:{message.Usage.InputTokens} output-token:{message.Usage.OutputTokens}");
            llmOutputs[i] = ExtractText(message);

            totalInputTokensPerMinute += message.Usage.InputTokens;

            // Since input tokens are variable, wait if it exceeds 30,000 tokens per minute
            if (totalInputTokensPerMinute >= 30000)
            {
                totalInputTokensPerMinute = 0;
                await Task.Delay(1000 * 60); // wait for 1 minute
            }
        }
    }
    catch (AnthropicRateLimitException rle)
    {
        Console.WriteLine($"[ERROR] AnthropicRateLimitException: {rle.StatusCode}");
        throw;
    }
    catch (AnthropicBadRequestException bre)
    {
        Console.WriteLine($"[ERROR] AnthropicBadRequestException: {bre.StatusCode}");
        throw;
    }
}

IAnthropicClient CreateAnthropicClient()
{
    // Configures ANTHROPIC_API_KEY.
    AnthropicClient anthropicClient = new();
    return anthropicClient.WithOptions(options => options with
    {
        Timeout = TimeSpan.FromMinutes(5),
        MaxRetries = 3,
    });
}

string ExtractText(Message message)
{
    var builder = new StringBuilder();
    foreach (var content in message.Content)
    {
        if (content.TryPickText(out var textBlock))
        {
            builder.Append(textBlock.Text);
        }
    }
    return builder.ToString();
}

// Builds the daily markdown (table of contents + one section per PR) in the original PR order.
string CreateMarkdown(PullRequestInfo[] pullRequestInfos, string?[] llmOutputs)
{
    var markdownlBuilder = new StringBuilder();
    var tableOfContentsBuilder = new StringBuilder();
    tableOfContentsBuilder.AppendLine("### 目次 {#table-of-contents}");

    var separator = Environment.NewLine + "---" + Environment.NewLine;

    for (var i = 0; i < pullRequestInfos.Length; i++)
    {
        var pr = pullRequestInfos[i];
        var title = TitleHelper.EscapedTitle(pr.Issue.Title);
        tableOfContentsBuilder.AppendLine($"{i + 1}. [#{pr.Issue.Number} {title}](#{pr.Issue.Number})");

        var labels = pr.PullRequest.Labels;
        var labelText = labels.Count > 0 ?
            string.Join(" ", labels.Select(label => $"<a style=\"text-decoration:none;\" href=\"../../labels/{HtmlGenerator.SanitizeLabelForPath(label.Name)}/index.html\"><span style=\"background-color: #{label.Color}; color: {GitHubLabalColor.GetFontColor(label.Color)}; display: inline-block; padding: 0 7px; font-size:12px; font-weight:500; line-height:18px; border-radius:2em; border:1px solid transparent;\">{label.Name}</span></a>")) :
            "指定なし";

        var prHeader = $$"""
### [#{{pr.Issue.Number}}]({{pr.Issue.HtmlUrl}}) {{title}} {#{{pr.Issue.Number}}}
- 作成者: [@{{pr.Issue.User.Login}}]({{pr.Issue.User.HtmlUrl}})
- 作成日時: {{pr.Issue.CreatedAt:yyyy年MM月dd日 HH:mm:ss}}(UTC)
- マージ日時: {{pr.PullRequest.MergedAt:yyyy年MM月dd日 HH:mm:ss}}(UTC)
- ラベル: {{labelText}}

""";
        markdownlBuilder.AppendLine(prHeader + llmOutputs[i]);
        markdownlBuilder.Append(separator);
    }

    return $"{tableOfContentsBuilder}{separator}{markdownlBuilder}";
}

// analyzed is newest first, so the feed is simply its first MaxDays entries.
async ValueTask CreateRss(string outputsDir, ArchiveAnalysis[] analyzed)
{
    const int MaxDays = 3;

    if (analyzed.Length == 0) return;

    var rssContent = RssFeedGenerator.Generate(analyzed.AsSpan(0, Math.Min(MaxDays, analyzed.Length)));
    await File.WriteAllTextAsync(Path.Combine(outputsDir, $"feed.xml"), rssContent);
}

async ValueTask<ArchiveAnalysis[]> CreateHtml(string archivesDir, string outputsDir)
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

    var comparer = StringComparerOptions.DefaultComparer;

    var archives = new List<(string Year, string Month, string Day, string Path)>(512);
    foreach (var yearDir in Directory.EnumerateDirectories(archivesDir).OrderDescending(comparer))
    {
        var year = Path.GetFileName(yearDir);
        foreach (var monthDir in Directory.EnumerateDirectories(yearDir).OrderDescending(comparer))
        {
            var month = Path.GetFileName(monthDir);

            // Created up front: the parallel loop below only writes files.
            Directory.CreateDirectory(Path.Combine(outputsDir, year, month));

            foreach (var mdFilePath in Directory.EnumerateFiles(monthDir, "*.md").OrderDescending(comparer))
            {
                var day = Path.GetFileNameWithoutExtension(mdFilePath);
                archives.Add((year, month, day, mdFilePath));
            }
        }
    }

    var analyzed = new ArchiveAnalysis[archives.Count];
    await Parallel.ForEachAsync(Enumerable.Range(0, archives.Count), parallelOptions, async (i, cancellationToken) =>
    {
        var (year, month, day, path) = archives[i];
        var markdown = await File.ReadAllTextAsync(path, cancellationToken);

        // ./yyyy/mm/dd.html
        var html = HtmlGenerator.GenerateHtmlFromMarkdown(year, month, day, markdown, out var analysis);
        analyzed[i] = new ArchiveAnalysis(year, month, day, analysis);
        await File.WriteAllTextAsync(Path.Combine(outputsDir, year, month, $"{day}.html"), html, cancellationToken);
    });

    // set up index.html
    await File.WriteAllTextAsync(Path.Combine(outputsDir, "index.html"), HtmlGenerator.GenerateIndex(outputsDir, analyzed));

    return analyzed;
}

async ValueTask CreateMonthlyPageHtml(string outputsDir, ArchiveAnalysis[] analyzed)
{
    const int MaxDays = 31;

    var initialCapacity = Math.Max(4, (analyzed.Length / 30) + 2);
    var months = new Dictionary<(string Year, string Month), List<(string Day, PullRequestAnalyzer.AnalysisResults Result)>>(initialCapacity);
    foreach (var archive in analyzed)
    {
        ref var days = ref CollectionsMarshal.GetValueRefOrAddDefault(months, (archive.Year, archive.Month), out var exists);
        if (!exists)
        {
            days = new List<(string, PullRequestAnalyzer.AnalysisResults)>(MaxDays);
        }
        days!.Add((archive.Day, archive.Result));
    }

    foreach (var ((year, month), days) in months)
    {
        // The page renders the days ascending (the bar chart) and descending (the PR list).
        days.Sort(static (x, y) => StringComparerOptions.DefaultComparer.Compare(x.Day, y.Day));

        var monthDir = Path.Combine(outputsDir, year, month);
        if (!Directory.Exists(monthDir))
        {
            Directory.CreateDirectory(monthDir);
        }

        var html = HtmlGenerator.GenerateMonthlyPageHtml(year, month, CollectionsMarshal.AsSpan(days));
        await File.WriteAllTextAsync(Path.Combine(monthDir, "index.html"), html);
    }
}

async ValueTask CreateLabelPageHtml(string outputsDir, ArchiveAnalysis[] analyzed)
{
    var labelTable = new Dictionary<string, LabelPullRequestInfo>(256);

    for (var i = 0; i < analyzed.Length; i++)
    {
        var target = $"{analyzed[i].Year}/{analyzed[i].Month}/{analyzed[i].Day}";
        var analyzerResult = analyzed[i].Result;

        foreach (var (label, metadata) in analyzerResult.LabelMap)
        {
            ref var aggregate = ref CollectionsMarshal.GetValueRefOrAddDefault(labelTable, label, out var exists);
            if (!exists)
            {
                aggregate = new LabelPullRequestInfo();
            }

            // Adopt the first color we encounter for the label (colors are stable per label).
            if (string.IsNullOrEmpty(aggregate!.Color) && analyzerResult.LabelColorGroups.TryGetValue(label, out var color))
            {
                aggregate.Color = color;
            }

            foreach (var m in metadata)
            {
                aggregate.Entries.Add((target, m));
            }
        }
    }

    // set up labels directory: outputs/labels
    var labelsDir = Path.Combine(outputsDir, "labels");
    if (!Directory.Exists(labelsDir))
    {
        Directory.CreateDirectory(labelsDir);
    }

    // outputs/labels/index.html : every label as a badge (with PR count) linking to its page
    await File.WriteAllTextAsync(Path.Combine(labelsDir, "index.html"), HtmlGenerator.GenerateLabelIndexHtml(labelTable));

    // outputs/labels/{sanitized}/index.html
    await Parallel.ForEachAsync(labelTable, parallelOptions, async (key, _) =>
    {
        var label = key.Key;
        var info = key.Value;
        var labelDir = Path.Combine(labelsDir, HtmlGenerator.SanitizeLabelForPath(label));
        if (!Directory.Exists(labelDir))
        {
            Directory.CreateDirectory(labelDir);
        }
        await File.WriteAllTextAsync(Path.Combine(labelDir, "index.html"), HtmlGenerator.GenerateLabelPageHtml(label, info));
    });
}

async ValueTask CreatePrivacyPolicyHtml(string outputsDir)
{
    await File.WriteAllTextAsync(Path.Combine(outputsDir, "privacy.html"), HtmlGenerator.GeneratePrivacyPolicyHtml());
}

internal sealed class PullRequestInfo
{
    public required Issue Issue { get; init; }

    public required PullRequest PullRequest { get; init; }

    public required IReadOnlyList<PullRequestFile> Files { get; init; }

    public required IReadOnlyList<IssueComment> IssueComments { get; init; }

    public required IReadOnlyList<PullRequestReview> Reviews { get; init; }
}

internal readonly record struct ArchiveAnalysis(string Year, string Month, string Day, PullRequestAnalyzer.AnalysisResults Result);

internal sealed class LabelPullRequestInfo
{
    public string? Color { get; set; }

    public List<(string target, PullRequestAnalyzer.Metadata metadata)> Entries { get; } = [];
}

