#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 宝武学习助手 · 双平台发布脚本
#
#   ./publish.sh win      → Windows 自包含单文件 exe
#   ./publish.sh mac      → macOS .app + .dmg
#   ./publish.sh all      → 两个都出（在 macOS 上可交叉编译 Windows 包）
#   ./publish.sh run      → 本地调试运行
#   ./publish.sh clean    → 清空 dist 产物
# ─────────────────────────────────────────────────────────────
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/BaoWuLearn.Desktop/BaoWuLearn.Desktop.csproj"
OUT="$ROOT/dist"
APP_NAME="宝武学习助手"
EXE_NAME="BaoWuLearn"
VERSION="1.0.40"

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

  echo
  echo "ℹ 首次打开若被 Gatekeeper 拦截，执行："
  echo "    xattr -dr com.apple.quarantine \"$app\""
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
  all) publish_win; publish_mac ;;
  run) "$DOTNET" run --project "$PROJ" ;;
  clean) clean_dist ;;
  *) echo "用法：$0 {win|mac|all|run|clean}"; exit 1 ;;
esac
