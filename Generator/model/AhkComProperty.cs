
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
public record struct AhkComProperty(AhkType Interface, string Name, AhkMethod? Getter, AhkMethod? Setter)
{
    public void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    {Name} {{");

        if (Getter is not null)
            sb.AppendLine($"        get => this.{Getter.GetDeduplicatedName()}()");

        if (Setter is not null)
            sb.AppendLine($"        set => this.{Setter.GetDeduplicatedName()}(value)");

        sb.AppendLine("    }");
    }

    public void MaybeAppendDocumentation(StringBuilder sb)
    {
        // Doesn't seem like ApiDocs have anything for properties or getters / setters
        // Keeping this here in case that changes in the future
        sb.AppendLine("    /**");

        if(Interface.apiDetails != null)
        {
            ApiDetails apiDetails = Interface.apiDetails;
            if(apiDetails.Fields.TryGetValue(Name, out string? fieldDetails))
            {
                sb.AppendLine($"     * {AhkType.EscapeDocs(fieldDetails, "     ")}");
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