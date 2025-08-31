using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public abstract class AhkType : IAhkEmitter
{
    private protected readonly MetadataReader mr;
    private protected readonly TypeDefinition typeDef;
    private protected readonly Dictionary<string, ApiDetails> apiDocs;

    private protected readonly ApiDetails? apiDetails;

    public string Name => mr.GetString(typeDef.Name);

    public string Namespace => mr.GetString(typeDef.Namespace);

    public AhkType(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        this.mr = mr;
        this.typeDef = typeDef;
        this.apiDocs = apiDocs;

        apiDocs.TryGetValue(Name, out apiDetails);
    }

    public abstract void ToAhk(StringBuilder sb);

    private protected void MaybeAddTypeDocumentation(StringBuilder sb)
    {
        if (apiDetails != null)
        {
            sb.AppendLine("/**");
            sb.AppendLine(" * " + apiDetails.Description?.Replace("\n", "\n * "));
            if (apiDetails.Remarks != null)
                sb.AppendLine(" * " + apiDetails.Remarks?.Replace("\n", "\n * "));
            sb.AppendLine($" * @See {apiDetails.HelpLink}");
            sb.AppendLine(" */");
        }
    }

    public string GetDesiredFilepath(string root)
    {
        string namespacePath = Path.Join(Namespace.Split("."));
        return Path.Join(root, namespacePath, $"{Name}.ahk");
    }
}