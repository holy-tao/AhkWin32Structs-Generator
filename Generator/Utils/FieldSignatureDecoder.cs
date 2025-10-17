using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;

public static class FieldSignatureDecoder
{
    public static FieldInfo DecodeFieldType(MetadataReader reader, FieldDefinition fieldDef)
    {
        var blob = reader.GetBlobReader(fieldDef.Signature);
        byte header = blob.ReadByte();
        if (header != (byte)SignatureKind.Field)
            throw new BadImageFormatException("Not a field signature");

        return fieldDef.DecodeSignature(new FieldSignatureProvider(reader), new());
    }

    public static FieldInfo DecodeTypeDef(MetadataReader reader, TypeDefinitionHandle tdHandle)
    {
        var td = reader.GetTypeDefinition(tdHandle);
        string typeName = reader.GetString(td.Name);

        if (typeName == "HRESULT")
        {
            return new FieldInfo(SimpleFieldKind.HRESULT, "HRESULT", 0, td);
        }
        else if(IsNonHandleNativeTypedef(reader, td))
        {
            return DecodeNativeTypedef(reader, td);
        }
        else if (IsEnum(reader, tdHandle))
        {
            string underlying = GetEnumUnderlyingType(reader, tdHandle);
            return new FieldInfo(SimpleFieldKind.Primitive, underlying);
        }
        else if (IsUsedAsFunctionPointer(reader, tdHandle))
        {
            return new FieldInfo(SimpleFieldKind.Pointer, typeName, 0, td);
        }
        else if (IsComInterface(reader, tdHandle))
        {
            return new FieldInfo(SimpleFieldKind.COM, typeName, 0, td);
        }

        return new FieldInfo(SimpleFieldKind.Struct, typeName, 0, td);
    }

    public static bool IsComInterface(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition td = reader.GetTypeDefinition(handle);

        // All COM interfaces have the Interface flag
        if ((td.Attributes & TypeAttributes.ClassSemanticsMask) != TypeAttributes.Interface)
            return false;

        // Most (nearly all) COM interfaces have the [Guid] attribute
        if (CustomAttributeDecoder.GetAllNames(reader, td).Contains("GuidAttribute"))
            return true;

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

    public static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
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

    public static string GetEnumUnderlyingType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var td = reader.GetTypeDefinition(handle);
        foreach (var fieldHandle in td.GetFields())
        {
            var fd = reader.GetFieldDefinition(fieldHandle);
            if (reader.StringComparer.Equals(fd.Name, "value__"))
            {
                return fd.DecodeSignature(new FieldSignatureProvider(reader), new()).TypeName;
            }
        }
        return "Int32";
    }

    /// <summary>
    /// Some function pointers (notably the LPFN*PROC callbacks) are represented in the metadata as empty
    /// structs. So when we encounter one we need to check its attributes to see if it's a function pointer
    /// </summary>
    public static bool IsUsedAsFunctionPointer(MetadataReader reader, TypeDefinitionHandle defHandle)
    {
        TypeDefinition typeDef = reader.GetTypeDefinition(defHandle);
        return CustomAttributeDecoder.GetAllNames(reader, typeDef).Contains("UnmanagedFunctionPointerAttribute");
    }

    public static bool IsNonHandleNativeTypedef(MetadataReader mr, TypeDefinition typeDef)
    {
        return CustomAttributeDecoder.GetAllNames(mr, typeDef).Contains("NativeTypedefAttribute")
            && !AhkStruct.IsHandle(mr, typeDef)
            && typeDef.GetFields().Count == 1;
    }

    public static FieldInfo DecodeNativeTypedef(MetadataReader mr, TypeDefinition typeDef)
    {
        FieldDefinition fieldDef = mr.GetFieldDefinition(typeDef.GetFields().First());
        
        return new FieldInfo(
            SimpleFieldKind.NativeTypedef,
            mr.GetString(typeDef.Name),
            0,
            typeDef,
            fieldDef.DecodeSignature(new FieldSignatureProvider(mr, typeDef), new())
        );
    }

    public static TypeDefinitionHandle? ResolveTypeReference(MetadataReader reader, TypeReferenceHandle trHandle)
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
