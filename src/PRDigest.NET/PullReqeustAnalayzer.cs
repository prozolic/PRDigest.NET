using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Runtime.InteropServices;

namespace PRDigest.NET;

internal static class PullReqeustAnalayzer
{
    public static AnalayzerResult Analayze(MarkdownDocument document)
    {
        var tableOfContents = false;
        var pullRequestTotalCount = 0;
        var pullRequestCountForBot = 0;
        HeadingBlock? nextPrNumber = null;
        HashSet<string>? prNumberTable = null;
        Dictionary<string, List<HeadingBlock>>? labelTable = new();
        Dictionary<string, string>? labelColorMap = new();
        List<HeadingBlock> botPullRequestHeadings = new();
        List<HeadingBlock> communityPrHeadings = new();

        foreach (var block in document)
        {
            if (tableOfContents && block is HeadingBlock headingBlock)
            {
                var link = headingBlock.Inline?.Descendants<LinkInline>().FirstOrDefault()?.FirstChild;
                if (prNumberTable!.TryGetValue(((link as LiteralInline)?.Content.ToString() ?? "Notfound"), out var prNumber))
                {
                    nextPrNumber = headingBlock;
                }
            }
            else if (block is ListBlock listBlock)
            {
                if (tableOfContents && nextPrNumber is not null)
                {
                    // pullRequestInfo is 4 item.
                    // 0: User
                    // 1: Created at
                    // 2: Merged at
                    // 3: Labels
                    var pullRequestInfo = listBlock.Descendants<ListItemBlock>().ToArray();

                    var userBlock = pullRequestInfo[0];
                    var user = userBlock?.Descendants<LiteralInline>().Skip(1).FirstOrDefault();

                    if (user is not null)
                    {
                        var userName = user.Content.ToString().Trim();

                        // check ..[bot].. or @Copilot to count bot PRs
                        if (userName.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ||
                            userName.IndexOf("@Copilot", StringComparison.OrdinalIgnoreCase) > -1)
                        {
                            pullRequestCountForBot++;
                            botPullRequestHeadings.Add(nextPrNumber);
                        }
                        else
                        {
                            communityPrHeadings.Add(nextPrNumber);
                        }
                    }
                    else
                    {
                        communityPrHeadings.Add(nextPrNumber);
                    }

                    var labelBlock = pullRequestInfo[3];
                    var labels = labelBlock?.Descendants<LiteralInline>().Where(l => {
                                var labelText = l.Content.ToString();
                                return !string.IsNullOrWhiteSpace(labelText) && !labelText.Contains("ラベル");
                            });

                    foreach (var label in labels ?? [])
                    {
                        ref var prList = ref CollectionsMarshal.GetValueRefOrAddDefault(labelTable, label.ToString(), out var _);
                        prList ??= new();
                        prList.Add(nextPrNumber);
                    }

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

                    nextPrNumber = null;
                }
                else if (!tableOfContents)
                {
                    foreach (var listItemBlock in listBlock.Descendants<ListItemBlock>())
                    {
                        pullRequestTotalCount++;
                        var prNumber = listItemBlock.Descendants<LinkInline>().FirstOrDefault();
                        if (prNumber is not null)
                        {
                            prNumberTable ??= new HashSet<string>();
                            prNumberTable.Add(prNumber?.Url?.Trim() ?? "");
                        }
                    }
                    tableOfContents = true;
                }
            }
        }


        return new AnalayzerResult
        {
            PullRequestTotalCount = pullRequestTotalCount,
            PullRequestCountForBot = pullRequestCountForBot,
            LabelInfo = labelTable,
            LabelColorMap = labelColorMap,
            BotPullRequestHeadings = botPullRequestHeadings,
            CommunityPullRequestHeadings = communityPrHeadings
        };
    }

    public ref struct AnalayzerResult
    {
        public int PullRequestTotalCount;
        public int PullRequestCountForBot;
        public Dictionary<string, List<HeadingBlock>>? LabelInfo;
        public Dictionary<string, string>? LabelColorMap;
        public List<HeadingBlock>? BotPullRequestHeadings;
        public List<HeadingBlock> CommunityPullRequestHeadings;

        public readonly int LabelCount => LabelInfo?.Count ?? 0;
        public ReadOnlySpan<HeadingBlock> CommunityPullRequestHeadingSpan => CollectionsMarshal.AsSpan(CommunityPullRequestHeadings);
    }
}