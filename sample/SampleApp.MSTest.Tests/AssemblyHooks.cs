using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleApp.Common;

namespace SampleApp.MSTest.Tests;

/// <summary>
/// [AssemblyInitialize] runs once for EVERY test in this assembly, including those in
/// classes that never mention it. A change in here must therefore select the whole
/// assembly's tests, not just this class's — which is what scoping it to the declaring
/// fixture used to get wrong.
/// </summary>
[TestClass]
public static class AssemblyHooks
{
    internal static string Banner { get; private set; } = "";

    [AssemblyInitialize]
    public static void BeforeAll(TestContext context)
    {
        Banner = StringUtils.Normalise("  mstest   suite  ");
    }
}
