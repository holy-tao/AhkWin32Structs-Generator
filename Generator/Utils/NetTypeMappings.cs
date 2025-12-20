using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Helper for mapping types from .NET to WinRT / Win32 types.
/// </summary>
public static class NetTypeMappings
{
    private static Dictionary<string, TypeMapping> _netTypeMappings = [];

    public static void Load(string metadataPath)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();

        string mappingsPath = Path.Combine(metadataPath, "type-mappings.yml");
        string yamlContent = File.ReadAllText(mappingsPath);

        Trace.TraceInformation($"Loading type mappings from {mappingsPath}");

        _netTypeMappings = deserializer.Deserialize<Dictionary<string, TypeMapping>>(yamlContent);

        Trace.TraceInformation($"Loaded {_netTypeMappings.Count} type mappings:");
        foreach (var kvp in _netTypeMappings)
        {
            Trace.TraceInformation($"  {kvp.Key} -> {kvp.Value}");
        }
    }

    /// <summary>
    /// Attempts to get the mapped type for a given fully qualified name (FQN).
    /// </summary>
    /// <param name="fqn">Fully qualified name of the type to map.</param>
    /// <param name="mappedType">Handle and reader of the mapped type, if found</param>
    /// <returns>True if the type is found, false if not</returns>
    public static bool TryGetMappedType(string fqn, [NotNullWhen(true)] out (TypeDefinitionHandle handle, MetadataReader reader)? mappedType)
    {
        if (_netTypeMappings.TryGetValue(fqn, out var mapping))
        {
            Trace.TraceInformation($"Mapping {fqn} to {mapping}");

            var mappedHandle = FieldSignatureDecoder.FindTypeDefinition(
                mapping.AssemblyName, mapping.TypeNamespace, mapping.TypeName, out MetadataReader reader);
            
            mappedType = (mappedHandle, reader);
            return true;
        }

        mappedType = null;
        return false;
    }
}

public record struct TypeMapping
{
    /// <summary>
    /// The name of the assembly that contains the type, not including file extensions.
    /// </summary>
    [YamlMember(Alias = "Assembly")] 
    public required string AssemblyName { get; init; }

    /// <summary>
    /// The namespace of the type mapped type.
    /// </summary>
    [YamlMember(Alias = "Namespace")] 
    public required string TypeNamespace { get; init; }
    
    /// <summary>
    /// The name of the mapped type.
    /// </summary>
    [YamlMember(Alias = "Name")] 
    public required string TypeName { get; init; }

    public override string ToString() => $"{AssemblyName}!{TypeNamespace}.{TypeName}";

    /// <summary>
    /// The fully qualified name of the mapped type.
    /// </summary>
    public string Fqn => $"{TypeNamespace}.{TypeName}";
}