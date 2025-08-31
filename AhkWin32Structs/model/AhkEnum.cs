
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public class AhkEnum : AhkType
{
    private readonly List<ConstantInfo> constants;

    public AhkEnum(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        constants = new List<ConstantInfo>();

        foreach (FieldDefinitionHandle fieldDefhandle in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(fieldDefhandle);
            if (mr.GetString(fieldDef.Name) == "value__")
            {
                //value__ contains the struct's underlying data type
            }
            else
            {
                constants.Add(ConstantDecoder.DecodeConstant(mr, fieldDef));
            }
        }
    }

    public override void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name}{{");

        foreach (ConstantInfo constant in constants)
        {
            sb.AppendLine();
            MaybeAddConstDocumentation(sb, constant);
            sb.AppendLine($"    {constant.Name} => {constant.ValueAsAhkLiteral}");
        }

        sb.AppendLine("}");
    }

    private void MaybeAddConstDocumentation(StringBuilder sb, ConstantInfo constant)
    {
        string? fieldDescription = null;
        apiDetails?.Fields.TryGetValue(constant.Name, out fieldDescription);

        sb.AppendLine("    /**");

        if (fieldDescription != null)
        {
            sb.AppendLine("     * " + fieldDescription.Replace("\n", "\n * "));
        }

        sb.AppendLine($"     * @type {{Integer ({constant.TypeCode})}}");
        sb.AppendLine("     */");
    }
}