
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
        VTableOffset = 2;
        Methods.RemoveAll((method) => method.Name is not "Invoke");   // Remove .ctor and anything else
        Methods.Single().VTableIndex = 3; // Invoke is the third method in the vtable, this gets thrown off by the constructor
    }

    public override void ToAhk(StringBuilder sb)
    {
        HeadersToAhk(sb);
        sb.AppendLine();

        MaybeAddTypeDocumentation(sb);
        sb.AppendLine($"class {Name} extends IUnknown {{");

        sb.AppendLine();
        sb.AppendLine(AhkCtorCode);
        sb.AppendLine();
        sb.AppendLine("    Call(params*) => this.Invoke(params*)"); // Make delegates callable objects

        BodyToAhk(sb);

        sb.AppendLine("}");
    }

    /// <summary>
    /// __New code common to all delegates - make sure to include inentation
    /// Allows the delegate to be instantiated with a callback function so callers don't need to
    /// create an implementation object with only one method.
    /// </summary>
    private static readonly string AhkCtorCode = """
        /**
         * Constructor - create a new delegate instance
         *
         * @param {Object | Function | Number} callbackOrPtrOrImplObj callback function, pointer to the
         *             interface to wrap, or an implementation object with an Invoke method.
         * @param {String} callbackCreateOptions options for creating the callbacks in `callbackOrPtrOrImplObj`
         *             is a function or implementation object.
         */
        __New(callbackOrPtrOrImplObj, callbackCreateOptions := "") {
            if(HasMethod(callbackOrPtrOrImplObj)) {
                callbackOrPtrOrImplObj := { Invoke: callbackOrPtrOrImplObj }
            }
            super.__New(callbackOrPtrOrImplObj, callbackCreateOptions)
        }
    """;
}