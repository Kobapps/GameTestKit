using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit.Samples
{
    /// <summary>
    /// Joins the demo's analytics hub to <see cref="TestEventLog"/> so tests can assert on what it
    /// sends. <b>This is the file to copy into a real project.</b>
    /// </summary>
    /// <remarks>
    /// It is deliberately the only file in the sample that knows about both sides. The game's
    /// analytics (<see cref="DemoAnalytics"/>) has no reference to GameTestKit, which is how it should
    /// stay: telemetry that is aware of the test framework has a way of growing test-only branches,
    /// and then you are no longer testing what ships.
    /// <para>
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> is the earliest hook Unity offers
    /// and the reason this works at all. Install, first-session and loading events fire before a test's
    /// first step can run; subscribing any later — <c>AfterSceneLoad</c>, a bootstrap
    /// <c>MonoBehaviour</c>, a test's own <c>setup</c> — misses them, and they have no second moment at
    /// which they can be observed.
    /// </para>
    /// <para>
    /// Cost in a shipped build: one delegate on an event that already exists.
    /// <see cref="TestEventLog"/> records nothing outside the Editor and development builds unless it
    /// is explicitly switched on.
    /// </para>
    /// </remarks>
    public static class DemoAnalyticsTestBridge
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Install()
        {
            DemoAnalytics.Sent += captured =>
            {
                var deliveries = new List<TestEventDelivery>(captured.Deliveries.Count);

                foreach (var delivery in captured.Deliveries)
                    deliveries.Add(new TestEventDelivery(delivery.Sink, delivery.Delivered, delivery.Reason));

                TestEventLog.Record(captured.Name, captured.Properties, deliveries);
            };

            // Only so the sample's consent case can drive the Remote provider from a script. A real
            // bridge usually needs nothing here — the game's existing bindings are enough.
            GameTestBindings.BindAction("demo.setConsent",
                args => DemoAnalytics.ConsentGiven = args.Length > 0 && System.Convert.ToBoolean(args[0]),
                "Grants or withholds analytics consent, which decides whether the Remote sink accepts events.");
        }
    }
}
