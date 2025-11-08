
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public class AhkEnum : AhkType
{
    private readonly List<AhkConstant> constants;

    public AhkEnum(MetadataReader mr, TypeDefinition typeDef) : base(mr, typeDef)
    {
        ApiDetails? apiDetails = DocumentationUtils.GetApiDetails(mr, typeDef);

        constants = typeDef.GetFields()
            .Select(mr.GetFieldDefinition)
            .Where(fd => !mr.StringComparer.Equals(fd.Name, "value__"))
            .Select(fd => new AhkConstant(mr, fd, apiDetails))
            .ToList();
    }

    public override void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name}{{");

        foreach (AhkConstant constant in constants)
        {
            sb.AppendLine();
            constant.ToAhk(sb);
        }

        extensions?.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));

        sb.AppendLine("}");
    }
}