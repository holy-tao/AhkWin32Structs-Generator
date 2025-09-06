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

    protected readonly MemberFlags flags;

    public bool Deprecated => flags.HasFlag(MemberFlags.Deprecated);

    public bool Anonymous => flags.HasFlag(MemberFlags.Anonymous);

    public bool IsAnsi => flags.HasFlag(MemberFlags.Ansi);
    public bool IsUnicode => flags.HasFlag(MemberFlags.Unicode);

    public AhkType(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        this.mr = mr;
        this.typeDef = typeDef;
        this.apiDocs = apiDocs;

        flags = GetFlags();

        apiDocs.TryGetValue(Name, out apiDetails);
    }

    public abstract void ToAhk(StringBuilder sb);

    private protected void MaybeAddTypeDocumentation(StringBuilder sb)
    {
        sb.AppendLine("/**");

        if (apiDetails != null)
        {
            sb.AppendLine(" * " + EscapeDocs(apiDetails.Description));
            if (apiDetails.Remarks != null)
            {
                sb.AppendLine(" * @remarks");
                sb.AppendLine(" * " + EscapeDocs(apiDetails.Remarks));
            }
            sb.AppendLine($" * @see {apiDetails.HelpLink}");
        }

        sb.AppendLine($" * @namespace {Namespace}");
        sb.AppendLine($" * @version {mr.MetadataVersion}");

        if (Deprecated)
            sb.AppendLine(" * @deprecated");

        sb.AppendLine(" */");
    }

    protected static string? EscapeDocs(string? docString, string? indent = " ")
    {
        // Remove comments from documentation and add asterisks to newlines
        return docString?
            .Replace("/*", "//")
            .Replace("*/", "")
            .Replace("\n", $"\n{indent} * ");
    }

    public string GetDesiredFilepath(string root)
    {
        string namespacePath = Path.Join(Namespace.Split("."));
        return Path.Join(root, namespacePath, $"{Name}.ahk");
    }

    protected virtual MemberFlags GetFlags()
    {
        MemberFlags flags = MemberFlags.None;

        foreach (string attrName in CustomAttributeDecoder.GetAllNames(mr, typeDef))
        {
            flags |= attrName switch
            {
                "ObsoleteAttribute" => MemberFlags.Deprecated,
                "ReservedAttribute" => MemberFlags.Reserved,
                "AnsiAttribute" => MemberFlags.Ansi,
                "UnicodeAttribute" => MemberFlags.Unicode,
                _ => 0
            };
        }

        string typeName = mr.GetString(typeDef.Name);

        if (typeName.EndsWith("_e__Union"))
            flags |= MemberFlags.Union;

        if (typeName.EndsWith("_e__Struct") || typeName.StartsWith("_Anonymous"))
            flags |= MemberFlags.Anonymous;

        return flags;
    }
}