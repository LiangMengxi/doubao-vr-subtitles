# 豆包VR字幕

**创作者：凉梦曦** · **v0.2.5** · Windows 10 2004 / Windows 11 x64 · SteamVR

将豆包桌面版的实时字幕窗口捕获到 SteamVR 悬浮层。豆包负责识别和翻译，本程序只转发字幕画面，不读取麦克风、不上传字幕、不修改豆包或游戏。独立的非官方项目。

## 下载与安装

在 [Releases](https://github.com/LiangMengxi/doubao-vr-subtitles/releases/tag/v0.2.5) 下载：

- `DoubaoVR-v0.2.5-Setup-x64.exe`：一键安装版。安装和升级均显示目录，默认沿用上次安装路径。
- `DoubaoVR-v0.2.5-windows-x64.zip`：免安装版，完整解压后运行 `DoubaoVR.exe`，不要只复制 EXE。
- 两个包均提供 `.sha256` 校验文件。

下载版已包含 .NET 与 WinUI 运行文件，不需要安装 Python、.NET SDK 或额外依赖管理器。需要另外安装并登录豆包桌面版；VR 显示需要 SteamVR 和头显，VRChat 不是必需依赖。

## 使用

1. 在豆包设置实时字幕快捷键，并确认能正常显示字幕。
2. 启动 SteamVR，打开“豆包VR字幕”。
3. 点击“开启字幕”，确认预览正确，再点击“开启VR”。
4. “字幕”页调节大小与距离；“位置”页调节位置、上下倾斜、左右倾斜和不透明度；“设置”页调节捕获、裁剪、主题及退出行为。

开启“关闭时自动最小化”后，右上角叉号只最小化到任务栏，VR 字幕继续显示；底部“退出”始终真正退出。“退出时同时关闭豆包”只影响底部退出按钮，默认关闭。重复启动程序可恢复已运行窗口。

新配置和日志位于 `%LOCALAPPDATA%\DoubaoVR.Native`。首次运行可读取旧版 `%LOCALAPPDATA%\DoubaoVR\settings.json`，不修改旧配置。安装、升级和卸载不清空这两个配置目录。

详细操作和排错见 [使用说明](USAGE.md)。

## 源码与构建

v0.2.5 使用 C++20 图形核心与 C# WinUI 3 界面。VR 图像留在 GPU 上，透明边界检测仅回读 16 字节，桌面预览限制在 640×240、约 10 FPS。

- `native-rewrite/`：当前 C++ / WinUI 版本，包含固定依赖、测试及发布脚本。
- `src/`、`tools/`、`installer/`：保留的旧 Python 版本及其打包工具，不是 v0.2.5 的构建入口。
- `releases/`、`artifacts/`：本机发行文件与验证、备份资料，不进入源码提交。

安装 PowerShell 7、.NET SDK 8.0.424 后，从仓库目录执行：

```powershell
cd native-rewrite
.\build.ps1 -PrepareToolchain -Publish
```

已有 MSVC 2022 C++、Windows SDK 与 CMake 时可省略 `-PrepareToolchain`。不使用 vcpkg、Conan、npm、pip 或额外依赖管理器。构建执行依赖校验、编译警告检查、GPU 测试及跨语言接口测试；产物位于 `native-rewrite/out/app/DoubaoVR.exe`。

生成 ZIP、安装 EXE 及 SHA256：

```powershell
.\package-release.ps1 -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

需要 Inno Setup 6.7 或更新版本。脚本重新执行完整构建和测试，发布前备份源码及上一版程序，校验载荷与版本，不打包个人设置、日志、开发工具或调试符号。

完整工程说明见 [重构工程](native-rewrite/README.md)，第三方许可见 [组件说明](native-rewrite/THIRD_PARTY_NOTICES.md)。

## 兼容性说明

仅支持 SteamVR 会话，不支持绕过 SteamVR 的原生 OpenXR 会话。当前版本不提供旧版托盘及通过图标识别单独关闭豆包字幕的功能；单独关闭字幕请使用豆包自身按钮。

已验证本机桌面捕获、窗口缩放、配置保存和资源释放；实际头显显示、双轴观感、混合显卡和游戏内兼容性仍需实机确认。构建及 GPU 测试不代表所有设备已通过验证。
