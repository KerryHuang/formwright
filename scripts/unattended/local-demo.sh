#!/usr/bin/env bash
# 合併前本機走查用：以全新 Demo 資料啟動後端（5180，開 Demo 一鍵登入）與前端 dev（9180）。
# 用法：bash scripts/unattended/local-demo.sh [worktree 根目錄，預設目前 repo]
# DEMO_LOGIN=false 時關掉 Demo 一鍵登入（Demo 登入開著時 MCP 整個停用；MCP 實機驗收用，見 mcp1a 計畫判斷 25）。
# BACKUP_ROOT 預設 .tmp/local-e2e-backups（啟動前清空），走查備份分頁用；設成空字串＝不設 Backup:Root（走查「未設定」）；
# 設成唯讀目錄可走查「寫不進去」（故障注入，備份設計 §15）。
# 前景執行到被停止為止（請用背景執行）；就緒時印 READY。資料庫另放 local-e2e.db，不碰 poc.db。
set -u
ROOT="${1:-$(git rev-parse --show-toplevel)}"
cd "$ROOT" || exit 2

# 印出占用某埠的行程（PID 與工作目錄），讓人知道是哪個 worktree 的
show_owner() {
  for p in $(lsof -t -nP -iTCP:"$1" -sTCP:LISTEN 2>/dev/null); do
    echo "  PID ${p}：$(ps -o command= -p "$p" 2>/dev/null | cut -c1-120)" >&2
    echo "    cwd $(lsof -a -p "$p" -d cwd -Fn 2>/dev/null | sed -n 's/^n//p')" >&2
  done
}
for port in 5180 9180; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "port $port 已被占用（可能有另一份 POC 在跑），請先停掉再試" >&2
    show_owner "$port"
    exit 2
  fi
done
[ -d frontend/node_modules ] || (cd frontend && bun install) || exit 1

DB="$ROOT/backend/data/local-e2e.db"
rm -f "$DB" "$DB-shm" "$DB-wal"
export ConnectionStrings__Dynamic="Data Source=$DB" ASPNETCORE_ENVIRONMENT=Development
API=backend/src/DynamicModules.WebAPI

echo "產生 Demo 資料 ..."
dotnet run --project "$API" -- --generate-demo-data >/dev/null 2>&1 || { echo "Demo 資料產生失敗" >&2; exit 1; }

# 後端輸出寫到 local-e2e.log（走查要看 JSON log 時加環境變數 Logging__Console__FormatterName=json 啟動）
LOG="$ROOT/backend/data/local-e2e.log"
rm -f "$LOG"
BACKUP_ROOT="${BACKUP_ROOT-$ROOT/.tmp/local-e2e-backups}"
if [ -n "$BACKUP_ROOT" ]; then
  [ "$BACKUP_ROOT" = "$ROOT/.tmp/local-e2e-backups" ] && { chmod -R u+w "$BACKUP_ROOT" 2>/dev/null; rm -rf "$BACKUP_ROOT"; mkdir -p "$BACKUP_ROOT"; }
  export Backup__Root="$BACKUP_ROOT"
fi
Demo__LoginEnabled="${DEMO_LOGIN:-true}" dotnet run --no-build --project "$API" >"$LOG" 2>&1 &
BACKEND=$!
(cd frontend && exec bunx quasar dev >/dev/null 2>&1) &
FRONTEND=$!

cleanup() {
  trap - INT TERM HUP EXIT
  for pid in $BACKEND $FRONTEND; do
    pkill -TERM -P "$pid" 2>/dev/null
    kill -TERM "$pid" 2>/dev/null
  done
  wait 2>/dev/null
}
trap cleanup INT TERM HUP EXIT

for url in http://127.0.0.1:5180/api/health http://127.0.0.1:9180/; do
  i=0
  until curl -s -o /dev/null "$url"; do
    i=$((i + 1)); [ "$i" -ge 180 ] && { echo "逾時：$url 沒有回應" >&2; exit 1; }
    sleep 1
  done
done
# 啟動前檢查到綁埠之間可能被別人先占（#125）：READY 前確認兩個埠都是本腳本的子孫行程在聽，否則前端 proxy 會接到別人的後端
is_ours() {
  p=$1
  while [ -n "$p" ] && [ "$p" -gt 1 ]; do
    [ "$p" = "$$" ] && return 0
    p=$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')
  done
  return 1
}
for port in 5180 9180; do
  owners=$(lsof -t -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null)
  [ -n "$owners" ] || { echo "port $port 沒有行程在聽" >&2; exit 1; }
  for p in $owners; do
    is_ours "$p" || { echo "port $port 被別的行程占用，不是本腳本啟動的（後端或前端多半綁埠失敗，見 ${LOG}）" >&2; show_owner "$port"; exit 1; }
  done
done
echo "READY http://127.0.0.1:9180"
wait
