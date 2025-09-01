using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using MetadataUtils;
using Microsoft.VisualBasic.FileIO;

// The kind of field - for AHK we only care whether it's a primitive, pointer, or array (and its type and rank if an array)
// We don't care about most of the specifics
public enum SimpleFieldKind
{
    Primitive,  // int, int16, uint, etc
    Pointer,    // any pointer-sized integer (function pointers, COM interface pointers, etc)
    Array,
    Struct,     // an embedded struct. TypeDef contains the type of the struct itself in this case
    Class,
    Other,      // unhandled, will produce an error
    String      // A string buffer for which we can use StrPut / StrGet (usually a character array)
}

public record FieldInfo
{

    public readonly SimpleFieldKind Kind;
    public readonly string TypeName;
    public readonly int Length;
    public readonly TypeDefinition? TypeDef;
    public readonly FieldInfo? ArrayType;

    public FieldInfo(SimpleFieldKind Kind, string TypeName, int Length = 0, TypeDefinition? TypeDef = null, FieldInfo? ArrayType = null)
    {
        this.Kind = Kind;
        this.TypeName = TypeName.ToLower();
        this.Length = Length;
        this.TypeDef = TypeDef;
        this.ArrayType = ArrayType;
    }
    // https://www.autohotkey.com/docs/v2/lib/DllCall.htm
    public string DllCallType
    {
        get
        {
            if (Kind == SimpleFieldKind.Primitive)
            {
                switch (TypeName)
                {
                    case "single":
                        return "float";
                    case "boolean":
                    case "int32":
                        return "int";
                    case "double":
                        return "double";
                    case "int64":
                        return "int64";
                    case "uint32":
                        return "uint";
                    case "uint64":
                        return "uint";
                    case "int16":
                        return "short";
                    case "uint16":
                        return "ushort";
                    case "byte":
                    case "sbyte":
                    case "char":
                        return "char";
                    case "uintptr":
                    case "intptr":
                    case "void":
                    case "ptr":
                        return "ptr";
                    default:
                        throw new NotSupportedException(TypeName);
                }
            }
            else if (Kind == SimpleFieldKind.Pointer)
            {
                return "ptr";
            }
            else
            {
                // TODO handle arrays
                // Everything else in AHK is a pointer
                return "ptr";
            }
        }
    }

    public int Width
    {
        get
        {
            if (Kind == SimpleFieldKind.Primitive)
            {
                switch (TypeName)
                {
                    case "single":
                    case "boolean":
                    case "int32":
                    case "uint32":
                        return 4;
                    case "double":
                    case "int64":
                    case "intptr":
                    case "uint64":
                    case "uintptr":
                    case "void":
                    case "ptr":
                        return 8;
                    case "int16":
                    case "uint16":
                    case "char":        // Assuming UTF-16
                        return 2;
                    case "byte":
                    case "sbyte":
                        return 1;
                    default:
                        throw new NotSupportedException($"{TypeName} ({Kind})");
                }
            }
            else if (Kind == SimpleFieldKind.Array)
            {
                throw new NotSupportedException("Cannot get width of array FieldInfo directly - use Rank * width of TypeDef");
            }
            else if (Kind == SimpleFieldKind.Pointer)
            {
                return 8;
            }
            else
            {
                // Else assume pointer
                return 8;
            }
        }
    }
    
    public string AhkType
    {
        get
        {
            if (Kind == SimpleFieldKind.Primitive)
            {
                switch (TypeName)
                {
                    case "single":
                    case "double":
                        return "Float";
                    case "boolean":
                        return "Boolean";
                    case "int32":
                    case "uint32":
                    case "int64":
                    case "uint64":
                    case "int16":
                    case "uint16":
                    case "byte":
                    case "sbyte":
                    case "char":
                        return "Integer";
                    case "uintptr":
                    case "intptr":
                    case "void":
                    case "ptr":
                        return "Pointer";
                    default:
                        throw new NotSupportedException(TypeName);
                }
            }
            else if (Kind == SimpleFieldKind.Array)
            {
                return $"Array<{TypeName}>";
            }
            else
            {
                // Assuming 64-bit ahk
                return "Pointer";
            }
        }
    }
}

public static class FieldSignatureDecoder
{
    public static FieldInfo DecodeFieldType(MetadataReader reader, FieldDefinition fieldDef)
    {
        var blob = reader.GetBlobReader(fieldDef.Signature);
        byte header = blob.ReadByte();
        if (header != (byte)SignatureKind.Field)
            throw new BadImageFormatException("Not a field signature");

        return DecodeType(ref blob, reader, fieldDef);
    }

    private static FieldInfo DecodeType(ref BlobReader blob, MetadataReader reader, FieldDefinition fieldDef)
    {
        var et = (SignatureTypeCode)blob.ReadByte();

        switch (et)
        {
            // Primitives
            case SignatureTypeCode.Boolean:
            case SignatureTypeCode.Char:
            case SignatureTypeCode.SByte:
            case SignatureTypeCode.Byte:
            case SignatureTypeCode.Int16:
            case SignatureTypeCode.UInt16:
            case SignatureTypeCode.Int32:
            case SignatureTypeCode.UInt32:
            case SignatureTypeCode.Int64:
            case SignatureTypeCode.UInt64:
            case SignatureTypeCode.Single:
            case SignatureTypeCode.Double:
            case SignatureTypeCode.IntPtr:
            case SignatureTypeCode.UIntPtr:
                return new FieldInfo(SimpleFieldKind.Primitive, et.ToString());

            // Pointer
            case SignatureTypeCode.Pointer:
                DecodeType(ref blob, reader, fieldDef); // skip pointee
                return new FieldInfo(SimpleFieldKind.Pointer, "Ptr");

            // SZARRAY - we should probably skip structs with these
            case SignatureTypeCode.SZArray:
                throw new NotSupportedException($"{reader.GetString(fieldDef.Name)}: dynamic array");

            // ARRAY
            case SignatureTypeCode.Array:
                var arrElem = DecodeType(ref blob, reader, fieldDef);
                int rank = blob.ReadCompressedInteger();
                int arrLength = GetFixedArrayLength(reader, fieldDef);

                int numSizes = blob.ReadCompressedInteger();
                for (int i = 0; i < numSizes; i++) blob.ReadCompressedInteger();
                int numLoBounds = blob.ReadCompressedInteger();
                for (int i = 0; i < numLoBounds; i++) blob.ReadCompressedInteger();

                return new FieldInfo(SimpleFieldKind.Array, arrElem.TypeName, arrLength, null, arrElem);

            // ValueType or Class
            case (SignatureTypeCode)17:         //0x11 - also a TypeHandle
            case (SignatureTypeCode)18:         //0x12 - also a TypeHandle
            case SignatureTypeCode.TypeHandle:
                // Type handles can be TypeDefinition, TypeReference, OR TypeSpecification (e.g. sized arrays / specs)
                var handle = blob.ReadTypeHandle();

                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    return DecodeTypeDef(reader, (TypeDefinitionHandle)handle, fieldDef);
                }
                else if (handle.Kind == HandleKind.TypeReference)
                {
                    // TODO further decoding is necessary - we need to know what the reference refers to
                    TypeDefinitionHandle? resolvedTypeDefHandle = ResolveTypeReference(reader, (TypeReferenceHandle)handle);
                    if (resolvedTypeDefHandle != null)
                    {
                        return DecodeTypeDef(reader, (TypeDefinitionHandle)resolvedTypeDefHandle, fieldDef);
                    }
                    else
                    {
                        // Assume a pointer if we couldn't resolve the typedef
                        return new FieldInfo(SimpleFieldKind.Pointer, "Ptr");
                    }
                }
                else if (handle.Kind == HandleKind.TypeSpecification)
                {
                    var ts = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                    var specReader = reader.GetBlobReader(ts.Signature);
                    return DecodeType(ref specReader, reader, fieldDef);
                }

                return new FieldInfo(SimpleFieldKind.Other, "TypeSpec");

            case SignatureTypeCode.Void:
                // This is an opaque pointer or handle type - we'll just treat it as a pointer
                return new FieldInfo(SimpleFieldKind.Pointer, "Ptr");

            default:
                Console.WriteLine($"Unhandled signature type code: {et}");
                return new FieldInfo(SimpleFieldKind.Other, et.ToString());
        }
    }

    /// <summary>
    /// Returns the fixed array length for a field
    /// </summary>
    private static int GetFixedArrayLength(MetadataReader reader, FieldDefinition fieldDef)
    {
        var sig = fieldDef.DecodeSignature(new GenericSignatureTypeProvider(), null);
        Match match = Regex.Match(sig, @"^(?<Namespace>\w*?.*?).?(?<TypeName>\w+)\[(?<Min>\d+)...(?<Max>\d+)]$") ??
            throw new FormatException($"Failed to parse array signature: {sig}");

        if (int.TryParse(match.Groups["Max"].Value, out int maxLength))
        {
            return maxLength + 1;
        }

        throw new FormatException($"Failed to parse array signature: {sig}");
    }

    private static FieldInfo DecodeTypeDef(MetadataReader reader, TypeDefinitionHandle tdHandle, FieldDefinition fieldDef)
    {
        var td = reader.GetTypeDefinition(tdHandle);
        if (IsEnum(reader, tdHandle))
        {
            string underlying = GetEnumUnderlyingType(reader, tdHandle, fieldDef);
            // TODO I think this is producing FieldInfos with type Primitive but TypeName of the enum
            return new FieldInfo(SimpleFieldKind.Primitive, underlying);
        }
        else
        {
            if (IsPseudoPrimitive(reader, tdHandle, out FieldInfo? fieldInfo))
            {
                return (FieldInfo)fieldInfo;
            }

            return new FieldInfo(SimpleFieldKind.Struct, reader.GetString(td.Name), 0, td);
        }
    }

    private static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        var baseHandle = td.BaseType;
        if (baseHandle.Kind == HandleKind.TypeReference)
        {
            var tr = reader.GetTypeReference((TypeReferenceHandle)baseHandle);
            return reader.StringComparer.Equals(tr.Namespace, "System") &&
                   reader.StringComparer.Equals(tr.Name, "Enum");
        }
        return false;
    }

    /// <summary>
    /// Determine whether or not the given type can be represented in AutoHotkey with a simple Integer, and thus
    /// we can treat it as a primitive. Such types include handles (e.g. HWND), NativeTypeDefs (BOOL, HRESULT, etc),
    /// string pointers (LPWSTR, BSTR, etc), and function pointers and callback addresses.
    /// 
    /// Note: the [NativeTypedef] attribute won't work for us here because it also picks up things like RECT and POINT,
    /// which are structs.
    /// </summary>
    public static bool IsPseudoPrimitive(MetadataReader reader, TypeDefinitionHandle handle, [NotNullWhen(true)] out FieldInfo? fieldInfo )
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);

        FieldDefinitionHandleCollection fields = td.GetFields();

        // If struct is empty, check to see if any function pointers point to it
        if (fields.Count == 0 && IsUsedAsFunctionPointer(reader, handle))
        {
            fieldInfo = new FieldInfo(SimpleFieldKind.Pointer, "Ptr");
            return true;
        }

        // Otherwise a pseudo-primitive must have exactly one member
        if (fields.Count != 1) {
            fieldInfo = null;
            return false;
        } 

        FieldDefinitionHandle? singleFieldHandle = fields.FirstOrDefault();
        if (!singleFieldHandle.HasValue)
        {
            fieldInfo = null;
            return false;
        }

        FieldDefinition singleField = reader.GetFieldDefinition(singleFieldHandle.Value);

        var blob = reader.GetBlobReader(singleField.Signature);
        var sigTypeCode = (SignatureTypeCode)blob.ReadByte();
        if (sigTypeCode.IsPrimitive())
        {
            // Extract underlying primitive type
            fieldInfo = new FieldInfo(SimpleFieldKind.Primitive, sigTypeCode.ToString());
            return true;
        }
        else if (sigTypeCode == SignatureTypeCode.IntPtr || sigTypeCode == SignatureTypeCode.UIntPtr || sigTypeCode == SignatureTypeCode.FunctionPointer)
        {
            // Some other pointer type
            fieldInfo = new FieldInfo(SimpleFieldKind.Pointer, "Ptr");
            return true;
        }
        else if (sigTypeCode == SignatureTypeCode.TypeHandle || sigTypeCode == (SignatureTypeCode)17 || sigTypeCode == (SignatureTypeCode)18)
        {
            var sigHandle = blob.ReadTypeHandle();

            TypeDefinitionHandle? innerDef = sigHandle.Kind switch
            {
                HandleKind.TypeReference => ResolveTypeReference(reader, (TypeReferenceHandle)sigHandle),
                HandleKind.TypeDefinition => (TypeDefinitionHandle)sigHandle,
                _ => null
            };

            if (innerDef.HasValue)
                return IsPseudoPrimitive(reader, innerDef.Value, out fieldInfo); // recursively unwrap
        }

        fieldInfo = null;
        return false;
    }

    private static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle, FieldDefinition fieldDef)
    {
        var td = reader.GetTypeDefinition(handle);
        foreach (var fieldHandle in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fieldHandle);
            if (reader.StringComparer.Equals(fd.Name, "value__"))
            {
                var blob = reader.GetBlobReader(fd.Signature);
                blob.ReadByte(); // skip FIELD (0x06)
                var fi = DecodeType(ref blob, reader, fieldDef);
                return fi.TypeName;
            }
        }
        return "Int32";
    }

    /// <summary>
    /// Some function pointers (notably the LPFN*PROC callbacks) are represented in the metadata as empty
    /// structs. So when we encounter one we need to check its attributes to see if it's a function pointer
    /// </summary>
    private static bool IsUsedAsFunctionPointer(MetadataReader reader, TypeDefinitionHandle defHandle)
    {
        TypeDefinition typeDef = reader.GetTypeDefinition(defHandle);
        return CustomAttributeDecoder.GetAllNames(reader, typeDef).Contains("UnmanagedFunctionPointerAttribute");
    }

    private static TypeDefinitionHandle? ResolveTypeReference(MetadataReader reader, TypeReferenceHandle trHandle)
    {
        var tr = reader.GetTypeReference(trHandle);
        string name = reader.GetString(tr.Name);
        string ns = reader.GetString(tr.Namespace);

        // Only resolve if it's supposed to be in this module
        if (tr.ResolutionScope.Kind == HandleKind.ModuleDefinition)
        {
            foreach (var tdHandle in reader.TypeDefinitions)
            {
                var td = reader.GetTypeDefinition(tdHandle);
                if (reader.StringComparer.Equals(td.Name, name) &&
                    reader.StringComparer.Equals(td.Namespace, ns))
                {
                    return tdHandle;
                }
            }
        }

        // Type is in an external assembly
        return null;
    }
}
