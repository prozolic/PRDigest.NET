using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PRDigest.NET;

internal static partial class PromptGenerator
{
    private const int MaxFileCount = 30;

    public const string SystemPrompt = """
        あなたは.NET開発者向けのPull Request要約アシスタントです。
        提供された情報のみに基づいて要約してください。
        提供されていない情報の推測・補完は行わないでください。

        ## 出力形式
        .NET開発者にとって有益な情報を含める形で、最大1000文字までで要約してください。
        以下の出力フォーマット（概要、変更内容、パフォーマンスへの影響、関連Issue、その他）に遵守して、markdown形式で出力してください。
        タイトルは不要です。

        ## 要約ガイドライン
        要約を作成する際は、以下の点に特に注意を払ってください：
        - 変更の主な意図と技術的ポイントを記述してください（ファイル一覧の再掲は不要）
        - 変更がランタイム/コンパイラ/ライブラリのどの部分に影響するか、公開APIか内部実装かを区別してください
        - 互換性への影響（破壊的変更、非推奨化など）があれば明記してください
        - セキュリティ脆弱性修正の場合はその重要度とCVE番号（あれば）を明記する
        - バグ修正の場合は修正前の問題と修正後の動作を対比する
        - パフォーマンス改善の場合は改善率や具体的な数値を記載する
        - 全体を簡潔にまとめ、必要最低限の情報に絞ること

        ## 出力フォーマット
        #### 概要
        変更の目的と内容を1〜5行で簡潔に記述してください。
        またサンプルコードが存在する場合のみ記載してください。

        #### 変更内容
        変更されたファイルと主な変更内容をリストアップしてください。

        #### パフォーマンスへの影響
        パフォーマンス（メモリ・実行速度・スループット）に関連する変更があれば具体的に記載してください。（なければ"影響なし"）
        ベンチマーク結果があれば含めてください。
        改善点や懸念点を明記してください。

        #### 関連Issue
        関連するIssueがなければ「なし」と記載してください。
        関連するIssueがある場合は記載以下のルールに従って記載してください。
        dotnet/runtimeのIssueの場合、[#12345](https://github.com/dotnet/runtime/issues/12345)の形で記載してください。
        それ以外のリポジトリのIssueの場合は、リポジトリ名とIssue番号を記載してください。

        #### その他
        上記以外の特記事項があれば記載してください。
        C#のコードサンプルを示す場合は ```csharp ブロックを必ず使用してください。
        なければ「なし」。
        """;

    private static ReadOnlySpan<string> EmptyReviewComment => new string[]
    {
        "generated no new comments",
        "generated no comments",
        "no new comments",
        "Copilot encountered an error and was unable to review this pull request"
    };

    private static readonly SearchValues<string> EmptyReviewCommentSearchValues = SearchValues.Create(EmptyReviewComment, StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*(##\s*Pull request overview\s*)?Copilot reviewed \d+ out of \d+ changed files in this pull request and generated \d+ comments\.\s*$")]
    private static partial Regex ReviewedOutOfRegex();

    public static string GeneratePrompt(PullRequestInfo info)
    {
        var prompt = $"""
以下のdotnet/runtimeのPull Requestを要約してください。
またできる限り、以下の情報以外の内容を推測して含めないようにしてください。

Pull Request:
- {info.PullRequest.Title} #{info.PullRequest.Number}
- 作成者: {info.PullRequest.User.Login}
- レビュワー: {GenerateReviewersText(info)}

作成者による概要:
{GenerateBody(info)}

Copilotによる概要:
{GenerateCopilotReviewText(info)}

変更ファイル:
{GenerateFilesChangedText(info)}
""";
        return prompt;
    }

    private static string GenerateBody(PullRequestInfo info)
    {
        var body = info.PullRequest.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            body = "なし";
        }
        return body;
    }

    private static string GenerateReviewersText(PullRequestInfo info)
    {
        var reviews = info.Reviews;
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        for (int i = 0; i < reviews.Count; i++)
        {
            var pullRequestReview = reviews[i];
            builder.AppendLiteral(pullRequestReview.User.Login);
            if (i < reviews.Count - 1)
            {
                builder.AppendLiteral(", ");
            }
        }

        return builder.ToStringAndClear();
    }

    private static string GenerateCopilotReviewText(PullRequestInfo pullRequestInfo)
    {
        var copilotReviews = pullRequestInfo
            .Reviews
            .Where(r => 
                r.User.Login == "copilot-pull-request-reviewer[bot]" &&
                r.Body.Length > 0 &&
                !r.Body.AsSpan().ContainsAny(EmptyReviewCommentSearchValues) && 
                !ReviewedOutOfRegex().IsMatch(r.Body)
                )
            .OrderBy(r => r.SubmittedAt)
            .ToArray();

        if (copilotReviews.Length == 0) return "なし";
        if (copilotReviews.Length == 1) return copilotReviews[0].Body.Trim();
        if (copilotReviews.Length == 2) return $"{copilotReviews[0].Body.Trim()}{Environment.NewLine}{Environment.NewLine}{copilotReviews[1].Body.Trim()}";

        var copilotReviewsSpan = copilotReviews.AsSpan();

        // Set first review body.
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        builder.AppendLiteral(copilotReviewsSpan[0].Body.Trim());

        // Append remaining reviews with two new lines as separator, but limit total length to 10000 characters to avoid exceeding token limit.
        foreach (var review in copilotReviewsSpan[1..^1])
        {
            if (builder.Text.Length > 10000)
            {
                break;
            }
            builder.AppendLiteral(Environment.NewLine);
            builder.AppendLiteral(Environment.NewLine);
            builder.AppendLiteral(review.Body.Trim());
        }

        // Append latest review.
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral(Environment.NewLine);
        builder.AppendLiteral(copilotReviewsSpan[^1].Body.Trim());

        return builder.ToStringAndClear();
    }

    private static string GenerateFilesChangedText(PullRequestInfo info)
    {
        var builder = new DefaultInterpolatedStringHandler(0, 0);
        foreach (var f in info.Files.Take(MaxFileCount))
        {
            builder.AppendLiteral($"- {f.FileName} (+{f.Additions}/-{f.Deletions}, total: {f.Changes})");
            builder.AppendLiteral(Environment.NewLine);
        }

        var count = info.Files.Count - MaxFileCount;
        if (count > 0)
        {
            builder.AppendLiteral($"- その他 {count} files");
        }

        return builder.ToStringAndClear();
    }
}