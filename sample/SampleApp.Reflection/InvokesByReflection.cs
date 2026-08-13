using System.Reflection;

namespace SampleApp.Reflection;

/// <summary>
/// Deliberate blind spot for testtrace: the call to ReflectionTarget.Describe goes
/// through MethodInfo.Invoke, so no static call edge exists.
/// </summary>
public static class InvokesByReflection
{
    public static string Describe(string input)
    {
        var method = typeof(ReflectionTarget).GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException(nameof(ReflectionTarget), "Describe");
        return (string)method.Invoke(null, [input])!;
    }
}
