#!/usr/bin/env bash
# SQLite 壓測一條龍（安裝版設計 D-15、AC-11）：publish（Release）→ 產生 Demo 資料 → 每次跑前還原乾淨資料庫
# → 啟動站台 → k6 → 從站台 log 計數鎖錯誤 → 關站台。細節見 README.md。
# 用法：scripts/load/run-sqlite.sh [VUS] [DURATION]    例：scripts/load/run-sqlite.sh 5 1m
set -euo pipefail

VUS="${1:-50}"
DURATION="${2:-10m}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK:-$ROOT/.tmp/load-site}"          # .tmp/ 已被 git 忽略
APP="$WORK/app"; DATA="$WORK/data"
PORT_URL="http://127.0.0.1:5180"              # 後端沒設 PORT 時固定聽這裡
STAMP="$(date +%Y%m%d-%H%M%S)"
RUN="$WORK/runs/$STAMP-${VUS}u-${DURATION}"
mkdir -p "$RUN" "$DATA"

site_env() {
  export ConnectionStrings__Dynamic="Data Source=$DATA/poc.db"
  export ASPNETCORE_ENVIRONMENT=Production
  export Demo__LoginEnabled=true
  export Auth__ClientIpHeader=X-Forwarded-For         # 讓每個虛擬使用者帶自己的來源 IP，避開 Demo 登入的每 IP 節流
  export DataProtection__KeysDirectory="$DATA/keys"   # 非 Development 一定要設
  export Backup__Root="$WORK/backups"
}

if [ ! -f "$APP/DynamicModules.WebAPI.dll" ]; then
  dotnet publish "$ROOT/backend/src/DynamicModules.WebAPI/DynamicModules.WebAPI.csproj" -c Release -o "$APP"
fi

if [ ! -f "$DATA/poc.db.orig" ]; then
  rm -rf "$DATA/poc.db" "$DATA/attachments" "$DATA/keys"
  (cd "$APP" && site_env && dotnet DynamicModules.WebAPI.dll --generate-demo-data)   # 約 3 分鐘
  cp "$DATA/poc.db" "$DATA/poc.db.orig"
  rm -rf "$DATA/attachments.orig" && cp -R "$DATA/attachments" "$DATA/attachments.orig"
fi

# 每次跑前還原成剛產生完 Demo 資料的狀態，結果才可比較
rm -f "$DATA/poc.db" "$DATA/poc.db-wal" "$DATA/poc.db-shm"
cp "$DATA/poc.db.orig" "$DATA/poc.db"
rm -rf "$DATA/attachments" && cp -R "$DATA/attachments.orig" "$DATA/attachments"

(cd "$APP" && site_env && exec dotnet DynamicModules.WebAPI.dll) > "$RUN/server.log" 2>&1 &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true' EXIT

for _ in $(seq 1 60); do
  curl -fsS "$PORT_URL/api/health" >/dev/null 2>&1 && break
  sleep 1
done
curl -fsS "$PORT_URL/api/health" >/dev/null   # 起不來就在這裡失敗

echo "uptime（前）：$(uptime)" | tee "$RUN/uptime.txt"
set +e
k6 run -e BASE_URL="$PORT_URL" -e VUS="$VUS" -e DURATION="$DURATION" -e OUT="$RUN/summary.json" \
  "$ROOT/scripts/load/sqlite-50users.js" 2>&1 | tee "$RUN/k6.log"
K6_EXIT=${PIPESTATUS[0]}
set -e
echo "uptime（後）：$(uptime)" | tee -a "$RUN/uptime.txt"

kill "$SERVER_PID" 2>/dev/null || true
wait "$SERVER_PID" 2>/dev/null || true
trap - EXIT

# 站台 log 的鎖錯誤與未處理例外（用 grep -c；0 筆時 grep 回 1，所以不接 set -e）
set +e
LOCKS=$(grep -ciE 'database is locked|SQLITE_BUSY|SQLite Error 5' "$RUN/server.log")
ERRS=$(grep -c '"LogLevel":"Error"' "$RUN/server.log")
CRITS=$(grep -c '"LogLevel":"Critical"' "$RUN/server.log")
set -e
echo "站台 log：鎖錯誤 ${LOCKS} 行、Error ${ERRS} 行、Critical ${CRITS} 行" | tee "$RUN/server-log-counts.txt"
echo "k6 結束碼 ${K6_EXIT}（0＝k6 內建門檻全通過；99＝門檻未過）；輸出在 ${RUN}"
exit "$K6_EXIT"
