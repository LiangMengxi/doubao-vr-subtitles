# 豆包VR字幕

**创作者：凉梦曦** · **v0.3.1** · Windows 10 2004 / Windows 11 x64 · SteamVR

**v0.3.1** 新增 PICO 麦克风延迟改善及随 SteamVR 启动设置，修复深浅色标题栏，并采用 GPL-3.0 开源许可。保留音频恢复和主题个性化功能。

将豆包桌面版的实时字幕窗口捕获到 SteamVR 悬浮层。豆包负责识别和翻译，本程序转发字幕画面，不上传字幕、不修改豆包或游戏。默认不读取麦克风；明确开启可选的 PICO 延迟改善后，仅消费并丢弃匹配设备的音频包，不保存、不上传。独立的非官方项目。

## 下载与安装

在 [Releases](https://github.com/LiangMengxi/doubao-vr-subtitles/releases/tag/v0.3.1) 下载：

- `DoubaoVR-v0.3.1-Setup-x64.exe`：一键安装版。安装和升级均显示目录，默认沿用上次安装路径。
- `DoubaoVR-v0.3.1-windows-x64.zip`：免安装版，完整解压后运行 `DoubaoVR.exe`，不要只复制 EXE。
- `DoubaoVR-v0.3.1-source.zip`：对应版本的完整公开源码及构建资料。
- 三个包均提供 `.sha256` 校验文件。

下载版已包含 .NET 与 WinUI 运行文件，不需要安装 Python、.NET SDK 或额外依赖管理器。需要另外安装并登录豆包桌面版；VR 显示需要 SteamVR 和头显，VRChat 不是必需依赖。

## 使用

1. 在豆包设置实时字幕快捷键，并确认能正常显示字幕。
2. 启动 SteamVR，打开“豆包VR字幕”。
3. 点击“开启字幕”，确认预览正确，再点击“开启VR”。
4. “字幕”页调节大小与距离；“位置”页调节位置、上下倾斜、左右倾斜和不透明度；“设置”页调节捕获、裁剪、主题及退出行为。

开启“关闭时自动最小化”后，右上角叉号只最小化到任务栏，VR 字幕继续显示；底部“退出”始终真正退出。“退出时同时关闭豆包”只影响底部退出按钮，默认关闭。重复启动程序可恢复已运行窗口。

新配置和日志位于 `%LOCALAPPDATA%\DoubaoVR.Native`。首次运行可读取旧版 `%LOCALAPPDATA%\DoubaoVR\settings.json`，不修改旧配置。安装、升级和卸载不清空这两个配置目录。

详细操作和排错见 [使用说明](docs/USAGE.md)，版本变更见 [发布说明](docs/RELEASE_NOTES.md)。

在“音频设备”内开启 PICO 麦克风延迟改善，在“程序设置”内注册随 SteamVR 启动；标题栏与深浅色主题同步。PICO 改善效果依赖实际串流设备及驱动，不保证所有环境有效。

## 源码与构建

当前版本使用 C++20 图形核心与 C# WinUI 3 界面。VR 图像留在 GPU 上，透明边界检测仅回读 16 字节，桌面预览限制在 640×240、约 10 FPS。

- `native-rewrite/`：当前 C++ / WinUI 版本，包含程序源码、固定依赖声明、构建及发布脚本。
- `scripts/`：统一的构建、打包和启动入口。
- `docs/`：使用说明、发布说明与维护者发布流程。
- `VERSION`：当前版本号的唯一来源，C++、C# 和发布脚本共同读取。
- `releases/`、`artifacts/`：本机发行文件与验证、备份资料，不进入源码提交。

主分支只维护当前程序及其必要构建资料，不提交测试代码、验证记录或开发指令文件。旧 Python 工程及其打包工具已从主分支移出，完整源码可在 [v0.2.4 标签](https://github.com/LiangMengxi/doubao-vr-subtitles/tree/v0.2.4) 中查阅；本地已有旧源码、测试资料和备份不受影响。

安装 PowerShell 7、.NET SDK 8.0.424 后，从仓库目录执行：

```powershell
.\scripts\build.ps1 -PrepareToolchain
```

已有 MSVC 2022 C++、Windows SDK 与 CMake 时可省略 `-PrepareToolchain`。不使用 vcpkg、Conan、npm、pip 或额外依赖管理器。公开源码可独立构建，不要求本地测试目录；始终执行依赖校验、内存规范和编译警告检查。源码构建产物位于 `native-rewrite/ui/bin/Release/net8.0-windows10.0.19041.0/win-x64/DoubaoVR.exe`。

维护者准备好完整的本地验证资料后，可生成 ZIP、安装 EXE 及 SHA256：

```powershell
.\scripts\package-release.ps1 -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

需要 Inno Setup 6.7 或更新版本。正式发布必须执行本地 GPU、跨语言接口与动效验收，缺少验证资料会明确终止，不会将跳过测试视为通过。发布前备份源码及上一版程序，正式程序不编入动效测试类；发行包不包含 Markdown 文档、个人设置、日志、开发工具或调试符号，必要许可证以 TXT 保留。

构建后可双击 `scripts/start.cmd` 启动当前版本，不会回退运行本机保留的 Python 程序。完整工程说明见 [工程说明](native-rewrite/README.md)，发布流程见 [发布约定](docs/RELEASING.md)，第三方许可见 [组件说明](THIRD_PARTY_NOTICES.md)。

## 兼容性说明

仅支持 SteamVR 会话，不支持绕过 SteamVR 的原生 OpenXR 会话。当前版本不提供旧版托盘及通过图标识别单独关闭豆包字幕的功能；单独关闭字幕请使用豆包自身按钮。

已验证本机桌面捕获、窗口缩放、配置保存和资源释放；实际头显显示、双轴观感、混合显卡和游戏内兼容性仍需实机确认。构建及 GPU 测试不代表所有设备已通过验证。

## 开源许可与借鉴

Copyright (C) 2026 凉梦曦。本项目原创程序代码、构建脚本及原创文档以 **GNU GPL version 3 only（GPL-3.0-only）** 授权，全文见 [LICENSE](LICENSE)。允许依照该许可证使用、修改和再分发；本程序不提供任何担保。第三方组件保留各自许可证，不因本项目采用 GPL 而被重新授权。

PICO 麦克风改善思路参考 Kill11 的 PicoStreamingMicrophoneKeeper，本项目根据 Windows WASAPI 文档独立实现，未复制参考项目的代码或资源。详细借鉴声明见 [参考说明](docs/REFERENCES.md)。分发安装包或 ZIP 时，应同时提供与该二进制对应的完整源码及构建说明，而非只指向可能已变化的最新主分支。

公开源码无需私有测试资料即可重建完整程序、ZIP 和安装 EXE，见 [源码重建说明](docs/SOURCE_BUILD.md)。源码重建输出与正式发行目录隔离，不替代维护者的完整发布验收。
