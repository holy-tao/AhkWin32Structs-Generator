using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public abstract class AhkType : IAhkEmitter
{

    /// <summary>
    /// List of top-level AutoHotkey built-in class names, to prevent collisions.
    /// See <a href="https://www.autohotkey.com/docs/v2/ObjList.htm">Built-in Classes</a> in the AHK docs
    /// </summary>
    public static readonly ImmutableArray<string> BuiltinClassNames = [
        "Any", "Object", "Array", "Buffer", "ClipboardAll", "Class", "Error", "MemoryError", "OSError", "TargetError", 
        "TimeoutError", "TypeError", "UnsetError", "MemberError", "PropertyError", "MethodError", "UnsetItemError", 
        "ValueError", "IndexError", "ZeroDivisionError", "File", "Func", "BoundFunc", "Closure", "Enumerator", "Gui",
        "InputHook", "Map", "Menu", "MenuBar", "RegExMatchInfo", "Primitive", "Number", "Float", "Integer", "String",
        "VarRef", "ComValue", "ComObjArray", "ComObject", "ComValueRef"
    ];

    public readonly MetadataReader mr;
    public readonly TypeDefinition typeDef;

    public readonly ApiDetails? apiDetails;

    private protected readonly List<AhkExtension>? extensions;

    /// <summary>
    /// The original type name from metadata without conflict resolution
    /// </summary>
    public string MetadataName => mr.GetString(typeDef.Name)
        .TrimEnd("_e__Struct")
        .Split('`').First();

    public virtual string Name
    {
        get
        {
            string candidate = MetadataName;
            string resolved = TypeNameResolver.ResolveConflict(candidate, IsWinRT);

            if (candidate != resolved)
            {
                System.Diagnostics.Trace.TraceInformation(
                    $"Name conflict resolved: {Namespace}.{candidate} → {resolved}");
            }

            return resolved;
        }
    }

    public string Namespace => mr.GetString(typeDef.Namespace);

    protected readonly MemberFlags flags;

    public bool Deprecated => flags.HasFlag(MemberFlags.Deprecated);

    public bool Anonymous => flags.HasFlag(MemberFlags.Anonymous);

    public bool IsAnsi => flags.HasFlag(MemberFlags.Ansi);    //Some types have both flags!?
    public bool IsUnicode => flags.HasFlag(MemberFlags.Unicode);

    /// <summary>
    /// Indicates whether or not this type is part of the Windows Runtime
    /// </summary>
    public bool IsWinRT => typeDef.Attributes.HasFlag(TypeAttributes.WindowsRuntime);

    public readonly List<CAInfo> CustomAttributes;

    public AhkType(MetadataReader mr, TypeDefinition typeDef)
    {
        this.mr = mr;
        this.typeDef = typeDef;

        CustomAttributes = CustomAttributeDecoder.DecodeAll(mr, typeDef);

        flags = GetFlags();

        apiDetails = DocumentationUtils.GetApiDetails(mr, typeDef);
        Program.Extensions.TryGetValue(GetFqn(mr,typeDef).Split('`').First(), out extensions);
    }

    public abstract void ToAhk(StringBuilder sb);

    protected void MaybeAddTypeDocumentation(StringBuilder sb)
    {
        sb.AppendLine("/**");

        if (apiDetails != null)
        {
            sb.AppendLine(" * " + EscapeDocs(apiDetails.Description));
            if (apiDetails.Remarks != null)
            {
                sb.AppendLine(" * @remarks");
                sb.AppendLine(" * " + EscapeDocs(apiDetails.Remarks, ""));
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
        {
            string message = DocumentationUtils.GetDeprecationMessage(mr, typeDef);
            sb.AppendLine($" * @deprecated {message}");
        }
            

        sb.AppendLine(" */");
    }

    public string GetDesiredFilepath(string root)
    {
        string namespacePath = Path.Join(Namespace.Split("."));
        // Use Name property to ensure filename matches class name (with conflict resolution)
        return Path.Join(root, namespacePath, $"{Name}.ahk");
    }

    protected virtual void AppendImports(StringBuilder sb)
    {
        foreach (string import in GetReferencedTypes().Distinct())
        {
            if(import is "System.Guid")
            {
                sb.AppendLine($"#Include {GetPathToBase()}Guid.ahk");
                continue;
            }

            List<string> parts = [.. import.Split(".")];
            string importNamespace = string.Join(".", parts[0..^1]);    // All but last
            string rawImportName = parts.Last().Split('`').First();

            // Apply same conflict resolution as Name property to ensure #Include matches actual filename
            bool isImportWinRT = TypeNameResolver.IsWinRTNamespace(importNamespace);
            string importName = TypeNameResolver.ResolveConflict(rawImportName, isImportWinRT);

            string sbPath = AhkStruct.RelativePathBetweenNamespaces(Namespace, importNamespace);
            sb.AppendLine($"#Include {sbPath}{importName}.ahk");
        }
    }

    public virtual List<string> GetReferencedTypes()
    {
        List<string> imports = [];

        extensions?.ForEach(e => imports.AddRange(e.Requirements));

        return imports;
    }

    protected string GetExtensionCodeTokenized(AhkExtension ex)
    {
        return ex.GetCodeIndented(1)
            .Replace("$Class", Name);
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

    protected string GetPathToBase() => GetPathToBase(Namespace);

    public static string GetPathToBase(string ns)
    {
        return ns.Split(".")
            .Select(val => $"..{Path.DirectorySeparatorChar}")
            .Aggregate((agg, cur) => agg + cur);
    }

    public static string? EscapeDocs(string? docString, string? indent = " ")
    {
        // Remove comments from documentation and add asterisks to newlines
        return docString?
            .Replace("/*", "//")
            .Replace("*/", "")
            .Replace("\n", $"\n{indent} * ");
    }

    public static string GetFqn(MetadataReader reader, TypeDefinition td)
    {
        return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
    }
    
    public static string NamespaceToPath(string ns)
    {
        // Replace dots with directory separators
        return ns.Replace('.', Path.DirectorySeparatorChar);
    }

    public static string RelativePathBetweenNamespaces(string fromNs, string? toNs)
    {
        if (string.IsNullOrEmpty(toNs))
        {
            // Assume current directory
            return $".{Path.DirectorySeparatorChar}";
        }

        string fromDir = NamespaceToPath(fromNs);
        string toDir = NamespaceToPath(toNs);

        string relativePath = Path.GetRelativePath(fromDir, toDir);
        if (!relativePath.EndsWith(Path.DirectorySeparatorChar))
            relativePath += Path.DirectorySeparatorChar;
        return relativePath;
    }
}