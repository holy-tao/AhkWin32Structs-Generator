using YamlDotNet.Serialization;

/// <summary>
/// Extension data read from a .yml file
/// </summary>
public class AhkExtension
{
    [YamlMember(Alias = "add-to", ApplyNamingConventions = false)]
    public required List<string> AppliesTo { get; set; }

    [YamlMember(Alias = "requires", ApplyNamingConventions = false)]
    public required List<string> Requirements { get; set; }

    [YamlMember(Alias = "code", ApplyNamingConventions = false)]
    public required string Code { get; set; }
    
    public string GetCodeIndented(int indentLevel)
    {
        string indent = "\r\n" + string.Join("", Enumerable.Repeat(" ", 4 * indentLevel));
        return indent + Code.Replace("\n", indent);
    }

    /// <summary>
    /// Primarilly for debugging - returns a Yaml-serizlied representation of the extension
    /// </summary>
    /// <returns>Yaml-serialized representation of this extension</returns>
    public override string ToString()
    {
        return new SerializerBuilder().Build().Serialize(this);
    }
}