namespace TestTrace.Scenarios.Tests;

/// <summary>One source edit applied to the sample solution. Anchors must match
/// exactly once; a stale anchor fails the scenario loudly rather than silently
/// testing nothing.</summary>
public sealed record Edit(string File, string Find, string Replace);

public sealed record Scenario
{
    public required string Name { get; init; }
    public required Edit[] Edits { get; init; }

    /// <summary>Which framework this scenario is analysed under. A run targets one
    /// framework, so a scenario editing xUnit or TUnit sources must say so.</summary>
    public string Framework { get; init; } = "nunit";

    /// <summary>Every pattern must match at least one selected test.</summary>
    public string[] MustInclude { get; init; } = [];

    /// <summary>No selected test may match any of these ("*" means select nothing).
    /// This is what stops the tool degenerating into "always run everything".</summary>
    public string[] MustNotInclude { get; init; } = [];

    /// <summary>The tool must report RUN_EVERYTHING.</summary>
    public bool ExpectRunEverything { get; init; }

    /// <summary>Semantically affected tests the static approach cannot reach.
    /// Asserted ABSENT, so the documented limitation stays visible.</summary>
    public string[] KnownMiss { get; init; } = [];

    /// <summary>Under the PDB front-end this scenario deviates from its manifest-mode
    /// expectations (line-based detection over-approximates). Asserted to still
    /// deviate, so the degradation cannot silently disappear.</summary>
    public bool PdbDeviates { get; init; }

    public override string ToString() => Name;
}

public static class Scenarios
{
    private const string OrderService = "SampleApp.Services/OrderService.cs";
    private const string OrderTests = "SampleApp.Services.Tests/OrderServiceTests.cs";
    private const string XunitPricingTests = "SampleApp.Xunit.Tests/OrderPricingTests.cs";
    private const string XunitCatalogTests = "SampleApp.Xunit.Tests/CatalogFixtureTests.cs";
    private const string TUnitDeliveryTests = "SampleApp.TUnit.Tests/DeliveryTests.cs";
    private const string MsTestOrderTests = "SampleApp.MSTest.Tests/OrderTotalTests.cs";
    private const string MsTestAssemblyHooks = "SampleApp.MSTest.Tests/AssemblyHooks.cs";

    public static readonly Scenario[] All =
    [
        new()
        {
            Name = "comment-only-change",
            Edits =
            [
                new(OrderService,
                    """        var total = Money.Zero("GBP");""",
                    """
                            // Totals are always in GBP for now.
                            var total = Money.Zero("GBP");
                    """.TrimEnd()),
            ],
            MustNotInclude = ["*"],
            // A comment inside a method body falls inside that method's line span,
            // so the PDB front-end selects its tests. Inherent, and safe.
            PdbDeviates = true,
        },
        new()
        {
            Name = "single-method-body-change",
            Edits =
            [
                new(OrderService,
                    """
                            foreach (var line in order.Lines)
                                total += line.UnitPrice.Times(line.Quantity);
                    """.TrimEnd(),
                    """
                            foreach (var line in order.Lines)
                            {
                                if (line.Quantity != 0)
                                    total += line.UnitPrice.Times(line.Quantity);
                            }
                    """.TrimEnd()),
            ],
            // A run targets one framework, so this scenario asserts only its own.
            // That one changed method reaches ALL frameworks is asserted separately by
            // MixedSolution_EachFrameworkSeesItsOwnTests, which analyses one build
            // three times.
            MustInclude = ["*.OrderServiceTests.*", "*.OrdersControllerTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "add-unrelated-method",
            Edits =
            [
                new(OrderService,
                    "    public IReadOnlyList<string> DescribeLines(Order order) =>",
                    """
                        public int CountLines(Order order)
                        {
                            var count = 0;
                            foreach (var _ in order.Lines)
                                count++;
                            return count;
                        }

                        public IReadOnlyList<string> DescribeLines(Order order) =>
                    """.TrimEnd()),
            ],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "interface-impl-change",
            Edits =
            [
                new(OrderService,
                    """        var summary = $"{order.Lines.Count} line(s) for {StringUtils.Normalise(order.Customer.Name)}";""",
                    """        var summary = $"{order.Lines.Count} order line(s) for {StringUtils.Normalise(order.Customer.Name)}";"""),
            ],
            MustInclude = ["*.OrdersControllerTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "new-test-added",
            Edits =
            [
                new(OrderTests,
                    "    private static IEnumerable<TestCaseData> TotalCases()",
                    """
                        [Test]
                        public void CalculateTotal_EmptyOrder_IsZero_NewCheck()
                        {
                            var empty = new Order { Customer = new Customer(9, "Nobody", "n@example.com") };
                            Assert.That(_service.CalculateTotal(empty).Amount, Is.EqualTo(0m));
                        }

                        private static IEnumerable<TestCaseData> TotalCases()
                    """.TrimEnd()),
            ],
            MustInclude = ["*.OrderServiceTests.CalculateTotal_EmptyOrder_IsZero_NewCheck"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "shared-util-change",
            Edits =
            [
                new("SampleApp.Common/StringUtils.cs",
                    "        return string.Join(' ', parts);",
                    """        return string.Join(" ", parts);"""),
            ],
            MustInclude =
            [
                "*.OrderServiceTests.*", "*.CustomerServiceTests.*",
                "*.OrdersControllerTests.*", "*.CustomersControllerTests.*",
            ],
        },
        new()
        {
            Name = "lambda-change",
            Edits =
            [
                new(OrderService,
                    """            .Select(line => $"{line.Quantity} x {StringUtils.Normalise(line.Sku)}")""",
                    """            .Select(line => $"{line.Quantity} x {StringUtils.Normalise(line.Sku)}".Trim())"""),
            ],
            MustInclude = ["*.OrderServiceTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "async-method-change",
            Edits =
            [
                new(OrderService,
                    "        if (order.Lines.Count > 5)",
                    "        if (order.Lines.Count >= 6)"),
            ],
            MustInclude = ["*.OrderServiceTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "record-property-added",
            Edits =
            [
                new("SampleApp.Domain/Customer.cs",
                    "public record Customer(int Id, string Name, string Email);",
                    "public record Customer(int Id, string Name, string Email, string? Phone = null);"),
            ],
            MustInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "setup-change",
            Edits =
            [
                new(OrderTests,
                    """            Customer = new Customer(1, "  Ada   Lovelace ", "ada@example.com"),""",
                    """            Customer = new Customer(1, "  Ada   Lovelace ", "ada.lovelace@example.com"),"""),
            ],
            MustInclude = ["*.OrderServiceTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            Name = "testcasesource-change",
            Edits =
            [
                new(OrderTests,
                    """        yield return new TestCaseData(new[] { ("A", 2, 2.50m), ("B", 1, 1.00m) }, 6.00m);""",
                    """
                            yield return new TestCaseData(new[] { ("A", 2, 2.50m), ("B", 1, 1.00m) }, 6.00m);
                            yield return new TestCaseData(new[] { ("C", 3, 0.50m) }, 1.50m);
                    """.TrimEnd()),
            ],
            MustInclude = ["*.OrderServiceTests.CalculateTotal_FromCases*"],
        },
        new()
        {
            Name = "config-change",
            Edits =
            [
                new("SampleApp.Api/appsettings.json",
                    """    "FreeDeliveryThreshold": 50""",
                    """    "FreeDeliveryThreshold": 75"""),
            ],
            ExpectRunEverything = true,
        },
        new()
        {
            Name = "attribute-change",
            Edits =
            [
                new("SampleApp.Api/Controllers/OrdersController.cs",
                    "using Microsoft.AspNetCore.Mvc;",
                    "using Microsoft.AspNetCore.Authorization;\nusing Microsoft.AspNetCore.Mvc;"),
                new("SampleApp.Api/Controllers/OrdersController.cs",
                    """    [HttpPost("estimate")]""",
                    """
                        [Authorize]
                        [HttpPost("estimate")]
                    """.TrimEnd()),
            ],
            MustInclude = ["*.OrdersControllerTests.*"],
            // Attributes carry no sequence points, so a line-range intersection only
            // finds this if the diff's context lines happen to reach into the method
            // body. With exact ranges (git diff -U0) the PDB front-end misses it —
            // a real limitation of line-based detection, not an accident.
            PdbDeviates = true,
        },
        new()
        {
            // xUnit has no [SetUp]: it builds a fresh instance per test, so the
            // constructor is the per-test setup and a change in it impacts every test
            // in the class — including the ones that never touch the edited line.
            Name = "xunit-constructor-is-setup",
            Framework = "xunit",
            Edits =
            [
                new(XunitPricingTests,
                    """        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(10.00m, "GBP")));""",
                    """        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(11.00m, "GBP")));"""),
            ],
            MustInclude = ["*.OrderPricingTests.*"],
            MustNotInclude = ["*.CatalogFixtureTests.*"],
        },
        new()
        {
            // IClassFixture<T> is constructed by the runner, never by a call site here.
            // Only the fixture's own constructor changes, so nothing but the explicit
            // fixture edge can connect it to the tests.
            Name = "xunit-classfixture-constructor-change",
            Framework = "xunit",
            Edits =
            [
                new(XunitCatalogTests,
                    """            new OrderLine("BOOK-2", 2, new Money(3.00m, "GBP")),""",
                    """            new OrderLine("BOOK-2", 3, new Money(3.00m, "GBP")),"""),
            ],
            MustInclude = ["*.CatalogFixtureTests.*"],
            MustNotInclude = ["*.OrderPricingTests.*"],
        },
        new()
        {
            // [MemberData] provider: the consumers must run when the data changes.
            Name = "xunit-memberdata-change",
            Framework = "xunit",
            Edits =
            [
                new(XunitPricingTests,
                    "        yield return [6, 3];",
                    """
                            yield return [6, 3];
                            yield return [9, 3];
                    """.TrimEnd()),
            ],
            MustInclude = ["*.OrderPricingTests.EstimateDeliveryDays_DependsOnLineCount*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            // TUnit's lifecycle is [Before(Test)], not a constructor or [SetUp]. A
            // change in one must select every test in the class.
            Name = "tunit-before-hook-change",
            Framework = "tunit",
            Edits =
            [
                new(TUnitDeliveryTests,
                    "    public void Setup() => _service = new OrderService();",
                    """
                        public void Setup()
                        {
                            _service = new OrderService();
                        }
                    """.TrimEnd()),
            ],
            MustInclude = ["*.DeliveryTests.*"],
            MustNotInclude = ["*.CustomerServiceTests.*"],
        },
        new()
        {
            // [Arguments] is TUnit's inline data. Changing a row must re-run the test
            // it feeds, and nothing in the other TUnit class.
            Name = "tunit-arguments-change",
            Framework = "tunit",
            Edits =
            [
                new(TUnitDeliveryTests,
                    "    [Arguments(6, 3)]",
                    "    [Arguments(7, 3)]"),
            ],
            MustInclude = ["*.DeliveryTests.DeliveryDays_DependOnLineCount"],
            MustNotInclude = ["*.TUnit.Tests.PricingTests.*"],
            // Inline data lives in an ATTRIBUTE, and attributes carry no sequence
            // points, so the PDB front-end's line intersection finds nothing — the
            // same limitation the attribute-change scenario pins down. The manifest
            // front-end sees it, because attributes are part of the method's hash.
            PdbDeviates = true,
        },
        new()
        {
            // [MethodDataSource] provider: its consumers must run when the data moves.
            Name = "tunit-methoddatasource-change",
            Framework = "tunit",
            Edits =
            [
                new(TUnitDeliveryTests,
                    "        yield return 4;",
                    """
                            yield return 4;
                            yield return 5;
                    """.TrimEnd()),
            ],
            MustInclude = ["*.DeliveryTests.ModestOrders_StillTakeTwoDays"],
            MustNotInclude = ["*.TUnit.Tests.PricingTests.*"],
        },
        new()
        {
            // MSTest's per-test setup is [TestInitialize]; a change there impacts every
            // test in the class, including ones that never touch the edited line.
            Name = "mstest-testinitialize-change",
            Framework = "mstest",
            Edits =
            [
                new(MsTestOrderTests,
                    """        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(10.00m, "GBP")));""",
                    """        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(11.00m, "GBP")));"""),
            ],
            MustInclude = ["*.OrderTotalTests.*"],
            // Fixture-scoped, so it must NOT reach the other class in this assembly.
            MustNotInclude = ["*.DeliveryEstimateTests.*"],
        },
        new()
        {
            // [DynamicData] provider: its consumers must run when the data changes.
            Name = "mstest-dynamicdata-change",
            Framework = "mstest",
            Edits =
            [
                new(MsTestOrderTests,
                    "        yield return [6, 3];",
                    """
                            yield return [6, 3];
                            yield return [9, 3];
                    """.TrimEnd()),
            ],
            MustInclude = ["*.OrderTotalTests.EstimateDeliveryDays_DependsOnLineCount"],
        },
        new()
        {
            // [AssemblyInitialize] runs for every test in the assembly. Scoping it to
            // its declaring class — as every lifecycle method used to be — selected
            // only AssemblyHooks' own tests (of which there are none) and silently
            // dropped the rest. Both other classes must come back.
            Name = "mstest-assemblyinitialize-change",
            Framework = "mstest",
            Edits =
            [
                new(MsTestAssemblyHooks,
                    """        Banner = StringUtils.Normalise("  mstest   suite  ");""",
                    """        Banner = StringUtils.Normalise("  mstest   suite  ").Trim();"""),
            ],
            MustInclude = ["*.OrderTotalTests.*", "*.DeliveryEstimateTests.*"],
        },
        new()
        {
            Name = "reflection-call",
            Edits =
            [
                new("SampleApp.Reflection/ReflectionTarget.cs",
                    """    public static string Describe(string input) => $"described:{input.Trim()}";""",
                    """    public static string Describe(string input) => $"described:{input.Trim(' ', '\t')}";"""),
            ],
            // Semantically affected, but MethodInfo.Invoke leaves no static edge.
            KnownMiss = ["*.ReflectionTests.*"],
        },
    ];
}
