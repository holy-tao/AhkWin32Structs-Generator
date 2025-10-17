
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkHandle : AhkStruct
{
    private readonly string? FreeFunc;

    private readonly List<long> InvalidValues;

    public AhkHandle(MetadataReader reader, TypeDefinition typeDef) : base(reader, typeDef)
    {
        CAInfo? RAIIFree = MaybeGetCustomAttribute("RAIIFreeAttribute");
        FreeFunc = (string?)RAIIFree?.Attr.FixedArguments[0].Value;

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
        if (FreeFunc != null)
            sb.AppendLine($"#Include .\\Apis.ahk");
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
    
    private void AppendDestructor(StringBuilder sb)
    {
        string apisCls = Namespace.Split(".").Last();

        sb.AppendLine();
        sb.AppendLine("    Free(){");
        sb.AppendLine($"        {apisCls}.{FreeFunc}(this.{Members.First().Name})");
        sb.AppendLine($"        this.{Members.First().Name} := {InvalidValues.FirstOrDefault()}");
        sb.AppendLine("    }");
    }
}