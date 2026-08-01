using NUnit.Framework;
using UnityEngine;
using Expr = Kobapps.GameTestKit.Scripting.Expression;

namespace Kobapps.GameTestKit.Tests
{
    public class ExpressionTests
    {
        [SetUp]
        public void SetUp()
        {
            GameTestBindings.Clear();
            GameTestBindings.BindValue("player.gold", () => 40);
            GameTestBindings.BindValue("player.name", () => "Ada");
            GameTestBindings.BindValue("player.alive", () => true);
        }

        [TearDown]
        public void TearDown() => GameTestBindings.Clear();

        [Test]
        public void ComparesNumbers()
        {
            Assert.That(Expr.EvaluateBool("player.gold == 40"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold >= 40"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold > 40"), Is.False);
            Assert.That(Expr.EvaluateBool("player.gold < 100"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold != 41"), Is.True);
        }

        [Test]
        public void ComparesStrings()
        {
            Assert.That(Expr.EvaluateBool("player.name == 'Ada'"), Is.True);
            Assert.That(Expr.EvaluateBool("player.name contains 'd'"), Is.True);
            Assert.That(Expr.EvaluateBool("player.name startsWith 'A'"), Is.True);
            Assert.That(Expr.EvaluateBool("player.name matches '^A.a$'"), Is.True);
        }

        [Test]
        public void CombinesWithBooleanOperators()
        {
            Assert.That(Expr.EvaluateBool("player.gold == 40 and player.alive"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold == 1 or player.name == 'Ada'"), Is.True);
            Assert.That(Expr.EvaluateBool("not (player.gold == 1)"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold == 1 || player.gold == 2"), Is.False);
        }

        [Test]
        public void DoesArithmeticIncludingNegativeLiterals()
        {
            Assert.That(Expr.EvaluateBool("player.gold - 10 == 30"), Is.True);
            Assert.That(Expr.EvaluateBool("player.gold * 2 == 80"), Is.True);
            Assert.That(Expr.EvaluateBool("(player.gold + 60) / 2 == 50"), Is.True);
            Assert.That(Expr.EvaluateBool("-5 < 0"), Is.True);
        }

        [Test]
        public void ReadsBuiltInValues()
        {
            Assert.That(Expr.EvaluateBool("screen.width > 0"), Is.True);
            Assert.That(Expr.EvaluateBool("timeScale >= 0"), Is.True);
        }

        [Test]
        public void UnknownValueExplainsWhatIsBound()
        {
            var exception = Assert.Throws<TestFailureException>(() => Expr.Evaluate("player.mana > 0"));

            Assert.That(exception.Message, Does.Contain("player.mana"));
            Assert.That(exception.Message, Does.Contain("player.gold"), "the message should list what is bound");
        }

        [Test]
        public void UnknownFunctionListsTheAvailableOnes()
        {
            var exception = Assert.Throws<TestFailureException>(() => Expr.Evaluate("wobble('#A')"));
            Assert.That(exception.Message, Does.Contain("visible"));
        }

        [Test]
        public void SceneFunctionsWorkAgainstLiveObjects()
        {
            var go = new GameObject("ExpressionProbe");
            try
            {
                Assert.That(Expr.EvaluateBool("exists('#ExpressionProbe')"), Is.True);
                Assert.That(Expr.EvaluateBool("count('#ExpressionProbe') == 1"), Is.True);
                Assert.That(Expr.EvaluateBool("exists('#NothingCalledThis')"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TryEvaluateReportsErrorsInsteadOfThrowing()
        {
            Assert.That(Expr.TryEvaluateBool("player.gold == 40", out var ok, out var error), Is.True);
            Assert.That(ok, Is.True);
            Assert.That(error, Is.Null);

            Assert.That(Expr.TryEvaluateBool("player.nope == 1", out _, out error), Is.False);
            Assert.That(error, Is.Not.Null);
        }
    }
}
