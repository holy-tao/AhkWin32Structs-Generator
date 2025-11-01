using System.Reflection.Metadata;

public readonly record struct CAInfo(string Namespace, string Name, CustomAttributeValue<string> Attr);

public class CustomAttributeDecoder
{
    public static CustomAttribute? GetAttribute(MetadataReader reader, FieldDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, TypeDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, MethodDefinition def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static CustomAttribute? GetAttribute(MetadataReader reader, Parameter def, string targetAttr)
        => GetAttributeFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static List<CustomAttribute> GetAllAttributes(MetadataReader reader, FieldDefinition def, string targetAttr)
        => GetAllAttributesFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static List<CustomAttribute> GetAllAttributes(MetadataReader reader, TypeDefinition def, string targetAttr)
        => GetAllAttributesFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static List<CustomAttribute> GetAllAttributes(MetadataReader reader, MethodDefinition def, string targetAttr)
        => GetAllAttributesFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static List<CustomAttribute> GetAllAttributes(MetadataReader reader, Parameter def, string targetAttr)
        => GetAllAttributesFromCollection(reader, def.GetCustomAttributes(), targetAttr);

    public static List<CAInfo> DecodeAll(MetadataReader reader, FieldDefinition def)
        => DecodeAll(reader, def.GetCustomAttributes());

    public static List<CAInfo> DecodeAll(MetadataReader reader, TypeDefinition def)
        => DecodeAll(reader, def.GetCustomAttributes());

    public static List<CAInfo> DecodeAll(MetadataReader reader, MethodDefinition def)
        => DecodeAll(reader, def.GetCustomAttributes());

    public static List<CAInfo> DecodeAll(MetadataReader reader, Parameter def)
        => DecodeAll(reader, def.GetCustomAttributes());

    public static IEnumerable<string> GetAllNames(MetadataReader reader, TypeDefinition typeDef)
    {
        return GetAllNamesFromCollection(reader, typeDef.GetCustomAttributes());
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, FieldDefinition fieldDef)
    {
        return GetAllNamesFromCollection(reader, fieldDef.GetCustomAttributes());
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, MethodDefinition methodDef)
    {
        return GetAllNamesFromCollection(reader, methodDef.GetCustomAttributes());
    }

    public static IEnumerable<string> GetAllNames(MetadataReader reader, Parameter param)
    {
        return GetAllNamesFromCollection(reader, param.GetCustomAttributes());
    }

    private static List<string> GetAllNamesFromCollection(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        List<string> names = [];

        foreach (var attrHandle in handles)
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            (string _, string attrName) = GetAttributeTypeName(reader, attr);

            names.Add(attrName);
        }

        return names;
    }

    private static CustomAttribute? GetAttributeFromCollection(MetadataReader reader, CustomAttributeHandleCollection handles, string targetAttr)
    {
        foreach (var attrHandle in handles)
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            (string _, string attrName) = GetAttributeTypeName(reader, attr);

            if (attrName == targetAttr)
            {
                return attr;
            }
        }

        return null;
    }

    private static List<CustomAttribute> GetAllAttributesFromCollection(MetadataReader reader, CustomAttributeHandleCollection handles, string targetAttr)
    {
        List<CustomAttribute> foundAttrs = [];

        foreach (var attrHandle in handles)
        {
            var attr = reader.GetCustomAttribute(attrHandle);            
            (string _, string attrName) = GetAttributeTypeName(reader, attr);

            if (attrName == targetAttr)
            {
                foundAttrs.Add(attr);
            }
        }

        return foundAttrs;
    }

    public static List<CAInfo> DecodeAll(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        List<CAInfo> infos = [];
        CaTypeProvider provider = new();

        foreach (var attrHandle in handles)
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            (string attrNamespace, string attrName) = GetAttributeTypeName(reader, attr);

            infos.Add(new(attrNamespace, attrName, attr.DecodeValue(provider)));
        }

        return infos;
    }

    private static (string Namespace, string Name) GetAttributeTypeName(MetadataReader reader, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                {
                    var mr = reader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    var parent = mr.Parent;

                    if (parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = reader.GetTypeReference((TypeReferenceHandle)parent);
                        return (reader.GetString(tr.Namespace), reader.GetString(tr.Name));
                    }
                    else if (parent.Kind == HandleKind.TypeDefinition)
                    {
                        var td = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                        return (reader.GetString(td.Namespace), reader.GetString(td.Name));
                    }
                    break;
                }

            case HandleKind.MethodDefinition:
                {
                    var md = reader.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                    var td = reader.GetTypeDefinition(md.GetDeclaringType());
                    return (reader.GetString(td.Namespace), reader.GetString(td.Name));
                }
        }

        throw new NotSupportedException(attr.Constructor.Kind.ToString());
    }

}