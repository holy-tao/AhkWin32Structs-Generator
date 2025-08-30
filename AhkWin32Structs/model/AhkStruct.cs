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
            Member newMember = new(mr, fieldDef, apiDetails?.Fields, Size);

            memberList.Add(newMember);

            Size += newMember.fieldInfo.Width;
        }

        this.members = memberList;
        this.nestedTypes = new List<AhkStruct>();
    }

    public void ToAhk(StringBuilder sb)
    {
        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
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

        foreach (Member m in members)
        {
            sb.AppendLine();
            m.ToAhk(sb);
        }
        sb.AppendLine("}");
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

        private readonly string? apiDetails;

        public int offset { get; private set; }

        public string Name => mr.GetString(def.Name);

        public FieldInfo fieldInfo { get; private set; }

        internal Member(MetadataReader mr, FieldDefinition fieldDef, Dictionary<string, string>? apiFields, int offset = 0)
        {
            this.mr = mr;
            this.offset = offset;

            def = fieldDef;
            fieldInfo = FieldSignatureDecoder.DecodeFieldType(mr, fieldDef);

            apiFields?.TryGetValue(Name, out apiDetails);
        }

        // https://www.autohotkey.com/docs/v2/lib/NumPut.htm
        // https://www.autohotkey.com/docs/v2/lib/NumGet.htm
        public void ToAhk(StringBuilder sb)
        {
            if (apiDetails != null)
            {
                sb.AppendLine("    /**");
                sb.AppendLine("     * " + apiDetails.Replace("\n", "\n     * "));
                sb.AppendLine($"     * @type {{{fieldInfo.AhkType}}}");
                sb.AppendLine("     */");
            }

            // TODO handle arrays
            if (fieldInfo.Kind == SimpleFieldKind.Array)
            {
                Console.WriteLine("Wrote array info");
                FieldInfo arrTypeInfo = new FieldInfo(SimpleFieldKind.Primitive, fieldInfo.TypeName);

                sb.AppendLine($"    {Name}[index]{{");
                sb.AppendLine( "         get {");
                sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Rank})");
                sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Rank}\", , index)");
                sb.AppendLine($"            return NumGet(this, {offset} + (index * {arrTypeInfo.Width}), \"{arrTypeInfo.DllCallType}\")");
                sb.AppendLine( "         }");
                sb.AppendLine( "         set {");
                sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Rank})");
                sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Rank}\", , index)");
                sb.AppendLine($"            return NumPut(\"{arrTypeInfo.DllCallType}\", value, this, {offset} + (index * {arrTypeInfo.Width}))");
                sb.AppendLine( "         }");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine($"    {Name} {{");
                sb.AppendLine($"        get => NumGet(this, {offset}, \"{fieldInfo.DllCallType}\")");
                sb.AppendLine($"        set => NumPut(\"{fieldInfo.DllCallType}\", value, this, {offset})");
                sb.AppendLine($"    }}");
            }
        }
    }
}