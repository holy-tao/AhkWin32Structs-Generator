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

    internal void ToAhk(StringBuilder sb, bool headers, List<AhkStructMember> emittedMembers)
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

        foreach (AhkStructMember m in GetAllNonNestedMembers().Where((m) => m.embeddedStruct != null))
        {
            if (importedTypes.Contains(m.fieldInfo.TypeName) || m.fieldInfo.Kind == SimpleFieldKind.COM)
                continue;

            if (m.flags.HasFlag(MemberFlags.Anonymous) || (m.flags.HasFlag(MemberFlags.Union) && m.IsNested))
                continue;

            if (m.fieldInfo.Kind == SimpleFieldKind.Array && m.fieldInfo.UnderlyingType?.Kind != SimpleFieldKind.Struct)
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
}