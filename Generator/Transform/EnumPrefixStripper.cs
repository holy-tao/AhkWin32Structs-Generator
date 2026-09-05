namespace AhkWin32.Generator.Transform;

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Members;
using AhkWin32.Generator.Model.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Strips the redundant type-name prefix from enum constant names, so that
/// <c>WinHttpRequestAutoLogonPolicy.AutoLogonPolicy_Always</c> becomes
/// <c>WinHttpRequestAutoLogonPolicy.Always</c>.
///
/// <para>
/// Modelled on the Swift Objective-C importer: for each constant, strip the longest leading run
/// of words that appears (in order) in the enum's own type name. The original metadata name is
/// preserved on <see cref="ConstantMember.NativeName"/> so the emitter can keep it in the doc
/// comment for searchability against the Microsoft docs.
/// </para>
/// </summary>
public sealed partial class EnumPrefixStripper(ILogger<EnumPrefixStripper> logger)
{
    /// <summary>Value of the <c>enum-prefix</c> override that disables stripping for a type.</summary>
    public const string KeepSentinel = "keep";

    /// <summary>
    /// Splits a name into words. All-caps runs stay whole (<c>DDDIFMT</c>), camelCase splits
    /// (<c>IoPriority</c> -> <c>Io</c>, <c>Priority</c>), digit runs are their own word, and
    /// underscore runs are captured so cut points can be identified.
    /// </summary>
    ///
    private static readonly Regex Tokenizer = TokenizerRegex();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+|_+", RegexOptions.Compiled)]
    private static partial Regex TokenizerRegex();

    /// <summary>
    /// Members of the emitted enum shape that a stripped constant could shadow,
    /// notably built-in members like Prototype, Ptr, Size.
    /// </summary>
    private static readonly FrozenSet<string> ShadowedMembers = FrozenSet.ToFrozenSet(
        ["value", "__value", "__New", "__Class", "Prototype", "Base", "Call", "Clone", "Ptr", "Size"],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ILogger<EnumPrefixStripper> _logger = logger;

    /// <summary>
    /// Rename enum constants across the registry. <paramref name="overrides"/> supplies the
    /// <c>enum-prefix</c> escape hatch.
    /// </summary>
    public void Apply(TypeRegistry registry, OverrideSet overrides)
    {
        int renamedEnums = 0;
        int renamedConstants = 0;
        int vetoed = 0;
        int kept = 0;

        foreach (EnumType enumType in registry.GetAll<EnumType>())
        {
            string? prefixOverride = overrides.GetOverride(enumType.FQN)?.EnumPrefix;

            if (string.Equals(prefixOverride, KeepSentinel, StringComparison.OrdinalIgnoreCase))
            {
                kept++;
                continue;
            }

            string[] candidates = new string[enumType.Constants.Count];
            for (int i = 0; i < enumType.Constants.Count; i++)
            {
                ConstantMember constant = enumType.Constants[i];
                candidates[i] = prefixOverride is null
                    ? StripTypeNamePrefix(constant.Name, enumType.Name)
                    : StripLiteralPrefix(constant.Name, prefixOverride);
            }

            if (!Validate(enumType, candidates, out string? reason))
            {
                _logger.LogWarning("Not stripping enum prefix for {FQN}: {Reason}", enumType.FQN, reason);
                vetoed++;
                continue;
            }

            int changed = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                ConstantMember constant = enumType.Constants[i];
                if (string.Equals(candidates[i], constant.Name, StringComparison.Ordinal))
                    continue;

                _logger.LogTrace("{FQN}: {Old} -> {New}", enumType.FQN, constant.Name, candidates[i]);
                constant.NativeName = constant.Name;
                constant.Name = candidates[i];
                changed++;
            }

            if (changed > 0)
            {
                renamedEnums++;
                renamedConstants += changed;
            }
        }

        _logger.LogInformation(
            "Stripped enum prefixes: {Constants} constant(s) across {Enums} enum(s), "
                + "{Vetoed} enum(s) left alone due to collisions, {Kept} via enum-prefix: keep",
            renamedConstants,
            renamedEnums,
            vetoed,
            kept
        );
    }

    /// <summary>
    /// Rejects the whole enum if any two candidates collide (case-insensitively, as AHK
    /// identifiers are) or if a candidate would shadow a member of the emitted enum shape.
    /// </summary>
    private static bool Validate(EnumType enumType, string[] candidates, out string? reason)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];

            // A name we didn't touch can't newly shadow anything - it was already being emitted.
            // Only vet the ones we're actually changing.
            bool isRenamed = !string.Equals(candidate, enumType.Constants[i].Name, StringComparison.Ordinal);

            if (isRenamed && ShadowedMembers.Contains(candidate))
            {
                reason = $"'{enumType.Constants[i].Name}' would shadow enum member '{candidate}'";
                return false;
            }

            if (!seen.Add(candidate))
            {
                reason = $"'{enumType.Constants[i].Name}' would collide with another constant as '{candidate}'";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Strips from <paramref name="constantName"/> the longest leading run of words that occurs,
    /// in order, among the words of <paramref name="typeName"/>. Returns the original name when
    /// nothing can be stripped or when stripping would consume the name entirely.
    /// </summary>
    internal static string StripTypeNamePrefix(string constantName, string typeName)
    {
        string[] typeWords = Words(typeName);
        if (typeWords.Length == 0)
            return constantName;

        string[] tokens = Tokenize(constantName);

        // Indices of the word tokens (skipping the underscore-run tokens).
        List<int> wordAt = [];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i][0] != '_')
                wordAt.Add(i);
        }

        // Longest leading run of the constant's words that is an in-order subsequence of the
        // type's words. Subsequence rather than contiguous prefix: the type name may carry extra
        // leading context, e.g. WinHttpRequest|AutoLogonPolicy vs AutoLogonPolicy_Always.
        int matched = 0;
        for (int k = wordAt.Count; k > 0; k--)
        {
            int j = 0;
            foreach (string typeWord in typeWords)
            {
                if (j < k && string.Equals(tokens[wordAt[j]], typeWord, StringComparison.OrdinalIgnoreCase))
                    j++;
            }

            if (j == k)
            {
                matched = k;
                break;
            }
        }

        // Constant words match the type exactly - no suffix to keep, don't touch it
        // E.g. ACTIVITY_STATE_COUNT.ActivityStateCount
        if (matched == wordAt.Count)
            return constantName;

        // Back off until the cut lands on a legal boundary. Without this, D3DDDIFORMAT matches
        // D,3 in D3DDDIFMT_UNKNOWN and we would emit the garbage name DDDIFMT_UNKNOWN.
        while (matched > 0 && !IsLegalCut(tokens, wordAt[matched - 1] + 1))
            matched--;

        if (matched == 0)
            return constantName;

        string stripped = string.Concat(tokens[(wordAt[matched - 1] + 1)..]).TrimStart('_');
        return stripped.Length == 0 ? constantName : stripped;
    }

    /// <summary>
    /// Strips an explicit prefix supplied by an <c>enum-prefix</c> override, plus any underscore
    /// separator that follows it. Case-insensitive; returns the original name if it doesn't match
    /// or if nothing would remain.
    /// </summary>
    internal static string StripLiteralPrefix(string constantName, string prefix)
    {
        if (prefix.Length == 0 || !constantName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return constantName;

        string stripped = constantName[prefix.Length..].TrimStart('_');
        return stripped.Length == 0 ? constantName : stripped;
    }

    /// <summary>
    /// A cut is legal at an underscore boundary, or at a camelCase boundary. This prevents slicing
    /// an all-caps run (<c>D3DDDIFMT</c>) or splitting a word off a digit group mid-token.
    /// </summary>
    private static bool IsLegalCut(string[] tokens, int cutIndex)
    {
        if (cutIndex >= tokens.Length)
            return false;

        if (tokens[cutIndex][0] == '_')
            return true;

        if (tokens[cutIndex - 1][0] == '_')
            return true;

        // camelCase boundary
        string next = tokens[cutIndex];
        return next.Length >= 2 && char.IsUpper(next[0]) && char.IsLower(next[1]);
    }

    private static string[] Tokenize(string name) => [.. Tokenizer.Matches(name).Select(m => m.Value)];

    private static string[] Words(string name) =>
        [.. Tokenizer.Matches(name).Select(m => m.Value).Where(t => t[0] != '_')];
}
