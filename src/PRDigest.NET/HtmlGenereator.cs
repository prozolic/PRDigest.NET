using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PRDigest.NET;

internal static class HtmlGenereator
{
    public static string GenerateIndex(string archivesDir)
    {
        var comparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

        var builder = new DefaultInterpolatedStringHandler(0, 0);
        foreach (var yearDirs in Directory.GetDirectories(archivesDir).OrderDescending(comparer))
        {
            var year = Path.GetFileName(yearDirs);
            foreach (var monthDirss in Directory.GetDirectories(yearDirs).OrderDescending(comparer))
            {
                var month = Path.GetFileName(monthDirss);
                builder.AppendLiteral($"<details>{Environment.NewLine}");
                builder.AppendLiteral($"   <summary>{year}/{month}</summary>{Environment.NewLine}");
                builder.AppendLiteral($"   <ul>{Environment.NewLine}");

                foreach (var dayFiles in Directory.GetFiles(monthDirss, "*.html").OrderDescending(comparer))
                {
                    var day = Path.GetFileName(dayFiles);
                    builder.AppendLiteral($"     <li><a href=\"./{year}/{month}/{day}\">{year}/{month}/{Path.GetFileNameWithoutExtension(day)}</a> </li>{Environment.NewLine}");
                }

                builder.AppendLiteral($"   </ul>{Environment.NewLine}");
                builder.AppendLiteral($"</details>{Environment.NewLine}");
            }
        }

        return GenerateTemplateHtml($"PR Digest.NET", "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", builder.ToStringAndClear());
    }

    public static string GenerateHtmlFromMarkdown(string startTargetDate, string markdownContent)
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseAutoIdentifiers(AutoIdentifierOptions.GitHub).Build();
        var contentHtml = Markdown.ToHtml(markdownContent, pipeline);

        var content = $"""
      <h2>このページは、<a href="https://github.com/dotnet/runtime">dotnet/runtime</a>リポジトリにマージされたPull Requestを自動的に収集し、その内容をAIが要約した内容を表示しています。</h2>
      <hr>
      {contentHtml}
""";

        return GenerateTemplateHtml($"PR Digest.NET - Pull Request on {startTargetDate}", "dotnet/runtimeにマージされたPull RequestをAIで日本語要約", content);
    }

    private static string GenerateTemplateHtml(string title, string subTitle, string content)
    {
        return $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>{{title}}</title>
  <meta name="description" content="Merged pull request in dotnet/runtime digest." />
  <meta name="author" content="prozolic" />
  <meta name="keywords" content="C#,.NET,pull request,LLM," />
  <meta name="robots" content="index, follow" />
  <meta name="theme-color" content="#03173d" />

  <link href="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism.min.css" rel="stylesheet">
  <style>
{{GenerateCssStyle("")}}
  </style>
</head>
<body>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-core.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/autoloader/prism-autoloader.min.js"></script>
  <nav class="navbar fixtop"> 
    <div class="container">
      <a class="navbarlink" href="/">PR Digest.NET</a>
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
</body>
</html>
""";

    }

    private static string GenerateCssStyle(string additional)
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
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans JP", "Hiragino Kaku Gothic ProN", Meiryo, sans-serif;
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
      line-height: 1.5;
      margin: 32px 0 16px 0;
      color: #1a1a1a;
    }

    p {
      margin: 16px 0;
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
      margin: 16px 0;
      padding-left: 24px;
    }
    
    li {
      margin: 8px 0;
      color: #374151;
    }

    code {
      font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
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
    }
    
    @media (prefers-color-scheme: dark) {
      body {
        background-color: #111827;
        color: #e5e7eb;
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
    
      code {
        background: #374151;
        color: #fca5a5;
      }
    
      pre {
        background: #111827;
        border-color: #374151;
      }
    }
""";
    }
}
