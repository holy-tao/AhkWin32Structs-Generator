namespace AhkWin32.Generator.Metadata;

using System.Reflection.Metadata;

/// <summary>
/// Minimal ICustomAttributeTypeProvider for decoding custom attribute values.
/// </summary>
internal sealed class CaTypeProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    public string GetSystemType() => "System.Type";

    public string GetTypeFromSerializedName(string name) => name ?? "Unknown";

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw)
    {
        TypeDefinition td = r.GetTypeDefinition(h);
        string ns = r.GetString(td.Namespace);
        string n = r.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
    }

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        TypeReference tr = r.GetTypeReference(h);
        string ns = r.GetString(tr.Namespace);
        string n = r.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
    }

    public string GetSZArrayType(string elementType) => $"{elementType}[]";

    public string GetTypeFromSpecification(MetadataReader r, object? ctx, TypeSpecificationHandle h, byte raw) =>
        "TypeSpec";

    public bool IsSystemType(string type) => type == "System.Type";

    PrimitiveTypeCode ICustomAttributeTypeProvider<string>.GetUnderlyingEnumType(string type) =>
        PrimitiveTypeCode.UInt32;

    public string GetUnderlyingEnumType(string type) => type;
}
