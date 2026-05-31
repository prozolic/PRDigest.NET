using System.Globalization;

namespace PRDigest.NET;

internal static class StringComparerOptions
{
    public static readonly StringComparer DefaultComparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);
}
