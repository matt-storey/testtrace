using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestTrace.Core.Tests;

/// <summary>
/// An MSTest-shaped assembly built in memory with Cecil, mirroring the xUnit and TUnit
/// fakes. The inheritance is faithful to MSTest 3.11 where the detector depends on it:
/// [DataTestMethod] and [STATestMethod] derive from [TestMethod], while the lifecycle
/// attributes share no base at all and so must be matched individually.
/// </summary>
internal static class FakeMsTestAssembly
{
    private const string Ns = "Microsoft.VisualStudio.TestTools.UnitTesting";

    public static ModuleDefinition Build()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FakeMsTest", new Version(1, 0)), "FakeMsTest", ModuleKind.Dll);
        var module = assembly.MainModule;
        var attributeBase = module.ImportReference(typeof(Attribute));
        var typeType = module.ImportReference(typeof(Type));

        // -- framework surface ------------------------------------------------
        var testMethod = AddType(module, Ns, "TestMethodAttribute", attributeBase);
        AddConstructor(module, testMethod);

        var dataTestMethod = AddType(module, Ns, "DataTestMethodAttribute", testMethod);
        AddConstructor(module, dataTestMethod);

        var testClass = AddType(module, Ns, "TestClassAttribute", attributeBase);
        AddConstructor(module, testClass);

        // No shared base between these — that is why the detector enumerates them.
        var testInitialize = AddType(module, Ns, "TestInitializeAttribute", attributeBase);
        AddConstructor(module, testInitialize);
        var testCleanup = AddType(module, Ns, "TestCleanupAttribute", attributeBase);
        AddConstructor(module, testCleanup);
        var classInitialize = AddType(module, Ns, "ClassInitializeAttribute", attributeBase);
        AddConstructor(module, classInitialize);
        var assemblyInitialize = AddType(module, Ns, "AssemblyInitializeAttribute", attributeBase);
        AddConstructor(module, assemblyInitialize);

        var dataRow = AddType(module, Ns, "DataRowAttribute", attributeBase);
        AddConstructor(module, dataRow, module.TypeSystem.Object);

        var dynamicData = AddType(module, Ns, "DynamicDataAttribute", attributeBase);
        AddConstructor(module, dynamicData, module.TypeSystem.String);
        AddConstructor(module, dynamicData, module.TypeSystem.String, typeType);

        // -- types under inspection -------------------------------------------
        var caseData = AddType(module, "Tests", "CaseData", module.TypeSystem.Object);
        AddConstructor(module, caseData);
        AddMethod(module, caseData, "Rows", isStatic: true);

        var orderTests = AddType(module, "Tests", "OrderTotalTests", module.TypeSystem.Object);
        AddConstructor(module, orderTests);
        AddMethod(module, orderTests, "Helper");
        AddMethod(module, orderTests, "LineCounts", isStatic: true);

        Attribute(AddMethod(module, orderTests, "PlainTest"), testMethod);
        Attribute(AddMethod(module, orderTests, "DerivedTest"), dataTestMethod);

        // Instance lifecycle.
        Attribute(AddMethod(module, orderTests, "Setup"), testInitialize);
        Attribute(AddMethod(module, orderTests, "Teardown"), testCleanup);

        // STATIC lifecycle — the trap: filtering statics out would drop these.
        Attribute(AddMethod(module, orderTests, "ClassInit", isStatic: true), classInitialize);
        Attribute(AddMethod(module, orderTests, "AssemblyInit", isStatic: true), assemblyInitialize);

        var withRows = AddMethod(module, orderTests, "WithDataRow");
        Attribute(withRows, testMethod);
        Attribute(withRows, dataRow, new CustomAttributeArgument(module.TypeSystem.Object, 1));

        var ownSource = AddMethod(module, orderTests, "FromOwnDynamicData");
        Attribute(ownSource, testMethod);
        Attribute(ownSource, dynamicData, new CustomAttributeArgument(module.TypeSystem.String, "LineCounts"));

        var otherSource = AddMethod(module, orderTests, "FromOtherTypeDynamicData");
        Attribute(otherSource, testMethod);
        var external = new CustomAttribute(
            dynamicData.Methods.First(m => m.IsConstructor && m.Parameters.Count == 2));
        external.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "Rows"));
        external.ConstructorArguments.Add(new CustomAttributeArgument(typeType, caseData));
        otherSource.CustomAttributes.Add(external);

        var plain = AddType(module, "Tests", "NotATestClass", module.TypeSystem.Object);
        AddConstructor(module, plain);
        AddMethod(module, plain, "DoWork");

        return module;
    }

    private static TypeDefinition AddType(ModuleDefinition module, string ns, string name, TypeReference baseType)
    {
        var type = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Class, baseType);
        module.Types.Add(type);
        return type;
    }

    private static MethodDefinition AddConstructor(
        ModuleDefinition module, TypeDefinition type, params TypeReference[] parameters)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        foreach (var parameter in parameters)
            ctor.Parameters.Add(new ParameterDefinition(parameter));
        ctor.Body.GetILProcessor().Emit(OpCodes.Ret);
        type.Methods.Add(ctor);
        return ctor;
    }

    private static MethodDefinition AddMethod(
        ModuleDefinition module, TypeDefinition type, string name, bool isStatic = false)
    {
        var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;
        if (isStatic)
            attributes |= MethodAttributes.Static;

        var method = new MethodDefinition(name, attributes, module.TypeSystem.Void);
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        type.Methods.Add(method);
        return method;
    }

    private static CustomAttribute Attribute(
        MethodDefinition method, TypeDefinition attributeType, params CustomAttributeArgument[] arguments)
    {
        var constructor = attributeType.Methods.First(
            m => m.IsConstructor && m.Parameters.Count == arguments.Length);
        var attribute = new CustomAttribute(constructor);
        foreach (var argument in arguments)
            attribute.ConstructorArguments.Add(argument);
        method.CustomAttributes.Add(attribute);
        return attribute;
    }
}
