using System.Buffers;
using System.Runtime.CompilerServices;

namespace PRDigest.NET;

internal static class TitleHelper
{
    private static readonly SearchValues<char> EscapedChars = SearchValues.Create("[]()<>");

    public static string EscapedTitle(string title)
    {
        if (title.AsSpan().IndexOfAny(EscapedChars) < 0)
            return title;

        var handler = new DefaultInterpolatedStringHandler(0, 0);

        Span<char> buffer = stackalloc char[1];
        for (int i = 0; i < title.Length; i++)
        {
            char c = title[i];
            if (c is '[' or ']' or '(' or ')' or '<' or '>')
            {
                handler.AppendLiteral("\\");
            }
            buffer[0] = c;
            handler.AppendFormatted(buffer);
        }

        return handler.ToStringAndClear();
    }
}
