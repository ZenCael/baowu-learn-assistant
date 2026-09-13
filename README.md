# 宝武学习助手（BaoWuLearn）

针对宝武学习平台（learn.baowugroup.com）的**纯 API 模式**自动学习助手。
不加载、不显示平台任何页面——与平台之间只有 HTTP 接口通讯。
支持 Windows / macOS 双平台。

> ⚠️ **免责声明**：本项目为个人学习自动化工具，仅供技术交流。自动化行为可能违反
> 平台服务条款，账号风险由使用者自行承担；请在了解相关规定的的前提下谨慎使用。

---

## 目录结构

```
.
├── BaoWuLearn/            # ★ 桌面客户端（当前主形态，v1.0.39）
│   ├── src/               #   .NET 10 + Avalonia 11 源码
│   ├── build/             #   双平台发布脚本 + SM2 基线样本
│   ├── design/            #   应用图标工程
│   └── README.md          #   客户端详细说明（技术栈 / 架构 / 口径）
├── userscript/            # 前史：浏览器油猴脚本时代（v2.1.1 → v2.5.2）
│   └── libs/              #   脚本时代的公共库
└── docs/                  # 思路与踩坑记录
    ├── 开发总览.md         #   全程开发日志（时间序）
    ├── 平台协议事实.md     #   逆向得到的平台接口口径（实测钉死）
    ├── 工程与交付经验.md   #   Avalonia / .NET / 打包交付踩坑
    ├── 桌面客户端技术验证报告.md
    └── 诊断报告/           #   三份疑难问题诊断（学时账本 / 零点失效等）
```

## 下载

去 **[Releases](https://github.com/ZenCael/baowu-learn-assistant/releases)** 页面下载对应平台的安装包：

| 平台 | 文件 |
|---|---|
| Windows | `BaoWuLearn-*-win-x64.exe`（自包含单文件，免装 .NET） |
| macOS (Apple Silicon) | `BaoWuLearn-*-macos-arm64.dmg` 或 `.zip` |

首次运行如遇 Gatekeeper 拦截：右键 → 打开，或 `xattr -dr com.apple.quarantine`。

## 形态说明

本项目为**纯 API 桌面客户端**：逆向平台前端 JS 得到全部业务接口后，
脱离浏览器直接与网关通讯。心跳拟人化、学时账本对齐、结算轮询确认，
以及 41 项离线启动自检（`--selftest`）。

## 技术要点

- **协议逆向**：从前端 bundle 反推私有 API（SM2 加密登录、两本账学时模型、ES 全文检索）
- **国密对齐**：C# BouncyCastle 输出与网页端 `sm-crypto` 完全同构的 SM2 密文
- **两本账模型**：课件播放位置 ≠ 平台计入学时（每跳心跳最多计 60s），引擎按平台口径记账
- **拟人化**：心跳间隔 58–63s 随机、请求指纹对齐真实浏览器
- **自检**：`宝武学习助手 --selftest` 离线跑 41 项断言（含逐页面真实例化，编译通过≠界面能开）

## License

MIT
