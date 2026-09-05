namespace AhkWin32.Generator.Transform;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads YAML overrides and applies them to types in the TypeRegistry.
/// Runs between extraction and extension application. Overrides are somewhat
/// ad-hoc, every case requires an explicit branch here as the IR is, by design,
/// immutable in most cases.
/// </summary>
public sealed class OverrideApplier(OverrideReader reader, ILogger<OverrideApplier> logger)
{
    private readonly OverrideReader _reader = reader;
    private readonly ILogger<OverrideApplier> _logger = logger;

    /// <summary>
    /// Load overrides from the given directory and apply them to the registry.
    /// </summary>
    public OverrideSet Apply(TypeRegistry registry, string overrideDirectoryPath)
    {
        OverrideSet overrides = _reader.LoadOverrides(overrideDirectoryPath);
        if (overrides.Count == 0)
            return overrides;

        int applied = 0;
        int skipped = 0;
        int unmatched = 0;

        foreach (TypeOverride ov in overrides.All)
        {
            // Skip: remove type from registry entirely
            if (ov.Skip)
            {
                int removed = registry.Remove(ov.FQN);
                if (removed > 0)
                {
                    _logger.LogDebug("Removed type {FQN} ({Count} variant(s)) via skip override", ov.FQN, removed);
                    skipped += removed;
                }
                else
                {
                    _logger.LogWarning("Skip override targets type not in registry: {FQN}", ov.FQN);
                    unmatched++;
                }
                continue;
            }

            var variants = registry.GetAllVariants(ov.FQN);
            if (variants.Count == 0)
            {
                _logger.LogWarning("Override targets type not in registry: {FQN}", ov.FQN);
                unmatched++;
                continue;
            }

            foreach (var type in variants)
            {
                ApplyTypeOverride(type, ov);
            }

            // add-methods requires access to the full registry
            if (ov.AddMethods is { Count: > 0 })
                ApplyAddMethods(registry, ov);

            applied++;
        }

        _logger.LogInformation(
            "Applied {Applied} override(s), skipped {Skipped} type(s){Unmatched}",
            applied,
            skipped,
            unmatched > 0 ? $", {unmatched} unmatched" : ""
        );

        return overrides;
    }

    private void ApplyTypeOverride(Win32Type type, TypeOverride ov)
    {
        // struct-size-field
        if (ov.StructSizeField is not null)
        {
            if (type is StructType structType)
            {
                structType.StructSizeFieldName = ov.StructSizeField;
                _logger.LogDebug("Set StructSizeFieldName={Field} on {FQN}", ov.StructSizeField, ov.FQN);
            }
            else
            {
                _logger.LogWarning(
                    "struct-size-field override on non-struct type {FQN} ({Kind})",
                    ov.FQN,
                    type.GetType().Name
                );
            }
        }

        // Field overrides
        if (ov.Fields is { Count: > 0 } && type is StructType st)
        {
            ApplyFieldOverrides(st, ov.Fields, ov.FQN);
        }

        // Method overrides
        if (ov.Methods is { Count: > 0 } && type is ApiType apiType)
        {
            ApplyMethodOverrides(apiType, ov.Methods, ov.FQN);
        }

        // value-accessor (native typedefs and handles only — they share the v2.1 __value emitter)
        if (ov.ValueAccessor is { } va)
        {
            ApplyValueAccessor(type, va, ov.FQN);
        }
    }

    private void ApplyValueAccessor(Win32Type type, ValueAccessorOverride va, string typeFqn)
    {
        switch (type)
        {
            case NativeTypedefType typedef:
                typedef.ValueGetterExpr = va.Getter;
                typedef.ValueSetterCoerceExpr = va.SetterCoerce;
                break;
            case HandleType handle:
                handle.ValueGetterExpr = va.Getter;
                handle.ValueSetterCoerceExpr = va.SetterCoerce;
                break;
            default:
                _logger.LogWarning(
                    "value-accessor override on unsupported type {FQN} ({Kind}) — only native typedefs and handles are supported",
                    typeFqn,
                    type.GetType().Name
                );
                return;
        }

        _logger.LogDebug(
            "Set value-accessor (getter={HasGetter}, setter-coerce={HasCoerce}) on {FQN}",
            va.Getter is not null,
            va.SetterCoerce is not null,
            typeFqn
        );
    }

    private void ApplyFieldOverrides(
        StructType structType,
        IReadOnlyDictionary<string, FieldOverride> fieldOverrides,
        string typeFqn
    )
    {
        foreach (var (fieldName, fieldOv) in fieldOverrides)
        {
            var field = structType.Members.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.Ordinal));

            if (field is null)
            {
                _logger.LogWarning("Field override targets non-existent field {Type}.{Field}", typeFqn, fieldName);
                continue;
            }

            if (fieldOv.AddAttributes != MemberFlags.None)
            {
                field.Flags |= fieldOv.AddAttributes;
                _logger.LogDebug("Added {Flags} to {Type}.{Field}", fieldOv.AddAttributes, typeFqn, fieldName);
            }
        }
    }

    private void ApplyMethodOverrides(
        ApiType apiType,
        IReadOnlyDictionary<string, MethodOverride> methodOverrides,
        string typeFqn
    )
    {
        foreach (var (methodName, methodOv) in methodOverrides)
        {
            // Skip method: remove from the method list
            if (methodOv.Skip)
            {
                int removed = apiType.Methods.RemoveAll(m => m.Name.Equals(methodName, StringComparison.Ordinal));

                if (removed > 0)
                    _logger.LogDebug("Removed method {Type}.{Method} via skip override", typeFqn, methodName);
                else
                    _logger.LogWarning(
                        "Method skip override targets non-existent method {Type}.{Method}",
                        typeFqn,
                        methodName
                    );
                continue;
            }

            // Parameter overrides
            if (methodOv.Parameters is not { Count: > 0 })
                continue;

            var method = apiType.Methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.Ordinal));

            if (method is null)
            {
                _logger.LogWarning("Method override targets non-existent method {Type}.{Method}", typeFqn, methodName);
                continue;
            }

            ApplyParameterOverrides(method, methodOv.Parameters, typeFqn, methodName);
        }
    }

    private void ApplyParameterOverrides(
        MethodMember method,
        IReadOnlyDictionary<string, ParameterOverride> paramOverrides,
        string typeFqn,
        string methodName
    )
    {
        foreach (var (paramName, paramOv) in paramOverrides)
        {
            var param = method.Parameters.FirstOrDefault(p => p.Name.Equals(paramName, StringComparison.Ordinal));

            if (param is null)
            {
                _logger.LogWarning(
                    "Parameter override targets non-existent parameter {Type}.{Method}.{Param}",
                    typeFqn,
                    methodName,
                    paramName
                );
                continue;
            }

            if (paramOv.AddAttributes != ParameterFlags.None)
            {
                param.Attributes |= paramOv.AddAttributes;
                _logger.LogDebug(
                    "Added {Flags} to {Type}.{Method}.{Param}",
                    paramOv.AddAttributes,
                    typeFqn,
                    methodName,
                    paramName
                );
            }
        }
    }

    private void ApplyAddMethods(TypeRegistry registry, TypeOverride ov)
    {
        if (ov.AddMethods is null)
            return;

        // The target is the type specified by ov.FQN
        var targetVariants = registry.GetAllVariants(ov.FQN);

        foreach (AddMethodRef addRef in ov.AddMethods)
        {
            // Find the source method
            var sourceVariants = registry.GetAllVariants(addRef.SourceFQN);
            if (sourceVariants.Count == 0)
            {
                _logger.LogWarning("add-methods source type not in registry: {SourceFQN}", addRef.SourceFQN);
                continue;
            }

            // Use the first variant to find the method (methods are the same across arch variants for ApiTypes)
            if (sourceVariants[0] is not ApiType sourceApi)
            {
                _logger.LogWarning("add-methods source {SourceFQN} is not an ApiType", addRef.SourceFQN);
                continue;
            }

            var sourceMethod = sourceApi.Methods.FirstOrDefault(m =>
                m.Name.Equals(addRef.MethodName, StringComparison.Ordinal)
            );

            if (sourceMethod is null)
            {
                _logger.LogWarning(
                    "add-methods: method {Method} not found in {SourceFQN}",
                    addRef.MethodName,
                    addRef.SourceFQN
                );
                continue;
            }

            // Add to all variants of the target
            foreach (var target in targetVariants)
            {
                if (target is not ApiType targetApi)
                {
                    _logger.LogWarning("add-methods target {FQN} is not an ApiType", ov.FQN);
                    break;
                }

                // Check if method already exists in target
                if (targetApi.Methods.Any(m => m.Name.Equals(addRef.MethodName, StringComparison.Ordinal)))
                {
                    _logger.LogDebug(
                        "Method {Method} already exists in {FQN} — skipping add-methods",
                        addRef.MethodName,
                        ov.FQN
                    );
                    continue;
                }

                // Clone the method with the target's namespace
                var cloned = CloneMethod(sourceMethod, targetApi.Namespace);
                targetApi.Methods.Add(cloned);

                // Add imports from the cloned method
                targetApi.Imports.MergeFrom(cloned.Imports);

                _logger.LogDebug(
                    "Cloned method {Method} from {Source} to {Target}",
                    addRef.MethodName,
                    addRef.SourceFQN,
                    ov.FQN
                );
            }
        }
    }

    /// <summary>
    /// Create a copy of a MethodMember with a different namespace.
    /// </summary>
    private static MethodMember CloneMethod(MethodMember source, string targetNamespace)
    {
        return new MethodMember
        {
            Name = source.Name,
            Namespace = targetNamespace,
            DllName = source.DllName,
            EntryPoint = source.EntryPoint,
            CallingConvention = source.CallingConvention,
            CharSet = source.CharSet,
            SetsLastError = source.SetsLastError,
            PreserveSig = source.PreserveSig,
            CanReturnErrorsAsSuccess = source.CanReturnErrorsAsSuccess,
            CanReturnMultipleSuccessValues = source.CanReturnMultipleSuccessValues,
            Parameters = source.Parameters,
            OutputParameter = source.OutputParameter,
            Description = source.Description,
            Remarks = source.Remarks,
            HelpLink = source.HelpLink,
            DeprecationMessage = source.DeprecationMessage,
            ReturnValueDoc = source.ReturnValueDoc,
            SupportedOSPlatform = source.SupportedOSPlatform,
            ShouldThrowOnHResult = source.ShouldThrowOnHResult,
            Imports = source.Imports,
        };
    }
}
