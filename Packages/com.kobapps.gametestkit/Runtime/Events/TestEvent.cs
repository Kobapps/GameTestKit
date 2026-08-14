using System;
using System.Collections.Generic;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// What one sink did with an event — an analytics provider, a server endpoint, a log file.
    /// </summary>
    /// <remarks>
    /// Recorded separately from the payload because "the event fired" and "the event arrived" are
    /// different claims, and a dashboard row that never appears is usually the second one failing. A
    /// provider that dropped, batched or rejected the event is the answer to a question the payload
    /// cannot answer.
    /// </remarks>
    public sealed class TestEventDelivery
    {
        /// <summary>The sink's name, e.g. <c>Mixpanel</c>. Matched case-insensitively.</summary>
        public readonly string Sink;

        /// <summary>False when the sink refused, dropped or errored on the event.</summary>
        public readonly bool Delivered;

        /// <summary>Why it was not delivered, when the sink says.</summary>
        public readonly string Reason;

        public TestEventDelivery(string sink, bool delivered = true, string reason = null)
        {
            Sink = sink ?? "";
            Delivered = delivered;
            Reason = reason;
        }

        public override string ToString() =>
            Delivered ? Sink : $"{Sink} (not delivered{(string.IsNullOrEmpty(Reason) ? "" : $": {Reason}")})";
    }

    /// <summary>
    /// One event the game emitted, as observed by <see cref="TestEventLog"/>.
    /// </summary>
    /// <remarks>
    /// The kit deliberately does not know what the event <em>means</em>. An analytics event, an IAP
    /// receipt, a server call, an ad callback and a save write are the same shape — a name, a payload,
    /// and what each sink did with it — so one set of assertions covers all of them.
    /// <para>
    /// <see cref="Sequence"/> is a per-session counter rather than a timestamp: two events can share a
    /// millisecond, and "since this case opened" has to be exact even when they do. A gap in the
    /// sequence is also the only in-band evidence that something was dropped before it was recorded.
    /// </para>
    /// </remarks>
    public sealed class TestEvent
    {
        private static readonly Dictionary<string, object> NoProperties = new Dictionary<string, object>(0);
        private static readonly TestEventDelivery[] NoDeliveries = Array.Empty<TestEventDelivery>();

        /// <summary>Position in the session, starting at 1. Assigned by the log, not by the caller.</summary>
        public long Sequence { get; internal set; }

        public readonly string Name;

        /// <summary>When it was recorded. Realtime seconds since the game started, unaffected by timeScale.</summary>
        public readonly float AtRealtime;

        public readonly DateTime TimestampUtc;

        /// <summary>The payload. Copied at record time, so a caller reusing its dictionary cannot rewrite history.</summary>
        public readonly IReadOnlyDictionary<string, object> Properties;

        /// <summary>What each sink did with it. Empty when the game does not report delivery.</summary>
        public readonly IReadOnlyList<TestEventDelivery> Deliveries;

        /// <summary>True when the event was recorded but deliberately not sent — a mute switch, a sampling rule.</summary>
        public readonly bool Suppressed;

        public TestEvent(
            string name,
            IReadOnlyDictionary<string, object> properties = null,
            IReadOnlyList<TestEventDelivery> deliveries = null,
            bool suppressed = false,
            float atRealtime = 0f,
            DateTime timestampUtc = default)
        {
            Name = name ?? "";
            Properties = properties ?? NoProperties;
            Deliveries = deliveries ?? NoDeliveries;
            Suppressed = suppressed;
            AtRealtime = atRealtime;
            TimestampUtc = timestampUtc == default ? DateTime.UtcNow : timestampUtc;
        }

        public bool TryGet(string key, out object value)
        {
            value = null;
            return Properties != null && Properties.TryGetValue(key, out value);
        }

        /// <summary>The delivery record for a sink, or null when that sink never saw the event.</summary>
        public TestEventDelivery DeliveryTo(string sink)
        {
            if (Deliveries == null) return null;

            for (int i = 0; i < Deliveries.Count; i++)
                if (string.Equals(Deliveries[i].Sink, sink, StringComparison.OrdinalIgnoreCase))
                    return Deliveries[i];

            return null;
        }

        public bool WasDeliveredTo(string sink) => DeliveryTo(sink)?.Delivered == true;

        public override string ToString() => $"#{Sequence} {Name}";
    }
}
