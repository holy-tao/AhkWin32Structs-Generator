namespace AhkWin32.Generator.Metadata;

using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// Orchestrates type extraction from metadata assemblies.
/// Iterates TypeDefinitions, classifies each type, and delegates to
/// struct/enum/handle extraction, registering results in a TypeRegistry.
/// </summary>
public sealed class TypeExtractor
{
    /// <summary>
    /// Reserved names in AutoHotkey that must be deconflicted.
    /// </summary>
    private static readonly string[] s_reservedNames = ["string", "number", "float", "integer"];

    private readonly MetadataLoader _loader;
    private readonly DocumentationLoader _docs;
    private readonly ILogger<TypeExtractor> _logger;
    private readonly FieldExtractor _fieldExtractor;
    private readonly MethodExtractor _methodExtractor;
    private readonly ComInterfaceExtractor _comExtractor;

    public TypeExtractor(MetadataLoader loader, DocumentationLoader docs,
        ILoggerFactory loggerFactory)
    {
        _loader = loader;
        _docs = docs;
        _logger = loggerFactory.CreateLogger<TypeExtractor>();
        _fieldExtractor = new FieldExtractor(loader, _logger, ExtractStructRecursive);

        var paramExtractor = new ParameterExtractor(loader, loggerFactory.CreateLogger<ParameterExtractor>());
        _methodExtractor = new MethodExtractor(docs, paramExtractor,
            loggerFactory.CreateLogger<MethodExtractor>());
        _comExtractor = new ComInterfaceExtractor(loader, docs, _methodExtractor,
            loggerFactory.CreateLogger<ComInterfaceExtractor>());
    }

    /// <summary>
    /// Extract all types from all loaded primary assemblies into a TypeRegistry.
    /// </summary>
    public TypeRegistry ExtractAll()
    {
        TypeRegistry registry = new();
        Stopwatch totalWatch = Stopwatch.StartNew();

        foreach (var (assemblyName, version, reader) in _loader.GetPrimaryAssemblies())
        {
            _logger.LogInformation("Extracting types from {AssemblyName} v{Version}...",
                assemblyName, version);

            Stopwatch asmWatch = Stopwatch.StartNew();
            ExtractionCounts counts = ExtractFromAssembly(reader, assemblyName, version, registry);
            asmWatch.Stop();

            _logger.LogInformation(
                "Extracted {StructCount} structs, {HandleCount} handles, {EnumCount} enums, {ComCount} COM, {ApiCount} APIs from {AssemblyName} in {Elapsed:F1}s",
                counts.Structs, counts.Handles, counts.Enums, counts.ComInterfaces, counts.ApiTypes,
                assemblyName, asmWatch.Elapsed.TotalSeconds);
            _logger.LogInformation(
                "  Skipped: {Arch} arch-filtered, {Nested} nested, {Delegate} delegates, {Other} other, {Errors} errors",
                counts.SkippedArch, counts.SkippedNested, counts.SkippedDelegate, counts.SkippedOther, counts.Errors);

            if (counts.SkippedArch > 0)
            {
                _logger.LogWarning(
                    "Skipped {Count} types due to unsupported architecture (use --log-level Debug for details)",
                    counts.SkippedArch);
            }
        }

        totalWatch.Stop();
        _logger.LogInformation("Extraction complete: {TotalTypes} types in TypeRegistry ({Elapsed:F1}s total)",
            registry.Count, totalWatch.Elapsed.TotalSeconds);

        return registry;
    }

    /// <summary>
    /// Counts from a single assembly extraction pass.
    /// </summary>
    public sealed record ExtractionCounts(
        int Structs, int Handles, int Enums,
        int ComInterfaces, int ApiTypes,
        int SkippedArch, int SkippedNested, int SkippedDelegate, int SkippedOther,
        int Errors);

    /// <summary>
    /// Extract all types from a single assembly's MetadataReader.
    /// </summary>
    private ExtractionCounts ExtractFromAssembly(
        MetadataReader reader, string assemblyName, string version, TypeRegistry registry)
    {
        int structCount = 0, handleCount = 0, enumCount = 0;
        int comCount = 0, apiCount = 0;
        int archSkipCount = 0, nestedSkipCount = 0, delegateSkipCount = 0, otherSkipCount = 0;
        int errorCount = 0;

        reader.TypeDefinitions.AsParallel().ForAll(hTypeDef =>
        {
            TypeDefinition typeDef = reader.GetTypeDefinition(hTypeDef);
            string typeName = reader.GetString(typeDef.Name);
            string typeNamespace = reader.GetString(typeDef.Namespace);
            string fqn = $"{typeNamespace}.{typeName}";

            // Skip non-extractable types
            SkipReason? skipReason = ShouldSkipType(reader, hTypeDef, typeDef);
            if (skipReason.HasValue)
            {
                _logger.LogDebug("Skipping type {FQN}: {Reason}", fqn, skipReason.Value);
                switch (skipReason.Value)
                {
                    case SkipReason.Nested: Interlocked.Increment(ref nestedSkipCount); break;
                    case SkipReason.Delegate: Interlocked.Increment(ref delegateSkipCount); break;
                    default: Interlocked.Increment(ref otherSkipCount); break;
                }
                return;
            }

            try
            {
                // Decode attributes (single pass)
                int fieldCount = typeDef.GetFields().Count;
                TypeAttrs attrs = AttributeReader.DecodeTypeAttributes(reader, typeDef, fieldCount, _logger);

                // Architecture filter
                if (attrs.SupportedArchitecture.HasValue)
                {
                    Architecture arch = attrs.SupportedArchitecture.Value;
                    if (!arch.HasFlag(Architecture.X64) && !arch.HasFlag(Architecture.Arm64))
                    {
                        Interlocked.Increment(ref archSkipCount);
                        _logger.LogDebug("Skipping type {FQN}: unsupported architecture {Arch}",
                            fqn, arch);
                        return;
                    }
                }

                // Classify and extract
                TypeKind? kind = ClassifyType(reader, hTypeDef, typeDef, attrs);
                if (kind == null)
                {
                    _logger.LogDebug("Skipping type {FQN}: non-extractable", fqn);
                    Interlocked.Increment(ref otherSkipCount);
                    return;
                }

                Win32Type? extracted = kind.Value switch
                {
                    TypeKind.Handle => ExtractHandle(reader, typeDef, assemblyName, version, attrs),
                    TypeKind.Struct => ExtractStruct(reader, typeDef, assemblyName, version, attrs),
                    TypeKind.Enum => ExtractEnum(reader, typeDef, assemblyName, version, attrs),
                    TypeKind.ComInterface => _comExtractor.ExtractComInterface(reader, typeDef, assemblyName, version, attrs),
                    TypeKind.ApiType => ExtractApiType(reader, typeDef, assemblyName, version, attrs),
                    _ => null
                };

                if (extracted != null)
                {
                    registry.Register(extracted);

                    switch (extracted)
                    {
                        case HandleType:
                            Interlocked.Increment(ref handleCount);
                            break;
                        case StructType:
                            Interlocked.Increment(ref structCount);
                            break;
                        case EnumType:
                            Interlocked.Increment(ref enumCount);
                            break;
                        case ComInterfaceType:
                            Interlocked.Increment(ref comCount);
                            break;
                        case ApiType:
                            Interlocked.Increment(ref apiCount);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errorCount);
                _logger.LogError(ex, "Failed to extract {FQN}", fqn);
            }
        });

        return new ExtractionCounts(
            structCount, handleCount, enumCount,
            comCount, apiCount,
            archSkipCount, nestedSkipCount, delegateSkipCount, otherSkipCount,
            errorCount);
    }

    // --- Type classification ---

    private enum TypeKind { Struct, Handle, Enum, ComInterface, ApiType }

    private enum SkipReason { Module, NotTypeReference, Delegate, Attribute, Nested }

    /// <summary>
    /// Check if a type should be skipped entirely. Port of Program.ShouldSkipType.
    /// </summary>
    private static SkipReason? ShouldSkipType(
        MetadataReader reader, TypeDefinitionHandle handle, TypeDefinition typeDef)
    {
        if (typeDef.BaseType.IsNil)
        {
            return reader.StringComparer.Equals(typeDef.Name, "<Module>")
                ? SkipReason.Module : null;
        }

        if (typeDef.BaseType.Kind is not HandleKind.TypeReference)
            return null; // non-reference base type — still process

        TypeReference baseTypeRef = reader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string baseTypeName = reader.GetString(baseTypeRef.Name);

        if (baseTypeName == "MulticastDelegate")
            return SkipReason.Delegate;
        if (baseTypeName == "Attribute")
            return SkipReason.Attribute;
        if (baseTypeName == "<Module>")
            return SkipReason.Module;

        if (typeDef.IsNested)
            return SkipReason.Nested;

        return null;
    }

    /// <summary>
    /// Classify a non-skipped type into its extraction kind. Port of Program.ParseType.
    /// </summary>
    private static TypeKind? ClassifyType(
        MetadataReader reader, TypeDefinitionHandle handle, TypeDefinition typeDef, TypeAttrs attrs)
    {
        // Interface = COM. There are no other kinds of interfaces in win32metadata
        if ((typeDef.Attributes & TypeAttributes.Interface) != 0)
            return TypeKind.ComInterface;

        // Need base type reference for further classification
        if (typeDef.BaseType.IsNil || typeDef.BaseType.Kind != HandleKind.TypeReference)
            return null;

        TypeReference baseTypeRef = reader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
        string baseTypeName = reader.GetString(baseTypeRef.Name);
        string typeName = reader.GetString(typeDef.Name);

        // Object + "Apis" = API container
        if (baseTypeName == "Object" && typeName == "Apis")
            return TypeKind.ApiType;

        return baseTypeName switch
        {
            "Enum" => TypeKind.Enum,
            "Struct" or "ValueType" when attrs.IsHandle => TypeKind.Handle,
            "Struct" or "ValueType" => TypeKind.Struct,
            _ => null
        };
    }

    // --- Struct extraction ---

    /// <summary>
    /// Extract a StructType from a TypeDefinition.
    /// </summary>
    private StructType ExtractStruct(
        MetadataReader reader, TypeDefinition typeDef,
        string assemblyName, string version, TypeAttrs attrs)
    {
        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        TypeIdentity identity = BuildIdentity(fqn, attrs);

        StructLayoutKind layoutKind = GetLayoutKind(typeDef);
        int packingSize = EstimatePackingSize(typeDef);
        bool isUnion = attrs.Flags.HasFlag(MemberFlags.Union);
        bool isAnsi = attrs.Flags.HasFlag(MemberFlags.Ansi);

        // Get API documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, typeDef);
        Dictionary<string, string>? apiFields = apiDetails?.Fields;

        // Extract fields with layout computation
        StructLayout layout = _fieldExtractor.ExtractFields(
            reader, typeDef, layoutKind, packingSize, isUnion, isAnsi, fqn, apiFields);

        // Build referenced types list
        List<string> referencedTypes = CollectStructReferencedTypes(layout.Fields);

        // Name deconfliction
        string displayName = DeconflictName(typeName);

        StructType result = new()
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = assemblyName,
            MetadataVersion = $"{assemblyName} v{version}",
            Flags = attrs.Flags,
            Size = layout.TotalSize,
            PackingSize = layout.PackingSize,
            LayoutKind = layoutKind,
            Members = layout.Fields,
            StructSizeFieldName = attrs.StructSizeFieldName,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = attrs.DeprecationMessage,
            SupportedOSPlatform = attrs.SupportedOSPlatform,
            ReferencedTypes = referencedTypes
        };

        _logger.LogDebug("Extracted StructType {FQN} ({FieldCount} fields, {Size} bytes)",
            fqn, layout.Fields.Count, layout.TotalSize);

        return result;
    }

    /// <summary>
    /// Extract a HandleType from a TypeDefinition (extends struct extraction).
    /// </summary>
    private HandleType ExtractHandle(
        MetadataReader reader, TypeDefinition typeDef,
        string assemblyName, string version, TypeAttrs attrs)
    {
        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        TypeIdentity identity = BuildIdentity(fqn, attrs);

        StructLayoutKind layoutKind = GetLayoutKind(typeDef);
        int packingSize = EstimatePackingSize(typeDef);
        bool isAnsi = attrs.Flags.HasFlag(MemberFlags.Ansi);

        // Get API documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, typeDef);

        // Extract fields
        StructLayout layout = _fieldExtractor.ExtractFields(
            reader, typeDef, layoutKind, packingSize, false, isAnsi, fqn, apiDetails?.Fields);

        // Handle-specific attributes
        IReadOnlyList<long> invalidValues = AttributeReader.DecodeInvalidHandleValues(attrs.All);
        FreeFuncRef? freeFunc = AttributeReader.DecodeRAIIFreeFunc(attrs.All, typeNamespace);

        // Build referenced types
        List<string> referencedTypes = CollectStructReferencedTypes(layout.Fields);
        if (freeFunc != null)
            referencedTypes.Add(freeFunc.ApisFQN);

        string displayName = DeconflictName(typeName);

        HandleType result = new()
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = assemblyName,
            MetadataVersion = $"{assemblyName} v{version}",
            Flags = attrs.Flags,
            Size = layout.TotalSize,
            PackingSize = layout.PackingSize,
            LayoutKind = layoutKind,
            Members = layout.Fields,
            InvalidValues = invalidValues,
            FreeFunc = freeFunc,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = attrs.DeprecationMessage,
            SupportedOSPlatform = attrs.SupportedOSPlatform,
            ReferencedTypes = referencedTypes
        };

        _logger.LogDebug("Extracted HandleType {FQN} (invalidValues=[{Values}], freeFunc={FuncName})",
            fqn, string.Join(", ", invalidValues), freeFunc?.Name ?? "null");

        return result;
    }

    // --- Enum extraction ---

    /// <summary>
    /// Extract an EnumType from a TypeDefinition.
    /// Port of AhkEnum constructor + AhkConstant for constant value decoding.
    /// </summary>
    private EnumType ExtractEnum(
        MetadataReader reader, TypeDefinition typeDef,
        string assemblyName, string version, TypeAttrs attrs)
    {
        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        TypeIdentity identity = BuildIdentity(fqn, attrs);

        // Get API documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, typeDef);

        // Get underlying type
        string underlyingTypeName = SignatureDecoder.GetEnumUnderlyingType(reader,
            FindTypeDefHandle(reader, typeDef));

        // Extract constants
        List<ConstantMember> constants = [];
        foreach (FieldDefinitionHandle fieldHandle in typeDef.GetFields())
        {
            FieldDefinition fieldDef = reader.GetFieldDefinition(fieldHandle);
            string fieldName = reader.GetString(fieldDef.Name);

            // Skip the backing value__ field
            if (fieldName == "value__")
                continue;

            ConstantMember? constant = ExtractEnumConstant(reader, fieldDef, fieldName, apiDetails);
            if (constant != null)
                constants.Add(constant);
        }

        string displayName = DeconflictName(typeName);

        EnumType result = new()
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = assemblyName,
            MetadataVersion = $"{assemblyName} v{version}",
            Flags = attrs.Flags,
            Constants = constants,
            IsFlags = attrs.IsFlags,
            UnderlyingTypeName = underlyingTypeName,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = attrs.DeprecationMessage,
            SupportedOSPlatform = attrs.SupportedOSPlatform
        };

        _logger.LogDebug("Extracted EnumType {FQN} ({ConstantCount} constants, flags={IsFlags})",
            fqn, constants.Count, attrs.IsFlags);

        return result;
    }

    // --- API type extraction ---

    /// <summary>
    /// Extract an ApiType from an "Apis" TypeDefinition.
    /// Port of AhkApiType constructor.
    /// </summary>
    private ApiType ExtractApiType(
        MetadataReader reader, TypeDefinition typeDef,
        string assemblyName, string version, TypeAttrs attrs)
    {
        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        TypeIdentity identity = BuildIdentity(fqn, attrs);

        // Get API documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, typeDef);

        // Extract constants (same as enum constants — reuse existing logic)
        List<ConstantMember> constants = [];
        foreach (FieldDefinitionHandle fieldHandle in typeDef.GetFields())
        {
            FieldDefinition fieldDef = reader.GetFieldDefinition(fieldHandle);
            string fieldName = reader.GetString(fieldDef.Name);

            if (fieldName == "value__")
                continue;

            ConstantMember? constant = ExtractEnumConstant(reader, fieldDef, fieldName, apiDetails);
            if (constant != null)
                constants.Add(constant);
        }

        // Extract methods, deduplicating by name (matching legacy AhkApiType behavior)
        List<MethodMember> methods = [];
        HashSet<string> seenMethodNames = [];
        foreach (MethodDefinitionHandle hMethod in typeDef.GetMethods())
        {
            MethodDefinition methodDef = reader.GetMethodDefinition(hMethod);
            string methodName = reader.GetString(methodDef.Name);

            if (!seenMethodNames.Add(methodName))
                continue; // Deduplicate by name

            MethodMember? method = _methodExtractor.ExtractMethod(reader, methodDef, typeNamespace);
            if (method != null)
                methods.Add(method);
        }

        // Collect referenced types from both constants and methods
        List<string> referencedTypes = [];
        foreach (ConstantMember c in constants)
            referencedTypes.AddRange(c.ReferencedTypes);
        foreach (MethodMember m in methods)
            referencedTypes.AddRange(m.ReferencedTypes);

        string displayName = DeconflictName(typeName);

        ApiType result = new()
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = assemblyName,
            MetadataVersion = $"{assemblyName} v{version}",
            Flags = attrs.Flags,
            Constants = constants,
            Methods = methods,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = attrs.DeprecationMessage,
            SupportedOSPlatform = attrs.SupportedOSPlatform,
            ReferencedTypes = referencedTypes.Distinct().ToList()
        };

        _logger.LogDebug("Extracted ApiType {FQN} ({ConstantCount} constants, {MethodCount} methods)",
            fqn, constants.Count, methods.Count);

        return result;
    }

    /// <summary>
    /// Extract a single enum constant from a FieldDefinition.
    /// </summary>
    private ConstantMember? ExtractEnumConstant(
        MetadataReader reader, FieldDefinition fieldDef, string fieldName,
        ApiDetails? apiDetails)
    {
        // Check for GUID constant
        Guid? guid = AttributeReader.DecodeGuid(reader, fieldDef);
        if (guid.HasValue)
        {
            string? desc = null;
            apiDetails?.Fields.TryGetValue(fieldName, out desc);
            return new ConstantMember
            {
                Name = fieldName,
                Value = new GuidConstantValue(guid.Value),
                Type = new PrimitiveType("Guid"),
                Description = desc,
                IsDeprecated = AttributeReader.GetAllAttributeNames(reader, fieldDef.GetCustomAttributes())
                    .Contains("ObsoleteAttribute"),
                NeedsGuid = true
            };
        }

        // Decode primitive constant value from blob
        ConstantHandle constHandle = fieldDef.GetDefaultValue();
        if (constHandle.IsNil)
            return null;

        Constant constant = reader.GetConstant(constHandle);
        BlobReader blob = reader.GetBlobReader(constant.Value);

        string formattedValue = FormatConstantValue(constant.TypeCode, ref blob, fieldName);
        string ahkTypeName = ConstantTypeCodeToAhkType(constant.TypeCode);

        string? description = null;
        apiDetails?.Fields.TryGetValue(fieldName, out description);

        return new ConstantMember
        {
            Name = fieldName,
            Value = new PrimitiveConstantValue(formattedValue, ahkTypeName),
            Type = new PrimitiveType(constant.TypeCode.ToString()),
            Description = description,
            IsDeprecated = AttributeReader.GetAllAttributeNames(reader, fieldDef.GetCustomAttributes())
                .Contains("ObsoleteAttribute")
        };
    }

    /// <summary>
    /// Format a constant value as an AHK literal string.
    /// Port of AhkConstant.GetValueAsAhk.
    /// </summary>
    private static string FormatConstantValue(ConstantTypeCode typeCode, ref BlobReader blob, string name)
    {
        object value = typeCode switch
        {
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int16 => blob.ReadInt16(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.SByte => blob.ReadSByte(),
            ConstantTypeCode.Single => blob.ReadSingle(),
            ConstantTypeCode.Double => blob.ReadDouble(),
            ConstantTypeCode.Char => blob.ReadChar(),
            ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
            _ => throw new NotSupportedException(
                $"Unexpected constant type {typeCode} for '{name}'")
        };

        return typeCode switch
        {
            ConstantTypeCode.Byte => $"0x{(byte)value:X2}",
            ConstantTypeCode.SByte => $"0x{(sbyte)value:X2}",
            ConstantTypeCode.String => EscapeAhkStringLiteral($"\"{value}\""),
            _ => value.ToString() ?? throw new InvalidOperationException($"Null ToString for constant '{name}'")
        };
    }

    /// <summary>
    /// Map ConstantTypeCode to AHK display type name.
    /// </summary>
    private static string ConstantTypeCodeToAhkType(ConstantTypeCode typeCode) => typeCode switch
    {
        ConstantTypeCode.Single or ConstantTypeCode.Double => "Float",
        ConstantTypeCode.String => "String",
        _ => $"Integer ({typeCode})"
    };

    /// <summary>
    /// Escape a string literal for AHK output.
    /// </summary>
    private static string EscapeAhkStringLiteral(string val)
    {
        StringBuilder sb = new();
        foreach (char c in val)
        {
            if (char.IsControl(c))
            {
                sb.Append($"\" Chr({(int)c}) \"");
                continue;
            }

            sb.Append(c switch
            {
                '\n' => "`n",
                '\t' => "`t",
                '\r' => "`r",
                '`' => "``",
                _ => c
            });
        }
        return sb.ToString();
    }

    // --- Helpers ---

    /// <summary>
    /// Build a TypeIdentity from an FQN and decoded attributes.
    /// </summary>
    private static TypeIdentity BuildIdentity(string fqn, TypeAttrs attrs)
    {
        Architecture arch = attrs.SupportedArchitecture ?? Architecture.All;
        return new TypeIdentity(fqn, arch);
    }

    /// <summary>
    /// Get the LayoutKind from TypeDefinition attributes.
    /// </summary>
    private static StructLayoutKind GetLayoutKind(TypeDefinition typeDef)
    {
        var attr = typeDef.Attributes & TypeAttributes.LayoutMask;
        return attr switch
        {
            TypeAttributes.SequentialLayout => StructLayoutKind.Sequential,
            TypeAttributes.ExplicitLayout => StructLayoutKind.Explicit,
            TypeAttributes.AutoLayout => StructLayoutKind.Auto,
            _ => StructLayoutKind.Sequential
        };
    }

    /// <summary>
    /// Estimate packing size from TypeLayout metadata.
    /// </summary>
    private static int EstimatePackingSize(TypeDefinition typeDef)
    {
        TypeLayout layout = typeDef.GetLayout();
        if (typeDef.Attributes.HasFlag(TypeAttributes.ExplicitLayout) && layout.PackingSize != 0)
            return layout.PackingSize;
        return 8;
    }

    /// <summary>
    /// Deconflict a type name against AHK reserved words.
    /// Strip _e__Struct suffix, prefix with Win32 if reserved.
    /// </summary>
    private static string DeconflictName(string name)
    {
        string candidate = name.EndsWith("_e__Struct")
            ? name[..^"_e__Struct".Length]
            : name;

        if (s_reservedNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            return $"Win32{candidate}";

        return candidate;
    }

    /// <summary>
    /// Collect FQNs of all types referenced by struct fields (for #Include generation).
    /// </summary>
    private static List<string> CollectStructReferencedTypes(IReadOnlyList<FieldMember> fields)
    {
        List<string> refs = [];

        foreach (FieldMember field in fields)
        {
            CollectTypeReferences(field.Type, refs);
        }

        return refs.Distinct().ToList();
    }

    /// <summary>
    /// Recursively collect type FQN references from a ResolvedType.
    /// </summary>
    private static void CollectTypeReferences(ResolvedType type, List<string> refs)
    {
        switch (type)
        {
            case StructRef s:
                refs.Add(s.FQN);
                break;
            case HandleRef h:
                refs.Add(h.FQN);
                break;
            case ComRef c:
                refs.Add(c.FQN);
                break;
            case EnumRef e:
                refs.Add(e.FQN);
                break;
            case PointerType p when p.Pointee != null:
                CollectTypeReferences(p.Pointee, refs);
                break;
            case ArrayType a:
                CollectTypeReferences(a.ElementType, refs);
                break;
        }
    }

    /// <summary>
    /// Callback for FieldExtractor to recursively extract an embedded struct.
    /// </summary>
    private StructType? ExtractStructRecursive(MetadataReader reader, TypeDefinition typeDef, bool isAnsi)
    {
        int fieldCount = typeDef.GetFields().Count;
        TypeAttrs attrs = AttributeReader.DecodeTypeAttributes(reader, typeDef, fieldCount);

        string typeName = reader.GetString(typeDef.Name);
        string typeNamespace = reader.GetString(typeDef.Namespace);
        string fqn = $"{typeNamespace}.{typeName}";

        // For embedded structs, we always extract as struct (even if it could be a handle)
        // because we need it for layout computation, not for standalone emission
        TypeIdentity identity = BuildIdentity(fqn, attrs);

        StructLayoutKind layoutKind = GetLayoutKind(typeDef);
        int packingSize = EstimatePackingSize(typeDef);
        bool isUnion = attrs.Flags.HasFlag(MemberFlags.Union);

        StructLayout layout = _fieldExtractor.ExtractFields(
            reader, typeDef, layoutKind, packingSize, isUnion, isAnsi, fqn);

        string displayName = DeconflictName(typeName);

        return new StructType
        {
            Identity = identity,
            Name = displayName,
            CanonicalName = typeName,
            AssemblyName = "",
            MetadataVersion = "",
            Flags = attrs.Flags,
            Size = layout.TotalSize,
            PackingSize = layout.PackingSize,
            LayoutKind = layoutKind,
            Members = layout.Fields,
            StructSizeFieldName = attrs.StructSizeFieldName
        };
    }

    /// <summary>
    /// Find the TypeDefinitionHandle for a TypeDefinition within its reader.
    /// </summary>
    private static TypeDefinitionHandle FindTypeDefHandle(MetadataReader reader, TypeDefinition typeDef)
    {
        string name = reader.GetString(typeDef.Name);
        string ns = reader.GetString(typeDef.Namespace);

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition td = reader.GetTypeDefinition(handle);
            if (reader.StringComparer.Equals(td.Name, name) &&
                reader.StringComparer.Equals(td.Namespace, ns))
            {
                return handle;
            }
        }

        throw new InvalidOperationException($"Could not find TypeDefinitionHandle for {ns}.{name}");
    }
}
