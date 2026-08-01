using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Kobapps.GameTestKit.Tests
{
    public class LocatorTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        private GameObject Make(string name, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent.transform, false);
            else _created.Add(go);
            return go;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        [Test]
        public void FindsByName()
        {
            var target = Make("LocatorTarget");
            Assert.That(Locator.Find("#LocatorTarget"), Is.EqualTo(target));
            Assert.That(Locator.Find("LocatorTarget"), Is.EqualTo(target));
        }

        [Test]
        public void FindsByPathSuffix()
        {
            var root = Make("LocatorRoot");
            var middle = Make("Middle", root);
            var leaf = Make("Leaf", middle);

            Assert.That(Locator.Find("Middle/Leaf"), Is.EqualTo(leaf));
            Assert.That(Locator.Find("LocatorRoot/Middle/Leaf"), Is.EqualTo(leaf));
        }

        [Test]
        public void FindsByTestId()
        {
            var target = Make("HasAnId");
            TestId.Assign(target, "the_id");

            Assert.That(Locator.Find("id:the_id"), Is.EqualTo(target));
            Assert.That(Locator.Find("id:THE_ID"), Is.EqualTo(target), "ids are case-insensitive");
        }

        [Test]
        public void FindsTestIdOnInactiveObjects()
        {
            var target = Make("StartsHidden");
            TestId.Assign(target, "hidden_id");
            target.SetActive(false);

            // "Matched nothing" would be a lie here — the element exists, it just isn't shown yet,
            // and steps need to be able to say which of the two is true.
            Assert.That(Locator.Find("id:hidden_id"), Is.EqualTo(target));
            Assert.That(UiProbe.IsVisible(target), Is.False);
        }

        [Test]
        public void FindsByComponentType()
        {
            var target = Make("HasAnImage");
            target.AddComponent<Image>();

            Assert.That(Locator.Find("type:Image"), Is.EqualTo(target));
            Assert.That(Locator.Find("type:UnityEngine.UI.Image"), Is.EqualTo(target));
        }

        [Test]
        public void TextSelectorResolvesToTheClickableOwner()
        {
            var button = Make("TheButton");
            button.AddComponent<RectTransform>();
            button.AddComponent<Image>();
            button.AddComponent<Button>();

            var label = Make("Label", button);
            label.AddComponent<RectTransform>();
            label.AddComponent<Text>().text = "Press Me";

            // A player clicks the button, not the letters inside it.
            Assert.That(Locator.Find("text:Press"), Is.EqualTo(button));
            Assert.That(Locator.Find("text:\"Press Me\""), Is.EqualTo(button));
            Assert.That(Locator.Find("text:\"Press\""), Is.Null, "quoted text must match exactly");
        }

        [Test]
        public void IndexPicksTheNthMatch()
        {
            var first = Make("Duplicate");
            var second = Make("Duplicate");

            var all = Locator.FindAll("#Duplicate");
            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(Locator.Find("#Duplicate[0]"), Is.EqualTo(all[0]));
            Assert.That(Locator.Find("#Duplicate[1]"), Is.EqualTo(all[1]));
            Assert.That(Locator.Find("#Duplicate[5]"), Is.Null);

            Assert.That(new[] { first, second }, Is.EquivalentTo(all));
        }

        [Test]
        public void ScopeOperatorSearchesDescendantsOnly()
        {
            var panelA = Make("PanelA");
            var insideA = Make("Shared", panelA);

            var panelB = Make("PanelB");
            Make("Shared", panelB);

            Assert.That(Locator.FindAll("#Shared").Count, Is.EqualTo(2));
            Assert.That(Locator.Find("#PanelA >> #Shared"), Is.EqualTo(insideA));
        }

        [Test]
        public void PointSelectorsResolveWithoutAnObject()
        {
            Assert.That(Locator.IsPointSelector("pos:0.5,0.5"), Is.True);

            Assert.That(Locator.TryResolvePoint("pos:0.5,0.25", out var normalized), Is.True);
            Assert.That(normalized.x, Is.EqualTo(Screen.width * 0.5f).Within(0.01f));
            Assert.That(normalized.y, Is.EqualTo(Screen.height * 0.25f).Within(0.01f));

            Assert.That(Locator.TryResolvePoint("screen:120,340", out var pixels), Is.True);
            Assert.That(pixels, Is.EqualTo(new Vector2(120, 340)));

            Assert.That(Locator.TryResolvePoint("#NotAPoint", out _), Is.False);
        }

        [Test]
        public void FindsInactiveObjectsToo()
        {
            var hidden = Make("HiddenThing");
            hidden.SetActive(false);

            Assert.That(Locator.Find("#HiddenThing"), Is.EqualTo(hidden));
            Assert.That(UiProbe.IsVisible(hidden), Is.False, "found is not the same as visible");
        }

        [Test]
        public void MissingSelectorErrorSuggestsWhatIsActuallyThere()
        {
            var target = Make("KnownThing");
            TestId.Assign(target, "known_id");

            var message = Locator.DescribeMiss("id:typo_id");
            Assert.That(message, Does.Contain("known_id"));
        }

        [Test]
        public void UnknownPrefixIsRejectedInsteadOfSilentlyMatchingNothing()
        {
            var exception = Assert.Throws<TestFailureException>(() => Locator.FindAll("colour:red"));
            Assert.That(exception.Message, Does.Contain("id:"), "the error should list the valid prefixes");

            // A typo'd prefix is the common case and must not degrade into a name lookup.
            Assert.Throws<TestFailureException>(() => Locator.FindAll("txt:Play"));
        }

        [Test]
        public void PointSelectorsAreRejectedByObjectLookups()
        {
            var exception = Assert.Throws<TestFailureException>(() => Locator.FindAll("pos:0.5,0.5"));
            Assert.That(exception.Message, Does.Contain("screen point"));
        }

        [Test]
        public void NamesContainingAColonStillResolve()
        {
            var target = Make("Level 1: The Beginning");
            Assert.That(Locator.Find("#Level 1: The Beginning"), Is.EqualTo(target));
        }

        [Test]
        public void SuggestsStableSelectorsForRecording()
        {
            var button = Make("SuggestMe");
            button.AddComponent<RectTransform>();
            TestId.Assign(button, "suggest_id");

            var suggestions = Locator.SuggestSelectorsFor(button);
            Assert.That(suggestions[0], Is.EqualTo("id:suggest_id"), "a TestId always wins");
        }
    }
}
