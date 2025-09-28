
public static class StringExtensions
{
    public static string TrimEnd(this string source, string value)
    {
        if (!source.EndsWith(value))
            return source;

        return source[..source.LastIndexOf(value)];
    }
}