using System.Text.RegularExpressions;

namespace TestTrace.Core;

/// <summary>
/// Roslyn's generated-member naming, and the normalization that keeps hashes stable.
///
/// Generated names embed ordinals that shift when UNRELATED members are added
/// (&lt;M&gt;d__4 becomes &lt;M&gt;d__5, &lt;&gt;c__DisplayClass4_0 becomes 5_0, b__3_0
/// becomes b__4_0). Those names appear in operands (newobj, ldftn, ldsfld) and in
/// state-machine attributes, so without normalization adding one method makes every
/// lambda- or async-containing method in the type look changed.
/// The per-method ordinal (the part after the underscore) is stable; only the
/// leading member ordinal is replaced with '#'.
/// </summary>
public static partial class GeneratedNames
{
    [GeneratedRegex(@"^<(?<owner>[^>]+)>[bg]__")]
    private static partial Regex MemberOwnerPattern();

    [GeneratedRegex(@"^<(?<owner>[^>]+)>d__[0-9]+")]
    private static partial Regex StateMachineTypePattern();

    [GeneratedRegex(@"(?<=>b__)[0-9]+_")]
    private static partial Regex LambdaOrdinal();

    [GeneratedRegex(@"(?<=>d__)[0-9]+")]
    private static partial Regex StateMachineOrdinal();

    [GeneratedRegex(@"(?<=c__DisplayClass)[0-9]+_")]
    private static partial Regex DisplayClassOrdinal();

    [GeneratedRegex(@"(?<=<>9__)[0-9]+_")]
    private static partial Regex DelegateCacheOrdinal();

    [GeneratedRegex(@"(?<=g__[A-Za-z0-9_]{1,200}\|)[0-9]+_")]
    private static partial Regex LocalFunctionOrdinal();

    /// <summary>Owner method name for lambda/local-function members (&lt;M&gt;b__*, &lt;M&gt;g__*).</summary>
    public static string? OwnerFromMemberName(string name)
    {
        var match = MemberOwnerPattern().Match(name);
        return match.Success ? match.Groups["owner"].Value : null;
    }

    /// <summary>Owner method name for async/iterator state machine types (&lt;M&gt;d__N).</summary>
    public static string? OwnerFromTypeName(string name)
    {
        var match = StateMachineTypePattern().Match(name);
        return match.Success ? match.Groups["owner"].Value : null;
    }

    public static bool IsGeneratedTypeName(string name) => name.StartsWith('<');

    /// <summary>
    /// Replace shift-prone member ordinals in rendered names/text with '#'.
    ///
    /// Applied to whole canonical method text, string-literal operands included, so a
    /// literal containing something shaped like a generated name (">d__1") is
    /// normalized too. That can only mask a change, never invent one, and it needs a
    /// literal that both looks like compiler output and differs only in that ordinal —
    /// accepted rather than paying for operand-aware normalization.
    /// </summary>
    public static string NormalizeOrdinals(string text)
    {
        text = LambdaOrdinal().Replace(text, "#_");
        text = StateMachineOrdinal().Replace(text, "#");
        text = DisplayClassOrdinal().Replace(text, "#_");
        text = DelegateCacheOrdinal().Replace(text, "#_");
        text = LocalFunctionOrdinal().Replace(text, "#_");
        return text;
    }
}
