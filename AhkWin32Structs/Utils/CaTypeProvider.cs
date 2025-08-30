using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

// Minimal provider to decode CustomAttribute values
sealed class CaTypeProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSystemType() => "System.Type";
    public string GetTypeFromSerializedName(string name) => name ?? "Unknown";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw)
    {
        var td = r.GetTypeDefinition(h);
        var ns = r.GetString(td.Namespace);
        var n  = r.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        var tr = r.GetTypeReference(h);
        var ns = r.GetString(tr.Namespace);
        var n  = r.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromSpecification(MetadataReader r, object ctx, TypeSpecificationHandle h, byte raw) => "TypeSpec";
    public string GetUnderlyingEnumType(string type) => type;
    public bool IsSystemType(string type) => type == "System.Type";

    PrimitiveTypeCode ICustomAttributeTypeProvider<string>.GetUnderlyingEnumType(string type)
    {
        throw new NotImplementedException();
    }
}