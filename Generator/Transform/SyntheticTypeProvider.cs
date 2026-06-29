namespace AhkWin32.Generator.Transform;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Injects hand-declared synthetic types into the <see cref="TypeRegistry"/> — types that
/// have no Win32 metadata definition but that we want to participate fully in type
/// resolution, imports, and emission (rather than being special-cased like System.Guid).
///
/// <para>
/// Currently the only entry is <c>WCHAR</c>: Win32 metadata defines <c>CHAR</c> as a
/// NativeTypedef but has no <c>WCHAR</c>/<c>TCHAR</c> equivalent (CLR <c>char</c> arrays
/// carry no typedef). Registering <c>WCHAR</c> as a NativeTypedef over <c>UInt16</c> lets
/// <see cref="Emit.Emitters.NativeTypedefEmitter21"/> emit <c>WCHAR.ahk</c> for free and
/// gives Unicode fixed-char-array fields a real per-type hook for string-assignment magic
/// (attached via the <c>WCHAR</c> extension).
/// </para>
///
/// <para>v2.1 only — the v2.0 emitter handles fixed char arrays via StringType/StrGet/StrPut.</para>
/// </summary>
public sealed class SyntheticTypeProvider(ILogger<SyntheticTypeProvider> logger)
{
    private readonly ILogger<SyntheticTypeProvider> _logger = logger;

    /// <summary>
    /// Register synthetic types into the registry. Must run after extraction and before
    /// the override/extension transforms so extensions can attach to the synthetic types.
    /// </summary>
    public void Apply(TypeRegistry registry)
    {
        RegisterWchar(registry);
    }

    /// <summary>
    /// Register <c>Windows.Win32.Foundation.WCHAR</c> as a NativeTypedef over <c>UInt16</c>,
    /// mirroring the assembly/version of the existing <c>CHAR</c> typedef so it lands in the
    /// same namespace and is consistent with the emitted metadata.
    /// </summary>
    private void RegisterWchar(TypeRegistry registry)
    {
        const string charFqn = "Windows.Win32.Foundation.CHAR";
        const string wcharFqn = "Windows.Win32.Foundation.WCHAR";

        if (registry.Contains(wcharFqn))
        {
            _logger.LogDebug("{Fqn} already present; skipping synthetic registration", wcharFqn);
            return;
        }

        var charType = registry.Resolve<NativeTypedefType>(charFqn, Architecture.All);
        if (charType is null)
        {
            _logger.LogWarning("{Fqn} not found in registry; cannot derive synthetic WCHAR", charFqn);
            return;
        }

        var wchar = new NativeTypedefType
        {
            Identity = TypeIdentity.Universal(wcharFqn),
            Name = "WCHAR",
            CanonicalName = "WCHAR",
            AssemblyName = charType.AssemblyName,
            MetadataVersion = charType.MetadataVersion,
            Underlying = new PrimitiveType("UInt16"),
            Description = "A 16-bit Unicode (UTF-16) character.",
        };

        registry.Register(wchar);
        _logger.LogInformation("Registered synthetic type {Fqn} (NativeTypedef over UInt16)", wcharFqn);
    }
}
