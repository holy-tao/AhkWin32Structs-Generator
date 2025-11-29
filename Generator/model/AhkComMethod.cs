
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkComMethod : AhkMethod
{
    public int VTableIndex { get; private set; }

    public bool HasStringParam => parameters[1..].Any(p => p.GetTypeDefName(mr) is "BSTR");

    public bool IsSpecialName => methodDef.Attributes.HasFlag(MethodAttributes.SpecialName);

    private readonly AhkComInterface parent;

    public AhkComMethod(AhkComInterface parent, MetadataReader mr, MethodDefinition methodDef, int vTableIndex) : base(mr, methodDef)
    {
        VTableIndex = vTableIndex;
        this.parent = parent;
    }

    public override void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    {GetDeduplicatedName()}({BuildMethodArgumentList()}) {{");

        List<AhkParameter> reservedParams = [.. parameters.Where(p => p.Reserved)];
        if (reservedParams.Count > 0)
        {
            sb.Append("        static ");
            sb.Append(string.Join(", ", reservedParams.Select(p => $"{p.Name} := 0")));
            sb.Append(" ;Reserved parameters must always be NULL");

            sb.AppendLine();
            sb.AppendLine();
        }

        StringBuilder paramConversions = GetParameterConversions();
        sb.Append(paramConversions);
        if (paramConversions.Length > 0)
            sb.AppendLine();

        StringBuilder marshalCode = GetParameterMarshallingCode();
        sb.Append(marshalCode);
        if (marshalCode.Length > 0)
            sb.AppendLine();

        if (SetsLastError)
        {
            sb.AppendLine($"        A_LastError := 0");
            sb.AppendLine();
        }

        AppendOutputParamMarshallingCode(sb);
        sb.AppendLine($"        {BuildDllCallCall("")}");

        AppendErrorCheck(sb);
        AppendReturnStatement(sb);
        sb.AppendLine($"    }}");
    }

    private protected override StringBuilder GetParameterConversions()
    {
        StringBuilder conversions = new();

        foreach (AhkParameter param in parameters[1..])
        {
            string? typeName = param.GetTypeDefName(mr);

            if (typeName is "BSTR")
            {
                conversions.AppendLine($"        {param.Name} := {param.Name} is String ? BSTR.Alloc({param.Name}).Value : {param.Name}");
            }
            else if (typeName is "PSTR" or "PWSTR")
            {
                conversions.AppendLine($"        {param.Name} := {param.Name} is String ? StrPtr({param.Name}) : {param.Name}");
            }
            else if (param.IsHandle(mr))
            {
                conversions.AppendLine($"        {param.Name} := {param.Name} is Win32Handle ? NumGet({param.Name}, \"ptr\") : {param.Name}");
            }
        }

        return conversions;
    }

    private protected override string BuildDllCallCall(string entry)
    {
        StringBuilder sb = new();

        // ComCall can check HRESULTs for us
        if (FuncHasReturnValue)
            sb.Append("result := ");

        // https://www.autohotkey.com/docs/v2/lib/ComCall.htm
        sb.Append($"ComCall({VTableIndex}, this");

        if (parameters.Count > 1)
        {
            sb.Append(", ");
            sb.Append(BuildDllCallArgumentList());
        }

        // Calling convention / return type
        if (CallingConvention == MethodImportAttributes.CallingConventionCDecl || FuncHasReturnValue)
        {
            sb.Append(", \"");
            if (CallingConvention == MethodImportAttributes.CallingConventionCDecl)
            {
                sb.Append("CDecl ");
            }

            if (FuncHasReturnValue)
                sb.Append(parameters[0].FieldInfo.GetDllCallType(false));

            sb.Append('"');
        }

        return sb.Append(')').ToString();
    }

    /// <summary>
    /// Some interfaces have overloaded methods. AHK doesn't support this, class members need to
    /// have unique names. So we append a counter to overloads for uniqueness
    /// </summary>
    /// <returns></returns>
    public string GetDeduplicatedName()
    {
        int counter = parent.Methods
            .Where(m => (m.Name == Name) && (m.VTableIndex < VTableIndex))
            .Count();

        return counter > 0 ? Name + counter : Name;
    }

    private protected override AhkParameter? GetOutputParameter()
    {
        if (!parameters[0].IsHRESULT || CanReturnErrorsAsSuccess)
        {
            return null;
        }

        AhkParameter outParam = default;
        outParam = parameters.SingleOrDefault(p => p.IsReturnValue);

        if(outParam == default)
        {
            IEnumerable<AhkParameter> candidateParams = parameters
                .Where(p => p.IsOutParam && !p.IsInParam)
                .Where(p => p.IsPtrToPrimitive || p.IsPtrToStruct || p.IsPtrToCom || p.IsPtrToHandle(mr));
            if (candidateParams.Count() == 1)
            {
                outParam = candidateParams.Single();
            }
        }

        return (outParam == default) ? null : outParam;
    }
}