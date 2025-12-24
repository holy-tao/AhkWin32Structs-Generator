
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// A COM property, represented by its getter and/or setter methods
/// </summary>
/// <param name="Name">Name of the property</param>
/// <param name="Getter">Getter method for the property, if any</param>
/// <param name="Setter">Setter method for the property, if any</param>
public record struct AhkComProperty(AhkType Interface, string Name, AhkMethod? Getter, AhkMethod? Setter, bool IsStatic = false)
{
    public void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    {(IsStatic? "static " : "")}{Name} {{");

        if (Getter is not null)
            sb.AppendLine($"        get => {(IsStatic? Interface.Name : "this")}.{Getter.GetDeduplicatedName()}()");

        if (Setter is not null)
            sb.AppendLine($"        set => {(IsStatic? Interface.Name : "this")}.{Setter.GetDeduplicatedName()}(value)");

        sb.AppendLine("    }");
    }

    public void MaybeAppendDocumentation(StringBuilder sb)
    {
        // Note: only WinRT docs have documentation for properties - in that case the key is the fqn 
        // of the class + property name. For all others we'll only add type information
        sb.AppendLine("    /**");

        ApiDetails? apiDetails = DocumentationUtils.GetApiDetails($"{Interface.Namespace}.{Interface.Name}.{Name}", null);
        if(apiDetails is not null)
        {
            sb.AppendLine($"     * {AhkType.EscapeDocs(apiDetails.Description, "    ")}");

            if(!string.IsNullOrWhiteSpace(apiDetails.Remarks))
            {
                sb.AppendLine("     * @remarks");
                sb.AppendLine($"     * {AhkType.EscapeDocs(apiDetails.Remarks, "    ")}");
            }

            if(apiDetails.HelpLink is not null)
            {
                sb.AppendLine($"     * @see {apiDetails.HelpLink}");
            }
        }

        // Type is getter's return type if it exists, otherwise setter's parameter type
        AhkParameter? param = null;
        if (Getter is not null)
        {
            param = Getter.outputParameter;
        }
        else if (Setter is not null)
        {
            param = Setter.parameters.First(p => !p.Reserved);
        }

        if(param is not null)
        {
            AhkParameter typeParam = (AhkParameter)param;
            string? actualValueName = typeParam.IsPtr ? typeParam.FieldInfo.UnderlyingType?.AhkType : typeParam.FieldInfo.AhkType;
            sb.AppendLine($"     * @type {{{actualValueName}}} ");
        }

        sb.AppendLine("     */");
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        ToAhk(sb);
        return sb.ToString();
    }
}