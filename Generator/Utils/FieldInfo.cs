using System.Reflection.Metadata;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

// TODO this record is a bit of a mess

/// <summary>
/// Contrary to the name, this contains type information for fields, method parameters, return types,
/// and other related elements.
/// 
/// The name is a holdover from when this generator was focused on structs and the only type information
/// we cared about was that of the struct's fields.
/// </summary>
public record FieldInfo
{
    /// <summary>
    /// The kind of field this is, for the purposes of AutoHotkey code generation (Primitive, COM, etc.)
    /// </summary>
    public SimpleFieldKind Kind {get; init; }

    /// <summary>
    /// The name of the type. This is not necessarily the same as the name of the TypeDefinition, and the
    /// TypeDefinition may not be available if this is a primitive type (in which case this will be something like
    /// "Single" or "Int32").
    /// </summary>
    public string TypeName { get; init; }

    /// <summary>
    /// If this is a fixed-size array, this will be the number of elements.
    /// </summary>
    public int Length { get; init; }

    /// <summary>
    /// If this is a struct or class, this will be the TypeDefinition of the type.
    /// If this is a primitive, this will be null.
    /// </summary>
    public TypeDefinition? TypeDef { get; init; }

    /// <summary>
    /// If this is a pointer, this will be the underlying type
    /// </summary>
    public FieldInfo? UnderlyingType { get; init; }

    /// <summary>
    /// The metadata reader that contains the TypeDefinition, if applicable. Use this when reading data off the
    /// TypeDefinition, as it may be in a different module or assembly than that of the type containing this one
    /// </summary>
    public MetadataReader? Reader { get; init; }

    public ImmutableArray<FieldInfo> GenericArguments { get; init; }

    public bool HasGenericArgs => GenericArguments.Length > 0;

    public FieldInfo(SimpleFieldKind kind, string typeName, int length = 0, TypeDefinition? typeDef = null, 
        FieldInfo? underlyingType = null, MetadataReader? reader = null, ImmutableArray<FieldInfo>? genericArguments = null)
    {
        Kind = kind;
        TypeName = typeName;
        Length = length;
        TypeDef = typeDef;
        UnderlyingType = underlyingType;
        Reader = reader;
        GenericArguments = genericArguments ?? [];
    }

    /// <summary>
    /// Get the DllCall type of the field. See: <see cref="https://www.autohotkey.com/docs/v2/lib/DllCall.htm"/>
    /// </summary>
    /// <param name="useNakedPointer">If false, pointers to primitives are emitted as type*, if true, just "ptr</param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public string GetDllCallType(bool useNakedPointer)
    {
        if (Kind == SimpleFieldKind.Primitive)
        {
            return TypeName.ToLower() switch
            {
                "single" => "float",
                "boolean" or "int32" => "int",
                "double" => "double",
                "int64" => "int64",
                "uint32" => "uint",
                "uint64" => "uint",
                "int16" => "short",
                "uint16" => "ushort",
                "byte" or "sbyte" or "char" => "char",
                "uintptr" or "intptr" or "void" or "ptr" or "typehandle" => "ptr",
                _ => "ptr",// A pointer-sized NativeTypedef
            };
        }
        else if (Kind == SimpleFieldKind.HRESULT)
        {
            return "int";   // 32-bit integers under the hood
        }
        else if (Kind == SimpleFieldKind.NativeTypedef)
        {
            return UnderlyingType?.GetDllCallType(useNakedPointer) ?? throw new NullReferenceException();
        }
        else if (Kind is SimpleFieldKind.COM or SimpleFieldKind.Class)
        {
            return "ptr";
        }
        else if (Kind == SimpleFieldKind.Pointer)
        {
            if (!useNakedPointer && UnderlyingType != null)
            {
                return UnderlyingType.Kind switch
                {
                    SimpleFieldKind.Pointer => UnderlyingType.Kind == SimpleFieldKind.Pointer ?
                        "ptr*" :
                        UnderlyingType.GetDllCallType(useNakedPointer),
                    SimpleFieldKind.COM or SimpleFieldKind.Class => "ptr*",
                    SimpleFieldKind.Primitive => UnderlyingType.TypeName.Equals("void", StringComparison.InvariantCultureIgnoreCase) ?
                        "ptr" :
                        UnderlyingType.GetDllCallType(useNakedPointer) + '*',
                    SimpleFieldKind.NativeTypedef or SimpleFieldKind.HRESULT => UnderlyingType.GetDllCallType(true) + "*",
                    SimpleFieldKind.OpenGeneric => "ptr*",
                    _ => "ptr"
                };
            }

            return "ptr";
        }
        else if (Kind is SimpleFieldKind.OpenGeneric)
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

    public int GetWidth(bool ansi)
    {
        if (Kind == SimpleFieldKind.Primitive)
        {
            switch (TypeName.ToLower())
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
                case "string":
                    return 8;       // HSTRING
                default:
                    throw new NotSupportedException($"{TypeName} ({Kind})");
            }
        }
        else if (Kind == SimpleFieldKind.Array)
        {
            throw new NotSupportedException("Cannot get width of array FieldInfo directly - use Rank * width of TypeDef");
        }
        else if (Kind == SimpleFieldKind.String)
        {
            return Length * (ansi ? 1 : 2);  //2 for CHARs, assuming UTF-16
        }
        else if (Kind is SimpleFieldKind.Pointer or SimpleFieldKind.OpenGeneric)
        {
            return 8;
        }
        else if (Kind == SimpleFieldKind.HRESULT)
        {
            return 4;
        }
        else if (Kind == SimpleFieldKind.NativeTypedef)
        {
            return UnderlyingType?.GetWidth(ansi) ?? throw new NullReferenceException();
        }
        else
        {
            // Else assume pointer
            return 8;
        }
    }

    // Get the name of the AHK type that's used here, for documentation purposes only
    public string AhkType
    {
        get
        {
            if (Kind == SimpleFieldKind.Primitive)
            {
                switch (TypeName.ToLower())
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
                    case "ptr":
                        return "Pointer";
                    case "void":
                        return "Void";
                    case "string":
                        return "HSTRING";   // Primitive Strings mean WinRT strings, which are pointers to HSTRINGS
                    case "object":      
                        return "IInspectable"; // TODO figure something out about this - can we wrap automatically?
                    default:
                        throw new NotSupportedException(TypeName);
                }
            }
            else if (Kind == SimpleFieldKind.String)
            {
                return "String";
            }
            else if (Kind == SimpleFieldKind.Array)
            {
                return $"Array<{TypeName}>";
            }
            else if (Kind == SimpleFieldKind.Pointer)
            {
                return UnderlyingType == null ? $"Pointer<{TypeName}>" : $"Pointer<{UnderlyingType?.AhkType}>";
            }
            else if (Kind == SimpleFieldKind.COM)
            {
                return TypeName;
            }
            else if (Kind == SimpleFieldKind.HRESULT)
            {
                return "HRESULT";
            }
            else if (Kind == SimpleFieldKind.Struct || Kind == SimpleFieldKind.Class || Kind == SimpleFieldKind.NativeTypedef)
            {
                return HasGenericArgs ? $"{TypeName}<{string.Join(", ", GenericArguments.Select(arg => arg.AhkType))}>" : TypeName;
            }
            else if (Kind == SimpleFieldKind.OpenGeneric)
            {
                return "Generic";
            }
            else
            {
                // Assuming 64-bit ahk
                return "Pointer";
            }
        }
    }

    /// <summary>
    /// We don't have generics in AHK, but we can bind arguments to functions - so instead we pass around functions
    /// that return objects of the generic type. In most case this is just Call, but in some cases we need to do
    /// other work.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public string GetTypeAsGenericCallable() => Kind switch
    {
        // Object is just IInspectable, all other primitives and structs are boxed in IPropertyValue, which we can use to unbox them
        // This generator won't work for the Get*Array methods. None appear in WinRT, but worth noting
        SimpleFieldKind.Primitive or SimpleFieldKind.Struct => TypeName.ToLower() is "object" ? 
            "IInspectable" :            // non-object Objects are boxed as PropertyValues...
            TypeName is "HSTRING" ?     // except strings (usually)
                $"(ptr) => HSTRING({{ Value: ptr }})" :
                $"(ptr) => IPropertyValue(ptr).Get{TypeName}()",
        SimpleFieldKind.OpenGeneric => $"this.{TypeName}",      // Type comes from implementer
        _ => HasGenericArgs ? 
            // Generic - bind generic types / getters to Call method of the type of the return value
            $"{GetTypeDefNameNoBacktick()}.Call.Bind({GetTypeDefNameNoBacktick()}, {string.Join(", ", GenericArguments.Select(arg => arg.GetTypeAsGenericCallable()))})" :
            GetTypeDefNameNoBacktick()
    };

    [MemberNotNull(nameof(Reader), nameof(TypeDef))]
    public string GetTypeDefName()
    {
        return Reader?.GetString(TypeDef?.Name ?? throw new NullReferenceException(nameof(TypeDef)))
            ?? throw new NullReferenceException(nameof(Reader));
    }

    public string GetTypeDefNameNoBacktick() => GetTypeDefName().Split("`").First();

    [MemberNotNull(nameof(Reader), nameof(TypeDef))]
    public string GetTypeDefNamespace()
    {
        return Reader?.GetString(TypeDef?.Namespace ?? throw new NullReferenceException(nameof(TypeDef)))
            ?? throw new NullReferenceException(nameof(Reader));
    }

    [MemberNotNull(nameof(Reader), nameof(TypeDef))]
    public string GetTypeDefFqn()
    {
        if(Reader is null)
            throw new NullReferenceException(nameof(Reader));
        if(TypeDef is null)
            throw new NullReferenceException(nameof(TypeDef));

        return $"{Reader.GetString(TypeDef.Value.Namespace)}.{Reader.GetString(TypeDef.Value.Name)}";
    }

    [MemberNotNull(nameof(UnderlyingType))]
    public string GetUnderlyingTypeFqn()
    {
        if(UnderlyingType is null)
            throw new NullReferenceException(nameof(UnderlyingType));
        return UnderlyingType.GetTypeDefFqn();
    }
    
    [MemberNotNull(nameof(UnderlyingType))]
    public string GetUnderlyingTypeName()
    {
        if(UnderlyingType is null)
            throw new NullReferenceException(nameof(UnderlyingType));
        return UnderlyingType.GetTypeDefName();
    }

    [MemberNotNull(nameof(Reader), nameof(TypeDef))]
    public AhkStruct DecodeStruct()
    {
        if(Reader is null)
            throw new NullReferenceException(nameof(Reader));
        if(TypeDef is null)
            throw new NullReferenceException(nameof(TypeDef));

        return AhkStruct.Get(Reader, TypeDef.Value) ?? throw new TypeAccessException($"Could not resolve '{GetTypeDefFqn()}'");
    }

    public string GetFullTypeSignature()
    {
        switch(Kind)
        {
            case SimpleFieldKind.Primitive or SimpleFieldKind.NativeTypedef:
                return TypeName switch
                {
                    "String" => "Windows.Win32.System.WinRT.HSTRING",
                    "Object" => "Windows.Win32.System.WinRT.IInspectable",
                    _ => TypeName
                };
            case SimpleFieldKind.String:
                return "Windows.Win32.System.WinRT.HSTRING";
            case SimpleFieldKind.HRESULT:
                return "Int32";
            case SimpleFieldKind.Array:
                return TypeName + "[]";
            case SimpleFieldKind.Pointer:
                return UnderlyingType?.GetFullTypeSignature() + "*";
            case SimpleFieldKind.Struct:
                return GetTypeDefFqn();
            case SimpleFieldKind.Class or SimpleFieldKind.COM:
                if(GenericArguments.Length > 0)
                    return $"{GetTypeDefFqn()}<{string.Join(",", GenericArguments.Select(info => info.GetFullTypeSignature()))}>";
                
                return GetTypeDefFqn();
            case SimpleFieldKind.OpenGeneric:
                return $"{GetTypeDefFqn()}<{string.Join(",", GenericArguments.Select(info => info.GetFullTypeSignature()))}>";
                
            default:
                throw new NotSupportedException(Kind.ToString());
        }
            

    }
}