using Anthropic.Models.Messages;

namespace PRDigest.NET;

internal static class Constants
{
    public const string Owner = "dotnet";
    public const string Repository = "runtime";
    public const string FullRepository = $"{Owner}/{Repository}";

    public const Model SummaryModel = Model.ClaudeSonnet5_5; // Claude Sonnet 5.5

    // Summarization settings shared by the Messages API and the Message Batches API.
    // Thinking is turned off with "between_tools" (Sonnet 5.5 rejects "disabled"), so max_tokens covers only the summary.
    // The prompt caps the summary at 1000 characters; 1024 tokens cut off about 4% of Haiku 4.5 summaries mid-sentence.
    public const int SummaryMaxTokens = 2048;

    // Shown instead of the summary when Claude declines to summarize a PR. It keeps the 概要 heading so that the
    // monthly page and the RSS feed, which read only that section, show it too.
    public const string RefusedSummary = """
    #### 概要
    この Pull Request は、安全性に関する判定により要約を生成できませんでした。変更内容は元の Pull Request を参照してください。
    """;
}