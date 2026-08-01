using System;
using System.Collections;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Yield helpers for steps. Everything returns a plain <see cref="IEnumerator"/> so the runner can
    /// drive it directly (and therefore time it out and attribute exceptions to the right step).
    /// </summary>
    public static class Wait
    {
        /// <summary>Wait a single frame.</summary>
        public static IEnumerator Frame()
        {
            yield return null;
        }

        /// <summary>Wait <paramref name="count"/> frames.</summary>
        public static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++)
                yield return null;
        }

        /// <summary>Wait scaled game seconds — respects <c>Time.timeScale</c>, so it matches game logic.</summary>
        public static IEnumerator Seconds(float seconds)
        {
            if (seconds <= 0f) { yield return null; yield break; }
            float end = Time.time + seconds;
            while (Time.time < end)
                yield return null;
        }

        /// <summary>Wait wall-clock seconds — unaffected by <c>Time.timeScale</c> (use for input pacing).</summary>
        public static IEnumerator RealSeconds(float seconds)
        {
            if (seconds <= 0f) { yield return null; yield break; }
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        /// <summary>
        /// Poll <paramref name="predicate"/> every frame until it is true. Throws
        /// <see cref="TestFailureException"/> if <paramref name="timeout"/> wall-clock seconds elapse first.
        /// </summary>
        public static IEnumerator Until(Func<bool> predicate, float timeout, string what)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            float end = Time.realtimeSinceStartup + Mathf.Max(0f, timeout);
            while (true)
            {
                Exception thrown = null;
                bool done = false;
                try { done = predicate(); }
                catch (Exception e) { thrown = e; }

                if (thrown != null)
                    throw new TestFailureException($"Condition '{what}' threw: {thrown.Message}", thrown);
                if (done)
                    yield break;
                if (Time.realtimeSinceStartup >= end)
                    throw new TestFailureException($"Timed out after {timeout:0.##}s waiting for {what}.");
                yield return null;
            }
        }

        /// <summary>Poll until <paramref name="predicate"/> is false.</summary>
        public static IEnumerator While(Func<bool> predicate, float timeout, string what)
        {
            return Until(() => !predicate(), timeout, "not " + what);
        }

        /// <summary>Wait until the given <see cref="AsyncOperation"/> reports done.</summary>
        public static IEnumerator Operation(AsyncOperation op, float timeout, string what)
        {
            if (op == null) yield break;
            float end = Time.realtimeSinceStartup + Mathf.Max(0f, timeout);
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup >= end)
                    throw new TestFailureException($"Timed out after {timeout:0.##}s waiting for {what} (progress {op.progress:0.00}).");
                yield return null;
            }
        }
    }
}
