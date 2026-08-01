using NUnit.Framework;

namespace Kobapps.GameTestKit.Tests
{
    public class LoopWatchTests
    {
        private static LoopWatch Watch(int window = 6, int maxStates = 3, int visits = 3) =>
            new LoopWatch(window, maxStates, visits);

        private static void Feed(LoopWatch watch, params string[] states)
        {
            foreach (var state in states) watch.Record(state);
        }

        [Test]
        public void SpotsTwoScreensAlternating()
        {
            var watch = Watch();
            Feed(watch, "home", "store", "home", "store", "home", "store");

            Assert.That(watch.IsGoingInCircles(out var screens, out var visits), Is.True);
            Assert.That(screens, Is.EqualTo(2));
            Assert.That(visits, Is.EqualTo(3));
        }

        [Test]
        public void SpotsALongerCycle()
        {
            var watch = Watch(window: 9);
            Feed(watch, "home", "store", "offer", "home", "store", "offer", "home", "store", "offer");

            Assert.That(watch.IsGoingInCircles(out var screens, out var visits), Is.True);
            Assert.That(screens, Is.EqualTo(3));
            Assert.That(visits, Is.EqualTo(3));
        }

        [Test]
        public void WaitsForAFullWindow()
        {
            var watch = Watch();
            Feed(watch, "home", "store", "home", "store");

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void StandingStillIsNotACircle()
        {
            // One state forever is a dead end, which the driver's barren-action counter already reports —
            // and reports better, since it also knows whether the game is still moving on its own.
            var watch = Watch();
            Feed(watch, "home", "home", "home", "home", "home", "home");

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void ExploringIsNotACircle()
        {
            var watch = Watch();
            Feed(watch, "home", "store", "map", "level", "pause", "level");

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void RevisitingWhileStillMovingOnIsNotACircle()
        {
            // Back to the home screen twice, but the window keeps reaching new places.
            var watch = Watch(window: 6, maxStates: 3, visits: 3);
            Feed(watch, "home", "store", "home", "map", "home", "level");

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void ForgetsOnDemand()
        {
            var watch = Watch();
            Feed(watch, "home", "store", "home", "store", "home", "store");
            watch.Forget();

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void CanBeTurnedOff()
        {
            var watch = Watch(visits: 0);
            Feed(watch, "home", "store", "home", "store", "home", "store");

            Assert.That(watch.Enabled, Is.False);
            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }

        [Test]
        public void OnlyTheWindowCounts()
        {
            // A run that circled early and then found its way out is not reported.
            var watch = Watch();
            Feed(watch, "home", "store", "home", "store", "home", "store");
            Feed(watch, "map", "level", "win", "map", "level", "boss");

            Assert.That(watch.IsGoingInCircles(out _, out _), Is.False);
        }
    }
}
