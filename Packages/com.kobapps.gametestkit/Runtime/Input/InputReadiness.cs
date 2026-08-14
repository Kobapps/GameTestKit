using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Checks that uGUI is actually in a state to receive the input a run is about to send it.
    /// </summary>
    /// <remarks>
    /// Every problem here fails the same way — silently. The kit dispatches a perfectly good click at
    /// the right coordinates, the element is found, visible and interactable, the step passes, and
    /// nothing in the game reacts. The test then dies at whatever it waited for next, pointing at a
    /// symptom several steps away from the cause, with nothing in the console.
    /// <para>
    /// The most expensive of these is an <c>InputSystemUIInputModule</c> added from a script: created
    /// that way it has no actions assigned and drops every pointer event on the floor. Unity's
    /// <c>GameObject ▸ UI ▸ Event System</c> menu assigns them; <c>AddComponent</c> does not.
    /// </para>
    /// </remarks>
    public static class InputReadiness
    {
        /// <summary>Problems worth telling somebody about. Empty when uGUI can receive input.</summary>
        public static List<string> Check()
        {
            var problems = new List<string>();

            var system = EventSystem.current;
            if (system == null)
            {
                // Only worth saying when there is uGUI on screen right now. A game that builds its
                // canvas later — which includes every test whose first step spawns the UI — has no
                // EventSystem at the moment a run starts, and warning about that every single time
                // trains people to ignore the one run where it is the actual fault.
                if (HasLiveCanvas())
                    problems.Add("There is a Canvas on screen but no EventSystem, so no uGUI element " +
                                 "can receive a click, a drag or typed text. Add one with " +
                                 "GameObject ▸ UI ▸ Event System.");

                return problems;
            }

            if (!system.isActiveAndEnabled)
                problems.Add($"The EventSystem on '{system.gameObject.name}' is disabled, so uGUI will " +
                             "ignore every simulated interaction.");

            var module = system.currentInputModule;
            if (module == null)
            {
                problems.Add($"The EventSystem on '{system.gameObject.name}' has no active input module, " +
                             "so uGUI will ignore every simulated interaction.");
                return problems;
            }

#if ENABLE_INPUT_SYSTEM
            CheckInputSystemModule(module, problems);
#endif
            return problems;
        }

#if ENABLE_INPUT_SYSTEM
        private static void CheckInputSystemModule(BaseInputModule module, ICollection<string> problems)
        {
            var type = module.GetType();
            if (type.Name != "InputSystemUIInputModule") return;

            // Reflected rather than referenced: the field names are stable but the package is optional
            // at compile time for consumers who force the EventSystem backend.
            var pointAction = type.GetProperty("point")?.GetValue(module);
            var clickAction = type.GetProperty("leftClick")?.GetValue(module);

            if (pointAction != null && clickAction != null) return;

            problems.Add(
                $"The InputSystemUIInputModule on '{module.gameObject.name}' has no actions assigned, so " +
                "it silently ignores every pointer event — including simulated ones. A module added with " +
                "AddComponent starts out this way; call AssignDefaultActions() on it, or create the " +
                "EventSystem from GameObject ▸ UI ▸ Event System instead.");
        }
#endif

        /// <summary>True when something is actually drawing uGUI at this moment.</summary>
        private static bool HasLiveCanvas()
        {
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
                if (canvas != null && canvas.isActiveAndEnabled) return true;

            return false;
        }

        /// <summary>Writes any problems to the console, once, as warnings.</summary>
        public static void Report()
        {
            foreach (var problem in Check())
                Debug.LogWarning($"[GameTestKit] {problem}");
        }
    }
}
