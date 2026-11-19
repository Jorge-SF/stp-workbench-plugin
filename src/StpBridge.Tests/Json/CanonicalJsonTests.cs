using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Json
{
    public class CanonicalJsonTests
    {
        public static IEnumerable<object[]> GoldenPackages()
            => Directory.GetFiles(TestPaths.GoldenExpected, "*.apply.canonical.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

        /// <summary>Every case index.json records with an apply package has its canonical file, and all of them are tested.</summary>
        [Fact]
        public void EveryIndexedGoldenPackageIsTested()
        {
            var index = (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenExpected, "index.json")));
            var indexed = ((JArray)index["cases"])
                .Where(c => c["applySha256"]?.Type == JTokenType.String)
                .Select(c => $"{(string)c["fixture"]}.{(string)c["case"]}.apply.canonical.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var tested = GoldenPackages().Select(r => (string)r[0]).ToList();
            Assert.True(tested.Count >= 18, $"only {tested.Count} golden packages");
            Assert.Equal(indexed, tested);
            var normalized = ((JArray)TestPaths.PythonVectors["goldenNormalized"]).Select(v => (string)v["file"]);
            Assert.Equal(tested, normalized.OrderBy(f => f, StringComparer.Ordinal)); // regenerate TestData when goldens change
        }

        [Theory]
        [MemberData(nameof(GoldenPackages))]
        public void GoldenPackageBytesAndHashMatchPython(string fileName)
        {
            var path = Path.Combine(TestPaths.GoldenExpected, fileName);
            var raw = File.ReadAllBytes(path);
            var result = (JObject)WireJson.Parse(File.ReadAllBytes(path.Replace(".apply.canonical.json", ".json")));
            var recorded = (string)result["applyPackage"]["sha256"];

            // Generic canonicalization of the parsed tree.
            var canonical = CanonicalJson.ToBytes(WireJson.Parse(raw));
            Assert.Equal(raw, canonical);
            Assert.Equal(recorded, CanonicalJson.Sha256Hex(canonical));

            // Typed round trip: what the client sends equals what the Python server stores and hashes
            // (load_apply_package -> apply_package_to_json_value). That is the JS bytes, except that Python
            // re-normalizes some spellings, e.g. fractional seconds ".250" -> ".250000".
            var python = ((JArray)TestPaths.PythonVectors["goldenNormalized"]).Single(v => (string)v["file"] == fileName);
            var package = ApplyPackage.FromJson(WireJson.Parse(raw));
            Assert.Equal((string)python["canonical"], Encoding.UTF8.GetString(package.ToCanonicalBytes()));
            Assert.Equal((string)python["sha256"], package.ComputeSha256());
            Assert.Equal((bool)python["sameAsJs"], package.ComputeSha256() == recorded);
        }

        [Theory]
        [InlineData("2026-03-02T06:30:00.250")]
        [InlineData("2026-03-02T06:30:00.25")]
        [InlineData("2026-03-02T06:30:00.000001")]
        [InlineData("2026-03-02T06:30:00")]
        [InlineData("2026-03-02T06:30")]
        [InlineData("2026-03-02")]
        public void DateLikeStringsKeepTheirExactSpelling(string text)
        {
            var json = "{\"a\":[\"" + text + "\"],\"b\":{\"t\":\"" + text + "\"}}";
            Assert.Equal(json, CanonicalJson.ToText(WireJson.Parse(json)));
            Assert.Equal(JTokenType.String, WireJson.Parse(json)["b"]["t"].Type);
        }

        public static IEnumerable<object[]> CanonicalVectors()
            => ((JArray)TestPaths.PythonVectors["canonical"]).Select(v => new object[] { (string)v["input"], (string)v["canonical_b64"] });

        [Theory]
        [MemberData(nameof(CanonicalVectors))]
        public void EdgeCasesMatchPythonBytes(string input, string expectedBase64)
        {
            Assert.Equal(Convert.FromBase64String(expectedBase64), CanonicalJson.ToBytes(input));
        }

        [Fact]
        public void SortsKeysByCodePointNotUtf16()
        {
            // U+FFFF < U+1F600 in Python; UTF-16 ordinal order would put the surrogate pair first.
            var obj = new JObject { ["\U0001F600"] = 1, ["\uFFFF"] = 2, ["\uE000"] = 3, ["z"] = 4 };
            Assert.Equal("{\"z\":4,\"\uE000\":3,\"\uFFFF\":2,\"\U0001F600\":1}", CanonicalJson.ToText(obj));
        }

        [Fact]
        public void EscapesExactlyLikePython()
        {
            var value = new JValue("\"\\/\b\f\n\r\t\u0000\u001f\u007f\u00e9\u2028\U0001D11E");
            Assert.Equal("\"\\\"\\\\/\\b\\f\\n\\r\\t\\u0000\\u001f\u007f\u00e9\u2028\U0001D11E\"", CanonicalJson.ToText(value));
        }

        [Fact]
        public void NonAsciiIsRawUtf8()
        {
            Assert.Equal(new byte[] { 0x22, 0xC3, 0xB1, 0xF0, 0x9D, 0x84, 0x9E, 0x22 }, CanonicalJson.ToBytes(new JValue("ñ\U0001D11E")));
        }

        [Fact]
        public void LoneSurrogateIsRejectedLikePythonUtf8Encode()
        {
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes(new JValue("a\uD800b")));
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes("{\"s\":\"\\udc00\"}"));
        }

        [Fact]
        public void BigIntegersAreExact()
        {
            var big = BigInteger.Parse("-123456789012345678901234567890123456789");
            Assert.Equal("[-123456789012345678901234567890123456789,9223372036854775807]", CanonicalJson.ToText(new JArray(new JValue(big), new JValue(long.MaxValue))));
            Assert.Equal("[18446744073709551616]", CanonicalJson.ToText(WireJson.Parse("[18446744073709551616]")));
        }

        [Fact]
        public void NestedEmptyContainers()
        {
            Assert.Equal("{\"a\":{},\"b\":[],\"c\":[{}],\"d\":{\"e\":{\"f\":[]}}}", CanonicalJson.ToText(WireJson.Parse("{ \"d\": {\"e\": {\"f\": [ ]}}, \"c\": [{}], \"b\": [], \"a\": {} }")));
        }

        [Fact]
        public void RejectsValuesPythonCannotDumpOrWouldDumpDifferently()
        {
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes(new JValue(double.NaN)));
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes(new JValue(double.PositiveInfinity)));
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes(new JValue(1.5m)));
            Assert.Throws<WireFormatException>(() => CanonicalJson.ToBytes(new JValue(new DateTime(2026, 1, 1))));
            Assert.Throws<WireFormatException>(() => WireJson.Parse("[NaN]"));
            Assert.Throws<WireFormatException>(() => WireJson.Parse("{\"a\":1,\"a\":2}"));
            Assert.Throws<WireFormatException>(() => WireJson.Parse("{} []"));
        }

        [Fact]
        public void DateLikeStringsStayStrings()
        {
            var token = WireJson.Parse("{\"d\":\"2026-03-02T08:00:00\"}");
            Assert.Equal(JTokenType.String, token["d"].Type);
            Assert.Equal("{\"d\":\"2026-03-02T08:00:00\"}", CanonicalJson.ToText(token));
        }

        [Fact]
        public void Sha256IsLowercaseHex()
        {
            Assert.Equal("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes("{}")));
        }
    }

    public class PythonNumberAndTimeTests
    {
        [Fact]
        public void FloatReprMatchesPythonForAllVectors()
        {
            var failures = new List<string>();
            var vectors = (JArray)TestPaths.PythonVectors["floats"];
            Assert.True(vectors.Count > 4000);
            foreach (var vector in vectors)
            {
                var value = BitConverter.Int64BitsToDouble(long.Parse((string)vector["bits"]));
                var expected = (string)vector["repr"];
                var actual = PythonFloat.Repr(value);
                if (actual != expected) failures.Add($"{(string)vector["bits"]}: expected {expected}, got {actual}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        }

        [Fact]
        public void PythonFloatJsonTextRoundTripsThroughCanonicalJson()
        {
            // json.dumps(float) == repr(float); parsing that text in .NET and re-canonicalizing must give it back.
            var failures = new List<string>();
            foreach (var vector in (JArray)TestPaths.PythonVectors["floats"])
            {
                var json = (string)vector["json"];
                var text = Encoding.UTF8.GetString(CanonicalJson.ToBytes("[" + json + "]"));
                if (text != "[" + json + "]") failures.Add($"{json} -> {text}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        }

        [Fact]
        public void DecimalStrAndFixedMatchPython()
        {
            foreach (var vector in (JArray)TestPaths.PythonVectors["decimals"])
            {
                var text = (string)vector["text"];
                var str = (string)vector["str"];
                var ok = PyDecimal.TryParse(text, out var value);
                if (str == null)
                {
                    Assert.False(ok, $"'{text}' should be rejected");
                    continue;
                }
                Assert.True(ok, $"'{text}' should parse");
                Assert.Equal(str, value.ToString());
                Assert.Equal((string)vector["fixed"], value.ToFixedString());
            }
        }

        [Fact]
        public void DecimalEqualityIsNumericLikePython()
        {
            Assert.Equal(PyDecimal.Parse("40"), PyDecimal.Parse("4E+1"));
            Assert.Equal(PyDecimal.Parse("40").GetHashCode(), PyDecimal.Parse("40.000").GetHashCode());
            Assert.NotEqual(PyDecimal.Parse("40"), PyDecimal.Parse("40.0001"));
            Assert.True(PyDecimal.Parse("-1").CompareTo(PyDecimal.Parse("0.5")) < 0);
        }

        [Fact]
        public void DateTimesMatchPythonIsoformat()
        {
            foreach (var vector in (JArray)TestPaths.PythonVectors["datetimes"])
            {
                var text = (string)vector["text"];
                var iso = (string)vector["isoformat"];
                var ok = PyDateTime.TryParseDateTime(text, true, out var value);
                if (iso == null)
                {
                    Assert.False(ok, text);
                    continue;
                }
                Assert.True(ok, text);
                Assert.Equal(iso, PyDateTime.IsoFormat(value));
                Assert.Equal((string)vector["snapshot"], PyDateTime.SnapshotIso(value));
            }
        }

        [Theory]
        [InlineData("2026-03-02T08:00:00+00:00")]
        [InlineData("2026-03-02T08:00:00Z")]
        [InlineData("2026-03-02 08:00:00")]
        [InlineData("20260302T080000")]
        [InlineData("2026-03-02T24:00:00")]
        [InlineData("2026-03-02T08:00:00.1234567")]
        public void RejectsDateTimeFormsStpNeverUses(string text)
        {
            Assert.False(PyDateTime.TryParseDateTime(text, true, out _));
        }

        [Fact]
        public void DateOnlyOnlyWhenAllowed()
        {
            Assert.True(PyDateTime.TryParseDateTime("2026-03-02", true, out _));
            Assert.False(PyDateTime.TryParseDateTime("2026-03-02", false, out _));
        }
    }
}
