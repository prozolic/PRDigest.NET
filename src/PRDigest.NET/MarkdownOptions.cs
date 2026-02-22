using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace PRDigest.NET;

internal static class MarkdownOptions
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAdvancedExtensions()
        .Build();
}
