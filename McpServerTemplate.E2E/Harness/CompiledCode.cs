using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace McpServerTemplate.E2E.Harness;

/// <summary>
/// contract-005 · G-17 round 3 (HAR-R3-02, HAR-R3-04) — what the suite's code compiles to: the members each method's IL names,
/// and the methods of the suite's own code a method reaches.
///
/// The harness's rules about its own code were read from its source, by fixed spellings, and a spelling can be walked round: a
/// second part of a partial class in a file nobody read, a helper under Harness/, a name the list did not spell, a type built by
/// an object initializer where the check looked for a call. The IL is what runs, whatever file and whatever spelling it came
/// from, and reflection sees every part of a type. So those rules read it: the class exempt from sabotages reaches no start of
/// the environment (HarnessSelfTests), only ServerUnderTest makes a <see cref="RequestSource"/>, and only
/// <see cref="StandardClient"/> makes the SDK's OAuth client.
///
/// What this cannot see is named where it is used: a method reached only through a type's initializer, through reflection by a
/// name held in a string, or through an interface of another assembly (IAsyncDisposable, say) whose implementation is the
/// suite's is not followed.
/// </summary>
internal static class CompiledCode
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every IL opcode, by its value: one byte, or 0xFE and a second.</summary>
    private static readonly Dictionary<int, OpCode> Instructions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .GroupBy(o => o.Value & 0xFFFF)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>One member a method's IL names, and the instruction that names it.</summary>
    /// <param name="From">The method whose IL names it.</param>
    /// <param name="To">The member: a method or a constructor it calls, makes or loads a pointer to, a field, or a type.</param>
    /// <param name="Instruction">The instruction.</param>
    internal sealed record Reference(MethodBase From, MemberInfo To, OpCode Instruction);

    /// <summary>
    /// Every method, constructor and type initializer of <paramref name="type"/> and of every type nested in it, however deep —
    /// the compiler's own among them: the closures of its lambdas, and the state machines of its async and iterator methods.
    /// </summary>
    public static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        foreach (var each in Nested(type).Prepend(type))
        {
            foreach (var method in each.GetMethods(Declared))
            {
                yield return method;
            }

            foreach (var constructor in each.GetConstructors(Declared))
            {
                yield return constructor;
            }
        }

        static IEnumerable<Type> Nested(Type type) =>
            type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(n => Nested(n).Prepend(n));
    }

    /// <summary>
    /// Every member <paramref name="method"/>'s IL names: what it calls, constructs or loads a pointer to, the fields it reads and
    /// writes, and the types and members it loads tokens of. A token that cannot be read is a failure of this reading, named.
    /// </summary>
    public static IReadOnlyList<Reference> ReferencesOf(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return [];
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } open ? open.GetGenericArguments() : null;
        var found = new List<Reference>();
        for (var at = 0; at < il.Length;)
        {
            var value = il[at] == 0xFE && at + 1 < il.Length ? 0xFE00 | il[at + 1] : il[at];
            if (!Instructions.TryGetValue(value, out var instruction))
            {
                throw new InvalidOperationException($"{Describe(method)}: byte {at} of its IL is no opcode (0x{value:X}).");
            }

            at += instruction.Size;
            switch (instruction.OperandType)
            {
                case OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, at);
                    MemberInfo? member;
                    try
                    {
                        member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                    }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException or MissingMemberException)
                    {
                        throw new InvalidOperationException($"{Describe(method)}: the token 0x{token:X8} at byte {at} could not be read ({ex.Message}).", ex);
                    }

                    if (member is not null)
                    {
                        found.Add(new Reference(method, member, instruction));
                    }

                    at += 4;
                    break;

                case OperandType.InlineSwitch:
                    at += 4 + (4 * BitConverter.ToInt32(il, at));
                    break;

                default:
                    at += OperandSize(instruction.OperandType);
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Every reference made by every method reachable from <paramref name="roots"/> through the suite's own code (this
    /// assembly's): a method of it that is called, constructed or pointed to is read in turn, and so is the state machine of an
    /// async or iterator method, and every override or implementation, in this assembly, of an overridable method the suite
    /// itself declares (its own abstract classes and interfaces; an override of ToString or Dispose is not followed from the
    /// slot another assembly declares). A method of another assembly is named, never read.
    /// </summary>
    public static IReadOnlyList<Reference> Reach(IEnumerable<MethodBase> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var suite = typeof(CompiledCode).Module;
        var seen = new HashSet<MethodBase>();
        var waiting = new Queue<MethodBase>();
        var found = new List<Reference>();

        void Read(MethodBase method)
        {
            if (method.Module != suite)
            {
                return;
            }

            var definition = Definition(method);
            if (seen.Add(definition))
            {
                waiting.Enqueue(definition);
            }
        }

        foreach (var root in roots)
        {
            Read(root);
        }

        while (waiting.TryDequeue(out var method))
        {
            if (method.GetCustomAttribute<StateMachineAttribute>() is { StateMachineType: { } machine })
            {
                foreach (var step in MethodsOf(machine))
                {
                    Read(step);
                }
            }

            if (method is MethodInfo { IsVirtual: true } overridable && overridable.GetBaseDefinition().Module == suite)
            {
                foreach (var implementation in ImplementationsOf(overridable))
                {
                    Read(implementation);
                }
            }

            foreach (var reference in ReferencesOf(method))
            {
                found.Add(reference);
                if (reference.To is MethodBase called)
                {
                    Read(called);
                }
            }
        }

        return found;
    }

    /// <summary>The type <paramref name="type"/> is nested in at the top: the one a source file declares.</summary>
    public static Type Outermost(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    /// <summary>A method as a failure message names it: its type's full name and its own.</summary>
    public static string Describe(MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return $"{member.DeclaringType?.FullName ?? "?"}.{member.Name}";
    }

    /// <summary>The method as it is declared: not an instantiation of a generic one, or one of a generic type.</summary>
    private static MethodBase Definition(MethodBase method) =>
        method.Module.ResolveMethod(method.MetadataToken) ?? method;

    /// <summary>Every override or implementation, in this assembly, of <paramref name="method"/>, an overridable method.</summary>
    private static IEnumerable<MethodBase> ImplementationsOf(MethodInfo method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null || declaring.Module != typeof(CompiledCode).Module)
        {
            yield break;
        }

        foreach (var type in declaring.Module.GetTypes().Where(t => t != declaring && !t.IsInterface))
        {
            if (declaring.IsInterface)
            {
                if (!type.IsGenericTypeDefinition && declaring.IsAssignableFrom(type) && !declaring.IsGenericTypeDefinition)
                {
                    var map = type.GetInterfaceMap(declaring);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].MetadataToken == method.MetadataToken && map.TargetMethods[i].Module == declaring.Module)
                        {
                            yield return map.TargetMethods[i];
                        }
                    }
                }

                continue;
            }

            foreach (var candidate in type.GetMethods(Declared).Where(m => m.IsVirtual && m.Name == method.Name))
            {
                if (candidate.GetBaseDefinition().MetadataToken == method.GetBaseDefinition().MetadataToken
                    && candidate.GetBaseDefinition().Module == method.GetBaseDefinition().Module)
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>The size of an operand of <paramref name="type"/>, other than a token's or a switch's.</summary>
    private static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => throw new InvalidOperationException($"No operand size is known for {type}."),
    };
}
