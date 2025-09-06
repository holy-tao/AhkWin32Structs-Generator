using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MetadataUtils;
using Microsoft.VisualBasic.FileIO;

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
                FieldInfo pointee = DecodeType(ref blob, reader, fieldDef); // skip pointee
                return new FieldInfo(SimpleFieldKind.Pointer, pointee.TypeName);

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

                if (arrElem.TypeName.ToLower().Equals("char", StringComparison.CurrentCultureIgnoreCase) ||
                    arrElem.TypeName.ToLower().Equals("tchar", StringComparison.CurrentCultureIgnoreCase) ||
                    arrElem.TypeName.ToLower().Equals("wchar", StringComparison.CurrentCultureIgnoreCase))
                {
                    return new FieldInfo(SimpleFieldKind.String, arrElem.TypeName, arrLength);
                }

                return new FieldInfo(SimpleFieldKind.Array, arrElem.TypeName, arrLength, arrElem.TypeDef, arrElem);

            // ValueType or Class
            case (SignatureTypeCode)17:         //0x11 - also a TypeHandle
            case (SignatureTypeCode)18:         //0x12 - also a TypeHandle
            case SignatureTypeCode.TypeHandle:
                var handle = blob.ReadTypeHandle();

                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    return DecodeTypeDef(reader, (TypeDefinitionHandle)handle, fieldDef);
                }
                else if (handle.Kind == HandleKind.TypeReference)
                {
                    // We need to resolve the TypeRef
                    TypeDefinitionHandle? resolvedTypeDefHandle = ResolveTypeReference(reader, (TypeReferenceHandle)handle);
                    if (resolvedTypeDefHandle != null)
                    {
                        return DecodeTypeDef(reader, (TypeDefinitionHandle)resolvedTypeDefHandle, fieldDef);
                    }
                    else
                    {
                        // Assume a pointer if we couldn't resolve the typedef
                        TypeReference td = reader.GetTypeReference((TypeReferenceHandle)handle);
                        return new FieldInfo(SimpleFieldKind.Pointer, reader.GetString(td.Name));
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
                return new FieldInfo(SimpleFieldKind.Pointer, "Void");

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
#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
        var sig = fieldDef.DecodeSignature(new GenericSignatureTypeProvider(), null);
#pragma warning restore CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.

        Match match = Regex.Match(sig, @"^(?<Namespace>\w*?.*?).?(?<TypeName>\w+)(?<Pointer>\*?)\[(?<Min>\d+)...(?<Max>\d+)]$") ??
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
            return new FieldInfo(SimpleFieldKind.Primitive, underlying);
        }
        else if (IsComInterface(reader, tdHandle))
        {
            return new FieldInfo(SimpleFieldKind.COM, reader.GetString(td.Name), 0, td);
        }
        else if (IsPseudoPrimitive(reader, tdHandle, out FieldInfo? fieldInfo))
        {
            return fieldInfo;
        }

        return new FieldInfo(SimpleFieldKind.Struct, reader.GetString(td.Name), 0, td);
    }

    private static bool IsComInterface(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);

        // All COM interfaces have the Interface flag
        if ((td.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Interface)
            return false;

        // Most (nearly all) COM interfaces have the [Guid] attribute
        if (CustomAttributeDecoder.GetAllNames(reader, td).Contains("GuidAttribute"))
            return true;

        // The base interfaces do not have GUIDs
        //string typeName = reader.GetString(td.Name);
        //if (typeName == "IUnknown" || typeName == "IDispatch")
        //   return true;

        if (!td.BaseType.IsNil)
        {
            // Fallback - check to see if interface derives from IUnknown or IDispatch
            string baseName = td.BaseType.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)td.BaseType).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)td.BaseType).Name),
                _ => throw new NotSupportedException($"Unknown base type while checking for COM: {td.BaseType.Kind}")
            };

            if (baseName == "IUnknown" || baseName == "IDispatch")
                return true;
        }
        else
        {
            // Fallback 2 - If BaseType is Nil and the interface is abstract, it's COM 
            // (base interfaces like IUnknown and caller-supplied interfaces like IOleUILinkInfoW)
            if (td.Attributes.HasFlag(TypeAttributes.Abstract))
                return true;
        }

        return false;
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
        var _ = blob.ReadSignatureHeader();
        SkipCustomModsAndPinned(ref blob);
        var sigTypeCode = blob.ReadSignatureTypeCode();

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

    // Skip the optional / required / pinned flags for fields
    private static void SkipCustomModsAndPinned(ref BlobReader b)
    {
        while (b.RemainingBytes > 0)
        {
            byte marker = b.ReadByte();

            switch (marker)
            {
                case 0x1F: // cmod_reqd
                case 0x20: // cmod_opt
                    b.ReadCompressedInteger(); // skip the TypeDefOrRef coded index
                    continue;

                case 0x45: // pinned
                    continue;

                default:
                    // Not a modifier - back up
                    b.Offset -= 1;
                    return;
            }
        }
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

        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.ModuleDefinition:
                // type is in this module
                foreach (var tdHandle in reader.TypeDefinitions)
                {
                    var td = reader.GetTypeDefinition(tdHandle);
                    if (reader.StringComparer.Equals(td.Name, name) && reader.StringComparer.Equals(td.Namespace, ns))
                    {
                        return tdHandle;
                    }
                }
                break;

            case HandleKind.TypeReference:
                // nested type - resolve parent and then check its nested types
                var parentHandle = (TypeReferenceHandle)tr.ResolutionScope;
                var parentTdHandle = ResolveTypeReference(reader, parentHandle);
                if (parentTdHandle != null)
                {
                    var parentTd = reader.GetTypeDefinition(parentTdHandle.Value);
                    foreach (var nestedHandle in parentTd.GetNestedTypes())
                    {
                        var nestedTd = reader.GetTypeDefinition(nestedHandle);
                        if (reader.StringComparer.Equals(nestedTd.Name, name))
                            return nestedHandle;
                    }
                }
                break;

            case HandleKind.AssemblyReference:
                // external type — not in this module
                return null;

            case HandleKind.ModuleReference:
                // type in another module of same assembly
                return null;
        }

        return null;
    }
}
