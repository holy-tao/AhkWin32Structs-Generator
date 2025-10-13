
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkComMethod : AhkMethod
{
    public int VTableIndex { get; private set; }

    public AhkComMethod(MetadataReader mr, MethodDefinition methodDef, Dictionary<string, ApiDetails> apiDocs, int vTableIndex) : base(mr, methodDef, apiDocs)
    {
        VTableIndex = vTableIndex;
    }

    //TODO: handle [RetVal] parameters
    //TODO: Related to above, wrap ahk literals as ComValues where possible - https://www.autohotkey.com/docs/v2/lib/ComValue.htm
    //TODO: pass BSTR params as ptrs

    public override void ToAhk(StringBuilder sb)
    {
        MaybeAppendDocumentation(sb);
        sb.AppendLine($"    {Name}({BuildMethodArgumentList()}) {{");

        List<AhkParameter> reservedParams = [.. parameters.Where(p => p.Reserved)];
        if (reservedParams.Count > 0)
        {
            sb.Append("        static ");
            sb.Append(string.Join(", ", reservedParams.Select(p => $"{p.Name} := 0")));
            sb.Append(" ;Reserved parameters must always be NULL");

            sb.AppendLine();
            sb.AppendLine();
        }

        AppendParameterConversions(sb);

        if (SetsLastError)
        {
            sb.AppendLine($"        A_LastError := 0");
            sb.AppendLine();
        }

        AhkParameter returnParam = parameters.FirstOrDefault(p => p.IsReturnValue);
        if (returnParam != default)
        {
            sb.AppendLine($"        {returnParam.Name} := 0");
        }

        sb.AppendLine($"        {BuildDllCallCall("")}");

        if (SetsLastError)
        {
            // Inspect last error for errors
            sb.AppendLine($"        if(A_LastError)");
            sb.AppendLine($"            throw OSError()");
            sb.AppendLine();
        }

        if (HasReturnValue && ShouldThrowForReturnValue())
        {
            // The function returns an HRESULT that we must check to see if we need to throw
            sb.AppendLine($"        if(result != 0)");
            sb.AppendLine($"            throw OSError(result)");
            sb.AppendLine();
        }

        if (returnParam != default)
        {
            AppendRetValMarshalCode(sb, returnParam);
        }

        if (HasReturnValue || returnParam != default)
            sb.AppendLine($"        return {(returnParam != default ? returnParam.Name : "result")}");

        sb.AppendLine($"    }}");
    }

    private protected override string BuildDllCallArgumentList()
    {
        StringBuilder argList = new();

        // Skip param 0, which is return value
        for (int i = 1; i < parameters.Count; i++)
        {
            AhkParameter param = parameters[i];

            bool isString = param.GetTypeDefName(mr) is "PWSTR" or "PSTR" or "BSTR";

            string dllCallType = isString ? "ptr" : param.FieldInfo.GetDllCallType(false);

            argList.Append($"\"{dllCallType}\"");
            argList.Append(", ");

            // Pass return values ByRef if they're pointers to primitives
            if (param.IsReturnValue && param.IsPtrToPrimitive)
                argList.Append('&');

            argList.Append(param.Name);

            if (i < parameters.Count - 1)
                argList.Append(", ");
        }

        return argList.ToString();
    }

    private void AppendParameterConversions(StringBuilder sb)
    {
        bool addedConversions = false;
        foreach (AhkParameter param in parameters[1..])
        {
            string? typeName = param.GetTypeDefName(mr);

            if (typeName is "BSTR")
            {
                sb.AppendLine($"        {param.Name} := {param.Name} is String? ComValue(VARENUM.VT_BSTR, {param.Name}) : {param.Name}");
                addedConversions = true;
            }

            //TODO other ahk literal types that may need to be converted to variants?
        }

        if (addedConversions)
            sb.AppendLine();
    }
    
    private void AppendRetValMarshalCode(StringBuilder sb, AhkParameter returnParam)
    {
        FieldInfo underlying = returnParam.FieldInfo.UnderlyingType ?? throw new NullReferenceException();
        if (underlying.Kind == SimpleFieldKind.Primitive)
            return; // Do nothing, we pass these ByRef

        sb.Append($"        {returnParam.Name} := ");

        switch (underlying.Kind)
        {
            case SimpleFieldKind.HRESULT:
            case SimpleFieldKind.Pointer:
                // dereference pointers
                sb.AppendLine($"NumGet({returnParam.Name}, {underlying.GetDllCallType(true)})");
                return;
            case SimpleFieldKind.Struct:
                if (underlying.TypeName.StartsWith("VARIANT"))
                {
                    // Wrap in a ComValue
                    sb.AppendLine($"ComValue(ComObjType({returnParam.Name}), {returnParam.Name})");
                }
                else
                {
                    // TODO need to import these
                    sb.AppendLine($"{underlying.TypeName}({returnParam.Name})");
                }
                return;
            case SimpleFieldKind.String:
                sb.AppendLine($"ComValue(VARENUM.VT_BSTR, {returnParam.Name})");
                return;
            case SimpleFieldKind.COM:
                sb.AppendLine($"{underlying.TypeName}({returnParam.Name})");
                return;
            default:
                throw new NotSupportedException(underlying.Kind.ToString());
        }
    }

    private protected override void AppendAhkEntryPoint(StringBuilder sb, string entryPoint = "")
    {
        // https://www.autohotkey.com/docs/v2/lib/ComCall.htm
        sb.Append($"ComCall({VTableIndex}, this");
    }
}