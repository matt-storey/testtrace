using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestTrace.Core.Tests;

/// <summary>
/// Builds an xUnit-shaped assembly in memory with Cecil, so the detector's rules can
/// be asserted without a compiler, a package restore, or a dependency on the sample
/// solution. Only the shapes the detector inspects are modelled: attribute names and
/// inheritance, the IClassFixture interface, and method kinds.
/// </summary>
internal static class FakeXunitAssembly
{
    public static ModuleDefinition Build()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FakeXunit", new Version(1, 0)), "FakeXunit", ModuleKind.Dll);
        var module = assembly.MainModule;
        var attributeBase = module.ImportReference(typeof(Attribute));
        var typeType = module.ImportReference(typeof(Type));

        // -- the framework's own surface ------------------------------------
        var fact = AddType(module, "Xunit", "FactAttribute", attributeBase);
        AddConstructor(module, fact);

        // Theory derives from Fact, exactly as xUnit declares it.
        var theory = AddType(module, "Xunit", "TheoryAttribute", fact);
        AddConstructor(module, theory);

        var memberData = AddType(module, "Xunit", "MemberDataAttribute", attributeBase);
        AddConstructor(module, memberData, module.TypeSystem.String);

        var classData = AddType(module, "Xunit", "ClassDataAttribute", attributeBase);
        AddConstructor(module, classData, typeType);

        var classFixture = new TypeDefinition("Xunit", "IClassFixture`1",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        classFixture.GenericParameters.Add(new GenericParameter("TFixture", classFixture));
        module.Types.Add(classFixture);

        // A project-specific attribute deriving from [Fact] — idiomatic xUnit.
        var integrationFact = AddType(module, "Tests", "IntegrationFactAttribute", fact);
        AddConstructor(module, integrationFact);

        // -- types under inspection ------------------------------------------
        var caseData = AddType(module, "Tests", "CaseData", module.TypeSystem.Object);
        AddConstructor(module, caseData);
        AddMethod(module, caseData, "GetEnumerator");

        var pricing = AddType(module, "Tests", "PricingTests", module.TypeSystem.Object);
        AddConstructor(module, pricing);
        AddMethod(module, pricing, "Dispose");
        AddMethod(module, pricing, "Helper");
        AddMethod(module, pricing, "Cases", isStatic: true);

        Attribute(AddMethod(module, pricing, "PlainFact"), fact);
        Attribute(AddMethod(module, pricing, "CustomFact"), integrationFact);
        Attribute(AddMethod(module, pricing, "InlineTheory"), theory);

        var memberTheory = AddMethod(module, pricing, "MemberTheory");
        Attribute(memberTheory, theory);
        Attribute(memberTheory, memberData, new CustomAttributeArgument(module.TypeSystem.String, "Cases"));

        var externalTheory = AddMethod(module, pricing, "ExternalMemberTheory");
        Attribute(externalTheory, theory);
        var external = Attribute(externalTheory, memberData,
            new CustomAttributeArgument(module.TypeSystem.String, "Shared"));
        external.Properties.Add(new CustomAttributeNamedArgument(
            "MemberType", new CustomAttributeArgument(typeType, caseData)));

        var classTheory = AddMethod(module, pricing, "ClassTheory");
        Attribute(classTheory, theory);
        Attribute(classTheory, classData, new CustomAttributeArgument(typeType, caseData));

        // Holds no tests: its constructor must NOT read as lifecycle.
        var plain = AddType(module, "Tests", "NotATestClass", module.TypeSystem.Object);
        AddConstructor(module, plain);
        AddMethod(module, plain, "DoWork");

        var sharedFixture = AddType(module, "Tests", "SharedFixture", module.TypeSystem.Object);
        AddConstructor(module, sharedFixture);

        var consumer = AddType(module, "Tests", "FixtureConsumer", module.TypeSystem.Object);
        AddConstructor(module, consumer, sharedFixture);
        Attribute(AddMethod(module, consumer, "UsesFixture"), fact);
        var fixtureInterface = new GenericInstanceType(classFixture);
        fixtureInterface.GenericArguments.Add(sharedFixture);
        consumer.Interfaces.Add(new InterfaceImplementation(fixtureInterface));

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
        var constructor = attributeType.Methods.First(m => m.IsConstructor);
        var attribute = new CustomAttribute(constructor);
        foreach (var argument in arguments)
            attribute.ConstructorArguments.Add(argument);
        method.CustomAttributes.Add(attribute);
        return attribute;
    }
}
