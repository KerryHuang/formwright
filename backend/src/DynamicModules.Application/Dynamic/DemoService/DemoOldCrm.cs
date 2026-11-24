using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 的「較舊版本」CRM（套件更新設計 §11.1，T15）：從目錄版原文在 JSON 文字層推導，不另存舊套件檔（舊檔會隨目錄版演進而過期）。
/// 產出的文字就是 Demo 開啟的原文，也是基準。拿掉與改掉的都避開 Demo 情境與測試會用到的、被其他定義引用的代號：
/// <list type="bullet">
/// <item>拿掉「活動」類型的「線上」選項（升級預覽是新版新增）；</item>
/// <item>「潛在客戶」的「公司」泰文標籤換成舊譯名（只有新版改）；</item>
/// <item>「互動紀錄」的「主旨」繁中標籤換成舊名稱，開啟後再由 <see cref="DemoStructure"/> 以客戶身分改成第三個值（兩邊都改）；</item>
/// <item>設計案「需求確認 → 設計中」少「主管」群組（新版多給群組，示範 transitionGroups 只列出、不能勾）：挑有兩個群組的轉移，
/// 拿掉後仍有「業務」，劇本由業務推這條轉移不受影響（§9.6.5、複核 R6）；</item>
/// <item>設計案簽核流程第一關的繁中名稱換成舊名（只有新版改，Q27 下仍可勾；設計案劇本留一張簽核中的單，示範 inFlightApprovals）。</item>
/// </list>
/// 儀表板引用了 CRM 的兩份報表，所以不拿掉報表（§11.1 最後一點）。
/// </summary>
public static class DemoOldCrm
{
    public const string OldCompanyThai = "ชื่อบริษัท", OldSubject = "主題", CustomerSubject = "互動主旨", RemovedOption = "campaign.online";
    public const string NarrowedTransition = "transition:design-case:requirement>designing", RemovedGroup = "manager";
    public const string RenamedFlow = "crm-design-case-review", OldStageName = "業務主管審核";

    public static string Derive(string catalogText)
    {
        var root = JsonNode.Parse(catalogText)!.AsObject();
        var manifest = root["manifest"]!.AsObject();
        manifest["version"] = Older(manifest["version"]!.GetValue<string>());
        foreach (var module in root["modules"]!.AsArray())
            foreach (var field in module!["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()))
            {
                var name = $"{module["code"]}.{field!["code"]}";
                if (name == "lead.company") field["label"]!["th"] = OldCompanyThai;
                if (name == "activity.subject") field["label"]!["zh-TW"] = OldSubject;
                if (name == "campaign.kind" && field["options"] is JsonArray options)
                    options.Remove(options.First(o => $"campaign.{o!["code"]}" == RemovedOption));
            }
        foreach (var wf in root["workflows"]!.AsArray())
            foreach (var t in wf!["transitions"]!.AsArray())
                if ($"transition:{wf["module"]}:{t!["from"]}>{t["to"]}" == NarrowedTransition && t["groups"] is JsonArray groups)
                    groups.Remove(groups.First(g => g!.GetValue<string>() == RemovedGroup));
        foreach (var flow in root["approvalFlows"]!.AsArray().Where(f => (string?)f!["code"] == RenamedFlow))
            flow!["stages"]![0]!["name"]!["zh-TW"] = OldStageName;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>比目錄版小一版：修訂號減一，修訂號是 0 時次版號減一、修訂號 99。</summary>
    public static string Older(string version)
    {
        var p = version.Split('.').Select(int.Parse).ToArray();
        return p[2] > 0 ? $"{p[0]}.{p[1]}.{p[2] - 1}" : p[1] > 0 ? $"{p[0]}.{p[1] - 1}.99" : $"{p[0] - 1}.99.99";
    }
}
