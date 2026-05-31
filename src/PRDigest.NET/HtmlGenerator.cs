using Markdig;
using Markdig.Helpers;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;

namespace PRDigest.NET;

internal static class HtmlGenerator
{
    private static readonly SearchValues<char> AllowedLabelPathChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    private static bool IsYearDirectoryName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 4)
        {
            var nameSpan = name.AsSpan();
            return nameSpan[0].IsDigit() && nameSpan[1].IsDigit() && nameSpan[2].IsDigit() && nameSpan[3].IsDigit();
        }
        return false;
    }

    public static string GenerateIndex(string archivesDir, string outputsDir)
    {
        var comparer = StringComparerOptions.DefaultComparer;

        var yearDirs = Directory.GetDirectories(outputsDir).Where(IsYearDirectoryName).OrderDescending(comparer).ToArray();
        var detailsBuilder = new DefaultInterpolatedStringHandler(0, 0);
        foreach (var yearDir in yearDirs)
        {
            var year = Path.GetFileName(yearDir);
            foreach (var monthDir in Directory.GetDirectories(yearDir).OrderDescending(comparer))
            {
                var month = Path.GetFileName(monthDir);
                detailsBuilder.AppendLiteral("<details>");
                detailsBuilder.AppendLiteral(Environment.NewLine);
                detailsBuilder.AppendLiteral("   <summary>");
                detailsBuilder.AppendLiteral(year);
                detailsBuilder.AppendLiteral("年");
                detailsBuilder.AppendLiteral(month);
                detailsBuilder.AppendLiteral("月");
                detailsBuilder.AppendLiteral("</summary>");
                detailsBuilder.AppendLiteral(Environment.NewLine);
                detailsBuilder.AppendLiteral($"   <ul class=\"daylist\">");
                detailsBuilder.AppendLiteral(Environment.NewLine);

                foreach (var htmlPath in Directory.GetFiles(monthDir, "*.html").Order(comparer))
                {
                    detailsBuilder.AppendLiteral($"     <li class=\"dayitem\"><a href=\"./");
                    detailsBuilder.AppendLiteral(year);
                    detailsBuilder.AppendLiteral("/");
                    detailsBuilder.AppendLiteral(month);
                    detailsBuilder.AppendLiteral("/");
                    detailsBuilder.AppendLiteral(Path.GetFileName(htmlPath));
                    detailsBuilder.AppendLiteral("\">");
                    detailsBuilder.AppendLiteral(year);
                    detailsBuilder.AppendLiteral("年");
                    detailsBuilder.AppendLiteral(month);
                    detailsBuilder.AppendLiteral("月");
                    detailsBuilder.AppendLiteral(Path.GetFileNameWithoutExtension(htmlPath));
                    detailsBuilder.AppendLiteral("日</a> </li>");
                    detailsBuilder.AppendLiteral(Environment.NewLine);
                }

                detailsBuilder.AppendLiteral("   </ul>");
                detailsBuilder.AppendLiteral(Environment.NewLine);
                detailsBuilder.AppendLiteral("</details>");
                detailsBuilder.AppendLiteral(Environment.NewLine);
            }
        }

        var latestPullRequestInfo = "";
        var latestYearDir = yearDirs.Length > 0 ? yearDirs[0] : null;
        if (!string.IsNullOrWhiteSpace(latestYearDir))
        {

            var lastedYear = Path.GetFileName(latestYearDir);
            var lastedMonthDirs = Directory.GetDirectories(latestYearDir!).OrderDescending(comparer).FirstOrDefault();
            var lastedMonth = Path.GetFileName(lastedMonthDirs);
            var lastedDayHtmlPath = Directory.GetFiles(lastedMonthDirs!, "*.html").OrderDescending(comparer).FirstOrDefault();

            var lastedDay = Path.GetFileNameWithoutExtension(lastedDayHtmlPath);
            var latestMarkdownPath = Path.Combine(archivesDir, lastedYear!, lastedMonth!, $"{lastedDay}.md");

            var statsHtml = "";
            if (File.Exists(latestMarkdownPath))
            {
                var markdownContent = File.ReadAllText(latestMarkdownPath);
                var document = Markdown.Parse(markdownContent, MarkdownOptions.Pipeline);
                var analyzerResult = PullRequestAnalyzer.Analyze(document);

                statsHtml = $"""
                                <div class="stats-grid">
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.PullRequestTotalCount}</div>
                                        <div class="stat-label">PR 数（Total）</div>
                                    </div>
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.PullRequestCountForCommunity}</div>
                                        <div class="stat-label">PR 数（Community）</div>
                                    </div>
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.PullRequestCountForAiAgent}</div>
                                        <div class="stat-label">PR 数（AI Agent）</div>
                                    </div>
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.PullRequestCountForBot}</div>
                                        <div class="stat-label">PR 数（Bot）</div>
                                    </div>
                                </div>
                                <div class="stats-label-row">
                                    <div class="stat-card stat-card-label">
                                        <div class="stat-value">{analyzerResult.LabelCount}</div>
                                        <div class="stat-label">ラベルタイプ数</div>
                                    </div>
                                </div>
                """;
            }

            latestPullRequestInfo = $"""
                            <h2>最新のダイジェスト</h2>
                            <p><a href="./{lastedYear}/{lastedMonth}/{Path.GetFileName(lastedDayHtmlPath)}">{lastedYear}年{lastedMonth}月{Path.GetFileNameWithoutExtension(lastedDayHtmlPath)}日</a></p>
                            {statsHtml}
                            <h2>ラベルから探す</h2>
                            <p><a href="./labels/index.html">全ラベル一覧</a></p>
                            <h2>過去の月別ダイジェスト</h2>
                            """;
        }

        return GenerateTemplateHtml(
            title: $"PR Digest.NET", 
            subTitle: "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", 
            content: latestPullRequestInfo + detailsBuilder.ToStringAndClear(), 
            viewScript: GenerateViewScript(),
            floatingTocHtml: "",
            floatingTocScript: "");
    }

    public static string GenerateHtmlFromMarkdown(string startTargetDate, string markdownContent)
    {
        var document = Markdown.Parse(markdownContent, MarkdownOptions.Pipeline);
        var contentHtml = Markdown.ToHtml(document, MarkdownOptions.Pipeline);

        // Split contentHtml into TOC part and PR details part
        // The TOC ends after </ol>, then a <hr /> separates it from PR details
        var contentSpan = contentHtml.AsSpan();
        var tocEndIndex = contentSpan.IndexOf("</ol>", StringComparison.Ordinal);
        ReadOnlySpan<char> tocHtml;
        ReadOnlySpan<char> prDetailsHtml;

        if (tocEndIndex >= 0)
        {
            tocEndIndex += "</ol>".Length;
            var hrIndex = contentSpan[tocEndIndex..].IndexOf("<hr", StringComparison.Ordinal);
            if (hrIndex >= 0)
            {
                tocHtml = contentSpan[..tocEndIndex];
                prDetailsHtml = contentSpan[(tocEndIndex + hrIndex)..];
            }
            else
            {
                tocHtml = contentSpan[..tocEndIndex];
                prDetailsHtml = contentSpan[tocEndIndex..];
            }
        }
        else
        {
            // Fallback: no split possible
            tocHtml = contentHtml;
            prDetailsHtml = "";
        }

        var analyzerResult = PullRequestAnalyzer.Analyze(document);
        var categoryViewHtml = GenerateCategorizedTocHtml(analyzerResult);
        var labelViewHtml = GenerateLabelViewHtml(analyzerResult);

        // Extract <ol> part from tocHtml for floating TOC
        var floatingTocSpan = tocHtml;
        var olStart = floatingTocSpan.IndexOf("<ol>", StringComparison.Ordinal);
        var floatingTocOlHtml = olStart >= 0 ? floatingTocSpan[olStart..].ToString() : "";

        var floatingTocHtml = string.IsNullOrEmpty(floatingTocOlHtml) ? "" : $"""
<div id="floating-toc" class="floating-toc">
  <div class="floating-toc-header">目次</div>
  <nav class="floating-toc-nav">
    {floatingTocOlHtml}
  </nav>
</div>
<div id="toc-backdrop" class="toc-backdrop"></div>
""";

        var content = $"""
      <h2>注意点</h2>
      <p>このページは、<a href="https://github.com/dotnet/runtime">dotnet/runtime</a>リポジトリにマージされたPull Requestを自動的に収集し、その内容をAIが要約した内容を表示しています。そのため、必ずしも正確な要約ではない場合があります。</p>
      <hr>
      <div class="view-tabs">
        <button class="view-tab active" data-view="list">一覧</button>
        <button class="view-tab" data-view="category">カテゴリ別</button>
        <button class="view-tab" data-view="label">ラベル別</button>
      </div>
      <div id="list-view" class="view-panel">
        {tocHtml}
      </div>
      <div id="category-view" class="view-panel" style="display:none">
        {categoryViewHtml}
      </div>
      <div id="label-view" class="view-panel" style="display:none">
        {labelViewHtml}
      </div>
      {prDetailsHtml}
""";

        return GenerateTemplateHtml(
            title: $"Pull Request on {startTargetDate}", 
            subTitle: "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", 
            content: content,
            viewScript: GenerateViewScript(), 
            floatingTocHtml: floatingTocHtml,
            floatingTocScript: GenerateFloatingTocScript());
    }

    private static string GenerateLabelViewHtml(PullRequestAnalyzer.AnalysisResults analyzerResult)
    {
        if (analyzerResult.LabelCount == 0)
            return "<p>ラベル情報がありません。</p>";

        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral("<h3>ラベル別PR一覧</h3>");
        builder.AppendLiteral(Environment.NewLine);

        foreach (var (labelName, metadataList) in analyzerResult.LabelMap.OrderByDescending(kv => kv.Value.Length))
        {
            builder.AppendLiteral("<details class=\"label-group\">");
            builder.AppendLiteral(Environment.NewLine);
            builder.AppendLiteral($"  <summary class=\"label-group-summary\"><span");
            if (analyzerResult.LabelColorGroups.TryGetValue(labelName, out var color))
            {
                builder.AppendLiteral(" style=\"background-color: ");
                builder.AppendLiteral(color);
                builder.AppendLiteral("; color: ");
                builder.AppendLiteral(GitHubLabalColor.GetFontColor(color));
                builder.AppendLiteral("; display: inline-block; padding: 0 7px; font-size: 12px; font-weight: 500; line-height: 18px; border-radius: 2em; border: 1px solid transparent;\"");
            }
            builder.AppendLiteral(">");
            builder.AppendLiteral(labelName);
            builder.AppendLiteral("</span> <span class=\"label-pr-count\">(");
            builder.AppendFormatted(metadataList.Length);
            builder.AppendLiteral(" PRs)</span></summary>");
            builder.AppendFormatted(Environment.NewLine);

            builder.AppendLiteral("  <ol class=\"label-pr-list\">");
            builder.AppendLiteral(Environment.NewLine);

            foreach (var metadata in metadataList)
            {
                AppendHeadingListItem(ref builder, metadata);
            }

            builder.AppendLiteral("  </ol>");
            builder.AppendLiteral(Environment.NewLine);
            builder.AppendLiteral("</details>");
            builder.AppendLiteral(Environment.NewLine);
        }

        return builder.ToStringAndClear();
    }

    private static string GenerateCategorizedTocHtml(PullRequestAnalyzer.AnalysisResults analyzerResult)
    {
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral($"<h3>カテゴリ別PR一覧</h3>");
        builder.AppendLiteral(Environment.NewLine);

        // Community PRs (expanded)
        var communityCount = analyzerResult.CommunityPullRequestMetadataSpan.Length;
        builder.AppendLiteral($"<details class=\"label-group\">");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral($"  <summary class=\"label-group-summary\">Community PRs <span class=\"label-pr-count\">(");
        builder.AppendFormatted(communityCount);
        builder.AppendLiteral(" PRs)</span></summary>");
        builder.AppendFormatted(Environment.NewLine);

        builder.AppendLiteral($"  <ol class=\"label-pr-list\">");
        builder.AppendLiteral(Environment.NewLine);

        foreach (var heading in analyzerResult.CommunityPullRequestMetadataSpan)
        {
            AppendHeadingListItem(ref builder, heading);
        }
        builder.AppendLiteral("  </ol>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("</details>");
        builder.AppendLiteral(Environment.NewLine);

        // AI Agent PRs (collapsed)
        var aiAgentCount = analyzerResult.AgentPullRequestMetadataSpan.Length;
        builder.AppendLiteral("<details class=\"label-group\">");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("  <summary class=\"label-group-summary\">AI Agent PRs <span class=\"label-pr-count\">(");
        builder.AppendFormatted(aiAgentCount);
        builder.AppendLiteral(" PRs)</span></summary>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("  <ol class=\"label-pr-list\">");
        builder.AppendLiteral(Environment.NewLine);
        foreach (var heading in analyzerResult.AgentPullRequestMetadataSpan)
        {
            AppendHeadingListItem(ref builder, heading);
        }
        builder.AppendLiteral("  </ol>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("</details>");
        builder.AppendLiteral(Environment.NewLine);

        // Bot PRs (collapsed)
        var botCount = analyzerResult.BotPullRequestMetadataSpan.Length;
        builder.AppendLiteral("<details class=\"label-group\">");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral($"  <summary class=\"label-group-summary\">Bot PRs <span class=\"label-pr-count\">(");
        builder.AppendFormatted(botCount);
        builder.AppendLiteral(" PRs)</span></summary>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("  <ol class=\"label-pr-list\">");
        builder.AppendLiteral(Environment.NewLine);
        foreach (var heading in analyzerResult.BotPullRequestMetadataSpan)
        {
            AppendHeadingListItem(ref builder, heading);
        }
        builder.AppendLiteral("  </ol>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("</details>");
        builder.AppendLiteral(Environment.NewLine);

        return builder.ToStringAndClear();
    }

    private static void AppendHeadingListItem(ref DefaultInterpolatedStringHandler builder, PullRequestAnalyzer.Metadata metadata)
    {
        var text = HtmlEncoder.Default.Encode(metadata.TitleText);
        builder.AppendLiteral($"    <li><a href=\"#");
        builder.AppendLiteral(metadata.PullRequestNumber);
        builder.AppendLiteral("\">");
        builder.AppendLiteral(text);
        builder.AppendLiteral("</a></li>");
        builder.AppendLiteral(Environment.NewLine);
    }

    public static string GenerateLabelIndexHtml(Dictionary<string, LabelPullRequestInfo> labels)
    {
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral("<p>各ラベルをクリックすると、そのラベルが付いた Pull Request の一覧を表示します。</p>");
        builder.AppendLiteral(Environment.NewLine);

        if (labels.Count == 0)
        {
            builder.AppendLiteral("<p>ラベル情報がありません。</p>");
            return GenerateLabelPage("ラベル一覧", builder.ToStringAndClear());
        }

        builder.AppendLiteral("<table class=\"label-index-table\">");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("  <thead><tr><th class=\"sortable\" data-sort-type=\"text\">ラベル</th><th class=\"sortable\" data-sort-type=\"number\">PR数</th></tr></thead>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("  <tbody>");
        builder.AppendLiteral(Environment.NewLine);
        foreach (var (key, value) in labels.OrderBy(kv => kv.Key))
        {
            var label = key;
            var color = value.Color;
            var count = value.Entries.Count;

            var encodedLabel = HtmlEncoder.Default.Encode(label);
            builder.AppendLiteral("    <tr><td data-sort=\"");
            builder.AppendLiteral(encodedLabel);
            builder.AppendLiteral("\"><a style=\"text-decoration:none;\" href=\"./");
            builder.AppendLiteral(SanitizeLabelForPath(label));
            builder.AppendLiteral("/index.html\"><span");
            AppendIndexBadgeStyle(ref builder, color);
            builder.AppendLiteral(">");
            builder.AppendLiteral(encodedLabel);
            builder.AppendLiteral("</span></a></td><td class=\"label-pr-count\" data-sort=\"");
            builder.AppendFormatted(count);
            builder.AppendLiteral("\">");
            builder.AppendFormatted(count);
            builder.AppendLiteral(" PRs</td></tr>");
            builder.AppendLiteral(Environment.NewLine);
        }
        builder.AppendLiteral("  </tbody>");
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral("</table>");
        builder.AppendLiteral(Environment.NewLine);

        return GenerateLabelPage("ラベル一覧", builder.ToStringAndClear(), GenerateLabelSortScript());
    }

    // Client-side column sorting for the labels/index.html table — no third-party library.
    // Clicking a header reorders the <tbody> rows by that column's data-sort value (text via
    // localeCompare, number via parseFloat) and toggles ascending/descending on repeat clicks.
    private static string GenerateLabelSortScript()
    {
        return """
<script>
document.addEventListener('DOMContentLoaded', function() {
  var table = document.querySelector('table.label-index-table');
  if (!table || !table.tBodies.length) return;
  var tbody = table.tBodies[0];
  var headers = Array.prototype.slice.call(table.querySelectorAll('thead th.sortable'));
  var sortedIndex = 0;   // server pre-sorts by label name ascending
  var ascending = true;

  function cellValue(row, index) {
    var cell = row.cells[index];
    var v = cell.getAttribute('data-sort');
    return v !== null ? v : cell.textContent;
  }

  function sortBy(index, type, asc) {
    var rows = Array.prototype.slice.call(tbody.rows);
    rows.sort(function(a, b) {
      var av = cellValue(a, index), bv = cellValue(b, index);
      var cmp = type === 'number'
        ? (parseFloat(av) || 0) - (parseFloat(bv) || 0)
        : av.localeCompare(bv);
      return asc ? cmp : -cmp;
    });
    var frag = document.createDocumentFragment();
    rows.forEach(function(row) { frag.appendChild(row); });
    tbody.appendChild(frag);
  }

  function updateIndicators() {
    headers.forEach(function(th, index) {
      th.classList.remove('sort-asc', 'sort-desc');
      if (index === sortedIndex) th.classList.add(ascending ? 'sort-asc' : 'sort-desc');
    });
  }

  headers.forEach(function(th, index) {
    th.addEventListener('click', function() {
      var type = th.getAttribute('data-sort-type') || 'text';
      if (sortedIndex === index) {
        ascending = !ascending;
      } else {
        sortedIndex = index;
        ascending = type !== 'number';   // text: ascending first, number: descending first
      }
      sortBy(index, type, ascending);
      updateIndicators();
    });
  });

  updateIndicators();   // reflect the initial (label name ascending) order
});
</script>
""";
    }

    public static string GenerateLabelPageHtml(string label, LabelPullRequestInfo labelAggregate)
    {
        var ordered = labelAggregate.Entries
            .OrderByDescending(static e => e.metadata.MergedAt)
            .ThenByDescending(static e => int.TryParse(e.metadata.PullRequestNumber, out var n) ? n : 0)
            .ToArray();

        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral("<p><a href=\"../index.html\">ラベル一覧へ戻る</a></p>");
        builder.AppendLiteral(Environment.NewLine);

        builder.AppendLiteral("<p>ラベル: ");
        builder.AppendLiteral(HtmlEncoder.Default.Encode(label));
        builder.AppendLiteral(" の Pull Request の一覧を表示します。上から新しい順に表示されています。</p>");
        builder.AppendLiteral(Environment.NewLine);

        builder.AppendLiteral("<ol class=\"label-pr-list\">");
        builder.AppendLiteral(Environment.NewLine);
        foreach (var (target, md) in ordered)
        {
            // Relative link from outputs/labels/{label}/ back to the daily page: ../../yyyy/MM/dd.html#PR番号
            builder.AppendLiteral("  <li><a href=\"../../");
            builder.AppendLiteral(target);
            builder.AppendLiteral(".html#");
            builder.AppendLiteral(md.PullRequestNumber);
            builder.AppendLiteral("\">");
            builder.AppendLiteral(HtmlEncoder.Default.Encode(md.TitleText));
            builder.AppendLiteral("</a></li>");
            builder.AppendLiteral(Environment.NewLine);
        }
        builder.AppendLiteral("</ol>");
        builder.AppendLiteral(Environment.NewLine);

        return GenerateLabelPage($"ラベル: {HtmlEncoder.Default.Encode(label)}", builder.ToStringAndClear(), GenerateScrollToTopHtml());
    }

    public static string SanitizeLabelForPath(string label)
    {
        // Replace every character outside [A-Za-z0-9._-] with '-' so the label is safe as both a
        // directory name and a URL segment (e.g. "Priority:2" -> "Priority-2"). The same result is
        // used for the on-disk folder and for the link to it, keeping path and href in sync.
        if (label.AsSpan().IndexOfAnyExcept(AllowedLabelPathChars) < 0)
            return label;

        return string.Create(label.Length, label, static (span, state) =>
        {
            for (var i = 0; i < state.Length; i++)
            {
                var c = state[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-';
            }
        });
    }

    private static void AppendIndexBadgeStyle(ref DefaultInterpolatedStringHandler builder, string? color)
    {
        if (string.IsNullOrEmpty(color))
            return;

        builder.AppendLiteral(" style=\"background-color: ");
        builder.AppendLiteral(color);
        builder.AppendLiteral("; color: ");
        builder.AppendLiteral(GitHubLabalColor.GetFontColor(color));
        builder.AppendLiteral("; display: inline-block; padding: 0 7px; font-size: 12px; font-weight: 500; line-height: 1.5; border-radius: 0.2em; border: 1px solid transparent;\"");
    }

    private static string GenerateLabelPage(string title, string content, string bodyEndHtml = "")
    {
        return GenerateTemplateHtml(
            title: title,
            subTitle: "dotnet/runtimeにマージされたPull RequestをAIで日本語要約",
            content: content,
            viewScript: bodyEndHtml,
            floatingTocHtml: "",
            floatingTocScript: "");
    }

    private static string GenerateTemplateHtml(string title, string subTitle, string content, string viewScript, string floatingTocHtml, string floatingTocScript)
    {
        return $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
  <!-- Google tag (gtag.js) -->
  <script async src="https://www.googletagmanager.com/gtag/js?id=G-34XNJ13EZY"></script>
  <script>
    window.dataLayer = window.dataLayer || [];
    function gtag(){dataLayer.push(arguments);}
    gtag('js', new Date());
  
    gtag('config', 'G-34XNJ13EZY');
  </script>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{{title}}</title>
  <meta name="description" content="Merged pull request in dotnet/runtime digest." />
  <meta name="author" content="prozolic" />
  <meta name="keywords" content="C#,.NET,pull request,LLM," />
  <meta name="robots" content="index, follow" />
  <meta name="theme-color" content="#03173d" />

  <!-- Open Graph meta tags -->
  <meta property="og:type" content="website" />
  <meta property="og:url" content="https://prozolic.github.io/PRDigest.NET/" />
  <meta property="og:title" content="{{title}}" />
  <meta property="og:site_name" content="PR Digest.NET" />
  <meta property="og:description" content="Merged pull request in dotnet/runtime digest." />
  <meta property="og:image" content="https://prozolic.github.io/PRDigest.NET/icon-512.png" />
  <meta property="og:locale" content="ja_JP" />

  <meta name="twitter:card" content="summary" />

  <link rel="shortcut icon" href="https://prozolic.github.io/PRDigest.NET/favicon.ico" />
  <link rel="icon" type="image/png" sizes="16x16" href="https://prozolic.github.io/PRDigest.NET/icon-512.png" />
  <link rel="icon" type="image/png" sizes="32x32" href="https://prozolic.github.io/PRDigest.NET/icon-512.png" />
  <link rel="icon" type="image/png" sizes="192x192" href="https://prozolic.github.io/PRDigest.NET/icon-512.png" />
  <link rel="icon" type="image/png" sizes="512x512" href="https://prozolic.github.io/PRDigest.NET/icon-512.png" />
  <link rel="alternate" type="application/rss+xml" title="PR Digest.NET" href="https://prozolic.github.io/PRDigest.NET/feed.xml" />

  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
  <link href="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism.min.css" rel="stylesheet">
  <style>
{{GenerateCssStyle()}}
  </style>
</head>
<body>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-core.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/autoloader/prism-autoloader.min.js"></script>
  <nav class="navbar fixtop"> 
    <div class="container">
      <a class="navbarlink" href="https://prozolic.github.io/PRDigest.NET/">PR Digest.NET</a>
      <div style="display: flex; gap: 8px; align-items: center;">
        <!-- Material Design Icons: rss https://pictogrammers.com/library/mdi/icon/rss/ -->
        <a href="https://prozolic.github.io/PRDigest.NET/feed.xml">
          <svg style="filter: invert(1);" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><path d="M6.18 15.64a2.18 2.18 0 0 1 2.18 2.18C8.36 19.01 7.38 20 6.18 20C4.98 20 4 19.01 4 17.82a2.18 2.18 0 0 1 2.18-2.18M4 4.44A15.56 15.56 0 0 1 19.56 20h-2.83A12.73 12.73 0 0 0 4 7.27V4.44m0 5.66a9.9 9.9 0 0 1 9.9 9.9h-2.83A7.07 7.07 0 0 0 4 12.93V10.1z"/></svg>
        </a>
        <!-- GitHub Octicons: mark-github https://github.com/primer/octicons/blob/main/icons/mark-github-24.svg -->
        <a href="https://github.com/prozolic/PRDigest.NET">
          <svg style="filter: invert(1);" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><path d="M12.5.75C6.146.75 1 5.896 1 12.25c0 5.089 3.292 9.387 7.863 10.91.575.101.79-.244.79-.546 0-.273-.014-1.178-.014-2.142-2.889.532-3.636-.704-3.866-1.35-.13-.331-.69-1.352-1.18-1.625-.402-.216-.977-.748-.014-.762.906-.014 1.553.834 1.769 1.179 1.035 1.74 2.688 1.25 3.349.948.1-.747.402-1.25.733-1.538-2.559-.287-5.232-1.279-5.232-5.678 0-1.25.445-2.285 1.178-3.09-.115-.288-.517-1.467.115-3.048 0 0 .963-.302 3.163 1.179.92-.259 1.897-.388 2.875-.388.977 0 1.955.13 2.875.388 2.2-1.495 3.162-1.179 3.162-1.179.633 1.581.23 2.76.115 3.048.733.805 1.179 1.825 1.179 3.09 0 4.413-2.688 5.39-5.247 5.678.417.36.776 1.05.776 2.128 0 1.538-.014 2.774-.014 3.162 0 .302.216.662.79.547C20.709 21.637 24 17.324 24 12.25 24 5.896 18.854.75 12.5.75Z"></path></svg>
        </a>
      </div>
    </div>
  </nav>
  <header class="head">
    <div class="container">
      <div style="text-align: center; width: 100%;">
        <h1 style="margin: 0 0 8px 0; padding: 0; border: none; color: #ffffff; font-size: 36px;">{{title}}</h1>
        <p style="margin: 0; color: #9ca3af; font-size: 16px;">{{subTitle}}</p>
      </div>
    </div>
  </header>
<div class="page">
  <main class="main">
    <div class="content">
      {{content}}
    </div>
  </main>
</div>
<footer>
  <div>
    <p>Copyright &copy; 2025 prozolic</p>
  </div>
</footer>
{{viewScript}}
{{floatingTocHtml}}
{{floatingTocScript}}
</body>
</html>
""";

    }

    private static string GenerateViewScript()
    {
        return """
<button id="scroll-to-top" class="scroll-to-top" type="button" aria-label="ページ上部へ戻る" title="ページ上部へ戻る">
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" aria-hidden="true"><path fill="currentColor" d="M13,20H11V8L5.5,13.5L4.08,12.08L12,4.16L19.92,12.08L18.5,13.5L13,8V20Z"/></svg>
</button>
<script>
document.addEventListener('DOMContentLoaded', function() {
  var tabs = document.querySelectorAll('.view-tab');
  var panels = document.querySelectorAll('.view-panel');
  tabs.forEach(function(tab) {
    tab.addEventListener('click', function() {
      var view = this.getAttribute('data-view');
      tabs.forEach(function(t) { t.classList.remove('active'); });
      this.classList.add('active');
      panels.forEach(function(p) { p.style.display = 'none'; });
      document.getElementById(view + '-view').style.display = '';
    });
  });

  var btn = document.getElementById('scroll-to-top');
  if (!btn) return;
  function toggle() {
    if (window.scrollY > 300) { btn.classList.add('visible'); }
    else { btn.classList.remove('visible'); }
  }
  toggle();
  window.addEventListener('scroll', toggle, { passive: true });
  btn.addEventListener('click', function() {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  });
});
</script>
""";
    }

    private static string GenerateScrollToTopHtml()
    {
        return """
<button id="scroll-to-top" class="scroll-to-top" type="button" aria-label="ページ上部へ戻る" title="ページ上部へ戻る">
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" aria-hidden="true"><path fill="currentColor" d="M13,20H11V8L5.5,13.5L4.08,12.08L12,4.16L19.92,12.08L18.5,13.5L13,8V20Z"/></svg>
</button>
<script>
document.addEventListener('DOMContentLoaded', function() {
  var btn = document.getElementById('scroll-to-top');
  if (!btn) return;
  function toggle() {
    if (window.scrollY > 300) { btn.classList.add('visible'); }
    else { btn.classList.remove('visible'); }
  }
  toggle();
  window.addEventListener('scroll', toggle, { passive: true });
  btn.addEventListener('click', function() {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  });
});
</script>
""";
    }

    private static string GenerateFloatingTocScript()
    {
        return """
<script>
document.addEventListener('DOMContentLoaded', function() {
  var toc = document.getElementById('floating-toc');
  var backdrop = document.getElementById('toc-backdrop');
  if (!toc) return;

  function alignTocToContent() {
    var content = document.querySelector('.content');
    if (!content) return;

    var rect = content.getBoundingClientRect();

    // Sync top with .content's visible top, clamped to stay inside viewport
    var top = Math.max(0, rect.top);
    toc.style.top = top + 'px';

    // Constrain maxHeight so the TOC does not overlap the footer
    var footer = document.querySelector('footer');
    var footerRect = footer.getBoundingClientRect();
    var gap = footerRect.top - rect.bottom;
    var bottomBound = footer ? Math.min(window.innerHeight, footerRect.top - gap) : window.innerHeight;
    toc.style.maxHeight = Math.max(100, bottomBound - top) + 'px';

    // Left edge: only on wide screens where the sidebar is shown
    if (window.innerWidth >= 1600) {
      toc.style.left = (rect.right + 8) + 'px';
      toc.style.right = 'auto';
    } else {
      toc.style.left = '';
      toc.style.right = '';
    }
  }

  alignTocToContent();
  window.addEventListener('resize', alignTocToContent);
  window.addEventListener('scroll', alignTocToContent, { passive: true });

  // --- Active heading tracking (Zenn-style) ---
  var headings = Array.from(document.querySelectorAll('h2[id], h3[id]'));
  var tocLinks = Array.from(toc.querySelectorAll('a[href^="#"]'));
  var visibilityMap = new Map();

  function setActiveLink(id) {
    tocLinks.forEach(function(a) {
      var isActive = a.getAttribute('href') === '#' + id;
      a.classList.toggle('toc-active', isActive);
      if (isActive) {
        // Auto-scroll the TOC nav so the active item stays visible
        var nav = toc.querySelector('.floating-toc-nav');
        if (nav) {
          var aTop = a.offsetTop;
          var navHeight = nav.clientHeight;
          if (aTop < nav.scrollTop || aTop > nav.scrollTop + navHeight - 32) {
            nav.scrollTop = aTop - navHeight / 2;
          }
        }
      }
    });
  }

  var observer = new IntersectionObserver(function(entries) {
    entries.forEach(function(entry) {
      visibilityMap.set(entry.target.id, entry.isIntersecting);
    });

    // First visible heading in document order
    var activeId = null;
    for (var i = 0; i < headings.length; i++) {
      if (visibilityMap.get(headings[i].id)) {
        activeId = headings[i].id;
        break;
      }
    }

    // If none visible, use last heading above viewport
    if (!activeId) {
      for (var i = headings.length - 1; i >= 0; i--) {
        if (headings[i].getBoundingClientRect().top < 0) {
          activeId = headings[i].id;
          break;
        }
      }
    }

    if (activeId) setActiveLink(activeId);
  }, { rootMargin: '0px 0px -80% 0px' });

  headings.forEach(function(h) { observer.observe(h); });
  // --- End active heading tracking ---

  function closeToc() {
    toc.classList.remove('open');
    backdrop.classList.remove('visible');
  }

  backdrop.addEventListener('click', closeToc);

  tocLinks.forEach(function(a) {
    a.addEventListener('click', closeToc);
  });
});
</script>
""";
    }

    private static string GenerateCssStyle()
    {
        return $$"""
    * {
      box-sizing: border-box;
    }

    :root {
      interpolate-size: allow-keywords;
    }
    
    body {
      margin: 0;
      padding: 0;
      overflow-x: hidden;
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans JP", "Hiragino Kaku Gothic ProN", Meiryo, sans-serif;
      font-size: 16px;
      font-feature-settings: "palt" 1;
      line-height: 1.8;
      letter-spacing: 0.05em;
      color: #333;
      background-color: #f9fafb;
    }

    .navbar {
      position: absolute;
      background: #151b23;
      display: flex;
      align-items: center;
      width: 100%;
      height: 60px;
      padding: 0;
      box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
    }

    .fixtop {
      top: 0;
      right: 0;
      left: 0;
      z-index: 1030;
    }

    .head {
      background: #151b23;
      padding-top: 60px;
      padding-bottom: 40px;
      margin-bottom: 0;
    }
    
    .container {
      display: flex;
      justify-content: space-between;
      align-items: center;
      width: 100%;
      margin: 0 auto;
      padding: 0 24px;
    }
    
    .page {
      min-height: 100vh;
      padding: 24px 16px;
    }
    
    .main {
      margin: 0 auto;
    }
    
    .content {
      background: #ffffff;
      width: 100%;
      padding: 48px 40px;
      border-radius: 8px;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.04);
    }

    h1 {
      font-size: 32px;
      font-weight: 700;
      line-height: 1.4;
      margin: 0 0 32px 0;
      padding-bottom: 16px;
      color: #1a1a1a;
    }
    
    h2 {
      font-size: 24px;
      font-weight: 700;
      line-height: 1.5;
      margin: 48px 0 24px 0;
      color: #1a1a1a;
      padding-top: 16px;
      border-top: 1px solid #e5e7eb;
    }
    
    h2:first-of-type {
      margin-top: 0;
      padding-top: 0;
      border-top: none;
    }
    
    h3 {
      font-size: 20px;
      font-weight: 600;
      font-weight: bold;
      line-height: 1.5;
      margin: 32px 0 16px 0;
      overflow-wrap: break-word;
      color: #1a1a1a;
    }

    p {
      margin: 16px 0;
      overflow-wrap: break-word;
      color: #374151;
    }
    
    a {
      color: #2563eb;
      border-radius: 24px;
      line-height: 24px;
      text-decoration: none;
    }
    
    a:hover {
      color: #2563eb;
      text-decoration: underline;
    }

    .navbarlink {
      color: white;
      border-radius: 24px;
      line-height: 24px;
      font-size: 16px;
      text-decoration: none;
    }

    .navbarlink:hover {
      color: #9ca3af;
      text-decoration: none;
    }
    
    ul {
      margin: 0;
      padding: 2px 2px;
      padding-left: 24px;
    }

    ul p{
      margin: 0;
      padding: 0;
    }

    .daylist {
      list-style-type: none;
      display: grid;
      grid-auto-flow: column;
      grid-template-rows: repeat(12, auto);
    }

    .dayitem {
      list-style: none;
      display: flex;
      align-items: center;
    }
    
    li {
      margin: 0;
      padding: 2px 2px;
      color: #374151;
    }

    code {
      font-family: 'JetBrains Mono', 'Consolas', 'Monaco', 'Courier New', monospace;
      font-size: 14px;
      background: #f3f4f6;
      padding: 2px 6px;
      border-radius: 4px;
      color: #e11d48;
    }
    
    pre {
      margin: 24px 0;
      padding: 0;
      border-radius: 8px;
      overflow: hidden;
      background: #f9fafb;
      border: 1px solid #e5e7eb;
    }
    
    pre code {
      display: block;
      padding: 16px 20px;
      overflow-x: auto;
      background: transparent;
      color: inherit;
      border-radius: 0;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      margin: 16px 0;
      font-size: 14px;
      color: #374151;
    }

    th {
      background: #f3f4f6;
      color: #1a1a1a;
      font-weight: 600;
      text-align: left;
      padding: 10px 14px;
      border: 1px solid #e5e7eb;
      overflow-wrap: break-word;
    }

    td {
      padding: 8px 14px;
      border: 1px solid #e5e7eb;
      overflow-wrap: anywhere;
      word-break: break-word;
    }

    details {
      margin: 0px 0px 8px 0px;
      background-color: #f5f5f5;
    }

    details::details-content {
      height: 0;
      overflow: clip;
      opacity: 0;
      transition: height 0.1s ease, opacity 0.1s ease,
        content-visibility 0.1s ease allow-discrete;
    }
    
    details[open]::details-content {
      height: auto; /* for unsupported browser */
      height: calc-size(auto, size);
      opacity: 1;
    }

    summary {
      background-color: #ddd;
      padding: 1em 1em;
      border-radius: 4px;
      font-weight: bold;
      cursor: pointer;
    }
    
    strong {
      font-weight: 600;
      color: #1a1a1a;
    }

    #table-of-contents + ol li {
      padding: 0;
      margin: 0;
      font-weight: bold;
    }

    #table-of-contents + ol li a {
      color: #2563eb;
      font-weight: bold;
      text-decoration: underline;
    }

    #table-of-contents + ol li a:hover {
      color: #1d4ed8;
      text-decoration: underline;
    }

    footer {
      background: #151b23;
      color: #9ca3af;
      padding: 32px 24px;
      margin-top: 48px;
      text-align: center;
      font-size: 14px;
    }

    footer p {
      margin: 8px 0;
      color: #9ca3af;
    }

    .stats-grid {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 16px;
      margin: 16px 0 8px 0;
    }

    .stats-label-row {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 16px;
      margin: 0 0 24px 0;
    }

    .stat-card {
      background: #f0f6ff;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 24px;
      text-align: center;
      min-width: 0;
    }

    .stat-value {
      font-size: 36px;
      font-weight: 700;
      color: #1a1a1a;
      line-height: 1.2;
      font-family: 'JetBrains Mono', 'Consolas', monospace;
    }

    .stat-label {
      font-size: 14px;
      color: #6b7280;
      margin-top: 4px;
      overflow-wrap: break-word;
    }

    .view-tabs {
      display: flex;
      border-bottom: 2px solid #e5e7eb;
      margin: 24px 0 16px 0;
    }

    .view-tab {
      padding: 10px 24px;
      background: transparent;
      color: #6b7280;
      font-size: 16px;
      font-weight: 600;
      border: none;
      border-bottom: 2px solid transparent;
      margin-bottom: -2px;
      cursor: pointer;
      transition: color 0.15s, border-bottom-color 0.15s;
    }

    .view-tab:hover {
      color: #2563eb;
    }

    .view-tab.active {
      color: #2563eb;
      border-bottom-color: #2563eb;
    }

    .label-group {
      margin: 0 0 8px 0;
    }

    .label-group-summary {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }

    .label-pr-count {
      font-size: 13px;
      color: #6b7280;
      font-weight: normal;
    }

    .label-pr-list {
      padding: 8px 16px 8px 32px;
    }

    .label-pr-list li {
      padding: 2px 0;
    }

    .label-pr-list li a {
      color: #2563eb;
      text-decoration: underline;
    }

    .label-pr-list li a:hover {
      color: #1d4ed8;
    }

    @media (min-width: 1200px) {
      .container {
        max-width: 1140px;
      }

      .content {
        max-width: 1140px;
      }

      .main {
        max-width: 1140px;
      }

    }

    @media (max-width: 768px) {
      .page {
        padding: 16px 8px;
      }

      .container {
        max-width: 720px;
      }


      .main {
        max-width: 720px;
      }
    
      .content {
        max-width: 720px;
        padding: 32px 24px;
      }
    
      h1 {
        font-size: 28px;
      }
    
      h2 {
        font-size: 22px;
      }
    
      h3 {
        font-size: 18px;
      }
    
      body {
        font-size: 15px;
      }

      code {
        word-break: break-all;
        overflow-wrap: anywhere;
      }

      pre code {
        word-break: break-all;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }

      li {
        word-break: break-word;
        overflow-wrap: anywhere;
      }

      li > span[style*="border-radius:2em"] {
        white-space: normal !important;
        overflow-wrap: anywhere;
      }

      .daylist {
        list-style-type: none;
        display: grid;
        grid-auto-flow: column;
        grid-template-rows: repeat(16, auto);
      }

      summary {
        padding: 1.2em 1em;
        font-size: 16px;
      }

      .stats-grid,
      .stats-label-row {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .stat-value {
        font-size: 28px;
      }

      .view-tab {
        padding: 8px 16px;
        font-size: 14px;
      }

      .label-pr-list {
        padding: 8px 8px 8px 24px;
      }

    }

    @media (prefers-color-scheme: dark) {
      body {
        background-color: #111827;
        color: #e5e7eb;
      }

      details { 
        background-color: #1f2937; 
      }

      summary { 
        background-color: #374151; 
      }
    
      .content {
        background: #1f2937;
        box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
      }
    
      h1, h2, h3, strong {
        color: #f9fafb;
      }
    
      h1 {
        border-bottom-color: #374151;
      }
    
      h2 {
        border-top-color: #374151;
      }
    
      p, li {
        color: #d1d5db;
      }
    
      a {
        color: #60a5fa;
      }
    
      a:hover {
        color: #93c5fd;
      }

      #table-of-contents + ol li a {
        color: #60a5fa;
      }
    
      code {
        background: #374151;
        color: #fca5a5;
      }
    
      pre {
        background: #111827;
        border-color: #374151;
      }

      th {
        background: #374151;
        color: #f9fafb;
        border-color: #4b5563;
      }

      td {
        border-color: #4b5563;
        color: #d1d5db;
      }

      .stat-card {
        background: #374151;
        border-color: #4b5563;
      }

      .stat-value {
        color: #f9fafb;
      }

      .stat-label {
        color: #9ca3af;
      }

      .view-tabs {
        border-bottom-color: #374151;
      }

      .view-tab {
        color: #9ca3af;
      }

      .view-tab:hover,
      .view-tab.active {
        color: #60a5fa;
        border-bottom-color: #60a5fa;
      }

      .label-group {
        background-color: #1f2937;
      }

      .label-group-summary {
        background-color: #374151;
      }

      .label-pr-count {
        color: #9ca3af;
      }

      .label-pr-list li a {
        color: #60a5fa;
      }

      .label-pr-list li a:hover {
        color: #93c5fd;
      }
    }

    .floating-toc {
      position: fixed;
      top: 220px;
      right: 12px;
      width: 200px;
      max-height: calc(100vh - 220px);
      overflow-y: auto;
      background: #fff;
      border-radius: 8px;
      padding: 12px;
      z-index: 200;
      display: none;
    }

    .floating-toc-header {
      font-weight: 700;
      font-size: 13px;
      padding-bottom: 8px;
      margin-bottom: 8px;
      border-bottom: 1px solid #e5e7eb;
      color: #1a1a1a;
    }

    .floating-toc-nav ol {
      padding-left: 14px;
      margin: 0;
    }

    .floating-toc-nav ol li {
      font-weight: normal;
      padding: 2px 0;
      font-size: 12px;
    }

    .floating-toc-nav ol li {
      border-left: 2px solid transparent;
      padding-left: 6px;
    }

    .floating-toc-nav ol li a {
      display: block;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      color: #6b7280;
      text-decoration: none;
      line-height: 1.5;
      transition: color 0.15s;
    }

    .floating-toc-nav ol li a:hover {
      color: #2563eb;
      text-decoration: none;
    }

    .floating-toc-nav ol li:has(a.toc-active) {
      border-left-color: #2563eb;
    }

    .floating-toc-nav ol li a.toc-active {
      color: #2563eb;
      font-weight: 700;
    }

    .toc-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.35);
      z-index: 190;
      display: none;
    }

    .toc-backdrop.visible { display: block; }

    @media (min-width: 1600px) {
      .floating-toc { display: block; }
    }

    @media (max-width: 1599px) {
      .floating-toc.open {
        display: block;
        top: 0;
        right: 0;
        height: 100vh;
        max-height: 100vh;
        width: min(280px, 85vw);
        border-radius: 0;
        border-top: none;
        border-bottom: none;
        border-right: none;
        padding-top: 24px;
      }
    }

    @media (prefers-color-scheme: dark) {
      .floating-toc {
        background: #1f2937;
        border-color: #374151;
      }

      .floating-toc-header {
        color: #f9fafb;
        border-bottom-color: #374151;
      }

      .floating-toc-nav ol li a { color: #9ca3af; }
      .floating-toc-nav ol li a:hover { color: #60a5fa; }
      .floating-toc-nav ol li a.toc-active { color: #60a5fa; }
      .floating-toc-nav ol li:has(a.toc-active) { border-left-color: #60a5fa; }
    }

    .scroll-to-top {
      position: fixed;
      bottom: 24px;
      right: 24px;
      width: 44px;
      height: 44px;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border: none;
      border-radius: 50%;
      background: #2563eb;
      color: #ffffff;
      cursor: pointer;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.25);
      opacity: 0;
      visibility: hidden;
      transition: opacity 0.2s ease, visibility 0.2s ease;
      z-index: 300;
    }

    .scroll-to-top.visible {
      opacity: 1;
      visibility: visible;
    }

    .scroll-to-top:hover {
      background: #1d4ed8;
    }

    /* 画面幅が広いときは .content（最大1140px・中央寄せ）の右下に寄せる。本文右端からの余白は通常時と同じ24px */
    @media (min-width: 1200px) {
      .scroll-to-top {
        right: calc((100vw - 1140px) / 2 + 24px);
      }
    }

    @media (max-width: 768px) {
      .scroll-to-top {
        bottom: 16px;
        right: 16px;
      }
    }

    .label-index-table th.sortable {
      cursor: pointer;
      user-select: none;
      white-space: nowrap;
    }

    .label-index-table th.sortable::after {
      content: "\2195";
      margin-left: 6px;
      font-size: 12px;
      opacity: 0.4;
    }

    .label-index-table th.sort-asc::after {
      content: "\2191";
      opacity: 1;
    }

    .label-index-table th.sort-desc::after {
      content: "\2193";
      opacity: 1;
    }
""";
    }
}
