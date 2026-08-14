using Kobapps.GameTestKit.Scripting;
using NUnit.Framework;

namespace Kobapps.GameTestKit.Tests
{
    /// <summary>
    /// Categories: normalisation, the folder → category derivation, filtering, and the parser.
    /// </summary>
    /// <remarks>
    /// The derivation tests assume the default settings (<c>Assets/GameTests</c> as the only test
    /// folder), which is what <see cref="GameTesterSettings.Instance"/> falls back to when no settings
    /// asset exists — the state these tests run in.
    /// </remarks>
    public class CategoryTests
    {
        // ---------------------------------------------------------------- normalising

        [Test]
        public void NormalizeCleansSeparatorsAndBlankSegments()
        {
            Assert.That(TestCategory.Normalize(@"Shop\Checkout"), Is.EqualTo("Shop/Checkout"));
            Assert.That(TestCategory.Normalize("//Shop//Checkout//"), Is.EqualTo("Shop/Checkout"));
            Assert.That(TestCategory.Normalize("  Shop / Checkout  "), Is.EqualTo("Shop/Checkout"));
            Assert.That(TestCategory.Normalize("   "), Is.EqualTo(""));
            Assert.That(TestCategory.Normalize(null), Is.EqualTo(""));
        }

        [Test]
        public void LeafAndParentSplitThePath()
        {
            Assert.That(TestCategory.Leaf("Shop/Checkout"), Is.EqualTo("Checkout"));
            Assert.That(TestCategory.Leaf("Shop"), Is.EqualTo("Shop"));
            Assert.That(TestCategory.Leaf(""), Is.EqualTo(""));

            Assert.That(TestCategory.Parent("Shop/Checkout"), Is.EqualTo("Shop"));
            Assert.That(TestCategory.Parent("Shop"), Is.EqualTo(""));
        }

        [Test]
        public void SelfAndAncestorsWalksOutermostFirst()
        {
            Assert.That(TestCategory.SelfAndAncestors("Shop/Checkout/Coupons"),
                Is.EqualTo(new[] { "Shop", "Shop/Checkout", "Shop/Checkout/Coupons" }));

            Assert.That(TestCategory.SelfAndAncestors(""), Is.Empty);
        }

        [Test]
        public void DisplayNamesTheEmptyCategory()
        {
            Assert.That(TestCategory.Display(""), Is.EqualTo(TestCategory.UncategorizedLabel));
            Assert.That(TestCategory.Display("Shop"), Is.EqualTo("Shop"));
        }

        // ---------------------------------------------------------------- filtering

        [Test]
        public void IsWithinMatchesTheCategoryAndItsDescendants()
        {
            Assert.That(TestCategory.IsWithin("Shop", "Shop"), Is.True);
            Assert.That(TestCategory.IsWithin("Shop/Checkout", "Shop"), Is.True);
            Assert.That(TestCategory.IsWithin("shop/checkout", "SHOP"), Is.True, "case-insensitive");
        }

        [Test]
        public void IsWithinDoesNotMatchASiblingWithASharedPrefix()
        {
            Assert.That(TestCategory.IsWithin("Shopping", "Shop"), Is.False);
            Assert.That(TestCategory.IsWithin("Shop", "Shop/Checkout"), Is.False, "a parent is not in its child");
            Assert.That(TestCategory.IsWithin("", "Shop"), Is.False);
        }

        [Test]
        public void AnEmptyFilterMatchesEverything()
        {
            Assert.That(TestCategory.IsWithin("Shop/Checkout", ""), Is.True);
            Assert.That(TestCategory.IsWithin("", ""), Is.True);
        }

        // ---------------------------------------------------------------- from the folder

        /// <summary>The roots the default settings produce, spelled out so these tests need no asset.</summary>
        private static readonly string[] Roots =
        {
            "Assets/GameTests",
            "Assets/Resources/GameTests",
            "Resources/GameTests",
            "StreamingAssets/GameTests",
            "Assets/Resources",
            "StreamingAssets",
            "Assets",
        };

        [Test]
        public void TheFolderUnderTheTestRootBecomesTheCategory()
        {
            Assert.That(TestCategory.FromSourcePath("Assets/GameTests/Shop/buy.gametest.json", Roots),
                Is.EqualTo("Shop"));

            Assert.That(TestCategory.FromSourcePath("Assets/GameTests/Shop/Checkout/pay.gametest.json", Roots),
                Is.EqualTo("Shop/Checkout"));
        }

        [Test]
        public void ATestInTheRootHasNoCategory()
        {
            Assert.That(TestCategory.FromSourcePath("Assets/GameTests/smoke.gametest.json", Roots),
                Is.EqualTo(""));
        }

        [Test]
        public void BackSlashesInAPathAreAccepted()
        {
            Assert.That(TestCategory.FromSourcePath(@"Assets\GameTests\Shop\buy.gametest.json", Roots),
                Is.EqualTo("Shop"));
        }

        [Test]
        public void TheLongestMatchingRootWins()
        {
            // Assets, Assets/GameTests and Assets/Resources/GameTests all contain this path; only the
            // deepest one gives a category a person would recognise.
            Assert.That(TestCategory.FromSourcePath("Assets/Resources/GameTests/Shop/buy.gametest.json", Roots),
                Is.EqualTo("Shop"));
        }

        [Test]
        public void AnAbsolutePathIsMeasuredFromTheRootItContains()
        {
            Assert.That(TestCategory.FromSourcePath(
                    "C:/dev/MyGame/Assets/GameTests/Shop/buy.gametest.json", Roots),
                Is.EqualTo("Shop"), "StreamingAssets and file paths arrive absolute");

            Assert.That(TestCategory.FromSourcePath(
                    "C:/build/MyGame_Data/StreamingAssets/GameTests/Combat/boss.gametest.json", Roots),
                Is.EqualTo("Combat"));
        }

        [Test]
        public void ScriptsOutsideTheTestRootFallBackToTheirFolderUnderAssets()
        {
            Assert.That(TestCategory.FromSourcePath("Assets/Levels/Tests/boss.gametest.json", Roots),
                Is.EqualTo("Levels/Tests"));
        }

        [Test]
        public void AnAbsolutePathOutsideTheProjectHasNoCategory()
        {
            Assert.That(TestCategory.FromSourcePath("C:/Users/someone/Desktop/scratch.gametest.json", Roots),
                Is.EqualTo(""), "a category must never name somebody's home directory");
        }

        [Test]
        public void ARelativePathOutsideAnyRootKeepsItsFolderChain()
        {
            Assert.That(TestCategory.FromSourcePath("Tests/Smoke/boot.gametest.json", Roots),
                Is.EqualTo("Tests/Smoke"));
        }

        [Test]
        public void ARootFolderItselfIsUncategorized()
        {
            Assert.That(TestCategory.FromSourcePath("C:/dev/MyGame/Assets/GameTests/smoke.gametest.json", Roots),
                Is.EqualTo(""));
        }

        // ---------------------------------------------------------------- the script format

        [Test]
        public void ParsingDerivesTheCategoryFromTheSourcePath()
        {
            var test = TestScriptParser.ParseTest(
                @"{ ""steps"": [ { ""wait"": 1 } ] }",
                "Assets/GameTests/Shop/Checkout/pay.gametest.json");

            Assert.That(test.Category, Is.EqualTo("Shop/Checkout"));
            Assert.That(test.IsInCategory("Shop"), Is.True);
            Assert.That(test.CategoryLabel, Is.EqualTo("Shop/Checkout"));
        }

        [Test]
        public void AnExplicitCategoryOverridesTheFolder()
        {
            var test = TestScriptParser.ParseTest(
                @"{ ""category"": ""Onboarding/Tutorial"", ""steps"": [ { ""wait"": 1 } ] }",
                "Assets/GameTests/Shop/pay.gametest.json");

            Assert.That(test.Category, Is.EqualTo("Onboarding/Tutorial"));
        }

        [Test]
        public void ATestBuiltInCodeHasNoCategoryUntilOneIsSet()
        {
            var test = new GameTest("Written in C#");

            Assert.That(test.Category, Is.EqualTo(""));
            Assert.That(test.CategoryLabel, Is.EqualTo(TestCategory.UncategorizedLabel));
            Assert.That(test.FullName, Is.EqualTo("Written in C#"));
        }

        [Test]
        public void FullNameCombinesCategoryAndName()
        {
            var test = new GameTest("Buy a sword") { Category = "Shop" };
            Assert.That(test.FullName, Is.EqualTo("Shop ▸ Buy a sword"));
        }

        [Test]
        public void ASuiteCanFilterByCategory()
        {
            var suite = TestScriptParser.ParseSuite(@"{
                ""name"": ""Shop only"",
                ""categories"": [""Shop"", ""  ""],
                ""excludeCategories"": [""Shop/Experimental""]
            }", "shop.gamesuite.json");

            Assert.That(suite.Options.Categories, Is.EqualTo(new[] { "Shop" }),
                "blank entries are dropped");
            Assert.That(suite.Options.ExcludeCategories, Is.EqualTo(new[] { "Shop/Experimental" }));
        }

        // ---------------------------------------------------------------- run filters

        [Test]
        public void RunOptionsIncludeACategoryAndItsDescendants()
        {
            var options = new RunOptions();
            options.Categories.Add("Shop");

            Assert.That(options.Matches(In("Shop")), Is.True);
            Assert.That(options.Matches(In("Shop/Checkout")), Is.True);
            Assert.That(options.Matches(In("Onboarding")), Is.False);
            Assert.That(options.Matches(In("")), Is.False);
        }

        [Test]
        public void ExcludedCategoriesWinOverIncludedOnes()
        {
            var options = new RunOptions();
            options.Categories.Add("Shop");
            options.ExcludeCategories.Add("Shop/Experimental");

            Assert.That(options.Matches(In("Shop/Checkout")), Is.True);
            Assert.That(options.Matches(In("Shop/Experimental")), Is.False);
            Assert.That(options.Matches(In("Shop/Experimental/Deep")), Is.False,
                "excluding a category excludes what is nested inside it");
        }

        [Test]
        public void CategoriesAndTagsBothHaveToMatch()
        {
            var options = new RunOptions();
            options.Categories.Add("Shop");
            options.Tags.Add("smoke");

            var tagged = In("Shop");
            tagged.Tags.Add("smoke");

            var untagged = In("Shop");

            var elsewhere = In("Onboarding");
            elsewhere.Tags.Add("smoke");

            Assert.That(options.Matches(tagged), Is.True);
            Assert.That(options.Matches(untagged), Is.False, "in the category but not tagged");
            Assert.That(options.Matches(elsewhere), Is.False, "tagged but in the wrong category");
        }

        [Test]
        public void NoCategoryFilterLeavesEveryTestMatching()
        {
            var options = new RunOptions();

            Assert.That(options.Matches(In("")), Is.True);
            Assert.That(options.Matches(In("Shop/Checkout")), Is.True);
        }

        [Test]
        public void CloneCarriesTheCategoryFilters()
        {
            var options = new RunOptions();
            options.Categories.Add("Shop");
            options.ExcludeCategories.Add("Shop/Experimental");

            var clone = options.Clone();
            clone.Categories.Add("Onboarding");

            Assert.That(clone.ExcludeCategories, Is.EqualTo(new[] { "Shop/Experimental" }));
            Assert.That(options.Categories, Is.EqualTo(new[] { "Shop" }),
                "the clone must not share the original's lists");
        }

        // ---------------------------------------------------------------- discovery

        [Test]
        public void DiscoverCategoriesListsEveryLevelOfTheTree()
        {
            var tests = new[] { In("Shop/Checkout"), In("Onboarding"), In("") };

            Assert.That(GameTestCatalog.DiscoverCategories(tests),
                Is.EqualTo(new[] { "Onboarding", "Shop", "Shop/Checkout" }),
                "parents appear even when no test sits directly in them");
        }

        private static GameTest In(string category) =>
            new GameTest("test") { Category = category };
    }
}
