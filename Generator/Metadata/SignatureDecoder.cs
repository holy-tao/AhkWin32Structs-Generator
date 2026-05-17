namespace AhkWin32.Generator.Metadata;

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stub generic context — Win32Metadata doesn't use generics.
/// </summary>
public class SignatureGenericContext;

/// <summary>
/// Decodes metadata signatures into ResolvedType instances.
/// Implements ISignatureTypeProvider to plug into the System.Reflection.Metadata
/// signature decoding pipeline, producing ResolvedType directly instead of FieldInfo.
/// </summary>
public sealed class SignatureDecoder : ISignatureTypeProvider<ResolvedType, SignatureGenericContext>
{
    private readonly MetadataReader _reader;
    private readonly MetadataLoader _loader;
    private readonly ILogger _logger;
    private readonly TypeDefinition? _resolutionContext;

    /// <summary>
    /// WinRT namespaces that are external to Win32 — treated as pointers.
    /// </summary>
    private static readonly string[] s_excludeNamespaces =
        ["Windows.UI", "Windows.Foundation", "Windows.Graphics", "Windows.Storage"];

    private static readonly string[] s_handleAttrNames =
        ["RAIIFreeAttribute", "AlsoUsableForAttribute", "InvalidHandleValueAttribute"];

    public SignatureDecoder(MetadataReader reader, MetadataLoader loader, ILogger logger,
        TypeDefinition? resolutionContext = null)
    {
        _reader = reader;
        _loader = loader;
        _logger = logger;
        _resolutionContext = resolutionContext;
    }

    // --- ISignatureTypeProvider implementation ---

    public ResolvedType GetPrimitiveType(PrimitiveTypeCode typeCode)
        => new PrimitiveType(typeCode.ToString());

    public ResolvedType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => ClassifyTypeDef(reader, handle);

    public ResolvedType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        // Try nested type resolution in parent context first
        if (_resolutionContext != null)
        {
            TypeReference typeRef = _reader.GetTypeReference(handle);
            string typeName = _reader.GetString(typeRef.Name);

            foreach (TypeDefinitionHandle nestedHandle in _resolutionContext.Value.GetNestedTypes())
            {
                TypeDefinition nestedTd = _reader.GetTypeDefinition(nestedHandle);
                if (_reader.StringComparer.Equals(nestedTd.Name, typeName))
                {
                    _logger.LogTrace("Resolved nested type {Name} in context {ParentName}",
                        typeName, _reader.GetString(_resolutionContext.Value.Name));
                    return ClassifyTypeDef(_reader, nestedHandle);
                }
            }
        }

        // Fall back to global cross-assembly resolution
        var (targetReader, targetHandle) = _loader.ResolveTypeReference(_reader, handle);
        return ClassifyTypeDef(targetReader, targetHandle);
    }

    public ResolvedType GetTypeFromSpecification(MetadataReader reader, SignatureGenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        TypeSpecification ts = reader.GetTypeSpecification(handle);
        return ts.DecodeSignature(this, genericContext);
    }

    public ResolvedType GetArrayType(ResolvedType elementType, ArrayShape shape)
    {
        int length = shape.Rank == 1 && shape.Sizes.Length == 1 ? shape.Sizes[0] : 0;

        // Detect character arrays → StringType
        bool isString = elementType switch
        {
            PrimitiveType p when p.Name.Equals("Char", StringComparison.OrdinalIgnoreCase) => true,
            NativeTypedefRef n when n.Name is "CHAR" or "WCHAR" or "TCHAR" => true,
            // SByte that comes from a CHAR typedef — in Win32Metadata, CHAR is NativeTypedef over SByte,
            // so by the time we see it here it's already a NativeTypedefRef, not raw SByte.
            _ => false
        };

        if (isString)
        {
            StringEncoding encoding = elementType switch
            {
                NativeTypedefRef n when n.Name is "CHAR" => StringEncoding.Ansi,
                _ => StringEncoding.Unicode
            };
            return new StringType(length, encoding);
        }

        return new ArrayType(elementType, length);
    }

    public ResolvedType GetPointerType(ResolvedType elementType)
        => new PointerType(elementType);

    public ResolvedType GetByReferenceType(ResolvedType elementType)
        => new PointerType(elementType);

    public ResolvedType GetSZArrayType(ResolvedType elementType)
        => throw new NotSupportedException("SZARRAY not supported in Win32Metadata");

    public ResolvedType GetGenericInstantiation(ResolvedType genericType, ImmutableArray<ResolvedType> typeArguments)
        => new PrimitiveType($"{genericType.DisplayName}<{string.Join(",", typeArguments.Select(t => t.DisplayName))}>");

    public ResolvedType GetGenericMethodParameter(SignatureGenericContext genericContext, int index)
        => new PrimitiveType($"!!{index}");

    public ResolvedType GetGenericTypeParameter(SignatureGenericContext genericContext, int index)
        => new PrimitiveType($"!{index}");

    public ResolvedType GetModifiedType(ResolvedType modifier, ResolvedType unmodifiedType, bool isRequired)
        => unmodifiedType;

    public ResolvedType GetPinnedType(ResolvedType elementType)
        => elementType;

    public ResolvedType GetFunctionPointerType(MethodSignature<ResolvedType> signature)
    {
        string paramStr = string.Join(", ", signature.ParameterTypes.Select(p => p.DisplayName));
        string sig = $"({paramStr}) => {signature.ReturnType.DisplayName}";
        return new FunctionPointerType("FnPtr", sig);
    }

    // --- Method signature decoding ---

    /// <summary>
    /// Decode a method's full signature into ResolvedType return type and parameter types.
    /// Convenience wrapper that creates a SignatureDecoder and invokes DecodeSignature.
    /// </summary>
    public static (ResolvedType ReturnType, ResolvedType[] ParameterTypes) DecodeMethodSignature(
        MetadataReader reader, MethodDefinition methodDef,
        MetadataLoader loader, ILogger logger,
        TypeDefinition? resolutionContext = null)
    {
        var decoder = new SignatureDecoder(reader, loader, logger, resolutionContext);
#pragma warning disable CS8620 // Argument nullability — Win32Metadata has no generics
        var sig = methodDef.DecodeSignature(decoder, null);
#pragma warning restore CS8620
        return (sig.ReturnType, [.. sig.ParameterTypes]);
    }

    // --- Type classification ---

    /// <summary>
    /// Classify a TypeDefinition into the appropriate ResolvedType variant.
    /// Port of FieldSignatureDecoder.DecodeTypeDef.
    /// </summary>
    internal ResolvedType ClassifyTypeDef(MetadataReader reader, TypeDefinitionHandle tdHandle)
    {
        TypeDefinition td = reader.GetTypeDefinition(tdHandle);
        string rawName = reader.GetString(td.Name);
        string typeNamespace = reader.GetString(td.Namespace);
        string fqn = $"{typeNamespace}.{rawName}";
        // *Ref.Name carries the display name â€” strip the generator-injected
        // _e__Struct / _e__Union suffixes so it matches DeconflictName on the
        // referent. FQN stays as the metadata-form for registry lookups.
        string typeName = StripGeneratedSuffix(rawName);

        // Exclude WinRT namespaces — treat as pointer
        if (s_excludeNamespaces.Any(typeNamespace.StartsWith))
        {
            _logger.LogWarning("Treating external type {Namespace}.{TypeName} as pointer",
                typeNamespace, typeName);
            return new PointerType(null);
        }

        // Special well-known types
        if (typeName == "HRESULT")
            return new HResultType();
        if (typeName == "NTSTATUS")
            return new NtStatusType();

        // NativeTypedef (not a handle)
        if (IsNonHandleNativeTypedef(reader, td))
        {
            ResolvedType resolved = DecodeNativeTypedef(reader, td);
            _logger.LogDebug("Decoded NativeTypedef {Name} → {UnderlyingType}",
                typeName, ((NativeTypedefRef)resolved).Underlying.DisplayName);
            return resolved;
        }

        // Enum
        if (IsEnum(reader, tdHandle))
        {
            string underlying = GetEnumUnderlyingType(reader, tdHandle);
            _logger.LogTrace("Resolved {Namespace}.{TypeName} → EnumRef ({FQN})",
                typeNamespace, typeName, fqn);
            return new EnumRef(fqn, typeName, new PrimitiveType(underlying));
        }

        // Function pointer (via UnmanagedFunctionPointerAttribute)
        if (IsFunctionPointer(reader, td))
        {
            _logger.LogTrace("Resolved {Namespace}.{TypeName} → FunctionPointerType ({FQN})",
                typeNamespace, typeName, fqn);
            return new FunctionPointerType(typeName, "");
        }

        // COM interface
        if (IsComInterface(reader, tdHandle))
        {
            _logger.LogTrace("Resolved {Namespace}.{TypeName} → ComRef ({FQN})",
                typeNamespace, typeName, fqn);
            return new ComRef(fqn, typeName);
        }

        // Handle (single field + handle attributes)
        if (IsHandle(reader, td))
        {
            _logger.LogTrace("Resolved {Namespace}.{TypeName} → HandleRef ({FQN})",
                typeNamespace, typeName, fqn);
            return new HandleRef(fqn, typeName);
        }

        // Default: struct reference
        _logger.LogTrace("Resolved {Namespace}.{TypeName} → StructRef ({FQN})",
            typeNamespace, typeName, fqn);
        return new StructRef(fqn, typeName);
    }

    // --- Classification helpers ---

    /// <summary>
    /// Check if a type derives from System.Enum.
    /// </summary>
    internal static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);
        EntityHandle baseHandle = td.BaseType;
        if (baseHandle.Kind == HandleKind.TypeReference)
        {
            TypeReference tr = reader.GetTypeReference((TypeReferenceHandle)baseHandle);
            return reader.StringComparer.Equals(tr.Namespace, "System") &&
                   reader.StringComparer.Equals(tr.Name, "Enum");
        }
        return false;
    }

    /// <summary>
    /// Get the underlying primitive type name of an enum.
    /// </summary>
    internal static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);
        foreach (FieldDefinitionHandle fieldHandle in td.GetFields())
        {
            FieldDefinition fd = reader.GetFieldDefinition(fieldHandle);
            if (reader.StringComparer.Equals(fd.Name, "value__"))
            {
                // Decode the field signature to get its primitive type
                BlobReader blob = reader.GetBlobReader(fd.Signature);
                blob.ReadByte(); // field signature header
                return blob.ReadCompressedInteger() switch
                {
                    0x04 => "SByte",
                    0x05 => "Byte",
                    0x06 => "Int16",
                    0x07 => "UInt16",
                    0x08 => "Int32",
                    0x09 => "UInt32",
                    0x0A => "Int64",
                    0x0B => "UInt64",
                    var code => throw new NotSupportedException($"Unknown enum underlying type code: 0x{code:X2}")
                };
            }
        }
        return "Int32"; // default
    }

    /// <summary>
    /// Check if a type is a COM interface.
    /// </summary>
    internal static bool IsComInterface(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);

        // All COM interfaces have the Interface flag
        if ((td.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Interface)
            return false;

        // Most COM interfaces have [Guid]
        if (AttributeReader.GetAllAttributeNames(reader, td.GetCustomAttributes()).Contains("GuidAttribute"))
            return true;

        // Fallback: check base type
        if (!td.BaseType.IsNil)
        {
            string baseName = td.BaseType.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(
                    reader.GetTypeReference((TypeReferenceHandle)td.BaseType).Name),
                HandleKind.TypeDefinition => reader.GetString(
                    reader.GetTypeDefinition((TypeDefinitionHandle)td.BaseType).Name),
                _ => ""
            };

            if (baseName is "IUnknown" or "IDispatch")
                return true;
        }
        else
        {
            // No base type + abstract interface → COM (e.g., IUnknown itself)
            if (td.Attributes.HasFlag(TypeAttributes.Abstract))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a type is a function pointer (via UnmanagedFunctionPointerAttribute).
    /// </summary>
    internal static bool IsFunctionPointer(MetadataReader reader, TypeDefinition td)
    {
        return AttributeReader.GetAllAttributeNames(reader, td.GetCustomAttributes())
            .Contains("UnmanagedFunctionPointerAttribute");
    }

    /// <summary>
    /// Check if a type is a non-handle native typedef.
    /// </summary>
    internal static bool IsNonHandleNativeTypedef(MetadataReader reader, TypeDefinition td)
    {
        IEnumerable<string> attrs = AttributeReader.GetAllAttributeNames(reader, td.GetCustomAttributes());
        bool hasNativeTypedef = false;
        bool hasHandleAttr = false;

        foreach (string attr in attrs)
        {
            if (attr == "NativeTypedefAttribute") hasNativeTypedef = true;
            if (s_handleAttrNames.Contains(attr)) hasHandleAttr = true;
        }

        return hasNativeTypedef && !hasHandleAttr && td.GetFields().Count == 1;
    }

    /// <summary>
    /// Check if a type is a handle (single field + handle attributes).
    /// </summary>
    internal static bool IsHandle(MetadataReader reader, TypeDefinition td)
    {
        if (td.GetFields().Count != 1)
            return false;

        return AttributeReader.GetAllAttributeNames(reader, td.GetCustomAttributes())
            .Any(s_handleAttrNames.Contains);
    }

    /// <summary>
    /// Decode a NativeTypedef's underlying type by reading its single field.
    /// </summary>
    private NativeTypedefRef DecodeNativeTypedef(MetadataReader reader, TypeDefinition td)
    {
        string rawName = reader.GetString(td.Name);
        string typeNamespace = reader.GetString(td.Namespace);
        string fqn = $"{typeNamespace}.{rawName}";

        FieldDefinitionHandle fieldHandle = td.GetFields().First();
        FieldDefinition fieldDef = reader.GetFieldDefinition(fieldHandle);

        // Decode with this same reader + the typedef as resolution context
        var innerDecoder = new SignatureDecoder(reader, _loader, _logger, td);
        ResolvedType underlying = fieldDef.DecodeSignature(innerDecoder, new SignatureGenericContext());

        return new NativeTypedefRef(StripGeneratedSuffix(rawName), fqn, underlying);
    }

    /// <summary>
    /// Strip Win32 metadata's generated suffixes (_e__Struct / _e__Union) from a
    /// type name. The stripped form is the display name; the original (with
    /// suffix) remains in the FQN for registry lookup.
    /// </summary>
    internal static string StripGeneratedSuffix(string name)
    {
        const string structSuffix = "_e__Struct";
        const string unionSuffix = "_e__Union";
        if (name.EndsWith(structSuffix)) return name[..^structSuffix.Length];
        if (name.EndsWith(unionSuffix)) return name[..^unionSuffix.Length];
        return name;
    }
}
