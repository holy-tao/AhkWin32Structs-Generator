namespace AhkWin32.Generator.Tests;

using System.Collections.Frozen;
using AhkWin32.Generator.Model;
using AhkWin32.Generator.Model.Types;
using AhkWin32.Generator.Tests.Support;
using AhkWin32.Generator.Transform;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class EnumPrefixStripperTests
{
    /// <summary>
    /// Run the stripper over a single enum and return its constant names in order.
    /// <paramref name="overrides"/> defaults to empty.
    /// </summary>
    private static string[] Strip(EnumType enumType, OverrideSet? overrides = null)
    {
        var registry = new TypeRegistry();
        registry.Register(enumType);

        new EnumPrefixStripper(NullLogger<EnumPrefixStripper>.Instance).Apply(registry, overrides ?? OverrideSet.Empty);

        return [.. enumType.Constants.Select(c => c.Name)];
    }

    private static OverrideSet Overrides(string fqn, string enumPrefix) =>
        new(
            new Dictionary<string, TypeOverride>
            {
                [fqn] = new TypeOverride(
                    FQN: fqn,
                    Skip: false,
                    StructSizeField: null,
                    Fields: null,
                    Methods: null,
                    AddMethods: null,
                    ValueAccessor: null,
                    EnumPrefix: enumPrefix
                ),
            }.ToFrozenDictionary()
        );

    [TestMethod]
    public void UnderscorePrefix_Stripped()
    {
        // The motivating case: the type name carries extra leading context (WinHttpRequest), so
        // the match has to be a subsequence of the type's words, not a contiguous prefix.
        var e = Ir.Enum(
            "Test.WinHttpRequestAutoLogonPolicy",
            "AutoLogonPolicy_Always",
            "AutoLogonPolicy_OnlyIfBypassProxy",
            "AutoLogonPolicy_Never"
        );

        CollectionAssert.AreEqual(new[] { "Always", "OnlyIfBypassProxy", "Never" }, Strip(e));
    }

    [TestMethod]
    public void CamelCasePrefix_Stripped()
    {
        var e = Ir.Enum("Test.IO_PRIORITY_HINT", "IoPriorityVeryLow", "IoPriorityLow", "IoPriorityNormal");

        CollectionAssert.AreEqual(new[] { "VeryLow", "Low", "Normal" }, Strip(e));
    }

    [TestMethod]
    public void SentinelSharingNoPrefix_KeepsFullName()
    {
        // MaxIoPriorityTypes shares no leading word with the type name. Stripping is per-constant,
        // so it keeps its name while its siblings shorten - one outlier must not veto the enum.
        var e = Ir.Enum("Test.IO_PRIORITY_HINT", "IoPriorityVeryLow", "IoPriorityCritical", "MaxIoPriorityTypes");

        CollectionAssert.AreEqual(new[] { "VeryLow", "Critical", "MaxIoPriorityTypes" }, Strip(e));
    }

    [TestMethod]
    public void AllCapsRun_NotSplitMidWord()
    {
        // D3DDDIFORMAT tokenizes as D|3|DDDIFORMAT, which matches D,3 in D3DDDIFMT_UNKNOWN.
        // Without the cut-boundary rule this would emit the garbage name "DDDIFMT_UNKNOWN".
        var e = Ir.Enum("Test.D3DDDIFORMAT", "D3DDDIFMT_UNKNOWN", "D3DDDIFMT_R8G8B8");

        CollectionAssert.AreEqual(new[] { "D3DDDIFMT_UNKNOWN", "D3DDDIFMT_R8G8B8" }, Strip(e));
    }

    [TestMethod]
    public void ConstantIsEntirelyThePrefix_Unchanged()
    {
        // Stripping would leave nothing at all, so the original name stands.
        var e = Ir.Enum("Test.CRYPT_TIMESTAMP_VERSION", "TIMESTAMP_VERSION");

        CollectionAssert.AreEqual(new[] { "TIMESTAMP_VERSION" }, Strip(e));
    }

    [TestMethod]
    public void SingleConstantWithNoSharedWords_Unchanged()
    {
        // No member-count gate is needed: WTA is not a word of the type name, so nothing matches.
        var e = Ir.Enum("Test.WINDOWTHEMEATTRIBUTETYPE", "WTA_NONCLIENT");

        CollectionAssert.AreEqual(new[] { "WTA_NONCLIENT" }, Strip(e));
    }

    [TestMethod]
    public void LeadingDigitResult_Allowed()
    {
        // AHK property names may begin with a digit, and D3D_FEATURE_LEVEL.11_0 was verified to
        // lex on both v2.0.26 and v2.1-alpha.30.
        var e = Ir.Enum("Test.D3D_FEATURE_LEVEL", "D3D_FEATURE_LEVEL_11_0", "D3D_FEATURE_LEVEL_9_1");

        CollectionAssert.AreEqual(new[] { "11_0", "9_1" }, Strip(e));
    }

    [TestMethod]
    public void CollidingResults_WholeEnumLeftAlone()
    {
        // HALFTONE and STRETCH_HALFTONE both reduce to HALFTONE. A half-stripped enum is worse
        // than an unstripped one, so every constant keeps its name.
        var e = Ir.Enum("Test.STRETCH_BLT_MODE", "BLACKONWHITE", "HALFTONE", "STRETCH_HALFTONE");

        CollectionAssert.AreEqual(new[] { "BLACKONWHITE", "HALFTONE", "STRETCH_HALFTONE" }, Strip(e));
    }

    [TestMethod]
    public void ResultIsAnAhkReservedWord_StillStripped()
    {
        // Enum constants become static properties, which per the AHK docs may be reserved words.
        // Verified on v2.0.26 and v2.1-alpha.30: static ERROR / LOOP / Object all parse and leave
        // the global classes untouched. Vetoing on ahk-reserved-names.yml would needlessly skip
        // ~275 real enums.
        var e = Ir.Enum("Test.THING_KIND", "THING_KIND_OBJECT", "THING_KIND_ERROR", "THING_KIND_LOOP");

        CollectionAssert.AreEqual(new[] { "OBJECT", "ERROR", "LOOP" }, Strip(e));
    }

    [TestMethod]
    public void ResultShadowsEnumShapeMember_WholeEnumLeftAlone()
    {
        // "value" is the v2.1 struct's backing field, and a static SIZE genuinely does override the
        // struct's built-in Size used for marshalling. Real shadowing, unlike a bare keyword.
        var e = Ir.Enum("Test.THING_KIND", "THING_KIND_VALUE", "THING_KIND_WIDGET");

        CollectionAssert.AreEqual(new[] { "THING_KIND_VALUE", "THING_KIND_WIDGET" }, Strip(e));
    }

    [TestMethod]
    public void PreexistingShadowingName_DoesNotVetoTheEnum()
    {
        // "Size" here is not produced by us - it was already being emitted before this pass
        // existed. Only names the pass actually changes get vetted.
        var e = Ir.Enum("Test.THING_KIND", "Size", "THING_KIND_WIDGET");

        CollectionAssert.AreEqual(new[] { "Size", "WIDGET" }, Strip(e));
    }

    [TestMethod]
    public void EnumPrefixKeep_DisablesStripping()
    {
        var e = Ir.Enum("Test.THING_KIND", "THING_KIND_ALPHA", "THING_KIND_BETA");

        CollectionAssert.AreEqual(
            new[] { "THING_KIND_ALPHA", "THING_KIND_BETA" },
            Strip(e, Overrides("Test.THING_KIND", EnumPrefixStripper.KeepSentinel))
        );
    }

    [TestMethod]
    public void EnumPrefixLiteral_StripsThatPrefixInstead()
    {
        // An explicit prefix overrides the heuristic entirely, including for constants the
        // heuristic would not have touched.
        var e = Ir.Enum("Test.THING_KIND", "TK_ALPHA", "TK_BETA", "OTHER_GAMMA");

        CollectionAssert.AreEqual(
            new[] { "ALPHA", "BETA", "OTHER_GAMMA" },
            Strip(e, Overrides("Test.THING_KIND", "TK"))
        );
    }

    [TestMethod]
    public void RenamedConstant_RecordsNativeName()
    {
        var e = Ir.Enum("Test.THING_KIND", "THING_KIND_ALPHA", "OTHER");
        Strip(e);

        Assert.AreEqual("ALPHA", e.Constants[0].Name);
        Assert.AreEqual("THING_KIND_ALPHA", e.Constants[0].NativeName);

        // Untouched constants must not gain a redundant "Native name:" doc line.
        Assert.AreEqual("OTHER", e.Constants[1].Name);
        Assert.IsNull(e.Constants[1].NativeName);
    }

    [TestMethod]
    public void ArchitectureVariants_BothStripped()
    {
        // Variants are separate instances under the same FQN; GetAll<EnumType>() yields each one,
        // so both must be renamed or the two architectures diverge.
        var x86 = Ir.Enum("Test.THING_KIND", "THING_KIND_ALPHA");
        var x64 = Ir.Enum("Test.THING_KIND", "THING_KIND_BETA");

        var registry = new TypeRegistry();
        registry.Register(WithArch(x86, Architecture.X86));
        registry.Register(WithArch(x64, Architecture.X64));

        new EnumPrefixStripper(NullLogger<EnumPrefixStripper>.Instance).Apply(registry, OverrideSet.Empty);

        CollectionAssert.AreEqual(
            new[] { "ALPHA", "BETA" },
            registry.GetAllVariants<EnumType>("Test.THING_KIND").Select(e => e.Constants[0].Name).Order().ToArray()
        );
    }

    private static EnumType WithArch(EnumType source, Architecture arch) =>
        new()
        {
            Identity = new TypeIdentity(source.FQN, arch),
            Name = source.Name,
            CanonicalName = source.CanonicalName,
            AssemblyName = source.AssemblyName,
            MetadataVersion = source.MetadataVersion,
            Constants = source.Constants,
            IsFlags = source.IsFlags,
            UnderlyingTypeName = source.UnderlyingTypeName,
        };
}
