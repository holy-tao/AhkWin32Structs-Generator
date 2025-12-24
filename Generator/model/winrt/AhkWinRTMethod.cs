using System.Reflection.Metadata;
using System.Text;

class AhkWinRTMethod : AhkMethod
{
    public readonly AhkWinRTClass DeclaringClass;
    public readonly TypeDefinition DeclaringInterface;
    public readonly bool IsStatic;
    public readonly bool IsConstructor;

    public string DeclaringInterfaceName => mr.GetString(DeclaringInterface.Name).Split('`').First();
    public string DeclaringInterfaceNamespace => mr.GetString(DeclaringInterface.Namespace);
    public string DeclaringInterfaceFqn => $"{DeclaringInterfaceNamespace}.{DeclaringInterfaceName}";

    public readonly string? OverloadName;

    private readonly AhkComMethod interfaceMethod;

    public AhkWinRTMethod(AhkWinRTClass declarer, MetadataReader mr, MethodDefinition methodDef, bool isStatic, bool isConstructor) : base(mr, methodDef)
    {
        DeclaringClass = declarer;
        DeclaringInterface = mr.GetTypeDefinition(methodDef.GetDeclaringType());
        IsStatic = isStatic;
        IsConstructor = isConstructor;

        OverloadName = GetOverloadName();
        interfaceMethod = new AhkComMethod(mr, methodDef, -1);
        
        string nameForDoc = IsConstructor ? "#ctor" : (OverloadName ?? Name);
        apiDetails = DocumentationUtils.GetApiDetails($"{DeclaringClass.Fqn}.{nameForDoc.Split('`').First()}", null);
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
        MaybeAppendDocumentation(sb);
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
        sb.AppendLine($"        return this.__{DeclaringInterfaceName}.{interfaceMethod.GetDeduplicatedName()}({argList})");
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
        sb.AppendLine($"        return {DeclaringClass.Name}.__{DeclaringInterfaceName}.{interfaceMethod.GetDeduplicatedName()}({argList})");
        sb.AppendLine("    }");
    }

    public override string GetDeduplicatedName()
    {
        string effectiveName = OverloadName ?? Name;
        int counter = (IsStatic? DeclaringClass.StaticMethods : DeclaringClass.InstanceMethods)
            .TakeWhile(m => m != this)
            .Count(m => (m.OverloadName ?? m.Name) == effectiveName);

        return counter > 0 ? effectiveName + counter : effectiveName;
    }

    private protected override AhkParameter? GetOutputParameter()
    {
        AhkParameter? outParam = null;
        IEnumerable<AhkParameter> candidateParams = parameters
            .Where(p => p.IsOutParam && !p.IsInParam)
            .Where(p => p.IsPtrToPrimitive || p.IsPtrToStruct || p.IsPtrToCom || p.IsPtrToWinRTClass || p.IsPtrToHandle());
            
        if (candidateParams.Count() == 1)
            outParam = candidateParams.Single();

        return outParam;
    }
}