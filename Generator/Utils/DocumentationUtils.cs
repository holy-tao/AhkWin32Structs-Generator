using System.Reflection.Metadata;
using Microsoft.Windows.SDK.Win32Docs;

class DocumentationUtils
{
    static readonly CaTypeProvider attrProvider = new();

    public static ApiDetails? GetApiDetails(MetadataReader mr, TypeDefinition typeDef)
    {
        return GetApiDetails(mr.GetString(typeDef.Name), CustomAttributeDecoder.GetAttribute(mr, typeDef, "DocumentationAttribute"));
    }

    public static ApiDetails? GetApiDetails(MetadataReader mr, MethodDefinition typeDef)
    {
        return GetApiDetails(mr.GetString(typeDef.Name), CustomAttributeDecoder.GetAttribute(mr, typeDef, "DocumentationAttribute"));
    }

    public static ApiDetails? GetApiDetails(string forName, CustomAttribute? documentationAttr)
    {
        Program.ApiDocs.TryGetValue(forName, out ApiDetails? details);
        
        // Fall back to the [Documentation] attribute if HelpLink is null
        if(details?.HelpLink == null && documentationAttr != null)
        {
            details ??= new();
            var decoded = documentationAttr.Value.DecodeValue(attrProvider);
            string uriString = (string)(decoded.FixedArguments[0].Value ?? throw new NullReferenceException());

            details.HelpLink = new Uri(uriString);
        }

        return details;
    }

    public static string GetDeprecationMessage(MetadataReader mr, TypeDefinition td)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(mr, td, "ObsoleteAttribute");
        return attr.HasValue ? GetDeprecationMessage((CustomAttribute)attr) : string.Empty;
    }

    public static string GetDeprecationMessage(MetadataReader mr, MethodDefinition def)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(mr, def, "ObsoleteAttribute");
        return attr.HasValue ? GetDeprecationMessage((CustomAttribute)attr) : string.Empty;
    }

    public static string GetDeprecationMessage(MetadataReader mr, FieldDefinition def)
    {
        CustomAttribute? attr = CustomAttributeDecoder.GetAttribute(mr, def, "ObsoleteAttribute");
        return attr.HasValue ? GetDeprecationMessage((CustomAttribute)attr) : string.Empty;
    }

    public static string GetDeprecationMessage(CustomAttribute attr)
    {
        var decoded = attr.DecodeValue(new CaTypeProvider());

        string? message = (string?)decoded.FixedArguments.FirstOrDefault().Value;
        message ??= string.Empty;

        return message;
    }
}