using System.Reflection.Metadata;

public static class SigTypeCodeExtensions
{
    public static bool IsPrimitive(this SignatureTypeCode sigTypeCode) => sigTypeCode switch
    {
        SignatureTypeCode.Boolean => true,
        SignatureTypeCode.Char => true,
        SignatureTypeCode.SByte => true,
        SignatureTypeCode.Byte => true,
        SignatureTypeCode.Int16 => true,
        SignatureTypeCode.UInt16 => true,
        SignatureTypeCode.Int32 => true,
        SignatureTypeCode.UInt32 => true,
        SignatureTypeCode.Int64 => true,
        SignatureTypeCode.UInt64 => true,
        SignatureTypeCode.Single => true,
        SignatureTypeCode.Double => true,
        SignatureTypeCode.IntPtr => true,
        SignatureTypeCode.UIntPtr => true,
        _ => false
    };

    public static bool IsPointer(this SignatureTypeCode signatureTypeCode) => signatureTypeCode switch
    {
        SignatureTypeCode.IntPtr => true,
        SignatureTypeCode.UIntPtr => true,
        SignatureTypeCode.FunctionPointer => true,
        _ => false
    };
}