using System.Reflection.Metadata;

public class ParameterDecoder
{
    public static List<AhkParameter> DecodeParameters(MetadataReader reader, MethodDefinition methodDef)
    {
#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
        var sig = methodDef.DecodeSignature(new FieldSignatureProvider(reader), null);
#pragma warning restore CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.

        var result = new List<AhkParameter>();

        // Build a lookup of ParameterHandle -> Parameter info
        Dictionary<int, Parameter> paramInfos = GetParameters(reader, methodDef);

        if (paramInfos.TryGetValue(0, out var retParam))
        {
            // Return type might be parameter at sequenceNumber 0
            result.Add(new AhkParameter(
                retParam.Name.IsNil ? "" : reader.GetString(retParam.Name),
                0,
                sig.ReturnType,
                retParam.Attributes,
                CustomAttrsForParam(reader, retParam)
            ));
        }
        else
        {
            result.Add(new AhkParameter(
                "Return Value",
                0,
                sig.ReturnType,
                System.Reflection.ParameterAttributes.None,
                CustomParamAttributes.None
            ));
        }

        // Parameters (SequenceNumber = 1..n)
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            int seq = i + 1;
            paramInfos.TryGetValue(seq, out var param);

            var custAttrs = CustomAttrsForParam(reader, param);
            // Check for [MemorySize] to identify buffers - these usually show up as byte buffers
            // BOOL SystemPrng([Out][MemorySize(BytesParamIndex = 1)] byte* pbRandomData, [In] UIntPtr cbRandomData);
            var fieldInfo = custAttrs.HasFlag(CustomParamAttributes.SizedBuffer) ?
                new FieldInfo(SimpleFieldKind.Primitive, "ptr") :
                sig.ParameterTypes[i];

            result.Add(new AhkParameter(
                param.Name.IsNil ? "" : reader.GetString(param.Name),
                param.SequenceNumber,
                fieldInfo,
                param.Attributes,
                custAttrs
            ));
        }

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

    private static CustomParamAttributes CustomAttrsForParam(MetadataReader reader, Parameter param)
    {
        CustomParamAttributes attrs = CustomParamAttributes.None;

        foreach (string attrName in CustomAttributeDecoder.GetAllNames(reader, param))
        {
            attrs |= attrName switch
            {
                "ReservedAttribute" => CustomParamAttributes.Reserved,
                "ConstAttribute" => CustomParamAttributes.Constant,
                "MemorySizeAttribute" => CustomParamAttributes.SizedBuffer,
                _ => 0
            };
        }

        return attrs;
    }
}