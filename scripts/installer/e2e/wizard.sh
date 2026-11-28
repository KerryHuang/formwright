#!/usr/bin/env bash
# 安裝端到端測試用：以 curl 走設定精靈 API 與登入（Linux、Docker 共用）。需要 curl、jq。
#   wizard.sh <base-url> <code> <backup-dir>     完整走一遍精靈並驗證完成後的狀態
#   wizard.sh verify-kept <base-url>             登入、記錄還在、印成功備份份數
#   wizard.sh checks <base-url> <code>           等服務起來後印環境檢查的 JSON
#   wizard.sh complete-expect <base-url> <code> <status>   送合法的 complete，狀態碼不符 exit 1
#   wizard.sh backups <base-url>                 等到沒有排隊或執行中的備份，印備份清單 JSON
set -euo pipefail

ADMIN_USER="admin"
ADMIN_PASSWORD='Fw-test-2026'
SITE_NAME='青禾貿易'
API=/api/v3/dynamic
MODULE=e2e-note
RECORD_TITLE=e2e-record-1
# 雙因素（系統預設管理員第一次存取受保護 API 前要先綁定，產品照設計的行為）：secret 與最後用過的時間步存這裡，
# 讓同一個 job 的後續步驟（升級、重裝後登入）共用；後端拒絕重放（時間步必須大於上次用過的）
STATE="${FW_E2E_STATE:-${RUNNER_TEMP:-/tmp}/fw-e2e-totp}"
umask 077   # STATE 含 TOTP secret，本腳本建的檔都只給自己讀寫
TOTP="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/totp.py"
JAR=$(mktemp)
trap 'rm -f "$JAR"' EXIT

step() { echo "==> $*" >&2; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# http <METHOD> <url> [header…] -- [curl 其餘參數…]：回應本文落 $BODY，印狀態碼
BODY=$(mktemp)
trap 'rm -f "$JAR" "$BODY"' EXIT
http() {
  local method="$1" url="$2"; shift 2
  curl -sS -o "$BODY" -w '%{http_code}' -X "$method" -b "$JAR" -c "$JAR" "$@" "$url" || echo 000
}
expect() { # expect <說明> <期望碼> <實得碼>
  [ "$2" = "$3" ] || { echo "FAIL: $1：期望 $2、實得 $3" >&2; cat "$BODY" >&2 || true; echo >&2; exit 1; }
}

setup_post() { # setup_post <base> <code> <path> <json>
  http POST "$1/api/setup/$3" -H "X-Setup-Code: $2" -H 'Content-Type: application/json' -d "$4"
}

complete_body() { # complete_body <backup-dir>
  jq -nc --arg p "$ADMIN_PASSWORD" --arg s "$SITE_NAME" --arg b "$1" \
    '{provider:"sqlite", adminPassword:$p, siteName:$s, backupRoot:$b}'
}

wait_status() { # wait_status <url> <期望碼> <秒>
  local _ code
  for _ in $(seq 1 "$3"); do
    code=$(http GET "$1"); [ "$code" = "$2" ] && return 0; sleep 1
  done
  return 1
}

# 產生下一組可用的 TOTP 碼：時間步必須大於上次用過的，且不超過「現在 + 1」（後端容許前後各一步）；不夠就等到下一個時間窗
totp_code() {
  local secret last now step
  secret=$(sed -n 1p "$STATE"); last=$(sed -n 2p "$STATE"); last="${last:--1}"
  while :; do
    now=$(( $(date +%s) / 30 ))
    step=$(( now > last ? now : last + 1 ))
    [ "$step" -le $(( now + 1 )) ] && break
    sleep 5
  done
  printf '%s\n%s\n' "$secret" "$step" > "$STATE"
  python3 -I "$TOTP" "$secret" --step "$step"
}

# 綁定雙因素：setup 回 secret → 算碼 → enable
enroll_2fa() { # enroll_2fa <base>
  local c
  c=$(http POST "$1$API/account/two-factor/setup" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg p "$ADMIN_PASSWORD" '{password:$p}')")
  expect "雙因素 setup" 200 "$c"
  printf '%s\n\n' "$(jq -r '.secret' "$BODY")" > "$STATE"
  c=$(http POST "$1$API/account/two-factor/enable" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg c "$(totp_code)" '{code:$c}')")
  expect "雙因素 enable" 200 "$c"
}

login() { # login <base>：登入；被要求第二步就帶 TOTP；還沒綁定過就綁定
  local c
  c=$(http POST "$1$API/auth/login" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg u "$ADMIN_USER" --arg p "$ADMIN_PASSWORD" '{userId:$u,password:$p}')")
  expect "登入" 200 "$c"
  if jq -e '.twoFactorRequired == true' "$BODY" >/dev/null; then
    [ -s "$STATE" ] || fail "登入要求雙因素，但沒有已綁定的 secret（$STATE）"
    c=$(http POST "$1$API/auth/two-factor" -H 'Content-Type: application/json' \
      -d "$(jq -nc --arg c "$(totp_code)" '{code:$c}')")
    expect "雙因素登入" 200 "$c"
  elif [ ! -s "$STATE" ]; then
    enroll_2fa "$1"
  fi
}

cmd_checks() { # checks <base> <code>
  step "等 /api/setup/status 200（最多 60 秒）"
  wait_status "$1/api/setup/status" 200 60 || fail "/api/setup/status 沒有回 200"
  local code
  code=$(setup_post "$1" "$2" checks '{"provider":"sqlite"}')
  expect "checks" 200 "$code"
  cat "$BODY"; echo
}

cmd_complete_expect() { # complete-expect <base> <code> <status>
  local code
  code=$(setup_post "$1" "$2" complete "$(complete_body "${FW_E2E_BACKUP_DIR:-/var/lib/formwright/backup}")")
  cat "$BODY"; echo
  [ "$code" = "$3" ] || fail "complete 期望 $3、實得 $code"
}

cmd_backups() { # backups <base>
  login "$1"
  local _ code
  for _ in $(seq 1 60); do
    code=$(http GET "$1$API/system/ops/backups")
    expect "備份清單" 200 "$code"
    if jq -e '(.current == null) and ([.runs[] | select((.status|tostring|ascii_downcase) as $s | $s=="queued" or $s=="running")] | length == 0)' "$BODY" >/dev/null; then
      jq -c '[.runs[] | {trigger, requestedUserId, status, queuedUtc, finishedUtc}]' "$BODY"
      return 0
    fi
    sleep 2
  done
  fail "備份一直沒有結束"
}

cmd_verify_kept() { # verify-kept <base>
  login "$1"
  local code
  code=$(http GET "$1$API/modules/$MODULE/records/paged?currentPage=1&pagesSize=50")
  expect "記錄清單" 200 "$code"
  # 精確比對：某筆記錄的 values 裡有一個值剛好等於標題
  jq -e --arg t "$RECORD_TITLE" '[.. | objects | select(has("values")) | .values | to_entries[] | .value] | index($t) != null' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "記錄 $RECORD_TITLE 不見了"; }
  code=$(http GET "$1$API/system/ops/backups")
  expect "備份清單" 200 "$code"
  echo "成功備份份數：$(jq '[.runs[] | select((.status|tostring|ascii_downcase)=="succeeded")] | length' "$BODY")"
}

create_module_and_record() { # <base>
  local base="$1" code blockId titleId
  code=$(http POST "$base$API/modules" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg k "$MODULE" '{key:$k, name:{"zh-TW":"端到端筆記"}}')")
  expect "建模組" 201 "$code"
  code=$(http GET "$base$API/modules/$MODULE"); expect "讀模組" 200 "$code"
  blockId=$(jq -r '.blocks[0].id' "$BODY"); titleId=$(jq -r '.titleFieldId' "$BODY")
  [ "$titleId" != null ] || fail "模組沒有標題欄位"
  code=$(http POST "$base$API/modules/$MODULE/fields" -H 'Content-Type: application/json' \
    -d "$(jq -nc --argjson b "$blockId" '{blockId:$b, type:"text", label:{"zh-TW":"備註"}, config:{}}')")
  expect "加欄位" 201 "$code"
  code=$(http POST "$base$API/modules/$MODULE/activate"); expect "啟用模組" 200 "$code"
  code=$(http POST "$base$API/modules/$MODULE/records" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg k "f_$titleId" --arg v "$RECORD_TITLE" '{values:{($k):$v}}')")
  expect "建記錄" 201 "$code"
}

cmd_full() { # <base> <code> <backup-dir>
  local base="$1" code="$2" backup="$3" c
  rm -f "$STATE"
  # 設定碼是啟動時最早印的一行，那時 Kestrel 可能還沒開始接受連線（#149）
  step "等 /api/setup/status 200（最多 60 秒）"
  wait_status "$base/api/setup/status" 200 60 || fail "/api/setup/status 沒有回 200"
  step "GET / 是 HTML"
  c=$(http GET "$base/" -D "$BODY.h"); expect "GET /" 200 "$c"
  grep -qi '^content-type: *text/html' "$BODY.h" || fail "GET / 不是 HTML"
  rm -f "$BODY.h"
  step "/api/setup/status 200"
  c=$(http GET "$base/api/setup/status"); expect "status" 200 "$c"
  step "錯的設定碼 403（AC-10）"
  c=$(http POST "$base/api/setup/verify" -H 'Content-Type: application/json' -d '{"code":"000000"}'); expect "verify 000000" 403 "$c"
  step "環境檢查全綠"
  c=$(setup_post "$base" "$code" checks "$(jq -nc --arg b "$backup" '{provider:"sqlite", backupRoot:$b}')"); expect "checks" 200 "$c"
  jq -e '.ok == true' "$BODY" >/dev/null || { cat "$BODY" >&2; fail "環境檢查不是全綠"; }
  step "complete"
  c=$(setup_post "$base" "$code" complete "$(complete_body "$backup")"); expect "complete" 200 "$c"
  step "等站台以一般模式回來（最多 120 秒）"
  local _ ok=0
  for _ in $(seq 1 120); do
    if [ "$(http GET "$base/api/setup/status")" = 404 ] && [ "$(http GET "$base/api/health/ready")" = 200 ]; then ok=1; break; fi
    sleep 1
  done
  [ "$ok" = 1 ] || fail "站台沒有在 120 秒內回到一般模式"
  step "登入、不必改密碼"
  c=$(http POST "$base$API/auth/login" -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg u "$ADMIN_USER" --arg p "$ADMIN_PASSWORD" '{userId:$u,password:$p}')")
  expect "登入" 200 "$c"
  jq -e '.mustChangePassword == false' "$BODY" >/dev/null || fail "mustChangePassword 不是 false"
  step "綁定雙因素（產品要求系統預設管理員先綁定）"
  enroll_2fa "$base"
  c=$(http GET "$base/api/setup/status"); expect "完成後 status" 404 "$c"
  step "備份位置等於精靈填的（AC-03）"
  c=$(http GET "$base$API/system/ops/backups"); expect "備份清單" 200 "$c"
  [ "$(jq -r '.displayLocation' "$BODY")" = "$backup" ] || { cat "$BODY" >&2; fail "備份位置不是 $backup"; }
  step "建模組與記錄"
  create_module_and_record "$base"
  echo "OK"
}

verb="${1:-}"
case "$verb" in
  verify-kept) [ $# -eq 2 ] || fail "用法：verify-kept <base-url>"; cmd_verify_kept "$2" ;;
  checks) [ $# -eq 3 ] || fail "用法：checks <base-url> <code>"; cmd_checks "$2" "$3" ;;
  complete-expect) [ $# -eq 4 ] || fail "用法：complete-expect <base-url> <code> <status>"; cmd_complete_expect "$2" "$3" "$4" ;;
  backups) [ $# -eq 2 ] || fail "用法：backups <base-url>"; cmd_backups "$2" ;;
  http*) [ $# -eq 3 ] || fail "用法：<base-url> <code> <backup-dir>"; cmd_full "$1" "$2" "$3" ;;
  *) fail "不認得的子命令：$verb" ;;
esac
