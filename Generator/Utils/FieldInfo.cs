using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

public record FieldInfo(SimpleFieldKind Kind, string TypeName, int Length = 0, TypeDefinition? TypeDef = null, FieldInfo? UnderlyingType = null, [NotNullIfNotNull(nameof(TypeDef))] MetadataReader? Reader = null)
{
    // 
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
            switch (TypeName.ToLower())
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
                case "typehandle":
                    return "ptr";
                default:
                    return "ptr";   // A pointer-sized NativeTypedef
            }
        }
        else if (Kind is SimpleFieldKind.HRESULT or SimpleFieldKind.NTSTATUS)
        {
            return "int";   // 32-bit integers under the hood
        }
        else if (Kind == SimpleFieldKind.NativeTypedef)
        {
            return UnderlyingType?.GetDllCallType(useNakedPointer) ?? throw new NullReferenceException();
        }
        else if (Kind == SimpleFieldKind.COM)
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
                    SimpleFieldKind.COM => "ptr*",
                    SimpleFieldKind.Primitive => UnderlyingType.TypeName.Equals("void", StringComparison.InvariantCultureIgnoreCase) ?
                        "ptr" :
                        UnderlyingType.GetDllCallType(useNakedPointer) + '*',
                    SimpleFieldKind.NativeTypedef or SimpleFieldKind.HRESULT => UnderlyingType.GetDllCallType(true) + "*",
                    _ => "ptr"
                };
            }

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
        else if (Kind == SimpleFieldKind.Pointer)
        {
            return 8;
        }
        else if (Kind is SimpleFieldKind.HRESULT or SimpleFieldKind.NTSTATUS)
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
            else if (Kind is SimpleFieldKind.HRESULT or SimpleFieldKind.NTSTATUS)
            {
                return Kind.ToString();
            }
            else if (Kind == SimpleFieldKind.Struct || Kind == SimpleFieldKind.Class || Kind == SimpleFieldKind.NativeTypedef)
            {
                return TypeName;
            }
            else
            {
                // Assuming 64-bit ahk
                return "Pointer";
            }
        }
    }

    public string GetTypeDefName()
    {
        return Reader?.GetString(TypeDef?.Name ?? throw new NullReferenceException(nameof(TypeDef)))
            ?? throw new NullReferenceException(nameof(Reader));
    }

    public string GetTypeDefNamespace()
    {
        return Reader?.GetString(TypeDef?.Namespace ?? throw new NullReferenceException(nameof(TypeDef)))
            ?? throw new NullReferenceException(nameof(Reader));
    }

    public string GetTypeDefFqn()
    {
        if(Reader is null)
            throw new NullReferenceException(nameof(Reader));
        if(TypeDef is null)
            throw new NullReferenceException(nameof(TypeDef));

        return $"{Reader.GetString(TypeDef.Value.Namespace)}.{Reader.GetString(TypeDef.Value.Name)}";
    }

    public string GetUnderlyingTypeFqn()
    {
        if(UnderlyingType is null)
            throw new NullReferenceException(nameof(UnderlyingType));
        return UnderlyingType.GetTypeDefFqn();
    }
}