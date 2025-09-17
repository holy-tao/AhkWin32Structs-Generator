using System.Reflection.Metadata;

public record FieldInfo(SimpleFieldKind Kind, string TypeName, int Length = 0, TypeDefinition? TypeDef = null, FieldInfo? UnderlyingType = null)
{
    public static int POINTER_SIZE = 8;

    // https://www.autohotkey.com/docs/v2/lib/DllCall.htm
    public string DllCallType
    {
        get
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
                        throw new NotSupportedException(TypeName);
                }
            }
            else if (Kind == SimpleFieldKind.Pointer)
            {
                if (UnderlyingType != null && UnderlyingType.Kind == SimpleFieldKind.Primitive && UnderlyingType.TypeName != "Void")
                {
                    return UnderlyingType.DllCallType + '*';
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
            return Length * (ansi? 1 : 2);  //2 for CHARs, assuming UTF-16
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
                    case "void":
                    case "ptr":
                        return "Pointer";
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
            else if (Kind == SimpleFieldKind.Pointer || Kind == SimpleFieldKind.COM)
            {
                return $"Pointer<{TypeName}>";
            }
            else
            {
                // Assuming 64-bit ahk
                return "Pointer";
            }
        }
    }
}