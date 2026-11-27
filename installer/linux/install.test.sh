#!/usr/bin/env bash
# 函式替身由 install.sh 的函式間接呼叫（SC2317）；install.sh 以 shellcheck source 指令另外檢查（SC1091）
# shellcheck disable=SC2317,SC1091
# install.sh 的離線測試：bash installer/linux/install.test.sh
# 不需要 root、不碰 systemd；install.sh 在 FW_TEST=1 時只定義函式、不執行 main。
set -u
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
fail=0
FW_TEST=1
export FW_TEST
# shellcheck source=install.sh
source "$here/install.sh"
set +e   # install.sh 的 set -e 不帶進測試；失敗由 check 統計

ok() { echo "ok   $1"; }
bad() { echo "FAIL $1 $2"; fail=1; }
# check <說明> <期望> <實得>
check() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "（期望 [$2]、實得 [$3]）"; fi; }
# run_rc <指令…>：在子 shell 跑，回 exit code；stdout 與 stderr 落到 $out
out="$tmp/out"
run_rc() { ( "$@" ) >"$out" 2>&1; echo $?; }

# --- parse_args ---
parsed() { ( parse_args "$@"; echo "$ACTION|$OPT_PORT|$OPT_SKIP_BACKUP|$OPT_LANG|$OPT_YES" ); }
check "parse_args 預設是 install"        "install||0||0" "$(parsed)"
check "parse_args remove"                "remove||0||0" "$(parsed remove)"
check "parse_args purge --yes"           "purge||0||1" "$(parsed purge --yes)"
check "parse_args --port 9000"           "install|9000|0||0" "$(parsed --port 9000)"
check "parse_args --skip-backup"         "install||1||0" "$(parsed --skip-backup)"
check "parse_args --lang en"             "install||0|en|0" "$(parsed --lang en)"
check "parse_args install --port 9000 --skip-backup" "install|9000|1||0" "$(parsed install --port 9000 --skip-backup)"
check "parse_args 未知動作 exit 1"       "1" "$(run_rc parse_args frobnicate)"
check "parse_args 未知選項 exit 1"       "1" "$(run_rc parse_args --nope)"
check "parse_args --port 不是數字 exit 1" "1" "$(run_rc parse_args --port abc)"
check "parse_args --port 超出範圍 exit 1" "1" "$(run_rc parse_args --port 70000)"
check "parse_args --lang fr exit 1"      "1" "$(run_rc parse_args --lang fr)"

# --- check_os ---
os_case() { # <VERSION_ID> [ID] [ARCH]
  mkdir -p "$tmp/os/etc"
  printf 'ID=%s\nVERSION_ID="%s"\n' "${2:-ubuntu}" "$1" > "$tmp/os/etc/os-release"
  FW_ROOT="$tmp/os" FW_ARCH="${3:-x86_64}" run_rc check_os
}
check "check_os 24.04 通過"   "0"  "$(os_case 24.04)"
check "check_os 22.04 通過"   "0"  "$(os_case 22.04)"
check "check_os 20.04 exit 11" "11" "$(os_case 20.04)"
check "check_os debian exit 11" "11" "$(os_case 12 debian)"
check "check_os aarch64 exit 11" "11" "$(os_case 24.04 ubuntu aarch64)"

# --- backup_exit_to_action ---
bea() { OPT_SKIP_BACKUP="$2" backup_exit_to_action "$1"; }
check "backup 0 → proceed"                "proceed"  "$(bea 0 0)"
check "backup 1 → abort20"                "abort20"  "$(bea 1 0)"
check "backup 2 無 skip → abort21"        "abort21"  "$(bea 2 0)"
check "backup 2 有 skip → proceed"        "proceed"  "$(bea 2 1)"
check "backup 134 → abort20"              "abort20"  "$(bea 134 0)"
check "backup 255 有 skip → abort20（skip 不略過崩潰）" "abort20" "$(bea 255 1)"
check "backup 1 有 skip → abort20"        "abort20"  "$(bea 1 1)"

# --- detect_state ---
root="$tmp/st"
check "detect_state 沒有設定檔 → fresh" "fresh" "$(FW_ROOT=$root detect_state)"
mkdir -p "$root$(dirname "$FW_CONFIG_FILE")" && : > "$root$FW_CONFIG_FILE"
check "detect_state 有設定檔沒有程式 → reinstall" "reinstall" "$(FW_ROOT=$root detect_state)"
mkdir -p "$root$FW_PROGRAM_DIR" && : > "$root$FW_PROGRAM_DIR/DynamicModules.WebAPI"
check "detect_state 兩者都有 → upgrade" "upgrade" "$(FW_ROOT=$root detect_state)"

# --- 來源檢查（FW_VERSION 空） ---
FW_VERSION="" FORMWRIGHT_PACKAGE="" FW_LANG=zh-TW run_rc check_source > /dev/null
check "FW_VERSION 空 exit 12" "12" "$(FW_VERSION="" FORMWRIGHT_PACKAGE="" FW_LANG=zh-TW run_rc check_source)"
case "$(cat "$out")" in *"不是從 Releases 下載"*) ok "FW_VERSION 空的繁中訊息";; *) bad "FW_VERSION 空的繁中訊息" "$(cat "$out")";; esac
FW_VERSION="" FORMWRIGHT_PACKAGE="" FW_LANG=en run_rc check_source > /dev/null
case "$(cat "$out")" in *"not downloaded from Releases"*) ok "FW_VERSION 空的英文訊息";; *) bad "FW_VERSION 空的英文訊息" "$(cat "$out")";; esac
check "有 FORMWRIGHT_PACKAGE 就通過" "0" "$(FW_VERSION="" FORMWRIGHT_PACKAGE=/x.tar.gz run_rc check_source)"
check "有 FW_VERSION 與 FW_REPO 就通過" "0" "$(FW_VERSION=1.0.0 FW_REPO=o/r FORMWRIGHT_PACKAGE="" run_rc check_source)"

# --- pick_lang ---
lang_case() { # <OPT_LANG> <FORMWRIGHT_LANG> <locale 檔內容或 -> <環境 LANG 或 ->
  rm -rf "$tmp/lg"; mkdir -p "$tmp/lg/etc/default"
  [ "$3" = "-" ] || printf '%s\n' "$3" > "$tmp/lg/etc/default/locale"
  if [ "$4" = "-" ]; then
    ( unset LANG; OPT_LANG="$1" FORMWRIGHT_LANG="$2" FW_ROOT="$tmp/lg" pick_lang )
  else
    ( OPT_LANG="$1" FORMWRIGHT_LANG="$2" FW_ROOT="$tmp/lg" LANG="$4" pick_lang )
  fi
}
check "pick_lang --lang en"                         "en"    "$(lang_case en "" - -)"
check "pick_lang FORMWRIGHT_LANG=zh-TW"             "zh-TW" "$(lang_case "" zh-TW - C.UTF-8)"
check "pick_lang 系統設定優先於環境"                "zh-TW" "$(lang_case "" "" LANG=zh_TW.UTF-8 C.UTF-8)"
check "pick_lang locale 有引號"                     "zh-TW" "$(lang_case "" "" 'LANG="zh_TW.UTF-8"' C.UTF-8)"
check "pick_lang 沒有 locale 檔、LANG=zh_HK"        "zh-TW" "$(lang_case "" "" - zh_HK.UTF-8)"
check "pick_lang zh_MO"                             "zh-TW" "$(lang_case "" "" - zh_MO.UTF-8)"
check "pick_lang zh_Hant_TW"                        "zh-TW" "$(lang_case "" "" - zh_Hant_TW.UTF-8)"
check "pick_lang zh-TW 連字號寫法"                  "zh-TW" "$(lang_case "" "" - zh-TW)"
check "pick_lang zh_CN → en"                        "en"    "$(lang_case "" "" - zh_CN.UTF-8)"
check "pick_lang th_TH → en"                        "en"    "$(lang_case "" "" - th_TH.UTF-8)"
check "pick_lang vi_VN → en"                        "en"    "$(lang_case "" "" - vi_VN.UTF-8)"
check "pick_lang C → en"                            "en"    "$(lang_case "" "" - C)"
check "pick_lang 未設 → en"                         "en"    "$(lang_case "" "" - -)"
check "pick_lang --lang fr exit 1"                  "1"     "$(OPT_LANG=fr run_rc pick_lang)"

# --- 字串表 ---
keys_of() { printf '%s\n' "$1" | sed -n 's/^\([a-z_0-9]*\)=.*/\1/p' | sort; }
check "msg 兩份表的鍵集合相同" "$(keys_of "$MSG_ZH_TW")" "$(keys_of "$MSG_EN")"
empty=$(printf '%s\n%s\n' "$MSG_ZH_TW" "$MSG_EN" | grep -cE '^[a-z_0-9]+=$')
check "msg 沒有空值" "0" "$empty"
used=$(grep -v '^[[:space:]]*#' "$here/install.sh" | grep -o 'msg [a-z_0-9]*' | awk '{print $2}' | sort -u)
missing=""
for k in $used; do
  printf '%s\n' "$MSG_ZH_TW" | grep -q "^$k=" || missing="$missing $k"
  printf '%s\n' "$MSG_EN" | grep -q "^$k=" || missing="$missing $k"
done
check "msg 腳本用到的鍵都在表裡" "" "$missing"
check "msg 英文" "Pre-upgrade backup failed" "$(FW_LANG=en msg backup_failed | cut -c1-25)"
# 不用 cut -c：GNU cut 的 -c 目前按 byte 切，會切壞 UTF-8
zh_out=$(FW_LANG=zh-TW msg backup_failed)
case "$zh_out" in 升級前備份失敗*) ok "msg 繁中";; *) bad "msg 繁中" "（實得 [$zh_out]）";; esac
check "msg 帶參數" "http://192.0.2.1:8080/" "$(FW_LANG=en msg open_url http://192.0.2.1:8080/ | grep -o 'http://[^ ]*')"

# --- ensure_dirs（M1）：symlink 與非目錄一律拒絕，目標的 owner／mode 不變，也不呼叫 chown ---
chown_log="$tmp/chown.log"
chown() { echo "chown $*" >> "$chown_log"; }          # 替身：不需要 root、也記錄有沒有被呼叫
dirs_case() { # <名稱> ：建一組暫存目錄，回傳其根目錄
  local r="$tmp/dirs-$1"; rm -rf "$r"; mkdir -p "$r"; echo "$r"
}
mode_of() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }
ensure_in() { # <根目錄>
  ( FW_DATA_DIR="$1/state/data" FW_BACKUP_DIR="$1/state/backup" FW_CONFIG_FILE="$1/etc/formwright.json" \
    FW_SERVICE_USER=svc ensure_dirs )
}
for victim in data backup state config; do
  r=$(dirs_case "sym-$victim"); mkdir -p "$r/target" "$r/state" "$r/etc"; chmod 0711 "$r/target"
  case "$victim" in
    data) ln -s "$r/target" "$r/state/data" ;;
    backup) ln -s "$r/target" "$r/state/backup" ;;
    state) rmdir "$r/state"; ln -s "$r/target" "$r/state" ;;
    config) rmdir "$r/etc"; ln -s "$r/target" "$r/etc" ;;
  esac
  : > "$chown_log"
  rc=$(run_rc ensure_in "$r")
  check "ensure_dirs 拒絕 $victim 是 symlink（exit 1）" "1" "$rc"
  check "ensure_dirs $victim symlink：目標 mode 不變" "711" "$(mode_of "$r/target")"
  # 先鎖父目錄：data／backup 是 symlink 時父目錄與設定目錄已先 chown（2 次），但絕不碰 symlink 與它的目標；state／config 本身是 symlink 就一次都不 chown
  case "$victim" in data|backup) want=2 ;; *) want=0 ;; esac
  check "ensure_dirs $victim symlink：chown 次數" "$want" "$(wc -l < "$chown_log" | tr -d ' ')"
  check "ensure_dirs $victim symlink：沒有 chown 到目標或子目錄" "0" "$(grep -c "target\|/data\|/backup" "$chown_log")"
  case "$(cat "$out")" in *"$r"*) ok "ensure_dirs $victim symlink：訊息含路徑";; *) bad "ensure_dirs $victim symlink：訊息含路徑" "$(cat "$out")";; esac
done
r=$(dirs_case file); mkdir -p "$r/state"; : > "$r/state/data"
check "ensure_dirs 拒絕 data 是一般檔案" "1" "$(run_rc ensure_in "$r")"
# 一個路徑不安全時，其他路徑也不能被改（先全部檢查再動手）
r=$(dirs_case allcheck); mkdir -p "$r/state" "$r/target"; ln -s "$r/target" "$r/state/backup"
: > "$chown_log"; run_rc ensure_in "$r" > /dev/null
check "ensure_dirs 有一個不安全就整批不動（data 沒被建立）" "no" "$([ -e "$r/state/data" ] && echo yes || echo no)"
r=$(dirs_case ok)
: > "$chown_log"
check "ensure_dirs 正常路徑通過" "0" "$(run_rc ensure_in "$r")"
check "ensure_dirs state 0755" "755" "$(mode_of "$r/state")"
check "ensure_dirs data 0700" "700" "$(mode_of "$r/state/data")"
check "ensure_dirs backup 0700" "700" "$(mode_of "$r/state/backup")"
check "ensure_dirs config 目錄 0770" "770" "$(mode_of "$r/etc")"
check "ensure_dirs state 給 root" "1" "$(grep -c "chown root:root $r/state\$" "$chown_log")"
check "ensure_dirs data／backup 給服務帳號" "1" "$(grep -c "chown svc:svc $r/state/data $r/state/backup" "$chown_log")"
check "ensure_dirs config 目錄 root:服務帳號" "1" "$(grep -c "chown root:svc $r/etc\$" "$chown_log")"

# M1-A：舊配置（父目錄屬服務帳號）要先收緊父目錄，之後才檢查子目錄
eval "$(declare -f refuse_unsafe_path | sed '1s/refuse_unsafe_path/orig_refuse_unsafe_path/')"
refuse_unsafe_path() { echo "refuse $1" >> "$chown_log"; orig_refuse_unsafe_path "$1"; }
r=$(dirs_case legacy); mkdir -p "$r/state/data" "$r/state/backup" "$r/etc"; chmod 0700 "$r/state"
: > "$chown_log"; ensure_in "$r" >/dev/null 2>&1
lock_line=$(grep -n "chown root:root $r/state\$" "$chown_log" | head -n 1 | cut -d: -f1)
data_line=$(grep -n "refuse $r/state/data\$" "$chown_log" | head -n 1 | cut -d: -f1)
backup_line=$(grep -n "refuse $r/state/backup\$" "$chown_log" | head -n 1 | cut -d: -f1)
check "ensure_dirs 舊配置：父目錄先 chown root，之後才檢查 data" "yes" "$([ -n "$lock_line" ] && [ -n "$data_line" ] && [ "$lock_line" -lt "$data_line" ] && echo yes || echo no)"
check "ensure_dirs 舊配置：父目錄先收緊，之後才檢查 backup" "yes" "$([ -n "$lock_line" ] && [ -n "$backup_line" ] && [ "$lock_line" -lt "$backup_line" ] && echo yes || echo no)"
check "ensure_dirs 舊配置：父目錄被收緊成 0755" "755" "$(mode_of "$r/state")"
refuse_unsafe_path() { orig_refuse_unsafe_path "$@"; }
# R3-1：在 `||` 左側呼叫時 set -e 不生效，第一步 chown 失敗也必須立刻中止，不碰子目錄
for failing in "root:root STATE" "root:svc ETC"; do
  r=$(dirs_case "fail-${failing%% *}"); mkdir -p "$r/state" "$r/etc"
  want_args=${failing/STATE/$r/state}; want_args=${want_args/ETC/$r/etc}
  chown() { echo "chown $*" >> "$chown_log"; [ "$*" = "$want_args" ] && return 1; return 0; }
  : > "$chown_log"; rc=0
  ( FW_DATA_DIR="$r/state/data" FW_BACKUP_DIR="$r/state/backup" FW_CONFIG_FILE="$r/etc/formwright.json" \
    FW_SERVICE_USER=svc ensure_dirs ) > "$out" 2>&1 || rc=$?
  check "ensure_dirs 在 || 左側、chown $want_args 失敗 → 非 0" "1" "$rc"
  check "ensure_dirs chown 失敗後不碰 data／backup" "0" "$(grep -c "/data\|/backup" "$chown_log")"
  check "ensure_dirs chown 失敗後沒有建立 data" "no" "$([ -e "$r/state/data" ] && echo yes || echo no)"
  case "$(cat "$out")" in *"$r"*) ok "ensure_dirs chown 失敗訊息含路徑";; *) bad "ensure_dirs chown 失敗訊息含路徑" "$(cat "$out")";; esac
done
unset -f chown

# --- 低 port 的綁定權限（M6） ---
unit_for() { # <port> <ip_unprivileged_port_start 或 ->
  local r="$tmp/unit-$1-$2"; rm -rf "$r"; mkdir -p "$r/proc/sys/net/ipv4"
  [ "$2" = "-" ] || echo "$2" > "$r/proc/sys/net/ipv4/ip_unprivileged_port_start"
  printf '{"Server":{"Port":%s}}\n' "$1" > "$r/cfg.json"
  ( systemctl() { :; }; id() { return 0; }; timeout() { shift; "$@"; }; runuser() { shift 3; "$@"; }
    FW_ROOT="$r" FW_CONFIG_FILE="$r/cfg.json" FW_UNIT_FILE="$r/unit" write_unit; cat "$r/unit" )
}
has_cap() { case "$1" in *AmbientCapabilities=CAP_NET_BIND_SERVICE*) echo yes;; *) echo no;; esac; }
check "unit：port 80、門檻 1024 → 加 CAP_NET_BIND_SERVICE" "yes" "$(has_cap "$(unit_for 80 1024)")"
check "unit：port 80、門檻 0 → 不加"                          "no"  "$(has_cap "$(unit_for 80 0)")"
check "unit：port 8080、門檻 1024 → 不加"                     "no"  "$(has_cap "$(unit_for 8080 1024)")"
check "unit：port 80、沒有 sysctl 檔（當 1024）→ 加"          "yes" "$(has_cap "$(unit_for 80 -)")"
check "unit：port 1024、門檻 1024 → 不加"                     "no"  "$(has_cap "$(unit_for 1024 1024)")"
case "$(unit_for 80 1024)" in *"[Install]"*"WantedBy=multi-user.target"*) ok "unit 結構完整";; *) bad "unit 結構完整" "";; esac

# --- 下載與 SHA256（S1）：用 curl／sha256sum 替身，不連網 ---
if ! command -v sha256sum >/dev/null 2>&1; then sha256sum() { shasum -a 256 "$@"; }; fi
dl="$tmp/dl"; mkdir -p "$dl"
NAME=formwright-1.2.3-linux-x64.tar.gz
printf 'package-bytes' > "$dl/$NAME"
GOOD=$(sha256sum "$dl/$NAME" | awk '{print $1}')
mkdir -p "$tmp/prog"; echo v-old > "$tmp/prog/VERSION"   # 舊版哨兵：下載階段不能動到
DL_MODE=ok
curl() { # 只處理 -o <檔> <網址>
  local outf="" url=""
  while [ $# -gt 0 ]; do case "$1" in -o) outf="$2"; shift ;; -*) ;; *) url="$1" ;; esac; shift; done
  case "$DL_MODE:$url" in
    interrupt:*) return 18 ;;
    notfound:*"$NAME") return 22 ;;
    *"/SHA256SUMS") cp "$dl/sums.$DL_MODE" "$outf" ;;
    *) cp "$dl/$NAME" "$outf" ;;
  esac
}
printf '%s  %s\n' "$GOOD" "$NAME" > "$dl/sums.ok"
printf '%s *%s\n' "$GOOD" "$NAME" > "$dl/sums.binary"
printf '%s  %s\n' "$(printf 'x' | sha256sum | awk '{print $1}')" "$NAME" > "$dl/sums.badhash"
printf '%s  other-%s\n' "$GOOD" "$NAME" > "$dl/sums.missing"
printf '%s  %s.sig\n' "$GOOD" "$NAME" > "$dl/sums.suffix"
printf '%s  %s\n%s  %s\n' "$GOOD" "$NAME" "$GOOD" "$NAME" > "$dl/sums.dup"
fetch_in() { # <mode>
  DL_MODE="$1"
  ( FW_VERSION=1.2.3 FW_REPO=o/r FORMWRIGHT_PACKAGE="" fetch_package; echo "$PACKAGE_FILE" ) 
}
for mode in ok binary; do
  check "下載：$mode 格式的正確 hash 通過" "0" "$(run_rc fetch_in $mode)"
done
for mode in badhash missing suffix dup; do
  check "下載：$mode → exit 12" "12" "$(run_rc fetch_in $mode)"
done
check "下載：404 → exit 12" "12" "$(run_rc fetch_in notfound)"
check "下載：中斷 → exit 12" "12" "$(run_rc fetch_in interrupt)"
check "下載失敗不動舊版" "v-old" "$(cat "$tmp/prog/VERSION")"
# shellcheck disable=SC2034  # FW_LANG 由子 shell 內的 msg 讀取
case "$(FW_LANG=en; DL_MODE=badhash; ( FW_VERSION=1.2.3 FW_REPO=o/r FORMWRIGHT_PACKAGE="" fetch_package ) 2>&1)" in *"Checksum verification failed"*) ok "下載：hash 不符的訊息";; *) bad "下載：hash 不符的訊息" "";; esac
unset -f curl

# --- 設定檔不以 root 直接開（M1-B）：改以服務帳號限時限量讀取 ---
rc_log="$tmp/rc.log"
cfg_stub() { # 替身：記錄 timeout／runuser 的參數，真的讀檔
  id() { return 0; }
  timeout() { echo "timeout $*" >> "$rc_log"; shift; "$@"; }
  runuser() { echo "runuser $*" >> "$rc_log"; shift 3; "$@"; }
}
printf '{"Server":{"Port":8088},"Attachments":{"Root":"/a"},"Backup":{"Root":"/b"}}\n' > "$tmp/c.json"
: > "$rc_log"
check "config_port 讀到設定的 port" "8088" "$(cfg_stub; FW_CONFIG_FILE="$tmp/c.json" FW_SERVICE_USER=svc config_port)"
check "read_config 用服務帳號、timeout 5、限 64KB" "yes" "$(grep -q 'timeout 5 runuser -u svc -- head -c 65536 -- ' "$rc_log" && echo yes || echo no)"
check "config_port 帳號不存在 → 預設值" "$FW_PORT" "$( (id() { return 1; }; FW_CONFIG_FILE="$tmp/c.json" config_port) )"
for bad in 0 99999 123456789012 70000; do
  printf '{"Server":{"Port":%s}}\n' "$bad" > "$tmp/bad.json"
  check "config_port 非法 port $bad → 預設值" "$FW_PORT" "$(cfg_stub; FW_CONFIG_FILE="$tmp/bad.json" FW_SERVICE_USER=svc config_port)"
done
check "config_port 讀檔失敗 → 預設值" "$FW_PORT" "$(cfg_stub; FW_CONFIG_FILE="$tmp/none.json" FW_SERVICE_USER=svc config_port)"
check "purge 取 Backup 區段的 Root（不是 Attachments 的）" "/b" "$(cfg_stub; FW_CONFIG_FILE="$tmp/c.json" FW_SERVICE_USER=svc read_config | awk '/"Backup"[[:space:]]*:/ { f = 1 } f && /"Root"/ { print; exit }' | sed -n 's/.*"Root"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
# shellcheck disable=SC2016  # 要比對字面的 $FW_CONFIG_FILE
check "root 不直接 sed 設定檔" "0" "$(grep -cE 'sed .*"\$FW_CONFIG_FILE"' "$here/install.sh")"
check "install.sh 檔頭列出 FORMWRIGHT_DOWNLOAD_BASE" "yes" "$(head -n 12 "$here/install.sh" | grep -q FORMWRIGHT_DOWNLOAD_BASE && echo yes || echo no)"

# --- 升級前備份的輸出寫進 log 檔、終端只印一行（#145）：stub 掉 systemd 與換程式的步驟 ---
up_case() { # <名稱> <備份結束碼> <備份最後一行> [OPT_SKIP_BACKUP]；回傳升級的 exit code，終端輸出在 $out
  up="$tmp/up-$1"; rm -rf "$up"; mkdir -p "$up/prog"
  # shellcheck disable=SC2034  # 這些變數由子 shell 內的 do_upgrade、msg 讀取
  ( set -e
    FW_PROGRAM_DIR="$up/prog" FW_LOG_DIR="$up/log" FW_LANG="${UP_LANG:-en}" OPT_SKIP_BACKUP="${4:-0}" PACKAGE_VERSION=2.0.0
    stage_package() { mkdir -p "$FW_PROGRAM_DIR.new"; }
    installed_version() { echo 1.0.0; }
    systemctl() { echo "systemctl $*" >> "$up/calls"; }
    chown() { echo "chown $*" >> "$up/calls"; }
    ensure_dirs() { :; }; swap_program() { :; }; write_unit() { :; }; wait_active() { :; }
    as_service() {
      local i; for i in $(seq 1 30); do echo "{\"LogLevel\":\"Information\",\"n\":$i}"; done
      echo '{"LogLevel":"Warning","stream":"stderr"}' >&2
      echo "$backup_line"; return "$backup_rc"
    }
    backup_rc="$2"; backup_line="$3"
    do_upgrade ) >"$out" 2>&1
  echo $?
}
has() { grep -qF -- "$2" "$1" 2>/dev/null && echo yes || echo no; }
up_log() { find "$up/log" -name 'upgrade-*.log' 2>/dev/null | head -n 1; }

rc=$(up_case ok 0 'FORMWRIGHT_BACKUP_OK k1'); up="$tmp/up-ok"
check "升級：備份成功 exit 0" "0" "$rc"
check "升級：終端沒有 JSON log" "no" "$(has "$out" '"LogLevel"')"
check "升級：終端印備份完成" "yes" "$(has "$out" 'Pre-upgrade backup completed.')"
log=$(up_log)
check "升級：log 含 stdout 與機器標記" "yes" "$(has "$log" 'FORMWRIGHT_BACKUP_OK k1')"
check "升級：log 含 stderr" "yes" "$(has "$log" '"stream":"stderr"')"
check "升級：log 目錄 0700" "700" "$(mode_of "$up/log")"
check "升級：log 檔 0600" "600" "$(mode_of "$log")"
check "升級：log 目錄給 root" "yes" "$(has "$up/calls" "chown root:root $up/log")"

for case_ in "1 20 Pre-upgrade" "2 21 No"; do
  read -r brc want head_ <<< "$case_"
  rc=$(up_case "fail$brc" "$brc" "FORMWRIGHT_BACKUP_X code$brc"); up="$tmp/up-fail$brc"; log=$(up_log)
  check "升級：備份結束碼 $brc → exit $want" "$want" "$rc"
  check "升級 ${want}：印既有訊息" "yes" "$(grep -q "^$head_" "$out" && echo yes || echo no)"
  check "升級 ${want}：印 log 路徑" "yes" "$([ -n "$log" ] && has "$out" "$log" || echo no)"
  check "升級 ${want}：只印最後 20 行（含最後一行）" "yes" "$(has "$out" "FORMWRIGHT_BACKUP_X code$brc")"
  check "升級 ${want}：JSON 行數 19（最後 20 行扣掉標記行）" "19" "$(grep -c '"LogLevel"' "$out")"
  check "升級 ${want}：舊版服務拉起來" "yes" "$(has "$up/calls" 'systemctl start formwright')"
  check "升級 ${want}：清掉 .new" "no" "$([ -e "$up/prog.new" ] && echo yes || echo no)"
  check "升級 ${want}：不印備份完成" "no" "$(has "$out" 'Pre-upgrade backup completed.')"
done

rc=$(up_case skip 2 'FORMWRIGHT_BACKUP_NOT_CONFIGURED notConfigured' 1)
check "升級：--skip-backup exit 0" "0" "$rc"
check "升級：--skip-backup 明說略過、不說完成" "yes|no" "$(has "$out" 'skipped because of --skip-backup')|$(has "$out" 'Pre-upgrade backup completed.')"
rc=$(up_case pending 0 'FORMWRIGHT_BACKUP_SETUP_PENDING')
check "升級：精靈未完成 exit 0" "0" "$rc"
check "升級：精靈未完成明說沒有資料可備份、不說完成" "yes|no" "$(has "$out" 'nothing to back up')|$(has "$out" 'Pre-upgrade backup completed.')"
UP_LANG=zh-TW; rc=$(up_case zh 0 'FORMWRIGHT_BACKUP_OK k2'); unset UP_LANG
check "升級：繁中訊息" "yes" "$(has "$out" '升級前備份完成')"

# log 目錄是 symlink：停服務之前就中止，目標不動
up="$tmp/up-sym"; mkdir -p "$tmp/up-target"
# shellcheck disable=SC2034  # 同上：子 shell 內讀取
rc=$( ( FW_PROGRAM_DIR="$tmp/up-symprog" FW_LOG_DIR="$tmp/up-symlog" PACKAGE_VERSION=2.0.0
  ln -s "$tmp/up-target" "$tmp/up-symlog"
  stage_package() { mkdir -p "$FW_PROGRAM_DIR.new"; }
  systemctl() { echo "systemctl $*" >> "$tmp/up-sym.calls"; }
  chown() { echo "chown $*" >> "$tmp/up-sym.calls"; }
  do_upgrade ) >"$out" 2>&1; echo $? )
check "升級：log 目錄是 symlink → exit 1" "1" "$rc"
check "升級：symlink 時沒有停服務、沒有 chown" "no" "$([ -e "$tmp/up-sym.calls" ] && echo yes || echo no)"
check "升級：symlink 目標沒有被寫入" "" "$(ls -A "$tmp/up-target")"
check "升級：symlink 時清掉 .new" "no" "$([ -e "$tmp/up-symprog.new" ] && echo yes || echo no)"

exit $fail
