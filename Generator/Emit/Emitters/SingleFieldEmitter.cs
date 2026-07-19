namespace AhkWin32.Generator.Emit.Emitters;

using AhkWin32.Generator.Model.Types;

/// <summary>
/// Shared emission helpers for single-field "value" types — handles and native
/// typedefs — both of which render as a one-field v2.1 <c>struct</c> with a
/// transparent <c>__value</c> setter. Keeps the two emitters from duplicating the
/// import and conversion logic.
/// </summary>
internal static class SingleFieldEmitter
{
    /// <summary>
    /// Emit #Import lines for the type's referenced types/functions, plus the
    /// implicitly-convertible source types referenced by the <c>__value</c> setter.
    /// Convertible imports come from <see cref="Win32Type.ConvertibleFrom"/> rather
    /// than the <see cref="Win32Type.Imports"/> collection, so a type only imports a
    /// convertible source when it actually emits a check against it.
    /// </summary>
    public static void EmitImports(AhkWriter w, Win32Type type)
    {
        IEnumerable<string> typeFqns = type
            .Imports.GetTypes()
            .Concat((type.ConvertibleFrom ?? []).Select(t => t.FQN))
            .Where(fqn => fqn != type.FQN)
            .Distinct()
            .OrderBy(fqn => fqn, StringComparer.Ordinal);

        foreach (string fqn in typeFqns)
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, fqn, moduleRelative: true);
            w.Import(path, [ImportResolver.GetImportName(fqn)]);
        }

        foreach (string apisFqn in type.Imports.GetFunctionNamespaces())
        {
            string path = ImportResolver.GetIncludePath(type.Namespace, apisFqn, moduleRelative: true);
            w.Import(path, type.Imports.GetFunctionsForNamespace(apisFqn));
        }
    }

    /// <summary>
    /// Emit the <c>__value</c> property whose setter unwraps the backing field of the
    /// type itself or any implicitly-convertible source (from <c>[AlsoUsableFor]</c>,
    /// inverted into <see cref="Win32Type.ConvertibleFrom"/>), and otherwise stores the
    /// raw value. Source and target are assumed to share the backing field name, which
    /// holds because convertibility only links types of the same kind (handle->handle,
    /// typedef->typedef).
    ///
    /// A <c>value-accessor</c> override may supply <paramref name="getterExpr"/> (restores a
    /// <c>__value</c> getter; <c>$field</c> -> <c>this.&lt;field&gt;</c>) and/or
    /// <paramref name="coerceExpr"/> (transforms the raw-value else branch; <c>$value</c> ->
    /// the setter's <c>value</c>). The instance-unwrap branch is unaffected.
    /// </summary>
    public static void EmitValueSetter(
        AhkWriter w,
        Win32Type type,
        string field,
        string? getterExpr = null,
        string? coerceExpr = null
    )
    {
        using (w.InstanceProperty("__value"))
        {
            if (getterExpr is not null)
            {
                w.Line($"get => {getterExpr.Replace("$field", $"this.{field}")}");
            }

            using (w.SetBlock())
            {
                string typeCheck = $"value is {type.Name}";
                if (type.ConvertibleFrom is { Count: > 0 })
                {
                    IEnumerable<string> sources = type
                        .ConvertibleFrom.Append(type)
                        .Select(t => t.Name)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase); // Stable order for source control
                    typeCheck = string.Join(" || ", sources.Select(n => $"(value is {n})"));
                }

                string stored = coerceExpr is null ? "value" : coerceExpr.Replace("$value", "value");

                using (w.If(typeCheck))
                {
                    w.Line($"this.{field} := value.{field}");
                }
                using (w.Else())
                {
                    w.Line($"this.{field} := {stored}");
                }
            }
        }
    }
}
