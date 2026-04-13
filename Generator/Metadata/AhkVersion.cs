namespace AhkWin32.Generator.Metadata;

/// <summary>
/// An AutoHotkey major version, either 2.0 or 2.1
/// </summary>
public enum AhkVersion
{
    v20,
    v21
}

public static class AhkVersionExtensions
{
    /// <summary>
    /// Can't actually override ToString - https://stackoverflow.com/a/479453
    /// </summary>
    public static string ToFriendlyString(this AhkVersion version) => version switch
    {
        AhkVersion.v20 => "2.0",
        AhkVersion.v21 => "2.1-alpha",
        _ => version.ToString()
    };
}