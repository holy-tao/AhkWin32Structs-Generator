namespace AhkWin32.Generator.Model.Members;

/// <summary>
/// A COM property backed by getter and/or setter methods.
/// </summary>
public sealed class ComPropertyMember
{
    /// <summary>Property name (without get_/put_ prefix).</summary>
    public required string Name { get; init; }

    /// <summary>Getter method, if any.</summary>
    public ComMethodMember? Getter { get; init; }

    /// <summary>Setter method, if any.</summary>
    public ComMethodMember? Setter { get; init; }
}
