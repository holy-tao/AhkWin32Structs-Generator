

using System.Collections.Immutable;

namespace Tao.AHK.WindowsBindGen.PiidPrecompute;

/// <summary>
/// Represents a WinRT type signature used for computing parameterized interface IDs.
/// </summary>
public abstract record WinRTSignature
{
    public abstract string ToSignatureString();

    public sealed record Primitive(string Code) : WinRTSignature
    {
        public override string ToSignatureString() => Code;
    }

    public sealed record Guid(System.Guid Value) : WinRTSignature
    {
        public override string ToSignatureString() => $"{{{Value}}}";
    }

    public sealed record Enum(string FullName, WinRTSignature UnderlyingType) : WinRTSignature
    {
        public override string ToSignatureString() =>
            $"enum({FullName};{UnderlyingType.ToSignatureString()})";
    }

    public sealed record Struct(string FullName, ImmutableArray<WinRTSignature> Fields) : WinRTSignature
    {
        public override string ToSignatureString()
        {
            var fields = string.Join(";", Fields.Select(f => f.ToSignatureString()));
            return $"struct({FullName};{fields})";
        }
    }

    public sealed record Delegate(System.Guid Iid) : WinRTSignature
    {
        public override string ToSignatureString() => $"delegate({{{Iid}}})";
    }

    public sealed record RuntimeClass(string FullName, WinRTSignature DefaultInterface) : WinRTSignature
    {
        public override string ToSignatureString() =>
            $"rc({FullName};{DefaultInterface.ToSignatureString()})";
    }

    public sealed record PInterface(System.Guid Piid, ImmutableArray<WinRTSignature> TypeArgs) : WinRTSignature
    {
        public override string ToSignatureString()
        {
            var args = string.Join(";", TypeArgs.Select(a => a.ToSignatureString()));
            return $"pinterface({{{Piid}}};{args})";
        }

        /// <summary>
        /// Computes the instantiated interface ID by hashing the signature.
        /// </summary>
        public System.Guid ComputeIid() => WinRTGuidGenerator.ComputeGuid(ToSignatureString());
    }

    public sealed record GenericParameter(int Index) : WinRTSignature
    {
        public override string ToSignatureString() =>
            throw new InvalidOperationException("Generic parameters must be substituted before computing signature");
    }

    public sealed record Array(WinRTSignature ElementType) : WinRTSignature
    {
        public override string ToSignatureString() =>
            throw new NotSupportedException("WinRT does not support array signatures in pinterface computation");
    }

    public sealed record Invalid(string Message) : WinRTSignature
    {
        public override string ToSignatureString() =>
            throw new InvalidOperationException(Message);
    }
}