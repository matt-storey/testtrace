using Mono.Cecil;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

// Deliberately exercises every compiler-generated member shape the hasher must
// fold back onto its owner: lambdas (capturing and non-capturing), async and
// iterator state machines, and local functions.
internal class HasherFixture
{
    private readonly int _offset = 3;

    public int PlainAdd(int a, int b) => a + b;

    public IEnumerable<int> WithLambdas(IEnumerable<int> values) =>
        values.Where(v => v > 0).Select(v => v + _offset);

    public async Task<int> WithAwait(int value)
    {
        await Task.Yield();
        return value * 2;
    }

    public IEnumerable<int> WithIterator(int count)
    {
        for (var i = 0; i < count; i++)
            yield return i;
    }

    public int WithLocalFunction(int value)
    {
        return Double(value) + 1;
        static int Double(int v) => v * 2;
    }
}

[TestFixture]
public class MethodHasherTests
{
    private static List<MethodEntry> HashOwnAssembly()
    {
        using var module = ModuleDefinition.ReadModule(
            typeof(MethodHasherTests).Assembly.Location,
            new ReaderParameters(ReadingMode.Deferred));
        return MethodHasher.HashModule(module);
    }

    [Test]
    public void GeneratedMembers_AreAttributed_NotStandalone()
    {
        var entries = HashOwnAssembly();
        var generated = entries
            .Where(e => e.Fqn.Contains(">b__") || e.Fqn.Contains(">d__") || e.Fqn.Contains(">g__"))
            .Select(e => e.Fqn)
            .ToList();

        Assert.That(generated, Is.Empty,
            "lambda/state-machine/local-function members must fold into their owner's hash");
    }

    [Test]
    public void FixtureMethods_AllHaveEntries()
    {
        var fqns = HashOwnAssembly().Select(e => e.Fqn).ToList();
        Assert.Multiple(() =>
        {
            foreach (var name in new[] { "PlainAdd", "WithLambdas", "WithAwait", "WithIterator", "WithLocalFunction" })
                Assert.That(fqns.Any(f => f.Contains($"HasherFixture::{name}(")), Is.True, name);
            Assert.That(fqns, Has.Member($"{typeof(HasherFixture).FullName}{MethodHasher.TypeEntrySuffix}"));
        });
    }

    [Test]
    public void Hashes_AreStableAcrossReads()
    {
        var first = HashOwnAssembly();
        var second = HashOwnAssembly();

        Assert.That(
            second.Select(e => (e.Fqn, e.Hash)),
            Is.EqualTo(first.Select(e => (e.Fqn, e.Hash))));
    }

    [Test]
    public void OrdinalNormalization_StripsShiftProneNumbers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                GeneratedNames.NormalizeOrdinals("m:System.Void T/<M>d__4::MoveNext()"),
                Is.EqualTo("m:System.Void T/<M>d__#::MoveNext()"));
            Assert.That(
                GeneratedNames.NormalizeOrdinals("f:T/<>c__DisplayClass7_0::value"),
                Is.EqualTo("f:T/<>c__DisplayClass#_0::value"));
            Assert.That(
                GeneratedNames.NormalizeOrdinals("m:System.Int32 T/<>c::<M>b__12_1(System.Int32)"),
                Is.EqualTo("m:System.Int32 T/<>c::<M>b__#_1(System.Int32)"));
            Assert.That(
                GeneratedNames.NormalizeOrdinals("f:System.Func`2<System.Int32,System.Int32> T/<>c::<>9__12_0"),
                Is.EqualTo("f:System.Func`2<System.Int32,System.Int32> T/<>c::<>9__#_0"));
            Assert.That(
                GeneratedNames.NormalizeOrdinals("m:System.Int32 T::<M>g__Double|5_0(System.Int32)"),
                Is.EqualTo("m:System.Int32 T::<M>g__Double|#_0(System.Int32)"));
        });
    }

    [Test]
    public void OwnerParsing_CoversMemberAndTypeShapes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedNames.OwnerFromMemberName("<DescribeLines>b__3_0"), Is.EqualTo("DescribeLines"));
            Assert.That(GeneratedNames.OwnerFromMemberName("<WithLocalFunction>g__Double|5_0"), Is.EqualTo("WithLocalFunction"));
            Assert.That(GeneratedNames.OwnerFromMemberName("MoveNext"), Is.Null);
            Assert.That(GeneratedNames.OwnerFromTypeName("<PlaceAsync>d__4"), Is.EqualTo("PlaceAsync"));
            Assert.That(GeneratedNames.OwnerFromTypeName("<>c__DisplayClass4_0"), Is.Null);
            Assert.That(GeneratedNames.OwnerFromTypeName("<>c"), Is.Null);
        });
    }
}
