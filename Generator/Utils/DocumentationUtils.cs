using System.Diagnostics;
using System.Reflection.Metadata;
using Gma.DataStructures.StringSearch;
using MessagePack;
using Microsoft.Windows.SDK.Win32Docs;

class DocumentationUtils
{
    static readonly CaTypeProvider attrProvider = new();

    public static readonly PatriciaTrie<ApiDetails> ApiDocs = new();

    public static void Load(string filepath)
    {
        Trace.TraceInformation($"Loading ApiDocs from {filepath}...");
        Stopwatch watch = Stopwatch.StartNew();
        using FileStream apiDocFileStream = File.OpenRead(filepath);

        int count = 0;
        foreach(var pair in MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(apiDocFileStream))
        {
            ApiDocs.Add(pair.Key, pair.Value);
            count++;
        }

        watch.Stop();
        Trace.TraceInformation($"Loaded {count} ApiDetails records in {watch.ElapsedMilliseconds} ms");
    }

    public static ApiDetails? GetApiDetails(MetadataReader mr, TypeDefinition typeDef)
    {
        // try type name and namespace.type
        CustomAttribute? docAttr = CustomAttributeDecoder.GetAttribute(mr, typeDef, "DocumentationAttribute");

        string fqn = $"{mr.GetString(typeDef.Namespace)}.{mr.GetString(typeDef.Name)}";

        ApiDetails? found = GetApiDetails(fqn, docAttr);
        found ??= GetApiDetails(fqn.Split('`').First(), docAttr);
        found ??= GetApiDetails(mr.GetString(typeDef.Name), docAttr);
        
        return found;
    }

    public static ApiDetails? GetApiDetails(MetadataReader mr, MethodDefinition def)
    {
        string methodName = mr.GetString(def.Name);
        CustomAttribute? documentationAttr = CustomAttributeDecoder.GetAttribute(mr, def, "DocumentationAttribute");
        ApiDetails? details = null;

        // First check parent to see if method is on a class or interface
        TypeDefinitionHandle handle = def.GetDeclaringType();
        if (!handle.IsNil)
        {
            TypeDefinition parentTypeDef = mr.GetTypeDefinition(handle);
            details = GetApiDetails(mr, parentTypeDef, def);
        }

        // Fallback to method name only (for e.g. Win32 free functions)
        details ??= GetApiDetails(methodName, documentationAttr);

        return details;
    }

    public static ApiDetails? GetApiDetails(MetadataReader mr, TypeDefinition parentTypeDef, MethodDefinition def)
    {
        ApiDetails? details;

        string methodName = mr.GetString(def.Name);
        CustomAttribute? documentationAttr = CustomAttributeDecoder.GetAttribute(mr, def, "DocumentationAttribute");

        string declarerName = mr.GetString(parentTypeDef.Name);
        string declarerNamespace = mr.GetString(parentTypeDef.Namespace);
        string qualifiedName = $"{declarerName}.{methodName}";
        string fullyQualifiedName = $"{declarerNamespace}.{declarerName}.{methodName}";

        // TODO we could probably optimize this further now that Program.ApiDetails is a Trie - might be worth it, doc lookup was a major bottleneck when it was just a dictionary
        details = GetApiDetails(qualifiedName, documentationAttr);
        details ??= GetApiDetails($"{fullyQualifiedName}", documentationAttr);

        return details == default(ApiDetails) ? null : details;
    }

    public static ApiDetails? GetApiDetails(string forName, CustomAttribute? documentationAttr)
    {
        IEnumerable<ApiDetails> matches = ApiDocs.Retrieve(forName);
        if(!matches.Any())
            return null;

        ApiDetails details = matches.First();
        
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