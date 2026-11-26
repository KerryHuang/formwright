#!/usr/bin/env bash
# 等 GitHub Actions 的 gate 跑完：bash scripts/unattended/ci-wait.sh <sha>
# 最後一行是 gate 的結論（success／failure／cancelled…）；失敗時先列出失敗的 job。
# 任一 job 一失敗就回報 failure，不等整個 run 跑完。
# exit 0 只代表 gate 通過；其他情況 exit 1。
#
# 沿用已綠的 CI（#120）：rebase 後若程式碼範圍和已綠的 commit 完全相同，就不必重跑整套 gate。
#   bash scripts/unattended/ci-wait.sh --reuse <已綠 sha> <sha>   可沿用就印「沿用」並 exit 0，否則照常等 <sha> 的 CI
#   bash scripts/unattended/ci-wait.sh --can-reuse <已綠 sha> <sha>  只比對 tree（離線），exit 0 可沿用、1 要重跑
# 程式碼範圍＝下面 CODE_PATHS；rebase 帶進的 main 新 commit 碰到這些路徑時 tree 就不同，照規則重跑。
set -u
CODE_PATHS=(backend frontend shared scripts .github Dockerfile)
same_code() { git diff --quiet "$1" "$2" -- "${CODE_PATHS[@]}"; }
case "${1:-}" in
  --can-reuse) same_code "${2:?缺已綠 sha}" "${3:?缺 sha}"; exit ;;
  --reuse)
    green=$(git rev-parse "${2:?缺已綠 sha}"); shift 2
    if same_code "$green" "${1:?缺 sha}"; then
      run=$(gh run list --workflow gate.yml --commit "$green" --limit 1 --json databaseId,conclusion -q '.[0] | "\(.databaseId) \(.conclusion)"' 2>/dev/null)
      if [ "${run#* }" = success ]; then
        echo "沿用 $green 的 gate run ${run%% *}（程式碼範圍相同：${CODE_PATHS[*]}）"; echo success; exit 0
      fi
      echo "已綠 sha $green 的 gate 不是 success（${run:-找不到}），照常等 CI"
    else
      echo "程式碼範圍和 $green 不同，照常等 CI"
    fi ;;
esac
sha=$(git rev-parse "${1:?用法：ci-wait.sh [--reuse <已綠 sha>] <sha>}")
id=""
for _ in $(seq 1 60); do
  id=$(gh run list --workflow gate.yml --commit "$sha" --limit 1 --json databaseId -q '.[0].databaseId' 2>/dev/null)
  [ -n "$id" ] && break
  sleep 10
done
[ -n "$id" ] || { echo "找不到 $sha 的 gate run（分支推上去了嗎？）"; echo timeout; exit 1; }
# 每 30 秒看一次：整個 run 完成，或任一 job 已失敗（不等其他 job 跑完）就往下報
while :; do
  st=$(gh run view "$id" --json status,jobs -q '"\(.status) \([.jobs[] | select(.conclusion == "failure")] | length)"' 2>/dev/null)
  case "$st" in
    completed*) break ;;
    *" "[1-9]*) early=1; break ;;
  esac
  sleep 30
done
gh run view "$id" --json jobs -q '.jobs[] | "\(.name)\t\(.conclusion)\t\(if (.startedAt|startswith("0001")) or (.completedAt|startswith("0001")) then "-" else (((.completedAt|fromdate) - (.startedAt|fromdate)) / 60 | floor | tostring) + "m" end)"'
gh run view "$id" --json jobs -q '.jobs[] | select(.conclusion == "failure") | .name' | sed 's/^/FAILED /'
c=$(gh run view "$id" --json jobs -q '.jobs[] | select(.name == "gate") | .conclusion')
[ "${early:-}" = 1 ] && { echo "（run 仍在進行，首個失敗即回報；其餘 job 未等）"; echo failure; exit 1; }
echo "${c:-unknown}"
[ "$c" = success ]
