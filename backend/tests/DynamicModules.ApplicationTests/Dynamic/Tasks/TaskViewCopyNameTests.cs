using DynamicModules.Application.Dynamic.TaskViewService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 看板、甘特另存的名稱（名稱多語設計 §7.3；審查 🟡2）：後綴可被語系包覆寫，太長時不能讓另存 500。
/// 結果不超過名稱上限、原名稱至少留一半、不切斷代理字元對。
/// </summary>
public class TaskViewCopyNameTests
{
    private const int Max = TaskViewRules.MaxName;

    [Fact]
    public void FitsWithinLimit_SuffixAppended()
    {
        TaskViewRules.CopyName("本週出貨", "（副本）").Should().Be("本週出貨（副本）");
        TaskViewRules.CopyName(new string('名', Max - 4), "（副本）").Should().HaveLength(Max).And.EndWith("（副本）");
    }

    [Fact]
    public void LongName_CutsNameKeepsSuffix()
    {
        var copy = TaskViewRules.CopyName(new string('名', Max), " (copy)");
        copy.Should().HaveLength(Max).And.EndWith(" (copy)");
    }

    [Theory]
    [InlineData(Max - 1)]
    [InlineData(Max)]
    [InlineData(Max + 1)]
    [InlineData(300)]
    public void OverlongSuffix_CappedAtHalf_NameKeepsTheOtherHalf(int suffixLength)
    {
        var copy = TaskViewRules.CopyName(new string('名', Max), new string('副', suffixLength));
        copy.Should().HaveLength(Max);
        copy.Should().StartWith(new string('名', Max - TaskViewRules.MaxCopySuffix));
        copy.Should().EndWith(new string('副', TaskViewRules.MaxCopySuffix));
    }

    [Fact]
    public void ShortName_OverlongSuffix_KeepsWholeName()
    {
        TaskViewRules.CopyName("A", new string('副', 150)).Should().Be("A" + new string('副', TaskViewRules.MaxCopySuffix));
    }

    [Fact]
    public void CutNeverSplitsSurrogatePairs()
    {
        // 名稱與後綴都由 4 位元組字元（代理字元對）組成，截斷點剛好落在一對中間
        var name = string.Concat(Enumerable.Repeat("𠀀", Max));
        var suffix = "x" + string.Concat(Enumerable.Repeat("𠀀", Max));
        var copy = TaskViewRules.CopyName(name, suffix);
        copy.Length.Should().BeLessThanOrEqualTo(Max);
        for (var i = 0; i < copy.Length; i++)
            if (char.IsHighSurrogate(copy[i])) char.IsLowSurrogate(copy[i + 1]).Should().BeTrue($"位置 {i} 的高位代理字元後面要接低位");
            else if (char.IsLowSurrogate(copy[i])) char.IsHighSurrogate(copy[i - 1]).Should().BeTrue($"位置 {i} 的低位代理字元前面要有高位");
    }
}
