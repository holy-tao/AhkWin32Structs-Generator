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

        // Return type (SequenceNumber = 0)
        if (paramInfos.TryGetValue(0, out var retParam))
        {
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
                "Null Return Value",
                0,
                new FieldInfo(SimpleFieldKind.Other, "Void"),
                System.Reflection.ParameterAttributes.None,
                CustomParamAttributes.None
            ));
        }

        // Parameters (SequenceNumber = 1..n)
        for (int i = 0; i < sig.ParameterTypes.Length; i++)
        {
            int seq = i + 1;
            paramInfos.TryGetValue(seq, out var param);

            result.Add(new AhkParameter(
                param.Name.IsNil ? "" : reader.GetString(param.Name),
                param.SequenceNumber,
                sig.ParameterTypes[i],
                param.Attributes,
                CustomAttrsForParam(reader, param)
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
                _ => 0
            };
        }

        return attrs;
    }
}