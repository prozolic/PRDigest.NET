using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PRDigest.NET;

internal static class HtmlGenereator
{
    private static readonly StringComparer NumericOrderingComparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);
    private static MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAdvancedExtensions()
        .Build();

    public static string GenerateIndex(string archivesDir, string outputsDir)
    {
        var comparer = NumericOrderingComparer;

        var detailsBuilder = new DefaultInterpolatedStringHandler(0, 0);
        foreach (var yearDirs in Directory.GetDirectories(outputsDir).OrderDescending(comparer))
        {
            var year = Path.GetFileName(yearDirs);
            foreach (var monthDirss in Directory.GetDirectories(yearDirs).OrderDescending(comparer))
            {
                var month = Path.GetFileName(monthDirss);
                detailsBuilder.AppendLiteral($"<details>{Environment.NewLine}");
                detailsBuilder.AppendLiteral($"   <summary>{year}年{month}月</summary>{Environment.NewLine}");
                detailsBuilder.AppendLiteral($"   <ul class=\"daylist\">{Environment.NewLine}");

                foreach (var htmlPath in Directory.GetFiles(monthDirss, "*.html").Order(comparer))
                {
                    detailsBuilder.AppendLiteral($"     <li class=\"dayitem\"><a href=\"./{year}/{month}/{Path.GetFileName(htmlPath)}\">{year}年{month}月{Path.GetFileNameWithoutExtension(htmlPath)}日</a> </li>{Environment.NewLine}");
                }

                detailsBuilder.AppendLiteral($"   </ul>{Environment.NewLine}");
                detailsBuilder.AppendLiteral($"</details>{Environment.NewLine}");
            }
        }

        var latestPullRequestInfo = "";
        var lastedYearDirs = Directory.GetDirectories(outputsDir).OrderDescending(comparer).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(lastedYearDirs))
        {

            var lastedYear = Path.GetFileName(lastedYearDirs);
            var lastedMonthDirs = Directory.GetDirectories(lastedYearDirs!).OrderDescending(comparer).FirstOrDefault();
            var lastedMonth = Path.GetFileName(lastedMonthDirs);
            var lastedDayHtmlPath = Directory.GetFiles(lastedMonthDirs!, "*.html").OrderDescending(comparer).FirstOrDefault();

            var lastedDay = Path.GetFileNameWithoutExtension(lastedDayHtmlPath);
            var latestMarkdownPath = Path.Combine(archivesDir, lastedYear!, lastedMonth!, $"{lastedDay}.md");

            var statsHtml = "";
            if (File.Exists(latestMarkdownPath))
            {
                var markdownContent = File.ReadAllText(latestMarkdownPath);
                var document = Markdown.Parse(markdownContent, Pipeline);
                var analyzerResult = PullReqeustAnalayzer.Analayze(document);

                statsHtml = $"""
                                <div class="stats-grid">
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.PullRequestCount}</div>
                                        <div class="stat-label">マージされたPR</div>
                                    </div>
                                    <div class="stat-card">
                                        <div class="stat-value">{analyzerResult.LabelCount}</div>
                                        <div class="stat-label">ラベル種類</div>
                                    </div>
                                </div>
                """;
            }

            latestPullRequestInfo = $"""
                            <h2>最新のダイジェスト</h2>
                            <p><a href="./{lastedYear}/{lastedMonth}/{Path.GetFileName(lastedDayHtmlPath)}">{lastedYear}年{lastedMonth}月{Path.GetFileNameWithoutExtension(lastedDayHtmlPath)}日</a></p>
                            {statsHtml}
                            <h2>過去の月別ダイジェスト</h2>
                            """;
        }

        return GenerateTemplateHtml($"PR Digest.NET", "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", latestPullRequestInfo + detailsBuilder.ToStringAndClear());
    }

    public static string GenerateHtmlFromMarkdown(string startTargetDate, string markdownContent)
    {
        var document = Markdown.Parse(markdownContent, Pipeline);
        var contentHtml = Markdown.ToHtml(document, Pipeline);
        var analyzerResult = PullReqeustAnalayzer.Analayze(document);

        // Split contentHtml into TOC part and PR details part
        // The TOC ends after </ol>, then a <hr /> separates it from PR details
        var contentSpan = contentHtml.AsSpan();
        var tocEndIndex = contentSpan.IndexOf("</ol>", StringComparison.Ordinal);
        string tocHtml;
        string prDetailsHtml;

        if (tocEndIndex >= 0)
        {
            tocEndIndex += "</ol>".Length;
            var hrIndex = contentSpan[tocEndIndex..].IndexOf("<hr", StringComparison.Ordinal);
            if (hrIndex >= 0)
            {
                tocHtml = contentSpan[..tocEndIndex].ToString();
                prDetailsHtml = contentSpan[(tocEndIndex + hrIndex)..].ToString();
            }
            else
            {
                tocHtml = contentSpan[..tocEndIndex].ToString();
                prDetailsHtml = contentSpan[tocEndIndex..].ToString();
            }
        }
        else
        {
            // Fallback: no split possible
            tocHtml = contentHtml;
            prDetailsHtml = "";
        }

        var labelViewHtml = GenerateLabelViewHtml(document, analyzerResult);

        var content = $"""
      <h2>注意点</h2>
      <p>このページは、<a href="https://github.com/dotnet/runtime">dotnet/runtime</a>リポジトリにマージされたPull Requestを自動的に収集し、その内容をAIが要約した内容を表示しています。そのため、必ずしも正確な要約ではない場合があります。</p>
      <hr>
      <div class="view-tabs">
        <button class="view-tab active" data-view="list">一覧</button>
        <button class="view-tab" data-view="label">ラベル別</button>
      </div>
      <div id="list-view" class="view-panel">
        {tocHtml}
      </div>
      <div id="label-view" class="view-panel" style="display:none">
        {labelViewHtml}
      </div>
      {prDetailsHtml}
""";

        return GenerateTemplateHtml($"Pull Request on {startTargetDate}", "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", content, includeViewScript: true);
    }

    private static string GenerateLabelViewHtml(MarkdownDocument document, PullReqeustAnalayzer.AnalayzerResult analyzerResult)
    {
        if (analyzerResult.LabelInfo is null || analyzerResult.LabelCount == 0)
            return "<p>ラベル情報がありません。</p>";

        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral($"<h3>ラベル別PR一覧</h3>{Environment.NewLine}");

        foreach (var (labelName, headingBlocks) in analyzerResult.LabelInfo.OrderByDescending(kv => kv.Value.Count))
        {
            var colorStyle = "";
            if (analyzerResult.LabelColorMap is not null && analyzerResult.LabelColorMap.TryGetValue(labelName, out var color))
            {
                colorStyle = $" style=\"background-color: {color}; color: #000000; display: inline-block; padding: 0 7px; font-size: 12px; font-weight: 500; line-height: 18px; border-radius: 2em; border: 1px solid transparent;\"";
            }

            builder.AppendLiteral($"<details class=\"label-group\">{Environment.NewLine}");
            builder.AppendLiteral($"  <summary class=\"label-group-summary\"><span{colorStyle}>{labelName}</span> <span class=\"label-pr-count\">({headingBlocks.Count} PRs)</span></summary>{Environment.NewLine}");
            builder.AppendLiteral($"  <ol class=\"label-pr-list\">{Environment.NewLine}");

            foreach (var heading in headingBlocks)
            {
                // Extract PR number and title from HeadingBlock inlines
                var pullRequestNumber = "";
                var titleText = "";

                var inline = heading.Inline?.FirstChild;
                while (inline is not null)
                {
                    if (inline is LinkInline linkInline)
                    {
                        // The link text contains the PR number like "#124237"
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
                    }
                    else if (inline is CodeInline codeInline)
                    {
                        titleText += codeInline.Content;
                    }
                    inline = inline.NextSibling;
                }

                // Derive anchor ID from PR number (remove '#')
                var anchorId = pullRequestNumber.TrimStart('#');
                var displayText = $"{pullRequestNumber} {titleText.Trim()}";

                builder.AppendLiteral($"    <li><a href=\"#{anchorId}\">{System.Net.WebUtility.HtmlEncode(displayText)}</a></li>{Environment.NewLine}");
            }

            builder.AppendLiteral($"  </ol>{Environment.NewLine}");
            builder.AppendLiteral($"</details>{Environment.NewLine}");
        }

        return builder.ToStringAndClear();
    }

    private static string GenerateTemplateHtml(string title, string subTitle, string content, bool includeViewScript = false)
    {
        var viewScript = includeViewScript ? GenerateViewScript() : "";
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
      <a href="https://github.com/prozolic/PRDigest.NET">
        <svg style="filter: invert(1);" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><path d="M12.5.75C6.146.75 1 5.896 1 12.25c0 5.089 3.292 9.387 7.863 10.91.575.101.79-.244.79-.546 0-.273-.014-1.178-.014-2.142-2.889.532-3.636-.704-3.866-1.35-.13-.331-.69-1.352-1.18-1.625-.402-.216-.977-.748-.014-.762.906-.014 1.553.834 1.769 1.179 1.035 1.74 2.688 1.25 3.349.948.1-.747.402-1.25.733-1.538-2.559-.287-5.232-1.279-5.232-5.678 0-1.25.445-2.285 1.178-3.09-.115-.288-.517-1.467.115-3.048 0 0 .963-.302 3.163 1.179.92-.259 1.897-.388 2.875-.388.977 0 1.955.13 2.875.388 2.2-1.495 3.162-1.179 3.162-1.179.633 1.581.23 2.76.115 3.048.733.805 1.179 1.825 1.179 3.09 0 4.413-2.688 5.39-5.247 5.678.417.36.776 1.05.776 2.128 0 1.538-.014 2.774-.014 3.162 0 .302.216.662.79.547C20.709 21.637 24 17.324 24 12.25 24 5.896 18.854.75 12.5.75Z"></path></svg>
      </a>
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
</body>
</html>
""";

    }

    private static string GenerateViewScript()
    {
        return """
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
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans JP", "Hiragino Kaku Gothic ProN", Meiryo, sans-serif;
      font-size: 16px;
      line-height: 1.8;
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
      grid-template-columns: repeat(2, 1fr);
      gap: 16px;
      margin: 16px 0 24px 0;
    }

    .stat-card {
      background: #f0f6ff;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 24px;
      text-align: center;
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
""";
    }
}
