using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text;
using MetadataUtils;

class AhkWinRTMethod : AhkMethod
{
    public readonly AhkWinRTClass DeclaringClass;
    public readonly TypeDefinition DeclaringInterface;
    public readonly bool IsStatic;
    public readonly bool IsConstructor;
    public readonly bool IsComposableActivator;

    /// <summary>
    /// Generic arguments for the declaring interface, if any
    /// </summary>
    public readonly ImmutableArray<FieldInfo> DeclarerGenericArgs;

    public string DeclaringInterfaceName => mr.GetString(DeclaringInterface.Name).Split('`').First();
    public string DeclaringInterfaceNamespace => mr.GetString(DeclaringInterface.Namespace);
    public string DeclaringInterfaceFqn => $"{DeclaringInterfaceNamespace}.{DeclaringInterfaceName}";

    public readonly string? OverloadName;

    private readonly AhkComMethod interfaceMethod;

    public AhkWinRTMethod(AhkWinRTClass declarer, MetadataReader mr, MethodDefinition methodDef, TypeDefinition declaringInterface, 
        bool isStatic, bool isConstructor, bool isComposableActivator, ImmutableArray<FieldInfo> declarerGenericArgs) : base(mr, methodDef)
    {
        DeclaringClass = declarer;
        DeclaringInterface = declaringInterface;
        IsStatic = isStatic;
        IsConstructor = isConstructor;
        IsComposableActivator = isComposableActivator;
        DeclarerGenericArgs = declarerGenericArgs;

        OverloadName = GetOverloadName();
        interfaceMethod = new AhkComMethod(mr, methodDef, -1);
        
        apiDetails = IsConstructor ?
            DocumentationUtils.GetApiDetails($"{DeclaringClass.Fqn}.#ctor-{parameters.Count(p => p.SequenceNumber > 0)}", null) :
            DocumentationUtils.GetApiDetails(mr, declarer.typeDef, methodDef);

        if (IsComposableActivator)
        {
            // We will add these back into the generated code with hardocoded inputs, they should be invisible
            // to consumers
            parameters.RemoveAll(p => p.Name is "baseInterface" or "innerInterface");
        }
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

    private Guid GetPiid()
    {
        
        string sigList = string.Join(",", DeclarerGenericArgs.Select(arg => arg.GetFullTypeSignature()));
        string typeKey = $"{DeclaringInterfaceFqn}`{DeclarerGenericArgs.Length}<{sigList}>";

        if (!PiidUtils.TryGetPiid(typeKey, out Guid? piid))
            throw new KeyNotFoundException(typeKey);

        Trace.TraceInformation($"Resolved generic instantiation {typeKey} to PIID {{{piid}}}");
        return (Guid)piid;
    }

    private void ToAhkInstance(StringBuilder sb)
    {
        string methodArgList = BuildMethodArgumentList();

        // TODO generics with generic args: Bind types to call method
        string declarerGenericArgs = string.Join(", ", DeclarerGenericArgs.Select(arg => arg.GetTypeAsGenericCallable()));
        if(!string.IsNullOrWhiteSpace(declarerGenericArgs))
            declarerGenericArgs += ", ";

        string iidAccessor = $"{DeclaringInterfaceName}.IID";            

        sb.AppendLine($"    {GetDeduplicatedName()}({methodArgList}) {{");
        sb.AppendLine($"        if (!this.HasProp(\"__{DeclaringInterfaceName}\")) {{");

        if(DeclarerGenericArgs.Length > 0)
        {
            sb.AppendLine($"            piid := Guid(\"{{{GetPiid()}}}\")");
            iidAccessor = "piid";
        }

        sb.AppendLine($"            if ((queryResult := this.QueryInterface({iidAccessor}, &outPtr := 0)) != 0)");
        sb.AppendLine($"                throw OSError(queryResult)");
        sb.AppendLine($"            this.__{DeclaringInterfaceName} := {DeclaringInterfaceName}({declarerGenericArgs}outPtr)");
        sb.AppendLine($"        }}");
        sb.AppendLine();
        sb.AppendLine($"        return this.__{DeclaringInterfaceName}.{interfaceMethod.GetDeduplicatedName()}({methodArgList})");
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

        if(IsComposableActivator)
        {
            argList = string.IsNullOrWhiteSpace(argList) ? 
                "0, Buffer(A_PtrSize)" :
                string.Join(", ", argList, "0", "Buffer(A_PtrSize)");
        }
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
            .Where(p => p.IsPtrToPrimitive 
                || p.IsPtrToStruct 
                || p.IsPtrToCom 
                || p.IsPtrToWinRTClass 
                || p.IsPtrToGeneric
                || p.IsPtrToHandle());
            
        if (candidateParams.Count() == 1)
            outParam = candidateParams.Single();

        return outParam;
    }
}