
using System.Globalization;

namespace PRDigest.NET;

internal sealed class GitHubLabalColor
{
    private static readonly double LightnessThreshold = 0.453;

    public static string GetFontColor(string backColor)
    {
        var hex = backColor.AsSpan().TrimStart('#');
        if (hex.Length < 6) return "#000000";

        var r = ToInt32FromHexChars(hex[0..2]) / 255.0d;
        var g = ToInt32FromHexChars(hex[2..4]) / 255.0d;
        var b = ToInt32FromHexChars(hex[4..6]) / 255.0d;
        var luminance = GetLightness(r, g, b);

        return luminance < LightnessThreshold ? "#ffffff" : "#000000";

        static double ToInt32FromHexChars(ReadOnlySpan<char> hex)
        {
            if (hex.Length != 2) return 0d;

            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            return 0d;
        }
    }

    private static double GetLightness(double r, double g, double b)
    {
        return 0.2126d * ToLinear(r) + 0.7152d * ToLinear(g) + 0.0722d * ToLinear(b);

        static double ToLinear(double c)
        {
            return c <= 0.04044823627710819 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}