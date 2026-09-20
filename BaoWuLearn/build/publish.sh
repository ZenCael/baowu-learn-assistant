#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 宝武学习助手 · 双平台发布脚本
#
#   ./publish.sh win      → Windows 自包含单文件 exe
#   ./publish.sh mac      → macOS .app + .dmg + .zip（zip 是自动更新通道用）
#   ./publish.sh update   → 只生成签名更新清单 update.json + update.sig
#   ./publish.sh all      → 以上全套（win + mac + 清单）
#   ./publish.sh run      → 本地调试运行
#   ./publish.sh clean    → 清空 dist 产物
# 清单签名需要 build/keys/update-ed25519.pem（私钥，gitignore 挡住，绝不入库）
# 与一把支持 ed25519 的 OpenSSL 3（脚本自动探测 brew 路径）。
# ─────────────────────────────────────────────────────────────
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/BaoWuLearn.Desktop/BaoWuLearn.Desktop.csproj"
OUT="$ROOT/dist"
APP_NAME="宝武学习助手"
EXE_NAME="BaoWuLearn"
VERSION="1.0.43"

# 更新清单的签名工具：macOS 系统 openssl 是 LibreSSL（不支持 ed25519 rawin），
# 必须找 OpenSSL 3（Homebrew 或 PATH 里可用的那份）。
find_openssl3() {
  local cand
  for cand in /opt/homebrew/opt/openssl@3/bin/openssl \
              /usr/local/opt/openssl@3/bin/openssl \
              "$(command -v openssl 2>/dev/null || true)"; do
    [ -n "$cand" ] && [ -x "$cand" ] || continue
    if "$cand" pkeyutl -help 2>&1 | grep -q -- '-rawin'; then
      echo "$cand"; return 0
    fi
  done
  return 1
}

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET="$HOME/.dotnet/dotnet"
fi

mkdir -p "$OUT"

publish_win() {
  echo "▶ 发布 Windows 自包含单文件…"
  # 先显式还原（含该 RID 的 runtime pack）；单独执行可避免 publish 阶段的凭据访问问题
  "$DOTNET" restore "$PROJ" -r win-x64 >/dev/null 2>&1 || true
  "$DOTNET" publish "$PROJ" -c Release -r win-x64 --self-contained true \
    --no-restore \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:Version="$VERSION" \
    -o "$OUT/win-x64"

  # 重命名为中文可执行文件名
  if [ -f "$OUT/win-x64/$EXE_NAME.exe" ]; then
    mv -f "$OUT/win-x64/$EXE_NAME.exe" "$OUT/$APP_NAME.exe"
    echo "✓ 产物：$OUT/$APP_NAME.exe"
    ls -lh "$OUT/$APP_NAME.exe"
  fi
}

write_plist() {
  local app="$1"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.baowu.learn.helper</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$EXE_NAME</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleIconFile</key><string>app.icns</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
</dict>
</plist>
PLIST
}

publish_mac() {
  local rid="osx-arm64"
  if [ "$(uname -m)" = "x86_64" ]; then
    rid="osx-x64"
  fi

  local app="$OUT/$APP_NAME.app"

  # 直接发布进 .app 内部目录：重启构建时文件原地覆盖，
  # 避免 "先删整棵 bundle 再重建" 这种既慢又容易误删的做法。
  echo "▶ 发布 macOS ($rid) → $app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

  "$DOTNET" restore "$PROJ" -r "$rid" >/dev/null 2>&1 || true
  "$DOTNET" publish "$PROJ" -c Release -r "$rid" --self-contained true \
    --no-restore \
    -p:PublishSingleFile=false \
    -p:Version="$VERSION" \
    -o "$app/Contents/MacOS"

  chmod +x "$app/Contents/MacOS/$EXE_NAME"
  write_plist "$app"

  # 应用图标：design/app.icns → Contents/Resources（Dock / Finder 显示用）
  local icns_src="$ROOT/design/app.icns"
  if [ -f "$icns_src" ]; then
    cp -f "$icns_src" "$app/Contents/Resources/app.icns"
    echo "✓ 已写入应用图标 app.icns"
  else
    echo "⚠ 未找到 $icns_src，本包不带自定义图标"
  fi

  # ★ 原地覆盖不会改动 .app 顶层目录自身的时间戳，Finder 里"修改日期"会停在上一版，
  #   看起来像没重新打包（发布面板里也容易被当成旧产物）。这里把目录时间戳顶到当前。
  touch "$app" "$app/Contents"
  echo "✓ 已生成 $app"

  # 压成 dmg（需要能挂载临时卷；沙箱/受限环境下会失败，此时只保留 .app）
  local dmg="$OUT/$APP_NAME.dmg"
  rm -f "$dmg" 2>/dev/null || true
  echo "▶ 打包 dmg…"
  if hdiutil create -volname "$APP_NAME" -srcfolder "$app" -ov -format UDZO "$dmg" >/dev/null 2>&1; then
    echo "✓ 产物：$dmg"
    ls -lh "$dmg"
  else
    echo "⚠ dmg 制作失败（通常是当前环境不允许挂载临时卷）"
    echo "   .app 已就绪，可直接使用；需要 dmg 时在本地终端重跑：./build/publish.sh mac"
  fi

  # ★ 交付面板只认文件不认目录，且自动更新的 mac 通道走 zip：
  #   这里直接产出，不留给"记得手动 ditto"这种老坑。
  local zip="$OUT/$APP_NAME-macOS.zip"
  rm -f "$zip" 2>/dev/null || true
  echo "▶ 打包 zip（自动更新用）…"
  ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
  echo "✓ 产物：$zip"
  ls -lh "$zip"

  echo
  echo "ℹ 首次打开若被 Gatekeeper 拦截，执行："
  echo "    xattr -dr com.apple.quarantine \"$app\""
}

gen_update_manifest() {
  echo "▶ 生成更新清单 update.json + update.sig…"
  local key="$ROOT/build/keys/update-ed25519.pem"
  if [ ! -f "$key" ]; then
    echo "✖ 缺少发布私钥 $key —— 跳过清单生成。"
    echo "  首次发布请生成：openssl genpkey -algorithm ed25519 -out $key"
    echo "  并把公钥同步进 Ed25519Verifier.PublicKeyHex（私钥绝不入库）。"
    exit 1
  fi
  local os3
  if ! os3="$(find_openssl3)"; then
    echo "✖ 找不到支持 ed25519 的 OpenSSL 3（brew install openssl@3）"
    exit 1
  fi

  local exe="$OUT/$APP_NAME.exe"
  local zip="$OUT/$APP_NAME-macOS.zip"
  [ -f "$exe" ] || { echo "✖ 缺 Windows 产物 $exe"; exit 1; }
  [ -f "$zip" ] || { echo "✖ 缺 macOS 产物 $zip"; exit 1; }

  local sha_win sha_mac n_win n_mac pubdate notes
  sha_win=$(shasum -a 256 "$exe" | cut -d' ' -f1)
  sha_mac=$(shasum -a 256 "$zip" | cut -d' ' -f1)
  n_win=$(stat -f%z "$exe" 2>/dev/null || stat -c%s "$exe")
  n_mac=$(stat -f%z "$zip" 2>/dev/null || stat -c%s "$zip")
  pubdate=$(date +%Y-%m-%dT%H:%M:%S%z | sed 's/\([+-][0-9][0-9]\)\([0-9][0-9]\)$/\1:\2/')
  notes="${UPDATE_NOTES:-常规更新}"

  # 清单里的 file 名 = Release 页的 ASCII 资产名（GitHub API 会剥掉中文名）
  local manifest="$OUT/update.json"
  printf '%s' "{\"schema\":1,\"version\":\"$VERSION\",\"pubDate\":\"$pubdate\",\"notes\":\"$notes\",\"assets\":{\"win-x64\":{\"file\":\"BaoWuLearn-$VERSION-win-x64.exe\",\"sha256\":\"$sha_win\",\"size\":$n_win},\"macos-arm64\":{\"file\":\"BaoWuLearn-$VERSION-macos-arm64.zip\",\"sha256\":\"$sha_mac\",\"size\":$n_mac}},\"mirrors\":[\"https://gh-proxy.com/\",\"https://ghfast.top/\"]}" > "$manifest"

  "$os3" pkeyutl -sign -inkey "$key" -rawin -in "$manifest" \
    | xxd -p | tr -d '\n' | tr 'A-F' 'a-f' > "$OUT/update.sig"
  # 长度自证：ed25519 签名必须恰好 128 个 hex 字符（64 字节）
  if [ "$(wc -c < "$OUT/update.sig" | tr -d ' ')" != "128" ]; then
    echo "✖ update.sig 长度异常（$(wc -c < "$OUT/update.sig" | tr -d ' ') 字符，应为 128）"
    exit 1
  fi

  # 自证：签完立刻用公钥回环验一次，坏钥/坏工具当场暴露，不等客户端用户去踩
  if "$os3" pkeyutl -verify -pubin \
        -inkey "$ROOT/build/keys/update-ed25519.pub.pem" -rawin -in "$manifest" \
        -sigfile <(xxd -p -r < "$OUT/update.sig") >/dev/null 2>&1; then
    echo "✓ 本地验签回环通过"
  else
    echo "✖ 本地验签回环失败 —— 私钥与内嵌公钥不匹配，或 openssl 行为异常。不要发布这份清单。"
    exit 1
  fi

  echo "✓ 清单：$manifest"
  echo "✓ 签名：$OUT/update.sig ($(cat "$OUT/update.sig" | wc -c | tr -d ' ') hex 字符)"
}

clean_dist() {
  echo "▶ 清理 $OUT"
  rm -rf "$OUT/win-x64" "$OUT/osx-arm64" "$OUT/osx-x64" 2>/dev/null || true
  rm -f "$OUT/$APP_NAME.exe" "$OUT/$APP_NAME.dmg" 2>/dev/null || true
  echo "ℹ 保留 $APP_NAME.app（如需彻底清掉请手动删除）"
}

case "${1:-all}" in
  win) publish_win ;;
  mac) publish_mac ;;
  update) gen_update_manifest ;;
  all) publish_win; publish_mac; gen_update_manifest ;;
  run) "$DOTNET" run --project "$PROJ" ;;
  clean) clean_dist ;;
  *) echo "用法：$0 {win|mac|update|all|run|clean}"; exit 1 ;;
esac
