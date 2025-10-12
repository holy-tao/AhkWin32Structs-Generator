
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

    private protected override void AppendAhkEntryPoint(StringBuilder sb, string entryPoint = "")
    {
        // https://www.autohotkey.com/docs/v2/lib/ComCall.htm
        sb.Append($"ComCall({VTableIndex}, this");
    }
}