#!/usr/bin/env bash
# Formwright（表匠）Ubuntu 安裝腳本。
# 用法：curl -fsSL <Releases 的 install.sh 網址> | sudo bash [-s -- <動作> [選項]]
#   動作：install（預設，也是升級）、remove、purge
#   選項：--port <n>（只在全新安裝生效）、--skip-backup、--lang <zh-TW|en>、--yes（purge 用）
# 環境變數：FORMWRIGHT_PACKAGE（本機 tarball 路徑，給測試用）、FORMWRIGHT_LANG、
#   FORMWRIGHT_DOWNLOAD_BASE（改從鏡像或測試伺服器下載：套件與 SHA256SUMS 兩個檔都從這個位置取；
#   sudo 預設會清掉環境變數，要寫成 sudo FORMWRIGHT_DOWNLOAD_BASE=… bash install.sh）
# 結束碼：0 成功；10 不是 root；11 不支援的系統；12 下載或校驗失敗；
#        20 升級前備份失敗（已中止、舊版照跑）；21 備份位置沒設且沒給 --skip-backup
set -euo pipefail

# 預設值由 scripts/installer/render-defaults.mjs 依 installer/install-defaults.json 寫入，不要手改標記之間的內容
# >>> install-defaults
FW_PORT=8080
FW_PROGRAM_DIR='/opt/formwright'
FW_DATA_DIR='/var/lib/formwright/data'
FW_BACKUP_DIR='/var/lib/formwright/backup'
FW_CONFIG_FILE='/etc/formwright/formwright.json'
FW_SERVICE_USER='formwright'
FW_VERSION=''
FW_REPO=''
# <<< install-defaults

# 測試用：FW_ROOT 是 /etc、/opt 的路徑前綴（正式執行為空）；FW_ARCH 取代 uname -m
FW_ROOT="${FW_ROOT:-}"
FW_UNIT_FILE=/etc/systemd/system/formwright.service
# 升級前備份的完整輸出（程式的 JSON log）寫在這裡，只有 root 讀得到
FW_LOG_DIR=/var/log/formwright
FW_LANG="${FW_LANG:-en}"

ACTION=install
OPT_PORT=""
OPT_SKIP_BACKUP=0
OPT_LANG=""
OPT_YES=0

# ---------------------------------------------------------------------------
# 訊息（D-20）：每行 key=句子，兩份表鍵集合相同；%s 是參數。機器讀的行（FORMWRIGHT_SETUP_CODE=、網址）不翻。
MSG_ZH_TW='need_root=請用 root 執行（例如 sudo bash）。
unsupported_os=不支援的系統：需要 x86_64 的 Ubuntu 22.04 或 24.04。
bad_arg=不認得的參數：%s
bad_lang=--lang 只接受 zh-TW 或 en：%s
bad_port=--port 必須是 1–65535 的整數：%s
not_from_releases=這份腳本不是從 Releases 下載的（沒有寫入版本與來源），請從專案的 Releases 頁下載 install.sh。
download_failed=下載失敗：%s
checksum_failed=檔案校驗失敗（SHA256SUMS 對不上或查無此檔）：%s
need_tool=缺少必要指令：%s
installing=正在安裝 Formwright %s …
upgrading=正在升級 Formwright：%s → %s
backup_running=升級前先備份資料 …
backup_done=升級前備份完成。
backup_skipped=備份位置沒有設定，依 --skip-backup 略過升級前備份。
backup_pending=設定精靈還沒完成，沒有資料需要備份。
backup_log=升級前備份的詳細紀錄（只有 root 讀得到）：%s
backup_log_tail=紀錄的最後 20 行：
log_failed=無法建立紀錄檔 %s，已中止，程式沒有更新。
unsafe_path=拒絕處理：%s 是符號連結或不是目錄，沒有做任何變更。請確認後手動處理再重跑。
dirs_failed=無法設定 %s 的擁有者或權限，已中止，程式沒有更新。
backup_failed=升級前備份失敗，升級已中止，舊版照常執行。
backup_not_configured=備份位置沒有設定，升級已中止；確定不備份就加 --skip-backup 重跑。
upgraded=已升級到版本 %s。
start_failed=服務沒有在時限內啟動，請用 journalctl -u formwright 查看；舊版程式留在 %s。
open_url=請用瀏覽器開啟：%s
setup_code=設定碼（精靈第一步要輸入）：%s
reinstalled=已重新安裝程式，原有資料與設定保留。
removed=已移除程式與服務。保留了這些資料，需要時可重新安裝：
purge_need_yes=沒有終端機可以確認，請加 --yes。
purge_confirm=這會永久刪除所有資料、設定與備份（%s）。輸入 yes 確認：
purge_cancelled=已取消。
purge_done=已移除程式、服務、資料與設定。
purge_backup_elsewhere=備份位置設在預設目錄之外，沒有刪除：%s'
MSG_EN='need_root=Please run as root (for example with sudo bash).
unsupported_os=Unsupported system: x86_64 Ubuntu 22.04 or 24.04 is required.
bad_arg=Unknown argument: %s
bad_lang=--lang accepts only zh-TW or en: %s
bad_port=--port must be an integer from 1 to 65535: %s
not_from_releases=This script was not downloaded from Releases (no version or source is embedded). Download install.sh from the project Releases page.
download_failed=Download failed: %s
checksum_failed=Checksum verification failed (SHA256SUMS does not match or has no entry): %s
need_tool=Required command not found: %s
installing=Installing Formwright %s ...
upgrading=Upgrading Formwright: %s -> %s
backup_running=Backing up data before the upgrade ...
backup_done=Pre-upgrade backup completed.
backup_skipped=No backup location is configured; the pre-upgrade backup was skipped because of --skip-backup.
backup_pending=The setup wizard has not been completed; there is nothing to back up.
backup_log=Full pre-upgrade backup log (readable by root only): %s
backup_log_tail=Last 20 lines of the log:
log_failed=Could not create the log file %s; stopped without updating the program.
unsafe_path=Refusing to continue: %s is a symbolic link or not a directory. Nothing was changed; fix it manually and re-run.
dirs_failed=Could not set the owner or permissions of %s; stopped without updating the program.
backup_failed=Pre-upgrade backup failed; the upgrade was cancelled and the current version is still running.
backup_not_configured=No backup location is configured, so the upgrade was cancelled. Re-run with --skip-backup if you are sure you want to upgrade without a backup.
upgraded=Upgraded to version %s.
start_failed=The service did not start in time; check journalctl -u formwright. The previous program files are kept in %s.
open_url=Open in your browser: %s
setup_code=Setup code (enter it in the first wizard step): %s
reinstalled=Program files reinstalled; existing data and settings are kept.
removed=Program and service removed. These were kept so you can reinstall later:
purge_need_yes=No terminal available to confirm; re-run with --yes.
purge_confirm=This permanently deletes all data, settings and backups (%s). Type yes to confirm:
purge_cancelled=Cancelled.
purge_done=Program, service, data and settings removed.
purge_backup_elsewhere=The backup location is outside the default directories and was not deleted: %s'

msg() {
  local key="$1"; shift
  local table line fmt=""
  if [ "$FW_LANG" = "zh-TW" ]; then table="$MSG_ZH_TW"; else table="$MSG_EN"; fi
  while IFS= read -r line; do
    case "$line" in "$key="*) fmt="${line#*=}"; break ;; esac
  done <<< "$table"
  # shellcheck disable=SC2059  # 句子表是 printf 格式
  printf "$fmt\n" "$@"
}

die() { # die <結束碼> <訊息鍵> [參數…]
  local rc="$1"; shift
  msg "$@" >&2
  exit "$rc"
}

# ---------------------------------------------------------------------------
parse_args() {
  ACTION=install; OPT_PORT=""; OPT_SKIP_BACKUP=0; OPT_LANG=""; OPT_YES=0
  local seen_action=0
  while [ $# -gt 0 ]; do
    case "$1" in
      install|remove|purge)
        [ "$seen_action" = 0 ] || die 1 bad_arg "$1"
        ACTION="$1"; seen_action=1 ;;
      --port)
        [ $# -ge 2 ] || die 1 bad_arg "$1"
        case "$2" in ''|*[!0-9]*) die 1 bad_port "$2" ;; esac
        { [ "$2" -ge 1 ] && [ "$2" -le 65535 ]; } || die 1 bad_port "$2"
        OPT_PORT="$2"; shift ;;
      --skip-backup) OPT_SKIP_BACKUP=1 ;;
      --yes) OPT_YES=1 ;;
      --lang)
        [ $# -ge 2 ] || die 1 bad_arg "$1"
        case "$2" in zh-TW|en) ;; *) die 1 bad_lang "$2" ;; esac
        OPT_LANG="$2"; shift ;;
      *) die 1 bad_arg "$1" ;;
    esac
    shift
  done
}

# 語言（D-20）：--lang → FORMWRIGHT_LANG → /etc/default/locale → 環境 LANG。
# sudo 常把呼叫者的 LANG 丟掉，所以系統設定排在環境之前。繁中以外一律英文。
lang_from_locale_value() {
  case "$1" in
    zh_TW*|zh_HK*|zh_MO*|zh_Hant*|zh-TW*|zh-HK*|zh-MO*|zh-Hant*) echo zh-TW ;;
    *) echo en ;;
  esac
}

pick_lang() {
  if [ -n "${OPT_LANG:-}" ]; then
    case "$OPT_LANG" in zh-TW|en) echo "$OPT_LANG"; return ;; *) die 1 bad_lang "$OPT_LANG" ;; esac
  fi
  if [ -n "${FORMWRIGHT_LANG:-}" ]; then lang_from_locale_value "$FORMWRIGHT_LANG"; return; fi
  local file="${FW_ROOT}/etc/default/locale" v=""
  if [ -r "$file" ]; then
    v=$(sed -n 's/^LANG=//p' "$file" | head -n 1 | tr -d '"'"'")
  fi
  [ -n "$v" ] || v="${LANG:-}"
  lang_from_locale_value "$v"
}

check_root() { [ "$(id -u)" = 0 ] || die 10 need_root; }

check_os() {
  local file="${FW_ROOT}/etc/os-release" id="" ver=""
  if [ -r "$file" ]; then
    id=$(sed -n 's/^ID=//p' "$file" | head -n 1 | tr -d '"')
    ver=$(sed -n 's/^VERSION_ID=//p' "$file" | head -n 1 | tr -d '"')
  fi
  local arch="${FW_ARCH:-$(uname -m)}"
  { [ "$id" = ubuntu ] && { [ "$ver" = 22.04 ] || [ "$ver" = 24.04 ]; } && [ "$arch" = x86_64 ]; } || die 11 unsupported_os
}

# 沒有版本與來源、也沒有本機 tarball，表示腳本不是從 Releases 下載的
check_source() {
  if [ -z "${FORMWRIGHT_PACKAGE:-}" ] && { [ -z "$FW_VERSION" ] || [ -z "$FW_REPO" ]; }; then
    die 12 not_from_releases
  fi
}

# 備份結束碼 → 動作。--skip-backup 只略過「沒設位置」（2），不略過失敗與崩潰。
backup_exit_to_action() {
  case "$1" in
    0) echo proceed ;;
    2) if [ "$OPT_SKIP_BACKUP" = 1 ]; then echo proceed; else echo abort21; fi ;;
    *) echo abort20 ;;
  esac
}

# fresh：沒有設定檔；reinstall：設定檔在、程式不在（remove 之後重裝）；upgrade：兩者都在
detect_state() {
  if [ ! -e "${FW_ROOT}${FW_CONFIG_FILE}" ]; then echo fresh
  elif [ ! -e "${FW_ROOT}${FW_PROGRAM_DIR}/DynamicModules.WebAPI" ]; then echo reinstall
  else echo upgrade; fi
}

# ---------------------------------------------------------------------------
# 取得並解開套件到 ${FW_PROGRAM_DIR}.new
WORK_DIR=""
cleanup() { [ -z "$WORK_DIR" ] || rm -rf "$WORK_DIR"; }

need_tool() { command -v "$1" >/dev/null 2>&1 || die 12 need_tool "$1"; }

fetch_package() { # 設 PACKAGE_FILE、PACKAGE_VERSION
  WORK_DIR=$(mktemp -d)
  trap cleanup EXIT
  if [ -n "${FORMWRIGHT_PACKAGE:-}" ]; then
    PACKAGE_FILE="$FORMWRIGHT_PACKAGE"
    PACKAGE_VERSION="${FW_VERSION:-local}"
    [ -r "$PACKAGE_FILE" ] || die 12 download_failed "$PACKAGE_FILE"
    return
  fi
  need_tool curl; need_tool sha256sum
  local name="formwright-${FW_VERSION}-linux-x64.tar.gz"
  # FORMWRIGHT_DOWNLOAD_BASE：改從鏡像或測試用的伺服器下載（SHA256SUMS 與套件同一個位置）
  local base="${FORMWRIGHT_DOWNLOAD_BASE:-https://github.com/${FW_REPO}/releases/download/v${FW_VERSION}}"
  curl -fsSL -o "$WORK_DIR/$name" "$base/$name" || die 12 download_failed "$base/$name"
  curl -fsSL -o "$WORK_DIR/SHA256SUMS" "$base/SHA256SUMS" || die 12 download_failed "$base/SHA256SUMS"
  # 精確比對檔名（text 格式「<hash>  名稱」與 binary 格式「<hash> *名稱」都認），必須剛好一筆
  local expected actual
  expected=$(awk -v n="$name" '{ f = $2; sub(/^\*/, "", f); if (f == n) print tolower($1) }' "$WORK_DIR/SHA256SUMS")
  if [ -z "$expected" ] || [ "$(printf '%s\n' "$expected" | wc -l)" -ne 1 ]; then die 12 checksum_failed "$name"; fi
  actual=$(sha256sum "$WORK_DIR/$name" | awk '{ print tolower($1) }')
  [ "$actual" = "$expected" ] || die 12 checksum_failed "$name"
  PACKAGE_FILE="$WORK_DIR/$name"
  PACKAGE_VERSION="$FW_VERSION"
}

stage_package() {
  need_tool tar
  rm -rf "${FW_PROGRAM_DIR}.new"
  mkdir -p "${FW_PROGRAM_DIR}.new"
  tar -xzf "$PACKAGE_FILE" -C "${FW_PROGRAM_DIR}.new" || die 12 download_failed "$PACKAGE_FILE"
  [ -x "${FW_PROGRAM_DIR}.new/DynamicModules.WebAPI" ] || die 12 download_failed "$PACKAGE_FILE"
  chown -R root:root "${FW_PROGRAM_DIR}.new"
  chmod 0755 "${FW_PROGRAM_DIR}.new"
}

swap_program() {
  rm -rf "${FW_PROGRAM_DIR}.old"
  if [ -e "$FW_PROGRAM_DIR" ]; then mv "$FW_PROGRAM_DIR" "${FW_PROGRAM_DIR}.old"; fi
  mv "${FW_PROGRAM_DIR}.new" "$FW_PROGRAM_DIR"
}

installed_version() { cat "$FW_PROGRAM_DIR/VERSION" 2>/dev/null || echo unknown; }

ensure_user() {
  if ! id "$FW_SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --home "$(dirname "$FW_DATA_DIR")" --shell /usr/sbin/nologin "$FW_SERVICE_USER"
  fi
}

# 拒絕符號連結與非目錄（M1）：root 對服務帳號可能換掉的路徑做 mkdir／chown／chmod 之前先擋，擋到就什麼都不改
refuse_unsafe_path() {
  if [ -L "$1" ] || { [ -e "$1" ] && [ ! -d "$1" ]; }; then die 1 unsafe_path "$1"; fi
}

# 權限設計（M1）：
#   <state>（/var/lib/formwright）root:root 0755  — 服務帳號不能在裡面換 symlink
#   <state>/data、<state>/backup  服務帳號 0700
#   <config dir>（/etc/formwright）root:<服務帳號> 0770 — 後端存設定檔是「同目錄暫存檔再覆蓋」，服務帳號要能在目錄裡建檔
# 順序：先鎖父目錄（它的上層 /var/lib、/etc 是 root 的，可信），父目錄不再給服務帳號寫入之後，
# 才檢查子目錄並對它們 mkdir／chown／chmod；否則舊配置（父目錄屬服務帳號）下，檢查之後、收緊之前子目錄可被換成 symlink。
ensure_dirs() {
  local state_dir config_dir
  state_dir=$(dirname "$FW_DATA_DIR"); config_dir=$(dirname "$FW_CONFIG_FILE")
  # 每一步明確檢查回傳值：呼叫端可能在 `||` 左側呼叫本函式，那時 set -e 不生效
  refuse_unsafe_path "$state_dir"
  refuse_unsafe_path "$config_dir"
  mkdir -p "$state_dir" "$config_dir" || die 1 dirs_failed "$state_dir"
  chown root:root "$state_dir" || die 1 dirs_failed "$state_dir"
  chmod 0755 "$state_dir" || die 1 dirs_failed "$state_dir"
  chown "root:$FW_SERVICE_USER" "$config_dir" || die 1 dirs_failed "$config_dir"
  chmod 0770 "$config_dir" || die 1 dirs_failed "$config_dir"
  refuse_unsafe_path "$FW_DATA_DIR"
  refuse_unsafe_path "$FW_BACKUP_DIR"
  mkdir -p "$FW_DATA_DIR" "$FW_BACKUP_DIR" || die 1 dirs_failed "$FW_DATA_DIR"
  chown "$FW_SERVICE_USER:$FW_SERVICE_USER" "$FW_DATA_DIR" "$FW_BACKUP_DIR" || die 1 dirs_failed "$FW_DATA_DIR"
  chmod 0700 "$FW_DATA_DIR" "$FW_BACKUP_DIR" || die 1 dirs_failed "$FW_DATA_DIR"
}

# 低於主機「非特權 port 起點」的 port，服務帳號要有綁定權限（M6）；只有需要時才加
needs_bind_capability() {
  local start=1024 f="${FW_ROOT}/proc/sys/net/ipv4/ip_unprivileged_port_start" v
  if [ -r "$f" ]; then v=$(cat "$f"); case "$v" in ''|*[!0-9]*) ;; *) start="$v" ;; esac; fi
  [ "$(config_port)" -lt "$start" ]
}

write_unit() {
  local caps=""
  if needs_bind_capability; then caps=$'AmbientCapabilities=CAP_NET_BIND_SERVICE\n'; fi
  cat > "$FW_UNIT_FILE" <<UNIT
[Unit]
Description=Formwright
After=network.target

[Service]
Type=notify
User=$FW_SERVICE_USER
WorkingDirectory=$FW_PROGRAM_DIR
ExecStart=$FW_PROGRAM_DIR/DynamicModules.WebAPI --Install:ConfigFile=$FW_CONFIG_FILE
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
${caps}
[Install]
WantedBy=multi-user.target
UNIT
  systemctl daemon-reload
}

service_is_active() { systemctl is-active --quiet formwright; }

wait_active() { # 最多 60 秒
  local _
  for _ in $(seq 1 60); do service_is_active && return 0; sleep 1; done
  return 1
}

# 設定檔在服務帳號可替換的目錄裡（symlink、FIFO 都換得進來），root 不直接開它：
# 以服務帳號讀，限時限量（FIFO 會被 timeout 擋住，指向 root 才讀得到的檔會因權限讀不到）；帳號不存在就當讀不到
read_config() {
  id "$FW_SERVICE_USER" >/dev/null 2>&1 || return 0
  timeout 5 runuser -u "$FW_SERVICE_USER" -- head -c 65536 -- "$FW_CONFIG_FILE" 2>/dev/null || true
}

config_port() {
  local p
  p=$(read_config | sed -n 's/.*"Port"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' | head -n 1 || true)
  case "$p" in ''|*[!0-9]*) p="" ;; esac
  if [ -n "$p" ] && { [ "${#p}" -gt 5 ] || [ "$p" -lt 1 ] || [ "$p" -gt 65535 ]; }; then p=""; fi
  echo "${p:-$FW_PORT}"
}

first_ipv4() {
  local ip=""
  ip=$(hostname -I 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | head -n 1 || true)
  echo "${ip:-127.0.0.1}"
}

print_url() { msg open_url "http://$(first_ipv4):$(config_port)/"; }

open_firewall() {
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
    ufw allow "$1/tcp" >/dev/null
  fi
}

# 以服務帳號跑安裝程式自己的 CLI（旗標與 --Key=value 順序不拘）
as_service() { runuser -u "$FW_SERVICE_USER" -- "$@"; }

# ---------------------------------------------------------------------------
do_fresh() {
  msg installing "$PACKAGE_VERSION"
  ensure_user
  if service_is_active; then systemctl stop formwright; fi
  ensure_dirs
  stage_package
  swap_program
  rm -rf "${FW_PROGRAM_DIR}.old"
  local port="${OPT_PORT:-$FW_PORT}" init_out
  init_out=$(cd "$FW_PROGRAM_DIR" && as_service ./DynamicModules.WebAPI --install-init \
    "--Install:ConfigFile=$FW_CONFIG_FILE" "--Install:DataDirectory=$FW_DATA_DIR" \
    "--Install:BackupDirectory=$FW_BACKUP_DIR" "--Server:Port=$port")
  write_unit
  systemctl enable --now formwright
  open_firewall "$port"
  wait_active || die 1 start_failed "$FW_PROGRAM_DIR"
  print_url
  local line code
  line=$(printf '%s\n' "$init_out" | grep '^FORMWRIGHT_SETUP_CODE=' | head -n 1 || true)
  if [ -n "$line" ]; then
    echo "$line"
    code="${line#FORMWRIGHT_SETUP_CODE=}"
    msg setup_code "$(printf '%s' "$code" | sed 's/^\(....\)\(....\)\(....\)$/\1-\2-\3/')"
  fi
}

do_reinstall() {
  msg installing "$PACKAGE_VERSION"
  ensure_user
  ensure_dirs
  stage_package
  swap_program
  rm -rf "${FW_PROGRAM_DIR}.old"
  write_unit
  systemctl enable --now formwright
  open_firewall "$(config_port)"
  wait_active || die 1 start_failed "$FW_PROGRAM_DIR"
  msg reinstalled
  print_url
}

# 建立這次升級的紀錄檔 UPGRADE_LOG（目錄 root 0700、檔案 0600）。
# /var/log 在 Ubuntu 上 syslog 群組可寫，所以目錄同樣擋 symlink；noclobber 讓既有檔案（含 symlink）不會被覆寫。
UPGRADE_LOG=""
create_upgrade_log() {
  refuse_unsafe_path "$FW_LOG_DIR"
  # 每一步明確檢查回傳值：呼叫端在 `||` 左側呼叫本函式，那時 set -e 不生效
  mkdir -p "$FW_LOG_DIR" || die 1 log_failed "$UPGRADE_LOG"
  chown root:root "$FW_LOG_DIR" || die 1 log_failed "$UPGRADE_LOG"
  chmod 0700 "$FW_LOG_DIR" || die 1 log_failed "$UPGRADE_LOG"
  (umask 077; set -C; : > "$UPGRADE_LOG") || die 1 log_failed "$UPGRADE_LOG"
}

# 備份失敗時印紀錄檔位置與結尾，讓使用者不必另外去找原因
show_backup_log() {
  msg backup_log "$UPGRADE_LOG"
  msg backup_log_tail
  tail -n 20 "$UPGRADE_LOG" || true
}

do_upgrade() {
  local old_version
  old_version=$(installed_version)
  msg upgrading "$old_version" "$PACKAGE_VERSION"
  stage_package
  # 停服務之前先備好紀錄檔：建不出來就中止，舊版照跑（子 shell 攔下 die，才清得掉 .new）
  UPGRADE_LOG="$FW_LOG_DIR/upgrade-$(date -u +%Y%m%dT%H%M%SZ)-$$.log"
  (create_upgrade_log) || { rm -rf "${FW_PROGRAM_DIR}.new"; exit 1; }
  # 先用舊版（替換檔案之前）備份：新版 migration 會在啟動時先跑
  systemctl stop formwright || true
  msg backup_running
  local rc=0
  # 紀錄檔由 root 開好再交給子程序寫，服務帳號不需要進得了紀錄目錄
  (cd "$FW_PROGRAM_DIR" && as_service ./DynamicModules.WebAPI --backup-now "--Install:ConfigFile=$FW_CONFIG_FILE") >> "$UPGRADE_LOG" 2>&1 || rc=$?
  case "$(backup_exit_to_action "$rc")" in
    abort21) systemctl start formwright || true; rm -rf "${FW_PROGRAM_DIR}.new"; show_backup_log >&2; die 21 backup_not_configured ;;
    abort20) systemctl start formwright || true; rm -rf "${FW_PROGRAM_DIR}.new"; show_backup_log >&2; die 20 backup_failed ;;
  esac
  # 結束碼 0 不一定做了備份（精靈沒跑完），2 搭配 --skip-backup 也會往下走：照實說
  if [ "$rc" != 0 ]; then msg backup_skipped
  elif grep -q '^FORMWRIGHT_BACKUP_SETUP_PENDING' "$UPGRADE_LOG"; then msg backup_pending
  else msg backup_done; fi
  # 舊配置的主機在這裡一併收緊權限；失敗（例如目錄是 symlink）就把舊版服務拉起來、不換程式
  # 失敗時程式還沒換：清掉 .new、把舊版服務拉起來並確認真的在跑（拉不起來就明講），再以非 0 結束
  (ensure_dirs) || { rm -rf "${FW_PROGRAM_DIR}.new"; systemctl start formwright || true; wait_active || die 1 start_failed "$FW_PROGRAM_DIR"; exit 1; }
  swap_program
  write_unit
  systemctl start formwright || true
  wait_active || die 1 start_failed "${FW_PROGRAM_DIR}.old"
  rm -rf "${FW_PROGRAM_DIR}.old"
  msg upgraded "$(installed_version)"
}

do_remove() {
  systemctl disable --now formwright >/dev/null 2>&1 || true
  rm -f "$FW_UNIT_FILE"
  systemctl daemon-reload || true
  rm -rf "$FW_PROGRAM_DIR" "${FW_PROGRAM_DIR}.new" "${FW_PROGRAM_DIR}.old"
  msg removed
  echo "  $(dirname "$FW_DATA_DIR")"
  echo "  $(dirname "$FW_CONFIG_FILE")"
}

do_purge() {
  local state_dir config_dir backup_root=""
  state_dir=$(dirname "$FW_DATA_DIR"); config_dir=$(dirname "$FW_CONFIG_FILE")
  if [ "$OPT_YES" != 1 ]; then
    [ -r /dev/tty ] || die 1 purge_need_yes
    local answer=""
    msg purge_confirm "$state_dir, $config_dir, $FW_LOG_DIR" > /dev/tty
    read -r answer < /dev/tty || true
    [ "$answer" = yes ] || { msg purge_cancelled; exit 1; }
  fi
  # Backup 區段裡的 Root（Attachments 也有 Root，不能取第一個）
  backup_root=$(read_config | awk '/"Backup"[[:space:]]*:/ { f = 1 } f && /"Root"/ { print; exit }' \
    | sed -n 's/.*"Root"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' || true)
  do_remove >/dev/null
  rm -rf "$state_dir" "$config_dir" "$FW_LOG_DIR"
  if id "$FW_SERVICE_USER" >/dev/null 2>&1; then userdel "$FW_SERVICE_USER" || true; fi
  msg purge_done
  case "$backup_root" in
    ''|"$state_dir"/*) ;;
    *) if [ -e "$backup_root" ]; then msg purge_backup_elsewhere "$backup_root"; fi ;;
  esac
}

main() {
  parse_args "$@"
  FW_LANG=$(pick_lang)
  check_root
  check_os
  case "$ACTION" in
    remove) do_remove ;;
    purge) do_purge ;;
    install)
      check_source
      need_tool systemctl
      fetch_package
      case "$(detect_state)" in
        fresh) do_fresh ;;
        reinstall) do_reinstall ;;
        upgrade) do_upgrade ;;
      esac ;;
  esac
}

if [ -z "${FW_TEST:-}" ]; then main "$@"; fi
