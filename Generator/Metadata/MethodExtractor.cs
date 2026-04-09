namespace AhkWin32.Generator.Metadata;

using System.Reflection;
using System.Reflection.Metadata;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.Win32Docs;

/// <summary>
/// Extracts method definitions from metadata into MethodMember instances.
/// Ports logic from AhkMethod constructor, GetOutputParameter, ShouldThrowForReturnValue,
/// and GetReferencedTypes.
/// </summary>
public sealed class MethodExtractor
{
    private readonly DocumentationLoader _docs;
    private readonly ParameterExtractor _paramExtractor;
    private readonly ILogger<MethodExtractor> _logger;

    public MethodExtractor(
        DocumentationLoader docs,
        ParameterExtractor paramExtractor, ILogger<MethodExtractor> logger)
    {
        _docs = docs;
        _paramExtractor = paramExtractor;
        _logger = logger;
    }

    /// <summary>
    /// Extract a MethodMember from a MethodDefinition.
    /// Returns null if extraction fails.
    /// </summary>
    public MethodMember? ExtractMethod(
        MetadataReader reader, MethodDefinition methodDef,
        string declaringNamespace)
    {
        string methodName = reader.GetString(methodDef.Name);

        try
        {
            return ExtractMethodCore(reader, methodDef, methodName, declaringNamespace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract method {Namespace}.{Method}",
                declaringNamespace, methodName);
            return null;
        }
    }

    private MethodMember ExtractMethodCore(
        MetadataReader reader, MethodDefinition methodDef,
        string methodName, string declaringNamespace)
    {
        // Extract DLL import info
        MethodImport import = methodDef.GetImport();
        string dllName = import.Module.IsNil ? "" : reader.GetString(reader.GetModuleReference(import.Module).Name);
        string entryPoint = import.Name.IsNil ? "" : reader.GetString(import.Name);

        // Map calling convention
        CallingConvention callingConvention = (import.Attributes & MethodImportAttributes.CallingConventionMask) switch
        {
            MethodImportAttributes.CallingConventionCDecl => CallingConvention.CDecl,
            MethodImportAttributes.CallingConventionStdCall => CallingConvention.StdCall,
            MethodImportAttributes.CallingConventionThisCall => CallingConvention.ThisCall,
            MethodImportAttributes.CallingConventionFastCall => CallingConvention.FastCall,
            MethodImportAttributes.CallingConventionWinApi => CallingConvention.WinApi,
            _ => CallingConvention.StdCall
        };

        // Map character set
        StringEncoding charSet = (import.Attributes & MethodImportAttributes.CharSetMask) switch
        {
            MethodImportAttributes.CharSetAnsi => StringEncoding.Ansi,
            MethodImportAttributes.CharSetUnicode => StringEncoding.Unicode,
            _ => StringEncoding.None
        };

        bool setsLastError = import.Attributes.HasFlag(MethodImportAttributes.SetLastError);

        // Decode method-level custom attributes
        var methodAttrs = DecodeMethodAttributes(reader, methodDef);

        // Load documentation
        ApiDetails? apiDetails = _docs.GetApiDetails(reader, methodDef);

        // Extract parameters
        List<ParameterMember> parameters = _paramExtractor.ExtractParameters(
            reader, methodDef, apiDetails);

        // Compute output parameter
        ParameterMember? outputParameter = GetOutputParameter(
            parameters, methodAttrs.PreserveSig, methodAttrs.CanReturnErrorsAsSuccess);

        // Compute ShouldThrowOnHResult
        bool shouldThrow = ShouldThrowOnHResult(
            parameters, methodAttrs.PreserveSigValue,
            methodAttrs.CanReturnErrorsAsSuccess, methodAttrs.CanReturnMultipleSuccessValues);

        // Collect referenced types
        List<string> referencedTypes = CollectReferencedTypes(
            parameters, entryPoint, outputParameter);

        MethodMember result = new()
        {
            Name = methodName,
            Namespace = declaringNamespace,
            DllName = dllName,
            EntryPoint = entryPoint,
            CallingConvention = callingConvention,
            CharSet = charSet,
            SetsLastError = setsLastError,
            PreserveSig = methodAttrs.PreserveSig,
            CanReturnErrorsAsSuccess = methodAttrs.CanReturnErrorsAsSuccess,
            CanReturnMultipleSuccessValues = methodAttrs.CanReturnMultipleSuccessValues,
            Parameters = parameters,
            OutputParameter = outputParameter,
            ShouldThrowOnHResult = shouldThrow,
            Description = apiDetails?.Description,
            Remarks = apiDetails?.Remarks,
            HelpLink = apiDetails?.HelpLink,
            DeprecationMessage = methodAttrs.DeprecationMessage,
            ReturnValueDoc = apiDetails?.ReturnValue,
            SupportedOSPlatform = methodAttrs.SupportedOSPlatform,
            ReferencedTypes = referencedTypes
        };

        _logger.LogTrace("Extracted method {Namespace}.{Method} ({ParamCount} params, dll={Dll})",
            declaringNamespace, methodName, parameters.Count - 1, dllName);

        return result;
    }

    /// <summary>
    /// Decode method-level custom attributes in a single pass.
    /// </summary>
    private static MethodAttrs DecodeMethodAttributes(MetadataReader reader, MethodDefinition methodDef)
    {
        bool preserveSig = false;
        bool? preserveSigValue = null;
        bool canReturnErrorsAsSuccess = false;
        bool canReturnMultipleSuccessValues = false;
        string? deprecationMessage = null;
        string? supportedOSPlatform = null;

        foreach (CustomAttributeHandle attrHandle in methodDef.GetCustomAttributes())
        {
            CustomAttribute attr = reader.GetCustomAttribute(attrHandle);
            (_, string attrName) = AttributeReader.GetAttributeTypeName(reader, attr);

            switch (attrName)
            {
                case "PreserveSigAttribute":
                {
                    preserveSig = true;
                    CustomAttributeValue<string> decoded = attr.DecodeValue(new CaTypeProvider());
                    preserveSigValue = decoded.FixedArguments.Length > 0
                        ? decoded.FixedArguments[0].Value as bool? ?? true
                        : true;
                    break;
                }

                case "CanReturnErrorsAsSuccessAttribute":
                    canReturnErrorsAsSuccess = true;
                    break;

                case "CanReturnMultipleSuccessValuesAttribute":
                    canReturnMultipleSuccessValues = true;
                    break;

                case "ObsoleteAttribute":
                {
                    CustomAttributeValue<string> decoded = attr.DecodeValue(new CaTypeProvider());
                    deprecationMessage = decoded.FixedArguments.Length > 0
                        ? decoded.FixedArguments[0].Value as string
                        : null;
                    break;
                }

                case "SupportedOSPlatformAttribute":
                {
                    CustomAttributeValue<string> decoded = attr.DecodeValue(new CaTypeProvider());
                    supportedOSPlatform = (string?)decoded.FixedArguments[0].Value;
                    break;
                }
            }
        }

        return new MethodAttrs(preserveSig, preserveSigValue, canReturnErrorsAsSuccess,
            canReturnMultipleSuccessValues, deprecationMessage, supportedOSPlatform);
    }

    /// <summary>
    /// Determine the logical output parameter for a DllImport method.
    /// Port of AhkMethod.GetOutputParameter.
    /// </summary>
    internal static ParameterMember? GetOutputParameter(
        List<ParameterMember> parameters, bool preserveSig, bool canReturnErrorsAsSuccess)
    {
        // If we have PreserveSig OR CanReturnErrorsAsSuccess OR the function doesn't return HRESULT,
        // don't collapse [out] parameters
        if (preserveSig || canReturnErrorsAsSuccess)
            return null;

        if (parameters.Count == 0 || parameters[0].Type is not HResultType)
            return null;

        // Find single [out] parameter. If there's more than one, don't use an output parameter
        var candidates = parameters
            .Where(p => p.IsOut && !p.IsIn)
            .Where(p => p.IsPtrToPrimitive || p.IsPtrToHandle || p.IsPtrToCom)
            .ToList();

        return candidates.Count == 1 ? candidates.Single() : null;
    }

    /// <summary>
    /// Should this method's HRESULT return throw automatically (via DllCall return type of "HRESULT")?
    /// Port of AhkMethod.ShouldThrowForReturnValue.
    /// </summary>
    private static bool ShouldThrowOnHResult(
        List<ParameterMember> parameters, bool? preserveSigValue,
        bool canReturnErrorsAsSuccess, bool canReturnMultipleSuccessValues)
    {
        // Must return HRESULT
        if (parameters.Count == 0 || parameters[0].Type is not HResultType)
            return false;

        // If we'd need to free resources before throwing, don't auto-throw
        if (parameters.Any(p => p.HasFreeWithAttr))
            return false;

        // If [PreserveSig] is present, return its value (true = auto-throw via DllCall "HRESULT")
        if (preserveSigValue.HasValue)
            return preserveSigValue.Value;

        // [CanReturnMultipleSuccessValues] or [CanReturnErrorsAsSuccess] → don't throw
        return !canReturnMultipleSuccessValues && !canReturnErrorsAsSuccess;
    }

    /// <summary>
    /// Collect FQNs of types referenced by a method for #Include generation.
    /// Port of AhkMethod.GetReferencedTypes.
    /// </summary>
    private static List<string> CollectReferencedTypes(
        List<ParameterMember> parameters, string entryPoint,
        ParameterMember? outputParameter)
    {
        List<string> refs = [];

        // Ordinal entry points need LibraryLoader + Foundation
        if (entryPoint.StartsWith('#'))
        {
            refs.Add("Windows.Win32.Foundation.Apis");
            refs.Add("Windows.Win32.System.LibraryLoader.Apis");
        }

        // Import handle if we return a handle
        if (parameters.Count > 0 && parameters[0].Type is HandleRef hr)
        {
            refs.Add(hr.FQN);
        }

        // Import NTSTATUS if we return NTStatus
        if (parameters.Count > 0 && parameters[0].Type is NtStatusType)
        {
            refs.Add("Windows.Win32.Foundation.NTSTATUS");
        }

        // Import output parameter pointee type
        if (outputParameter != null)
        {
            ResolvedType? pointee = outputParameter.Pointee;
            if (pointee is StructRef sr)
                refs.Add(sr.FQN);
            else if (pointee is ComRef cr)
                refs.Add(cr.FQN);
            else if (pointee is HandleRef ohr)
                refs.Add(ohr.FQN);
        }

        // Import [FreeWith] parameter Apis types
        foreach (ParameterMember param in parameters.Where(p => p.FreeWith != null))
        {
            refs.Add(param.FreeWith!.ApisFQN);
        }

        return refs.Distinct().ToList();
    }

    /// <param name="PreserveSig">Whether [PreserveSig] attribute is present.</param>
    /// <param name="PreserveSigValue">The boolean value of [PreserveSig], or true if present with no args.
    /// Null if attribute is not present.</param>
    private sealed record MethodAttrs(
        bool PreserveSig,
        bool? PreserveSigValue,
        bool CanReturnErrorsAsSuccess,
        bool CanReturnMultipleSuccessValues,
        string? DeprecationMessage,
        string? SupportedOSPlatform);
}
