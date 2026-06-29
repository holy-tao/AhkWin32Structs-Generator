namespace AhkWin32.Generator.Transform;

using YamlDotNet.Serialization;

/// <summary>
/// Loads the AHK reserved name list from a YAML config file.
/// The single list is used for both type name and parameter name deconfliction.
/// </summary>
public static class ReservedNameConfig
{
    /// <summary>
    /// Load the reserved name list from a YAML file containing a flat list of strings.
    /// Returns a case-insensitive HashSet.
    /// </summary>
    public static HashSet<string> Load(string configPath)
    {
        IDeserializer deserializer = new DeserializerBuilder().Build();
        List<string> names = deserializer.Deserialize<List<string>>(File.ReadAllText(configPath));

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }
}
