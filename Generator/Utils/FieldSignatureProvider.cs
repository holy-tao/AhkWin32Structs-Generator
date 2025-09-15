using System.Collections.Immutable;
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

    public FieldSignatureProvider(MetadataReader reader)
    {
        _reader = reader;
    }

    // Primitive and special codes
    public FieldInfo GetPrimitiveType(PrimitiveTypeCode typeCode)
        => new(SimpleFieldKind.Primitive, typeCode.ToString());

    public FieldInfo GetTypeFromDefinition(TypeDefinitionHandle handle, byte rawTypeKind)
        => FieldSignatureDecoder.DecodeTypeDef(_reader, handle);

    public FieldInfo GetTypeFromReference(TypeReferenceHandle handle, byte rawTypeKind)
    {
        var resolved = FieldSignatureDecoder.ResolveTypeReference(_reader, handle);
        return resolved != null
            ? FieldSignatureDecoder.DecodeTypeDef(_reader, resolved.Value)
            : new(SimpleFieldKind.Pointer, _reader.GetString(_reader.GetTypeReference(handle).Name));
    }

    public FieldInfo GetTypeFromSpecification(TypeSpecificationHandle handle, GenericContext genericContext)
    {
        var ts = _reader.GetTypeSpecification(handle);
        return ts.DecodeSignature(this, genericContext);
    }

    public FieldInfo GetSZArrayType(FieldInfo elementType)
        => throw new NotSupportedException("SZARRAY not supported");

    public FieldInfo GetArrayType(FieldInfo elementType, ArrayShape shape)
    {
        // Try to detect fixed arrays (like CHAR[n])
        int len = shape.Rank == 1 && shape.Sizes.ToList().Count == 1 ? shape.Sizes[0] : 0;

        string elemName = elementType.TypeName.ToLowerInvariant();
        if (elemName is "char" or "tchar" or "wchar" ||
            (elemName == "sbyte" && elementType.TypeDef != null && _reader.GetString(elementType.TypeDef.Value.Name) == "CHAR"))
        {
            return new(SimpleFieldKind.String, elementType.TypeName, len, elementType.TypeDef, elementType);
        }

        return new(SimpleFieldKind.Array, elementType.TypeName, len, elementType.TypeDef, elementType);
    }

    public FieldInfo GetPointerType(FieldInfo elementType)
        => new(SimpleFieldKind.Pointer, elementType.TypeName);

    public FieldInfo GetByReferenceType(FieldInfo elementType)
        => new(SimpleFieldKind.Pointer, elementType.TypeName);

    public FieldInfo GetGenericInstantiation(FieldInfo genericType, ImmutableArray<FieldInfo> typeArguments)
        => new(SimpleFieldKind.Other, $"{genericType.TypeName}<{string.Join(",", typeArguments)}>");

    public FieldInfo GetGenericMethodParameter(GenericContext genericContext, int index)
        => new(SimpleFieldKind.Other, $"!!{index}");

    public FieldInfo GetGenericTypeParameter(GenericContext genericContext, int index)
        => new(SimpleFieldKind.Other, $"!{index}");

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
        if (typeCode == SignatureTypeCode.Void)
            return new(SimpleFieldKind.Pointer, "Void");
        return new(SimpleFieldKind.Primitive, typeCode.ToString());
    }

    public FieldInfo GetTypeFromSerializedName(string name)
        => new(SimpleFieldKind.Other, name);

    public FieldInfo GetUnsupportedType()
        => new(SimpleFieldKind.Other, "Unsupported");

    public FieldInfo GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => GetTypeFromSpecification(handle, genericContext);

    public FieldInfo GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => GetTypeFromDefinition(handle, rawTypeKind);

    public FieldInfo GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => GetTypeFromReference(handle, rawTypeKind);
}
