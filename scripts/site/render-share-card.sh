#!/usr/bin/env bash
# 把 scripts/site/share-card.html 截成官網分享預覽圖 site/assets/og-image.png（1200×630）。
# 需要本機有 Google Chrome（macOS 預設路徑，或用 CHROME 指定）；字型從 Google Fonts 載入，要連網。
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
chrome="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
"$chrome" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
  --window-size=1200,630 --virtual-time-budget=5000 \
  --screenshot="$root/site/assets/og-image.png" "file://$root/scripts/site/share-card.html"
echo "已產生 site/assets/og-image.png"
