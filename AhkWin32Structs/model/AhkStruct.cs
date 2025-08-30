using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

public class AhkStruct : IAhkEmitter
{
    private readonly MetadataReader mr;
    private readonly TypeDefinition typeDef;
    private readonly Dictionary<string, ApiDetails> apiDocs;

    private readonly ApiDetails? apiDetails;

    public string Name => mr.GetString(typeDef.Name);

    public string Namespace => mr.GetString(typeDef.Namespace);

    public int Size { get; private set; }

    public IEnumerable<Member> members { get; private set; }

    private IEnumerable<AhkStruct> nestedTypes;

    public AhkStruct(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        this.mr = mr;

        this.typeDef = typeDef;
        this.apiDocs = apiDocs;

        apiDocs.TryGetValue(Name, out apiDetails);

        // Create members with offsets
        Size = 0;
        List<Member> memberList = new List<Member>();

        foreach (FieldDefinitionHandle hField in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(hField);
            Member newMember = new(mr, fieldDef, apiDetails?.Fields, apiDocs, Size);

            memberList.Add(newMember);

            Size += newMember.Size;
        }

        this.members = memberList;
        this.nestedTypes = new List<AhkStruct>();
    }

    private static string NamespaceToPath(string ns)
    {
        // Replace dots with directory separators
        return ns.Replace('.', Path.DirectorySeparatorChar);
    }

    private static string RelativePathBetweenNamespaces(string fromNs, string? toNs)
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

    public void ToAhk(StringBuilder sb)
    {
        // Path to Win32Struct.ahk, expecting it to be in the root of wherever we're making this class
        string pathToBase = Namespace.Split(".")
            .Select(val => $"..{Path.DirectorySeparatorChar}")
            .Aggregate((agg, cur) => agg + cur);

        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine($"#Include {pathToBase}Win32Struct.ahk");

        // Generate #Include statements for embedded structs
        var distinctMemberStructs = members.AsEnumerable().Where((m) => m.fieldInfo.Kind == SimpleFieldKind.Struct);
        var importedTypes = new List<string>();

        foreach (Member m in distinctMemberStructs)
        {
            if (importedTypes.Contains(m.fieldInfo.TypeName))
                continue;

            string sbPath = RelativePathBetweenNamespaces(Namespace, m.embeddedStruct?.Namespace);
            sb.AppendLine($"#Include {sbPath}{m.fieldInfo.TypeName}.ahk");
            importedTypes.Add(m.fieldInfo.TypeName);
        }

        sb.AppendLine();

        if (apiDetails != null)
        {
            sb.AppendLine("/**");
            sb.AppendLine(" * " + apiDetails.Description?.Replace("\n", "\n * "));
            if (apiDetails.Remarks != null)
                sb.AppendLine(" * " + apiDetails.Remarks?.Replace("\n", "\n * "));
            sb.AppendLine($" * @See {apiDetails.HelpLink}");
            sb.AppendLine(" */");
        }
        //TODO add documentation
        sb.AppendLine($"class {Name} extends Win32Struct");
        sb.AppendLine("{");
        sb.AppendLine($"    static sizeof => {Size}");

        BodyToAhk(sb, 0);

        sb.AppendLine("}");
    }

    public void BodyToAhk(StringBuilder sb, int embeddingOfset = 0)
    {
        foreach (Member m in members)
        {
            // TODO if member type is a struct or class, copy its values here.
            // This currently generates incorrect layouts in this case because we
            // assume that a struct or class must be a pointer
            sb.AppendLine();
            m.ToAhk(sb, embeddingOfset);
        }
    }

    public string ToAhk()
    {
        StringBuilder sb = new StringBuilder();
        this.ToAhk(sb);
        return sb.ToString();
    }

    public string GetDesiredFilepath(string root)
    {
        string namespacePath = Path.Join(Namespace.Split("."));
        return Path.Join(root, namespacePath, $"{Name}.ahk");
    }

    public class Member
    {
        private readonly MetadataReader mr;
        private readonly FieldDefinition def;

        public readonly AhkStruct? embeddedStruct;

        private readonly string? apiDetails;
        private readonly Dictionary<string, ApiDetails> apiDocs;

        public int offset { get; private set; }

        public string Name => mr.GetString(def.Name);

        public FieldInfo fieldInfo { get; private set; }

        public int Size { get; private set; }

        internal Member(MetadataReader mr, FieldDefinition fieldDef, Dictionary<string, string>? apiFields,
            Dictionary<string, ApiDetails> apiDocs, int offset = 0)
        {
            this.mr = mr;
            this.offset = offset;
            this.apiDocs = apiDocs;

            def = fieldDef;
            fieldInfo = FieldSignatureDecoder.DecodeFieldType(mr, fieldDef);

            if (fieldInfo.Kind == SimpleFieldKind.Struct)
            {
                TypeDefinition fieldTypeDef = fieldInfo.TypeDef ??
                    throw new NullReferenceException($"Null TypeDef for Class or Struct field {Name}");
                embeddedStruct = new(mr, fieldTypeDef, apiDocs);
                Size = embeddedStruct.Size;
            }
            else
            {
                Size = fieldInfo.Width;
            }

            apiFields?.TryGetValue(Name, out apiDetails);
        }

        public void ToAhk(StringBuilder sb, int embeddingOfset = 0)
        {
            switch (fieldInfo.Kind)
            {
                case SimpleFieldKind.Struct:
                    MaybeAppendDocumentation(sb);
                    sb.AppendLine($"    {Name} => {embeddedStruct.Name}(this.ptr + {offset})");
                    //embeddedStruct.BodyToAhk(sb, offset + embeddingOfset);
                    break;
                case SimpleFieldKind.Class:
                case SimpleFieldKind.Primitive:
                case SimpleFieldKind.Pointer:
                case SimpleFieldKind.Array:
                    ToAhkStructMember(sb, embeddingOfset);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported type (field {Name}): {fieldInfo.Kind}");
            }
        }

        private void MaybeAppendDocumentation(StringBuilder sb)
        {
            if (apiDetails != null)
            {
                sb.AppendLine("    /**");
                sb.AppendLine("     * " + apiDetails.Replace("\n", "\n     * "));
                sb.AppendLine($"     * @type {{{fieldInfo.AhkType}}}");
                sb.AppendLine("     */");
            }
        }

        // https://www.autohotkey.com/docs/v2/lib/NumPut.htm
        // https://www.autohotkey.com/docs/v2/lib/NumGet.htm
        public void ToAhkStructMember(StringBuilder sb, int embeddingOfset = 0)
        {
            int memberOffset = offset + embeddingOfset;

            MaybeAppendDocumentation(sb);

            // TODO handle arrays
            if (fieldInfo.Kind == SimpleFieldKind.Array)
            {
                Console.WriteLine("Wrote array info");
                FieldInfo arrTypeInfo = new FieldInfo(SimpleFieldKind.Primitive, fieldInfo.TypeName);

                sb.AppendLine($"    {Name}[index]{{");
                sb.AppendLine("         get {");
                sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Rank})");
                sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Rank}\", , index)");
                sb.AppendLine($"            return NumGet(this, {memberOffset} + (index * {arrTypeInfo.Width}), \"{arrTypeInfo.DllCallType}\")");
                sb.AppendLine("         }");
                sb.AppendLine("         set {");
                sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Rank})");
                sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Rank}\", , index)");
                sb.AppendLine($"            return NumPut(\"{arrTypeInfo.DllCallType}\", value, this, {memberOffset} + ((index - 1) * {arrTypeInfo.Width}))");
                sb.AppendLine("         }");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine($"    {Name} {{");
                sb.AppendLine($"        get => NumGet(this, {memberOffset}, \"{fieldInfo.DllCallType}\")");
                sb.AppendLine($"        set => NumPut(\"{fieldInfo.DllCallType}\", value, this, {memberOffset})");
                sb.AppendLine($"    }}");
            }
        }
    }
}