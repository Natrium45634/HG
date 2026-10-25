using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 5, stage A (docs/DECISIONS_5.md §84): data and checks — fields of other versions, the summary of dependents
/// before a deletion (T03), renaming (T04).
/// </summary>
public class Part5DataTests
{
    private static string TempPack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hex_p5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(TestUtil.ContentDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(TestUtil.ContentDir, f);
            var to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    [Fact]
    public void UnknownFields_OnEveryLevel_AreKept_Reported_AndWrittenBack()
    {
        var dir = TempPack();
        try
        {
            // A file of a «newer version»: a new field of a passive, of its modifier, and of the rules.
            var path = Path.Combine(dir, "passives.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var item = root["items"]!.AsArray().First(i => i!["modifiers"]!.AsArray().Count > 0)!.AsObject();
            string id = item["id"]!.GetValue<string>();
            item["futureFlag"] = true;
            item["modifiers"]![0]!["futureCurve"] = new JsonObject { ["kind"] = "smooth", ["k"] = 2 };
            File.WriteAllText(path, root.ToJsonString());
            var rpath = Path.Combine(dir, "rules.json");
            var rroot = JsonNode.Parse(File.ReadAllText(rpath))!.AsObject();
            rroot["rules"]!["time"]!["futureTiming"] = 7;
            File.WriteAllText(rpath, rroot.ToJsonString());

            var res = ContentIO.Load(new FileSystemStorage(dir));
            Assert.False(res.HasErrors);
            var p = res.Db.Get<PassiveDef>(id)!;
            Assert.True(p.Extra!.ContainsKey("futureFlag"));
            Assert.True(p.Modifiers[0].Extra!.ContainsKey("futureCurve"));
            // The check names each field with its place.
            var warn = res.Issues.Single(i => i.Source == $"Passive:{id}" && i.Message.Contains("другой версии"));
            Assert.Contains($"{id}.futureFlag", warn.Message);
            Assert.Contains($"{id}.Modifiers[0].futureCurve", warn.Message);
            Assert.Contains(res.Issues, i => i.Source == "Правила" && i.Message.Contains("Time.futureTiming"));

            // Saved and loaded again: the fields are still there, unchanged.
            ContentIO.Save(res.Db, new FileSystemStorage(dir));
            var again = JsonNode.Parse(File.ReadAllText(path))!["items"]!.AsArray().First(i => i!["id"]!.GetValue<string>() == id)!;
            Assert.True(again["futureFlag"]!.GetValue<bool>());
            Assert.Equal("smooth", again["modifiers"]![0]!["futureCurve"]!["kind"]!.GetValue<string>());
            Assert.Equal(7, JsonNode.Parse(File.ReadAllText(rpath))!["rules"]!["time"]!["futureTiming"]!.GetValue<int>());
            // Drafts (clones) and the history keep them too.
            var clone = ContentJson.Clone(p);
            Assert.True(clone.Modifiers[0].Extra!.ContainsKey("futureCurve"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShippedContent_HasNoUnknownFields()
    {
        var db = TestUtil.Shipped();
        Assert.Empty(ContentCompat.Check(db));
    }
}
