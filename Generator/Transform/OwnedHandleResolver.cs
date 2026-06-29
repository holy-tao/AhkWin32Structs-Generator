namespace AhkWin32.Generator.Transform;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Marks handle types that need an <c>OwnedWith(freeFunc)</c> factory. A returned or <c>[Out]</c>
/// handle whose call-site <c>[RAIIFree]</c> differs from the handle type's default free function
/// (e.g. a <c>HANDLE</c> closed with <c>FindClose</c> instead of <c>CloseHandle</c>) must be boxed
/// as a runtime subclass that frees via that specific function, rather than the plain <c>.Owned</c>.
/// Setting <see cref="HandleType.NeedsOwnedWith"/> lets the emitter generate that factory only on the
/// handful of handles that actually use it, instead of every freeable handle.
///
/// This is a transform, not extraction, because the divergence test needs each handle type's
/// resolved default <c>[RAIIFree]</c> - which may not be extracted yet when a referencing method is.
/// </summary>
class OwnedHandleResolver(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public void Apply(TypeRegistry registry)
    {
        foreach (var type in registry.GetAll())
        {
            IEnumerable<MethodMember> methods = type switch
            {
                ApiType api => api.Methods,
                ComInterfaceType com => com.Methods,
                _ => [],
            };

            foreach (var method in methods)
            {
                // A directly-returned handle, or an [Out] pointer-to-handle: the same shapes the
                // emitter boxes via OwnedWith.
                if (method.HasReturnValue && method.Parameters[0] is { Type: HandleRef rh } ret)
                    MarkIfDivergent(registry, ret, rh.FQN);

                if (method.OutputParameter is { Type: PointerType { Pointee: HandleRef ph } } outp)
                    MarkIfDivergent(registry, outp, ph.FQN);
            }
        }
    }

    private void MarkIfDivergent(TypeRegistry registry, ParameterMember param, string handleFqn)
    {
        // Borrowed ([DoNotRelease]) or no call-site RAIIFree -> uses the default free, no factory.
        if (!param.ScriptOwned || param.RAIIFree is not { } raii)
            return;

        if (registry.Resolve(handleFqn, Architecture.All) is not HandleType ht || ht.FreeFunc is null)
            return;

        if (raii != ht.FreeFunc && !ht.NeedsOwnedWith)
        {
            _logger.LogTrace("{handle} needs an OwnedWith factory (context free {free})", handleFqn, raii.Name);
            ht.NeedsOwnedWith = true;
        }
    }
}
