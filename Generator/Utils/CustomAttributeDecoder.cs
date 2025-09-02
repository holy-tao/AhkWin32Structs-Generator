using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

public record CustomAttributeArgs(IEnumerable<ArgInfo> FixedArgs, IEnumerable<ArgInfo> NamedArgs);

public readonly record struct ArgInfo(string Name, string Type, object Value, bool IsFixed);

public class CustomAttributeDecoder
{
    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, TypeDefinition typeDef)
    {
        return typeDef.GetCustomAttributes()
            .Select(hAttr => reader.GetCustomAttribute(hAttr).Constructor)
            .Where(ctor => ctor.Kind == HandleKind.MemberReference)
            .Select(ctor => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent)
            .Where(parent => parent.Kind == HandleKind.TypeReference)
            .Select(parent => reader.GetTypeReference((TypeReferenceHandle)parent));
    }

    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, FieldDefinition fieldDef)
    {
        return fieldDef.GetCustomAttributes()
            .Select(hAttr => reader.GetCustomAttribute(hAttr).Constructor)
            .Where(ctor => ctor.Kind == HandleKind.MemberReference)
            .Select(ctor => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent)
            .Where(parent => parent.Kind == HandleKind.TypeReference)
            .Select(parent => reader.GetTypeReference((TypeReferenceHandle)parent));
    }

    public static CustomAttribute? GetAttribute(MetadataReader reader, FieldDefinition fieldDefinition, string targetAttr)
    {
        foreach (var attrHandle in fieldDefinition.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var ctorHandle = attr.Constructor;
            // Get the attribute type
            var attrTypeHandle = reader.GetMemberReference((MemberReferenceHandle)ctorHandle).Parent;
            var attrTypeRef = reader.GetTypeReference((TypeReferenceHandle)attrTypeHandle);
            var attrName = reader.GetString(attrTypeRef.Name);

            if (attrName == targetAttr)
            {
                return attr;
            }
        }

        return null;
    }

    public static CustomAttribute? GetAttribute(MetadataReader reader, TypeDefinition typeDef, string targetAttr)
    {
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var ctorHandle = attr.Constructor;
            // Get the attribute type
            var attrTypeHandle = reader.GetMemberReference((MemberReferenceHandle)ctorHandle).Parent;
            var attrTypeRef = reader.GetTypeReference((TypeReferenceHandle)attrTypeHandle);
            var attrName = reader.GetString(attrTypeRef.Name);

            if (attrName == targetAttr)
            {
                return attr;
            }
        }

        return null;
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, TypeDefinition typeDef)
    {
        return GetAll(reader, typeDef).Select(td => reader.GetString(td.Name));
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, FieldDefinition fieldDef)
    {
        return GetAll(reader, fieldDef).Select(td => reader.GetString(td.Name));
    }
}