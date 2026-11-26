using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>
/// 套用前的重驗（語系包設計 §23.2）：預覽之後情況變了就回 null（呼叫端回 409），任何東西都還沒寫。
/// 審查 r1 🟡1（標題選項與系統預設語系要用交易內最新的值重算）、🟢3（路徑改綁到別的物件、文字又相同時，靠目標識別擋下）。
/// </summary>
public class DesignDataApplierTests
{
    /// <summary>直接回傳指定項目的目錄（不讀資料庫；建構參數用不到）。</summary>
    private sealed class FixedCatalog(params DesignTextItem[] items) : DesignTextCatalog(null!, null!, null!)
    {
        public override Task<IReadOnlyList<DesignTextItem>> BuildAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DesignTextItem>>(items);
    }

    private const string Path = "app:crm/module:order/option:open/label";

    private static DesignTextItem Option(string id = "101", bool titleOption = false)
    {
        var text = new I18nText([KeyValuePair.Create("zh-TW", "開立")]);
        return new DesignTextItem(Path, "option", "crm", 1, false, 200, [new DesignTextTarget("option", id, () => text, _ => { })]) { TitleOption = titleOption };
    }

    private static Task<DesignDataApplier.Prepared?> Prepare(DesignTextItem now, DesignDataPlanRow row, string systemDefault = "zh-TW") =>
        new DesignDataApplier(new FixedCatalog(now), null!, null!).PrepareAsync("ja", [row], false, systemDefault, CancellationToken.None);

    private static DesignDataPlanRow RowFor(DesignTextItem previewed) => new(Path, "オープン", previewed.Fingerprint(), false, previewed.TargetKey);

    [Fact]
    public async Task Unchanged_Prepared()
    {
        (await Prepare(Option(), RowFor(Option()))).Should().NotBeNull();
    }

    [Fact]
    public async Task BecameTitleOption_AfterPreview_Stale()
    {
        // 預覽時還不是標題樣板用到的選項；之後有人把欄位加進標題樣板，系統預設語系又剛好是目標語系
        (await Prepare(Option(titleOption: true), RowFor(Option()), systemDefault: "ja")).Should().BeNull();
        (await Prepare(Option(titleOption: true), RowFor(Option()), systemDefault: "zh-TW")).Should().NotBeNull("預設語系不是目標語系，標題不受影響");
    }

    [Fact]
    public async Task SystemDefaultChangedToTarget_AfterPreview_Stale()
    {
        // 預覽時預設語系是繁中（不擋）；之後改成日文
        (await Prepare(Option(titleOption: true), RowFor(Option(titleOption: true)), systemDefault: "ja")).Should().BeNull();
    }

    [Fact]
    public async Task PathRebound_SameText_Stale()
    {
        (await Prepare(Option(id: "202"), RowFor(Option(id: "101")))).Should().BeNull("同一路徑改綁到別的選項，文字相同、指紋一樣");
        (await Prepare(Option(), RowFor(Option()) with { Targets = null })).Should().BeNull("舊版預覽沒有目標識別，一律當不一致");
    }
}
