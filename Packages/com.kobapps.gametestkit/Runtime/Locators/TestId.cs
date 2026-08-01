using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// A stable, human-chosen handle for a GameObject: <c>id:play_button</c> in a test script.
    /// </summary>
    /// <remarks>
    /// Names and hierarchy paths change every time a designer reorganises a prefab, and localised text
    /// changes with the language — both make tests brittle. A <see cref="TestId"/> is the one selector
    /// that survives refactors, so prefer it for anything a test touches often. Ids are registered on
    /// enable, so runtime-spawned objects are found without a scene scan.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("GameTestKit/Test Id")]
    public sealed class TestId : MonoBehaviour
    {
        [Tooltip("Unique id used by test scripts, e.g. play_button. Falls back to the GameObject name when empty.")]
        [SerializeField] private string _id;

        [Tooltip("Optional note shown in the AI scene dump — what this element does.")]
        [SerializeField] private string _description;

        private static readonly Dictionary<string, List<TestId>> Registry =
            new Dictionary<string, List<TestId>>(System.StringComparer.OrdinalIgnoreCase);

        public string Id => string.IsNullOrEmpty(_id) ? name : _id;

        public string Description => _description;

        /// <summary>
        /// Sets the id at runtime, re-registering under the new key.
        /// </summary>
        /// <remarks>
        /// For elements that only exist while the game is running and whose identity is positional — a
        /// board cell, an inventory row, a spawned enemy. Authoring an id in the prefab cannot express
        /// "cell 2,3", so a game hands the ids out as it builds the layout instead. Re-registers rather
        /// than just assigning, or the object would stay findable under its previous id.
        /// </remarks>
        public void AssignId(string id, string description = null)
        {
            if (_id == id && (description == null || _description == description)) return;

            bool registered = isActiveAndEnabled;
            if (registered) Deregister();

            _id = id;
            if (description != null) _description = description;

            if (registered) Register();
        }

        private void OnEnable() => Register();

        private void OnDisable() => Deregister();

        private void Register()
        {
            if (!Registry.TryGetValue(Id, out var list))
                Registry[Id] = list = new List<TestId>();
            if (!list.Contains(this)) list.Add(this);
        }

        private void Deregister()
        {
            if (Registry.TryGetValue(Id, out var list))
            {
                list.Remove(this);
                if (list.Count == 0) Registry.Remove(Id);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            // Survives play-mode exit when Enter Play Mode Options disables the domain reload, and the
            // stale entries would otherwise shadow the new session's objects under the same ids.
            Registry.Clear();
        }

        /// <summary>All currently enabled objects carrying <paramref name="id"/>, in registration order.</summary>
        public static IReadOnlyList<TestId> Find(string id)
        {
            if (!string.IsNullOrEmpty(id) && Registry.TryGetValue(id, out var list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] == null) list.RemoveAt(i);
                return list;
            }
            return System.Array.Empty<TestId>();
        }

        /// <summary>Every registered id — used by the AI scene dump and the recorder.</summary>
        public static IEnumerable<string> AllIds => Registry.Keys;

        /// <summary>
        /// Registers an id for an object that has no component, e.g. one owned by third-party code.
        /// The component is added at runtime and cleaned up with the object.
        /// </summary>
        public static TestId Assign(GameObject target, string id, string description = null)
        {
            if (target == null) return null;
            var component = target.GetComponent<TestId>();
            if (component == null) component = target.AddComponent<TestId>();
            component._id = id;
            if (description != null) component._description = description;
            component.OnEnable();
            return component;
        }
    }
}
