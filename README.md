# PRDigest.NET

Displays AI-summarized information of Pull Requests merged in [dotnet/runtime](https://github.com/dotnet/runtime).
This is inspired by [YuheiNakasaka/rails-pr-digest](https://github.com/YuheiNakasaka/rails-pr-digest).

## How it Works

The application runs automatically via GitHub Actions every day at midnight (UTC). It:
1. Collects Pull Requests merged in the previous day from dotnet/runtime
2. Summarizes each PR using Claude API.
3. Generates markdown summaries and saves them to the archives directory
4. Converts all markdown files to HTML pages
5. Deploys the generated HTML to GitHub Pages

## AI Model

Claude Haiku 4.5

## Development

### System environment

- .NET 10
- C# 14

### Dependencies

- [markdig](https://github.com/xoofx/markdig)
- [octokit.net](https://github.com/octokit/octokit.net)
- [anthropic-sdk-csharp](https://github.com/anthropics/anthropic-sdk-csharp)

### Links

- [dotnet/runtime](https://github.com/dotnet/runtime)
- [Claude Docs - Models overview](https://platform.claude.com/docs/en/about-claude/models/overview)

## RSS Feed

https://prozolic.github.io/PRDigest.NET/feed.xml

## Third-party Notices

### Material Design Icons

- Icon: rss
- URL: https://pictogrammers.com/library/mdi/icon/rss/
- Copyright (c) 2014 Austin Andrews (https://templarian.com/)
- Licensed under the [Apache License, Version 2.0](https://github.com/Templarian/MaterialDesign/blob/master/LICENSE)

### GitHub Octicons

- Icon: mark-github
- URL: https://github.com/primer/octicons/blob/main/icons/mark-github-24.svg
- Copyright (c) 2012 GitHub, Inc.
- Licensed under the [MIT License](https://github.com/primer/octicons/blob/main/LICENSE)
