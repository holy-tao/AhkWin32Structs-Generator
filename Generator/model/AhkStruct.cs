using System.Text;
using System.Reflection.Metadata;

public partial class AhkStruct : AhkType
{
    public int Size { get; private set; }

    public int PackingSize { get; private set; }

    public override void ToAhk(StringBuilder sb) => ToAhk(sb, true, []);

    public bool IsUnion => flags.HasFlag(MemberFlags.Union);

    public virtual void ToAhk(StringBuilder sb, bool headers, List<AhkStructMember> emittedMembers)
    {
        HeadersToAhk(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends Win32Struct");
        sb.AppendLine("{");
        sb.AppendLine($"    static sizeof => {Size}");
        sb.AppendLine();
        sb.AppendLine($"    static packingSize => {PackingSize}");

        BodyToAhk(sb, 0, emittedMembers);

        sb.AppendLine("}");
    }

    private protected void HeadersToAhk(StringBuilder sb)
    {
        // Path to Win32Struct.ahk, expecting it to be in the root of wherever we're making this class

        sb.AppendLine("#Requires AutoHotkey v2.0.0 64-bit");
        sb.AppendLine($"#Include {GetPathToBase()}Win32Struct.ahk");

        AppendImports(sb);
    }

    protected override List<string> GetReferencedTypes()
    {
        var imports = base.GetReferencedTypes();

        IEnumerable<string> embeddedImports = GetAllNonNestedMembers()
            .Where(m => m.embeddedStruct != null)
            .Where(m =>
                m.fieldInfo.Kind != SimpleFieldKind.COM &&
                !(m.flags.HasFlag(MemberFlags.Anonymous) || (m.flags.HasFlag(MemberFlags.Union) && m.IsNested)) &&
                !(m.fieldInfo.Kind == SimpleFieldKind.Array &&
                m.fieldInfo.UnderlyingType?.Kind != SimpleFieldKind.Struct) &&
                !(m.fieldInfo.TypeDef?.IsNested ?? false)
            )
            .GroupBy(m => m.fieldInfo.TypeName) // prevent duplicates
            .Select(g => GetFqn(mr, g.First().fieldInfo.TypeDef ?? throw new NullReferenceException()));

        imports.AddRange(embeddedImports.Distinct());
        return imports;
    }

    // Get all members of the struct for which we should generate #Include statements,
    // including members of child structs and duplicate declarations
    internal IEnumerable<AhkStructMember> GetAllNonNestedMembers()
    {
        List<AhkStructMember> flatMembers = [];
        foreach (AhkStructMember m in Members)
        {
            if (m.embeddedStruct != null)
            {
                flatMembers.AddRange(m.embeddedStruct.GetAllNonNestedMembers());
            }
            
            flatMembers.Add(m);
        }

        return flatMembers;
    }

    internal void BodyToAhk(StringBuilder sb, int embeddingOfset, List<AhkStructMember> emittedMembers)
    {
        var mLogEqComparer = new AhkStructMemberEqualityComparer();
        var mNameComarer = EqualityComparer<AhkStructMember>.Create((left, right) => left?.Name.Equals(right?.Name, StringComparison.CurrentCultureIgnoreCase) ?? false);

        IEnumerable<AhkStruct> embeddedAnonymousStructs = Members
            .Where(m => m.IsNested && !m.flags.HasFlag(MemberFlags.Union) && !m.flags.HasFlag(MemberFlags.Anonymous))
            .Where(m => m.Name is not "Reserved")
            .Select(m => m.embeddedStruct ??
                throw new NullReferenceException($"{Name}.{m.Name} has no nested type information"))
            .DistinctBy(s => s.Name);

        // Define Anonymous nested types here
        foreach (AhkStruct embedded in embeddedAnonymousStructs)
        {
            sb.AppendLine();
            sb.AppendLine($"    class {embedded.Name} extends Win32Struct {{");
            sb.AppendLine($"        static sizeof => {Size}");
            sb.AppendLine($"        static packingSize => {PackingSize}");

            // TODO: This is a hack for pretty nesting, we should just take nesting level as a parameter
            StringBuilder embeddedSb = new();
            embedded.BodyToAhk(embeddedSb, 0, []);
            sb.AppendLine(embeddedSb.ToString()
                .Replace("\n", "\n    "));

            sb.AppendLine("    }");
        }

        foreach (AhkStructMember m in Members)
        {
            if (m.flags.HasFlag(MemberFlags.Reserved) || m.flags.HasFlag(MemberFlags.Alignment))
                continue;

            // Flatten unions
            if (m.IsNested && (m.flags.HasFlag(MemberFlags.Union) || m.flags.HasFlag(MemberFlags.Anonymous)))
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

        extensions?.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));

        // Check for [StructSizeField("<FIELDNAME>")] and generate a __New method if there is one
        // This seems to only pick up cbSize members. But e.g. TTVALIDATIONTESTSPARAMS.ulStructSize should also have this
        // TODO open an issue
        CAInfo sizeFieldAttr = CustomAttributes.SingleOrDefault(c => c.Name is "StructSizeFieldAttribute");
        if (sizeFieldAttr != default)
            GenerateAhkNew(sb, sizeFieldAttr.Attr);
    }

    private void GenerateAhkNew(StringBuilder sb, CustomAttributeValue<string> decoded)
    {
        var arg = decoded.FixedArguments[0];

        sb.AppendLine();
        sb.AppendLine("    __New(ptrOrObj := 0, parent := \"\"){");
        sb.AppendLine("        super.__New(ptrOrObj, parent)");
        sb.AppendLine($"        this.{arg.Value} := {Size}");
        sb.AppendLine("    }");
    }
}