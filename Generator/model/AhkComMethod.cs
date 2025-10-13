
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.Windows.SDK.Win32Docs;

class AhkComMethod : AhkMethod
{
    public int VTableIndex { get; private set; }

    public bool HasStringParam => parameters[1..].Any(p => p.GetTypeDefName(mr) is "BSTR");

    public AhkComMethod(MetadataReader mr, MethodDefinition methodDef, int vTableIndex) : base(mr, methodDef)
    {
        VTableIndex = vTableIndex;
    }

    //TODO: handle [RetVal] parameters
    //TODO: Related to above, wrap ahk literals as ComValues where possible - https://www.autohotkey.com/docs/v2/lib/ComValue.htm

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

        if (HasReturnValue)
        {
            sb.AppendLine($"        return result");
        }
        sb.AppendLine($"    }}");
    }

    private void AppendParameterConversions(StringBuilder sb)
    {
        bool addedConversions = false;
        foreach (AhkParameter param in parameters[1..])
        {
            string? typeName = param.GetTypeDefName(mr);

            if (typeName is "BSTR")
            {
                sb.AppendLine($"        {param.Name} := {param.Name} is String ? BSTR.Alloc({param.Name}).Value : {param.Name}");
                addedConversions = true;
            }
            else if (typeName is "PWSTR")
            {
                sb.AppendLine($"        {param.Name} := {param.Name} is String ? StrPtr({param.Name}) : {param.Name}");
                addedConversions = true;
            }
            else if (param.IsHandle(mr))
            {
                sb.AppendLine($"        {param.Name} := {param.Name} is Win32Handle ? NumGet({param.Name}, \"ptr\") : {param.Name}");
                addedConversions = true;
            }
            //TODO other ahk literal types that may need to be converted to variants?
        }

        if (addedConversions)
            sb.AppendLine();
    }

    private protected override void AppendAhkEntryPoint(StringBuilder sb, string entryPoint = "")
    {
        // https://www.autohotkey.com/docs/v2/lib/ComCall.htm
        sb.Append($"ComCall({VTableIndex}, this");
    }
}