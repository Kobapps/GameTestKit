using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Kobapps.GameTestKit
{
    /// <summary>What a selector resolved to: an object, a screen point, or both.</summary>
    public sealed class ResolvedTarget
    {
        public string Selector;
        public GameObject GameObject;
        public Vector2 ScreenPoint;

        public bool HasGameObject => GameObject != null;

        public override string ToString() =>
            HasGameObject ? $"{Selector} → {UiProbe.PathOf(GameObject)}" : $"{Selector} → {ScreenPoint}";
    }

    /// <summary>
    /// The selector language tests use to address things on screen.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <item><term><c>id:play_button</c></term><description>a <see cref="TestId"/> — the stable, refactor-proof choice</description></item>
    /// <item><term><c>#PlayButton</c></term><description>GameObject by exact name</description></item>
    /// <item><term><c>Canvas/Menu/Play</c></term><description>hierarchy path (a suffix is enough)</description></item>
    /// <item><term><c>text:Play</c></term><description>visible label contains "Play" (case-insensitive); <c>text:"Play"</c> for an exact match</description></item>
    /// <item><term><c>tag:Enemy</c></term><description>Unity tag</description></item>
    /// <item><term><c>type:Button</c></term><description>has a component of that type (short or full name)</description></item>
    /// <item><term><c>pos:0.5,0.5</c></term><description>normalised screen point — no object involved</description></item>
    /// <item><term><c>screen:640,360</c></term><description>absolute screen pixels</description></item>
    /// <item><term><c>world:1,0,3</c></term><description>world position projected through the main camera</description></item>
    /// </list>
    /// Add <c>[n]</c> to pick the n-th match (<c>type:Button[2]</c>), and chain with <c>&gt;&gt;</c> to
    /// scope a search to descendants: <c>id:shop_panel &gt;&gt; text:Buy</c>.
    /// </remarks>
    public static class Locator
    {
        public const string ScopeSeparator = ">>";

        /// <summary>True when the selector names a bare screen point rather than an object.</summary>
        public static bool IsPointSelector(string selector)
        {
            selector = (selector ?? "").Trim();
            return selector.StartsWith("pos:", StringComparison.OrdinalIgnoreCase)
                || selector.StartsWith("screen:", StringComparison.OrdinalIgnoreCase)
                || selector.StartsWith("world:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolves a point selector. Returns false for object selectors.</summary>
        public static bool TryResolvePoint(string selector, out Vector2 point)
        {
            point = default;
            selector = (selector ?? "").Trim();
            int colon = selector.IndexOf(':');
            if (colon < 0) return false;

            var prefix = selector.Substring(0, colon).ToLowerInvariant();
            var value = selector.Substring(colon + 1);
            var parts = value.Split(',');

            switch (prefix)
            {
                case "pos":
                    if (parts.Length < 2) return false;
                    point = new Vector2(ParseFloat(parts[0]) * Screen.width, ParseFloat(parts[1]) * Screen.height);
                    return true;
                case "screen":
                    if (parts.Length < 2) return false;
                    point = new Vector2(ParseFloat(parts[0]), ParseFloat(parts[1]));
                    return true;
                case "world":
                    if (parts.Length < 3) return false;
                    var camera = Camera.main;
                    if (camera == null)
                        throw new TestFailureException($"'{selector}' needs a camera tagged MainCamera.");
                    var projected = camera.WorldToScreenPoint(
                        new Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2])));
                    point = new Vector2(projected.x, projected.y);
                    return true;
                default:
                    return false;
            }
        }

        private static float ParseFloat(string value) =>
            float.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new TestFailureException($"'{value}' is not a number.");

        /// <summary>First match, or null. Does not wait — see <see cref="LocatorEngine.Resolve"/>.</summary>
        public static GameObject Find(string selector)
        {
            var all = FindAll(selector);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>All matches for a selector, in a deterministic order (scene order).</summary>
        public static List<GameObject> FindAll(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                throw new TestFailureException("Empty selector.");

            var segments = selector.Split(new[] { ScopeSeparator }, StringSplitOptions.RemoveEmptyEntries);
            List<GameObject> scope = null;

            for (int i = 0; i < segments.Length; i++)
            {
                var candidates = MatchSegment(segments[i].Trim(), scope);
                scope = candidates;
                if (scope.Count == 0) break;
            }

            return scope ?? new List<GameObject>();
        }

        private static List<GameObject> MatchSegment(string segment, List<GameObject> scope)
        {
            int index = -1;
            segment = StripIndex(segment, ref index);

            var results = new List<GameObject>();
            var pool = scope == null ? UiProbe.AllGameObjects() : Descendants(scope);

            string prefix = null, value = segment;
            int colon = segment.IndexOf(':');
            if (colon > 0)
            {
                var candidate = segment.Substring(0, colon).ToLowerInvariant();
                if (IsKnownPrefix(candidate))
                {
                    prefix = candidate;
                    value = segment.Substring(colon + 1).Trim();
                }
            }

            if (prefix == null)
            {
                if (segment.StartsWith("#", StringComparison.Ordinal)) { prefix = "name"; value = segment.Substring(1); }
                else if (segment.Contains("/")) { prefix = "path"; value = segment; }
                else
                {
                    // A typo like `txt:Play` must not silently degrade into a name lookup that matches
                    // nothing — that turns a one-line fix into a debugging session.
                    RejectUnknownPrefix(segment);
                    prefix = "name";
                    value = segment;
                }
            }

            switch (prefix)
            {
                case "id":
                    // The registry is the fast path but only knows about enabled components, so fall
                    // back to a scan — otherwise an element that starts inactive reports "matched
                    // nothing" when the truth is "found, but not visible yet".
                    if (scope == null)
                    {
                        foreach (var testId in TestId.Find(value))
                            if (testId != null) results.Add(testId.gameObject);
                    }

                    if (results.Count == 0)
                    {
                        foreach (var go in pool)
                        {
                            var testId = go.GetComponent<TestId>();
                            if (testId != null && string.Equals(testId.Id, value, StringComparison.OrdinalIgnoreCase))
                                results.Add(go);
                        }
                    }
                    break;

                case "name":
                    foreach (var go in pool)
                        if (string.Equals(go.name, value, StringComparison.Ordinal))
                            results.Add(go);
                    if (results.Count == 0)
                        foreach (var go in pool)
                            if (string.Equals(go.name, value, StringComparison.OrdinalIgnoreCase))
                                results.Add(go);
                    break;

                case "path":
                    var normalized = value.Trim('/');
                    foreach (var go in pool)
                    {
                        var path = UiProbe.PathOf(go);
                        if (path != null &&
                            (path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)))
                            results.Add(go);
                    }
                    break;

                case "text":
                    bool exact = value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"';
                    var needle = exact ? value.Substring(1, value.Length - 2) : value;
                    foreach (var go in pool)
                    {
                        var text = UiProbe.TextOf(go);
                        if (string.IsNullOrEmpty(text)) continue;
                        bool hit = exact
                            ? string.Equals(text.Trim(), needle, StringComparison.OrdinalIgnoreCase)
                            : text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (hit) results.Add(ClickableOwnerOf(go));
                    }
                    break;

                case "tag":
                    foreach (var go in pool)
                    {
                        try { if (go.CompareTag(value)) results.Add(go); }
                        catch (UnityException)
                        {
                            throw new TestFailureException($"Tag '{value}' is not defined in this project.");
                        }
                    }
                    break;

                case "type":
                    foreach (var go in pool)
                        foreach (var component in go.GetComponents<Component>())
                        {
                            if (component == null) continue;
                            var type = component.GetType();
                            if (string.Equals(type.Name, value, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(type.FullName, value, StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add(go);
                                break;
                            }
                        }
                    break;

                default:
                    throw new TestFailureException($"Unknown selector prefix '{prefix}:' in '{segment}'.");
            }

            Dedupe(results);

            if (index >= 0)
            {
                if (index >= results.Count) return new List<GameObject>();
                return new List<GameObject> { results[index] };
            }

            return results;
        }

        /// <summary>
        /// Throws when a segment looks like <c>something:value</c> but <c>something</c> is not a
        /// selector prefix, so a mistyped selector fails loudly instead of matching nothing.
        /// </summary>
        private static void RejectUnknownPrefix(string segment)
        {
            int colon = segment.IndexOf(':');
            if (colon <= 0) return;

            var prefix = segment.Substring(0, colon);
            for (int i = 0; i < prefix.Length; i++)
            {
                char c = prefix[i];
                bool valid = i == 0 ? char.IsLetter(c) : char.IsLetterOrDigit(c) || c == '_';
                if (!valid) return;   // not prefix-shaped — probably a genuine name containing ':'
            }

            var lowered = prefix.ToLowerInvariant();
            if (lowered == "pos" || lowered == "screen" || lowered == "world")
                throw new TestFailureException(
                    $"'{segment}' names a screen point, not an object. Point selectors work with steps that " +
                    "take a target (click, drag, swipe), but not with lookups such as exists() or assertVisible.");

            throw new TestFailureException(
                $"Unknown selector prefix '{prefix}:' in '{segment}'. Use id:, name:, path:, text:, tag:, type:, " +
                "pos:, screen: or world: — or drop the colon if the object really is called that.");
        }

        private static bool IsKnownPrefix(string prefix)
        {
            switch (prefix)
            {
                case "id":
                case "name":
                case "path":
                case "text":
                case "tag":
                case "type":
                    return true;
                default:
                    return false;
            }
        }

        private static string StripIndex(string segment, ref int index)
        {
            if (!segment.EndsWith("]", StringComparison.Ordinal)) return segment;
            int open = segment.LastIndexOf('[');
            if (open < 0) return segment;

            var inner = segment.Substring(open + 1, segment.Length - open - 2);
            if (!int.TryParse(inner, out index)) { index = -1; return segment; }
            return segment.Substring(0, open).Trim();
        }

        private static IEnumerable<GameObject> Descendants(List<GameObject> roots)
        {
            var seen = new HashSet<int>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var child in root.GetComponentsInChildren<Transform>(true))
                {
                    if (child == null) continue;
                    if (seen.Add(child.gameObject.GetInstanceID()))
                        yield return child.gameObject;
                }
            }
        }

        /// <summary>Removes destroyed and repeated entries while preserving scene order.</summary>
        private static void Dedupe(List<GameObject> list)
        {
            var seen = new HashSet<int>();
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var go = list[read];
                if (go == null || !seen.Add(go.GetInstanceID())) continue;
                list[write++] = go;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        /// <summary>
        /// For a text match, the thing a player would actually click: the nearest Selectable or click
        /// handler above the label, or the label itself when it stands alone.
        /// </summary>
        public static GameObject ClickableOwnerOf(GameObject target)
        {
            if (target == null) return null;

            var transform = target.transform;
            for (int depth = 0; depth < 4 && transform != null; depth++)
            {
                var go = transform.gameObject;
                if (go.GetComponent<Selectable>() != null || go.GetComponent<IPointerClickHandler>() != null)
                    return go;
                transform = transform.parent;
            }
            return target;
        }

        /// <summary>
        /// Best-effort selectors that would find <paramref name="target"/> again, most stable first.
        /// Used by the recorder and by the AI scene dump.
        /// </summary>
        public static List<string> SuggestSelectorsFor(GameObject target)
        {
            var suggestions = new List<string>();
            if (target == null) return suggestions;

            var testId = target.GetComponentInParent<TestId>();
            if (testId != null) suggestions.Add("id:" + testId.Id);

            var label = UiProbe.LabelOf(target);
            if (!string.IsNullOrWhiteSpace(label) && label.Length <= 40)
                suggestions.Add($"text:\"{label.Trim()}\"");

            if (FindAll("#" + target.name).Count == 1)
                suggestions.Add("#" + target.name);

            var path = UiProbe.PathOf(target);
            if (!string.IsNullOrEmpty(path)) suggestions.Add(path);

            return suggestions;
        }

        /// <summary>Human-readable "did you mean…" text for a selector that matched nothing.</summary>
        public static string DescribeMiss(string selector)
        {
            var hints = new List<string>();
            var ids = new List<string>();
            foreach (var id in TestId.AllIds)
            {
                ids.Add(id);
                if (ids.Count >= 12) break;
            }
            if (ids.Count > 0) hints.Add("known ids: " + string.Join(", ", ids));

            var labels = new List<string>();
            foreach (var go in UiProbe.AllGameObjects(false))
            {
                var text = UiProbe.TextOf(go);
                if (string.IsNullOrWhiteSpace(text) || text.Length > 32) continue;
                if (!labels.Contains(text)) labels.Add(text.Trim());
                if (labels.Count >= 12) break;
            }
            if (labels.Count > 0) hints.Add("visible text: " + string.Join(" | ", labels));

            return hints.Count == 0
                ? $"'{selector}' matched nothing and the scene has no test ids or visible text."
                : $"'{selector}' matched nothing. In the current scene — {string.Join("; ", hints)}.";
        }
    }

    /// <summary>
    /// Resolves selectors <em>with waiting</em>. Every lookup retries until the object exists and is in
    /// the requested state or the timeout expires, which is what keeps tests from flaking on animated
    /// UI, async scene loads, and objects that spawn a frame late.
    /// </summary>
    public sealed class LocatorEngine
    {
        private readonly RunOptions _options;

        public LocatorEngine(RunOptions options) { _options = options ?? new RunOptions(); }

        /// <summary>Resolution requirements — what "ready to interact with" means for this lookup.</summary>
        [Flags]
        public enum Requirement
        {
            Exists = 0,
            Visible = 1 << 0,
            Interactable = 1 << 1,
            HitTestable = 1 << 2,

            /// <summary>Everything a click needs: on screen, enabled, and nothing covering it.</summary>
            Clickable = Visible | Interactable | HitTestable,
        }

        /// <summary>
        /// Waits for <paramref name="selector"/> to satisfy <paramref name="requirement"/> and fills
        /// <paramref name="result"/>. Throws <see cref="TestFailureException"/> with a diagnostic
        /// message — including what blocked the click, or what the scene does contain — on timeout.
        /// </summary>
        public IEnumerator Resolve(string selector, ResolvedTarget result,
            Requirement requirement = Requirement.Exists, float? timeout = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            result.Selector = selector;

            if (Locator.TryResolvePoint(selector, out var point))
            {
                result.GameObject = null;
                result.ScreenPoint = point;
                yield break;
            }

            float budget = timeout ?? _options.LocatorTimeout;
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, budget);
            string lastProblem = null;

            while (true)
            {
                var matches = Locator.FindAll(selector);

                if (matches.Count == 0)
                {
                    lastProblem = null;
                }
                else
                {
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var candidate = matches[i];
                        if (!Accepts(candidate, requirement, out var problem))
                        {
                            lastProblem = problem;
                            continue;
                        }

                        result.GameObject = candidate;
                        result.ScreenPoint = UiProbe.ScreenPointOf(candidate);
                        yield break;
                    }
                }

                if (Time.realtimeSinceStartup >= deadline)
                {
                    if (matches.Count == 0)
                        throw new TestFailureException(
                            $"Could not find {Locator.DescribeMiss(selector)} (waited {budget:0.#}s).");

                    throw new TestFailureException(
                        $"Found {matches.Count} match(es) for '{selector}' but none was usable after {budget:0.#}s: {lastProblem}");
                }

                yield return null;
            }
        }

        /// <summary>Resolve to a screen point, whether the selector names an object or a raw position.</summary>
        public IEnumerator ResolvePoint(string selector, ResolvedTarget result,
            Requirement requirement = Requirement.Exists, float? timeout = null)
        {
            yield return Resolve(selector, result, requirement, timeout);
            if (result.HasGameObject)
                result.ScreenPoint = UiProbe.ScreenPointOf(result.GameObject);
        }

        private static bool Accepts(GameObject candidate, Requirement requirement, out string problem)
        {
            problem = null;
            if (candidate == null) { problem = "the object was destroyed"; return false; }

            if ((requirement & Requirement.Visible) != 0 && !UiProbe.IsVisible(candidate))
            {
                problem = $"'{UiProbe.PathOf(candidate)}' is not visible (inactive, transparent, or off screen)";
                return false;
            }

            if ((requirement & Requirement.Interactable) != 0 && !UiProbe.IsInteractable(candidate))
            {
                problem = $"'{UiProbe.PathOf(candidate)}' is not interactable (disabled Selectable or CanvasGroup)";
                return false;
            }

            if ((requirement & Requirement.HitTestable) != 0)
            {
                var point = UiProbe.ScreenPointOf(candidate);
                if (!UiProbe.IsOnScreen(point))
                {
                    problem = $"'{UiProbe.PathOf(candidate)}' centre {point} is off screen";
                    return false;
                }
                if (!UiProbe.IsHitTestable(candidate, point, out var blocker))
                {
                    problem = $"'{UiProbe.PathOf(candidate)}' is covered by '{UiProbe.PathOf(blocker)}'";
                    return false;
                }
            }

            return true;
        }
    }
}
