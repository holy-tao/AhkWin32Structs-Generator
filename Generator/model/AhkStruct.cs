using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

[Flags]
public enum MemberFlags
{
    None = 0,
    Deprecated = 1,
    Reserved = 2,
    Alignment = 3,
    Union = 4,
    EmbeddedAnonymous = 5
};

public class AhkStruct : AhkType
{
    public int Size { get; private set; }

    public int PackingSize { get; private set; }

    public bool IsUnion { get; private set; }

    internal IEnumerable<Member> Members { get; private set; }

    private IEnumerable<AhkStruct> NestedTypes;

    public AhkStruct(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        // Union and embedded anonymous struct types don't get tail padding
        IsUnion = Name.EndsWith("_e__Union");
        bool align = !(IsUnion || Name.EndsWith("_e__Struct"));
        
        TypeLayout layout = typeDef.GetLayout();
        PackingSize = layout.PackingSize != 0 ? layout.PackingSize : 8;

        // Size tracks our current offset
        Size = 0;
        List<Member> memberList = new List<Member>();

        int offset = 0, maxAlignment = 1;

        foreach (FieldDefinitionHandle hField in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(hField);
            Member newMember = new(mr, fieldDef, apiDetails?.Fields, apiDocs, offset);

            memberList.Add(newMember);

            int logicalFieldSize = newMember.fieldInfo.Kind switch
            {
                SimpleFieldKind.Array => newMember.fieldInfo.ArrayType?.Width ?? throw new NullReferenceException(),
                SimpleFieldKind.String => 2,
                _ => newMember.Size
            };

            int alignment = Math.Min(logicalFieldSize, PackingSize);
            maxAlignment = Math.Max(maxAlignment, alignment);
            int padding = (alignment - (offset % alignment)) % alignment;

            offset += padding;
            newMember.offset = IsUnion? 0 : offset;
            offset += newMember.Size;
        }

        Size = offset;
        if (IsUnion)
        {
            Size = memberList.Max(Comparer<Member>.Create((m1, m2) => m2.Size - m1.Size))?.Size ??
                throw new NullReferenceException("Union type has no members");
        }

        PackingSize = Math.Min(PackingSize, maxAlignment);
        int tailPadding = (maxAlignment - (offset % maxAlignment)) % maxAlignment;
        Size += tailPadding;

        Members = memberList;
        NestedTypes = typeDef.GetNestedTypes().Select(handle => new AhkStruct(mr, mr.GetTypeDefinition(handle), apiDocs));
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

    public override void ToAhk(StringBuilder sb) => ToAhk(sb, true, []);

    internal void ToAhk(StringBuilder sb, bool headers, List<Member> emittedMembers)
    {
        if (headers)
            HeadersToAhk(sb);

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends Win32Struct");
        sb.AppendLine("{");
        sb.AppendLine($"    static sizeof => {Size}");
        sb.AppendLine();
        sb.AppendLine($"    static packingSize => {PackingSize}");

        BodyToAhk(sb, 0, emittedMembers);

        sb.AppendLine("}");
    }

    private void HeadersToAhk(StringBuilder sb)
    {
        // Path to Win32Struct.ahk, expecting it to be in the root of wherever we're making this class
        string pathToBase = Namespace.Split(".")
            .Select(val => $"..{Path.DirectorySeparatorChar}")
            .Aggregate((agg, cur) => agg + cur);

        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine($"#Include {pathToBase}Win32Struct.ahk");

        // Generate #Include statements for embedded structs
        var importedTypes = new List<string>();

        foreach (Member m in Members.Where((m) => m.embeddedStruct != null))
        {
            if (importedTypes.Contains(m.fieldInfo.TypeName) || m.fieldInfo.Kind == SimpleFieldKind.COM)
                continue;

            if (m.flags.HasFlag(MemberFlags.Union) || m.flags.HasFlag(MemberFlags.EmbeddedAnonymous))
                continue;

            string sbPath = RelativePathBetweenNamespaces(Namespace, m.embeddedStruct?.Namespace);
            sb.AppendLine($"#Include {sbPath}{m.fieldInfo.TypeName}.ahk");
            importedTypes.Add(m.fieldInfo.TypeName);
        }

        sb.AppendLine();
    }

    internal void BodyToAhk(StringBuilder sb, int embeddingOfset, List<Member> emittedMembers)
    {
        var mLogEqComparer = EqualityComparer<Member>.Create((left, right) => left?.Name == right?.Name && left?.offset == right?.offset);
        var mNameComarer = EqualityComparer<Member>.Create((left, right) => left?.Name.Equals(right?.Name, StringComparison.CurrentCultureIgnoreCase) ?? false);

        foreach (Member m in Members)
        {
            if (m.flags.HasFlag(MemberFlags.Reserved) || m.flags.HasFlag(MemberFlags.Alignment))
                continue;

            if (m.flags.HasFlag(MemberFlags.Union) || m.flags.HasFlag(MemberFlags.EmbeddedAnonymous))
            {
                AhkStruct nested = NestedTypes.FirstOrDefault(s => s.Name == m.fieldInfo.TypeName) ??
                    throw new NullReferenceException($"{Name} has no nested type named {m.fieldInfo.TypeName}");

                nested.BodyToAhk(sb, m.offset + embeddingOfset, emittedMembers);
                continue;
            }

            // Skip duplicate members - this is mostly necessary for processing unions
            if (emittedMembers.Contains(m, mLogEqComparer))
                continue;

            int suffix = 0;
            while (emittedMembers.Contains(m, mNameComarer))
            {
                m.Name += ++suffix;
            }

            sb.AppendLine();
            m.ToAhk(sb, embeddingOfset);
            emittedMembers.Add(m);
        }

        // Check for [StructSizeField("<FIELDNAME>")] and generate a __New method if there is one
        // This seems to only pick up cbSize members. But e.g. TTVALIDATIONTESTSPARAMS.ulStructSize should also have this
        // TODO open an issue
        CustomAttribute? sizeFieldAttr = CustomAttributeDecoder.GetAttribute(mr, typeDef, "StructSizeFieldAttribute");
        if (sizeFieldAttr.HasValue)
            GenerateAhkNew(sb, sizeFieldAttr.Value);
    }

    private void GenerateAhkNew(StringBuilder sb, CustomAttribute sizeFieldAttr)
    {
        CustomAttributeValue<string> decoded = sizeFieldAttr.DecodeValue(new CaTypeProvider());
        var arg = decoded.FixedArguments[0];

        sb.AppendLine();
        sb.AppendLine("    /**");
        sb.AppendLine($"     * Initializes the struct. `{arg.Value}` must always contain the size of the struct.");
        sb.AppendLine($"     * @param {{Integer}} ptr The location at which to create the struct, or 0 to create a new `Buffer`");
        sb.AppendLine("     */");
        sb.AppendLine("    __New(ptr := 0){");
        sb.AppendLine("        super.__New(ptr)");
        sb.AppendLine($"        this.{arg.Value} := {Size}");
        sb.AppendLine("    }");
    }

    internal class Member
    {
        internal readonly MetadataReader mr;
        internal readonly FieldDefinition def;

        internal readonly AhkStruct? embeddedStruct;

        internal readonly string? apiDetails;
        internal readonly Dictionary<string, ApiDetails> apiDocs;

        internal readonly MemberFlags flags = MemberFlags.None;

        internal int offset;

        internal string Name;

        internal FieldInfo fieldInfo { get; private set; }

        internal int Size { get; private set; }

        internal Member(MetadataReader mr, FieldDefinition fieldDef, Dictionary<string, string>? apiFields,
            Dictionary<string, ApiDetails> apiDocs, int offset = 0)
        {
            this.mr = mr;
            this.offset = offset;
            this.apiDocs = apiDocs;

            def = fieldDef;
            Name = mr.GetString(def.Name);

            fieldInfo = FieldSignatureDecoder.DecodeFieldType(mr, fieldDef);
            flags = GetFlags();

            if (fieldInfo.Kind == SimpleFieldKind.Struct)
            {
                TypeDefinition fieldTypeDef = fieldInfo.TypeDef ??
                    throw new NullReferenceException($"Null TypeDef for Class or Struct field {Name}");
                embeddedStruct = new(mr, fieldTypeDef, apiDocs);
                Size = embeddedStruct.Size;
            }
            else if (fieldInfo.Kind == SimpleFieldKind.Array)
            {
                FieldInfo arrayElementType = fieldInfo.ArrayType ??
                    throw new NullReferenceException($"Null array element for Array field {Name}");
                Size = fieldInfo.Length * arrayElementType.Width;

                if (arrayElementType.TypeDef != null)
                    embeddedStruct = new(mr, (TypeDefinition)arrayElementType.TypeDef, apiDocs);
            }
            else
            {
                Size = fieldInfo.Width;
            }

            apiFields?.TryGetValue(Name, out apiDetails);
        }

        private MemberFlags GetFlags()
        {
            MemberFlags flags = MemberFlags.None;

            var attrs = CustomAttributeDecoder.GetAllNames(mr, def);
            if (attrs.Contains("ObsoleteAttribute"))
                flags |= MemberFlags.Deprecated;

            if (attrs.Contains("ReservedAttribute"))
                flags |= MemberFlags.Reserved;

            if (Name.StartsWith("___MISSING_ALIGNMENT__"))
                flags |= MemberFlags.Alignment;

            if (fieldInfo.TypeName.EndsWith("_e__Union"))
                flags |= MemberFlags.Union;

            if (fieldInfo.TypeName.EndsWith("_e__Struct"))
                flags |= MemberFlags.EmbeddedAnonymous;

            return flags;
        }

        public void ToAhk(StringBuilder sb, int embeddingOfset = 0)
        {
            MaybeAppendDocumentation(sb);

            switch (fieldInfo.Kind)
            {
                case SimpleFieldKind.Struct:
                    ToAhkEmbeddedStruct(sb, offset + embeddingOfset);
                    break;
                case SimpleFieldKind.Array:
                    ToAhkArray(sb, offset + embeddingOfset);
                    break;
                case SimpleFieldKind.Class:
                case SimpleFieldKind.Primitive:
                case SimpleFieldKind.Pointer:
                case SimpleFieldKind.String:
                case SimpleFieldKind.COM:
                    ToAhkStructMember(sb, offset + embeddingOfset);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported type (field {Name}): {fieldInfo.Kind}");
            }
        }

        private void ToAhkEmbeddedStruct(StringBuilder sb, int offset)
        {
            if (embeddedStruct == null)
                throw new NullReferenceException($"Null embeddedStruct for struct-type field {Name}");

            sb.AppendLine($"    {Name}{{");
            sb.AppendLine("        get {");
            sb.AppendLine($"            if(!this.HasProp(\"__{Name}\"))");
            sb.AppendLine($"                this.__{Name} := {embeddedStruct.Name}(this.ptr + {offset})");
            sb.AppendLine($"            return this.__{Name}");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        private void MaybeAppendDocumentation(StringBuilder sb)
        {
            sb.AppendLine("    /**");

            if (apiDetails != null)
            {
                sb.AppendLine("     * " + apiDetails.Replace("\n", "\n     * "));
            }

            if (flags.HasFlag(MemberFlags.Deprecated))
                sb.AppendLine("     * @deprecated");

            sb.AppendLine($"     * @type {{{(fieldInfo.Kind == SimpleFieldKind.Struct ? embeddedStruct?.Name : fieldInfo.AhkType)}}}");
            sb.AppendLine("     */");
        }

        // https://www.autohotkey.com/docs/v2/lib/NumPut.htm
        // https://www.autohotkey.com/docs/v2/lib/NumGet.htm
        public void ToAhkStructMember(StringBuilder sb, int offset)
        {

            // TODO handle arrays
            if (fieldInfo.Kind == SimpleFieldKind.String)
            {
                sb.AppendLine($"    {Name} {{");
                sb.AppendLine($"        get => StrGet(this.ptr + {offset}, {fieldInfo.Length - 1}, \"UTF-16\")");
                sb.AppendLine($"        set => StrPut(value, this.ptr + {offset}, {fieldInfo.Length - 1}, \"UTF-16\")");
                sb.AppendLine($"    }}");
            }
            else
            {
                sb.AppendLine($"    {Name} {{");
                sb.AppendLine($"        get => NumGet(this, {offset}, \"{fieldInfo.DllCallType}\")");
                sb.AppendLine($"        set => NumPut(\"{fieldInfo.DllCallType}\", value, this, {offset})");
                sb.AppendLine($"    }}");
            }
        }

        private void ToAhkArray(StringBuilder sb, int offset)
        {
            FieldInfo arrTypeInfo = fieldInfo.ArrayType ?? throw new NullReferenceException($"Null ArrayType for {Name}");

            string ahkElementType = arrTypeInfo.Kind == SimpleFieldKind.Primitive ? "Primitive" : arrTypeInfo.TypeName;
            string dllCallType = arrTypeInfo.Kind == SimpleFieldKind.Primitive ? arrTypeInfo.DllCallType : "";

            sb.AppendLine($"    {Name}{{");
            sb.AppendLine("        get {");
            sb.AppendLine($"            if(!this.HasProp(\"__{Name}ProxyArray\"))");
            sb.AppendLine($"                this.__{Name}ProxyArray := Win32FixedArray(this.ptr + {offset}, {arrTypeInfo.Width}, {ahkElementType}, \"{dllCallType}\")");
            sb.AppendLine($"            return this.__{Name}ProxyArray");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            /*
            sb.AppendLine($"    {Name}[index]{{");
            sb.AppendLine("         get {");
            sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Length})");
            sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Length}\", , index)");
            sb.AppendLine($"            return NumGet(this, {offset} + (index * {arrTypeInfo.Width}), \"{arrTypeInfo.DllCallType}\")");
            sb.AppendLine("         }");
            sb.AppendLine("         set {");
            sb.AppendLine($"            if(index < 1 || index > {fieldInfo.Length})");
            sb.AppendLine($"                throw IndexError(\"Index out of range for array of fixed length {fieldInfo.Length}\", , index)");
            sb.AppendLine($"            return NumPut(\"{arrTypeInfo.DllCallType}\", value, this, {offset} + ((index - 1) * {arrTypeInfo.Width}))");
            sb.AppendLine("         }");
            sb.AppendLine("    }");
            */
        }
    }
}