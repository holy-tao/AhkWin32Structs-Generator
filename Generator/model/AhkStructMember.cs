using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// A struct member. These should only be used by AhkStruct.
/// </summary>
public class AhkStructMember
{
    private readonly MetadataReader mr;
    private readonly FieldDefinition def;

    public readonly AhkStruct? embeddedStruct;

    private readonly AhkStruct parent;

    private readonly string? apiDetails;
    private readonly Dictionary<string, ApiDetails> apiDocs;

    public readonly MemberFlags flags;

    public int offset;

    public string Name;

    public FieldInfo fieldInfo { get; private set; }

    public int Size { get; private set; }

    // flags.HasFlag(MemberFlags.Union) || flags.HasFlag(MemberFlags.Anonymous) || 
    public bool IsNested => embeddedStruct?.typeDef.IsNested ?? false;

    public AhkStructMember(AhkStruct parent, MetadataReader mr, FieldDefinition fieldDef, Dictionary<string, string>? apiFields,
        Dictionary<string, ApiDetails> apiDocs, int offset = 0)
    {
        this.parent = parent;
        this.mr = mr;
        this.offset = offset;
        this.apiDocs = apiDocs;

        def = fieldDef;
        Name = mr.GetString(def.Name);

        fieldInfo = FieldSignatureDecoder.DecodeFieldType(mr, fieldDef);

        if (fieldInfo.Kind == SimpleFieldKind.Struct)
        {
            TypeDefinition fieldTypeDef = fieldInfo.TypeDef ??
                throw new NullReferenceException($"Null TypeDef for Class or Struct field {Name}");
            embeddedStruct = AhkStruct.Get(mr, fieldTypeDef, apiDocs);
            Size = embeddedStruct.Size;
        }
        else if (fieldInfo.Kind == SimpleFieldKind.Array)
        {
            FieldInfo arrayElementType = fieldInfo.UnderlyingType ??
                throw new NullReferenceException($"Null array element for Array field {Name}");
            Size = fieldInfo.Length * arrayElementType.GetWidth(parent.IsAnsi);

            if (arrayElementType.TypeDef != null)
                embeddedStruct = AhkStruct.Get(mr, (TypeDefinition)arrayElementType.TypeDef, apiDocs);
        }
        else
        {
            Size = fieldInfo.GetWidth(parent.IsAnsi);
        }

        flags = GetFlags();
        apiFields?.TryGetValue(Name, out apiDetails);
    }

    private MemberFlags GetFlags()
    {
        MemberFlags flags = MemberFlags.None;

        var attrs = CustomAttributeDecoder.GetAllNames(mr, def);
        if (attrs.Contains("ObsoleteAttribute"))
            flags |= MemberFlags.Deprecated;

        if (attrs.Contains("ReservedAttribute"))
            flags |= MemberFlags.Reserved;

        if (Name.StartsWith("___MISSING_ALIGNMENT__"))
            flags |= MemberFlags.Alignment;

        if (fieldInfo.TypeName.EndsWith("_e__Union") || (embeddedStruct?.IsUnion ?? false))
            flags |= MemberFlags.Union;

        if (fieldInfo.TypeName.EndsWith("_e__Struct") || fieldInfo.TypeName.StartsWith("_Anonymous") || (embeddedStruct?.Anonymous ?? false))
            flags |= MemberFlags.Anonymous;

        return flags;
    }

    public void ToAhk(StringBuilder sb, int embeddingOfset = 0)
    {
        MaybeAppendDocumentation(sb);

        switch (fieldInfo.Kind)
        {
            case SimpleFieldKind.Struct:
                ToAhkEmbeddedStruct(sb, offset + embeddingOfset);
                break;
            case SimpleFieldKind.Array:
                ToAhkArray(sb, offset + embeddingOfset);
                break;
            case SimpleFieldKind.String:
                ToAhkStringMember(sb, offset + embeddingOfset);
                break;
            case SimpleFieldKind.Class:
            case SimpleFieldKind.Primitive:
            case SimpleFieldKind.Pointer:
            case SimpleFieldKind.COM:
            case SimpleFieldKind.HRESULT:
                ToAhkNumericMember(sb, offset + embeddingOfset);
                break;
            default:
                throw new NotSupportedException($"Unsupported type (field {Name}): {fieldInfo.Kind}");
        }
    }

    private void ToAhkEmbeddedStruct(StringBuilder sb, int offset)
    {
        if (embeddedStruct == null)
            throw new NullReferenceException($"Null embeddedStruct for struct-type field {Name}");

        sb.AppendLine($"    {Name}{{");
        sb.AppendLine("        get {");
        sb.AppendLine($"            if(!this.HasProp(\"__{Name}\"))");
        sb.AppendLine($"                this.__{Name} := {embeddedStruct.Name}(this.ptr + {offset})");
        sb.AppendLine($"            return this.__{Name}");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private void MaybeAppendDocumentation(StringBuilder sb)
    {
        sb.AppendLine("    /**");

        if (apiDetails != null)
        {
            sb.AppendLine("     * " + AhkType.EscapeDocs(apiDetails, "    "));
        }

        if (flags.HasFlag(MemberFlags.Deprecated))
            sb.AppendLine("     * @deprecated");

        sb.AppendLine($"     * @type {{{(fieldInfo.Kind == SimpleFieldKind.Struct ? embeddedStruct?.Name : fieldInfo.AhkType)}}}");
        sb.AppendLine("     */");
    }

    // https://www.autohotkey.com/docs/v2/lib/NumPut.htm
    // https://www.autohotkey.com/docs/v2/lib/NumGet.htm
    public void ToAhkNumericMember(StringBuilder sb, int offset)
    {
        sb.AppendLine($"    {Name} {{");
        sb.AppendLine($"        get => NumGet(this, {offset}, \"{fieldInfo.GetDllCallType(true)}\")");
        sb.AppendLine($"        set => NumPut(\"{fieldInfo.GetDllCallType(true)}\", value, this, {offset})");
        sb.AppendLine($"    }}");
    }

    public void ToAhkStringMember(StringBuilder sb, int offset)
    {
        string encoding = parent.IsAnsi ? "UTF-8" : "UTF-16";

        sb.AppendLine($"    {Name} {{");
        sb.AppendLine($"        get => StrGet(this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
        sb.AppendLine($"        set => StrPut(value, this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
        sb.AppendLine($"    }}");
    }

    private void ToAhkArray(StringBuilder sb, int offset)
    {
        FieldInfo arrTypeInfo = fieldInfo.UnderlyingType ?? throw new NullReferenceException($"Null ArrayType for {Name}");

        string ahkElementType = arrTypeInfo.Kind switch
        {
            SimpleFieldKind.Primitive or SimpleFieldKind.Pointer => "Primitive",
            _ => arrTypeInfo.TypeName
        };
        string dllCallType = arrTypeInfo.Kind switch
        {
            SimpleFieldKind.Primitive => arrTypeInfo.GetDllCallType(false),
            SimpleFieldKind.Pointer => "ptr",
            _ => ""
        };

        sb.AppendLine($"    {Name}{{");
        sb.AppendLine("        get {");
        sb.AppendLine($"            if(!this.HasProp(\"__{Name}ProxyArray\"))");
        sb.AppendLine($"                this.__{Name}ProxyArray := Win32FixedArray(this.ptr + {offset}, {fieldInfo.Length}, {ahkElementType}, \"{dllCallType}\")");
        sb.AppendLine($"            return this.__{Name}ProxyArray");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
}