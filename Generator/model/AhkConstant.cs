using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkConstant
{
    private readonly MetadataReader mr;

    private readonly FieldDefinition fieldDef;

    private readonly FieldInfo fieldInfo;

    private readonly List<CAInfo> customAttributes;

    public string Name => mr.GetString(fieldDef.Name);

    private readonly string? description;

    public bool IsGuid => customAttributes.Any(c => c.Name is "GuidAttribute");

    [MemberNotNullWhen(true, nameof(decodedStruct))]
    public bool IsStruct => fieldInfo.Kind == SimpleFieldKind.Struct;

    private readonly AhkStruct? decodedStruct;

    public AhkConstant(MetadataReader mr, FieldDefinition fieldDef, ApiDetails? apiDetails)
    {
        this.mr = mr;
        this.fieldDef = fieldDef;
        fieldInfo = fieldDef.DecodeSignature(new FieldSignatureProvider(mr), new());
        customAttributes = CustomAttributeDecoder.DecodeAll(mr, fieldDef);
        apiDetails?.Fields.TryGetValue(Name, out description);

        if (IsStruct)
        {
            decodedStruct = AhkStruct.Get(mr, fieldInfo.TypeDef ?? throw new NullReferenceException())
                ?? throw new NullReferenceException();
        }
    }

    public void ToAhk(StringBuilder sb)
    {
        AppendDocumentation(sb);

        if (IsGuid)
        {
            ToAhkGuid(sb);
        }
        else if (IsStruct)
        {
            // Console.WriteLine($"Struct constant: {Name}: {fieldInfo.TypeName}");
            ToAhkStruct(sb);
        }
        else
        {
            ToAhkPrimitive(sb);
        }
    }
    
    public void ToAhkGuid(StringBuilder sb)
    {
        Guid guid = GuidDecoder.DecodeGuid(mr, fieldDef);
        sb.AppendLine($"    static {Name} => Guid(\"{{{guid.ToString("D")}}}\")");
    }

    public void ToAhkStruct(StringBuilder sb)
    {
        if (AhkStruct.IsHandle(mr, decodedStruct!.typeDef))
        {
            // Constant handles are not owned by the caller
            sb.AppendLine($"    static {Name} => {decodedStruct.Name}({{Value: {GetValueAsAhk()}}}, false)");
            return;
        }

        Queue<string> constValues = DecodeConstantAttribute();

        sb.AppendLine($"    static {Name} {{");
        sb.AppendLine($"        get {{");
        sb.AppendLine($"            value := {decodedStruct.Name}()");
        AppendStructFillCode(sb, decodedStruct, "value", constValues);
        sb.AppendLine($"            return value");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
    }
    
    private void AppendStructFillCode(StringBuilder sb, AhkStruct ahkStruct, string typePrefix, Queue<string> constValues)
    {
        string indent = "            ";

        foreach(AhkStructMember member in ahkStruct.Members)
        {
            if (member.fieldInfo.Kind is SimpleFieldKind.Struct)
            {
                AhkStruct embedded = member.embeddedStruct
                    ?? throw new NullReferenceException(nameof(member.embeddedStruct));

                AppendStructFillCode(sb, embedded, string.Join('.', typePrefix, embedded.Name), constValues);
            }
            else if (member.fieldInfo.Kind is SimpleFieldKind.Pointer && member.fieldInfo.TypeName == "Guid")
            {
                // Pointers to Guids are special, they also specify the guid
                Guid parsedGuid = GuidDecoder.DecodeFromQueue(constValues);

                sb.AppendLine($"{indent}static {member.Name}_guid := Guid(\"{{{parsedGuid.ToString("D")}}}\")");
                sb.AppendLine($"{indent}{typePrefix}.{member.Name} := {member.Name}_guid.ptr");
            }
            else if (member.fieldInfo.Kind is SimpleFieldKind.Primitive or SimpleFieldKind.Pointer)
            {
                // Primitive or Void* pointer
                sb.AppendLine($"{indent}{typePrefix}.{member.Name} := {constValues.Dequeue()}");
            }
            else if(member.fieldInfo.Kind is SimpleFieldKind.Array)
            {
                // right now only SID_IDENTIFIER_AUTHORITY constants
                for(int i = 0; i < member.fieldInfo.Length; i++)
                {
                    sb.AppendLine($"{indent}{typePrefix}.{member.Name}[{i + 1}] := {constValues.Dequeue()}");
                }
            }
            else
            {
                throw new NotSupportedException($"{Name} ({ahkStruct.Namespace}.{ahkStruct.Name}.{member.Name}) : {member.fieldInfo.Kind} --- {member.fieldInfo}");
            }
        }
    }
    
    private Queue<string> DecodeConstantAttribute()
    {
        CAInfo attrInfo = customAttributes.Single(c => c.Name is "ConstantAttribute");
        string raw = (string)(attrInfo.Attr.FixedArguments.First().Value ?? throw new NullReferenceException());

        IEnumerable<string> split = raw.Split(',')
            .Select(str => str.TrimStart('{'))
            .Select(str => str.TrimEnd('}'))
            .Select(str => str.Trim());

        return new Queue<string>(split);
    }

    public void ToAhkPrimitive(StringBuilder sb)
    {
        sb.Append($"    static {Name} => {GetValueAsAhk()}");
        sb.AppendLine();
    }

    private protected void AppendDocumentation(StringBuilder sb)
    {
        sb.AppendLine("    /**");

        if (description != null)
        {
            sb.AppendLine("     * " + AhkType.EscapeDocs(description, "    "));
        }

        if (customAttributes.Any(c => c.Name is "ObsoleteAttribute"))
            sb.AppendLine($"     * @deprecated");

        sb.AppendLine($"     * @type {{{GetAhkType()}}}");
        sb.AppendLine("     */");
    }

    private Constant GetFieldValue()
    {
        ConstantHandle constHandle = fieldDef.GetDefaultValue();
        if (constHandle.IsNil)
            throw new ArgumentException($"Primitive constant {Name} has no default value");

        return mr.GetConstant(constHandle);
    }

    private string GetValueAsAhk()
    {
        Constant constant = GetFieldValue();
        BlobReader blob = mr.GetBlobReader(constant.Value);

        object value = constant.TypeCode switch
        {
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int16 => blob.ReadInt16(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.SByte => blob.ReadSByte(),
            ConstantTypeCode.Single => blob.ReadSingle(),
            ConstantTypeCode.Double => blob.ReadDouble(),
            ConstantTypeCode.Char => blob.ReadChar(),
            ConstantTypeCode.String => blob.ReadUTF16(blob.Length),
            _ => throw new NotSupportedException($"Unexpected enum constant type {constant.TypeCode}: {Name}")
        };

        return constant.TypeCode switch
        {
            ConstantTypeCode.Byte => $"0x{(byte)value:X2}",
            ConstantTypeCode.SByte => $"0x{(sbyte)value:X2}",
            ConstantTypeCode.String => AhkEscapeStringLiteral($"\"{value}\""),
            _ => value.ToString() ?? throw new NullReferenceException()
        };
    }

    public bool NeedsGuid(){
        if (IsGuid)
        {
            return true;
        }

        if (IsStruct)
        {
            return decodedStruct.Members.Any(m => m.fieldInfo.Kind == SimpleFieldKind.Pointer && m.fieldInfo.TypeName is "Guid");
        }

        return false;
    }

    public List<string> GetReferencedTypes()
    {
        List<string> referencedTypes = [];
        if (IsStruct && !IsGuid)
        {
            TypeDefinition structType = fieldInfo.TypeDef ?? throw new NullReferenceException(nameof(fieldInfo.TypeDef));

            string fqn = AhkType.GetFqn(mr, structType);
            referencedTypes.AddRange(decodedStruct!.GetReferencedTypes());
            referencedTypes.Add(fqn);
        }

        return referencedTypes;
    }

    private string GetAhkType()
    {
        if (IsGuid)
        {
            return "Guid";
        }
        else if (IsStruct)
        {
            return fieldInfo.AhkType;
        }
        else
        {
            Constant constant = GetFieldValue();
            return ConstantTypeCodeToAhkType(constant.TypeCode);
        }
    }

    private static string ConstantTypeCodeToAhkType(ConstantTypeCode typeCode) => typeCode switch
    {
        ConstantTypeCode.Single => "Float",
        ConstantTypeCode.Double => "Float",
        ConstantTypeCode.String => "String",
        _ => $"Integer ({typeCode})"
    };

    public static string AhkEscapeStringLiteral(string val)
    {
        StringBuilder sb = new();

        foreach (char c in val)
        {
            if (char.IsControl(c))
            {
                sb.Append($"\" Chr({(int)c}) \"");
                continue;
            }

            sb.Append(c switch
            {
                '\n' => "`n",
                '\t' => "`t",
                '\r' => "`r",
                '`' => "``",
                _ => c
            });
        }

        return sb.ToString();
    }
}