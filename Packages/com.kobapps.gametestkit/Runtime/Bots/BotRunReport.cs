using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;

namespace Kobapps.GameTestKit
{
    /// <summary>Why a bot's run ended.</summary>
    public enum BotOutcome
    {
        /// <summary>The bot said it was finished.</summary>
        Done,
        /// <summary>The goal expression became true.</summary>
        GoalReached,
        /// <summary>Ran out of actions or wall-clock budget with the goal unmet.</summary>
        BudgetSpent,
        /// <summary>Nothing the bot did changed anything, and the game was not moving on its own.</summary>
        Stuck,
        /// <summary>The game logged an error, or an action could not be performed.</summary>
        Faulted,
    }

    /// <summary>One thing the bot did, and what the game looked like right after.</summary>
    public sealed class BotStep
    {
        public int Index;
        public float AtSeconds;
        public string Action;
        public string Scene;
        public bool ChangedSomething;
        public string Problem;
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
    }

    /// <summary>
    /// What happened while a bot played — the artefact you read when it finds something.
    /// </summary>
    /// <remarks>
    /// A bot run's value is not pass/fail, it is the trail. <see cref="Findings"/> holds what looked
    /// wrong, and every entry points at the step index where it happened, so the action log and the
    /// tracked values around it are right there. Screenshots are filed by the driver at the moment a
    /// finding is raised rather than at the end, because by the end the screen has usually moved on.
    /// </remarks>
    public sealed class BotRunReport
    {
        public string BotName;
        public string Persona;
        public BotOutcome Outcome = BotOutcome.BudgetSpent;
        public string Summary;
        public int Seed;
        public float DurationSeconds;
        public int ActionsTaken;

        public readonly List<BotStep> Steps = new List<BotStep>();

        /// <summary>Anything worth a human look: logged errors, dead ends, unreachable elements.</summary>
        public readonly List<string> Findings = new List<string>();

        public readonly List<string> Screenshots = new List<string>();

        public bool HasFindings => Findings.Count > 0;

        public JsonValue ToJson()
        {
            var steps = JsonValue.NewArray();
            foreach (var step in Steps)
            {
                var item = JsonValue.NewObject()
                    .Set("index", step.Index)
                    .Set("at", step.AtSeconds)
                    .Set("action", step.Action)
                    .Set("scene", step.Scene)
                    .Set("changed", step.ChangedSomething);

                if (!string.IsNullOrEmpty(step.Problem)) item.Set("problem", step.Problem);

                if (step.Values.Count > 0)
                {
                    var values = JsonValue.NewObject();
                    foreach (var pair in step.Values) values.Set(pair.Key, pair.Value);
                    item["values"] = values;
                }
                steps.Add(item);
            }

            var findings = JsonValue.NewArray();
            foreach (var finding in Findings) findings.Add(JsonValue.New(finding));

            var shots = JsonValue.NewArray();
            foreach (var shot in Screenshots) shots.Add(JsonValue.New(shot));

            var json = JsonValue.NewObject()
                .Set("bot", BotName)
                .Set("persona", Persona)
                .Set("outcome", Outcome.ToString())
                .Set("summary", Summary)
                .Set("seed", Seed)
                .Set("durationSeconds", DurationSeconds)
                .Set("actions", ActionsTaken);

            json["findings"] = findings;
            json["screenshots"] = shots;
            json["steps"] = steps;
            return json;
        }

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{BotName} — {Outcome} after {ActionsTaken} actions in {DurationSeconds:0.#}s");
            if (!string.IsNullOrEmpty(Summary)) sb.AppendLine(Summary);

            if (Findings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Findings ({Findings.Count}):");
                foreach (var finding in Findings) sb.AppendLine("  • " + finding);
            }
            return sb.ToString();
        }
    }
}
