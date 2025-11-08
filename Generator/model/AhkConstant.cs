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

    public bool IsStruct => fieldInfo.Kind == SimpleFieldKind.Struct;

    public AhkConstant(MetadataReader mr, FieldDefinition fieldDef, ApiDetails? apiDetails)
    {
        this.mr = mr;
        this.fieldDef = fieldDef;
        fieldInfo = fieldDef.DecodeSignature(new FieldSignatureProvider(mr), new());
        customAttributes = CustomAttributeDecoder.DecodeAll(mr, fieldDef);
        apiDetails?.Fields.TryGetValue(Name, out description);
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
            //TODO
            Console.WriteLine($"Struct constant: {Name}: {fieldInfo.TypeName}");
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

    public void ToAhkPrimitive(StringBuilder sb)
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

        sb.Append($"    static {Name} => ");
        sb.AppendLine(constant.TypeCode switch
        {
            ConstantTypeCode.Byte => $"0x{(byte)value:X2}",
            ConstantTypeCode.SByte => $"0x{(sbyte)value:X2}",
            ConstantTypeCode.String => AhkEscapeStringLiteral($"\"{value}\""),
            _ => value.ToString()
        });
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

    public List<string> GetReferencedTypes()
    {
        List<string> referencedTypes = [];
        if (IsStruct && !IsGuid)
        {
            string fqn = AhkType.GetFqn(mr, fieldInfo.TypeDef ?? throw new NullReferenceException());
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
                _ => c
            });
        }

        return sb.ToString();
    }
}