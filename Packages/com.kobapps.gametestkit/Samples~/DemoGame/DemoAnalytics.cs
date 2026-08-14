using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit.Samples
{
    /// <summary>
    /// The controlled vocabulary the demo's events use.
    /// </summary>
    /// <remarks>
    /// Declared as constants rather than written inline at each call site, so a test can assert with
    /// <c>"@const:…RESULT_*"</c> instead of holding its own copy of the string. A test that hard-codes
    /// <c>"Bought"</c> keeps passing after someone renames the constant — it is asserting against a
    /// literal that no longer means anything to the game.
    /// </remarks>
    public static class DemoTelemetry
    {
        public const string RESULT_BOUGHT = "Bought";
        public const string RESULT_DECLINED = "Declined";

        public const string REASON_NONE = "None";
        public const string REASON_NOT_ENOUGH_GOLD = "NotEnoughGold";

        public const string SINK_CONSOLE = "Console";
        public const string SINK_REMOTE = "Remote";
    }

    /// <summary>What one provider did with an event.</summary>
    public readonly struct DemoDelivery
    {
        public readonly string Sink;
        public readonly bool Delivered;
        public readonly string Reason;

        public DemoDelivery(string sink, bool delivered, string reason = null)
        {
            Sink = sink;
            Delivered = delivered;
            Reason = reason;
        }
    }

    /// <summary>One event the demo sent, with its payload and what each provider did with it.</summary>
    public sealed class DemoAnalyticsEvent
    {
        public readonly string Name;
        public readonly Dictionary<string, object> Properties;
        public readonly List<DemoDelivery> Deliveries = new List<DemoDelivery>();

        public DemoAnalyticsEvent(string name, Dictionary<string, object> properties)
        {
            Name = name;
            Properties = properties ?? new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// A miniature analytics hub — two providers, a consent switch, and per-event super properties.
    /// </summary>
    /// <remarks>
    /// <b>It does not reference GameTestKit, and that is the point.</b> A game's analytics has no
    /// business knowing that tests exist; the one file that joins them is
    /// <see cref="DemoAnalyticsTestBridge"/>, which is the file worth copying into a real project.
    /// <para>
    /// The <c>Remote</c> provider refuses everything while consent is withheld. That is what makes the
    /// sample's <c>delivered</c> / <c>notDelivered</c> assertions worth writing: an event can be
    /// perfectly formed and still never arrive, and a dashboard row that is simply missing has no
    /// other explanation in the log.
    /// </para>
    /// </remarks>
    public static class DemoAnalytics
    {
        /// <summary>Raised for every event, after the providers have had it.</summary>
        public static event Action<DemoAnalyticsEvent> Sent;

        /// <summary>While false, the Remote provider refuses everything.</summary>
        public static bool ConsentGiven = true;

        /// <summary>Bumped per event, so a test can prove nothing was dropped between two of them.</summary>
        private static int _counter;

        /// <summary>Stable for the session, and stamped on every event — a "super property".</summary>
        private static string _sessionId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            _counter = 0;
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            ConsentGiven = true;
        }

        public static void Send(string name, Dictionary<string, object> properties = null)
        {
            var captured = new DemoAnalyticsEvent(name, properties);

            // Super properties: on every event, which is what makes them worth a session-wide
            // invariant check rather than a per-case assertion.
            captured.Properties["Session_Id"] = _sessionId ?? "unset";
            captured.Properties["Event_Number"] = ++_counter;

            captured.Deliveries.Add(new DemoDelivery(DemoTelemetry.SINK_CONSOLE, true));
            captured.Deliveries.Add(ConsentGiven
                ? new DemoDelivery(DemoTelemetry.SINK_REMOTE, true)
                : new DemoDelivery(DemoTelemetry.SINK_REMOTE, false, "no consent"));

            Sent?.Invoke(captured);
        }
    }
}
