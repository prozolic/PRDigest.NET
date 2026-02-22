using Markdig;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using static PRDigest.NET.PullRequestAnalyzer;

namespace PRDigest.NET;

internal static class RssFeedGenerator
{
    public static string Generate(string target, string markdownContent)
    {
        var document = Markdown.Parse(markdownContent, MarkdownOptions.Pipeline);
        var analyzerResult = PullRequestAnalyzer.Analyze(document);

        var rss = $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:atom="http://www.w3.org/2005/Atom" version="2.0">
                <channel>
                    <title>PR Digest.NET</title>
                    <link>https://prozolic.github.io/PRDigest.NET/</link>
                    <description>dotnet/runtimeにマージされたPull RequestをAIで日本語要約</description>
                    <lastBuildDate>{TimeProvider.System.GetUtcNow():R}</lastBuildDate>
                    <atom:link href="https://prozolic.github.io/PRDigest.NET/feed.xml" rel="self" type="application/rss+xml"/>
                    <language>ja</language>
                    <image>
                        <url>https://prozolic.github.io/PRDigest.NET/icon-512.png</url>
                        <title>PR Digest.NET</title>
                        <link>https://prozolic.github.io/PRDigest.NET/</link>
                    </image>
                    <copyright>Copyright © 2025 prozolic</copyright>
                    {GenerateItems(target, analyzerResult)}
                </channel>
            </rss>
            """;

        return rss;
    }

    private static string GenerateItems(string target, PullRequestAnalyzer.AnalysisResults analysisResult)
    {
        var itemBuilder = new DefaultInterpolatedStringHandler(0, 0);
        itemBuilder.AppendLiteral(Environment.NewLine);
        foreach (var metadata in analysisResult.CommunityPullRequestMetadataSpan)
        {
            AppendItem(ref itemBuilder, target, analysisResult.SummaryMap, metadata);
        }
        foreach (var metadata in analysisResult.BotPullRequestMetadataSpan)
        {
            AppendItem(ref itemBuilder, target, analysisResult.SummaryMap, metadata);
        }

        return itemBuilder.ToStringAndClear();


        static void AppendItem(ref DefaultInterpolatedStringHandler builder, string target, FrozenDictionary<string, Summary> summaryGroups, PullRequestAnalyzer.Metadata metadata)
        {
            // Rss feed item format:
            // <item>
            //    <title></title>
            //    <link></link>
            //    <guid isPermaLink="true"></guid>
            //    <pubDate></pubDate>
            //    <description></description>
            // </item>

            builder.AppendLiteral("        <item>");
            builder.AppendLiteral(Environment.NewLine);

            var header = metadata;
            builder.AppendLiteral("            <title><![CDATA[ ");
            builder.AppendLiteral(HtmlEncoder.Default.Encode(header.TitleText));
            builder.AppendLiteral(" ]]></title>");
            builder.AppendLiteral(Environment.NewLine);

            builder.AppendLiteral("            <link>");
            builder.AppendLiteral($"https://prozolic.github.io/PRDigest.NET/");
            builder.AppendLiteral(target);
            builder.AppendLiteral(".html");
            builder.AppendLiteral("#");
            builder.AppendLiteral(header.PullRequestNumber);
            builder.AppendLiteral("</link>");
            builder.AppendLiteral(Environment.NewLine);

            builder.AppendLiteral("            <guid isPermaLink=\"true\">");
            builder.AppendLiteral("https://github.com/dotnet/runtime/pull/");
            builder.AppendLiteral(header.PullRequestNumber);
            builder.AppendLiteral("</guid>");
            builder.AppendLiteral(Environment.NewLine);

            if (DateTimeOffset.TryParseExact(target, "yyyy/MM/dd", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                builder.AppendLiteral("            <pubDate>");
                builder.AppendFormatted(date, format: "R");
                builder.AppendLiteral("</pubDate>");
                builder.AppendLiteral(Environment.NewLine);
            }

            if (summaryGroups.TryGetValue(metadata.PullRequestNumber, out var info))
            {
                builder.AppendLiteral("            <description><![CDATA[ ");
                builder.AppendLiteral(info.Overview);
                builder.AppendLiteral(" ]]></description>");
                builder.AppendLiteral(Environment.NewLine);
            }

            builder.AppendLiteral("        </item>");
            builder.AppendLiteral(Environment.NewLine);
        }
    }
}