using System.Text;
using Microsoft.Windows.SDK.Win32Docs;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

public partial class AhkStruct : AhkType
{
    public int Size { get; private set; }

    public int PackingSize { get; private set; }

    public override void ToAhk(StringBuilder sb) => ToAhk(sb, true, []);

    public bool IsUnion => flags.HasFlag(MemberFlags.Union);

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

    // Devices.BiometricFramework.WINBIO_ASYNC_RESULT not importing e.g. WINBIO_IDENTITY or WINBIO_PROTECTION POLICY
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

        foreach (Member m in GetAllNonNestedMembers().Where((m) => m.embeddedStruct != null))
        {
            if (importedTypes.Contains(m.fieldInfo.TypeName) || m.fieldInfo.Kind == SimpleFieldKind.COM)
                continue;

            if (m.flags.HasFlag(MemberFlags.Anonymous) || (m.flags.HasFlag(MemberFlags.Union) && m.IsNested))
                continue;

            if (m.fieldInfo.Kind == SimpleFieldKind.Array && m.fieldInfo.ArrayType?.Kind != SimpleFieldKind.Struct)
                continue;

            if (m.fieldInfo.TypeDef?.IsNested ?? false)
                continue;

            string sbPath = RelativePathBetweenNamespaces(Namespace, m.embeddedStruct?.Namespace);
            sb.AppendLine($"#Include {sbPath}{m.fieldInfo.TypeName}.ahk");
            importedTypes.Add(m.fieldInfo.TypeName);
        }

        sb.AppendLine();
    }

    // Get all members of the struct for which we should generate #Include statements,
    // including members of child structs and duplicate declarations
    internal IEnumerable<Member> GetAllNonNestedMembers()
    {
        List<Member> flatMembers = [];
        foreach (Member m in Members)
        {
            if (m.embeddedStruct != null)
            {
                flatMembers.AddRange(m.embeddedStruct.GetAllNonNestedMembers());
            }
            
            flatMembers.Add(m);
        }

        return flatMembers;
    }

    internal void BodyToAhk(StringBuilder sb, int embeddingOfset, List<Member> emittedMembers)
    {
        var mLogEqComparer = EqualityComparer<Member>.Create((left, right) => left?.Name == right?.Name && left?.offset == right?.offset);
        var mNameComarer = EqualityComparer<Member>.Create((left, right) => left?.Name.Equals(right?.Name, StringComparison.CurrentCultureIgnoreCase) ?? false);

        foreach (Member m in Members)
        {
            if (m.flags.HasFlag(MemberFlags.Reserved) || m.flags.HasFlag(MemberFlags.Alignment))
                continue;

            if (m.IsNested)
            {
                if(m.embeddedStruct == null)
                    throw new NullReferenceException($"{Name}.{m.Name} has no nested type information");

                m.embeddedStruct.BodyToAhk(sb, m.offset + embeddingOfset, emittedMembers);
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

        internal readonly AhkStruct parent;

        internal readonly string? apiDetails;
        internal readonly Dictionary<string, ApiDetails> apiDocs;

        internal readonly MemberFlags flags = MemberFlags.None;

        internal int offset;

        internal string Name;

        internal FieldInfo fieldInfo { get; private set; }

        internal int Size { get; private set; }

        // flags.HasFlag(MemberFlags.Union) || flags.HasFlag(MemberFlags.Anonymous) || 
        internal bool IsNested => embeddedStruct?.typeDef.IsNested ?? false;

        internal Member(AhkStruct parent, MetadataReader mr, FieldDefinition fieldDef, Dictionary<string, string>? apiFields,
            Dictionary<string, ApiDetails> apiDocs, int offset = 0)
        {
            this.parent = parent;
            this.mr = mr;
            this.offset = offset;
            this.apiDocs = apiDocs;

            def = fieldDef;
            Name = mr.GetString(def.Name);

            fieldInfo = FieldSignatureDecoder.DecodeFieldType(mr, fieldDef);

            if (fieldInfo.Kind == SimpleFieldKind.Struct)
            {
                TypeDefinition fieldTypeDef = fieldInfo.TypeDef ??
                    throw new NullReferenceException($"Null TypeDef for Class or Struct field {Name}");
                embeddedStruct = AhkStruct.Get(mr, fieldTypeDef, apiDocs);
                Size = embeddedStruct.Size;
            }
            else if (fieldInfo.Kind == SimpleFieldKind.Array)
            {
                FieldInfo arrayElementType = fieldInfo.ArrayType ??
                    throw new NullReferenceException($"Null array element for Array field {Name}");
                Size = fieldInfo.Length * arrayElementType.GetWidth(parent.IsAnsi);

                if (arrayElementType.TypeDef != null)
                    embeddedStruct = AhkStruct.Get(mr, (TypeDefinition)arrayElementType.TypeDef, apiDocs);
            }
            else
            {
                Size = fieldInfo.GetWidth(parent.IsAnsi);
            }

            flags = GetFlags();
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

            if (fieldInfo.TypeName.EndsWith("_e__Union") || (embeddedStruct?.IsUnion ?? false))
                flags |= MemberFlags.Union;

            if (fieldInfo.TypeName.EndsWith("_e__Struct") || fieldInfo.TypeName.StartsWith("_Anonymous") || (embeddedStruct?.Anonymous ?? false))
                flags |= MemberFlags.Anonymous;

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
                sb.AppendLine("     * " + EscapeDocs(apiDetails, "    "));
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
                string encoding = parent.IsAnsi? "UTF-8" : "UTF-16";

                sb.AppendLine($"    {Name} {{");
                sb.AppendLine($"        get => StrGet(this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
                sb.AppendLine($"        set => StrPut(value, this.ptr + {offset}, {fieldInfo.Length - 1}, \"{encoding}\")");
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

            string ahkElementType = arrTypeInfo.Kind switch
            {
                SimpleFieldKind.Primitive or SimpleFieldKind.Pointer => "Primitive",
                _ => arrTypeInfo.TypeName
            };
            string dllCallType= arrTypeInfo.Kind switch
            {
                SimpleFieldKind.Primitive => arrTypeInfo.DllCallType,
                SimpleFieldKind.Pointer => "ptr",
                _ => ""
            };

            sb.AppendLine($"    {Name}{{");
            sb.AppendLine("        get {");
            sb.AppendLine($"            if(!this.HasProp(\"__{Name}ProxyArray\"))");
            sb.AppendLine($"                this.__{Name}ProxyArray := Win32FixedArray(this.ptr + {offset}, {arrTypeInfo.GetWidth(parent.IsAnsi)}, {ahkElementType}, \"{dllCallType}\")");
            sb.AppendLine($"            return this.__{Name}ProxyArray");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }
}