using System.Reflection.Metadata;
using Microsoft.Windows.SDK.Win32Docs;

public partial class AhkStruct : AhkType
{
    private static readonly Dictionary<string, AhkStruct> LoadedStructs = [];

    public static AhkStruct Get(MetadataReader reader, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        string fqn = reader.GetString(typeDef.Namespace) + "." + reader.GetString(typeDef.Name);
        AhkStruct? ahkStruct = Get(fqn);
        if (ahkStruct == null)
        {
            ahkStruct = new AhkStruct(reader, typeDef, apiDocs);
            LoadedStructs.Add(fqn, ahkStruct);
        }

        return (AhkStruct)ahkStruct;
    }

    public static AhkStruct? Get(string fqn)
    {
        LoadedStructs.TryGetValue(fqn, out AhkStruct? foundStruct);
        return foundStruct;
    }

    internal IEnumerable<Member> Members { get; private set; }

    private IEnumerable<AhkStruct> NestedTypes;

    private MemberFlags flags;

    private AhkStruct(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        // Union and embedded anonymous struct types don't get tail padding
        bool align = !(IsUnion || Anonymous);

        TypeLayout layout = typeDef.GetLayout();
        int defaultPackingSize = IsUnicode ? 8 : 4;
        PackingSize = layout.PackingSize != 0 ? layout.PackingSize : defaultPackingSize;

        // Size tracks our current offset
        Size = 0;
        List<Member> memberList = [];

        int offset = 0, maxAlignment = 1;

        foreach (FieldDefinitionHandle hField in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(hField);
            Member newMember = new(this, mr, fieldDef, apiDetails?.Fields, apiDocs, offset);

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
            newMember.offset = IsUnion ? 0 : offset;
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
}