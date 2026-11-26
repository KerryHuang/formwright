using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using FluentAssertions;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.ApplicationTests.Dynamic.Apps;

/// <summary>分類引擎（套件更新設計 §6.3 兩張表的每一格、多語逐語系、沒有基準）與覆蓋率（§18）、版號（§7.2）。純函式。</summary>
public class UpgradeClassifierTests
{
    private static UpgradeItem Item(params (string Key, string? Value)[] props) =>
        new("field:lead.source", "fields", "lead", null,
            props.ToDictionary(p => p.Key, p => p.Value is null ? null : (JsonNode?)JsonValue.Create(p.Value)));

    [Theory]
    [InlineData("a", "a", "a", null)]
    [InlineData("a", "b", "a", NewOnly)]
    [InlineData("a", "a", "c", CustomerOnly)]
    [InlineData("a", "b", "b", null)]
    [InlineData("a", "b", "c", Both)]
    public void Property_FollowsTheThreeWayTable(string b, string n, string c, string? expected) =>
        Property(JsonValue.Create(b), JsonValue.Create(n), JsonValue.Create(c)).Should().Be(expected);

    [Fact]
    public void ExistenceTable_EachRow()
    {
        var x = Item(("label|zh-TW", "來源"));
        Classify(null, x, null, true, false)!.Should().Match<Result>(r => r.Category == Added && r.DefaultSelected && r.Applicable);
        Classify(x, x, null, true, false)!.Should().Match<Result>(r => r.Category == CustomerDeleted && !r.DefaultSelected);
        Classify(x, null, x, true, false)!.Should().Match<Result>(r => r.Category == RemovedInNew && !r.Applicable);
        Classify(x, null, null, true, false).Should().BeNull("兩邊都刪不列");
        Classify(null, x, x, true, false).Should().BeNull("新版新增、客戶已有同樣的：不列");
        Classify(null, x, Item(("label|zh-TW", "別的")), true, false)!.Category.Should().Be(Both);
        Classify(x, x, null, true, true)!.Category.Should().Be(ExcludedAtOpen);
    }

    [Fact]
    public void PerLocale_NewAddsEnglish_CustomerAddsJapanese_IsNewOnly_U9()
    {
        var b = Item(("label|zh-TW", "來源"));
        var n = Item(("label|zh-TW", "來源"), ("label|en-US", "Source"));
        var c = Item(("label|zh-TW", "來源"), ("label|ja", "ソース"));
        var r = Classify(b, n, c, true, false)!;
        r.Category.Should().Be(NewOnly);
        r.DefaultSelected.Should().BeTrue();
        r.Properties.Select(p => (p.Locale, p.Result)).Should().BeEquivalentTo([("en-US", NewOnly), ("ja", CustomerOnly)]);
    }

    [Fact]
    public void DifferentProperties_DoNotConflict_U10_AndBothKeepsAllThreeValues_U8()
    {
        var b = Item(("label|zh-TW", "線索來源"), ("config.maxLength", "10"));
        Classify(b, Item(("label|zh-TW", "線索來源"), ("config.maxLength", "20")), Item(("label|zh-TW", "名單來源"), ("config.maxLength", "10")), true, false)!
            .Category.Should().Be(NewOnly);
        var both = Classify(b, Item(("label|zh-TW", "來源"), ("config.maxLength", "10")), Item(("label|zh-TW", "名單來源"), ("config.maxLength", "10")), true, false)!;
        both.Category.Should().Be(Both);
        both.DefaultSelected.Should().BeFalse();
        both.Properties.Single().Should().Match<UpgradeProperty>(p =>
            p.Base!.ToString() == "線索來源" && p.New!.ToString() == "來源" && p.Current!.ToString() == "名單來源");
        Classify(b, b, Item(("label|zh-TW", "名單來源"), ("config.maxLength", "10")), true, false)!
            .Should().Match<Result>(r => r.Category == CustomerOnly && !r.Applicable, "U7");
    }

    [Fact]
    public void NoBaseline_EverythingDifferentIsNoBaseline_AndNotSelected_U17()
    {
        var n = Item(("label|zh-TW", "來源"));
        Classify(null, n, null, false, false)!.Should().Match<Result>(r => r.Category == NoBaseline && !r.DefaultSelected);
        Classify(n, n, Item(("label|zh-TW", "別的")), false, false)!.Should().Match<Result>(r => r.Category == NoBaseline && !r.DefaultSelected);
        Classify(null, n, n, false, false).Should().BeNull();
    }

    [Fact]
    public void Normalize_TreatsNullMissingAndEmptyAsTheSame()
    {
        UpgradeItems.Normalize(JsonNode.Parse("""{"a":null,"b":[],"c":{}}""")).Should().BeNull();
        UpgradeItems.Same(UpgradeItems.Normalize(JsonNode.Parse("""{"x":1,"y":2}""")), UpgradeItems.Normalize(JsonNode.Parse("""{"y":2,"x":1}"""))).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.3.0", "1.3.0", 0)]
    [InlineData("0.9.0", "1.0.0", -1)]
    public void Version_ComparesNumerically(string a, string b, int sign) => Math.Sign(PackageVersion.Compare(a, b)).Should().Be(sign);

    /// <summary>
    /// 覆蓋率（§13、§18，審查 M2）：從 AppPackageData 沿「不比對」的屬性往下走（比對的屬性整份序列化，底下的 record 一起比），
    /// 走到的每個 record 必須是整份比對（AutoCompared）或手工比對（HandCompared）其中之一；手工比對的 record 每個公開屬性
    /// 都要在手工比對清單或不比對清單裡。新增屬性或 section 沒歸類就失敗，不會被基準換新默默吞掉。清單裡的名字也必須是真的屬性。
    /// </summary>
    [Fact]
    public void EveryPackageProperty_IsCompared_OrListedAsNotCompared_BothDirections()
    {
        var (reached, problems) = Coverage(typeof(AppPackageData), InPackage);

        problems.Should().BeEmpty();
        reached.Should().ContainKeys(nameof(TemplateField), nameof(AppApprovalFlow), nameof(AppContribution), nameof(TemplatePermission));
        foreach (var name in UpgradeItems.NotCompared.Keys.Concat(UpgradeItems.HandCompared.Keys).Concat(UpgradeItems.AutoCompared))
            reached.Should().ContainKey(name, "清單裡的 record 名稱要對得上");
        foreach (var (type, names) in UpgradeItems.NotCompared)
            names.Keys.Should().BeSubsetOf(UpgradeItems.ComparedProperties(reached[type]).Select(p => p.Name), type);
        foreach (var (type, names) in UpgradeItems.HandCompared)
            names.Should().BeSubsetOf(UpgradeItems.ComparedProperties(reached[type]).Select(p => p.Name), type);
    }

    /// <summary>覆蓋率測試自己會擋：手工比對的 record 多一個沒歸類的屬性就要紅（i3c 的 excludeFromAi 已歸類；這裡模擬之後再加一個沒歸類的屬性，§13）。</summary>
    [Fact]
    public void Coverage_FlagsAnUnlistedProperty_OnAHandComparedRecord()
    {
        var (_, problems) = Coverage(typeof(Fake.TemplateField), t => t == typeof(Fake.TemplateField) || InPackage(t));
        problems.Should().Equal("TemplateField.FutureFlag 沒有歸類");
    }

    private static bool InPackage(Type t) => t.Namespace?.StartsWith("DynamicModules.Application.Dynamic") == true;

    private static (Dictionary<string, Type> Reached, List<string> Problems) Coverage(Type root, Func<Type, bool> inScope)
    {
        var reached = new Dictionary<string, Type>();
        var problems = new List<string>();
        void Walk(Type t)
        {
            if (!t.IsClass || !inScope(t) || !reached.TryAdd(t.Name, t)) return;
            var auto = UpgradeItems.AutoCompared.Contains(t.Name);
            var hand = UpgradeItems.HandCompared.GetValueOrDefault(t.Name);
            if (auto == (hand is not null)) { problems.Add($"{t.Name} 必須恰好屬於 AutoCompared 或 HandCompared 其中之一"); return; }
            var skip = UpgradeItems.NotCompared.GetValueOrDefault(t.Name) ?? new Dictionary<string, string>();
            foreach (var p in UpgradeItems.ComparedProperties(t))
            {
                if (hand is not null && !hand.Contains(p.Name) && !skip.ContainsKey(p.Name)) problems.Add($"{t.Name}.{p.Name} 沒有歸類");
                if (hand is not null && hand.Contains(p.Name) && skip.ContainsKey(p.Name)) problems.Add($"{t.Name}.{p.Name} 同時在兩份清單");
                if (!skip.ContainsKey(p.Name)) continue;
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                foreach (var arg in pt.IsGenericType ? pt.GetGenericArguments() : [pt]) Walk(arg.IsGenericType ? arg.GetGenericArguments()[0] : arg);
            }
        }
        Walk(root);
        return (reached, problems);
    }

    private static class Fake
    {
        public sealed record TemplateField(string Code, DynamicModules.Domain.Entities.Dynamic.FieldType Type, DynamicModules.Domain.ValueObjects.I18nText Label,
            JsonElement Config, List<TemplateOption> Options, bool? Active, bool? ExcludeFromAi, bool FutureFlag);
    }
}
