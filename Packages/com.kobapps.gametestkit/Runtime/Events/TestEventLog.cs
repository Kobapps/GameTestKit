using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// The window of events a test asserts against: everything the game emitted this session, in order,
    /// with its payload and what each sink did with it.
    /// </summary>
    /// <remarks>
    /// The game feeds it with one line wherever its own event bus already reports:
    /// <example>
    /// <code>
    /// // Once, at startup — RuntimeInitializeOnLoadMethod(SubsystemRegistration) if boot events matter.
    /// AnalyticsHub.Recorded += e =&gt; TestEventLog.Record(e.Name, e.Properties);
    /// </code>
    /// </example>
    /// <para>
    /// <b>Record early.</b> The events that are hardest to test — install, first session, the loading
    /// chain — all fire before a test's first step can run, and there is no later moment at which they
    /// can be observed. Subscribing at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> is
    /// what makes them assertable after the fact rather than unreachable.
    /// </para>
    /// <para>
    /// Marks are sequence numbers, not timestamps, so "since this case opened" stays exact when two
    /// events share a millisecond.
    /// </para>
    /// <para>
    /// Thread-safe: ad and attribution SDKs deliver on their own threads, and an event that arrives off
    /// the main thread is exactly the one worth recording.
    /// </para>
    /// </remarks>
    public static class TestEventLog
    {
        private static readonly object Gate = new object();
        private static readonly List<TestEvent> Window = new List<TestEvent>(256);
        private static readonly Dictionary<string, long> Marks = new Dictionary<string, long>(StringComparer.Ordinal);

        private static long _sequence;
        private static bool? _enabled;
        private static int _mainThreadId;

        /// <summary>
        /// Whether events are recorded at all. Defaults to on in the Editor and in development builds,
        /// off in a release build — a shipped game must not accumulate its own telemetry forever.
        /// </summary>
        /// <remarks>
        /// Set it explicitly before the first event if you need a release build to record, which is the
        /// case when a QA build on a device is what you are testing.
        /// </remarks>
        public static bool Enabled
        {
            get => _enabled ?? (_enabled = DefaultEnabled()).Value;
            set => _enabled = value;
        }

        /// <summary>
        /// On in the Editor and in development builds. Reading that needs Unity, which is not always
        /// there — a plain test host running the kit's own logic has no engine to ask — so an
        /// unanswerable question means "record", the answer that cannot silently lose data.
        /// </summary>
        private static bool DefaultEnabled()
        {
            try { return AskUnityWhetherThisIsADevelopmentBuild(); }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// The Unity call, alone in its own frame.
        /// </summary>
        /// <remarks>
        /// Outside the engine, touching a Unity API throws while the runtime is <em>preparing</em> the
        /// method — before any <c>try</c> inside that same method takes effect. Putting the call one
        /// frame down is what makes it catchable, and <see cref="MethodImplOptions.NoInlining"/> is what
        /// stops the JIT from folding the frame back in and reintroducing the problem.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AskUnityWhetherThisIsADevelopmentBuild() =>
            Application.isEditor || Debug.isDebugBuild;

        /// <summary>
        /// How many events to keep, oldest dropped first. 0 keeps everything.
        /// </summary>
        /// <remarks>
        /// Unbounded by default and deliberately so: a run that plays several levels would evict its own
        /// boot events long before the step that asserts them, and a ring buffer sized for "the last
        /// level attempt" is the reason those events look untestable in the first place. Cap it only for
        /// a long soak.
        /// </remarks>
        public static int Capacity { get; set; }

        /// <summary>Raised as each event is recorded, for tooling that wants to stream rather than poll.</summary>
        public static event Action<TestEvent> Recorded;

        /// <summary>Every event captured this session, oldest first. A snapshot — safe to enumerate.</summary>
        public static IReadOnlyList<TestEvent> Entries
        {
            get { lock (Gate) return new List<TestEvent>(Window); }
        }

        public static int Count
        {
            get { lock (Gate) return Window.Count; }
        }

        /// <summary>The sequence number of the newest event, or 0 when nothing has been recorded.</summary>
        public static long LastSequence
        {
            get { lock (Gate) return Window.Count == 0 ? 0 : Window[Window.Count - 1].Sequence; }
        }

        // ---------------------------------------------------------------- recording

        /// <summary>Records an event. Cheap and safe to call from any thread; a no-op when disabled.</summary>
        public static TestEvent Record(string name, IReadOnlyDictionary<string, object> properties = null,
            IReadOnlyList<TestEventDelivery> deliveries = null, bool suppressed = false)
        {
            if (!Enabled || string.IsNullOrEmpty(name)) return null;

            // Copied here rather than trusted: a caller that reuses one dictionary per event — which is
            // the usual way an event bus is written — would otherwise rewrite every event it ever sent.
            IReadOnlyDictionary<string, object> copy = null;
            if (properties != null && properties.Count > 0)
            {
                var map = new Dictionary<string, object>(properties.Count, StringComparer.Ordinal);
                foreach (var pair in properties) map[pair.Key] = pair.Value;
                copy = map;
            }

            return Record(new TestEvent(name, copy, deliveries, suppressed, Realtime()));
        }

        /// <summary>
        /// Seconds since the game started, or 0 when the caller is not on Unity's main thread.
        /// </summary>
        /// <remarks>
        /// <c>Time.realtimeSinceStartup</c> throws off the main thread, and an event delivered on a
        /// mediation or attribution thread is precisely the one that must still be recorded. The UTC
        /// timestamp on the event is always there; this is the convenience that sometimes is not.
        /// </remarks>
        private static float Realtime()
        {
            if (_mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId) return 0f;

            // One frame down, for the reason given on AskUnityWhetherThisIsADevelopmentBuild.
            try { return AskUnityForRealtime(); }
            catch (Exception) { return 0f; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float AskUnityForRealtime() => Time.realtimeSinceStartup;

        /// <summary>Records an event that is already built. Its <see cref="TestEvent.Sequence"/> is assigned here.</summary>
        public static TestEvent Record(TestEvent captured)
        {
            if (!Enabled || captured == null) return null;

            lock (Gate)
            {
                captured.Sequence = ++_sequence;
                Window.Add(captured);

                if (Capacity > 0 && Window.Count > Capacity)
                    Window.RemoveRange(0, Window.Count - Capacity);
            }

            try { Recorded?.Invoke(captured); }
            catch (Exception e) { Debug.LogWarning($"[GameTestKit] A TestEventLog subscriber threw: {e.Message}"); }

            return captured;
        }

        // ---------------------------------------------------------------- marks

        /// <summary>
        /// Names the current end of the window, so a later assertion can say "since here" without
        /// counting. Re-marking a name moves it.
        /// </summary>
        /// <returns>The sequence the mark was set at.</returns>
        public static long Mark(string name = null)
        {
            lock (Gate)
            {
                var mark = Window.Count == 0 ? 0L : Window[Window.Count - 1].Sequence;
                if (!string.IsNullOrEmpty(name)) Marks[name] = mark;
                return mark;
            }
        }

        /// <summary>Where a named mark was set, or -1 when there is no such mark.</summary>
        public static long MarkOf(string name)
        {
            lock (Gate)
                return !string.IsNullOrEmpty(name) && Marks.TryGetValue(name, out var mark) ? mark : -1;
        }

        // ---------------------------------------------------------------- reading

        /// <summary>Every event recorded after <paramref name="afterSequence"/>, oldest first.</summary>
        public static IReadOnlyList<TestEvent> Since(long afterSequence)
        {
            var found = new List<TestEvent>();

            lock (Gate)
                foreach (var captured in Window)
                    if (captured.Sequence > afterSequence) found.Add(captured);

            return found;
        }

        /// <summary>Every event of one name recorded after <paramref name="afterSequence"/>, oldest first.</summary>
        public static IReadOnlyList<TestEvent> EntriesOf(string name, long afterSequence = 0)
        {
            var found = new List<TestEvent>();

            lock (Gate)
                foreach (var captured in Window)
                    if (captured.Sequence > afterSequence &&
                        string.Equals(captured.Name, name, StringComparison.OrdinalIgnoreCase))
                        found.Add(captured);

            return found;
        }

        public static int CountOf(string name, long afterSequence = 0) => EntriesOf(name, afterSequence).Count;

        /// <summary>
        /// What actually happened in a window, as one readable line. This is the difference between
        /// "the event did not fire" and a failure a person can act on.
        /// </summary>
        public static string Describe(long afterSequence = 0, int max = 30)
        {
            var since = Since(afterSequence);
            if (since.Count == 0) return "(no events at all)";

            var text = new StringBuilder();
            int shown = Math.Min(max, since.Count);

            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(since[i].Name);
            }

            if (since.Count > shown) text.Append($", …(+{since.Count - shown} more)");
            return text.ToString();
        }

        /// <summary>Empties the window and every mark. Sequence numbering continues, so marks stay comparable.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Window.Clear();
                Marks.Clear();
            }
        }

        /// <summary>
        /// Clears the log at the start of every play session.
        /// </summary>
        /// <remarks>
        /// Static state survives entering play mode when <i>Reload Domain</i> is off, and a window
        /// carrying the previous session's boot events would make "exactly one App_Install" fail on the
        /// second run and every run after it.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            lock (Gate)
            {
                Window.Clear();
                Marks.Clear();
                _sequence = 0;
            }
        }
    }
}
