using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Registers every step the framework ships with. Each entry carries its documentation, which is
    /// what the validator, the GameTester window and the generated AI skill all read — so a step is
    /// documented exactly once, here.
    /// </summary>
    internal static class BuiltinSteps
    {
        private const string Input = "Input";
        private const string Flow = "Flow";
        private const string Scene = "Scene";
        private const string Assert = "Assertions";

        private static readonly StepParameter Timeout =
            new StepParameter("timeout", "number", "Seconds this step may take before it fails.");

        private static readonly StepParameter Label =
            new StepParameter("label", "string", "Human-readable name for this step in the report.");

        private static readonly StepParameter Soft =
            new StepParameter("continueOnFailure", "bool", "Record the failure but keep running.", false, "false");

        private static StepParameter[] With(params StepParameter[] parameters)
        {
            var all = new StepParameter[parameters.Length + 3];
            parameters.CopyTo(all, 0);
            all[parameters.Length] = Timeout;
            all[parameters.Length + 1] = Label;
            all[parameters.Length + 2] = Soft;
            return all;
        }

        public static void RegisterAll()
        {
            // ------------------------------------------------------------ input

            StepRegistry.Register(new StepDefinition
            {
                Key = "click",
                Aliases = new[] { "tap" },
                Category = Input,
                Summary = "Move the pointer onto an element and press it, exactly as a player would. " +
                          "Waits until the element exists, is visible, is interactable and is not covered.",
                Parameters = With(
                    new StepParameter("click", "selector", "What to click.", true),
                    new StepParameter("button", "string", "left, right or middle.", false, "left"),
                    new StepParameter("clicks", "number", "2 for a double click.", false, "1"),
                    new StepParameter("at", "array", "Normalised point inside the element, e.g. [0.9,0.5] for the right end of a slider.", false, "centre"),
                    new StepParameter("offset", "array", "Pixel offset [dx,dy] applied after 'at'."),
                    new StepParameter("force", "bool", "Click even if something is covering the element.", false, "false")),
                Example = "{ \"click\": \"id:play_button\" }",
                Factory = json => new ClickStep
                {
                    Selector = StepJson.RequiredString(json, PickKey(json, "click", "tap"), "click"),
                    Button = StepJson.Button(json, "button"),
                    Clicks = StepJson.Int(json, "clicks", 1),
                    NormalizedAt = StepJson.Vector(json, "at"),
                    Offset = StepJson.Vector(json, "offset") ?? Vector2.zero,
                    Force = StepJson.Bool(json, "force", false),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "doubleClick",
                Category = Input,
                Summary = "Two clicks in quick succession.",
                Parameters = With(new StepParameter("doubleClick", "selector", "What to double-click.", true)),
                Example = "{ \"doubleClick\": \"#InventorySlot\" }",
                Factory = json => new ClickStep
                {
                    Selector = StepJson.RequiredString(json, "doubleClick", "doubleClick"),
                    Clicks = 2,
                    Button = StepJson.Button(json, "button"),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "hold",
                Aliases = new[] { "longPress" },
                Category = Input,
                Summary = "Press and keep holding for a duration, then release.",
                Parameters = With(
                    new StepParameter("hold", "selector", "What to press.", true),
                    new StepParameter("seconds", "number", "How long to hold.", false, "1"),
                    new StepParameter("button", "string", "left, right or middle.", false, "left")),
                Example = "{ \"hold\": \"id:charge_button\", \"seconds\": 1.5 }",
                Factory = json => new HoldStep
                {
                    Selector = StepJson.RequiredString(json, PickKey(json, "hold", "longPress"), "hold"),
                    Seconds = StepJson.Float(json, "seconds", 1f),
                    Button = StepJson.Button(json, "button"),
                    Force = StepJson.Bool(json, "force", false),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "move",
                Aliases = new[] { "hover" },
                Category = Input,
                Summary = "Move the pointer over an element without pressing — hover states and tooltips.",
                Parameters = With(
                    new StepParameter("move", "selector", "Where to move the pointer.", true),
                    new StepParameter("duration", "number", "Seconds the movement takes.", false, "0.08")),
                Example = "{ \"hover\": \"#ItemIcon\" }",
                Factory = json => new MoveStep
                {
                    Selector = StepJson.RequiredString(json, PickKey(json, "move", "hover"), "move"),
                    Duration = StepJson.Float(json, "duration", 0.08f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "drag",
                Category = Input,
                Summary = "Press on one element, travel to another over time, and release there. " +
                          "The path is interpolated so drag thresholds and begin/drag/end handlers fire properly.",
                Parameters = With(
                    new StepParameter("drag", "selector", "What to pick up.", true),
                    new StepParameter("to", "selector", "Where to drop it. Accepts pos:/screen:/world: points.", true),
                    new StepParameter("duration", "number", "Seconds the drag takes.", false, "0.35"),
                    new StepParameter("offset", "array", "Pixel offset applied to the drop point.")),
                Example = "{ \"drag\": \"id:card_3\", \"to\": \"id:board_slot_1\", \"duration\": 0.5 }",
                Factory = json => new DragStep
                {
                    FromSelector = StepJson.RequiredString(json, "drag", "drag"),
                    ToSelector = StepJson.RequiredString(json, "to", "drag"),
                    ToOffset = StepJson.Vector(json, "offset") ?? Vector2.zero,
                    Duration = StepJson.Float(json, "duration", 0.35f),
                    Button = StepJson.Button(json, "button"),
                    Force = StepJson.Bool(json, "force", false),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "swipe",
                Category = Input,
                Summary = "A flick in a direction, released while still moving — carousels, page turns, camera pans.",
                Parameters = With(
                    new StepParameter("swipe", "string", "left, right, up or down.", true),
                    new StepParameter("from", "selector", "Where the swipe starts.", false, "screen centre"),
                    new StepParameter("distance", "number", "Pixels travelled.", false, "40% of the screen"),
                    new StepParameter("duration", "number", "Seconds the swipe takes.", false, "0.15")),
                Example = "{ \"swipe\": \"left\", \"from\": \"#LevelCarousel\" }",
                Factory = json => new SwipeStep
                {
                    Direction = StepJson.RequiredString(json, "swipe", "swipe"),
                    FromSelector = StepJson.OptionalString(json, "from"),
                    Distance = StepJson.Float(json, "distance", 0f),
                    Duration = StepJson.Float(json, "duration", 0.15f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "scroll",
                Category = Input,
                Summary = "Mouse-wheel scroll in wheel notches. Negative scrolls down.",
                Parameters = With(
                    new StepParameter("scroll", "number", "Vertical notches; negative scrolls down.", true),
                    new StepParameter("horizontal", "number", "Horizontal notches.", false, "0"),
                    new StepParameter("over", "selector", "Element to scroll over.", false, "screen centre")),
                Example = "{ \"scroll\": -4, \"over\": \"#ShopList\" }",
                Factory = json => new ScrollStep
                {
                    Amount = StepJson.Float(json, "scroll", -3f),
                    Horizontal = StepJson.Float(json, "horizontal", 0f),
                    Selector = StepJson.OptionalString(json, "over") ?? StepJson.OptionalString(json, "at"),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "type",
                Category = Input,
                Summary = "Type text one character at a time as real text input events.",
                Parameters = With(
                    new StepParameter("type", "string", "The text to type.", true),
                    new StepParameter("into", "selector", "Field to focus first (it is clicked)."),
                    new StepParameter("clear", "bool", "Empty the field before typing.", false, "false"),
                    new StepParameter("perCharacter", "number", "Seconds between characters.", false, "0.03")),
                Example = "{ \"type\": \"Ada\", \"into\": \"id:name_field\", \"clear\": true }",
                Factory = json => new TypeTextStep
                {
                    Text = StepJson.OptionalString(json, "type", ""),
                    IntoSelector = StepJson.OptionalString(json, "into"),
                    Clear = StepJson.Bool(json, "clear", false),
                    PerCharacterSeconds = StepJson.Float(json, "perCharacter", 0.03f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "press",
                Aliases = new[] { "key" },
                Category = Input,
                Summary = "Press and release a keyboard key.",
                Parameters = With(
                    new StepParameter("press", "string", "Key name: space, enter, escape, a, digit1, f5, leftShift, upArrow…", true),
                    new StepParameter("hold", "number", "Seconds to hold the key down.", false, "0.05"),
                    new StepParameter("repeat", "number", "Press this many times.", false, "1"),
                    new StepParameter("interval", "number", "Seconds between repeats.", false, "0.08")),
                Example = "{ \"press\": \"space\", \"repeat\": 3 }",
                Factory = json => new PressKeyStep
                {
                    Key = StepJson.RequiredString(json, PickKey(json, "press", "key"), "press"),
                    HoldSeconds = StepJson.Float(json, "hold", 0.05f),
                    Repeat = StepJson.Int(json, "repeat", 1),
                    IntervalSeconds = StepJson.Float(json, "interval", 0.08f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "keyDown",
                Category = Input,
                Summary = "Hold a key down until a later keyUp — walking, sprinting, modifier combos.",
                Parameters = With(new StepParameter("keyDown", "string", "Key name.", true)),
                Example = "{ \"keyDown\": \"w\" }",
                Factory = json => new KeyHoldStep { Key = StepJson.RequiredString(json, "keyDown", "keyDown"), Down = true },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "keyUp",
                Category = Input,
                Summary = "Release a key held by keyDown.",
                Parameters = With(new StepParameter("keyUp", "string", "Key name.", true)),
                Example = "{ \"keyUp\": \"w\" }",
                Factory = json => new KeyHoldStep { Key = StepJson.RequiredString(json, "keyUp", "keyUp"), Down = false },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "gamepad",
                Category = Input,
                Summary = "Press a gamepad button.",
                Parameters = With(
                    new StepParameter("gamepad", "string", "south/north/east/west, a/b/x/y, start, select, leftShoulder, dpadUp, leftTrigger…", true),
                    new StepParameter("hold", "number", "Seconds to hold.", false, "0.08")),
                Example = "{ \"gamepad\": \"south\" }",
                Factory = json => new GamepadButtonStep
                {
                    Button = StepJson.RequiredString(json, "gamepad", "gamepad"),
                    HoldSeconds = StepJson.Float(json, "hold", 0.08f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "stick",
                Category = Input,
                Summary = "Hold an analog stick in a direction, then centre it.",
                Parameters = With(
                    new StepParameter("stick", "string", "left or right.", true),
                    new StepParameter("value", "array", "[x,y] in the range -1..1.", true),
                    new StepParameter("seconds", "number", "How long to hold it.", false, "0.5")),
                Example = "{ \"stick\": \"left\", \"value\": [0, 1], \"seconds\": 1.2 }",
                Factory = json => new GamepadStickStep
                {
                    Stick = StepJson.RequiredString(json, "stick", "stick"),
                    Value = StepJson.Vector(json, "value") ?? Vector2.up,
                    Seconds = StepJson.Float(json, "seconds", 0.5f),
                },
            });

            // ------------------------------------------------------------ flow

            StepRegistry.Register(new StepDefinition
            {
                Key = "wait",
                Category = Flow,
                Summary = "Sleep for a fixed time. Prefer waitFor — fixed sleeps are the main source of flaky tests.",
                Parameters = With(
                    new StepParameter("wait", "number", "Seconds to wait.", true),
                    new StepParameter("realtime", "bool", "Ignore Time.timeScale.", false, "false")),
                Example = "{ \"wait\": 0.5 }",
                Factory = json => new WaitStep
                {
                    Seconds = StepJson.Float(json, "wait", 1f),
                    Realtime = StepJson.Bool(json, "realtime", false),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "waitFor",
                Category = Flow,
                Summary = "Wait until an expression becomes true. The correct way to synchronise with the game.",
                Parameters = With(
                    new StepParameter("waitFor", "expression", "e.g. \"player.gold >= 100 and visible('#Shop')\".", true),
                    new StepParameter("timeout", "number", "Seconds before giving up.", false, "10")),
                Example = "{ \"waitFor\": \"scene == 'Level1'\", \"timeout\": 20 }",
                Factory = json => new WaitForStep
                {
                    Expression = StepJson.RequiredString(json, "waitFor", "waitFor"),
                    Timeout = StepJson.Float(json, "timeout", 10f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "waitForVisible",
                Category = Flow,
                Summary = "Wait until an element is on screen (optionally until it is also interactable).",
                Parameters = With(
                    new StepParameter("waitForVisible", "selector", "What to wait for.", true),
                    new StepParameter("interactable", "bool", "Also require it to accept input.", false, "false"),
                    new StepParameter("timeout", "number", "Seconds before giving up.", false, "10")),
                Example = "{ \"waitForVisible\": \"id:reward_popup\" }",
                Factory = json => new WaitForElementStep
                {
                    Selector = StepJson.RequiredString(json, "waitForVisible", "waitForVisible"),
                    RequireInteractable = StepJson.Bool(json, "interactable", false),
                    Timeout = StepJson.Float(json, "timeout", 10f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "waitForGone",
                Category = Flow,
                Summary = "Wait until an element is no longer visible — loading screens, closing dialogs.",
                Parameters = With(
                    new StepParameter("waitForGone", "selector", "What should disappear.", true),
                    new StepParameter("timeout", "number", "Seconds before giving up.", false, "10")),
                Example = "{ \"waitForGone\": \"#LoadingSpinner\", \"timeout\": 30 }",
                Factory = json => new WaitForElementStep
                {
                    Selector = StepJson.RequiredString(json, "waitForGone", "waitForGone"),
                    Gone = true,
                    Timeout = StepJson.Float(json, "timeout", 10f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "waitForScene",
                Category = Flow,
                Summary = "Wait until a scene is loaded and active.",
                Parameters = With(
                    new StepParameter("waitForScene", "string", "Scene name.", true),
                    new StepParameter("active", "bool", "Require it to be the active scene.", false, "true"),
                    new StepParameter("timeout", "number", "Seconds before giving up.", false, "30")),
                Example = "{ \"waitForScene\": \"Gameplay\" }",
                Factory = json => new WaitForSceneStep
                {
                    SceneName = StepJson.RequiredString(json, "waitForScene", "waitForScene"),
                    MustBeActive = StepJson.Bool(json, "active", true),
                    Timeout = StepJson.Float(json, "timeout", 30f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "repeat",
                Category = Flow,
                Summary = "Run a block of steps several times.",
                Parameters = With(
                    new StepParameter("repeat", "number", "Iteration count.", true),
                    new StepParameter("steps", "steps", "The steps to repeat.", true)),
                Example = "{ \"repeat\": 3, \"steps\": [ { \"click\": \"id:buy\" }, { \"wait\": 0.2 } ] }",
                Factory = json => new RepeatStep
                {
                    Times = StepJson.Int(json, "repeat", 1),
                    Steps = StepJson.Steps(json, "steps", "repeat"),
                    // Same reasoning as `group`: the wrapper must not cap the sum of what it wraps.
                    TimeoutSeconds = StepJson.Float(json, "timeout", float.MaxValue),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "group",
                Category = Flow,
                Summary = "Name a block of steps so the report reads like the flow being tested.",
                Parameters = With(
                    new StepParameter("group", "string", "Block name.", true),
                    new StepParameter("steps", "steps", "The steps in the block.", true)),
                Example = "{ \"group\": \"Buy a sword\", \"steps\": [ … ] }",
                Factory = json => new GroupStep
                {
                    Name = StepJson.RequiredString(json, "group", "group"),
                    Steps = StepJson.Steps(json, "steps", "group"),
                    // A group is a label, not a budget. Left at the default step timeout it would cap the
                    // total of everything inside it — so three 75-second children under one heading die at
                    // 15 seconds, blaming the group rather than anything real. The runner clamps every step
                    // to the test deadline anyway, so handing the group an unbounded budget leaves each
                    // child limited by its own and the whole group by the test's.
                    TimeoutSeconds = StepJson.Float(json, "timeout", float.MaxValue),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "log",
                Category = Flow,
                Summary = "Write a note into the log and the report.",
                Parameters = With(new StepParameter("log", "string", "The message.", true)),
                Example = "{ \"log\": \"About to open the shop\" }",
                Factory = json => new LogStep { Message = StepJson.OptionalString(json, "log", "") },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "screenshot",
                Category = Flow,
                Summary = "Save a PNG of the game view into the run's artifact folder.",
                Parameters = With(new StepParameter("screenshot", "string", "File name (no extension).", true)),
                Example = "{ \"screenshot\": \"shop-open\" }",
                Factory = json => new ScreenshotStep { Name = StepJson.OptionalString(json, "screenshot", "screenshot") },
            });

            // ------------------------------------------------------------ scene & game state

            StepRegistry.Register(new StepDefinition
            {
                Key = "loadScene",
                Category = Scene,
                Summary = "Load a scene and wait for it to finish.",
                Parameters = With(
                    new StepParameter("loadScene", "string", "Scene name (must be in the build list).", true),
                    new StepParameter("additive", "bool", "Load alongside the current scene.", false, "false")),
                Example = "{ \"loadScene\": \"MainMenu\" }",
                Factory = json => new LoadSceneStep
                {
                    SceneName = StepJson.RequiredString(json, "loadScene", "loadScene"),
                    Additive = StepJson.Bool(json, "additive", false),
                    Timeout = StepJson.Float(json, "timeout", 60f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "unloadScene",
                Category = Scene,
                Summary = "Unload an additively loaded scene.",
                Parameters = With(new StepParameter("unloadScene", "string", "Scene name.", true)),
                Example = "{ \"unloadScene\": \"PauseMenu\" }",
                Factory = json => new UnloadSceneStep
                {
                    SceneName = StepJson.RequiredString(json, "unloadScene", "unloadScene"),
                    Timeout = StepJson.Float(json, "timeout", 30f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "call",
                Category = Scene,
                Summary = "Invoke a game action registered with GameTestBindings — set up state without clicking through it.",
                Parameters = With(
                    new StepParameter("call", "string", "Bound action name.", true),
                    new StepParameter("args", "array", "Arguments passed to the action.")),
                Example = "{ \"call\": \"grantGold\", \"args\": [500] }",
                Factory = json => new CallStep
                {
                    Action = StepJson.RequiredString(json, "call", "call"),
                    Args = StepJson.Args(json, "args"),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "timeScale",
                Category = Scene,
                Summary = "Set Time.timeScale — fast-forward long animations, or slow the game to watch a step.",
                Parameters = With(new StepParameter("timeScale", "number", "New time scale. 1 is normal.", true)),
                Example = "{ \"timeScale\": 4 }",
                Factory = json => new TimeScaleStep { Scale = StepJson.Float(json, "timeScale", 1f) },
            });

            // ------------------------------------------------------------ assertions

            StepRegistry.Register(new StepDefinition
            {
                Key = "assert",
                Category = Assert,
                Summary = "Assert an expression over game state and the screen.",
                Parameters = With(
                    new StepParameter("assert", "expression", "e.g. \"player.gold == 40\".", true),
                    new StepParameter("message", "string", "Prefix shown when it fails."),
                    new StepParameter("retryFor", "number", "Keep re-checking for this long before failing.", false, "0")),
                Example = "{ \"assert\": \"player.gold == 40\", \"message\": \"Sword should cost 60\" }",
                Factory = json => new AssertStep
                {
                    Expression = StepJson.RequiredString(json, "assert", "assert"),
                    Message = StepJson.OptionalString(json, "message"),
                    RetryFor = StepJson.Float(json, "retryFor", 0f),
                },
            });

            RegisterElementAssertion("assertVisible", ElementCondition.Visible, "on screen");
            RegisterElementAssertion("assertHidden", ElementCondition.Hidden, "not on screen");
            RegisterElementAssertion("assertExists", ElementCondition.Exists, "present in the hierarchy");
            RegisterElementAssertion("assertMissing", ElementCondition.Missing, "absent from the hierarchy");
            RegisterElementAssertion("assertInteractable", ElementCondition.Interactable, "able to receive input");
            RegisterElementAssertion("assertDisabled", ElementCondition.Disabled, "unable to receive input");

            StepRegistry.Register(new StepDefinition
            {
                Key = "assertText",
                Category = Assert,
                Summary = "Assert what an element says. Reads uGUI Text, TextMeshPro and input fields, " +
                          "including a label nested inside a button.",
                Parameters = With(
                    new StepParameter("assertText", "selector", "Element to read.", true),
                    new StepParameter("equals", "string", "Exact expected text (trimmed)."),
                    new StepParameter("contains", "string", "Substring the text must contain."),
                    new StepParameter("matches", "string", "Regular expression the text must match."),
                    new StepParameter("retryFor", "number", "Keep re-checking for this long.", false, "1")),
                Example = "{ \"assertText\": \"id:gold_label\", \"equals\": \"40\" }",
                Factory = json => new AssertTextStep
                {
                    Selector = StepJson.RequiredString(json, "assertText", "assertText"),
                    ExpectedEquals = StepJson.OptionalString(json, "equals"),
                    ExpectedContains = StepJson.OptionalString(json, "contains"),
                    ExpectedPattern = StepJson.OptionalString(json, "matches"),
                    RetryFor = StepJson.Float(json, "retryFor", 1f),
                },
            });

            StepRegistry.Register(new StepDefinition
            {
                Key = "expectLog",
                Category = Assert,
                Summary = "Declare that the game is supposed to log an error here, so the run's " +
                          "fail-on-error policy allows it. Fails if the message never appears.",
                Parameters = With(
                    new StepParameter("expectLog", "string", "Regular expression matched against log messages.", true),
                    new StepParameter("within", "number", "Seconds to wait for it.", false, "5")),
                Example = "{ \"expectLog\": \"Not enough gold\" }",
                Factory = json => new ExpectLogStep
                {
                    Pattern = StepJson.RequiredString(json, "expectLog", "expectLog"),
                    Within = StepJson.Float(json, "within", 5f),
                },
            });
        }

        private static void RegisterElementAssertion(string key, ElementCondition condition, string description)
        {
            StepRegistry.Register(new StepDefinition
            {
                Key = key,
                Category = Assert,
                Summary = $"Assert that an element is {description}.",
                Parameters = With(
                    new StepParameter(key, "selector", "Element to check.", true),
                    new StepParameter("retryFor", "number", "Keep re-checking for this long before failing.", false, "1")),
                Example = $"{{ \"{key}\": \"id:shop_panel\" }}",
                Factory = json => new AssertElementStep
                {
                    Selector = StepJson.RequiredString(json, key, key),
                    Condition = condition,
                    RetryFor = StepJson.Float(json, "retryFor", 1f),
                },
            });
        }

        /// <summary>Returns whichever of the alias keys the JSON object actually carries.</summary>
        private static string PickKey(Scripting.JsonValue json, params string[] keys)
        {
            foreach (var key in keys)
                if (json.Has(key)) return key;
            return keys[0];
        }
    }
}
