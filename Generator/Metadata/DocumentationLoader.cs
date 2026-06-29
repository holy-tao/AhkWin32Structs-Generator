namespace AhkWin32.Generator.Metadata;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection.Metadata;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// Loads and provides access to API documentation from apidocs.msgpack.
/// Instance-based replacement for the static DocumentationUtils, with proper DI.
/// </summary>
public sealed class DocumentationLoader
{
    private static readonly CaTypeProvider s_attrProvider = new();

    private FrozenDictionary<string, ApiDetails> _apiDocs = FrozenDictionary<string, ApiDetails>.Empty;
    private readonly ILogger<DocumentationLoader> _logger;

    public DocumentationLoader(ILogger<DocumentationLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Load API documentation from the msgpack file.
    /// </summary>
    public void Load(string filepath)
    {
        _logger.LogInformation("Loading ApiDocs from {FilePath}...", filepath);
        Stopwatch watch = Stopwatch.StartNew();

        using FileStream stream = File.OpenRead(filepath);
        _apiDocs = MessagePackSerializer
            .Deserialize<Dictionary<string, ApiDetails>>(stream)
            .ToFrozenDictionary(StringComparer.Ordinal);

        watch.Stop();
        _logger.LogInformation(
            "Loaded {Count} ApiDetails records in {Elapsed}ms",
            _apiDocs.Count,
            watch.ElapsedMilliseconds
        );
    }

    /// <summary>
    /// Look up API documentation for a TypeDefinition.
    /// Tries FQN, then simple name.
    /// </summary>
    public ApiDetails? GetApiDetails(MetadataReader reader, TypeDefinition typeDef)
    {
        CustomAttribute? docAttr = AttributeReader.FindAttribute(
            reader,
            typeDef.GetCustomAttributes(),
            "DocumentationAttribute"
        );

        string fqn = $"{reader.GetString(typeDef.Namespace)}.{reader.GetString(typeDef.Name)}";

        ApiDetails? found = GetApiDetails(fqn, docAttr);
        found ??= GetApiDetails(reader.GetString(typeDef.Name), docAttr);

        return found;
    }

    /// <summary>
    /// Look up API documentation for a MethodDefinition.
    /// Tries qualified name (Declarer.Method), FQN, then simple name.
    /// </summary>
    public ApiDetails? GetApiDetails(MetadataReader reader, MethodDefinition def)
    {
        string methodName = reader.GetString(def.Name);
        CustomAttribute? docAttr = AttributeReader.FindAttribute(
            reader,
            def.GetCustomAttributes(),
            "DocumentationAttribute"
        );
        ApiDetails? details = null;

        // Check parent type for qualified lookup
        TypeDefinitionHandle handle = def.GetDeclaringType();
        if (!handle.IsNil)
        {
            TypeDefinition parentTypeDef = reader.GetTypeDefinition(handle);
            string declarerName = reader.GetString(parentTypeDef.Name);
            string declarerNamespace = reader.GetString(parentTypeDef.Namespace);
            string qualifiedName = $"{declarerName}.{methodName}";
            string fullyQualifiedName = $"{declarerNamespace}.{declarerName}.{methodName}";

            details = GetApiDetails(qualifiedName, docAttr);
            details ??= GetApiDetails(fullyQualifiedName, docAttr);
        }

        // Fallback to method name only
        details ??= GetApiDetails(methodName, docAttr);

        return details;
    }

    /// <summary>
    /// Core lookup: search the trie by name, optionally enriching HelpLink from [DocumentationAttribute].
    /// </summary>
    private ApiDetails? GetApiDetails(string forName, CustomAttribute? documentationAttr)
    {
        if (!_apiDocs.TryGetValue(forName, out ApiDetails? details))
        {
            return null;
        }

        // Fall back to [Documentation] attribute if HelpLink is null
        if (details.HelpLink == null && documentationAttr != null)
        {
            var decoded = documentationAttr.Value.DecodeValue(s_attrProvider);
            string uriString = (string)(
                decoded.FixedArguments[0].Value ?? throw new NullReferenceException("Null DocumentationAttribute URI")
            );
            details.HelpLink = new Uri(uriString);
        }

        return details;
    }
}
