#!/usr/bin/env bash
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
check "msg 繁中" "升級前備份失敗" "$(FW_LANG=zh-TW msg backup_failed | cut -c1-7)"
check "msg 帶參數" "http://192.0.2.1:8080/" "$(FW_LANG=en msg open_url http://192.0.2.1:8080/ | grep -o 'http://[^ ]*')"

exit $fail
