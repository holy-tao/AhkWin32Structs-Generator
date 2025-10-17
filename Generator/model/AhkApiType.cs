
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection;

/// <summary>
/// Type for the special "Apis" type that contains functions and constants
/// </summary>
class AhkApiType : AhkType
{
    List<ConstantInfo> constants = [];
    List<AhkMethod> methods = [];

    public AhkApiType(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        // Constants
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

        methods = typeDef.GetMethods()
            .Select(handle => new AhkMethod(mr, mr.GetMethodDefinition(handle)))
            .DistinctBy(method => method.Name)
            .ToList();
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {GetName()} {{");
        sb.AppendLine();

        AppendConstants(sb);
        sb.AppendLine();
        AppendMethods(sb);

        sb.AppendLine("}");
    }

    private void HeadersToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.Append($"#Include {GetPathToBase()}Win32Handle.ahk");
        sb.AppendLine();

        List<TypeDefinition> imports = [];
        methods.ForEach(m => imports.AddRange(m.GetReferencedTypes()));
        imports = imports.DistinctBy(im => AhkStruct.GetFqn(mr, im)).ToList();

        foreach (TypeDefinition import in imports)
        {
            string sbPath = AhkStruct.RelativePathBetweenNamespaces(Namespace, mr.GetString(import.Namespace));
            sb.AppendLine($"#Include {sbPath}{mr.GetString(import.Name)}.ahk");
        }
    }

    private void AppendConstants(StringBuilder sb)
    {
        sb.AppendLine(";@region Constants");

        foreach (ConstantInfo constant in constants)
        {
            sb.AppendLine();
            MaybeAddConstDocumentation(sb, constant);
            sb.AppendLine($"    static {constant.Name} => {AhkEscape(constant.ValueAsAhkLiteral)}");
        }

        sb.AppendLine(";@endregion Constants");
    }

    private void AppendMethods(StringBuilder sb)
    {
        sb.AppendLine(";@region Methods");

        foreach (AhkMethod method in methods)
        {
            method.ToAhk(sb);
            sb.AppendLine();
        }

        sb.AppendLine(";@endregion Methods");
    }

    private string GetName()
    {
        // We don't want the name to just be "Apis"
        return Namespace.Split(".").Last();
    }

    private static string AhkEscape(string val)
    {
        StringBuilder sb = new();

        foreach (char c in val)
        {
            if (char.IsControl(c))
            {
                sb.Append($"\\u{((int)c).ToString("x4")}");
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