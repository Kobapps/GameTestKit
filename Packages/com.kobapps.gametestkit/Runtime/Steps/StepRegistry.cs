using System;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>Documentation for one parameter of a step, surfaced to authors, validators and agents.</summary>
    public sealed class StepParameter
    {
        public string Name;
        public string Type;          // selector | string | number | bool | expression | array | steps
        public bool Required;
        public string Default;
        public string Description;

        public StepParameter(string name, string type, string description, bool required = false, string @default = null)
        {
            Name = name; Type = type; Description = description; Required = required; Default = @default;
        }
    }

    /// <summary>
    /// A step kind: its JSON key, what it does, its parameters, and how to build it from JSON.
    /// </summary>
    /// <remarks>
    /// The registry is the single source of truth. The script parser, the validator, the GameTester
    /// window and the generated AI skill all read from it, so a custom step registered by a game shows
    /// up everywhere without touching the framework.
    /// </remarks>
    public sealed class StepDefinition
    {
        public string Key;
        public string[] Aliases = Array.Empty<string>();
        public string Summary;
        public string Category = "General";
        public StepParameter[] Parameters = Array.Empty<StepParameter>();
        public string Example;
        public Func<JsonValue, TestStep> Factory;
    }

    /// <summary>
    /// Marks a static, parameterless method that registers a game's own step verbs.
    /// </summary>
    /// <remarks>
    /// Put it on the method that calls <see cref="StepRegistry.Register"/>, and the verbs exist everywhere
    /// the catalogue is read — the Editor window, the script validator, the generated AI skill and a run —
    /// instead of only inside play mode. The method is invoked once per domain, before the first lookup.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class GameTestStepsAttribute : Attribute
    {
    }

    public static class StepRegistry
    {
        private static readonly Dictionary<string, StepDefinition> ByKey =
            new Dictionary<string, StepDefinition>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<StepDefinition> Definitions = new List<StepDefinition>();

        private static bool _builtinsRegistered;

        public static IReadOnlyList<StepDefinition> All
        {
            get { EnsureBuiltins(); return Definitions; }
        }

        /// <summary>
        /// Adds a step kind. Call this from <c>[RuntimeInitializeOnLoadMethod]</c> to teach the
        /// framework a verb that only your game has — <c>{"castSpell": "fireball"}</c>.
        /// </summary>
        public static void Register(StepDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrEmpty(definition.Key)) throw new ArgumentException("Step key is required.");
            if (definition.Factory == null) throw new ArgumentException($"Step '{definition.Key}' has no factory.");

            EnsureBuiltins();

            if (ByKey.TryGetValue(definition.Key, out var existing))
                Definitions.Remove(existing);

            Definitions.Add(definition);
            ByKey[definition.Key] = definition;
            foreach (var alias in definition.Aliases)
                ByKey[alias] = definition;
        }

        public static bool TryGet(string key, out StepDefinition definition)
        {
            EnsureBuiltins();
            return ByKey.TryGetValue(key ?? string.Empty, out definition);
        }

        /// <summary>
        /// Builds a step from one JSON object. The step kind is whichever registered key the object
        /// carries (<c>{"click": "#Play"}</c>), or the explicit <c>"step"</c> field.
        /// </summary>
        public static TestStep Create(JsonValue json)
        {
            EnsureBuiltins();

            if (json == null || !json.IsObject)
                throw new TestFailureException($"A step must be a JSON object, got {json?.Type.ToString() ?? "nothing"}.");

            StepDefinition definition = null;

            if (json.Has("step"))
            {
                var key = json["step"].AsString();
                if (!ByKey.TryGetValue(key ?? "", out definition))
                    throw new TestFailureException($"Unknown step '{key}'. {KnownKeys()}");
            }
            else
            {
                foreach (var key in json.Keys)
                {
                    if (ByKey.TryGetValue(key, out definition)) break;
                    definition = null;
                }

                if (definition == null)
                    throw new TestFailureException(
                        $"No known step verb in {{{string.Join(", ", json.Keys)}}}. {KnownKeys()}");
            }

            var step = definition.Factory(json);

            if (json.Has("label")) step.Label = json["label"].AsString();
            if (json.Has("timeout")) step.TimeoutSeconds = json["timeout"].AsFloat();
            if (json.Has("continueOnFailure")) step.ContinueOnFailure = json["continueOnFailure"].AsBool();
            if (json.Has("soft")) step.ContinueOnFailure = json["soft"].AsBool();

            return step;
        }

        private static string KnownKeys()
        {
            var keys = new List<string>();
            foreach (var definition in All) keys.Add(definition.Key);
            keys.Sort(StringComparer.Ordinal);
            return "Known steps: " + string.Join(", ", keys) + ".";
        }

        internal static void EnsureBuiltins()
        {
            if (_builtinsRegistered) return;
            _builtinsRegistered = true;
            BuiltinSteps.RegisterAll();
            BotSteps.RegisterAll();
            EventSteps.Register();
            RegisterDiscovered();
        }

        /// <summary>
        /// Runs every <see cref="GameTestStepsAttribute"/>-marked registrar in the loaded assemblies.
        /// </summary>
        /// <remarks>
        /// A game's verbs have to exist wherever the catalogue is read, not just where tests run. Bootstrap
        /// registration from <c>[RuntimeInitializeOnLoadMethod]</c> only fires in play mode, so the Editor
        /// window, the validator and the generated AI skill would all report a game's own scripts as using
        /// an unknown verb. Discovering registrars by attribute — the same way <see cref="GameTestBindings"/>
        /// discovers values — makes a custom verb behave like a built-in one everywhere.
        /// </remarks>
        private static void RegisterDiscovered()
        {
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
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null) continue;

                    System.Reflection.MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(System.Reflection.BindingFlags.Static |
                                                  System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.NonPublic |
                                                  System.Reflection.BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (var method in methods)
                    {
                        if (method.GetParameters().Length != 0) continue;
                        if (!Attribute.IsDefined(method, typeof(GameTestStepsAttribute))) continue;

                        try { method.Invoke(null, null); }
                        catch (Exception e)
                        {
                            Debug.LogError(
                                $"[GameTestKit] Step registrar {type.FullName}.{method.Name} threw: {e.InnerException?.Message ?? e.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>The whole catalogue as JSON — what the AI skill and the validator read.</summary>
        public static JsonValue DescribeCatalogue()
        {
            var array = JsonValue.NewArray();
            foreach (var definition in All)
            {
                var item = JsonValue.NewObject()
                    .Set("key", definition.Key)
                    .Set("category", definition.Category)
                    .Set("summary", definition.Summary ?? "");

                if (definition.Aliases.Length > 0)
                {
                    var aliases = JsonValue.NewArray();
                    foreach (var alias in definition.Aliases) aliases.Add(JsonValue.New(alias));
                    item.Set("aliases", aliases);
                }

                var parameters = JsonValue.NewArray();
                foreach (var parameter in definition.Parameters)
                {
                    var entry = JsonValue.NewObject()
                        .Set("name", parameter.Name)
                        .Set("type", parameter.Type)
                        .Set("required", parameter.Required)
                        .Set("description", parameter.Description ?? "");
                    if (parameter.Default != null) entry.Set("default", parameter.Default);
                    parameters.Add(entry);
                }
                item.Set("parameters", parameters);

                if (!string.IsNullOrEmpty(definition.Example)) item.Set("example", definition.Example);
                array.Add(item);
            }
            return array;
        }
    }

    /// <summary>Shared JSON reading helpers so every step reports the same kind of error.</summary>
    /// <summary>
    /// Parameter readers for step factories, with the error messages authors should see.
    /// </summary>
    /// <remarks>
    /// Public because a game registering its own verbs through <see cref="StepRegistry.Register"/> needs
    /// exactly the same parsing — and, more importantly, the same failure text. A custom step that reports
    /// a missing parameter differently from a built-in one makes the format feel like two formats.
    /// </remarks>
    public static class StepJson
    {
        public static string RequiredString(JsonValue json, string key, string stepName)
        {
            var value = json[key];
            if (value.IsNull || string.IsNullOrEmpty(value.AsString()))
                throw new TestFailureException($"Step '{stepName}' needs a \"{key}\" value.");
            return value.AsString();
        }

        public static string OptionalString(JsonValue json, string key, string fallback = null)
        {
            var value = json[key];
            return value.IsNull ? fallback : value.AsString(fallback);
        }

        public static float Float(JsonValue json, string key, float fallback)
        {
            var value = json[key];
            return value.IsNull ? fallback : value.AsFloat(fallback);
        }

        public static int Int(JsonValue json, string key, int fallback)
        {
            var value = json[key];
            return value.IsNull ? fallback : value.AsInt(fallback);
        }

        public static bool Bool(JsonValue json, string key, bool fallback)
        {
            var value = json[key];
            return value.IsNull ? fallback : value.AsBool(fallback);
        }

        /// <summary>Reads <c>[x, y]</c> or <c>{"x":…,"y":…}</c>.</summary>
        public static Vector2? Vector(JsonValue json, string key)
        {
            var value = json[key];
            if (value.IsNull) return null;
            if (value.IsArray && value.Count >= 2) return new Vector2(value[0].AsFloat(), value[1].AsFloat());
            if (value.IsObject) return new Vector2(value["x"].AsFloat(), value["y"].AsFloat());
            return null;
        }

        public static PointerButton Button(JsonValue json, string key)
        {
            var name = OptionalString(json, key, "left");
            switch ((name ?? "left").Trim().ToLowerInvariant())
            {
                case "right": return PointerButton.Right;
                case "middle": return PointerButton.Middle;
                default: return PointerButton.Left;
            }
        }

        public static List<TestStep> Steps(JsonValue json, string key, string stepName)
        {
            var array = json[key];
            if (!array.IsArray)
                throw new TestFailureException($"Step '{stepName}' needs a \"{key}\" array of steps.");

            var steps = new List<TestStep>();
            foreach (var item in array) steps.Add(StepRegistry.Create(item));
            return steps;
        }

        public static object[] Args(JsonValue json, string key)
        {
            var value = json[key];
            if (value.IsNull) return Array.Empty<object>();
            if (!value.IsArray) return new[] { value.AsObject() };

            var args = new object[value.Count];
            for (int i = 0; i < value.Count; i++) args[i] = value[i].AsObject();
            return args;
        }
    }
}
