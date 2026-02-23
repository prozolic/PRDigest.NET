using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    public static AnalysisResults Analyze(MarkdownDocument document)
    {
        var currentPosition = PullRequestPosition.None;
        var tableOfContents = false;
        var pullRequestTotalCount = 0;
        var pullRequestCountForBot = 0;
        HeadingBlock? nextPullRequestNumber = null;
        HashSet<string>? pullRequestNumberTable = null;
        Dictionary<string, List<Metadata>> labelTable = [];
        Dictionary<string, string> labelColorMap = [];
        List<Metadata> botPullRequestHeadings = [];
        List<Metadata> communityPrHeadings = [];
        Metadata currentMetadata = default;
        Dictionary<string, Summary> pullRequestInfoTable = [];

        foreach (var block in document)
        {
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
                    currentMetadata = GetMetadata(nextPullRequestNumber, labels, mergedAt);

                    foreach (var label in labels ?? [])
                    {
                        ref var prList = ref CollectionsMarshal.GetValueRefOrAddDefault(labelTable, label.ToString(), out var _);
                        prList ??= new List<Metadata>(1);
                        prList.Add(currentMetadata);
                    }

                    var user = metadataList[0]?.Descendants<LiteralInline>().Skip(1).FirstOrDefault();
                    if (user is not null)
                    {
                        var userName = user.Content.ToString().Trim();

                        // check ..[bot].. or @Copilot to count bot PRs
                        if (userName.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ||
                            userName.IndexOf("@Copilot", StringComparison.OrdinalIgnoreCase) > -1)
                        {
                            pullRequestCountForBot++;
                            botPullRequestHeadings.Add(currentMetadata);
                        }
                        else
                        {
                            communityPrHeadings.Add(currentMetadata);
                        }
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
            else if (block is ParagraphBlock paragraphBlock)
            {
                if (currentPosition == PullRequestPosition.Overview)
                {
                    var overviewText = GetOverview(paragraphBlock.Inline);
                    pullRequestInfoTable.TryAdd(currentMetadata.PullRequestNumber, new Summary(overviewText));
                }
                currentPosition = PullRequestPosition.None;
            }
        }

        return new AnalysisResults(
            pullRequestTotalCount,
            pullRequestCountForBot,
            labelTable.ToFrozenDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray()),
            labelColorMap.ToFrozenDictionary(),
            botPullRequestHeadings,
            communityPrHeadings,
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

    private static Metadata GetMetadata(HeadingBlock heading, IEnumerable<LiteralInline>? labels, DateTimeOffset mergedAt)
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

        var anchorId = pullRequestNumber.TrimStart('#');
        var displayText = $"{pullRequestNumber} {titleText.Trim()}";

        return new Metadata(anchorId, displayText, labels?.Select(l => l.ToString()).ToImmutableArray() ?? ImmutableArray<string>.Empty, mergedAt);
    }

    private static string GetOverview(ContainerInline? inline)
    {
        if (inline is null) return "";

        var builder = new DefaultInterpolatedStringHandler(0, 0);
        var child = inline.FirstChild;
        while (child is not null)
        {
            switch (child)
            {
                case LiteralInline literal:
                    builder.AppendLiteral(literal.Content.ToString());
                    break;
                case CodeInline codeInline:
                    builder.AppendLiteral(codeInline.Content);
                    break;
                case LineBreakInline:
                    builder.AppendLiteral("\n");
                    break;
                case LinkInline linkInline:
                    builder.AppendLiteral(GetOverview(linkInline));
                    break;
                case EmphasisInline emphasisInline:
                    builder.AppendLiteral(GetOverview(emphasisInline));
                    break;
            }
            child = child.NextSibling;
        }
        return builder.ToStringAndClear();
    }

    public sealed class AnalysisResults(
        int pullRequestTotalCount,
        int pullRequestCountForBot,
        FrozenDictionary<string, ImmutableArray<Metadata>> labelMap,
        FrozenDictionary<string, string> labelColorMap,
        List<Metadata> botPullRequestMetadata,
        List<Metadata> communityPullRequestMetadata,
        FrozenDictionary<string, Summary> summaryMap)
    {
        public int PullRequestTotalCount => pullRequestTotalCount;
        public int PullRequestCountForBot => pullRequestCountForBot;
        public FrozenDictionary<string, ImmutableArray<Metadata>> LabelMap => labelMap;
        public FrozenDictionary<string, string> LabelColorGroups => labelColorMap;
        public int LabelCount => LabelMap.Count;
        public ReadOnlySpan<Metadata> CommunityPullRequestMetadataSpan => CollectionsMarshal.AsSpan(communityPullRequestMetadata);
        public ReadOnlySpan<Metadata> BotPullRequestMetadataSpan => CollectionsMarshal.AsSpan(botPullRequestMetadata);
        public FrozenDictionary<string, Summary> SummaryMap => summaryMap;
    }

    public readonly struct Summary(string overview)
    {
        public string Overview => overview;
    }

    public readonly struct Metadata(
        string pullRequestNumber,
        string titleText,
        ImmutableArray<string> labels,
        DateTimeOffset mergedAt)
    {
        public string PullRequestNumber => pullRequestNumber;

        public string TitleText => titleText;

        public ImmutableArray<string> Labels => labels;

        public DateTimeOffset MergedAt => mergedAt;
    }
}