using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public abstract class AhkType : IAhkEmitter
{
    // List of globally-defined names in AutoHotkey that we must check for conflics. Most of these words
    // are also reserved in other languages so it's a non-issue, but at least string has a conflict with a
    // kernel struct
    private static readonly List<string> globalReservedNames = ["string", "number", "float", "integer"];

    private protected readonly MetadataReader mr;
    public readonly TypeDefinition typeDef;

    private protected readonly ApiDetails? apiDetails;

    public string Name
    {
        get
        {
            string candidate = mr.GetString(typeDef.Name).TrimEnd("_e__Struct");
            if (globalReservedNames.Contains(candidate, StringComparer.CurrentCultureIgnoreCase))
            {
                candidate = "Win32" + candidate;
            }

            return candidate;
        }
    }

    public string Namespace => mr.GetString(typeDef.Namespace);

    protected readonly MemberFlags flags;

    public bool Deprecated => flags.HasFlag(MemberFlags.Deprecated);

    public bool Anonymous => flags.HasFlag(MemberFlags.Anonymous);

    public bool IsAnsi => flags.HasFlag(MemberFlags.Ansi);    //Some types have both flags!?
    public bool IsUnicode => flags.HasFlag(MemberFlags.Unicode);

    public readonly List<CAInfo> CustomAttributes;

    public AhkType(MetadataReader mr, TypeDefinition typeDef)
    {
        this.mr = mr;
        this.typeDef = typeDef;

        CustomAttributes = CustomAttributeDecoder.DecodeAll(mr, typeDef);

        flags = GetFlags();

        Program.ApiDocs.TryGetValue(Name, out apiDetails);
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

        if (IsAnsi)
            sb.AppendLine(" * @charset ANSI");
        if(IsUnicode)
            sb.AppendLine(" * @charset Unicode");

        if (Deprecated)
            sb.AppendLine(" * @deprecated");

        sb.AppendLine(" */");
    }

    private protected void MaybeAddConstDocumentation(StringBuilder sb, ConstantInfo constant)
    {
        string? fieldDescription = null;
        apiDetails?.Fields.TryGetValue(constant.Name, out fieldDescription);

        sb.AppendLine("    /**");

        if (fieldDescription != null)
        {
            sb.AppendLine("     * " + fieldDescription.Replace("\n", "\n * "));
        }

        if (CustomAttributes.Any(c => c.Name is "ObsoleteAttribute"))
            sb.AppendLine($"     * @deprecated");

        sb.AppendLine($"     * @type {{{constant.Ahktype}}}");
        sb.AppendLine("     */");
    }

    public static string? EscapeDocs(string? docString, string? indent = " ")
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
        string canonicalName = mr.GetString(typeDef.Name);

        return Path.Join(root, namespacePath, $"{canonicalName}.ahk");
    }

    protected virtual MemberFlags GetFlags()
    {
        MemberFlags flags = MemberFlags.None;

        foreach (CAInfo attr in CustomAttributes)
        {
            flags |= attr.Name switch
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

    protected string GetPathToBase()
    {
        return Namespace.Split(".")
            .Select(val => $"..{Path.DirectorySeparatorChar}")
            .Aggregate((agg, cur) => agg + cur);
    }
}