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
    /// 覆蓋率（§13、§18）：AppPackageData 以下每個 record 的屬性，自動比對或列在「不比對」清單；清單裡的名字必須是真的屬性，
    /// 免得改名後清單默默失效。新增的屬性預設會被比對，不會被基準換新吞掉。
    /// </summary>
    [Fact]
    public void NotComparedList_OnlyNamesRealProperties_OfPackageRecords()
    {
        var types = new Dictionary<string, Type>();
        void Walk(Type t)
        {
            if (t.Namespace is null || !t.Namespace.StartsWith("DynamicModules.Application.Dynamic") || !types.TryAdd(t.Name, t)) return;
            foreach (var p in UpgradeItems.ComparedProperties(t))
            {
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                foreach (var arg in pt.IsGenericType ? pt.GetGenericArguments() : [pt]) Walk(arg.IsGenericType ? arg.GetGenericArguments()[0] : arg);
            }
        }
        Walk(typeof(AppPackageData));
        types.Should().ContainKeys(nameof(TemplateField), nameof(AppApprovalFlow), nameof(AppContribution));
        foreach (var (type, names) in UpgradeItems.NotCompared)
        {
            types.Should().ContainKey(type, "不比對清單的 record 名稱要對得上");
            var props = UpgradeItems.ComparedProperties(types[type]).Select(p => p.Name).ToHashSet();
            names.Keys.Should().BeSubsetOf(props, type);
        }
    }
}
