using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.Win32Docs;

public partial class AhkStruct : AhkType
{
    private static readonly Dictionary<string, AhkStruct> LoadedStructs = [];

    public static AhkStruct Get(MetadataReader reader, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs)
    {
        return new AhkStruct(reader, typeDef, apiDocs);
        /*
        string fqn = reader.GetString(typeDef.Namespace) + "." + reader.GetString(typeDef.Name);
        AhkStruct? ahkStruct = Get(fqn);
        if (ahkStruct == null)
        {
            ahkStruct = 
            //if(!ahkStruct.Anonymous)
                //LoadedStructs.Add(fqn, ahkStruct);
        }

        return ahkStruct;
        */
    }

    public static AhkStruct? Get(string fqn)
    {
        LoadedStructs.TryGetValue(fqn, out AhkStruct? foundStruct);
        return foundStruct;
    }

    internal IEnumerable<AhkStructMember> Members { get; private set; }

    private readonly LayoutKind Layout;

    private AhkStruct(MetadataReader mr, TypeDefinition typeDef, Dictionary<string, ApiDetails> apiDocs) : base(mr, typeDef, apiDocs)
    {
        PackingSize = EstimatePackingSize();
        Layout = GetLayoutKind();

        // Size tracks our current offset
        Size = 0;
        List<AhkStructMember> memberList = [];

        int offset = 0, maxAlignment = 1;

        foreach (FieldDefinitionHandle hField in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mr.GetFieldDefinition(hField);
            AhkStructMember newMember = new(this, mr, fieldDef, apiDetails?.Fields, apiDocs, offset);

            memberList.Add(newMember);

            int logicalFieldSize = newMember.fieldInfo.Kind switch
            {
                SimpleFieldKind.Array => newMember.fieldInfo.UnderlyingType?.GetWidth(IsAnsi) ?? throw new NullReferenceException(),
                SimpleFieldKind.String => IsAnsi? 1 : 2,
                _ => newMember.Size
            };

            int alignment = Math.Min(logicalFieldSize, PackingSize);
            maxAlignment = Math.Max(maxAlignment, alignment);
            int padding = (alignment - (offset % alignment)) % alignment;

            offset += padding;
            newMember.offset = Layout == LayoutKind.Explicit ? fieldDef.GetOffset() : offset;
            offset += newMember.Size;
        }

        Size = offset;
        if (IsUnion)
        {
            Size = memberList.Max(Comparer<AhkStructMember>.Create((m1, m2) => m2.Size - m1.Size))?.Size ??
                throw new NullReferenceException("Union type has no members");
        }

        PackingSize = Math.Min(PackingSize, maxAlignment);
        int tailPadding = (maxAlignment - (offset % maxAlignment)) % maxAlignment;
        Size += tailPadding;

        Members = memberList;
    }

    private int EstimatePackingSize()
    {
        TypeLayout layout = typeDef.GetLayout();
        if (typeDef.Attributes.HasFlag(TypeAttributes.ExplicitLayout) && layout.PackingSize != 0)
        {
            return layout.PackingSize;
        }

        return 8;
    }

    private LayoutKind GetLayoutKind()
    {
        var attr = typeDef.Attributes & TypeAttributes.LayoutMask;
        return attr switch
        {
            TypeAttributes.SequentialLayout => LayoutKind.Sequential,
            TypeAttributes.ExplicitLayout => LayoutKind.Explicit,
            TypeAttributes.AutoLayout => LayoutKind.Auto,
            _ => throw new NotSupportedException($"Unknown Type Layout attribute: {attr}")
        };
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