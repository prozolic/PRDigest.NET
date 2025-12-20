using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace PRDigest.NET;

internal static class PromptGenerator
{
    private const int MaxFileCount = 20;

    public static string GeneratePrompt(PullRequestInfo info)
    {
        // file changes info
        var files = info.Files;
        var filesChanged = string.Join(Environment.NewLine,
            files.Select(f => $"- {f.FileName} (+{f.Additions}/-{f.Deletions}, total: {f.Changes})")
                .Append(files.Count > MaxFileCount ? $"- その他 {files.Count - MaxFileCount} files" : "")
         );

        // reviewer info
        var reviews = info.Reviews;
        var reviewersBuilder = new DefaultInterpolatedStringHandler(0, 0);
        for (int i = 0; i < reviews.Count; i++)
        {
            var pullRequestReview = reviews[i];
            reviewersBuilder.AppendLiteral(pullRequestReview.User.Login);
            if (i < reviews.Count - 1)
            {
                reviewersBuilder.AppendLiteral(", ");
            }
        }

        // get latest 10 comments
        var issueComment = info.IssueComments;
        var latestComment = issueComment.Any() ? string.Join(Environment.NewLine,
                issueComment
                .Where(c => c.AuthorAssociation == "CONTRIBUTOR" || c.AuthorAssociation == "MEMBER")
                .OrderByDescending(c => c.CreatedAt)
                .Take(10)
                .Select(c => $"[{c.CreatedAt:yyyy-MM-dd HH:mm}] by {c.User.Login}{Environment.NewLine}{c.Body}{Environment.NewLine}")) : "";

        var prompt = $"""
以下のdotnet/runtimeのPull Requestを最大1000文字までで要約してください。
出力時にタイトルは不要です。

Pull Request名
- {info.PullRequest.Title} #{info.PullRequest.Number}
- 作成者: {info.PullRequest.User.Login}
- レビュワー: {reviewersBuilder.ToStringAndClear()}

説明文:
{info.PullRequest.Body}

変更ファイル:
{filesChanged}

Pull Requestに対する最新コメント（最大10件）:
{latestComment}

サンプルコードを記載する際の注意事項:
- C#のコード例を含める場合、コードブロックを使用してください。
```csharp
// ソースコードを記載
```

=================================
出力形式:

#### 概要
1行から5行ぐらいで簡潔に記述してください。
またサンプルコードなどもあれば記載してください。

#### 変更内容
変更されたファイルと主な変更内容をリストアップしてください。

#### パフォーマンスへの影響
パフォーマンスに関連する変更があれば具体的に記載してください。（なければ"影響なし"）
改善点や懸念点を明記してください。

#### 関連Issue
関連するIssueあれば記載してください。（なければ"なし"）

#### その他
それ以外に記載した方が良い特記事項があれば記載してください。（なければ"なし"）
=================================

.NET開発者によって有益な情報を含める形で要約してください。
出力形式:にそってmarkdown形式で出力してください。
""";
        return prompt;
    }
}