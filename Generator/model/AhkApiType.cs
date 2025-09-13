
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection;

/// <summary>
/// Type for the special "Apis" type that contains functions and constants
/// </summary>
class AhkApiType : AhkType
{
    List<ConstantInfo> constants;

    public AhkApiType(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        // Constants
        constants = new List<ConstantInfo>();
        foreach (FieldDefinitionHandle fieldDefHandle in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(fieldDefHandle);
            if (ConstantDecoder.IsConstant(mr, fieldDef))
            {
                constants.Add(ConstantDecoder.DecodeConstant(mr, fieldDef));
            }
            else
            {
                /**
                Inline functions or macros. See https://github.com/microsoft/win32metadata/issues/436
                Parsing is going to be hell in most cases
                
                E.g. this:
                    [Constant("{497408003, 54418, 20189, 140, 35, 224, 192, 255, 238, 127, 14}, 0")]
                    public static PROPERTYKEY PKEY_AudioEndpoint_FormFactor;
                Means to create a PROPERTYKEY struct with a GUID and an integer value. But 
                */
                //Console.WriteLine(mr.GetString(fieldDef.Name));
            }
        }

        // TODO methods

    }

    public override void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {GetName()} {{");
        sb.AppendLine();

        AppendConstants(sb);

        sb.AppendLine("}");
    }

    private void AppendConstants(StringBuilder sb)
    {
        sb.Append(";@region Constants");

        foreach (ConstantInfo constant in constants)
        {
            sb.AppendLine();
            MaybeAddConstDocumentation(sb, constant);
            sb.AppendLine($"    static {constant.Name} => {constant.ValueAsAhkLiteral}");
        }

        sb.AppendLine(";@endregion Constants");
    }

    private string GetName()
    {
        // We don't want the name to just be "Apis"
        return Namespace.Split(".").Last();
    }
}