
using System.Reflection.Metadata;
using System.Text;

class AhkHandle : AhkStruct
{
    private readonly AhkMethod? FreeFunc;

    private readonly List<long> InvalidValues;

    public AhkHandle(MetadataReader reader, TypeDefinition typeDef) : base(reader, typeDef)
    {
        CAInfo? RAIIFree = MaybeGetCustomAttribute("RAIIFreeAttribute");
        string? freeFuncName = (string?)RAIIFree?.Attr.FixedArguments[0].Value;
        if(freeFuncName is not null)
        {
            AhkMethod candidate = AhkMethod.Get(reader, freeFuncName);
            if(candidate.parameters.Count == 2)
                FreeFunc = candidate;
        }

        InvalidValues = CustomAttributes
            .Where(c => c.Name == "InvalidHandleValueAttribute")
            .Select(c => (long)(c.Attr.FixedArguments[0].Value ?? throw new NullReferenceException(c.Name)))
            .ToList();
    }

    public override void ToAhk(StringBuilder sb, bool headers, List<AhkStructMember> emittedMembers)
    {
        HeadersToAhk(sb);
        sb.AppendLine($"#Include {GetPathToBase()}Win32Handle.ahk");

        // RAIIFree method is guaranteed to be in our namespace if it exists
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends Win32Handle");
        sb.AppendLine("{");
        sb.AppendLine($"    static sizeof => {Size}");
        sb.AppendLine();
        sb.AppendLine($"    static packingSize => {PackingSize}");

        sb.AppendLine();
        sb.AppendLine("    /**");
        sb.AppendLine("     * The list of values which indicate that the handle is invalid");
        sb.AppendLine("     * @type {Array<Integer>}");
        sb.AppendLine("     */");
        sb.AppendLine($"    static invalidValues => [{string.Join(", ", InvalidValues)}]");

        BodyToAhk(sb, 0, emittedMembers);

        if (FreeFunc != null)
        {
            AppendDestructor(sb);
        }

        sb.AppendLine("}");
    }

    public override List<string> GetReferencedTypes()
    {
        List<string> imports = base.GetReferencedTypes();
        
        if (FreeFunc != null)
            imports.Add(string.Join('.', Namespace, "Apis"));
            
        return imports;
    }

    private void AppendDestructor(StringBuilder sb)
    {
        string apisCls = Namespace.Split(".").Last();

        sb.AppendLine();
        sb.AppendLine("    Free(){");
        sb.AppendLine($"        {apisCls}.{FreeFunc?.Name}(this.{Members.First().Name})");
        sb.AppendLine($"        this.{Members.First().Name} := {InvalidValues.FirstOrDefault()}");
        sb.AppendLine("    }");
    }
}