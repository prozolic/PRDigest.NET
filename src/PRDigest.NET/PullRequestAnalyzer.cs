using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PRDigest.NET;

internal static class PullRequestAnalyzer
{
    private enum PullRequestPosition
    {
        None,
        Title,
        Metadata,
        Overview,
        FileChanged,
        Performance,
        RelatedIssue,
        Other,
        Unknown
    }

    public static void EscapeStrayHtml(MarkdownDocument document)
    {
        List<HtmlInline>? strayInlines = null;
        List<HtmlBlock>? strayBlocks = null;
        Block? previous = null;

        foreach (var block in document)
        {
            var isMetadataList = block is ListBlock && previous is HeadingBlock { Level: 3 } && IsMetadataList(block);
            previous = block;
            if (isMetadataList) continue;

            if (block is HtmlBlock htmlBlock)
            {
                (strayBlocks ??= []).Add(htmlBlock);
                continue;
            }

            // Descendants<T>() yields nothing when its root is a LeafBlock, so a paragraph or heading
            // is walked from its inline container instead.
            if (block is LeafBlock leaf)
            {
                CollectInlines(leaf.Inline, ref strayInlines);
                continue;
            }

            foreach (var descendant in block.Descendants())
            {
                switch (descendant)
                {
                    case HtmlBlock nested:
                        (strayBlocks ??= []).Add(nested);
                        break;
                    case HtmlInline html when !IsLineBreakTag(html.Tag):
                        (strayInlines ??= []).Add(html);
                        break;
                }
            }
        }

        // Replaced after the walk: the replacements edit the tree the enumerators above are reading.
        if (strayInlines is not null)
        {
            foreach (var html in strayInlines)
            {
                html.ReplaceBy(new LiteralInline(html.Tag));
            }
        }

        if (strayBlocks is not null)
        {
            foreach (var htmlBlock in strayBlocks)
            {
                // Becomes a paragraph holding the raw lines as text, which the renderer escapes.
                var paragraph = new ParagraphBlock { Inline = new ContainerInline() };
                paragraph.Inline.AppendChild(new LiteralInline(htmlBlock.Lines.ToString()));

                var parent = htmlBlock.Parent!;
                var index = parent.IndexOf(htmlBlock);
                parent.RemoveAt(index);
                parent.Insert(index, paragraph);
            }
        }

        static void CollectInlines(ContainerInline? inline, ref List<HtmlInline>? strays)
        {
            if (inline is null)
            {
                return;
            }

            foreach (var html in inline.Descendants<HtmlInline>())
            {
                if (!IsLineBreakTag(html.Tag))
                {
                    (strays ??= []).Add(html);
                }
            }
        }
    }

    private static bool IsMetadataList(Block block)
    {
        // "- 作成者: …" opens the metadata list of every PR; no summary text starts a list item that way.
        if (block is not ListBlock { Count: > 0 } list || list[0] is not ListItemBlock { Count: > 0 } item)
        {
            return false;
        }
        if (item[0] is not ParagraphBlock { Inline.FirstChild: LiteralInline literal }) 
        {
            return false;
        }
        return literal.Content.AsSpan().StartsWith("作成者:", StringComparison.Ordinal);
    }

    private static bool IsLineBreakTag(ReadOnlySpan<char> tag)
    {
        tag = tag.Trim();
        // <br>, <br/>, <br /> are all valid line break tags in HTML.
        return tag.Equals("<br>", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("<br/>", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("<br />", StringComparison.OrdinalIgnoreCase);
    }

    public static AnalysisResults Analyze(MarkdownDocument document)
    {
        var currentPosition = PullRequestPosition.None;
        var tableOfContents = false;
        var pullRequestTotalCount = 0;
        HeadingBlock? nextPullRequestNumber = null;
        HashSet<string>? pullRequestNumberTable = null;
        Dictionary<string, List<Metadata>> labelTable = [];
        Dictionary<string, string> labelColorMap = [];
        List<Metadata> botPullRequestHeadings = [];
        List<Metadata> communityPrHeadings = [];
        List<Metadata> aiAgentPullRequestHeadings = [];
        List<Metadata> allPullRequestHeadings = [];
        Metadata currentMetadata = default;
        Dictionary<string, Summary> pullRequestInfoTable = [];

        // Every block of the current PR's 概要 section, and the renderer that turns them into HTML.
        // The renderer is created on first use and shared by all PRs of the document.
        List<Block>? overviewBlocks = null;
        StringBuilder? overviewHtmlBuffer = null;
        HtmlRenderer? overviewRenderer = null;

        foreach (var block in document)
        {
            // The 概要 section runs until the next heading or the PR separator: a paragraph is often
            // followed by a code block or a list that belongs to the same overview.
            if (currentPosition == PullRequestPosition.Overview)
            {
                if (block is not HeadingBlock and not ThematicBreakBlock)
                {
                    (overviewBlocks ??= new List<Block>(4)).Add(block);
                    continue;
                }

                FlushOverview();
                currentPosition = PullRequestPosition.None;
            }

            if (block is HeadingBlock headingBlock)
            {
                if (tableOfContents && currentPosition != PullRequestPosition.Metadata)
                {
                    var link = headingBlock.Inline?.Descendants<LinkInline>().FirstOrDefault()?.FirstChild;
                    if (pullRequestNumberTable!.TryGetValue(((link as LiteralInline)?.Content.ToString() ?? "Notfound"), out var prNumber))
                    {
                        nextPullRequestNumber = headingBlock;
                    }
                }
                else if (currentPosition == PullRequestPosition.Metadata)
                {
                    var content = headingBlock.Inline?.Descendants<LiteralInline>().FirstOrDefault()?.Content.ToString() ?? "";
                    if (content == "概要")
                    {
                        currentPosition = PullRequestPosition.Overview;
                    }
                    else
                    {
                        currentPosition = PullRequestPosition.Unknown;
                    }
                }
            }
            else if (block is ListBlock listBlock)
            {
                if (tableOfContents && nextPullRequestNumber is not null)
                {
                    // metadataList is 4 items.
                    // 0: User
                    // 1: Created at
                    // 2: Merged at
                    // 3: Labels
                    currentPosition = PullRequestPosition.Metadata;
                    var metadataList = listBlock.Descendants<ListItemBlock>().ToArray();
                    if (metadataList.Length < 4) throw new FormatException($"Expected metadata list length to be at least 4, but got {metadataList.Length}.");

                    var labelBlock = metadataList[3];
                    // Extract label colors from HtmlInline spans
                    if (labelBlock is not null)
                    {
                        int backgroundColorLength = 17; // "background-color:".Length
                        foreach (var htmlInline in labelBlock.Descendants<HtmlInline>())
                        {
                            var tag = htmlInline.Tag;
                            if (tag is null) continue;

                            var tagSpan = tag.AsSpan();
                            if (tagSpan.IndexOf("background-color") > -1)
                            {
                                var bgStart = tagSpan.IndexOf("background-color:", StringComparison.Ordinal);
                                if (bgStart < 0) continue;

                                bgStart += backgroundColorLength;
                                var bgEnd = tagSpan.Slice(bgStart).IndexOf(';');
                                if (bgEnd <= 0) continue;

                                var color = tagSpan[bgStart..(bgStart + bgEnd)].Trim();
                                // Find the label text: the next sibling LiteralInline
                                var nextSibling = htmlInline.NextSibling;
                                while (nextSibling is not null)
                                {
                                    if (nextSibling is LiteralInline literal)
                                    {
                                        var labelName = literal.Content.ToString().Trim();
                                        if (!string.IsNullOrWhiteSpace(labelName) && !labelName.Contains("ラベル"))
                                        {
                                            labelColorMap.TryAdd(labelName, color.ToString());
                                        }
                                        break;
                                    }
                                    nextSibling = nextSibling.NextSibling;
                                }
                            }
                        }
                    }

                    var labels = labelBlock?.Descendants<LiteralInline>().Where(l =>
                    {
                        var labelText = l.Content.ToString();
                        return !string.IsNullOrWhiteSpace(labelText) && !labelText.Contains("ラベル");
                    });

                    var mergedAt = ParseDate(metadataList[2]);
                    var userLiteralInlines = metadataList[0]?.Descendants<LiteralInline>();
                    var userName = userLiteralInlines is not null && userLiteralInlines.Any()
                        ? string.Concat(userLiteralInlines.Select(literal => literal.Content.ToString())).Trim()
                        : "";

                    var authorUrl = metadataList[0]?.Descendants<LinkInline>().FirstOrDefault()?.Url ?? "";
                    var authorLogin = userName;
                    var loginStart = authorLogin.IndexOf(": ", StringComparison.Ordinal);
                    if (loginStart >= 0)
                    {
                        authorLogin = authorLogin[(loginStart + 2)..];
                    }

                    currentMetadata = GetMetadata(nextPullRequestNumber, labels, mergedAt, authorLogin.TrimStart('@'), authorUrl);
                    allPullRequestHeadings.Add(currentMetadata);

                    foreach (var label in labels ?? [])
                    {
                        ref var prList = ref CollectionsMarshal.GetValueRefOrAddDefault(labelTable, label.ToString(), out var _);
                        prList ??= new List<Metadata>(1);
                        prList.Add(currentMetadata);
                    }

                    // check ..[bot].. to count bot PRs, @Copilot to count AI agent PRs
                    if (userName.Length == 0)
                    {
                        communityPrHeadings.Add(currentMetadata);
                    }
                    else if (userName.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
                    {
                        botPullRequestHeadings.Add(currentMetadata);
                    }
                    else if (userName.IndexOf("@Copilot", StringComparison.OrdinalIgnoreCase) > -1)
                    {
                        aiAgentPullRequestHeadings.Add(currentMetadata);
                    }
                    else
                    {
                        communityPrHeadings.Add(currentMetadata);
                    }

                    nextPullRequestNumber = null;
                }
                else if (!tableOfContents)
                {
                    foreach (var listItemBlock in listBlock.Descendants<ListItemBlock>())
                    {
                        pullRequestTotalCount++;
                        var prNumber = listItemBlock.Descendants<LinkInline>().FirstOrDefault();
                        if (prNumber is not null)
                        {
                            pullRequestNumberTable ??= new HashSet<string>();
                            pullRequestNumberTable.Add(prNumber?.Url?.Trim() ?? "");
                        }
                    }
                    tableOfContents = true;
                }
            }
            else if (block is ParagraphBlock)
            {
                currentPosition = PullRequestPosition.None;
            }
        }

        // The last PR of a document may end without a separator.
        if (currentPosition == PullRequestPosition.Overview)
        {
            FlushOverview();
        }

        void FlushOverview()
        {
            if (overviewBlocks is null || overviewBlocks.Count == 0) return;

            if (currentMetadata.PullRequestNumber is not null)
            {
                // Plain text: the lead paragraph only. It is the RSS description and what the monthly
                // page measures to decide whether the card needs clamping.
                var overviewText = overviewBlocks[0] is ParagraphBlock { Inline: not null } lead
                    ? GetOverviewText(lead.Inline)
                    : "";

                var overviewHtml = "";
                if (!currentMetadata.IsBot)
                {
                    if (overviewRenderer is null)
                    {
                        overviewHtmlBuffer = new StringBuilder(1024);
                        overviewRenderer = new HtmlRenderer(new StringWriter(overviewHtmlBuffer));
                        MarkdownOptions.Pipeline.Setup(overviewRenderer);
                    }

                    foreach (var overviewBlock in overviewBlocks)
                    {
                        overviewRenderer.Render(overviewBlock);
                    }
                    overviewRenderer.Writer.Flush();

                    var length = overviewHtmlBuffer!.Length;
                    while (length > 0 && overviewHtmlBuffer[length - 1] == '\n')
                    {
                        length--;
                    }
                    overviewHtml = overviewHtmlBuffer.ToString(0, length);
                    overviewHtmlBuffer.Clear();
                }

                pullRequestInfoTable.TryAdd(currentMetadata.PullRequestNumber, new Summary(overviewText, overviewHtml, overviewBlocks.Count > 1));
            }

            overviewBlocks.Clear();
        }

        return new AnalysisResults(
            pullRequestTotalCount,
            labelTable.ToFrozenDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray()),
            labelColorMap.ToFrozenDictionary(),
            botPullRequestHeadings,
            communityPrHeadings,
            aiAgentPullRequestHeadings,
            allPullRequestHeadings,
            pullRequestInfoTable.ToFrozenDictionary());
    }

    private static DateTimeOffset ParseDate(ListItemBlock block)
    {
        // example: "マージ日時: 2025年12月22日 20:19:50(UTC)"
        var text = string.Concat(block.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

        var textSpan = text.AsSpan();
        var startIndex = textSpan.IndexOf(": ", StringComparison.Ordinal);
        if (startIndex < 0)
            throw new FormatException($"Invalid date format: could not find ': ' separator in '{text}'.");

        var endIndex = textSpan.Slice(startIndex + 2).IndexOf("(UTC)", StringComparison.Ordinal);
        if (endIndex < 0)
            throw new FormatException($"Invalid date format: could not find '(UTC)' separator in '{text}'.");

        var dateTextSpan = textSpan.Slice(startIndex + 2, endIndex);
        if (DateTimeOffset.TryParseExact(dateTextSpan, "yyyy年MM月dd日 HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return result;
        }

        throw new FormatException($"Invalid date format: could not parse date in '{text}'.");
    }

    private static Metadata GetMetadata(HeadingBlock heading, IEnumerable<LiteralInline>? labels, DateTimeOffset mergedAt, string author, string authorUrl)
    {
        var pullRequestNumber = "";
        var titleText = "";

        var inline = heading.Inline?.FirstChild;
        while (inline is not null)
        {
            if (inline is LinkInline linkInline)
            {
                var linkChild = linkInline.FirstChild;
                while (linkChild is not null)
                {
                    if (linkChild is LiteralInline lit)
                    {
                        pullRequestNumber = lit.Content.ToString();
                    }
                    linkChild = linkChild.NextSibling;
                }
            }
            else if (inline is LiteralInline literal)
            {
                titleText += literal.Content.ToString();

                if (literal.NextSibling is LinkDelimiterInline linkDelimiterInline)
                {
                    titleText += linkDelimiterInline.ToLiteral();
                    foreach (var linkChild in linkDelimiterInline.OfType<LiteralInline>())
                    {
                        titleText += linkChild.Content.ToString();
                    }
                }
            }
            else if (inline is CodeInline codeInline)
            {
                titleText += codeInline.Content;
            }
            inline = inline.NextSibling;
        }

        var displayText = $"{pullRequestNumber} {titleText.Trim()}";

        return new Metadata(
            pullRequestNumber.TrimStart('#'),
            displayText,
            labels?.Select(l => l.ToString()).ToImmutableArray() ?? ImmutableArray<string>.Empty,
            mergedAt,
            author,
            authorUrl);
    }

    private static string GetOverviewText(ContainerInline inline)
    {
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        AppendInlineText(ref builder, inline);
        return builder.ToStringAndClear();
    }

    private static void AppendInlineText(ref DefaultInterpolatedStringHandler builder, ContainerInline inline)
    {
        var child = inline.FirstChild;
        while (child is not null)
        {
            switch (child)
            {
                case LiteralInline literal:
                    builder.AppendFormatted(literal.Content.AsSpan());
                    break;
                case CodeInline codeInline:
                    builder.AppendLiteral(codeInline.Content);
                    break;
                case LineBreakInline:
                    builder.AppendLiteral("\n");
                    break;
                case ContainerInline container:
                    AppendInlineText(ref builder, container);
                    break;
            }
            child = child.NextSibling;
        }
    }

    public sealed class AnalysisResults(
        int pullRequestTotalCount,
        FrozenDictionary<string, ImmutableArray<Metadata>> labelMap,
        FrozenDictionary<string, string> labelColorMap,
        List<Metadata> botPullRequestMetadata,
        List<Metadata> communityPullRequestMetadata,
        List<Metadata> agentPullRequestMetadata,
        List<Metadata> allPullRequestMetadata,
        FrozenDictionary<string, Summary> summaryMap)
    {
        public int PullRequestTotalCount => pullRequestTotalCount;

        public int PullRequestCountForCommunity => communityPullRequestMetadata.Count;
        public int PullRequestCountForBot => botPullRequestMetadata.Count;
        public int PullRequestCountForAiAgent => agentPullRequestMetadata.Count;
        public FrozenDictionary<string, ImmutableArray<Metadata>> LabelMap => labelMap;
        public FrozenDictionary<string, string> LabelColorGroups => labelColorMap;
        public int LabelCount => LabelMap.Count;
        public ReadOnlySpan<Metadata> CommunityPullRequestMetadataSpan => CollectionsMarshal.AsSpan(communityPullRequestMetadata);
        public ReadOnlySpan<Metadata> BotPullRequestMetadataSpan => CollectionsMarshal.AsSpan(botPullRequestMetadata);
        public ReadOnlySpan<Metadata> AgentPullRequestMetadataSpan => CollectionsMarshal.AsSpan(agentPullRequestMetadata);
        public ReadOnlySpan<Metadata> AllPullRequestMetadataSpan => CollectionsMarshal.AsSpan(allPullRequestMetadata);
        public FrozenDictionary<string, Summary> SummaryMap => summaryMap;
    }

    public readonly struct Summary(string overview, string overviewHtml, bool hasMoreBlocks)
    {
        public string Overview => overview;

        public string OverviewHtml => overviewHtml;

        public bool HasMoreBlocks => hasMoreBlocks;
    }

    public readonly struct Metadata(
        string pullRequestNumber,
        string titleText,
        ImmutableArray<string> labels,
        DateTimeOffset mergedAt,
        string author,
        string authorUrl)
    {
        public string PullRequestNumber => pullRequestNumber;

        public string TitleText => titleText;

        public ImmutableArray<string> Labels => labels;

        public DateTimeOffset MergedAt => mergedAt;

        public string Author => author;

        public string AuthorUrl => authorUrl;

        public bool IsBot => author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
    }
}