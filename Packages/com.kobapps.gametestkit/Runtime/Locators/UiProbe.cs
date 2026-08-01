using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Answers the questions a test needs before it touches something: where is it on screen, can the
    /// player see it, can the player actually hit it, and what does it say.
    /// </summary>
    public static class UiProbe
    {
        private static readonly List<RaycastResult> RaycastBuffer = new List<RaycastResult>();

        // ---------------------------------------------------------------- geometry

        /// <summary>The point a test should aim at: the centre of a UI rect, a renderer or a collider.</summary>
        public static Vector2 ScreenPointOf(GameObject target)
        {
            if (target == null) return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            var rect = target.transform as RectTransform;
            if (rect != null)
                return RectTransformUtility.WorldToScreenPoint(CameraFor(rect), rect.TransformPoint(rect.rect.center));

            var camera = Camera.main;
            if (camera == null) return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            Vector3 world = target.transform.position;
            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null) world = renderer.bounds.center;
            else
            {
                var collider = target.GetComponentInChildren<Collider>();
                if (collider != null) world = collider.bounds.center;
                else
                {
                    var collider2d = target.GetComponentInChildren<Collider2D>();
                    if (collider2d != null) world = collider2d.bounds.center;
                }
            }

            var point = camera.WorldToScreenPoint(world);
            return new Vector2(point.x, point.y);
        }

        /// <summary>Screen-space bounds of a UI element (or a small box around a world object).</summary>
        public static Rect ScreenRectOf(GameObject target)
        {
            if (target == null) return new Rect();

            var rect = target.transform as RectTransform;
            if (rect != null)
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var camera = CameraFor(rect);
                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                for (int i = 0; i < 4; i++)
                {
                    var point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
                return new Rect(min, max - min);
            }

            var centre = ScreenPointOf(target);
            return new Rect(centre.x - 16f, centre.y - 16f, 32f, 32f);
        }

        /// <summary>The camera a canvas renders through — null for Screen Space Overlay, as uGUI expects.</summary>
        public static Camera CameraFor(RectTransform rect)
        {
            var canvas = rect != null ? rect.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return null;
            canvas = canvas.rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        public static bool IsOnScreen(Vector2 point) =>
            point.x >= 0f && point.y >= 0f && point.x <= Screen.width && point.y <= Screen.height;

        // ---------------------------------------------------------------- state

        /// <summary>Active in the hierarchy, not transparent, not clipped away, and on screen.</summary>
        public static bool IsVisible(GameObject target)
        {
            if (target == null || !target.activeInHierarchy) return false;

            var group = target.GetComponentInParent<CanvasGroup>();
            while (group != null)
            {
                if (group.alpha <= 0.01f) return false;
                var parent = group.transform.parent;
                group = parent != null ? parent.GetComponentInParent<CanvasGroup>() : null;
            }

            var graphic = target.GetComponent<Graphic>();
            if (graphic != null && (!graphic.enabled || graphic.color.a <= 0.01f)) return false;

            var renderer = target.GetComponent<Renderer>();
            if (renderer != null && !renderer.isVisible) return false;

            var rect = target.transform as RectTransform;
            if (rect != null)
            {
                var screenRect = ScreenRectOf(target);
                if (screenRect.width <= 0.5f || screenRect.height <= 0.5f) return false;
                var viewport = new Rect(0f, 0f, Screen.width, Screen.height);
                if (!viewport.Overlaps(screenRect)) return false;
            }

            return true;
        }

        /// <summary>Visible and accepting input: <c>Selectable.interactable</c>, canvas group, colliders.</summary>
        public static bool IsInteractable(GameObject target)
        {
            if (!IsVisible(target)) return false;

            var selectable = target.GetComponent<Selectable>();
            if (selectable != null && !selectable.IsInteractable()) return false;

            var group = target.GetComponentInParent<CanvasGroup>();
            if (group != null && !group.interactable && group.blocksRaycasts == false) return false;

            return true;
        }

        /// <summary>
        /// True when a click at <paramref name="point"/> would actually reach <paramref name="target"/> —
        /// i.e. nothing (a full-screen blocker, a modal, a popup) is on top of it.
        /// </summary>
        public static bool IsHitTestable(GameObject target, Vector2 point, out GameObject blocker)
        {
            blocker = null;
            if (target == null || EventSystem.current == null) return true; // nothing to check against

            var data = new PointerEventData(EventSystem.current) { position = point };
            RaycastBuffer.Clear();
            EventSystem.current.RaycastAll(data, RaycastBuffer);
            if (RaycastBuffer.Count == 0) return true;

            var top = RaycastBuffer[0].gameObject;
            if (top == null) return true;
            if (IsRelated(target, top)) return true;

            blocker = top;
            return false;
        }

        private static bool IsRelated(GameObject a, GameObject b)
        {
            var t = b.transform;
            while (t != null)
            {
                if (t.gameObject == a) return true;
                t = t.parent;
            }
            t = a.transform;
            while (t != null)
            {
                if (t.gameObject == b) return true;
                t = t.parent;
            }
            return false;
        }

        // ---------------------------------------------------------------- text

        /// <summary>Reads whatever text an object displays: uGUI Text, TextMeshPro, or an input field.</summary>
        public static string TextOf(GameObject target)
        {
            if (target == null) return null;

            var inputField = target.GetComponent<InputField>();
            if (inputField != null) return inputField.text;

            var text = target.GetComponent<Text>();
            if (text != null) return text.text;

            var tmpInput = target.GetComponent<TMPro.TMP_InputField>();
            if (tmpInput != null) return tmpInput.text;

            var tmp = target.GetComponent<TMPro.TMP_Text>();
            if (tmp != null) return tmp.text;

            return null;
        }

        /// <summary>Text of an object or any of its children — what a player would read on a button.</summary>
        public static string LabelOf(GameObject target)
        {
            if (target == null) return null;

            var own = TextOf(target);
            if (!string.IsNullOrEmpty(own)) return own;

            var text = target.GetComponentInChildren<Text>(true);
            if (text != null && !string.IsNullOrEmpty(text.text)) return text.text;

            var tmp = target.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;

            return null;
        }

        // ---------------------------------------------------------------- hierarchy

        /// <summary>Slash-separated path from the scene root, e.g. <c>Canvas/Menu/PlayButton</c>.</summary>
        public static string PathOf(GameObject target)
        {
            if (target == null) return null;
            var stack = new Stack<string>();
            var t = target.transform;
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack);
        }

        private static List<GameObject> _cache;
        private static int _cacheFrame = -1;
        private static bool _cacheIncludedInactive;

        /// <summary>
        /// Every GameObject in every loaded scene, including inactive ones and objects moved to
        /// DontDestroyOnLoad (which no scene enumeration reaches on its own).
        /// </summary>
        /// <remarks>
        /// The result is cached for the current frame. Waits poll their selector every frame, so
        /// without this a ten-second wait in a large scene would walk the whole hierarchy hundreds of
        /// times. The cache is rebuilt on the next frame, which is exactly the granularity a poll
        /// needs — nothing appears or disappears part-way through one frame's evaluation.
        /// </remarks>
        public static IEnumerable<GameObject> AllGameObjects(bool includeInactive = true)
        {
            // Only cache while playing. Outside play mode "the current frame" is not a meaningful unit
            // — editor tests and tooling create objects and query them immediately — so a cache there
            // would hand back a hierarchy that no longer exists.
            bool cacheable = Application.isPlaying;

            if (cacheable && _cache != null && _cacheFrame == Time.frameCount &&
                _cacheIncludedInactive == includeInactive)
                return _cache;

            var list = new List<GameObject>(256);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root, includeInactive, list);
            }

            var persistent = PersistentScene();
            if (persistent.IsValid())
                foreach (var root in persistent.GetRootGameObjects())
                    Collect(root, includeInactive, list);

            if (cacheable)
            {
                _cache = list;
                _cacheFrame = Time.frameCount;
                _cacheIncludedInactive = includeInactive;
            }

            return list;
        }

        /// <summary>Drops the frame cache. Only needed if you create objects and query them without a frame in between.</summary>
        public static void InvalidateCache() => _cacheFrame = -1;

        private static void Collect(GameObject root, bool includeInactive, List<GameObject> into)
        {
            if (root == null) return;
            if (!includeInactive && !root.activeInHierarchy) return;

            into.Add(root);
            var transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
                Collect(transform.GetChild(i).gameObject, includeInactive, into);
        }

        private static IEnumerable<GameObject> Walk(GameObject root, bool includeInactive)
        {
            if (root == null) yield break;
            if (!includeInactive && !root.activeInHierarchy) yield break;

            yield return root;
            var transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
                foreach (var go in Walk(transform.GetChild(i).gameObject, includeInactive))
                    yield return go;
        }

        private static GameObject _probe;

        private static Scene PersistentScene()
        {
            if (!Application.isPlaying) return default;
            if (_probe == null)
            {
                _probe = new GameObject("~GameTestKit.SceneProbe") { hideFlags = HideFlags.HideInHierarchy };
                Object.DontDestroyOnLoad(_probe);
            }
            return _probe.scene;
        }
    }
}
