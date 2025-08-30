using System;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

// The kind of field - for AHK we only care whether it's a primitive, pointer, or array (and its type and rank if an array)
// We don't care about most of the specifics
public enum SimpleFieldKind
{
    Primitive,
    Pointer,
    Array,
    Struct,
    Class,
    Other
}

public readonly record struct FieldInfo(SimpleFieldKind Kind, string TypeName, int Rank = 0)
{
    // https://www.autohotkey.com/docs/v2/lib/DllCall.htm
    public string DllCallType
    {
        get
        {
            if (Kind == SimpleFieldKind.Primitive)
            {
                switch (TypeName)
                {
                    case "Single":
                        return "float";
                    case "Boolean":
                    case "Int32":
                        return "int";
                    case "Double":
                        return "double";
                    case "Int64":
                        return "int64";
                    case "UInt32":
                        return "uint";
                    case "UInt64":
                        return "uint";
                    case "Int16":
                        return "short";
                    case "UInt16":
                        return "ushort";
                    case "Byte":
                    case "SByte":
                    case "Char":
                        return "char";
                    case "IntPtr":
                    case "UIntPtr":
                    case "Void":
                        return "ptr";
                    default:
                        throw new NotSupportedException(TypeName);
                }
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
                    case "Single":
                    case "Boolean":
                    case "Int32":
                    case "UInt32":
                        return 4;
                    case "Double":
                    case "Int64":
                    case "IntPtr":
                    case "UInt64":
                    case "UIntPtr":
                    case "Void":
                        return 8;
                    case "Int16":
                    case "UInt16":
                        return 2;
                    case "Byte":
                    case "SByte":
                    case "Char":
                        return 1;
                    default:
                        throw new NotSupportedException(TypeName);
                }
            }
            else if (Kind == SimpleFieldKind.Array)
            {
                return Rank * new FieldInfo(SimpleFieldKind.Primitive, TypeName, 0).Width;
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
                    case "Single":
                    case "Double":
                        return "Float";
                    case "Boolean":
                        return "Boolean";
                    case "Int32":
                    case "UInt32":
                    case "Int64":
                    case "UInt64":
                    case "Int16":
                    case "UInt16":
                    case "Byte":
                    case "SByte":
                    case "Char":
                        return "Integer";
                    case "UIntPtr":
                    case "IntPtr":
                    case "Void":
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
        // Step 1: check for FixedBufferAttribute
        if (TryGetFixedBufferInfo(reader, fieldDef, out var elemType, out var length))
        {
            return new FieldInfo(SimpleFieldKind.Array, elemType, length);
        }

        // Step 2: decode normal field signature blob
        var blob = reader.GetBlobReader(fieldDef.Signature);
        byte header = blob.ReadByte();
        if (header != (byte)SignatureKind.Field)
            throw new BadImageFormatException("Not a field signature");

        return DecodeType(ref blob, reader);
    }

    private static FieldInfo DecodeType(ref BlobReader blob, MetadataReader reader)
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
                DecodeType(ref blob, reader); // skip pointee
                return new FieldInfo(SimpleFieldKind.Pointer, "ptr");

            // SZARRAY - we should probably skip structs with these
            case SignatureTypeCode.SZArray:
                var elem = DecodeType(ref blob, reader);
                return new FieldInfo(SimpleFieldKind.Array, elem.TypeName, -1);

            // ARRAY
            case SignatureTypeCode.Array:
                var arrElem = DecodeType(ref blob, reader);
                int rank = blob.ReadCompressedInteger();

                int numSizes = blob.ReadCompressedInteger();
                for (int i = 0; i < numSizes; i++) blob.ReadCompressedInteger();
                int numLoBounds = blob.ReadCompressedInteger();
                for (int i = 0; i < numLoBounds; i++) blob.ReadCompressedInteger();

                return new FieldInfo(SimpleFieldKind.Array, arrElem.TypeName + $"[{rank}]");

            // ValueType or Class
            case (SignatureTypeCode)17:         //0x11 - also a TypeHandle
            case (SignatureTypeCode)18:         //0x12 - also a TypeHandle
            case SignatureTypeCode.TypeHandle:
                // Type handles can be TypeDefinition, TypeReference, OR TypeSpecification (e.g. sized arrays / specs)
                var handle = blob.ReadTypeHandle();

                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    var tdHandle = (TypeDefinitionHandle)handle;
                    var td = reader.GetTypeDefinition(tdHandle);
                    if (IsEnum(reader, tdHandle))
                    {
                        string underlying = GetEnumUnderlyingType(reader, tdHandle);
                        return new FieldInfo(SimpleFieldKind.Primitive, underlying);
                    }
                    else
                    {
                        return new FieldInfo(SimpleFieldKind.Struct, reader.GetString(td.Name));
                    }
                }
                else if (handle.Kind == HandleKind.TypeReference)
                {
                    var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
                    return new FieldInfo(SimpleFieldKind.Class, reader.GetString(tr.Name));
                }
                else if (handle.Kind == HandleKind.TypeSpecification)
                {
                    // Handle TypeSpecification by parsing its own signature blob.
                    // This covers cases like compiler-generated type specs that encode fixed buffers or special arrays.
                    var ts = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                    var specReader = reader.GetBlobReader(ts.Signature);

                    // The TypeSpecification signature is itself a type signature (no leading FIELD header).
                    // Recurse into DecodeType, but it expects a blob that starts at a type opcode.
                    // Note: DecodeType's first action is to ReadByte to get a SignatureTypeCode,
                    // so pass specReader by ref.
                    return DecodeType(ref specReader, reader);
                }

                return new FieldInfo(SimpleFieldKind.Other, "TypeSpec");

            default:
                Console.WriteLine($"Unhandled signature type code: {et}");
                return new FieldInfo(SimpleFieldKind.Other, et.ToString());
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

    private static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        foreach (var fieldHandle in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fieldHandle);
            if (reader.StringComparer.Equals(fd.Name, "value__"))
            {
                var blob = reader.GetBlobReader(fd.Signature);
                blob.ReadByte(); // skip FIELD (0x06)
                var fi = DecodeType(ref blob, reader);
                return fi.TypeName;
            }
        }
        return "Int32";
    }

    private static bool TryGetFixedBufferInfo(MetadataReader reader, FieldDefinition field, out string elementType, out int length)
    {
        foreach (var caHandle in field.GetCustomAttributes())
        {
            var ca = reader.GetCustomAttribute(caHandle);
            var ctor = ca.Constructor;


            string typeNameHandle = "";
            string nsHandle = "";

            if (ctor.Kind == HandleKind.MemberReference)
            {
                var mr = reader.GetMemberReference((MemberReferenceHandle)ctor);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    var tr = reader.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    nsHandle = reader.GetString(tr.Namespace);
                    typeNameHandle = reader.GetString(tr.Name);
                }
            }
            else if (ctor.Kind == HandleKind.MethodDefinition)
            {
                var md = reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
                var td = reader.GetTypeDefinition(md.GetDeclaringType());
                nsHandle = reader.GetString(td.Namespace);
                typeNameHandle = reader.GetString(td.Name);
            }

            //Console.WriteLine(nsHandle + "::" + typeNameHandle);

            if (nsHandle == "System.Runtime.CompilerServices" && typeNameHandle == "FixedBufferAttribute")
            {
                var valueReader = reader.GetBlobReader(ca.Value);
                valueReader.ReadByte(); // prolog 0x01
                valueReader.ReadByte(); // prolog 0x00

                var elemTypeHandle = (TypeReferenceHandle)valueReader.ReadTypeHandle();
                elementType = reader.GetString(reader.GetTypeReference(elemTypeHandle).Name);
                length = valueReader.ReadInt32();
                return true;
            }
        }

        elementType = "";
        length = 0;
        return false;
    }
}
