using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// A struct member. These should only be used by AhkStruct.
/// </summary>
public class AhkStructMember
{
    /// <summary>
    /// A bitfield member parsed from a [NativeBitField] element
    /// </summary>
    /// <param name="Name"></param>
    /// <param name="Offset"></param>
    /// <param name="Length"></param>
    public record struct BitfieldMember(string Name, long Offset, long Length)
    {
        internal static BitfieldMember ParseAttribute(CustomAttribute attr)
        {
            var decoded = attr.DecodeValue(new CaTypeProvider());

            string memberName = (string?)decoded.FixedArguments[0].Value ?? throw new NullReferenceException(nameof(memberName));
            long bitOffset = (long?)decoded.FixedArguments[1].Value ?? throw new NullReferenceException(nameof(bitOffset));
            long length = (long?)decoded.FixedArguments[2].Value ?? throw new NullReferenceException(nameof(length));

            return new BitfieldMember(memberName, bitOffset, length);
        }
    }

    private readonly MetadataReader mr;
    private readonly FieldDefinition def;

    public readonly AhkStruct? embeddedStruct;

    private readonly AhkStruct parent;

    private readonly string? apiDetails;
    private readonly Dictionary<string, string>? apiFields;

    public readonly List<BitfieldMember> bitfields;

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
        this.apiFields = apiFields;

        def = fieldDef;
        Name = mr.GetString(def.Name);

        fieldInfo = fieldDef.DecodeSignature(new FieldSignatureProvider(mr, parent.typeDef), new());

        if (fieldInfo.Kind == SimpleFieldKind.Struct)
        {
            TypeDefinition fieldTypeDef = fieldInfo.TypeDef ??
                throw new NullReferenceException($"Null TypeDef for Class or Struct field {Name}");
            embeddedStruct = AhkStruct.Get(mr, fieldTypeDef, apiDocs) ?? throw new NullReferenceException();
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
        bitfields = flags.HasFlag(MemberFlags.NativeBitField) ? GetBitfields() : [];

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

        if (attrs.Contains("NativeBitfieldAttribute"))
            flags |= MemberFlags.NativeBitField;

        if (Name.StartsWith("___MISSING_ALIGNMENT__"))
            flags |= MemberFlags.Alignment;

        if (fieldInfo.TypeName.EndsWith("_e__Union") || (embeddedStruct?.IsUnion ?? false))
            flags |= MemberFlags.Union;

        if (fieldInfo.TypeName.StartsWith("_Anonymous"))
            flags |= MemberFlags.Anonymous;

        return flags;
    }

    private List<BitfieldMember> GetBitfields()
    {
        return CustomAttributeDecoder.GetAllAttributes(mr, def, "NativeBitfieldAttribute")
            .Select(BitfieldMember.ParseAttribute)
            .ToList();
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
                ToAhkNumericMember(sb, offset + embeddingOfset, fieldInfo);
                break;
            case SimpleFieldKind.NativeTypedef:
                if (fieldInfo.UnderlyingType == null)
                    throw new NullReferenceException();
                ToAhkNumericMember(sb, offset + embeddingOfset, fieldInfo.UnderlyingType);
                break;
            default:
                throw new NotSupportedException($"Unsupported type (field {Name}): {fieldInfo.Kind}");
        }
    }

    private void ToAhkEmbeddedStruct(StringBuilder sb, int offset)
    {
        if (embeddedStruct == null)
            throw new NullReferenceException($"Null embeddedStruct for struct-type field {Name}");

        string qualifiedName = embeddedStruct.typeDef.IsNested ?
            $"%this.__Class%.{embeddedStruct.Name}" :   //TODO a nicer way to do this woud be to walk up parents
            embeddedStruct.Name;

        sb.AppendLine($"    {Name}{{");
        sb.AppendLine("        get {");
        sb.AppendLine($"            if(!this.HasProp(\"__{Name}\"))");
        sb.AppendLine($"                this.__{Name} := {qualifiedName}({offset}, this)");
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

        if (flags.HasFlag(MemberFlags.NativeBitField))
        {
            sb.AppendLine("     * This bitfield backs the following members:");
            bitfields.ForEach(bf => sb.AppendLine($"     * - {bf.Name}"));
        }

        if (flags.HasFlag(MemberFlags.Deprecated))
            sb.AppendLine("     * @deprecated");

        sb.AppendLine($"     * @type {{{(fieldInfo.Kind == SimpleFieldKind.Struct ? embeddedStruct?.Name : fieldInfo.AhkType)}}}");
        sb.AppendLine("     */");
    }

    // https://www.autohotkey.com/docs/v2/lib/NumPut.htm
    // https://www.autohotkey.com/docs/v2/lib/NumGet.htm
    public void ToAhkNumericMember(StringBuilder sb, int offset, FieldInfo fieldInfo)
    {
        sb.AppendLine($"    {Name} {{");
        sb.AppendLine($"        get => NumGet(this, {offset}, \"{fieldInfo.GetDllCallType(true)}\")");
        sb.AppendLine($"        set => NumPut(\"{fieldInfo.GetDllCallType(true)}\", value, this, {offset})");
        sb.AppendLine($"    }}");

        if (flags.HasFlag(MemberFlags.NativeBitField))
        {
            AppendBitFieldMembers(sb);
        }
    }

    public void ToAhkStringMember(StringBuilder sb, int offset)
    {
        string encoding = parent.IsAnsi ? "UTF-8" : "UTF-16";

        sb.AppendLine($"    {Name} {{");
        sb.AppendLine($"        get => StrGet(this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
        sb.AppendLine($"        set => StrPut(value, this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
        sb.AppendLine($"    }}");
    }

    private void AppendBitFieldMembers(StringBuilder sb)
    {
        bitfields
            .Where(bf => bf.Name is not "Reserved")
            .ToList()
            .ForEach(bf => AppendBitfieldMember(sb, bf));
    }

    private void AppendBitfieldMember(StringBuilder sb, BitfieldMember bitfield)
    {
        sb.AppendLine();

        sb.AppendLine("    /**");
        if (apiFields?.TryGetValue(bitfield.Name, out string? memberDescription) ?? false)
        {
            sb.AppendLine("     * " + AhkType.EscapeDocs(memberDescription, "    "));
        }

        sb.AppendLine($"     * @type {{{(fieldInfo.Kind == SimpleFieldKind.Struct ? embeddedStruct?.Name : fieldInfo.AhkType)}}}");
        sb.AppendLine("     */");

        long mask = (1L << (int)bitfield.Length) - 1;

        sb.AppendLine($"    {bitfield.Name} {{");
        sb.AppendLine($"        get => (this.{Name} >> {bitfield.Offset}) & 0x{mask:X}");
        sb.AppendLine($"        set => this.{Name} := ((value & 0x{mask:X}) << {bitfield.Offset}) | (this.{Name} & ~(0x{mask:X} << {bitfield.Offset}))");
        sb.AppendLine($"    }}");
    }

    private void ToAhkArray(StringBuilder sb, int offset)
    {
        FieldInfo arrTypeInfo = fieldInfo.UnderlyingType ?? throw new NullReferenceException($"Null ArrayType for {Name}");

        string ahkElementType = arrTypeInfo.Kind switch
        {
            SimpleFieldKind.Primitive or SimpleFieldKind.Pointer or SimpleFieldKind.COM or SimpleFieldKind.NativeTypedef => "Primitive",
            SimpleFieldKind.Struct => (arrTypeInfo.TypeDef?.IsNested ?? false) ?
                $"%this.__Class%.{arrTypeInfo.TypeName}" :   //TODO a nicer way to do this woud be to walk up parents
                arrTypeInfo.TypeName,
            _ => arrTypeInfo.TypeName
        };
        
        string dllCallType = arrTypeInfo.Kind switch
        {
            SimpleFieldKind.Primitive => arrTypeInfo.GetDllCallType(false),
            SimpleFieldKind.Pointer or SimpleFieldKind.COM => "ptr",
            SimpleFieldKind.NativeTypedef => arrTypeInfo.UnderlyingType?.GetDllCallType(false)
                ?? throw new NullReferenceException(),
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

public class AhkStructMemberEqualityComparer : IEqualityComparer<AhkStructMember>
{
    public bool Equals(AhkStructMember? x, AhkStructMember? y)
    {
        if (x is null || y is null)
        {
            return false;
        }

        if (x.flags.HasFlag(MemberFlags.NativeBitField) && y.flags.HasFlag(MemberFlags.NativeBitField))
        {
            // bitfields are allowed to have different names if the fields the back are identical
            return x.bitfields.SequenceEqual(y.bitfields);
        }
        else
        {
            // Non-bitfield members are equal if they share a name and offset
            return x.offset == y.offset && x.Name.Equals(y.Name, StringComparison.InvariantCultureIgnoreCase);
        }
    }

    public int GetHashCode([DisallowNull] AhkStructMember obj)
    {
        throw new NotImplementedException();
    }
}