using System.Collections.Generic;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// A player persona: something that looks at the game and decides what to touch next.
    /// </summary>
    /// <remarks>
    /// A <see cref="ScriptableObject"/> rather than a plain class, because the interesting differences
    /// between personas are usually numbers, not code — how often this one uses a booster, how long it
    /// dithers, how greedily it plays. Making those serialized fields means a team can ship "Casual",
    /// "Impatient" and "Whale" as three assets off one script, tune them without recompiling, and drop a
    /// new one into a test by name.
    /// <para>
    /// The contract is narrow on purpose: <see cref="Decide"/> is handed a read-only
    /// <see cref="BotContext"/> and answers with a <see cref="BotAction"/>. A bot cannot call into the
    /// game — everything it does is performed as real input by the driver — so a flow a bot cannot get
    /// through is a flow a player cannot get through. That is the property that makes a bot's findings
    /// worth acting on.
    /// </para>
    /// <example>
    /// <code>
    /// [CreateAssetMenu(menuName = "MyGame/Bots/Shopper")]
    /// public sealed class ShopperBot : GameBot
    /// {
    ///     [Range(0f, 1f)] public float BuysOnSight = 0.3f;
    ///
    ///     public override BotAction Decide(BotContext ctx)
    ///     {
    ///         var buy = ctx.FindByText("Buy");
    ///         if (buy != null &amp;&amp; ctx.Chance(BuysOnSight)) return BotAction.Tap(buy.Selector, "impulse buy");
    ///         return BotAction.Wait(0.3f);
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public abstract class GameBot : ScriptableObject
    {
        [Tooltip("How this bot is named in scripts and reports. Falls back to the asset name.")]
        [SerializeField] private string _botName;

        [Tooltip("What kind of player this is — shown in the run report.")]
        [TextArea(2, 4)]
        [SerializeField] private string _persona;

        [Tooltip("Seconds to pause after each action. A human is not instant; 0 is a stress test.")]
        [Min(0f)]
        [SerializeField] private float _thinkTime = 0.15f;

        [Tooltip("Stop the run once this many actions have been taken. 0 = no limit.")]
        [Min(0)]
        [SerializeField] private int _maxActions;

        public string BotName => string.IsNullOrEmpty(_botName) ? name : _botName;

        public string Persona => _persona;

        public float ThinkTime => _thinkTime;

        public int MaxActions => _maxActions;

        /// <summary>
        /// Choose the next thing to do. Called once per beat while the bot is playing.
        /// </summary>
        /// <remarks>
        /// Return <see cref="BotAction.Wait"/> when the right move is to let the game run — an animation,
        /// a scroll, a cannon firing. Return <see cref="BotAction.Stuck"/> when nothing on screen looks
        /// actionable; the driver decides whether that is a defect or just a slow moment, because it can
        /// see whether the game is still changing on its own.
        /// </remarks>
        public abstract BotAction Decide(BotContext ctx);

        /// <summary>Called once before the first decision. Reset any per-run state here.</summary>
        public virtual void OnRunStarted(BotContext ctx) { }

        /// <summary>Called once when the run ends, whatever the reason.</summary>
        public virtual void OnRunFinished(BotContext ctx, BotRunReport report) { }

        /// <summary>
        /// Values this bot wants recorded every beat, for the report's timeline.
        /// </summary>
        /// <remarks>
        /// Binding paths, e.g. <c>ms.level</c>, <c>ms.pixelsAlive</c>. The driver samples them alongside
        /// each action, which is what turns a list of taps into something you can diagnose — you can see
        /// the number that stopped moving, and when.
        /// </remarks>
        public virtual IEnumerable<string> TrackedValues() { yield break; }
    }
}
