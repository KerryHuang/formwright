#!/usr/bin/env bash
# Formwright（表匠）Ubuntu 安裝腳本。
# 用法：curl -fsSL <Releases 的 install.sh 網址> | sudo bash [-s -- <動作> [選項]]
#   動作：install（預設，也是升級）、remove、purge
#   選項：--port <n>（只在全新安裝生效）、--skip-backup、--lang <zh-TW|en>、--yes（purge 用）
# 環境變數：FORMWRIGHT_PACKAGE（本機 tarball 路徑，給測試用）、FORMWRIGHT_LANG
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
unsafe_path=拒絕處理：%s 是符號連結或不是目錄，沒有做任何變更。請確認後手動處理再重跑。
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
unsafe_path=Refusing to continue: %s is a symbolic link or not a directory. Nothing was changed; fix it manually and re-run.
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
  [ -n "$expected" ] && [ "$(printf '%s\n' "$expected" | wc -l)" -eq 1 ] || die 12 checksum_failed "$name"
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
ensure_dirs() {
  local state_dir config_dir
  state_dir=$(dirname "$FW_DATA_DIR"); config_dir=$(dirname "$FW_CONFIG_FILE")
  local p
  for p in "$state_dir" "$FW_DATA_DIR" "$FW_BACKUP_DIR" "$config_dir"; do refuse_unsafe_path "$p"; done
  mkdir -p "$state_dir" "$config_dir"
  chown root:root "$state_dir"
  chmod 0755 "$state_dir"
  mkdir -p "$FW_DATA_DIR" "$FW_BACKUP_DIR"
  chown "$FW_SERVICE_USER:$FW_SERVICE_USER" "$FW_DATA_DIR" "$FW_BACKUP_DIR"
  chmod 0700 "$FW_DATA_DIR" "$FW_BACKUP_DIR"
  chown "root:$FW_SERVICE_USER" "$config_dir"
  chmod 0770 "$config_dir"
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
  local i
  for i in $(seq 1 60); do service_is_active && return 0; sleep 1; done
  return 1
}

config_port() {
  local p
  p=$(sed -n 's/.*"Port"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$FW_CONFIG_FILE" 2>/dev/null | head -n 1)
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

do_upgrade() {
  local old_version
  old_version=$(installed_version)
  msg upgrading "$old_version" "$PACKAGE_VERSION"
  stage_package
  # 先用舊版（替換檔案之前）備份：新版 migration 會在啟動時先跑
  systemctl stop formwright || true
  msg backup_running
  local rc=0
  (cd "$FW_PROGRAM_DIR" && as_service ./DynamicModules.WebAPI --backup-now "--Install:ConfigFile=$FW_CONFIG_FILE") || rc=$?
  case "$(backup_exit_to_action "$rc")" in
    abort21) systemctl start formwright || true; rm -rf "${FW_PROGRAM_DIR}.new"; die 21 backup_not_configured ;;
    abort20) systemctl start formwright || true; rm -rf "${FW_PROGRAM_DIR}.new"; die 20 backup_failed ;;
  esac
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
    msg purge_confirm "$state_dir, $config_dir" > /dev/tty
    read -r answer < /dev/tty || true
    [ "$answer" = yes ] || { msg purge_cancelled; exit 1; }
  fi
  if [ -r "$FW_CONFIG_FILE" ]; then
    backup_root=$(sed -n 's/.*"Root"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$FW_CONFIG_FILE" | head -n 1 || true)
  fi
  do_remove >/dev/null
  rm -rf "$state_dir" "$config_dir"
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
