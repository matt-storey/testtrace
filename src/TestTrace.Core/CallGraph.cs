using System.Collections.Concurrent;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestTrace.Core;

/// <summary>
/// Reverse call graph over every managed assembly in the current build.
///
/// Node keys are overload-collapsed: "Type::Name/ParamCount" (all generic
/// instantiations of a method are one node, and same-name-same-arity overloads
/// merge — a mild extra over-approximation). Type nodes "TN:Type" represent
/// "anything about this type": every member has an edge to its type node, and the
/// type node's dependents are field touchers, generic-type-argument users and (for
/// framework-dispatched types like MVC controllers) the assembly entry point.
///
/// The generic-argument edge is what makes DI resolvable: a controller calls
/// IOrderService::Place, and the only static mention of OrderService is
/// AddSingleton&lt;IOrderService, OrderService&gt;() in Program — the type-arg edge
/// connects them. WebApplicationFactory&lt;Program&gt; in a test connects Program to
/// the test the same way. Deliberately NOT an edge: ldtoken on a type (typeof) —
/// that is the reflection hole, documented, and adding it would hide the known miss.
/// </summary>
public sealed class CallGraphIndex
{
    // No format version here: GraphCache.FormatVersion is the single guard, and it is
    // part of the cache key, so a stale-format entry can never be loaded at all.
    public Dictionary<string, List<string>> Reverse { get; set; } = new(StringComparer.Ordinal);
    public List<TestNode> Tests { get; set; } = [];
    /// <summary>Lifecycle methods and how far each reaches. Keyed by method node key.</summary>
    public Dictionary<string, LifecycleTarget> SetupFixtureByKey { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BaseTypeOf { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> TypeMembers { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> TestKeysByProviderKey { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// [TestCaseSource]/[MemberData]-style links this assembly declared, resolved
    /// against every known method key once the partials are merged. It lives on the
    /// partial rather than in a side channel so that a cached partial is complete —
    /// otherwise a cache hit would silently drop the assembly's data-source links.
    /// </summary>
    public List<ProviderRef> ProviderRefs { get; set; } = [];

    /// <summary>
    /// Names of the frameworks whose marker assemblies are referenced anywhere in
    /// scope, whether or not they were the one discovered. Derived from assembly
    /// references, so it costs a few string compares per assembly — it is what lets
    /// the analyzer say "you asked for tunit but this build has nunit" without running
    /// every detector over every method.
    /// </summary>
    public List<string> FrameworksPresent { get; set; } = [];
}

/// <summary>
/// What a lifecycle method's impact covers. Fixture uses DeclaringType (and types
/// deriving from it); Assembly uses Assembly; Global covers every test in the run.
/// </summary>
public sealed class LifecycleTarget
{
    public TestLifecycleScope Scope { get; set; } = TestLifecycleScope.Fixture;
    public string DeclaringType { get; set; } = "";
    public string Assembly { get; set; } = "";
}

/// <summary>A data-source reference and the test that consumes it.</summary>
public sealed class ProviderRef
{
    public string TypeFullName { get; set; } = "";
    public string MemberName { get; set; } = "";
    public string TestKey { get; set; } = "";
}

public sealed class TestNode
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string DeclaringType { get; set; } = "";

    /// <summary>Parameterized tests ([TestCase], [TestCaseSource], [Theory],
    /// [Arguments]) run with argument lists in their names, so filters must
    /// contains-match (VSTest) or wildcard-suffix (tree paths), not equal-match.</summary>
    public bool Parameterized { get; set; }

    /// <summary>Namespace of the declaring type, for tree-path emission.</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Simple (unqualified) declaring type name, for tree-path emission.</summary>
    public string ClassName { get; set; } = "";
}

public static class MethodKeys
{
    public const string TypeNodePrefix = "TN:";

    public static string ForMethod(string typeFullName, string methodName, int paramCount) =>
        GeneratedNames.NormalizeOrdinals($"{typeFullName}::{methodName}/{paramCount}");

    public static string ForType(string typeFullName) =>
        TypeNodePrefix + GeneratedNames.NormalizeOrdinals(typeFullName);

    public static string ForDefinition(MethodDefinition method) =>
        ForMethod(method.DeclaringType.FullName, method.Name, method.Parameters.Count);

    public static string ForReference(MethodReference reference)
    {
        var element = reference is GenericInstanceMethod generic ? generic.ElementMethod : reference;
        var declaring = element.DeclaringType is GenericInstanceType instance
            ? instance.ElementType
            : element.DeclaringType;
        return ForMethod(declaring.FullName, element.Name, element.Parameters.Count);
    }

    /// <summary>Parse an M2 changed-method fqn ("Ret Type::Name(P1,P2)" or "Type::&lt;type&gt;").</summary>
    public static string FromChangedFqn(string fqn, out string? typeNodeType)
    {
        typeNodeType = null;
        if (fqn.EndsWith(MethodHasher.TypeEntrySuffix, StringComparison.Ordinal))
        {
            typeNodeType = fqn[..^MethodHasher.TypeEntrySuffix.Length];
            return ForType(typeNodeType);
        }

        var body = fqn;
        var space = body.IndexOf(' ');
        if (space >= 0)
            body = body[(space + 1)..];

        var sep = body.IndexOf("::", StringComparison.Ordinal);
        var typeName = sep >= 0 ? body[..sep] : body;
        var rest = sep >= 0 ? body[(sep + 2)..] : "";

        var paren = rest.IndexOf('(');
        var name = paren >= 0 ? rest[..paren] : rest;
        var paramCount = 0;
        if (paren >= 0)
        {
            var inner = rest[(paren + 1)..rest.LastIndexOf(')')];
            if (inner.Length > 0)
            {
                paramCount = 1;
                var depth = 0;
                foreach (var c in inner)
                {
                    if (c is '<' or '[') depth++;
                    else if (c is '>' or ']') depth--;
                    else if (c == ',' && depth == 0) paramCount++;
                }
            }
        }

        return ForMethod(typeName, name, paramCount);
    }
}

public static class CallGraphBuilder
{
    /// <summary>
    /// Builds the graph for ONE test framework, the one the caller chose. Only that
    /// framework's tests are discovered: a run targets a single runner, so finding the
    /// others would only produce a selection that has to be discarded again.
    ///
    /// Which frameworks are present in the build is still recorded (see
    /// FrameworksPresent) — that comes from assembly references, a handful of string
    /// compares per assembly rather than every detector over every method.
    /// </summary>
    public static CallGraphIndex Build(IReadOnlyList<string> assemblyPaths, ITestFrameworkDetector detector) =>
        Build(assemblyPaths, detector, cache: null);

    /// <param name="cache">
    /// When supplied, each assembly's partial graph is cached and reused across builds.
    /// Null disables it, which is what the unit tests use to exercise a clean scan.
    /// </param>
    public static CallGraphIndex Build(
        IReadOnlyList<string> assemblyPaths, ITestFrameworkDetector detector, GraphCacheContext? cache)
    {
        // Assembly name == file name for anything the SDK builds; used to drop edges
        // into the shared framework, which is never part of the diff.
        var inSet = new HashSet<string>(
            assemblyPaths.Select(Path.GetFileNameWithoutExtension).Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);

        var partials = new ConcurrentBag<CallGraphIndex>();

        // Per-assembly caching. Scanning an assembly is the expensive half of a build,
        // and a typical change touches one assembly out of hundreds, so the rest can be
        // reused verbatim — provided the key covers everything the scan reads.
        var identity = cache is null ? null : AssemblyIdentities(assemblyPaths, inSet);
        Parallel.ForEach(assemblyPaths, path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            string? key = null;
            if (cache is not null && identity is not null && identity.Closure.TryGetValue(name, out var closure))
            {
                key = GraphCache.KeyForAssembly(name, identity.Mvids, closure, cache.Scope, detector.Name);
                if (GraphCache.TryLoadPartial(key) is { } cached)
                {
                    partials.Add(cached);
                    return;
                }
            }

            var scanned = ScanAssembly(path, inSet, detector);
            if (scanned is null)
                return;

            partials.Add(scanned);
            if (key is not null)
                GraphCache.TrySavePartial(key, scanned);
        });

        var index = new CallGraphIndex();
        var edgeSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var partial in partials)
        {
            foreach (var (key, callers) in partial.Reverse)
            {
                if (!edgeSets.TryGetValue(key, out var set))
                    edgeSets[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(callers);
            }
            index.Tests.AddRange(partial.Tests);
            index.FrameworksPresent.AddRange(partial.FrameworksPresent);
            foreach (var (k, v) in partial.SetupFixtureByKey) index.SetupFixtureByKey[k] = v;
            foreach (var (k, v) in partial.BaseTypeOf) index.BaseTypeOf[k] = v;
            foreach (var (k, v) in partial.TypeMembers) index.TypeMembers[k] = v;
            index.ProviderRefs.AddRange(partial.ProviderRefs);
        }
        foreach (var (key, set) in edgeSets)
            index.Reverse[key] = set.ToList();

        // Resolve [TestCaseSource] references now that every method key is known.
        foreach (var reference in index.ProviderRefs)
        {
            var members = index.TypeMembers.GetValueOrDefault(reference.TypeFullName) ?? [];
            var candidates = reference.MemberName == "*"
                ? members
                : members.Where(k => KeyNameIs(k, reference.MemberName)).ToList();
            foreach (var providerKey in candidates)
            {
                if (!index.TestKeysByProviderKey.TryGetValue(providerKey, out var list))
                    index.TestKeysByProviderKey[providerKey] = list = [];
                list.Add(reference.TestKey);
            }
        }

        index.Tests.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        index.FrameworksPresent = index.FrameworksPresent
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        return index;

        static bool KeyNameIs(string key, string name)
        {
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0) return false;
            var rest = key[(sep + 2)..];
            var slash = rest.LastIndexOf('/');
            return string.Equals(slash >= 0 ? rest[..slash] : rest, name, StringComparison.Ordinal);
        }
    }

    /// <summary>Assembly MVIDs, plus the transitive in-scope reference closure of each.
    /// Transitive because base-chain walks reach through an intermediate assembly into
    /// one this assembly never references directly.</summary>
    private sealed record AssemblyIdentity(
        Dictionary<string, string> Mvids, Dictionary<string, HashSet<string>> Closure);

    private static AssemblyIdentity? AssemblyIdentities(
        IReadOnlyList<string> assemblyPaths, HashSet<string> inSet)
    {
        var mvids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in assemblyPaths)
        {
            try
            {
                using var module = ModuleDefinition.ReadModule(path, new ReaderParameters(ReadingMode.Deferred));
                var name = module.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(path);
                mvids[name] = module.Mvid.ToString("D");
                direct[name] = module.AssemblyReferences
                    .Select(r => r.Name)
                    .Where(inSet.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return null; // unreadable assembly: skip caching rather than risk a wrong key
            }
        }

        var closure = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in direct.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(direct[name]);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (!seen.Add(next))
                    continue;
                foreach (var onward in direct.GetValueOrDefault(next) ?? [])
                    queue.Enqueue(onward);
            }

            closure[name] = seen;
        }

        return new AssemblyIdentity(mvids, closure);
    }

    /// <summary>Scans one assembly, or returns null when it is not managed. Returns the
    /// partial rather than adding it to a shared collection so the caller can cache
    /// exactly this assembly's result.</summary>
    private static CallGraphIndex? ScanAssembly(
        string path, HashSet<string> inSet, ITestFrameworkDetector detector)
    {
        ModuleDefinition module;
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            module = ModuleDefinition.ReadModule(path, new ReaderParameters(ReadingMode.Deferred)
            {
                AssemblyResolver = resolver,
            });
        }
        catch (BadImageFormatException)
        {
            return null;
        }

        var partial = new CallGraphIndex();
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var assemblyName = module.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(path);

        // Which frameworks this assembly references, regardless of which one we scan for.
        var referenced = module.AssemblyReferences.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var known in TestFrameworks.All)
        {
            if (known.MarkerAssemblies.Any(referenced.Contains))
                partial.FrameworksPresent.Add(known.Name);
        }

        void AddEdge(string calleeKey, string callerKey)
        {
            if (calleeKey == callerKey)
                return;
            if (!edges.TryGetValue(calleeKey, out var set))
                edges[calleeKey] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(callerKey);
        }

        using (module)
        {
            var entryPointKey = module.EntryPoint is not null ? MethodKeys.ForDefinition(module.EntryPoint) : null;

            foreach (var type in AllTypes(module))
            {
                if (type.DeclaringType is null && GeneratedNames.IsGeneratedTypeName(type.Name))
                    continue;

                partial.BaseTypeOf[type.FullName] = type.BaseType?.FullName ?? "";

                var userType = NearestUserAncestor(type);
                var isGeneratedNested = !ReferenceEquals(userType, type);
                var typeNode = MethodKeys.ForType(userType.FullName);

                if (!isGeneratedNested)
                {
                    var members = new List<string>();
                    foreach (var method in type.Methods)
                    {
                        if (GeneratedNames.OwnerFromMemberName(method.Name) is not null)
                            continue;
                        members.Add(MethodKeys.ForDefinition(method));
                    }
                    partial.TypeMembers[type.FullName] = members.Distinct(StringComparer.Ordinal).ToList();
                }

                var typeOwner = GeneratedNames.OwnerFromTypeName(type.Name);
                var isController = DerivesFromControllerBase(type);

                if (!isGeneratedNested)
                    AddExternallyConstructedFixtureEdges(type, detector, inSet, AddEdge);

                foreach (var method in type.Methods)
                {
                    var callerKeys = CallerKeysOf(method, type, userType, typeOwner);
                    if (callerKeys.Count == 0)
                        continue;

                    // Every member reaches its type node; the type node's dependents
                    // are added where the type is USED (fields, generic args, hosts).
                    foreach (var callerKey in callerKeys)
                        AddEdge(callerKey, typeNode);

                    if (!isGeneratedNested && GeneratedNames.OwnerFromMemberName(method.Name) is null)
                    {
                        var key = MethodKeys.ForDefinition(method);

                        if (isController && entryPointKey is not null)
                            AddEdge(key, entryPointKey);

                        if (detector.IsTestMethod(method))
                        {
                            var display = type.FullName.Replace('/', '+') + "." + method.Name;
                            partial.Tests.Add(new TestNode
                            {
                                Key = key,
                                DisplayName = display,
                                Assembly = assemblyName,
                                DeclaringType = type.FullName,
                                Parameterized = detector.IsParameterizedTest(method),
                                Namespace = type.Namespace,
                                ClassName = type.Name,
                            });

                            foreach (var source in detector.GetTestCaseSources(method))
                                partial.ProviderRefs.Add(new ProviderRef
                                {
                                    TypeFullName = source.TypeFullName,
                                    MemberName = source.MemberName,
                                    TestKey = key,
                                });
                        }

                        var lifecycle = detector.GetLifecycleScope(method);
                        if (lifecycle != TestLifecycleScope.None)
                            partial.SetupFixtureByKey[key] = new LifecycleTarget
                            {
                                Scope = lifecycle,
                                DeclaringType = type.FullName,
                                Assembly = assemblyName,
                            };

                        AddPolymorphismEdges(method, type, key, inSet, AddEdge);
                    }

                    if (method.HasBody)
                        ScanBody(method.Body, callerKeys, inSet, AddEdge);
                }
            }
        }

        foreach (var (key, set) in edges)
            partial.Reverse[key] = set.ToList();
        return partial;
    }

    /// <summary>
    /// Connect a runner-constructed fixture (xUnit IClassFixture&lt;T&gt;) to the test
    /// class that receives it.
    ///
    /// The edges point at the consuming class's LIFECYCLE methods rather than its type
    /// node, because arriving at a type node mid-walk selects nothing — only a
    /// lifecycle key triggers SelectFixtureAndDerived. Routing through the constructor
    /// therefore gives the semantics the framework actually has: the fixture is built
    /// once for the class, so any change inside it impacts every test in that class.
    ///
    /// Without this, a change confined to the fixture's own constructor reaches nothing
    /// at all — the runner instantiates it reflectively, so no call site exists.
    /// (Fixture members the tests call are already reached by ordinary call edges.)
    /// </summary>
    private static void AddExternallyConstructedFixtureEdges(
        TypeDefinition type, ITestFrameworkDetector detector, HashSet<string> inSet,
        Action<string, string> addEdge)
    {
        var lifecycleKeys = type.Methods
            .Where(m => detector.GetLifecycleScope(m) != TestLifecycleScope.None)
            .Select(MethodKeys.ForDefinition)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (lifecycleKeys.Count == 0)
            return;

        foreach (var fixtureReference in detector.GetExternallyConstructedFixtures(type))
        {
            if (!InScope(ScopeName(fixtureReference), inSet))
                continue;
            var fixture = TryResolve(fixtureReference);
            if (fixture is null)
                continue;

            foreach (var member in fixture.Methods)
            {
                var memberKey = MethodKeys.ForDefinition(member);
                foreach (var lifecycleKey in lifecycleKeys)
                    addEdge(memberKey, lifecycleKey);
            }

            foreach (var lifecycleKey in lifecycleKeys)
                addEdge(MethodKeys.ForType(fixture.FullName), lifecycleKey);
        }
    }

    /// <summary>Caller identity: generated members act as their owning user method.</summary>
    private static List<string> CallerKeysOf(
        MethodDefinition method, TypeDefinition type, TypeDefinition userType, string? typeOwner)
    {
        var owner = GeneratedNames.OwnerFromMemberName(method.Name) ?? (ReferenceEquals(type, userType) ? null : typeOwner);
        if (owner is null)
        {
            return ReferenceEquals(type, userType)
                ? [MethodKeys.ForDefinition(method)]
                : []; // generated boilerplate with no owner (display-class ctor)
        }

        var keys = userType.Methods
            .Where(m => m.Name == owner)
            .Select(MethodKeys.ForDefinition)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return keys.Count > 0 ? keys : [MethodKeys.ForMethod(userType.FullName, owner, 0)];
    }

    private static void ScanBody(
        MethodBody body, List<string> callerKeys, HashSet<string> inSet,
        Action<string, string> addEdge)
    {
        void Add(string callee)
        {
            foreach (var caller in callerKeys)
                addEdge(callee, caller);
        }

        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case MethodReference methodRef:
                    // call, callvirt, newobj, ldftn, ldvirtftn, ldtoken-on-method
                    if (InScope(DeclaringScope(methodRef), inSet))
                        Add(MethodKeys.ForReference(methodRef));
                    foreach (var arg in TypeArgumentsOf(methodRef))
                    {
                        if (InScope(arg.Scope?.Name, inSet) && !GeneratedNames.IsGeneratedTypeName(arg.Name))
                            Add(MethodKeys.ForType(arg.FullName));
                    }
                    break;

                case FieldReference fieldRef:
                    // Field reads/writes (and ldtoken-on-field) depend on the declaring
                    // type's shape — routed through the type node.
                    var declaring = fieldRef.DeclaringType is GenericInstanceType g ? g.ElementType : fieldRef.DeclaringType;
                    if (InScope(declaring.Scope?.Name, inSet) && !GeneratedNames.IsGeneratedTypeName(declaring.Name))
                        Add(MethodKeys.ForType(declaring.FullName));
                    break;

                    // TypeReference operands (ldtoken type / typeof) intentionally add
                    // no edge: that is the documented reflection blind spot.
            }
        }
    }

    private static IEnumerable<TypeReference> TypeArgumentsOf(MethodReference methodRef)
    {
        var stack = new Stack<TypeReference>();
        if (methodRef is GenericInstanceMethod generic)
            foreach (var arg in generic.GenericArguments) stack.Push(arg);
        if (methodRef.DeclaringType is GenericInstanceType instance)
            foreach (var arg in instance.GenericArguments) stack.Push(arg);

        while (stack.Count > 0)
        {
            var arg = stack.Pop();
            if (arg is GenericInstanceType nested)
                foreach (var inner in nested.GenericArguments) stack.Push(inner);
            else if (arg is not GenericParameter)
                yield return arg;
        }
    }

    private static void AddPolymorphismEdges(
        MethodDefinition method, TypeDefinition type, string key, HashSet<string> inSet,
        Action<string, string> addEdge)
    {
        if (method.IsConstructor || method.IsStatic)
            return;

        // Explicit interface implementations / explicit overrides carry exact refs.
        if (method.HasOverrides)
        {
            foreach (var overridden in method.Overrides)
                addEdge(key, MethodKeys.ForReference(overridden));
        }

        if (!method.IsVirtual && !type.IsInterface)
            return;

        // Implicit interface implementations: name + arity match on any interface in
        // the hierarchy. Callers of the interface method become callers of the impl.
        foreach (var iface in InterfacesOf(type))
        {
            var ifaceDef = TryResolve(iface);
            if (ifaceDef is null || !InScope(ScopeName(iface), inSet))
                continue;
            foreach (var candidate in ifaceDef.Methods)
            {
                if (candidate.Name == method.Name && candidate.Parameters.Count == method.Parameters.Count)
                    addEdge(key, MethodKeys.ForMethod(ElementName(iface), candidate.Name, candidate.Parameters.Count));
            }
        }

        // Virtual overrides: callers of the base declaration reach the override.
        var baseType = type.BaseType;
        while (baseType is not null && InScope(ScopeName(baseType), inSet))
        {
            var baseDef = TryResolve(baseType);
            if (baseDef is null)
                break;
            foreach (var candidate in baseDef.Methods)
            {
                if (candidate.IsVirtual && candidate.Name == method.Name &&
                    candidate.Parameters.Count == method.Parameters.Count)
                    addEdge(key, MethodKeys.ForMethod(ElementName(baseType), candidate.Name, candidate.Parameters.Count));
            }
            baseType = baseDef.BaseType;
        }
    }

    private static IEnumerable<TypeReference> InterfacesOf(TypeDefinition type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = type;
        while (current is not null)
        {
            foreach (var impl in current.Interfaces)
            {
                if (seen.Add(impl.InterfaceType.FullName))
                {
                    yield return impl.InterfaceType;
                    var def = TryResolve(impl.InterfaceType);
                    if (def is not null)
                    {
                        foreach (var inherited in def.Interfaces)
                        {
                            if (seen.Add(inherited.InterfaceType.FullName))
                                yield return inherited.InterfaceType;
                        }
                    }
                }
            }
            current = TryResolve(current.BaseType);
        }
    }

    private static bool DerivesFromControllerBase(TypeDefinition type)
    {
        var baseType = type.BaseType;
        for (var depth = 0; baseType is not null && depth < 16; depth++)
        {
            if (baseType.FullName is "Microsoft.AspNetCore.Mvc.ControllerBase" or "Microsoft.AspNetCore.Mvc.Controller")
                return true;
            var def = TryResolve(baseType);
            if (def is null)
                return false;
            baseType = def.BaseType;
        }
        return false;
    }

    private static TypeDefinition? TryResolve(TypeReference? reference)
    {
        try
        {
            return reference?.Resolve();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DeclaringScope(MethodReference methodRef)
    {
        var element = methodRef is GenericInstanceMethod g ? g.ElementMethod : methodRef;
        var declaring = element.DeclaringType is GenericInstanceType i ? i.ElementType : element.DeclaringType;
        return ScopeName(declaring) ?? "";
    }

    private static string? ScopeName(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        ModuleDefinition module => module.Assembly?.Name.Name,
        _ => null,
    };

    private static bool InScope(string? scope, HashSet<string> inSet) =>
        scope is not null && inSet.Contains(scope);

    private static string ElementName(TypeReference type) =>
        type is GenericInstanceType instance ? instance.ElementType.FullName : type.FullName;

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
                stack.Push(nested);
        }
    }

    private static TypeDefinition NearestUserAncestor(TypeDefinition type)
    {
        var current = type;
        while (current.DeclaringType is not null && GeneratedNames.IsGeneratedTypeName(current.Name))
            current = current.DeclaringType;
        return current;
    }
}

public sealed class SelectedTest
{
    public string DisplayName { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string DeclaringType { get; set; } = "";
    public bool Parameterized { get; set; }

    /// <summary>Declaring namespace and simple type name, for tree-path emission.</summary>
    public string Namespace { get; set; } = "";
    public string ClassName { get; set; } = "";

    /// <summary>Why this test was selected, in one sentence.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Node keys from the changed method to the test (shortest path found).</summary>
    public List<string> Chain { get; set; } = [];
}

public static class GraphWalker
{
    public static List<SelectedTest> SelectTests(CallGraphIndex graph, IEnumerable<string> changedFqns)
    {
        var testsByKey = new Dictionary<string, List<TestNode>>(StringComparer.Ordinal);
        var testsByType = new Dictionary<string, List<TestNode>>(StringComparer.Ordinal);
        foreach (var test in graph.Tests)
        {
            (testsByKey.TryGetValue(test.Key, out var byKey) ? byKey : testsByKey[test.Key] = []).Add(test);
            (testsByType.TryGetValue(test.DeclaringType, out var byType) ? byType : testsByType[test.DeclaringType] = []).Add(test);
        }

        var selected = new Dictionary<string, SelectedTest>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var seedReason = new Dictionary<string, string>(StringComparer.Ordinal);

        // First (BFS-shortest) reason wins per test.
        void Select(TestNode test, string reason, List<string> chain)
        {
            if (!selected.ContainsKey(test.DisplayName))
                selected[test.DisplayName] = new SelectedTest
                {
                    DisplayName = test.DisplayName,
                    Assembly = test.Assembly,
                    DeclaringType = test.DeclaringType,
                    Parameterized = test.Parameterized,
                    Namespace = test.Namespace,
                    ClassName = test.ClassName,
                    Reason = reason,
                    Chain = chain,
                };
        }

        List<string> ChainTo(string key)
        {
            var chain = new List<string> { key };
            while (cameFrom.TryGetValue(key, out var previous))
            {
                key = previous;
                chain.Add(key);
            }
            chain.Reverse();
            return chain;
        }

        string SeedDescriptionOf(List<string> chain) =>
            seedReason.GetValueOrDefault(chain[0]) ?? "changed code";

        void Seed(string key, string why)
        {
            if (visited.Add(key))
            {
                queue.Enqueue(key);
                seedReason[key] = why;
            }
        }

        void SelectFixtureAndDerived(string fixtureType, string reason, List<string> chain)
        {
            foreach (var (type, tests) in testsByType)
            {
                if (DerivesFromOrIs(graph, type, fixtureType))
                    foreach (var test in tests)
                        Select(test, reason, chain);
            }
        }

        // A lifecycle method runs around every test in its scope, so a change inside
        // one impacts all of them — not merely the tests of the class that declares it,
        // which is what an [AssemblyInitialize] used to be narrowed to.
        void SelectLifecycleScope(LifecycleTarget target, string reason, List<string> chain)
        {
            switch (target.Scope)
            {
                case TestLifecycleScope.Assembly:
                    foreach (var test in graph.Tests)
                    {
                        if (string.Equals(test.Assembly, target.Assembly, StringComparison.OrdinalIgnoreCase))
                            Select(test, reason, chain);
                    }
                    break;

                case TestLifecycleScope.Global:
                    foreach (var test in graph.Tests)
                        Select(test, reason, chain);
                    break;

                default:
                    SelectFixtureAndDerived(target.DeclaringType, reason, chain);
                    break;
            }
        }

        foreach (var fqn in changedFqns)
        {
            var key = MethodKeys.FromChangedFqn(fqn, out var typeNodeType);
            Seed(key, typeNodeType is not null ? $"type-level change of {typeNodeType}" : $"changed method {fqn}");
            if (typeNodeType is not null)
            {
                // Type-level change: every member counts as changed, and fixtures
                // inheriting from the type are impacted wholesale.
                foreach (var member in graph.TypeMembers.GetValueOrDefault(typeNodeType) ?? [])
                    Seed(member, $"member of changed type {typeNodeType}");
                SelectFixtureAndDerived(typeNodeType,
                    $"fixture inherits from changed type {typeNodeType}", [key]);
            }
            else if (graph.TestKeysByProviderKey.TryGetValue(key, out var consumerKeys))
            {
                // A [TestCaseSource] provider changed: its consumers run.
                foreach (var consumerKey in consumerKeys)
                    foreach (var test in testsByKey.GetValueOrDefault(consumerKey) ?? [])
                        Select(test, $"[TestCaseSource] provider changed: {fqn}", [key, consumerKey]);
            }
        }

        while (queue.Count > 0)
        {
            var key = queue.Dequeue();

            var tests = testsByKey.GetValueOrDefault(key);
            if (tests is not null)
            {
                var chain = ChainTo(key);
                var reason = chain.Count == 1
                    ? $"test itself changed ({SeedDescriptionOf(chain)})"
                    : $"{SeedDescriptionOf(chain)} reaches this test in {chain.Count - 1} hop(s)";
                foreach (var test in tests)
                    Select(test, reason, chain);
            }

            if (graph.SetupFixtureByKey.TryGetValue(key, out var lifecycle))
            {
                var chain = ChainTo(key);
                var scopeText = lifecycle.Scope switch
                {
                    TestLifecycleScope.Assembly => $"assembly-wide lifecycle method {key}",
                    TestLifecycleScope.Global => $"global lifecycle method {key}",
                    _ => $"lifecycle method {key}",
                };
                SelectLifecycleScope(lifecycle,
                    $"{scopeText} impacted ({SeedDescriptionOf(chain)})", chain);
            }

            foreach (var caller in graph.Reverse.GetValueOrDefault(key) ?? [])
            {
                if (visited.Add(caller))
                {
                    cameFrom[caller] = key;
                    queue.Enqueue(caller);
                }
            }
        }

        return selected.Values.OrderBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Tests matching an --always-run pattern: pinned regardless of the diff,
    /// the mitigation for suites that reach code only through reflection.</summary>
    public static List<SelectedTest> SelectAlwaysRun(CallGraphIndex graph, IReadOnlyList<string> patterns)
    {
        var regexes = patterns
            .Select(p => (Pattern: p, Regex: new System.Text.RegularExpressions.Regex(
                "^" + string.Join("", p.Select(c => c switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
                })) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .ToList();

        var result = new List<SelectedTest>();
        foreach (var test in graph.Tests)
        {
            var match = regexes.FirstOrDefault(r => r.Regex.IsMatch(test.DisplayName));
            if (match.Pattern is not null)
                result.Add(new SelectedTest
                {
                    DisplayName = test.DisplayName,
                    Assembly = test.Assembly,
                    DeclaringType = test.DeclaringType,
                    Parameterized = test.Parameterized,
                    Namespace = test.Namespace,
                    ClassName = test.ClassName,
                    Reason = $"matches --always-run pattern '{match.Pattern}'",
                });
        }

        return result;
    }

    private static bool DerivesFromOrIs(CallGraphIndex graph, string type, string ancestor)
    {
        var current = type;
        for (var depth = 0; current.Length > 0 && depth < 64; depth++)
        {
            if (string.Equals(current, ancestor, StringComparison.Ordinal))
                return true;
            current = graph.BaseTypeOf.GetValueOrDefault(current) ?? "";
        }
        return false;
    }
}
