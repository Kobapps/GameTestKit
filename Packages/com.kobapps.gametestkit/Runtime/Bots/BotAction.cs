using System;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>What a bot asked to do. The driver turns it into real input.</summary>
    public enum BotActionKind
    {
        /// <summary>Nothing to do this beat — let the game run.</summary>
        Wait,
        Tap,
        DoubleTap,
        Hold,
        Drag,
        Swipe,
        Press,
        Type,
        /// <summary>The bot considers its goal reached. The run ends successfully.</summary>
        Done,
        /// <summary>The bot cannot see anything to do. The driver decides whether that is a defect.</summary>
        Stuck,
    }

    /// <summary>
    /// One thing a bot wants to do, expressed as input rather than as a game call.
    /// </summary>
    /// <remarks>
    /// A bot returns an <em>intent</em> and the driver performs it through the same virtual pointer a
    /// scripted test uses — so everything a bot does goes through the game's real input path, hit tests
    /// included. That constraint is the whole point of the type: a bot literally cannot reach past the UI,
    /// because there is no action kind that would let it. A bot that "plays" by calling
    /// <c>GameSession.TryMerge</c> proves the rules work and nothing about the game; one that has to find a
    /// tile on screen and drag it proves a player could.
    /// <para>
    /// Targets are selectors, so a bot names things the way a test does (<c>id:play_button</c>,
    /// <c>text:"Continue"</c>) and inherits the implicit waiting and the "covered by X" diagnostics.
    /// </para>
    /// </remarks>
    public sealed class BotAction
    {
        public BotActionKind Kind { get; private set; }

        /// <summary>Selector for the thing to touch, or null when <see cref="Point"/> is used.</summary>
        public string Target { get; private set; }

        /// <summary>Selector for a drag's destination.</summary>
        public string SecondTarget { get; private set; }

        public Vector2? Point { get; private set; }
        public Vector2? SecondPoint { get; private set; }

        /// <summary>Seconds, for <see cref="BotActionKind.Wait"/> and <see cref="BotActionKind.Hold"/>.</summary>
        public float Seconds { get; private set; }

        /// <summary>Text to type, or the key name to press.</summary>
        public string Text { get; private set; }

        /// <summary>Why the bot chose this — shown in the report next to the action.</summary>
        public string Reason { get; private set; }

        /// <summary>
        /// This beat is a deliberate pause, not a failure to find anything to do.
        /// </summary>
        /// <remarks>
        /// The driver calls a run stuck when nothing changes for long enough, which is right for a bot
        /// that has run out of ideas and wrong for one watching a 30-second video ad — the screen is
        /// supposed to sit still, and the bot is supposed to let it. Marking the wait says "count this as
        /// progress", so the stuck detector keeps its teeth everywhere else.
        /// </remarks>
        public bool Patient { get; private set; }

        private BotAction(BotActionKind kind) { Kind = kind; }

        private BotAction With(string reason) { Reason = reason; return this; }

        public static BotAction Wait(float seconds = 0.25f, string why = null) =>
            new BotAction(BotActionKind.Wait) { Seconds = seconds }.With(why);

        /// <summary>A wait the stuck detector must not count against the bot — an ad, a cutscene, a load.</summary>
        public static BotAction WaitPatiently(float seconds, string why) =>
            new BotAction(BotActionKind.Wait) { Seconds = seconds, Patient = true }.With(why);

        public static BotAction Tap(string selector, string why = null) =>
            new BotAction(BotActionKind.Tap) { Target = selector }.With(why);

        public static BotAction TapPoint(Vector2 screenPoint, string why = null) =>
            new BotAction(BotActionKind.Tap) { Point = screenPoint }.With(why);

        public static BotAction DoubleTap(string selector, string why = null) =>
            new BotAction(BotActionKind.DoubleTap) { Target = selector }.With(why);

        public static BotAction Hold(string selector, float seconds = 0.6f, string why = null) =>
            new BotAction(BotActionKind.Hold) { Target = selector, Seconds = seconds }.With(why);

        public static BotAction Drag(string fromSelector, string toSelector, string why = null) =>
            new BotAction(BotActionKind.Drag) { Target = fromSelector, SecondTarget = toSelector }.With(why);

        public static BotAction DragPoints(Vector2 from, Vector2 to, string why = null) =>
            new BotAction(BotActionKind.Drag) { Point = from, SecondPoint = to }.With(why);

        public static BotAction Swipe(Vector2 from, Vector2 to, string why = null) =>
            new BotAction(BotActionKind.Swipe) { Point = from, SecondPoint = to }.With(why);

        public static BotAction Press(string key, string why = null) =>
            new BotAction(BotActionKind.Press) { Text = key }.With(why);

        public static BotAction Type(string selector, string text, string why = null) =>
            new BotAction(BotActionKind.Type) { Target = selector, Text = text }.With(why);

        public static BotAction Done(string why = null) => new BotAction(BotActionKind.Done).With(why);

        public static BotAction Stuck(string why = null) => new BotAction(BotActionKind.Stuck).With(why);

        public string Describe()
        {
            string what = Kind switch
            {
                BotActionKind.Wait => $"wait {Seconds:0.##}s",
                BotActionKind.Tap => $"tap {Target ?? Point?.ToString()}",
                BotActionKind.DoubleTap => $"double-tap {Target ?? Point?.ToString()}",
                BotActionKind.Hold => $"hold {Target} for {Seconds:0.##}s",
                BotActionKind.Drag => $"drag {Target ?? Point?.ToString()} → {SecondTarget ?? SecondPoint?.ToString()}",
                BotActionKind.Swipe => $"swipe {Point} → {SecondPoint}",
                BotActionKind.Press => $"press {Text}",
                BotActionKind.Type => $"type \"{Text}\" into {Target}",
                BotActionKind.Done => "done",
                BotActionKind.Stuck => "stuck",
                _ => Kind.ToString(),
            };
            return string.IsNullOrEmpty(Reason) ? what : $"{what}  ({Reason})";
        }

        public override string ToString() => Describe();
    }
}
