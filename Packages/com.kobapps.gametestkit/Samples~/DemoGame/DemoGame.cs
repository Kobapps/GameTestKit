using System;
using System.Collections;
using System.Collections.Generic;
using Kobapps.GameTestKit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Kobapps.GameTestKit.Samples
{
    /// <summary>
    /// A miniature game — menu, shop, name entry and a drag-and-drop board — built entirely from code
    /// so the sample needs no scene, no prefabs and no build-settings changes. Call
    /// <c>demo.start</c> from a test (or press Play with a <see cref="DemoGameBootstrap"/> in the
    /// scene) and it appears.
    /// </summary>
    /// <remarks>
    /// It exists to show the three things that make a game testable:
    /// <list type="number">
    /// <item><description><see cref="TestId"/> on everything a test touches.</description></item>
    /// <item><description>Bindings for state a test needs to assert on or set up.</description></item>
    /// <item><description>Real uGUI interactions — buttons, an input field, drag and drop — so the
    /// simulated input is exercising the same code path a player would.</description></item>
    /// </list>
    /// </remarks>
    public sealed class DemoGame : MonoBehaviour
    {
        public static DemoGame Instance { get; private set; }

        public int Gold { get; private set; } = 100;
        public string PlayerName { get; private set; } = "";
        public readonly List<string> Inventory = new List<string>();
        public string PlacedCard { get; private set; } = "";

        private Text _goldLabel;
        private Text _statusLabel;
        private GameObject _shopPanel;
        private InputField _nameField;
        private Font _font;

        // ================================================================ bindings

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterTestBindings()
        {
            // A coroutine, not an action, so it can wait for the UI it just built to be able to
            // receive input. An EventSystem created this frame has its input module enabled but its
            // actions not yet listening; a tap delivered before that lands nowhere, and the failure
            // is silent — the click step passes and whatever it was supposed to open never opens.
            GameTestBindings.BindCoroutine("demo.start", _ => StartRoutine(),
                "Creates the demo game UI in the current scene and waits until it can take input.");

            GameTestBindings.BindAction("demo.reset", _ =>
            {
                if (Instance != null) Destroy(Instance.gameObject);
                Instance = null;
            }, "Tears the demo down again.");

            GameTestBindings.BindValue("demo.gold", () => Instance != null ? Instance.Gold : 0,
                "Player's gold.");

            GameTestBindings.BindValue("demo.items", () => Instance != null ? Instance.Inventory.Count : 0,
                "How many items the player owns.");

            GameTestBindings.BindValue("demo.playerName", () => Instance != null ? Instance.PlayerName : "",
                "Name entered in the shop.");

            GameTestBindings.BindValue("demo.placedCard", () => Instance != null ? Instance.PlacedCard : "",
                "Which card was dropped on the board slot.");

            GameTestBindings.BindAction("demo.grantGold", args =>
            {
                if (Instance != null) Instance.Gold += Convert.ToInt32(args[0]);
                Instance?.RefreshGold();
            }, "Adds gold. Use it in setup instead of grinding for it.");
        }

        public static DemoGame Spawn()
        {
            if (Instance != null) return Instance;

            var host = new GameObject("DemoGame");
            Instance = host.AddComponent<DemoGame>();
            return Instance;
        }

        /// <summary>
        /// Spawns the UI and does not return until uGUI can actually route a pointer to it.
        /// </summary>
        /// <remarks>
        /// Two frames, because that is what it takes: one for the EventSystem's input module to enable
        /// and one for its actions to start reporting. Without the wait the first interaction of a run
        /// is a coin toss — which is exactly the kind of flake that gets blamed on the test rather than
        /// on the fixture that set it up.
        /// </remarks>
        private static IEnumerator StartRoutine()
        {
            Spawn();

            yield return null;
            yield return null;
        }

        // ================================================================ construction

        private void Awake()
        {
            Instance = this;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUi();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void BuildUi()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("DemoCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            var root = canvasObject.transform;

            Label(root, "Title", "Demo Game", new Vector2(0.5f, 0.88f), new Vector2(400, 60), 34);

            _goldLabel = Label(root, "GoldLabel", "100", new Vector2(0.12f, 0.92f), new Vector2(200, 40), 24);
            TestId.Assign(_goldLabel.gameObject, "gold_label", "Shows the player's gold.");

            _statusLabel = Label(root, "StatusLabel", "", new Vector2(0.5f, 0.06f), new Vector2(700, 40), 20);
            TestId.Assign(_statusLabel.gameObject, "status_label", "Last thing that happened.");

            var shopButton = Button(root, "ShopButton", "Shop", new Vector2(0.5f, 0.70f), OpenShop);
            TestId.Assign(shopButton, "shop_button", "Opens the shop.");

            var playButton = Button(root, "PlayButton", "Play", new Vector2(0.5f, 0.58f),
                () => Status("Playing!"));
            TestId.Assign(playButton, "play_button", "Starts the game.");

            BuildShop(root);
            BuildBoard(root);

            RefreshGold();
        }

        private void BuildShop(Transform root)
        {
            _shopPanel = Panel(root, "ShopPanel", new Vector2(0.5f, 0.5f), new Vector2(560, 400));
            TestId.Assign(_shopPanel, "shop_panel", "The shop dialog.");

            var panel = _shopPanel.transform;

            Label(panel, "ShopTitle", "Shop", new Vector2(0.5f, 0.88f), new Vector2(300, 50), 28);

            var sword = Button(panel, "SwordButton", "Sword — 60", new Vector2(0.5f, 0.68f), () => Buy("Sword", 60));
            TestId.Assign(sword, "buy_sword", "Buys a sword for 60 gold.");

            var shield = Button(panel, "ShieldButton", "Shield — 30", new Vector2(0.5f, 0.54f), () => Buy("Shield", 30));
            TestId.Assign(shield, "buy_shield", "Buys a shield for 30 gold.");

            _nameField = InputFieldControl(panel, "NameField", "your name", new Vector2(0.5f, 0.36f));
            TestId.Assign(_nameField.gameObject, "name_field", "Engraving name for the purchase.");
            // React to every change rather than only to onEndEdit: it is what most games do, and it
            // keeps the field testable on platforms where committing with Enter is not a thing.
            _nameField.onValueChanged.AddListener(value =>
            {
                PlayerName = value;
                Status($"Engraved for {value}");
            });

            var close = Button(panel, "CloseButton", "Close", new Vector2(0.5f, 0.16f), CloseShop);
            TestId.Assign(close, "close_shop", "Closes the shop.");

            _shopPanel.SetActive(false);
        }

        private void BuildBoard(Transform root)
        {
            var slot = Panel(root, "BoardSlot", new Vector2(0.82f, 0.45f), new Vector2(160, 160));
            slot.GetComponent<Image>().color = new Color(0.2f, 0.25f, 0.35f, 0.9f);
            TestId.Assign(slot, "board_slot", "Drop a card here.");
            slot.AddComponent<CardSlot>();

            var card = Panel(root, "Card", new Vector2(0.2f, 0.45f), new Vector2(120, 160));
            card.GetComponent<Image>().color = new Color(0.6f, 0.35f, 0.2f, 0.95f);
            TestId.Assign(card, "card_dragon", "A draggable card.");
            Label(card.transform, "CardLabel", "Dragon", new Vector2(0.5f, 0.5f), new Vector2(110, 40), 18);
            card.AddComponent<DraggableCard>().CardName = "Dragon";
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));

            // Use whichever input module matches the project's active input backend.
#if ENABLE_INPUT_SYSTEM
            var moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (moduleType != null)
            {
                var module = go.AddComponent(moduleType);

                // A module added from code has no actions assigned, and an InputSystemUIInputModule
                // without actions silently ignores every pointer event — including simulated ones.
                // The Editor's "UI ▸ Event System" menu does this for you; AddComponent does not.
                var assign = moduleType.GetMethod("AssignDefaultActions");

                if (assign == null)
                    Debug.LogError(
                        "[DemoGame] This version of the Input System has no AssignDefaultActions, so the " +
                        "UI input module has no actions and will ignore every pointer event. Every click " +
                        "and drag in this sample will appear to do nothing.");
                else
                    assign.Invoke(module, null);
            }
            else
            {
                go.AddComponent<StandaloneInputModule>();
            }
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        // ================================================================ gameplay

        private void OpenShop()
        {
            _shopPanel.SetActive(true);
            Status("Shop open");

            DemoAnalytics.Send("Shop_Opened", new Dictionary<string, object>
            {
                { "Gold", Gold },
            });
        }

        private void CloseShop()
        {
            _shopPanel.SetActive(false);
            Status("Shop closed");
        }

        /// <summary>
        /// Buying, and the event that reports it.
        /// </summary>
        /// <remarks>
        /// The declined path sends the <em>same</em> event with a different result rather than staying
        /// silent, which is what lets a funnel tell "nobody tried" apart from "everybody tried and could
        /// not afford it". It is also why the sample can show <c>Purchase_Result</c> and
        /// <c>Fail_Reason</c> being asserted against the game's own constants.
        /// </remarks>
        private void Buy(string item, int price)
        {
            bool affordable = Gold >= price;

            if (affordable)
            {
                Gold -= price;
                Inventory.Add(item);
                RefreshGold();
                Status($"Bought {item}");
            }
            else
            {
                Status($"Not enough gold for {item}");
            }

            DemoAnalytics.Send("Purchase", new Dictionary<string, object>
            {
                { "Item", item },
                { "Price", price },
                { "Gold_After", Gold },
                { "Purchase_Result", affordable ? DemoTelemetry.RESULT_BOUGHT : DemoTelemetry.RESULT_DECLINED },
                { "Fail_Reason", affordable ? DemoTelemetry.REASON_NONE : DemoTelemetry.REASON_NOT_ENOUGH_GOLD },
                { "Engraved_For", PlayerName },
                { "Items_Owned", Inventory.Count },
            });
        }

        internal void PlaceCard(string cardName)
        {
            PlacedCard = cardName;
            Status($"Placed {cardName}");

            DemoAnalytics.Send("Card_Placed", new Dictionary<string, object>
            {
                { "Card", cardName },
            });
        }

        private void RefreshGold()
        {
            if (_goldLabel != null) _goldLabel.text = Gold.ToString();
        }

        private void Status(string message)
        {
            if (_statusLabel != null) _statusLabel.text = message;
        }

        // ================================================================ ui helpers

        private Text Label(Transform parent, string name, string text, Vector2 anchor, Vector2 size, int fontSize)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);

            var label = go.GetComponent<Text>();
            label.font = _font;
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;

            Place(go, anchor, size);
            return label;
        }

        private GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.17f, 0.96f);
            Place(go, anchor, size);
            return go;
        }

        private GameObject Button(Transform parent, string name, string text, Vector2 anchor, Action onClick)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.22f, 0.42f, 0.72f, 1f);
            go.GetComponent<Button>().onClick.AddListener(() => onClick());

            Place(go, anchor, new Vector2(260, 56));
            Label(go.transform, "Label", text, new Vector2(0.5f, 0.5f), new Vector2(240, 40), 22);
            return go;
        }

        private InputField InputFieldControl(Transform parent, string name, string placeholder, Vector2 anchor)
        {
            var go = new GameObject(name, typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.9f, 0.9f, 0.92f, 1f);
            Place(go, anchor, new Vector2(300, 44));

            var text = Label(go.transform, "Text", "", new Vector2(0.5f, 0.5f), new Vector2(280, 34), 20);
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            var hint = Label(go.transform, "Placeholder", placeholder, new Vector2(0.5f, 0.5f), new Vector2(280, 34), 20);
            hint.color = new Color(0.45f, 0.45f, 0.45f, 1f);
            hint.alignment = TextAnchor.MiddleLeft;
            hint.fontStyle = FontStyle.Italic;

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            return field;
        }

        private static void Place(GameObject go, Vector2 anchor, Vector2 size)
        {
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }
    }

    /// <summary>Drop one of these in a scene to launch the demo without a test.</summary>
    public sealed class DemoGameBootstrap : MonoBehaviour
    {
        private void Start() => DemoGame.Spawn();
    }

    /// <summary>A card the player drags. Implements the real uGUI drag interface, not a shortcut.</summary>
    public sealed class DraggableCard : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public string CardName = "Card";

        private Transform _originalParent;
        private Vector3 _originalPosition;
        private CanvasGroup _group;

        public void OnBeginDrag(PointerEventData eventData)
        {
            _originalParent = transform.parent;
            _originalPosition = transform.position;

            // Not `?? AddComponent`: a missing component comes back as Unity's fake null, which is not
            // null to the ?? operator, so that idiom silently returns a dead reference.
            _group = gameObject.GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;   // so the slot underneath can receive the drop

            transform.SetParent(transform.root, true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            transform.position = eventData.position;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_group != null) _group.blocksRaycasts = true;

            if (transform.parent == transform.root)
            {
                transform.SetParent(_originalParent, true);
                transform.position = _originalPosition;
            }
        }
    }

    /// <summary>The board slot a card can be dropped onto.</summary>
    public sealed class CardSlot : MonoBehaviour, IDropHandler
    {
        public void OnDrop(PointerEventData eventData)
        {
            var card = eventData.pointerDrag != null ? eventData.pointerDrag.GetComponent<DraggableCard>() : null;
            if (card == null) return;

            card.transform.SetParent(transform, false);
            card.transform.localPosition = Vector3.zero;

            if (DemoGame.Instance != null) DemoGame.Instance.PlaceCard(card.CardName);
        }
    }
}
