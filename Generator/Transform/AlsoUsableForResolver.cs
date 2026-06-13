namespace AhkWin32.Generator.Transform;

using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Some types in the Win32 metadata are implicitly convertible to other types. This is noted with the [AlsoUsableFor]
/// attribute, which carries the name of the type which the type the attribute applies to can be converted. This
/// transform inverts this relationship, so every type which <em>can be converted to</em> knows which type(s) can
/// convert to it.
/// </summary>
class AlsoUsableForResolver(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public void Apply(TypeRegistry registry)
    {
        foreach (var typedef in registry.GetAll().Where(t => t.AlsoUsableFor is not null))
        {
            _logger.LogTrace("Resolving [AlsoUsableFor] for {fqn}", typedef.FQN);

            foreach (string name in typedef.AlsoUsableFor ?? [])
            {
                string targetFqn = $"{typedef.Namespace}.{name}";
                Win32Type? target = registry.Resolve(targetFqn, typedef.Arch);
                if (target is null)
                {
                    _logger.LogError(
                        "'{name}' is also usable for '{target}', but failed to resolve '{fqn}'",
                        typedef.FQN,
                        name,
                        targetFqn
                    );
                    continue;
                }

                AddConvertible(target, typedef);
            }
        }
    }

    private void AddConvertible(Win32Type to, Win32Type from)
    {
        to.ConvertibleFrom ??= [];
        to.ConvertibleFrom.Add(from);
    }
}
