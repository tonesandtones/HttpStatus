public static class StringIntExtensions
{
    public static int? TryParseInt(this string s)
    {
        if (int.TryParse(s, out var n))
        {
            return n;
        }
        return null;
    }
}