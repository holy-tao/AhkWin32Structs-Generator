using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace Tao.AHK.WindowsBindGen.PiidPrecompute;

/// <summary>
/// Context for resolving generic type parameters during signature computation.
/// </summary>
public class GenericContext
{
    public ImmutableArray<WinRTSignature> TypeArguments { get; }
    public ImmutableArray<WinRTSignature> MethodArguments { get; }

    public GenericContext(
        ImmutableArray<WinRTSignature> typeArguments = default,
        ImmutableArray<WinRTSignature> methodArguments = default)
    {
        TypeArguments = typeArguments.IsDefault ? ImmutableArray<WinRTSignature>.Empty : typeArguments;
        MethodArguments = methodArguments.IsDefault ? ImmutableArray<WinRTSignature>.Empty : methodArguments;
    }

    public static GenericContext Empty { get; } = new();
}

/// <summary>
/// Computes WinRT GUIDs from type signatures using the UUID v5 algorithm.
/// </summary>
public static class WinRTGuidGenerator
{
    // WinRT base namespace GUID for pinterface computation
    private static readonly Guid WinRTNamespaceGuid = new("11f47ad5-7b73-42c0-abae-878b1e16adee");

    public static Guid ComputeGuid(string signature)
    {
        var signatureBytes = Encoding.UTF8.GetBytes(signature);
        var namespaceBytes = WinRTNamespaceGuid.ToByteArray();

        // Convert namespace GUID to big-endian for hashing
        SwapGuidByteOrder(namespaceBytes);

        // Concatenate namespace + signature and hash with SHA-1
        var hashInput = new byte[namespaceBytes.Length + signatureBytes.Length];
        namespaceBytes.CopyTo(hashInput, 0);
        signatureBytes.CopyTo(hashInput, namespaceBytes.Length);

        var hash = SHA1.HashData(hashInput);

        // Take first 16 bytes of hash
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        // Convert back to little-endian
        SwapGuidByteOrder(guidBytes);

        // Set version (5) and variant bits
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50); // Version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // Variant RFC 4122

        return new Guid(guidBytes);
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        // Swap first DWORD (bytes 0-3)
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);

        // Swap first WORD of second group (bytes 4-5)
        (guid[4], guid[5]) = (guid[5], guid[4]);

        // Swap second WORD of second group (bytes 6-7)
        (guid[6], guid[7]) = (guid[7], guid[6]);

        // Last 8 bytes stay the same (big-endian)
    }
}

/// <summary>
/// ISignatureTypeProvider implementation that builds WinRT type signatures
/// for computing parameterized interface IDs.
/// </summary>
public class WinRTSignatureTypeProvider : ISignatureTypeProvider<WinRTSignature, GenericContext>
{
    private readonly MetadataReader _reader;
    private readonly IWinRTTypeResolver _typeResolver;

    public WinRTSignatureTypeProvider(MetadataReader reader, IWinRTTypeResolver typeResolver)
    {
        _reader = reader;
        _typeResolver = typeResolver;
    }

    #region Primitive Types

    public WinRTSignature GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => new WinRTSignature.Primitive("b1"),
        PrimitiveTypeCode.Char => new WinRTSignature.Primitive("c2"),
        PrimitiveTypeCode.SByte => new WinRTSignature.Primitive("i1"),
        PrimitiveTypeCode.Byte => new WinRTSignature.Primitive("u1"),
        PrimitiveTypeCode.Int16 => new WinRTSignature.Primitive("i2"),
        PrimitiveTypeCode.UInt16 => new WinRTSignature.Primitive("u2"),
        PrimitiveTypeCode.Int32 => new WinRTSignature.Primitive("i4"),
        PrimitiveTypeCode.UInt32 => new WinRTSignature.Primitive("u4"),
        PrimitiveTypeCode.Int64 => new WinRTSignature.Primitive("i8"),
        PrimitiveTypeCode.UInt64 => new WinRTSignature.Primitive("u8"),
        PrimitiveTypeCode.Single => new WinRTSignature.Primitive("f4"),
        PrimitiveTypeCode.Double => new WinRTSignature.Primitive("f8"),
        PrimitiveTypeCode.String => new WinRTSignature.Primitive("string"),
        PrimitiveTypeCode.Object => new WinRTSignature.Primitive("cinterface(IInspectable)"),
        _ => new WinRTSignature.Invalid($"Invalid primitive type: {typeCode}")
    };

    #endregion

    #region Type References

    public WinRTSignature GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        return ResolveTypeDefinition(reader, handle);
    }

    public WinRTSignature GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        return _typeResolver.ResolveTypeReference(reader, handle);
    }

    public WinRTSignature GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind)
    {
        var typeSpec = reader.GetTypeSpecification(handle);
        return typeSpec.DecodeSignature(this, genericContext);
    }

    private WinRTSignature ResolveTypeDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var typeDef = reader.GetTypeDefinition(handle);
        var fullName = GetFullTypeName(reader, typeDef);

        // Check for special WinRT types
        if (fullName is "System.Guid" or "Windows.Foundation.Guid")
        {
            return new WinRTSignature.Primitive("g16");
        }

        // Determine type category
        var baseType = typeDef.BaseType;
        if (!baseType.IsNil)
        {
            var baseTypeName = GetBaseTypeName(reader, baseType);

            if (baseTypeName is "System.Enum")
            {
                var underlyingType = GetEnumUnderlyingType(reader, typeDef);
                return new WinRTSignature.Enum(fullName, underlyingType);
            }

            if (baseTypeName is "System.ValueType")
            {
                var fields = GetStructFieldSignatures(reader, typeDef);
                return new WinRTSignature.Struct(fullName, fields);
            }

            if (baseTypeName is "System.MulticastDelegate")
            {
                var guid = GetTypeGuid(reader, typeDef);
                return new WinRTSignature.Delegate(guid);
            }
        }

        // Check if it's an interface
        if ((typeDef.Attributes & System.Reflection.TypeAttributes.Interface) != 0)
        {
            var guid = GetTypeGuid(reader, typeDef);
            return new WinRTSignature.Guid(guid);
        }

        // Runtime class - need to find default interface
        var defaultInterface = GetDefaultInterface(reader, typeDef);
        return new WinRTSignature.RuntimeClass(fullName, defaultInterface);
    }

    #endregion

    #region Generic Types

    public WinRTSignature GetGenericInstantiation(WinRTSignature genericType, ImmutableArray<WinRTSignature> typeArguments)
    {
        // For parameterized interfaces, we need the piid (base generic interface GUID)
        if (genericType is WinRTSignature.Guid guidSig)
        {
            return new WinRTSignature.PInterface(guidSig.Value, typeArguments);
        }

        if (genericType is WinRTSignature.Delegate delegateSig)
        {
            return new WinRTSignature.PInterface(delegateSig.Iid, typeArguments);
        }

        return new WinRTSignature.Invalid($"Cannot instantiate {genericType.GetType().Name} as generic type");
    }

    public WinRTSignature GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        if (index < genericContext.TypeArguments.Length)
        {
            return genericContext.TypeArguments[index];
        }
        return new WinRTSignature.GenericParameter(index);
    }

    public WinRTSignature GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        if (index < genericContext.MethodArguments.Length)
        {
            return genericContext.MethodArguments[index];
        }
        return new WinRTSignature.Invalid("WinRT does not support generic methods in signature computation");
    }

    #endregion

    #region Arrays and Pointers

    public WinRTSignature GetSZArrayType(WinRTSignature elementType)
    {
        return new WinRTSignature.Array(elementType);
    }

    public WinRTSignature GetArrayType(WinRTSignature elementType, ArrayShape shape)
    {
        return new WinRTSignature.Invalid("WinRT does not support multi-dimensional arrays");
    }

    public WinRTSignature GetByReferenceType(WinRTSignature elementType)
    {
        // By-ref is used for out parameters but doesn't affect the signature
        return elementType;
    }

    public WinRTSignature GetPointerType(WinRTSignature elementType)
    {
        return new WinRTSignature.Invalid("WinRT does not support pointer types in signatures");
    }

    public WinRTSignature GetPinnedType(WinRTSignature elementType)
    {
        return new WinRTSignature.Invalid("WinRT does not support pinned types");
    }

    #endregion

    #region Modifiers and Special Types

    public WinRTSignature GetModifiedType(WinRTSignature modifier, WinRTSignature unmodifiedType, bool isRequired)
    {
        // Modifiers don't affect WinRT signatures
        return unmodifiedType;
    }

    public WinRTSignature GetFunctionPointerType(MethodSignature<WinRTSignature> signature)
    {
        return new WinRTSignature.Invalid("WinRT does not support function pointer types");
    }

    #endregion

    #region Helper Methods

    private string GetFullTypeName(MetadataReader reader, TypeDefinition typeDef)
    {
        var ns = reader.GetString(typeDef.Namespace);
        var name = reader.GetString(typeDef.Name);

        // Handle nested types
        if (typeDef.IsNested)
        {
            var declaringType = reader.GetTypeDefinition(typeDef.GetDeclaringType());
            var parentName = GetFullTypeName(reader, declaringType);
            return $"{parentName}.{name}";
        }

        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private string? GetBaseTypeName(MetadataReader reader, EntityHandle baseType)
    {
        return baseType.Kind switch
        {
            HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)baseType)),
            HandleKind.TypeReference => GetTypeReferenceName(reader, (TypeReferenceHandle)baseType),
            _ => null
        };
    }

    private string GetTypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var typeRef = reader.GetTypeReference(handle);
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private WinRTSignature GetEnumUnderlyingType(MetadataReader reader, TypeDefinition typeDef)
    {
        foreach (var fieldHandle in typeDef.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & System.Reflection.FieldAttributes.Static) == 0)
            {
                var signature = field.DecodeSignature(this, GenericContext.Empty);
                return signature;
            }
        }
        // Default to Int32 if not found
        return new WinRTSignature.Primitive("i4");
    }

    private ImmutableArray<WinRTSignature> GetStructFieldSignatures(MetadataReader reader, TypeDefinition typeDef)
    {
        var builder = ImmutableArray.CreateBuilder<WinRTSignature>();

        foreach (var fieldHandle in typeDef.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & System.Reflection.FieldAttributes.Static) == 0)
            {
                var signature = field.DecodeSignature(this, GenericContext.Empty);
                builder.Add(signature);
            }
        }

        return builder.ToImmutable();
    }

    private Guid GetTypeGuid(MetadataReader reader, TypeDefinition typeDef)
    {
        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = reader.GetCustomAttribute(attrHandle);
            var ctorHandle = attr.Constructor;

            string? attrTypeName = ctorHandle.Kind switch
            {
                HandleKind.MemberReference => GetMemberReferenceTypeName(reader, (MemberReferenceHandle)ctorHandle),
                HandleKind.MethodDefinition => GetMethodDefinitionTypeName(reader, (MethodDefinitionHandle)ctorHandle),
                _ => null
            };

            if (attrTypeName is "Windows.Foundation.Metadata.GuidAttribute" or "System.Runtime.InteropServices.GuidAttribute")
            {
                return DecodeGuidAttribute(reader, attr);
            }
        }

        throw new InvalidOperationException($"Type {GetFullTypeName(reader, typeDef)} does not have a GUID attribute");
    }

    private string? GetMemberReferenceTypeName(MetadataReader reader, MemberReferenceHandle handle)
    {
        var memberRef = reader.GetMemberReference(handle);
        return memberRef.Parent.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceName(reader, (TypeReferenceHandle)memberRef.Parent),
            HandleKind.TypeDefinition => GetFullTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)memberRef.Parent)),
            _ => null
        };
    }

    private string? GetMethodDefinitionTypeName(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var methodDef = reader.GetMethodDefinition(handle);
        var typeDef = reader.GetTypeDefinition(methodDef.GetDeclaringType());
        return GetFullTypeName(reader, typeDef);
    }

    private Guid DecodeGuidAttribute(MetadataReader reader, CustomAttribute attr)
    {
        var value = attr.DecodeValue(new GuidAttributeTypeProvider());

        // GuidAttribute can have either string or 11-parameter constructor
        if (value.FixedArguments.Length == 1 && value.FixedArguments[0].Value is string guidString)
        {
            return Guid.Parse(guidString);
        }

        if (value.FixedArguments.Length == 11)
        {
            var args = value.FixedArguments;
            return new Guid(
                (uint)args[0].Value!,
                (ushort)args[1].Value!,
                (ushort)args[2].Value!,
                (byte)args[3].Value!,
                (byte)args[4].Value!,
                (byte)args[5].Value!,
                (byte)args[6].Value!,
                (byte)args[7].Value!,
                (byte)args[8].Value!,
                (byte)args[9].Value!,
                (byte)args[10].Value!
            );
        }

        throw new InvalidOperationException("Unexpected GuidAttribute format");
    }

    private WinRTSignature GetDefaultInterface(MetadataReader reader, TypeDefinition typeDef)
    {
        foreach (var implHandle in typeDef.GetInterfaceImplementations())
        {
            var impl = reader.GetInterfaceImplementation(implHandle);

            // Check for DefaultAttribute
            foreach (var attrHandle in impl.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                var attrTypeName = attr.Constructor.Kind switch
                {
                    HandleKind.MemberReference => GetMemberReferenceTypeName(reader, (MemberReferenceHandle)attr.Constructor),
                    HandleKind.MethodDefinition => GetMethodDefinitionTypeName(reader, (MethodDefinitionHandle)attr.Constructor),
                    _ => null
                };

                if (attrTypeName == "Windows.Foundation.Metadata.DefaultAttribute")
                {
                    return ResolveInterfaceHandle(reader, impl.Interface);
                }
            }
        }

        throw new InvalidOperationException($"Runtime class {GetFullTypeName(reader, typeDef)} has no default interface");
    }

    private WinRTSignature ResolveInterfaceHandle(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => ResolveTypeDefinition(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => _typeResolver.ResolveTypeReference(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(this, GenericContext.Empty),
            _ => throw new InvalidOperationException($"Unexpected handle kind: {handle.Kind}")
        };
    }

    #endregion
}

/// <summary>
/// Interface for resolving type references across assemblies.
/// </summary>
public interface IWinRTTypeResolver
{
    WinRTSignature ResolveTypeReference(MetadataReader reader, TypeReferenceHandle handle);
}

/// <summary>
/// Simple type provider for decoding GuidAttribute values.
/// </summary>
internal class GuidAttributeTypeProvider : ICustomAttributeTypeProvider<Type>
{
    public Type GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => typeof(bool),
        PrimitiveTypeCode.Byte => typeof(byte),
        PrimitiveTypeCode.Char => typeof(char),
        PrimitiveTypeCode.Double => typeof(double),
        PrimitiveTypeCode.Int16 => typeof(short),
        PrimitiveTypeCode.Int32 => typeof(int),
        PrimitiveTypeCode.Int64 => typeof(long),
        PrimitiveTypeCode.SByte => typeof(sbyte),
        PrimitiveTypeCode.Single => typeof(float),
        PrimitiveTypeCode.String => typeof(string),
        PrimitiveTypeCode.UInt16 => typeof(ushort),
        PrimitiveTypeCode.UInt32 => typeof(uint),
        PrimitiveTypeCode.UInt64 => typeof(ulong),
        _ => typeof(object)
    };

    public Type GetSystemType() => typeof(Type);
    public Type GetSZArrayType(Type elementType) => elementType.MakeArrayType();
    public Type GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => typeof(object);
    public Type GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => typeof(object);
    public Type GetTypeFromSerializedName(string name) => Type.GetType(name) ?? typeof(object);
    public PrimitiveTypeCode GetUnderlyingEnumType(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.SByte => PrimitiveTypeCode.SByte,
        TypeCode.Byte => PrimitiveTypeCode.Byte,
        TypeCode.Int16 => PrimitiveTypeCode.Int16,
        TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
        TypeCode.Int32 => PrimitiveTypeCode.Int32,
        TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
        TypeCode.Int64 => PrimitiveTypeCode.Int64,
        TypeCode.UInt64 => PrimitiveTypeCode.UInt64,
        _ => PrimitiveTypeCode.Int32
    };
    public bool IsSystemType(Type type) => type == typeof(Type);
}
