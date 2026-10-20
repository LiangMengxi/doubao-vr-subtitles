# 第三方组件

本项目是独立的非官方工具，不隶属于豆包、Valve 或 VRChat。豆包与 SteamVR 需要用户另行安装，本仓库与发布包不包含豆包客户端。

当前版本使用 C++20 核心与 C# WinUI 3 界面，不包含 Python、Python 扩展或 Tcl/Tk。Windows 发布包中的第三方许可保留为 TXT；各组件及其内含依赖遵循各自许可。

| 组件 | 来源 | 许可信息 |
| --- | --- | --- |
| OpenVR SDK 与运行库 | https://github.com/ValveSoftware/openvr | BSD-3-Clause；随附 `OpenVR-LICENSE.txt` |
| .NET 运行时 | https://github.com/dotnet/runtime | 随附 `licenses/DotNet-LICENSE.txt` 与 `licenses/DotNet-THIRD-PARTY-NOTICES.txt` |
| Windows App SDK / WinUI 3 | https://github.com/microsoft/WindowsAppSDK | 随附 `licenses/WindowsAppSDK-LICENSE.txt` 与 `licenses/WindowsAppSDK-NOTICE.txt` |
| Inno Setup 安装引导程序及中文翻译 | https://jrsoftware.org/ / https://github.com/jrsoftware/issrc | Inno Setup 随附许可；翻译保留原作者信息 |

MSVC、Windows SDK、CMake 和 SDK 构建工具仅用于构建，不作为开发工具随应用分发。固定版本和许可来源见 [当前工程组件说明](native-rewrite/THIRD_PARTY_NOTICES.md)。应用图标为本项目生成。

旧 Python 版本的依赖与许可说明保留在 [v0.2.4 标签](https://github.com/LiangMengxi/doubao-vr-subtitles/tree/v0.2.4)，不代表当前发布包的组件。
