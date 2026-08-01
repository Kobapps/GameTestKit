using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Kobapps.GameTestKit
{
    /// <summary>One element in a scene snapshot.</summary>
    public sealed class ElementInfo
    {
        public string Selector;          // the selector we recommend for this element
        public string Path;
        public string Kind;              // Button, Toggle, InputField, Text, Collider, …
        public string Text;
        public string Description;       // from TestId
        public Rect ScreenRect;
        public bool Visible;
        public bool Interactable;
        public string BlockedBy;
        public List<string> AlternativeSelectors = new List<string>();
    }

    /// <summary>
    /// A machine-readable picture of what is on screen right now: which elements exist, where they
    /// are, whether they can be clicked, and what the game state bindings say.
    /// </summary>
    /// <remarks>
    /// This is the equivalent of "view the page" for a game. An agent writing or repairing a test calls
    /// it to discover real selectors instead of guessing names — which is the difference between a test
    /// that runs and one that fails on its first step.
    /// </remarks>
    public sealed class SceneSnapshot
    {
        public string ActiveScene;
        public List<string> LoadedScenes = new List<string>();
        public int ScreenWidth;
        public int ScreenHeight;
        public float TimeScale;
        public float RealtimeSinceStartup;
        public bool HasEventSystem;
        public string InputModule;
        public List<ElementInfo> Interactables = new List<ElementInfo>();
        public List<ElementInfo> Labels = new List<ElementInfo>();
        public List<BindingInfo> Bindings = new List<BindingInfo>();

        public JsonValue ToJson()
        {
            var root = JsonValue.NewObject();
            root.Set("activeScene", ActiveScene ?? "");

            var scenes = JsonValue.NewArray();
            foreach (var scene in LoadedScenes) scenes.Add(JsonValue.New(scene));
            root.Set("loadedScenes", scenes);

            root.Set("screen", JsonValue.NewObject().Set("width", ScreenWidth).Set("height", ScreenHeight));
            root.Set("timeScale", TimeScale);
            root.Set("realtime", RealtimeSinceStartup);
            root.Set("hasEventSystem", HasEventSystem);
            root.Set("inputModule", InputModule ?? "none");

            root.Set("interactables", Elements(Interactables));
            root.Set("labels", Elements(Labels));

            var bindings = JsonValue.NewArray();
            foreach (var binding in Bindings)
            {
                var item = JsonValue.NewObject()
                    .Set("path", binding.Path)
                    .Set("kind", binding.Kind == BindingKind.Value ? "value" : "action");
                if (!string.IsNullOrEmpty(binding.Description)) item.Set("description", binding.Description);
                if (!string.IsNullOrEmpty(binding.ValueTypeName)) item.Set("type", binding.ValueTypeName);
                if (binding.ParameterNames != null && binding.ParameterNames.Length > 0)
                {
                    var parameters = JsonValue.NewArray();
                    foreach (var name in binding.ParameterNames) parameters.Add(JsonValue.New(name));
                    item.Set("parameters", parameters);
                }
                bindings.Add(item);
            }
            root.Set("bindings", bindings);

            return root;
        }

        private static JsonValue Elements(List<ElementInfo> elements)
        {
            var array = JsonValue.NewArray();
            foreach (var element in elements)
            {
                var item = JsonValue.NewObject()
                    .Set("selector", element.Selector)
                    .Set("kind", element.Kind)
                    .Set("path", element.Path);

                if (!string.IsNullOrEmpty(element.Text)) item.Set("text", element.Text);
                if (!string.IsNullOrEmpty(element.Description)) item.Set("description", element.Description);

                item.Set("rect", JsonValue.NewObject()
                    .Set("x", Mathf.Round(element.ScreenRect.x))
                    .Set("y", Mathf.Round(element.ScreenRect.y))
                    .Set("width", Mathf.Round(element.ScreenRect.width))
                    .Set("height", Mathf.Round(element.ScreenRect.height)));

                item.Set("visible", element.Visible);
                item.Set("interactable", element.Interactable);
                if (!string.IsNullOrEmpty(element.BlockedBy)) item.Set("blockedBy", element.BlockedBy);

                if (element.AlternativeSelectors.Count > 1)
                {
                    var alternatives = JsonValue.NewArray();
                    for (int i = 1; i < element.AlternativeSelectors.Count; i++)
                        alternatives.Add(JsonValue.New(element.AlternativeSelectors[i]));
                    item.Set("alsoMatches", alternatives);
                }

                array.Add(item);
            }
            return array;
        }

        /// <summary>Compact text rendering — what a human (or an agent's log) reads at a glance.</summary>
        public string ToText()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"Scene: {ActiveScene}   screen: {ScreenWidth}x{ScreenHeight}   timeScale: {TimeScale}");
            builder.AppendLine($"EventSystem: {(HasEventSystem ? InputModule : "none")}");

            builder.AppendLine($"\nInteractable ({Interactables.Count}):");
            foreach (var element in Interactables)
            {
                var state = element.Interactable ? "" : element.Visible ? "  [disabled]" : "  [hidden]";
                if (!string.IsNullOrEmpty(element.BlockedBy)) state += $"  [covered by {element.BlockedBy}]";
                var label = string.IsNullOrEmpty(element.Text) ? "" : $" \"{element.Text}\"";
                builder.AppendLine($"  {element.Selector,-40} {element.Kind}{label}{state}");
            }

            if (Labels.Count > 0)
            {
                builder.AppendLine($"\nText on screen ({Labels.Count}):");
                foreach (var element in Labels)
                    builder.AppendLine($"  \"{element.Text}\"   ({element.Selector})");
            }

            if (Bindings.Count > 0)
            {
                builder.AppendLine($"\nGame state bindings ({Bindings.Count}):");
                foreach (var binding in Bindings)
                    builder.AppendLine($"  {binding.Path,-30} {(binding.Kind == BindingKind.Value ? "value" : "action")}" +
                                       (string.IsNullOrEmpty(binding.Description) ? "" : $"  — {binding.Description}"));
            }

            return builder.ToString();
        }
    }

    public static class SceneInspector
    {
        /// <summary>Snapshots the current screen. Safe to call any time, including between steps.</summary>
        public static SceneSnapshot Capture(int maxElements = 250, bool includeHidden = false)
        {
            var snapshot = new SceneSnapshot
            {
                ActiveScene = SceneManager.GetActiveScene().name,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                TimeScale = Time.timeScale,
                RealtimeSinceStartup = Time.realtimeSinceStartup,
                HasEventSystem = EventSystem.current != null,
                Bindings = GameTestBindings.Describe(),
            };

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) snapshot.LoadedScenes.Add(scene.name);
            }

            if (EventSystem.current != null)
            {
                var module = EventSystem.current.currentInputModule;
                snapshot.InputModule = module != null ? module.GetType().Name : "none active";
            }

            var seenLabels = new HashSet<string>();

            foreach (var go in UiProbe.AllGameObjects(includeHidden))
            {
                if (snapshot.Interactables.Count >= maxElements) break;
                if (go == null) continue;

                var kind = ClassifyInteractable(go);
                if (kind != null)
                {
                    var element = Describe(go, kind);
                    if (includeHidden || element.Visible) snapshot.Interactables.Add(element);
                    continue;
                }

                if (snapshot.Labels.Count >= maxElements) continue;
                var text = UiProbe.TextOf(go);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!UiProbe.IsVisible(go) && !includeHidden) continue;

                text = text.Trim();
                if (text.Length > 120) text = text.Substring(0, 117) + "…";
                if (!seenLabels.Add(text)) continue;

                snapshot.Labels.Add(Describe(go, "Text"));
            }

            return snapshot;
        }

        private static ElementInfo Describe(GameObject go, string kind)
        {
            var selectors = Locator.SuggestSelectorsFor(go);
            var testId = go.GetComponent<TestId>();
            var point = UiProbe.ScreenPointOf(go);
            UiProbe.IsHitTestable(go, point, out var blocker);

            var text = UiProbe.LabelOf(go);
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Trim();
                if (text.Length > 120) text = text.Substring(0, 117) + "…";
            }

            return new ElementInfo
            {
                Selector = selectors.Count > 0 ? selectors[0] : UiProbe.PathOf(go),
                AlternativeSelectors = selectors,
                Path = UiProbe.PathOf(go),
                Kind = kind,
                Text = text,
                Description = testId != null ? testId.Description : null,
                ScreenRect = UiProbe.ScreenRectOf(go),
                Visible = UiProbe.IsVisible(go),
                Interactable = UiProbe.IsInteractable(go),
                BlockedBy = blocker != null ? UiProbe.PathOf(blocker) : null,
            };
        }

        /// <summary>Names the interactive role of an object, or null when it isn't interactive.</summary>
        private static string ClassifyInteractable(GameObject go)
        {
            var selectable = go.GetComponent<Selectable>();
            if (selectable != null) return selectable.GetType().Name;

            if (go.GetComponent<IPointerClickHandler>() != null) return "Clickable";
            if (go.GetComponent<IDragHandler>() != null) return "Draggable";
            if (go.GetComponent<TestId>() != null) return "TestId";

            // World-space objects the player can hit with a pointer raycast.
            if (go.GetComponent<Collider>() != null) return "Collider";
            if (go.GetComponent<Collider2D>() != null) return "Collider2D";

            return null;
        }
    }
}
