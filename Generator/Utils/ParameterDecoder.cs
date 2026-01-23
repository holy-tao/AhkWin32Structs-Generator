using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;

public class ParameterDecoder
{
    public static List<AhkParameter> DecodeParameters(MetadataReader reader, MethodDefinition methodDef)
    {
        bool isWinRT = reader.GetTypeDefinition(methodDef.GetDeclaringType())
            .Attributes.HasFlag(TypeAttributes.WindowsRuntime);
        var sig = methodDef.DecodeSignature(new FieldSignatureProvider(reader), new());
        var result = new List<AhkParameter>();

        // Build a lookup of ParameterHandle -> Parameter info
        Dictionary<int, Parameter> paramInfos = GetParameters(reader, methodDef);

        // Get the return value
        if (!isWinRT && paramInfos.TryGetValue(0, out var retParam))
        {
            // Return type might be parameter at sequenceNumber 0 for Win32 interfaces
            result.Add(new AhkParameter(reader, retParam, sig.ReturnType));
        }
        else if(!isWinRT)
        {
            // Win32 method with primitive return type (potentially void)
            result.Add(new AhkParameter(null, default, sig.ReturnType));
        }
        else
        {
            // ABI return value for all WinRT methods is HRESULT, actual return values, even primitives,
            // are always the [out] params.
            result.Add(new AhkParameter(null, default, new(SimpleFieldKind.HRESULT, "HRESULT")));
        }

        // Parameters (SequenceNumber = 1..n)
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            if(!paramInfos.TryGetValue(i + 1, out Parameter param))
                throw new NullReferenceException($"No parameter at index {i}");

            // Check for [MemorySize] to identify buffers - these usually show up as byte buffers
            // BOOL SystemPrng([Out][MemorySize(BytesParamIndex = 1)] byte* pbRandomData, [In] UIntPtr cbRandomData);
            var fieldInfo = !isWinRT && CustomAttributeDecoder.GetAllNames(reader, param).Any(n => n is "MemorySizeAttribute") ?
                new FieldInfo(SimpleFieldKind.Primitive, "ptr") : sig.ParameterTypes[i];

            if(sig.ParameterTypes[i].Kind is SimpleFieldKind.SZArray)
            {
                Trace.TraceInformation($"Expanding SZArray parameter: {reader.GetFullyQualifiedName(methodDef)}::{reader.GetString(param.Name)}");
                // Expand SZArray parameters
                DecodeZSArrayParameter(reader, sig.ParameterTypes[i], param, result);
            }
            else
            {
                result.Add(new AhkParameter(reader, param, fieldInfo));
            }
        }

        // WinRT encodes the output parameter as parameter 0 (return value), but in the ABI surface it's
        // the last argument and is always a pointer to a type (unless it's void, in which case it's omitted). 
        // For example:
        //      string GetString(int value) => HRESULT GetString(int value, string** out)
        if (isWinRT && sig.ReturnType.TypeName is not "Void")
        {
            bool foundParamInfo = paramInfos.TryGetValue(0, out Parameter outParam);

            result.Add(new AhkParameter(
                foundParamInfo ? reader : null, 
                foundParamInfo ? outParam : default, 
                new FieldInfo(SimpleFieldKind.Pointer, "Pointer", 0, null, sig.ReturnType),
                true,
                "output_"));
        }

        return result;
    }

    /*
    Conceptually:
    [In] T[]        => PassArray        => UInt32 length, T* buffer
    [In, Out] T[]   => FillArray        => UInt32 length, T* buffer
    [Out] T[]       => ReceiveArray     => UInt32* length, T** buffer
    */
    private static void DecodeZSArrayParameter(MetadataReader reader, FieldInfo paramType, Parameter param, List<AhkParameter> parameters)
    {
        // See: https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#array-parameters
        bool isOut = param.Attributes.HasFlag(ParameterAttributes.Out);
        bool isIn = param.Attributes.HasFlag(ParameterAttributes.In);

        if(isOut && !isIn)
        {
            // ReceiveArray (Caller receives it) - UInt32* length, T** buffer
            parameters.Add(new AhkParameter(
                null, 
                default, 
                new(SimpleFieldKind.Pointer, "Pointer", 0, null, new(SimpleFieldKind.Primitive, "UInt32")), 
                false, 
                reader.GetString(param.Name) + "_length"));
            parameters.Add(new AhkParameter(
                null, 
                default,  // Don't use the SZArray param itself
                new(SimpleFieldKind.Pointer, "Pointer", 0, null, new(SimpleFieldKind.Pointer, "Pointer", 0, null, paramType.UnderlyingType)), 
                false, 
                reader.GetString(param.Name)));
        }
        else
        {
            // FillArray or PassArray - UInt32 length, T* buffer
            parameters.Add(new AhkParameter(null, default, new(SimpleFieldKind.Primitive, "UInt32"), false, reader.GetString(param.Name) + "_length"));
            parameters.Add(new AhkParameter(
                null, 
                default, 
                new(SimpleFieldKind.Pointer, "Pointer", 0, null, paramType.UnderlyingType), 
                false, 
                reader.GetString(param.Name)));
        }
    }

    private static Dictionary<int, Parameter> GetParameters(MetadataReader reader, MethodDefinition methodDef)
    {
        var paramInfos = new Dictionary<int, Parameter>();
        foreach (var paramHandle in methodDef.GetParameters())
        {
            var param = reader.GetParameter(paramHandle);
            paramInfos[param.SequenceNumber] = param;
        }

        return paramInfos;
    }
}