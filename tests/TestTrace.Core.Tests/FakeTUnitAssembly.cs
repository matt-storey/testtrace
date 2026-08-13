using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestTrace.Core.Tests;

/// <summary>
/// A TUnit-shaped assembly built in memory with Cecil, mirroring FakeXunitAssembly.
/// Only what the detector inspects is modelled, but the inheritance is faithful to
/// TUnit.Core 1.65 because that is exactly what the detector relies on:
/// TestAttribute derives from BaseTestAttribute, every hook derives from
/// HookAttribute, and the data-source attributes come in generic and non-generic
/// forms.
/// </summary>
internal static class FakeTUnitAssembly
{
    public static ModuleDefinition Build()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FakeTUnit", new Version(1, 0)), "FakeTUnit", ModuleKind.Dll);
        var module = assembly.MainModule;
        var attributeBase = module.ImportReference(typeof(Attribute));
        var typeType = module.ImportReference(typeof(Type));

        // -- framework surface, matching TUnit's real inheritance -------------
        var tunitBase = AddType(module, "TUnit.Core", "TUnitAttribute", attributeBase);
        var baseTest = AddType(module, "TUnit.Core", "BaseTestAttribute", tunitBase);
        var test = AddType(module, "TUnit.Core", "TestAttribute", baseTest);
        AddConstructor(module, test);

        // All four of [Before]/[After]/[BeforeEvery]/[AfterEvery] share this base, so
        // the detector matches the base rather than enumerating them.
        var hook = AddType(module, "TUnit.Core", "HookAttribute", tunitBase);
        var before = AddType(module, "TUnit.Core", "BeforeAttribute", hook);
        AddConstructor(module, before, module.TypeSystem.Int32);
        var after = AddType(module, "TUnit.Core", "AfterAttribute", hook);
        AddConstructor(module, after, module.TypeSystem.Int32);

        var arguments = AddType(module, "TUnit.Core", "ArgumentsAttribute", attributeBase);
        AddConstructor(module, arguments, module.TypeSystem.Int32);

        var methodDataSource = AddType(module, "TUnit.Core", "MethodDataSourceAttribute", attributeBase);
        AddConstructor(module, methodDataSource, module.TypeSystem.String);
        AddConstructor(module, methodDataSource, typeType, module.TypeSystem.String);

        // Generic form: the data type is a generic argument on the attribute itself.
        var classDataSourceGeneric = new TypeDefinition("TUnit.Core", "ClassDataSourceAttribute`1",
            TypeAttributes.Public | TypeAttributes.Class, tunitBase);
        classDataSourceGeneric.GenericParameters.Add(new GenericParameter("T", classDataSourceGeneric));
        module.Types.Add(classDataSourceGeneric);
        AddConstructor(module, classDataSourceGeneric);

        // A project-specific attribute deriving from [Test].
        var slowTest = AddType(module, "Tests", "SlowTestAttribute", test);
        AddConstructor(module, slowTest);

        // -- types under inspection ------------------------------------------
        var caseData = AddType(module, "Tests", "CaseData", module.TypeSystem.Object);
        AddConstructor(module, caseData);
        AddMethod(module, caseData, "GetEnumerator");

        var delivery = AddType(module, "Tests", "DeliveryTests", module.TypeSystem.Object);
        AddConstructor(module, delivery);
        AddMethod(module, delivery, "Helper");
        AddMethod(module, delivery, "LineCounts", isStatic: true);

        Attribute(AddMethod(module, delivery, "PlainTest"), test);
        Attribute(AddMethod(module, delivery, "SlowTest"), slowTest);

        // Hooks are lifecycle, not tests.
        Attribute(AddMethod(module, delivery, "Setup"), before,
            new CustomAttributeArgument(module.TypeSystem.Int32, 0));
        Attribute(AddMethod(module, delivery, "Teardown"), after,
            new CustomAttributeArgument(module.TypeSystem.Int32, 0));

        var inline = AddMethod(module, delivery, "WithArguments");
        Attribute(inline, test);
        Attribute(inline, arguments, new CustomAttributeArgument(module.TypeSystem.Int32, 1));

        var fromMethod = AddMethod(module, delivery, "FromMethodSource");
        Attribute(fromMethod, test);
        Attribute(fromMethod, methodDataSource, new CustomAttributeArgument(module.TypeSystem.String, "LineCounts"));

        var fromOtherType = AddMethod(module, delivery, "FromOtherTypeSource");
        Attribute(fromOtherType, test);
        var external = new CustomAttribute(methodDataSource.Methods.First(
            m => m.IsConstructor && m.Parameters.Count == 2));
        external.ConstructorArguments.Add(new CustomAttributeArgument(typeType, caseData));
        external.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "Shared"));
        fromOtherType.CustomAttributes.Add(external);

        var fromClass = AddMethod(module, delivery, "FromClassSource");
        Attribute(fromClass, test);
        var closed = new GenericInstanceType(classDataSourceGeneric);
        closed.GenericArguments.Add(caseData);
        var classDataAttribute = new CustomAttribute(
            new MethodReference(".ctor", module.TypeSystem.Void, closed) { HasThis = true });
        fromClass.CustomAttributes.Add(classDataAttribute);

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
