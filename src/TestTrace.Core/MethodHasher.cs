using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestTrace.Core;

/// <summary>
/// Produces a stable hash per user-visible method. Raw IL bytes are useless for this —
/// they contain metadata tokens, which are table indices that shift when anything is
/// added. Instead each method is rendered to canonical text: opcode names, operands
/// with member/type/field references resolved to full names, literals inline, plus
/// signature, custom attributes (rendered structurally, not as blobs, so generated
/// type names inside them can be normalized) and the exception-handler table.
/// Branch targets are ordinals into the nop-stripped instruction list.
///
/// Compiler-generated members (lambdas, closures, async/iterator state machines,
/// local functions) do not get entries of their own: their text contributes to the
/// hash of the user method that owns them, so a lambda edit reads as a change to the
/// declaring method — something the call graph can actually reach.
/// </summary>
public static class MethodHasher
{
    /// <summary>Suffix marking a type-structure entry (fields/base/interfaces), not a body.</summary>
    public const string TypeEntrySuffix = "::<type>";

    public static List<MethodEntry> HashModule(ModuleDefinition module)
    {
        // fqn -> (token, own canonical text, declaring type, simple name)
        var own = new Dictionary<string, (int Token, string Text, string Type, string Name)>();
        // (user type fqn, owner method simple name) -> contributor fqn -> contributor hash
        var contributions = new Dictionary<(string Type, string Owner), SortedDictionary<string, string>>();

        foreach (var type in AllTypes(module))
        {
            if (type.DeclaringType is null && GeneratedNames.IsGeneratedTypeName(type.Name))
                continue; // <Module>, <PrivateImplementationDetails>, <>f__AnonymousType*, ...

            var userType = NearestUserAncestor(type);
            if (ReferenceEquals(userType, type))
            {
                var typeFqn = type.FullName + TypeEntrySuffix;
                own[Normalize(typeFqn)] = (type.MetadataToken.ToInt32(), TypeStructureText(type), type.FullName, TypeEntrySuffix);

                foreach (var method in type.Methods)
                {
                    var owner = GeneratedNames.OwnerFromMemberName(method.Name);
                    if (owner is not null)
                        AddContribution(contributions, type.FullName, owner, method);
                    else
                        own[Normalize(method.FullName)] =
                            (method.MetadataToken.ToInt32(), MethodText(method), type.FullName, method.Name);
                }
            }
            else
            {
                // Generated nested type (display class, state machine). Attribute every
                // method to its owner; boilerplate without an owner (.ctor/.cctor of a
                // display class) is skipped — any semantic change to it always co-occurs
                // with a change in a member body that references its fields by name.
                var typeOwner = GeneratedNames.OwnerFromTypeName(type.Name);
                foreach (var method in type.Methods)
                {
                    var owner = GeneratedNames.OwnerFromMemberName(method.Name) ?? typeOwner;
                    if (owner is not null)
                        AddContribution(contributions, userType.FullName, owner, method);
                }
            }
        }

        var entries = new List<MethodEntry>(own.Count);
        var claimed = new HashSet<(string, string)>();
        foreach (var (fqn, info) in own)
        {
            var text = info.Text;
            if (contributions.TryGetValue((info.Type, info.Name), out var contribs))
            {
                claimed.Add((info.Type, info.Name));
                var builder = new StringBuilder(text);
                foreach (var (contributor, hash) in contribs)
                    builder.Append("\ncontrib|").Append(contributor).Append('|').Append(hash);
                text = builder.ToString();
            }
            entries.Add(new MethodEntry { Token = info.Token, Fqn = fqn, Hash = HashText(text) });
        }

        // A contribution whose owner method was not found still surfaces as its own
        // entry — over-approximation beats silently dropping a change.
        foreach (var ((type, ownerName), contribs) in contributions)
        {
            if (claimed.Contains((type, ownerName)))
                continue;
            foreach (var (contributor, hash) in contribs)
                entries.Add(new MethodEntry { Token = 0, Fqn = contributor, Hash = hash });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Fqn, b.Fqn));
        return entries;
    }

    private static void AddContribution(
        Dictionary<(string, string), SortedDictionary<string, string>> contributions,
        string userType, string owner, MethodDefinition method)
    {
        var key = (userType, owner);
        if (!contributions.TryGetValue(key, out var bucket))
            contributions[key] = bucket = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bucket[Normalize(method.FullName)] = HashText(MethodText(method));
    }

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

    // -- canonical text ------------------------------------------------------

    private static string MethodText(MethodDefinition method)
    {
        var sb = new StringBuilder(256);
        sb.Append("sig|").Append(method.FullName)
          .Append("|ga:").Append(method.GenericParameters.Count)
          .Append("|fl:").Append(((uint)method.Attributes).ToString("X", CultureInfo.InvariantCulture))
          .Append("|im:").Append(((uint)method.ImplAttributes).ToString("X", CultureInfo.InvariantCulture))
          .Append('\n');

        AppendCustomAttributes(sb, method.CustomAttributes);

        if (method.HasBody)
        {
            var body = method.Body;
            if (body.HasVariables)
            {
                sb.Append("locals|");
                foreach (var variable in body.Variables)
                    sb.Append(variable.VariableType.FullName).Append(';');
                sb.Append('\n');
            }

            var real = new List<Instruction>();
            var ordinals = new Dictionary<Instruction, int>();
            foreach (var instruction in body.Instructions)
            {
                if (instruction.OpCode.Code == Code.Nop)
                    continue;
                ordinals[instruction] = real.Count;
                real.Add(instruction);
            }

            int OrdinalOf(Instruction? target)
            {
                while (target is not null && target.OpCode.Code == Code.Nop)
                    target = target.Next;
                return target is not null && ordinals.TryGetValue(target, out var i) ? i : -1;
            }

            foreach (var instruction in real)
            {
                sb.Append(instruction.OpCode.Name);
                AppendOperand(sb.Append(' '), instruction.Operand, OrdinalOf);
                sb.Append('\n');
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                sb.Append("eh|").Append(handler.HandlerType)
                  .Append('|').Append(handler.CatchType?.FullName ?? "-")
                  .Append('|').Append(OrdinalOf(handler.TryStart))
                  .Append('|').Append(OrdinalOf(handler.TryEnd))
                  .Append('|').Append(OrdinalOf(handler.HandlerStart))
                  .Append('|').Append(OrdinalOf(handler.HandlerEnd))
                  .Append('|').Append(OrdinalOf(handler.FilterStart))
                  .Append('\n');
            }
        }

        return Normalize(sb.ToString());
    }

    private static void AppendOperand(StringBuilder sb, object? operand, Func<Instruction?, int> ordinalOf)
    {
        switch (operand)
        {
            case null:
                break;
            case MethodReference m:
                sb.Append("m:").Append(m.FullName);
                break;
            case FieldReference f:
                sb.Append("f:").Append(f.FullName);
                break;
            case TypeReference t:
                sb.Append("t:").Append(t.FullName);
                break;
            case CallSite cs:
                sb.Append("cs:").Append(cs.FullName);
                break;
            case string s:
                sb.Append("s:").Append(s.Replace("\n", "\\n", StringComparison.Ordinal));
                break;
            case Instruction target:
                sb.Append('@').Append(ordinalOf(target));
                break;
            case Instruction[] targets:
                sb.Append("@[");
                foreach (var t in targets)
                    sb.Append(ordinalOf(t)).Append(',');
                sb.Append(']');
                break;
            case VariableDefinition v:
                sb.Append("loc:").Append(v.Index);
                break;
            case ParameterDefinition p:
                sb.Append("par:").Append(p.Index);
                break;
            case float f32:
                sb.Append("f4:").Append(BitConverter.SingleToInt32Bits(f32).ToString("X", CultureInfo.InvariantCulture));
                break;
            case double f64:
                sb.Append("f8:").Append(BitConverter.DoubleToInt64Bits(f64).ToString("X", CultureInfo.InvariantCulture));
                break;
            case IFormattable n: // sbyte, byte, int, long
                sb.Append("n:").Append(n.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append("o:").Append(operand);
                break;
        }
    }

    private static string TypeStructureText(TypeDefinition type)
    {
        var sb = new StringBuilder(128);
        sb.Append("type|").Append(type.FullName)
          .Append("|fl:").Append(((uint)type.Attributes).ToString("X", CultureInfo.InvariantCulture))
          .Append("|ga:").Append(type.GenericParameters.Count)
          .Append("|base:").Append(type.BaseType?.FullName ?? "-")
          .Append('\n');

        foreach (var iface in type.Interfaces.Select(i => i.InterfaceType.FullName).OrderBy(n => n, StringComparer.Ordinal))
            sb.Append("iface|").Append(iface).Append('\n');

        foreach (var field in type.Fields.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            sb.Append("field|").Append(field.Name)
              .Append('|').Append(field.FieldType.FullName)
              .Append("|fl:").Append(((uint)field.Attributes).ToString("X", CultureInfo.InvariantCulture));
            if (field.HasConstant)
                AppendOperand(sb.Append("|const:"), field.Constant, _ => -1);
            sb.Append('\n');
        }

        AppendCustomAttributes(sb, type.CustomAttributes);
        return Normalize(sb.ToString());
    }

    private static void AppendCustomAttributes(StringBuilder sb, IEnumerable<CustomAttribute> attributes)
    {
        // Rendered structurally, not as raw blobs: state-machine attributes carry
        // generated type names whose ordinals must be normalizable as text.
        var lines = new List<string>();
        foreach (var attribute in attributes)
        {
            var line = new StringBuilder("ca|").Append(attribute.AttributeType.FullName);
            try
            {
                foreach (var argument in attribute.ConstructorArguments)
                    AppendAttributeArgument(line.Append("|a:"), argument);
                foreach (var named in attribute.Fields.Concat(attribute.Properties).OrderBy(n => n.Name, StringComparer.Ordinal))
                    AppendAttributeArgument(line.Append("|n:").Append(named.Name).Append('='), named.Argument);
            }
            catch (Exception)
            {
                // Argument parsing can require resolving enum types from assemblies we
                // do not have (shared framework). The raw blob is still deterministic;
                // it just cannot be ordinal-normalized — fine for non-user assemblies.
                line.Clear();
                line.Append("cab|").Append(attribute.AttributeType.FullName)
                    .Append('|').Append(Convert.ToHexString(attribute.GetBlob()));
            }
            lines.Add(line.ToString());
        }

        lines.Sort(StringComparer.Ordinal);
        foreach (var line in lines)
            sb.Append(line).Append('\n');
    }

    private static void AppendAttributeArgument(StringBuilder sb, CustomAttributeArgument argument)
    {
        switch (argument.Value)
        {
            case null:
                sb.Append("null");
                break;
            case TypeReference t:
                sb.Append(t.FullName);
                break;
            case CustomAttributeArgument nested:
                AppendAttributeArgument(sb, nested);
                break;
            case CustomAttributeArgument[] array:
                foreach (var element in array)
                    AppendAttributeArgument(sb.Append(','), element);
                break;
            case string s:
                sb.Append(s);
                break;
            case IFormattable n:
                sb.Append(n.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(argument.Value);
                break;
        }
    }

    private static string Normalize(string text) => GeneratedNames.NormalizeOrdinals(text);

    private static string HashText(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 16)); // 128 bits is plenty here
    }
}
