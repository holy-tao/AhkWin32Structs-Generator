using System.Reflection.Metadata;

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

    public static IEnumerable<string> GetAllNames(MetadataReader reader, TypeDefinition typeDef)
    {
        return GetAll(reader, typeDef).Select(td => reader.GetString(td.Name));
    }
}