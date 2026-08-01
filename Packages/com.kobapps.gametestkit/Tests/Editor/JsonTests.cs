using Kobapps.GameTestKit.Scripting;
using NUnit.Framework;

namespace Kobapps.GameTestKit.Tests
{
    public class JsonTests
    {
        [Test]
        public void ParsesObjectsArraysAndScalars()
        {
            var json = JsonValue.Parse(@"{
                ""name"": ""a test"",
                ""count"": 3,
                ""ratio"": -0.25,
                ""ok"": true,
                ""missing"": null,
                ""steps"": [ { ""click"": ""#Play"" }, { ""wait"": 1.5 } ]
            }");

            Assert.That(json["name"].AsString(), Is.EqualTo("a test"));
            Assert.That(json["count"].AsInt(), Is.EqualTo(3));
            Assert.That(json["ratio"].AsFloat(), Is.EqualTo(-0.25f).Within(0.0001f));
            Assert.That(json["ok"].AsBool(), Is.True);
            Assert.That(json["missing"].IsNull, Is.True);
            Assert.That(json["steps"].Count, Is.EqualTo(2));
            Assert.That(json["steps"][0]["click"].AsString(), Is.EqualTo("#Play"));
        }

        [Test]
        public void MissingKeysYieldNullInsteadOfThrowing()
        {
            var json = JsonValue.Parse("{}");

            Assert.That(json["nope"].IsNull, Is.True);
            Assert.That(json["nope"]["deeper"].IsNull, Is.True);
            Assert.That(json["nope"].AsString("fallback"), Is.EqualTo("fallback"));
            Assert.That(json[7].IsNull, Is.True);
        }

        [Test]
        public void ToleratesCommentsAndTrailingCommas()
        {
            var json = JsonValue.Parse(@"{
                // a line comment
                ""a"": 1, /* and a block one */
                ""b"": [1, 2, 3,],
            }");

            Assert.That(json["a"].AsInt(), Is.EqualTo(1));
            Assert.That(json["b"].Count, Is.EqualTo(3));
        }

        [Test]
        public void ReportsLineAndColumnOnSyntaxErrors()
        {
            var exception = Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\n  \"a\": ,\n}"));

            Assert.That(exception.Line, Is.EqualTo(2));
            Assert.That(exception.Message, Does.Contain("line 2"));
        }

        [Test]
        public void RoundTripsThroughText()
        {
            var original = JsonValue.NewObject()
                .Set("name", "quote \" and \\ backslash")
                .Set("nested", JsonValue.NewObject().Set("value", 42))
                .Set("flag", false);

            var reparsed = JsonValue.Parse(original.ToJson());

            Assert.That(reparsed["name"].AsString(), Is.EqualTo("quote \" and \\ backslash"));
            Assert.That(reparsed["nested"]["value"].AsInt(), Is.EqualTo(42));
            Assert.That(reparsed["flag"].AsBool(), Is.False);
        }

        [Test]
        public void WritesIntegersWithoutDecimalNoise()
        {
            Assert.That(JsonValue.New(40d).ToJson(false), Is.EqualTo("40"));
            Assert.That(JsonValue.New(0.5d).ToJson(false), Is.EqualTo("0.5"));
        }
    }
}
