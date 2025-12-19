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
            // Win32 method with primitive return type
            result.Add(new AhkParameter(null, default, sig.ReturnType));
        }
        else if(methodDef.Attributes.HasFlag(MethodAttributes.SpecialName))
        {
            // WinRT method - these don't expose their ABI return values in metadata. All WinRT methods return 
            // HSRESULTs, except for event add_ methods, which return EventRegistrationTokens, and remove_ methods,
            // which return void
            // https://learn.microsoft.com/en-us/uwp/winrt-cref/winrt-type-system#events
            string methodName = reader.GetString(methodDef.Name);
            if (methodName.StartsWith("add_"))
            {
                TypeDefinitionHandle evtRegTokHandle = FieldSignatureDecoder.FindTypeDefinition(
                    "Windows", "Windows.Foundation", "EventRegistrationToken", out var windowsMr);
                TypeDefinition evtRegTok = windowsMr.GetTypeDefinition(evtRegTokHandle);
                
                result.Add(new AhkParameter(null, default, 
                    new(SimpleFieldKind.Struct, "EventRegistrationToken", 0, evtRegTok, null, windowsMr)));
            }
            else if (methodName.StartsWith("remove_"))
            {
                result.Add(new AhkParameter(null, default, new(SimpleFieldKind.Primitive, "Void")));
            }
            else
            {
                // Not an event
                result.Add(new AhkParameter(null, default, new(SimpleFieldKind.HRESULT, "HRESULT")));
            }
        }
        else
        {
            // Normal WinRT method - these all return HRESULTs
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
            
            result.Add(new AhkParameter(reader, param, fieldInfo));
        }

        // WinRT encodes the output parameter as parameter 0 (return value), but for the Com plumbing
        // it's the last param
        // e.g. string GetString(int value) => HRESULT GetString(int value, string** out)
        if(isWinRT && paramInfos.TryGetValue(0, out Parameter outParam))
            result.Add(new AhkParameter(
                reader, 
                outParam, 
                new FieldInfo(SimpleFieldKind.Pointer, "Pointer", 0, null, sig.ReturnType), 
                true));

        return result;
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