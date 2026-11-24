/**
 * pi-hooks.ts — 只在 pi 載入（本機 .pi/settings.json 的 extensions 指到這裡）。
 * 用 claude-bridge 跑 pi 時，pi-claude-bridge 不讀專案的 .claude/settings.json，Claude Code 也不會
 * 依路徑載入 .claude/rules。這支把 Claude 端的專案設定接到 pi，判斷仍交給同一份來源檔：
 *
 *   Claude 端                                   pi 事件                      做法
 *   SessionStart  sync-codex-mirror.mjs          session_start（startup）     同一支腳本，fail-open
 *   PostToolUse   Edit|Write  hardcode-reminder  tool_result（edit、write）   轉成 Claude 的輸入格式交給同一支腳本，提醒附在工具結果後面
 *   .claude/rules、~/.claude/rules 的 paths:     tool_result（read、edit、write） 路徑第一次命中時，把規則全文附在工具結果後面（每 session 一次）
 *   ~/.claude/rules 沒有 paths: 的規則           before_agent_start           寫進 system prompt 區段（內容固定，不破壞 bridge 快取）
 *   專案條件式規則清單                           before_agent_start           同 codex-rules.mjs：列出「動到哪些路徑前必讀」
 *
 * ~/.claude/CLAUDE.md 本體已由 plugharbor 產生的 ~/.pi/agent/APPEND_SYSTEM.md 載入，這裡不重複。
 */
import { spawn } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

export const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");

export interface Rule {
  label: string; // 顯示用路徑，如 .claude/rules/backend.md
  globs: string[]; // 空陣列＝無條件規則
  body: string;
}

function split(raw: string): { meta: string; body: string } {
  const text = raw.replace(/\r\n/g, "\n");
  if (!text.startsWith("---\n")) return { meta: "", body: text };
  const end = text.indexOf("\n---\n", 4);
  if (end === -1) return { meta: "", body: text };
  return { meta: text.slice(4, end), body: text.slice(end + 5) };
}

export function loadRules(dir: string, label: string): Rule[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((n) => n.endsWith(".md"))
    .sort()
    .map((name) => {
      const { meta, body } = split(readFileSync(join(dir, name), "utf8"));
      const globs = /^paths:/m.test(meta)
        ? (meta.match(/^\s*-\s*"?([^"\n#]+?)"?\s*$/gm) ?? []).map((l) => l.replace(/^\s*-\s*"?|"?\s*$/g, ""))
        : [];
      return { label: `${label}/${name}`, globs, body: body.trim() };
    });
}

/** gitignore 風格的最小 glob：`**` 跨目錄、`*` 不跨 `/`、`?` 單一字元。 */
export function globToRegExp(glob: string): RegExp {
  let re = "";
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === "*" && glob[i + 1] === "*") {
      i++;
      if (glob[i + 1] === "/") {
        i++;
        re += "(?:.*/)?";
      } else re += ".*";
    } else if (c === "*") re += "[^/]*";
    else if (c === "?") re += "[^/]";
    else re += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
  }
  return new RegExp(`^${re}$`);
}

export function matchingRules(rules: Rule[], file: string, root = ROOT): Rule[] {
  const abs = isAbsolute(file) ? file : resolve(root, file);
  const rel = relative(root, abs).replace(/\\/g, "/");
  if (rel.startsWith("..")) return [];
  return rules.filter((r) => r.globs.some((g) => globToRegExp(g).test(rel)));
}

export function systemSection(projectRules: Rule[], userRules: Rule[]): string {
  const out = ["以下是 Claude Code 會自動載入、pi 預設不會的規則（由 .claude/scripts/pi-hooks.ts 補上），與 AGENTS.md 同級。"];
  for (const r of userRules.filter((r) => r.globs.length === 0)) out.push(`\n## ${r.label}\n\n${r.body}`);
  const scoped = [...projectRules, ...userRules].filter((r) => r.globs.length > 0);
  if (scoped.length > 0) {
    out.push(
      "\n## 條件式規則\n\n用 read／edit／write 碰到符合的路徑時，規則全文會自動附在工具結果後面；用 bash 直接改檔不會觸發，動手前自己讀。\n\n" +
        scoped.map((r) => `- \`${r.label}\` — ${r.globs.join("、")}`).join("\n"),
    );
  }
  return out.join("\n");
}

export interface RunResult {
  code: number | null;
  stdout: string;
  error?: string;
}

export function runNode(args: string[], input: string, timeoutMs: number): Promise<RunResult> {
  return new Promise((done) => {
    let stdout = "";
    let settled = false;
    const finish = (r: RunResult) => {
      if (!settled) {
        settled = true;
        done(r);
      }
    };
    const child = spawn(process.execPath, args, { stdio: ["pipe", "pipe", "ignore"], cwd: ROOT, windowsHide: true });
    const timer = setTimeout(() => {
      child.kill();
      finish({ code: null, stdout, error: "timeout" });
    }, timeoutMs);
    child.stdout.setEncoding("utf8").on("data", (c: string) => (stdout += c));
    child.on("error", (e) => {
      clearTimeout(timer);
      finish({ code: null, stdout, error: String(e) });
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      finish({ code, stdout });
    });
    child.stdin.on("error", () => {});
    child.stdin.end(input, "utf8");
  });
}

/** pi 的 edit／write 輸入換成 hardcode-reminder.mjs 讀的 Claude 格式。 */
export function toClaudeInput(toolName: string, input: Record<string, unknown>): Record<string, unknown> | undefined {
  const path = typeof input.path === "string" ? input.path : undefined;
  if (!path) return undefined;
  const file_path = isAbsolute(path) ? path : resolve(ROOT, path);
  if (toolName === "write") return { file_path, content: input.content };
  if (toolName === "edit") {
    const edits = Array.isArray(input.edits) ? input.edits : [];
    return { file_path, edits: edits.map((e) => ({ new_string: (e as { newText?: unknown })?.newText })) };
  }
  return undefined;
}

export async function hardcodeReminder(toolName: string, input: Record<string, unknown>, runner = runNode): Promise<string | undefined> {
  const toolInput = toClaudeInput(toolName, input);
  if (!toolInput) return undefined;
  const payload = JSON.stringify({ hook_event_name: "PostToolUse", tool_name: toolName === "edit" ? "Edit" : "Write", tool_input: toolInput });
  const r = await runner([join(ROOT, ".claude", "hooks", "hardcode-reminder.mjs")], payload, 5_000);
  if (r.error || r.code !== 0 || !r.stdout.trim()) return undefined;
  try {
    const text = JSON.parse(r.stdout).hookSpecificOutput?.additionalContext;
    return typeof text === "string" && text ? text : undefined;
  } catch {
    return undefined;
  }
}

export default function (pi: ExtensionAPI) {
  const projectRules = loadRules(join(ROOT, ".claude", "rules"), ".claude/rules");
  const userRules = loadRules(join(homedir(), ".claude", "rules"), "~/.claude/rules");
  const section = systemSection(projectRules, userRules);
  const injected = new Set<string>();

  pi.on("session_start", async (event) => {
    injected.clear();
    if (event.reason !== "startup") return;
    const r = await runNode([join(ROOT, ".claude", "scripts", "sync-codex-mirror.mjs"), "--root", ROOT], "", 15_000);
    if (r.error || r.code !== 0) console.error(`[formwright] sync-codex-mirror 失敗：${r.error ?? `exit ${r.code}`}`);
  });

  pi.on("before_agent_start", async (event) => {
    event.systemPromptOptions.sections["formwright-claude-rules"] = section;
  });

  pi.on("tool_result", async (event) => {
    if (!["read", "edit", "write"].includes(event.toolName)) return undefined;
    const input = (event.input ?? {}) as Record<string, unknown>;
    const notes: string[] = [];
    if (typeof input.path === "string") {
      for (const r of matchingRules([...projectRules, ...userRules], input.path)) {
        if (injected.has(r.label)) continue;
        injected.add(r.label);
        notes.push(`[規則 ${r.label}，因路徑符合自動載入]\n${r.body}`);
      }
    }
    if (!event.isError && event.toolName !== "read") {
      const reminder = await hardcodeReminder(event.toolName, input);
      if (reminder) notes.push(reminder);
    }
    if (notes.length === 0) return undefined;
    return { content: [...event.content, { type: "text", text: `\n\n${notes.join("\n\n")}` }] };
  });
}
