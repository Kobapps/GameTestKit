using System.Collections;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kobapps.GameTestKit
{
    /// <summary>Pauses for a fixed time. Prefer <see cref="WaitForStep"/> — sleeps make tests slow and flaky.</summary>
    public sealed class WaitStep : TestStep
    {
        public float Seconds = 1f;
        public bool Realtime;

        public override string Describe() => $"wait {Seconds:0.##}s";

        public override IEnumerator Execute(TestContext ctx) =>
            Realtime ? Wait.RealSeconds(Seconds) : Wait.Seconds(Seconds);
    }

    /// <summary>
    /// Waits until an expression becomes true. This is the right way to synchronise with the game:
    /// it finishes the instant the condition holds and fails with the last evaluation on timeout.
    /// </summary>
    public sealed class WaitForStep : TestStep
    {
        public string Expression;
        public float Timeout = 10f;

        public override string Describe() => $"wait for {Expression}";

        public override IEnumerator Execute(TestContext ctx)
        {
            float deadline = Time.realtimeSinceStartup + Timeout;
            string lastError = null;

            while (true)
            {
                if (Scripting.Expression.TryEvaluateBool(Expression, out var result, out var error))
                {
                    if (result) yield break;
                    lastError = null;
                }
                else
                {
                    lastError = error;
                }

                if (Time.realtimeSinceStartup >= deadline)
                {
                    throw new TestFailureException(lastError != null
                        ? $"Waited {Timeout:0.#}s for '{Expression}' but it could not be evaluated: {lastError}"
                        : $"Waited {Timeout:0.#}s but '{Expression}' never became true.");
                }

                yield return null;
            }
        }
    }

    /// <summary>Waits for an element to appear (or, with <see cref="Gone"/>, to disappear).</summary>
    public sealed class WaitForElementStep : TestStep
    {
        public string Selector;
        public float Timeout = 10f;
        public bool Gone;
        public bool RequireInteractable;

        public override string Describe() => Gone ? $"wait for {Selector} to go away" : $"wait for {Selector}";

        public override IEnumerator Execute(TestContext ctx)
        {
            if (Gone)
            {
                yield return Wait.Until(
                    () =>
                    {
                        var matches = Locator.FindAll(Selector);
                        for (int i = 0; i < matches.Count; i++)
                            if (UiProbe.IsVisible(matches[i])) return false;
                        return true;
                    },
                    Timeout, $"'{Selector}' to disappear");
                yield break;
            }

            var requirement = RequireInteractable
                ? LocatorEngine.Requirement.Clickable
                : LocatorEngine.Requirement.Visible;

            var target = new ResolvedTarget();
            yield return ctx.Locate.Resolve(Selector, target, requirement, Timeout);
        }
    }

    /// <summary>Waits until a scene is loaded and (by default) active.</summary>
    public sealed class WaitForSceneStep : TestStep
    {
        public string SceneName;
        public float Timeout = 30f;
        public bool MustBeActive = true;

        public override string Describe() => $"wait for scene {SceneName}";

        public override IEnumerator Execute(TestContext ctx)
        {
            yield return Wait.Until(() =>
            {
                if (MustBeActive)
                    return string.Equals(SceneManager.GetActiveScene().name, SceneName,
                        System.StringComparison.OrdinalIgnoreCase);

                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded &&
                        string.Equals(scene.name, SceneName, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }, Timeout, $"scene '{SceneName}'");
        }
    }

    /// <summary>Runs child steps several times — soak loops, "buy five of them", stress paths.</summary>
    public sealed class RepeatStep : TestStep
    {
        public int Times = 1;
        public List<TestStep> Steps = new List<TestStep>();

        public override string Describe() => $"repeat ×{Times}";

        public override IEnumerator Execute(TestContext ctx)
        {
            var record = ctx.CurrentStep;
            for (int iteration = 0; iteration < Times; iteration++)
            {
                foreach (var step in Steps)
                    yield return ctx.RunNested(step, record);
            }
        }
    }

    /// <summary>Names a block of steps so the report reads like the flow it is testing.</summary>
    public sealed class GroupStep : TestStep
    {
        public string Name = "group";
        public List<TestStep> Steps = new List<TestStep>();

        public override string Describe() => Name;

        public override IEnumerator Execute(TestContext ctx)
        {
            var record = ctx.CurrentStep;
            foreach (var step in Steps)
                yield return ctx.RunNested(step, record);
        }
    }

    /// <summary>Writes a line into the log and the report.</summary>
    public sealed class LogStep : TestStep
    {
        public string Message = "";

        public override string Describe() => $"log \"{Message}\"";

        public override IEnumerator Execute(TestContext ctx)
        {
            ctx.Log(Message);
            yield break;
        }
    }

    /// <summary>Captures the screen into the run's artifact folder.</summary>
    public sealed class ScreenshotStep : TestStep
    {
        public string Name = "screenshot";

        public override string Describe() => $"screenshot \"{Name}\"";

        public override IEnumerator Execute(TestContext ctx) => ctx.Screenshot(Name);
    }

    /// <summary>
    /// Calls a game action registered with <see cref="GameTestBindings"/> — the sanctioned way for a
    /// test to reach into the game (grant currency, unlock a level, fake a server response).
    /// </summary>
    public sealed class CallStep : TestStep
    {
        public string Action;
        public object[] Args = System.Array.Empty<object>();

        public override string Describe() => $"call {Action}()";

        public override IEnumerator Execute(TestContext ctx)
        {
            var routine = GameTestBindings.Invoke(Action, Args);
            if (routine != null) yield return routine;
            else yield return null;
        }
    }

    /// <summary>Changes <c>Time.timeScale</c>: fast-forward a slow animation, or slow one down to watch.</summary>
    public sealed class TimeScaleStep : TestStep
    {
        public float Scale = 1f;

        public override string Describe() => $"timeScale = {Scale:0.##}";

        public override IEnumerator Execute(TestContext ctx)
        {
            Time.timeScale = Mathf.Max(0f, Scale);
            yield return null;
        }
    }

    /// <summary>Loads a scene and waits for it to finish, so the next step sees the new hierarchy.</summary>
    public sealed class LoadSceneStep : TestStep
    {
        public string SceneName;
        public bool Additive;
        public float Timeout = 60f;

        public override string Describe() => $"load scene {SceneName}{(Additive ? " (additive)" : "")}";

        public override IEnumerator Execute(TestContext ctx)
        {
            AsyncOperation operation;
            try
            {
                operation = SceneManager.LoadSceneAsync(SceneName,
                    Additive ? LoadSceneMode.Additive : LoadSceneMode.Single);
            }
            catch (System.Exception e)
            {
                throw new TestFailureException($"Could not load scene '{SceneName}': {e.Message}");
            }

            if (operation == null)
                throw new TestFailureException(
                    $"Scene '{SceneName}' is not in the build settings (File ▸ Build Profiles ▸ Scene List).");

            yield return Wait.Operation(operation, Timeout, $"scene '{SceneName}' to load");
            yield return null; // let Awake/Start run before the next step looks for anything
        }
    }

    /// <summary>Unloads an additively loaded scene.</summary>
    public sealed class UnloadSceneStep : TestStep
    {
        public string SceneName;
        public float Timeout = 30f;

        public override string Describe() => $"unload scene {SceneName}";

        public override IEnumerator Execute(TestContext ctx)
        {
            var operation = SceneManager.UnloadSceneAsync(SceneName);
            if (operation == null)
                throw new TestFailureException($"Scene '{SceneName}' is not loaded, so it cannot be unloaded.");

            yield return Wait.Operation(operation, Timeout, $"scene '{SceneName}' to unload");
        }
    }
}
