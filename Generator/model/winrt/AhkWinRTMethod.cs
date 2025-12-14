using System.Reflection.Metadata;
using System.Text;

class AhkWinRTMethod : AhkMethod
{
    public readonly AhkWinRTClass DeclaringClass;
    public readonly TypeDefinition DeclaringInterface;
    public readonly bool IsStatic;

    public string DeclaringInterfaceName => mr.GetString(DeclaringInterface.Name).Split('`').First();
    public string DeclaringInterfaceNamespace => mr.GetString(DeclaringInterface.Namespace);
    public string DeclaringInterfaceFqn => $"{DeclaringInterfaceNamespace}.{DeclaringInterfaceName}";

    public readonly string? OverloadName;

    public AhkWinRTMethod(AhkWinRTClass declarer, MetadataReader mr, MethodDefinition methodDef, bool isStatic) : base(mr, methodDef)
    {
        DeclaringClass = declarer;
        DeclaringInterface = mr.GetTypeDefinition(methodDef.GetDeclaringType());
        IsStatic = isStatic;

        OverloadName = GetOverloadName();
    }

    private string? GetOverloadName()
    {
        CustomAttributeValue<string> overloadAttr = CustomAttributes
            .SingleOrDefault(c => c.Name is "OverloadAttribute").Attr;

        // https://stackoverflow.com/a/1896035
        if(!EqualityComparer<CustomAttributeValue<string>>.Default.Equals(overloadAttr, default))
        {
            return (string?)overloadAttr.FixedArguments.First().Value;
        }

        return null;
    }

    public override void ToAhk(StringBuilder sb)
    {
        // TODO produce documentation

        if(IsStatic)
        {
            ToAhkStatic(sb);
        }
        else
        {
            ToAhkInstance(sb);
        }
    }

    private void ToAhkInstance(StringBuilder sb)
    {
        string argList = BuildMethodArgumentList();
        sb.AppendLine($"    {GetDeduplicatedName()}({argList}) {{");
        sb.AppendLine($"        if (!this.HasProp(\"__{DeclaringInterfaceName}\")) {{");
        sb.AppendLine($"            if ((queryResult := this.QueryInterface({DeclaringInterfaceName}.IID, &outPtr := 0)) != 0)");
        sb.AppendLine($"                throw OSError(queryResult)");
        sb.AppendLine($"            this.__{DeclaringInterfaceName} := {DeclaringInterfaceName}(outPtr)");
        sb.AppendLine($"        }}");
        sb.AppendLine();
        sb.AppendLine($"        return this.__{DeclaringInterfaceName}.{GetDeduplicatedName()}({argList})");
        sb.AppendLine("    }");
    }

    private void ToAhkStatic(StringBuilder sb)
    {
        string argList = BuildMethodArgumentList();

        sb.AppendLine($"    static {GetDeduplicatedName()}({argList}) {{");
        sb.AppendLine($"        if (!{DeclaringClass.Name}.HasProp(\"__{DeclaringInterfaceName}\")) {{");
        sb.AppendLine($"            activatableClassId := HSTRING.Create(\"{DeclaringClass.Namespace}.{DeclaringClass.Name}\")");
        sb.AppendLine($"            factoryPtr := WinRT.RoGetActivationFactory(activatableClassId, {DeclaringInterfaceName}.IID)");
        sb.AppendLine($"            {DeclaringClass.Name}.__{DeclaringInterfaceName} := {DeclaringInterfaceName}(factoryPtr)");
        sb.AppendLine($"        }}");
        sb.AppendLine();
        sb.AppendLine($"        return {DeclaringClass.Name}.__{DeclaringInterfaceName}.{GetDeduplicatedName()}({argList})");
        sb.AppendLine("    }");
    }

    private protected override  AhkParameter? GetOutputParameter() => null;

    public override string GetDeduplicatedName()
    {
        return OverloadName ?? Name;
    }
}