namespace AhkWin32.Generator.Metadata;

using System.Reflection;
using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// Extracts COM interface types from metadata into ComInterfaceType instances.
/// Ports logic from AhkComInterface constructor.
/// </summary>
public sealed class ComInterfaceExtractor
{
    private readonly MetadataLoader _loader;
    private readonly DocumentationLoader _docs;
    private readonly MethodExtractor _methodExtractor;
    private readonly ILogger<ComInterfaceExtractor> _logger;

    public ComInterfaceExtractor(
        MetadataLoader loader,
        DocumentationLoader docs,
        MethodExtractor methodExtractor,
        ILogger<ComInterfaceExtractor> logger
    )
    {
        _loader = loader;
        _docs = docs;
        _methodExtractor = methodExtractor;
        _logger = logger;
    }

    /// <summary>
    /// Extract a ComInterfaceType from a TypeDefinition.
    /// </summary>
    public ComInterfaceType? ExtractComInterface(
        MetadataReader reader,
        TypeDefinition typeDef,
        string assemblyName,
        string version,
        TypeAttrs attrs,
        out int methodArchSkips
    )
    {
        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        try
        {
            return ExtractComInterfaceCore(
                reader,
                typeDef,
                typeName,
                typeNamespace,
                fqn,
                assemblyName,
                version,
                attrs,
                out methodArchSkips
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract COM interface {FQN}", fqn);
            methodArchSkips = 0;
            return null;
        }
    }

    private ComInterfaceType ExtractComInterfaceCore(
        MetadataReader reader,
        TypeDefinition typeDef,
        string typeName,
        string typeNamespace,
        string fqn,
        string assemblyName,
        string version,
        TypeAttrs attrs,
        out int methodArchSkips
    )
    {
        // Extract IID
        Guid? iid = AttributeReader.DecodeGuid(reader, typeDef);

        // Find CLSID (search for coclass with name matching interface sans 'I' prefix)
        Guid? clsid = FindClsid(reader, typeNamespace, typeName);

        // Resolve base interface
        var (baseFQN, baseName) = ResolveBaseInterface(reader, typeDef, typeNamespace, typeName);

        // Compute VTable offset by walking inheritance chain
        int vTableOffset = ComputeVTableOffset(reader, typeDef);

        // Get documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, typeDef);

        // Extract methods
        List<ComMethodMember> methods = ExtractComMethods(
            reader,
            typeDef,
            typeNamespace,
            vTableOffset,
            apiDetails,
            out methodArchSkips
        );

        // Group properties from special-name get_/put_ methods
        List<ComPropertyMember> properties = GroupProperties(methods, apiDetails);

        // Build type identity
        TypeIdentity identity = new(fqn, attrs.SupportedArchitecture ?? Architecture.All);
        string displayName = typeName;

        // Collect imports
        ImportCollection imports = CollectImports(methods, baseFQN);

        ComInterfaceType result = new()
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = assemblyName,
            MetadataVersion = $"{assemblyName} v{version}",
            Flags = attrs.Flags,
            IID = iid,
            CLSID = clsid,
            BaseInterfaceFQN = baseFQN,
            BaseInterfaceName = baseName,
            Methods = methods,
            Properties = properties,
            VTableOffset = vTableOffset,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = attrs.DeprecationMessage,
            SupportedOSPlatform = attrs.SupportedOSPlatform,
            Imports = imports,
        };

        _logger.LogDebug(
            "Extracted ComInterfaceType {FQN} ({MethodCount} methods, {PropCount} properties, vtableOffset={Offset})",
            fqn,
            methods.Count,
            properties.Count,
            vTableOffset
        );

        return result;
    }

    /// <summary>
    /// Find CLSID by searching for a coclass type with matching name (sans 'I' prefix)
    /// in the same namespace. Port of AhkComInterface.GetClsid.
    /// </summary>
    private static Guid? FindClsid(MetadataReader reader, string typeNamespace, string typeName)
    {
        string coclassName = typeName.TrimStart('I');

        foreach (TypeDefinitionHandle hTd in reader.TypeDefinitions)
        {
            if (hTd.IsNil)
                continue;

            TypeDefinition td = reader.GetTypeDefinition(hTd);
            if (
                reader.StringComparer.Equals(td.Namespace, typeNamespace)
                && reader.StringComparer.Equals(td.Name, coclassName)
            )
            {
                return AttributeReader.DecodeGuid(reader, td);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the base interface for a COM interface.
    /// Returns (FQN, SimpleName) or (null, null) for root interfaces.
    /// Port of AhkComInterface.GetBaseTypeDef.
    /// </summary>
    private (string? FQN, string? Name) ResolveBaseInterface(
        MetadataReader reader,
        TypeDefinition typeDef,
        string typeNamespace,
        string typeName
    )
    {
        List<(MetadataReader Reader, TypeDefinition TypeDef)> impls = GetResolvedInterfaceImplementations(
            reader,
            typeDef
        );

        if (impls.Count == 0)
            return (null, null);

        if (impls.Count > 1)
        {
            _logger.LogWarning(
                "Interface {Namespace}.{Name} implements {Count} interfaces, expected 0 or 1: [{Names}]",
                typeNamespace,
                typeName,
                impls.Count,
                string.Join(", ", impls.Select(i => i.Reader.GetString(i.TypeDef.Name)))
            );
        }

        var (baseReader, baseTd) = impls[0];
        string baseName = baseReader.GetString(baseTd.Name);
        string baseNamespace = baseReader.GetString(baseTd.Namespace);
        string baseFQN = $"{baseNamespace}.{baseName}";

        return (baseFQN, baseName);
    }

    /// <summary>
    /// Resolve all directly implemented interfaces for a type definition.
    /// Handles both TypeReference and TypeDefinition interface handles.
    /// Port of AhkComInterface.GetResolvedInterfaceImplementations.
    /// </summary>
    private List<(MetadataReader Reader, TypeDefinition TypeDef)> GetResolvedInterfaceImplementations(
        MetadataReader reader,
        TypeDefinition forType
    )
    {
        List<(MetadataReader, TypeDefinition)> results = [];

        foreach (InterfaceImplementationHandle ih in forType.GetInterfaceImplementations())
        {
            EntityHandle iface = reader.GetInterfaceImplementation(ih).Interface;

            switch (iface.Kind)
            {
                case HandleKind.TypeReference:
                {
                    var (targetReader, targetHandle) = _loader.ResolveTypeReference(reader, (TypeReferenceHandle)iface);
                    results.Add((targetReader, targetReader.GetTypeDefinition(targetHandle)));
                    break;
                }
                case HandleKind.TypeDefinition:
                {
                    results.Add((reader, reader.GetTypeDefinition((TypeDefinitionHandle)iface)));
                    break;
                }
                default:
                    _logger.LogWarning("Unsupported interface handle kind {Kind}", iface.Kind);
                    break;
            }
        }

        return results;
    }

    /// <summary>
    /// Compute VTable offset by counting methods in the inheritance chain.
    /// Port of AhkComInterface.GetVTableOffset.
    /// </summary>
    private int ComputeVTableOffset(MetadataReader reader, TypeDefinition typeDef)
    {
        int offset = 0;
        var impls = GetResolvedInterfaceImplementations(reader, typeDef);

        while (impls.Count > 0)
        {
            var (baseReader, baseTd) = impls[0];
            offset += baseTd.GetMethods().Count;
            impls = GetResolvedInterfaceImplementations(baseReader, baseTd);
        }

        return offset;
    }

    /// <summary>
    /// Extract COM methods from a type's method definitions.
    /// </summary>
    private List<ComMethodMember> ExtractComMethods(
        MetadataReader reader,
        TypeDefinition typeDef,
        string typeNamespace,
        int vTableOffset,
        ApiDetails? apiDetails,
        out int archSkips
    )
    {
        List<ComMethodMember> methods = [];
        int methodIndex = 0;
        archSkips = 0;

        foreach (MethodDefinitionHandle hMethod in typeDef.GetMethods())
        {
            MethodDefinition methodDef = reader.GetMethodDefinition(hMethod);
            string methodName = reader.GetString(methodDef.Name);
            int vTableIndex = methodIndex + vTableOffset;

            try
            {
                ComMethodMember? comMethod = ExtractComMethod(
                    reader,
                    methodDef,
                    methodName,
                    typeNamespace,
                    vTableIndex,
                    methods,
                    out bool methodArchSkipped
                );

                if (comMethod != null)
                    methods.Add(comMethod);
                else if (methodArchSkipped)
                    archSkips++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract COM method {Namespace}.{Method}", typeNamespace, methodName);
            }

            methodIndex++;
        }

        return methods;
    }

    /// <summary>
    /// Extract a single COM method into a ComMethodMember.
    /// </summary>
    private ComMethodMember? ExtractComMethod(
        MetadataReader reader,
        MethodDefinition methodDef,
        string methodName,
        string typeNamespace,
        int vTableIndex,
        List<ComMethodMember> previousMethods,
        out bool archSkipped
    )
    {
        // Use MethodExtractor for base method data
        MethodExtractionResult baseResult = _methodExtractor.ExtractMethod(reader, methodDef, typeNamespace);
        archSkipped = baseResult.SkipReason == MethodSkipReason.ArchFiltered;
        MethodMember? baseMember = baseResult.Method;

        if (baseMember == null)
            return null;

        // Check for string (BSTR) parameters
        bool hasStringParam = baseMember
            .Parameters.Skip(1) // skip return type
            .Any(p => p.TypeDefName is "BSTR");

        // Check for special name (get_/put_ for property backing)
        bool isSpecialName = methodDef.Attributes.HasFlag(MethodAttributes.SpecialName);

        // Compute COM-specific output parameter (different logic from DllImport)
        ParameterMember? outputParameter = GetComOutputParameter(
            baseMember.Parameters,
            baseMember.CanReturnErrorsAsSuccess
        );

        // Compute deduplicated name (append counter for overloaded methods)
        int overloadCount = previousMethods.Count(m => m.Name == methodName && m.VTableIndex < vTableIndex);
        string deduplicatedName = overloadCount > 0 ? methodName + overloadCount : methodName;

        return new ComMethodMember
        {
            // Base MethodMember properties
            Name = methodName,
            Namespace = typeNamespace,
            DllName = baseMember.DllName,
            EntryPoint = baseMember.EntryPoint,
            CallingConvention = baseMember.CallingConvention,
            CharSet = baseMember.CharSet,
            SetsLastError = baseMember.SetsLastError,
            PreserveSig = baseMember.PreserveSig,
            CanReturnErrorsAsSuccess = baseMember.CanReturnErrorsAsSuccess,
            CanReturnMultipleSuccessValues = baseMember.CanReturnMultipleSuccessValues,
            Parameters = baseMember.Parameters,
            OutputParameter = outputParameter,
            ShouldThrowOnHResult = baseMember.ShouldThrowOnHResult,
            Description = baseMember.Description,
            Remarks = baseMember.Remarks,
            HelpLink = baseMember.HelpLink,
            DeprecationMessage = baseMember.DeprecationMessage,
            ReturnValueDoc = baseMember.ReturnValueDoc,
            SupportedOSPlatform = baseMember.SupportedOSPlatform,
            Imports = baseMember.Imports,
            // ComMethodMember-specific properties
            VTableIndex = vTableIndex,
            HasStringParam = hasStringParam,
            IsSpecialName = isSpecialName,
            DeduplicatedName = deduplicatedName,
        };
    }

    /// <summary>
    /// Determine the logical output parameter for a COM method.
    /// Different from DllImport: checks [RetVal] first, and includes PtrToStruct in candidates.
    /// Port of AhkComMethod.GetOutputParameter.
    /// </summary>
    private static ParameterMember? GetComOutputParameter(
        IReadOnlyList<ParameterMember> parameters,
        bool canReturnErrorsAsSuccess
    )
    {
        if (parameters.Count == 0 || parameters[0].Type is not HResultType)
            return null;

        if (canReturnErrorsAsSuccess)
            return null;

        // Check for explicit [RetVal] first
        ParameterMember? retVal = parameters.SingleOrDefault(p => p.IsRetVal);
        if (retVal != null)
            return retVal;

        // Fallback: single [out] !in pointer-to-(primitive|struct|com|handle)
        var candidates = parameters
            .Where(p => p.IsOut && !p.IsIn)
            .Where(p => p.IsPtrToPrimitive || p.IsPtrToStruct || p.IsPtrToCom || p.IsPtrToHandle)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Group special-name get_/put_ methods into ComPropertyMember instances.
    /// Port of AhkComInterface property collection logic.
    /// </summary>
    private static List<ComPropertyMember> GroupProperties(List<ComMethodMember> methods, ApiDetails? apiDetails)
    {
        List<ComPropertyMember> properties = [];

        foreach (ComMethodMember method in methods.Where(m => m.IsSpecialName))
        {
            string normalizedName = method.DeduplicatedName[4..]; // Remove "get_" or "put_"
            if (properties.Any(p => p.Name == normalizedName))
                continue;

            ComMethodMember? getter = methods.FirstOrDefault(m =>
                m.IsSpecialName && m.DeduplicatedName == "get_" + normalizedName
            );
            ComMethodMember? setter = methods.FirstOrDefault(m =>
                m.IsSpecialName && m.DeduplicatedName == "put_" + normalizedName
            );

            string? description = null;
            apiDetails?.Fields.TryGetValue(normalizedName, out description);

            properties.Add(
                new ComPropertyMember
                {
                    Name = normalizedName,
                    Getter = getter,
                    Setter = setter,
                    Description = description,
                }
            );
        }

        return properties;
    }

    /// <summary>
    /// Collect imports for a COM interface.
    /// </summary>
    private static ImportCollection CollectImports(List<ComMethodMember> methods, string? baseInterfaceFQN)
    {
        var imports = new ImportCollection();

        // Base interface
        if (baseInterfaceFQN != null)
            imports.AddType(baseInterfaceFQN);

        // Per-method imports
        foreach (ComMethodMember method in methods)
        {
            imports.MergeFrom(method.Imports);

            // BSTR import
            if (method.HasStringParam)
                imports.AddType("Windows.Win32.Foundation.BSTR");

            // COM output parameter types (computed separately from base method extraction)
            if (method.OutputParameter is { } outParam)
            {
                string? outFqn = outParam.Type switch
                {
                    PointerType { Pointee: StructRef s } => s.FQN,
                    PointerType { Pointee: ComRef c } => c.FQN,
                    PointerType { Pointee: HandleRef h } => h.FQN,
                    _ => null,
                };
                if (outFqn != null)
                    imports.AddType(outFqn);
            }
        }

        return imports;
    }
}
