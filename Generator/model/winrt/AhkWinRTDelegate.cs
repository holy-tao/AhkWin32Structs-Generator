
using System.Reflection.Metadata;
using System.Text;

/// <summary>
/// A WinRT delegate. WinRT delegates are COM Interfaces extending IUnknown with one method, Invoke. The shape of
/// Invoke varies depending on the delegate; that's what's encoded in the metadata.
/// </summary>
class AhkWinRTDelegate : AhkComInterface
{
    public AhkWinRTDelegate(MetadataReader mr, TypeDefinition typeDef): base(mr, typeDef)
    {
        // Override BaseInterface, remove .ctor as a method, remove any properties because the delegate
        // method has [SpecialName] on it
        // Kind of a hack, but I think less of a hack than type checking ourselves in AhkComInterface
        TypeDefinitionHandle hDef = FieldSignatureDecoder.FindTypeDefinition("Windows.Win32",
            "Windows.Win32.System.Com", "IUnknown", out var baseReader);
        BaseInterface = (baseReader, baseReader.GetTypeDefinition(hDef));

        Properties.Clear();
        VTableOffset = 3;
        Methods.RemoveAll((method) => method.Name is not "Invoke");   // Remove .ctor and anything else
        Methods.Single().VTableIndex = 3; // Invoke is the third method in the vtable, this gets thrown off by the constructor
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends IUnknown {{");

        BodyToAhk(sb);

        sb.AppendLine("}");
    }

    private protected override void BodyToAhk(StringBuilder sb)
    {
        sb.AppendLine();
        AppendStaticCode(sb);

        sb.AppendLine();
        AppendVTableList(sb);

        foreach (AhkComProperty prop in Properties)
        {
            sb.AppendLine();
            prop.ToAhk(sb);
        }

        foreach (AhkComMethod method in Methods)
        {
            sb.AppendLine();
            method.ToAhk(sb);
        }
        
        extensions?.ForEach(ex => sb.AppendLine(GetExtensionCodeTokenized(ex)));
    }
}