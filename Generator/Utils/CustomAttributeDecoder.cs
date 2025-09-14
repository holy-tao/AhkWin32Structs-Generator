using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

public class CustomAttributeDecoder
{
    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, TypeDefinition def)
        => GetAllCustomAttributeTypeRefs(reader, def.GetCustomAttributes());

    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, FieldDefinition def)
        => GetAllCustomAttributeTypeRefs(reader, def.GetCustomAttributes());

    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, MethodDefinition def)
        => GetAllCustomAttributeTypeRefs(reader, def.GetCustomAttributes());

    public static IEnumerable<TypeReference> GetAll(MetadataReader reader, Parameter def)
        => GetAllCustomAttributeTypeRefs(reader, def.GetCustomAttributes());

    public static CustomAttribute? GetAttribute(MetadataReader reader, FieldDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, TypeDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, MethodDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, Parameter def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static IEnumerable<string> GetAllNames(MetadataReader reader, TypeDefinition typeDef)
    {
        return GetAll(reader, typeDef).Select(td => reader.GetString(td.Name));
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, FieldDefinition fieldDef)
    {
        return GetAll(reader, fieldDef).Select(td => reader.GetString(td.Name));
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, MethodDefinition methodDef)
    {
        return GetAll(reader, methodDef).Select(td => reader.GetString(td.Name));
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, Parameter param)
    {
        return GetAll(reader, param).Select(td => reader.GetString(td.Name));
    }

    private static CustomAttribute? GetAttributeFromCollection(MetadataReader reader, CustomAttributeHandleCollection handles, string targetAttr)
    {
        foreach (var attrHandle in handles)
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

    private static IEnumerable<TypeReference> GetAllCustomAttributeTypeRefs(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        return handles
            .Select(hAttr => reader.GetCustomAttribute(hAttr).Constructor)
            .Where(ctor => ctor.Kind == HandleKind.MemberReference)
            .Select(ctor => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent)
            .Where(parent => parent.Kind == HandleKind.TypeReference)
            .Select(parent => reader.GetTypeReference((TypeReferenceHandle)parent));
    }
}