using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;

public class GenericContext
{
    // Stub - Win32Metadata doesn't have generics so we can safely ignore this
    // Just needs to exist to conform to the interface
}

// Decodes signatures into FieldInfo records, for struct members and method parameters
public sealed class FieldSignatureProvider : ISignatureTypeProvider<FieldInfo, GenericContext>
{
    private readonly MetadataReader _reader;

    /// <summary>
    /// The Type Definition in which to resolve any Type References
    /// </summary>
    private readonly TypeDefinition? _typeRefResolutionContext;

    public FieldSignatureProvider(MetadataReader reader)
    {
        _reader = reader;
    }

    public FieldSignatureProvider(MetadataReader reader, TypeDefinition typeRefResolutionContext)
    {
        _reader = reader;
        _typeRefResolutionContext = typeRefResolutionContext;
    }

    // Primitive and special codes
    public FieldInfo GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        /*
        Carve-out: WinRT primitive strings are HSTRINGs, which are actually handles. The WinRT metadata are showing
        us the projected .NET type, so we need to interpolate. Win32 never has primitive strings, because primitive
        strings don't actually exist. See also:
            - https://learn.microsoft.com/en-us/windows/win32/winrt/hstring
            - https://learn.microsoft.com/en-us/windows/uwp/cpp-and-winrt-apis/strings
        */
        if(typeCode is PrimitiveTypeCode.String)
        {
            TypeDefinitionHandle resolved = FieldSignatureDecoder.FindTypeDefinition(
                "Windows.Win32", "Windows.Win32.System.WinRT", "HSTRING", out var win32Reader);
            TypeDefinition def = win32Reader.GetTypeDefinition(resolved);

            return new(SimpleFieldKind.Struct, "HSTRING", 0, def, null, win32Reader);
        }

        return new(SimpleFieldKind.Primitive, typeCode.ToString());
    }
    public FieldInfo GetTypeFromDefinition(TypeDefinitionHandle handle, byte rawTypeKind)
        => FieldSignatureDecoder.DecodeTypeDef(_reader, handle);

    public FieldInfo GetTypeFromReference(TypeReferenceHandle handle, byte rawTypeKind)
    {
        if (_typeRefResolutionContext != null)
        {
            // Caller sent in resolution context - search it
            TypeDefinition parent = (TypeDefinition)_typeRefResolutionContext;
            TypeReference typeRef = _reader.GetTypeReference(handle);
            string typeName = _reader.GetString(typeRef.Name);

            foreach (var nestedHandle in parent.GetNestedTypes())
            {
                var nestedTd = _reader.GetTypeDefinition(nestedHandle);
                if (_reader.StringComparer.Equals(nestedTd.Name, typeName))
                    return FieldSignatureDecoder.DecodeTypeDef(_reader, nestedHandle);
            }
        }

        // No resolution context provided or failed to resolve in it - resolve globally
        var resolved = FieldSignatureDecoder.ResolveTypeReference(_reader, handle, out MetadataReader targetReader);
        return FieldSignatureDecoder.DecodeTypeDef(targetReader, resolved);
    }

    public FieldInfo GetTypeFromSpecification(TypeSpecificationHandle handle, GenericContext genericContext)
    {
        var ts = _reader.GetTypeSpecification(handle);
        return ts.DecodeSignature(this, genericContext);
    }

    public FieldInfo GetSZArrayType(FieldInfo elementType)
    {
        return new(SimpleFieldKind.Array, elementType.TypeName, -1, elementType.TypeDef, elementType, _reader);
    }

    public FieldInfo GetArrayType(FieldInfo elementType, ArrayShape shape)
    {
        // Try to detect fixed arrays (like CHAR[n])
        int len = shape.Rank == 1 && shape.Sizes.ToList().Count == 1 ? shape.Sizes[0] : 0;

        string elemName = elementType.TypeName.ToLowerInvariant();
        if (elemName is "char" or "tchar" or "wchar" ||
            (elemName == "sbyte" && elementType.TypeDef != null && _reader.GetString(elementType.TypeDef.Value.Name) == "CHAR"))
        {
            return new(SimpleFieldKind.String, elementType.TypeName, len, elementType.TypeDef, elementType, _reader);
        }

        return new(SimpleFieldKind.Array, elementType.TypeName, len, elementType.TypeDef, elementType, _reader);
    }

    public FieldInfo GetPointerType(FieldInfo elementType)
        => new(SimpleFieldKind.Pointer, elementType.TypeName, 0, null, elementType);

    public FieldInfo GetByReferenceType(FieldInfo elementType)
        => new(SimpleFieldKind.Pointer, elementType.TypeName);

    public FieldInfo GetGenericInstantiation(FieldInfo genericType, ImmutableArray<FieldInfo> typeArguments)
    {
        // Debug.WriteLine($"Flattening GenericInstantiation: {genericType.TypeName}<{string.Join(", ", typeArguments.Select(t => t.TypeName))}>");
        return genericType;
    }

    public FieldInfo GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        // throw new NotSupportedException($"GenericMethodParameter: {index}");
        return new FieldInfo(SimpleFieldKind.Pointer, $"Callback");
    }

    public FieldInfo GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        // throw new NotSupportedException($"GenericTypeParameter: {index}");
        return new FieldInfo(SimpleFieldKind.Other, $"Any");
    }

    public FieldInfo GetModifiedType(FieldInfo modifier, FieldInfo unmodifiedType, bool isRequired)
        => unmodifiedType;

    public FieldInfo GetPinnedType(FieldInfo elementType)
        => elementType;

    public FieldInfo GetFunctionPointerType(MethodSignature<FieldInfo> signature)
    {
        string paramStr = string.Join(", ", signature.ParameterTypes.Select(p => p.AhkType));
        return new(SimpleFieldKind.Pointer, $"Function Pointer: ({paramStr}) => {signature.ReturnType.AhkType}");
    }

    public FieldInfo GetPrimitiveType(SignatureTypeCode typeCode)
    {
        return new(SimpleFieldKind.Primitive, typeCode.ToString());
    }

    public FieldInfo GetTypeFromSerializedName(string name)
    {
        throw new NotSupportedException($"GetTypeFromSerializedName(\"{name}\")");
    }

    public FieldInfo GetUnsupportedType()
        => new(SimpleFieldKind.Other, "Unsupported");

    public FieldInfo GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => GetTypeFromSpecification(handle, genericContext);

    public FieldInfo GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => GetTypeFromDefinition(handle, rawTypeKind);

    public FieldInfo GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => GetTypeFromReference(handle, rawTypeKind);
}
