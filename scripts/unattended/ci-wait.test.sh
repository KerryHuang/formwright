#!/usr/bin/env bash
# ci-wait.sh --can-reuse 的離線測試：bash scripts/unattended/ci-wait.test.sh（不連 GitHub）
set -eu
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
cd "$tmp" && git init -q -b main && git config user.email t@example.invalid && git config user.name t
mkdir -p backend docs && echo a > backend/a.cs && echo d > docs/x.md && git add backend docs && git commit -qm base
green=$(git rev-parse HEAD)
fail=0
check() { # 期望（0 可沿用／1 要重跑） 說明
  if bash "$here/ci-wait.sh" --can-reuse "$green" HEAD >/dev/null; then got=0; else got=1; fi
  [ "$got" = "$1" ] && echo "ok   $2" || { echo "FAIL $2（期望 $1、實得 $got）"; fail=1; }
}
check 0 "同一個 commit"
echo d2 > docs/x.md && git commit -qam "只改文件"
check 0 "只差文件就沿用"
echo b > backend/a.cs && git commit -qam "改後端"
check 1 "程式碼範圍有變就重跑"
exit $fail
