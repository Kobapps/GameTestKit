using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Marks a static property, field or method as reachable from test scripts.
    /// Properties/fields become readable values (<c>assert: "player.gold &gt;= 100"</c>);
    /// methods become callable actions (<c>{"call": "giveGold", "args": [100]}</c>).
    /// A method returning <see cref="IEnumerator"/> is run as a coroutine and awaited.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method)]
    public sealed class GameTestBindingAttribute : Attribute
    {
        public string Path { get; }
        public string Description { get; set; }

        public GameTestBindingAttribute(string path) { Path = path; }
    }

    public enum BindingKind { Value, Action }

    public sealed class BindingInfo
    {
        public string Path;
        public BindingKind Kind;
        public string Description;
        public string ValueTypeName;
        public string[] ParameterNames = Array.Empty<string>();
    }

    /// <summary>
    /// The bridge between declarative test scripts and your game's internals: named values tests can
    /// assert on, and named actions tests can invoke. Registrations are global and survive scene loads;
    /// they are cleared by a domain reload, so register them from
    /// <c>[RuntimeInitializeOnLoadMethod]</c> or a bootstrap object.
    /// </summary>
    /// <example>
    /// <code>
    /// GameTestBindings.BindValue("player.gold", () =&gt; Player.Instance.Gold, "Soft currency");
    /// GameTestBindings.BindAction("grantGold", args =&gt; Player.Instance.Gold += Convert.ToInt32(args[0]));
    /// </code>
    /// </example>
    public static class GameTestBindings
    {
        private sealed class ValueBinding
        {
            public Func<object> Getter;
            public string Description;
            public string TypeName;
        }

        private sealed class ActionBinding
        {
            public Func<object[], object> Invoke;
            public string Description;
            public string[] ParameterNames = Array.Empty<string>();
        }

        private static readonly Dictionary<string, ValueBinding> Values =
            new Dictionary<string, ValueBinding>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ActionBinding> Actions =
            new Dictionary<string, ActionBinding>(StringComparer.OrdinalIgnoreCase);

        private static bool _scanned;

        /// <summary>Expose a readable value to test expressions.</summary>
        public static void BindValue(string path, Func<object> getter, string description = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Binding path is required.", nameof(path));
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            Values[path] = new ValueBinding { Getter = getter, Description = description };
        }

        /// <summary>Expose a callable action. Args come from the script's <c>args</c> array.</summary>
        public static void BindAction(string name, Action<object[]> action, string description = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Actions[name] = new ActionBinding
            {
                Invoke = args => { action(args); return null; },
                Description = description,
            };
        }

        /// <summary>Expose a coroutine the runner will await before moving to the next step.</summary>
        public static void BindCoroutine(string name, Func<object[], IEnumerator> routine, string description = null)
        {
            if (routine == null) throw new ArgumentNullException(nameof(routine));
            Actions[name] = new ActionBinding
            {
                Invoke = args => routine(args),
                Description = description,
            };
        }

        public static void Unbind(string path)
        {
            Values.Remove(path);
            Actions.Remove(path);
        }

        /// <summary>Drops every registration, including attribute-discovered ones.</summary>
        public static void Clear()
        {
            Values.Clear();
            Actions.Clear();
            _scanned = false;
        }

        public static bool TryGetValue(string path, out object value)
        {
            EnsureScanned();
            value = null;
            if (!Values.TryGetValue(path, out var binding)) return false;
            value = binding.Getter();
            return true;
        }

        public static bool HasAction(string name)
        {
            EnsureScanned();
            return Actions.ContainsKey(name);
        }

        /// <summary>
        /// Invokes a bound action. Returns an <see cref="IEnumerator"/> when the binding is a coroutine
        /// (the caller should yield it), otherwise null.
        /// </summary>
        public static IEnumerator Invoke(string name, object[] args)
        {
            EnsureScanned();
            if (!Actions.TryGetValue(name, out var binding))
                throw new TestFailureException(
                    $"No action named '{name}' is bound. Bind it with GameTestBindings.BindAction(\"{name}\", …) " +
                    "or mark a static method with [GameTestBinding].");

            var result = binding.Invoke(args ?? Array.Empty<object>());
            return result as IEnumerator;
        }

        /// <summary>Everything currently bound — used by the AI catalogue and the GameTester window.</summary>
        public static List<BindingInfo> Describe()
        {
            EnsureScanned();
            var list = new List<BindingInfo>();
            foreach (var kv in Values)
            {
                string typeName = kv.Value.TypeName;
                if (string.IsNullOrEmpty(typeName))
                {
                    try { typeName = kv.Value.Getter()?.GetType().Name; }
                    catch { typeName = "?"; }
                }
                list.Add(new BindingInfo
                {
                    Path = kv.Key,
                    Kind = BindingKind.Value,
                    Description = kv.Value.Description,
                    ValueTypeName = typeName ?? "null",
                });
            }
            foreach (var kv in Actions)
            {
                list.Add(new BindingInfo
                {
                    Path = kv.Key,
                    Kind = BindingKind.Action,
                    Description = kv.Value.Description,
                    ParameterNames = kv.Value.ParameterNames,
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return list;
        }

        /// <summary>Reflect over loaded assemblies once, picking up <see cref="GameTestBindingAttribute"/>.</summary>
        public static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System", StringComparison.Ordinal) ||
                    name.StartsWith("mscorlib", StringComparison.Ordinal) ||
                    name.StartsWith("netstandard", StringComparison.Ordinal) ||
                    name.StartsWith("Mono.", StringComparison.Ordinal) ||
                    name.StartsWith("nunit", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEditor", StringComparison.Ordinal))
                    continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    try { ScanType(type); }
                    catch (Exception e) { Debug.LogWarning($"[GameTestKit] Failed to scan {type.FullName}: {e.Message}"); }
                }
            }
        }

        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static void ScanType(Type type)
        {
            foreach (var property in type.GetProperties(StaticMembers))
            {
                var attr = property.GetCustomAttribute<GameTestBindingAttribute>();
                if (attr == null || !property.CanRead) continue;
                var getter = property.GetGetMethod(true);
                Values[attr.Path] = new ValueBinding
                {
                    Getter = () => getter.Invoke(null, null),
                    Description = attr.Description,
                    TypeName = property.PropertyType.Name,
                };
            }

            foreach (var field in type.GetFields(StaticMembers))
            {
                var attr = field.GetCustomAttribute<GameTestBindingAttribute>();
                if (attr == null) continue;
                Values[attr.Path] = new ValueBinding
                {
                    Getter = () => field.GetValue(null),
                    Description = attr.Description,
                    TypeName = field.FieldType.Name,
                };
            }

            foreach (var method in type.GetMethods(StaticMembers))
            {
                var attr = method.GetCustomAttribute<GameTestBindingAttribute>();
                if (attr == null) continue;

                var parameters = method.GetParameters();
                var names = new string[parameters.Length];
                for (int i = 0; i < parameters.Length; i++) names[i] = parameters[i].Name;

                // Parameterless getters read like values; anything else is an action.
                if (parameters.Length == 0 && method.ReturnType != typeof(void) &&
                    !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
                {
                    Values[attr.Path] = new ValueBinding
                    {
                        Getter = () => method.Invoke(null, null),
                        Description = attr.Description,
                        TypeName = method.ReturnType.Name,
                    };
                    continue;
                }

                var captured = method;
                var captureParams = parameters;
                Actions[attr.Path] = new ActionBinding
                {
                    Invoke = args => captured.Invoke(null, CoerceArguments(captureParams, args)),
                    Description = attr.Description,
                    ParameterNames = names,
                };
            }
        }

        private static object[] CoerceArguments(ParameterInfo[] parameters, object[] args)
        {
            if (parameters.Length == 0) return Array.Empty<object>();
            var result = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                object value = args != null && i < args.Length ? args[i] : null;
                var target = parameters[i].ParameterType;
                if (value == null)
                {
                    result[i] = parameters[i].HasDefaultValue
                        ? parameters[i].DefaultValue
                        : (target.IsValueType ? Activator.CreateInstance(target) : null);
                    continue;
                }
                if (target.IsInstanceOfType(value)) { result[i] = value; continue; }
                try { result[i] = Convert.ChangeType(value, target); }
                catch
                {
                    throw new TestFailureException(
                        $"Argument {i} ('{parameters[i].Name}') expects {target.Name} but got '{value}'.");
                }
            }
            return result;
        }
    }
}
