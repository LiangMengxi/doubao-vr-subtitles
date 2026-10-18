# 豆包 VR 字幕桥

**创作者：凉梦曦**

将豆包桌面版的实时字幕窗口直接捕获到 SteamVR 悬浮层中。豆包负责原有的识别与翻译，本程序只转发画面。无需修改豆包或 VRChat。

独立的非官方项目。当前版本 **v0.2.0**，适用于 Windows x64 / SteamVR，主要用于 VRChat 等 VR 场景中的多语种交流。

## 下载版快速开始

从仓库的 **Releases** 下载 `DoubaoVR-v0.2.0-windows-x64.zip`，完整解压后双击 `DoubaoVR.exe`，无需安装 Python。保留同目录的 `_internal` 文件夹。首次运行步骤见 [版本说明](RELEASE_NOTES.md)。

额外的识别和翻译软件只需安装并登录**豆包桌面版**；VR 显示使用已有的 **SteamVR 与头显**。无需其他语音识别或翻译工具，VRChat 也不是必需依赖。

**完整步骤、按钮说明、调节方法和排错：[使用说明](USAGE.md)。**

## 使用

1. 启动 SteamVR，并连接头显；通过 SteamVR 运行 VRChat。
2. 双击 `start.cmd`，或打开 `dist/DoubaoVR/DoubaoVR.exe`。
3. 点击左上角“一键开启字幕”。程序会在后台启动本机豆包（如尚未运行），使用豆包已配置的实时字幕快捷键，并自动连接字幕窗口。豆包已在后台运行时不会另行启动或激活主界面。
4. 确认桌面预览显示目标字幕，点击“开启 VR 悬浮”。
5. 在“大小与距离”选择小/标准/大字幕、近/适中/远距离，或使用“＋／−”微调。每次调整大小 5%、距离 0.1 米；也支持拖动滑块。

“位置与更多设置”中可以调整水平/垂直位置、倾斜角度、不透明度、顶部裁剪和捕获来源。大小百分比以宽 1 米为 100%。

字幕开启后，同一个按钮切换为“一键关闭字幕”。点击会关闭豆包字幕条并隐藏 VR 中的字幕，豆包进程保留在后台；再次开启字幕时，已连接的 VR 悬浮会自动恢复显示。在豆包中手动开启或关闭字幕时，按钮也会随实际状态更新。

关闭通过捕获画面识别字幕条右上角的关闭图标后调用，仅针对字幕窗口；若豆包更新导致图标无法识别，界面会提示手动关闭，避免点击错误位置。

字幕默认位于视野下方，跟随头显移动，完整显示字幕条（顶部裁剪默认为 0）。若希望隐藏豆包工具栏，可自行增加顶部裁剪，通常约 44 像素；点击“显示完整字幕条”可还原。设置自动保存。退出程序会销毁悬浮层。

桌面控制窗口采用 WinUI 3 / Fluent 风格的圆角卡片、强调色按钮、滑块和分页导航，默认大小为 560×660，可缩小到 520×650。右上角可选择浅色、深色或跟随系统，外观会自动保存。字幕预览会随窗口尺寸调整；这不会改变 VR 中设置的字幕大小。

点击右上角关闭按钮或“退出并关闭 VR 字幕”，会先销毁 SteamVR 悬浮层，再停止捕获并退出。需要继续显示时用“最小化”。再次双击桌面快捷方式或 EXE，会恢复已运行的控制窗口，不会重复启动一个实例。

“程序设置”中可开启“最小化到系统托盘”（默认关闭），启用后最小化会收入右下角托盘，点击托盘图标、选择“打开控制窗口”或再次运行同一个 EXE 可恢复。关闭此设置时仍按普通任务栏最小化。

“显示捕获黄框”控制 Windows 在豆包字幕窗口周围绘制的黄色捕获边框，默认关闭，切换立即生效并保存。切换时保留 VR 中最后一帧，待新捕获连接更新；若系统不支持切换且启动失败，会保留原连接。其他程序同时捕获同一窗口并要求边框时，Windows 仍可能显示黄框。参见 [微软捕获边框说明](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)。

“退出按钮同时关闭豆包”默认开启，只对底部退出按钮和托盘“退出”生效：先移除 VR 悬浮，再结束豆包及其后台进程以释放占用。关闭该设置后，这两个退出入口仅退出字幕桥。右上角 × 仍只退出字幕桥；上方字幕与 VR 开关保持独立。程序会记住这两个设置。

一键开启读取豆包当前的实时字幕快捷键，不修改豆包配置。本机已验证配置为 `Alt+Z`。若未设置快捷键、未登录豆包或两者运行权限不同，界面会提示处理方法。首次登录和权限授权需在豆包中手动完成。

豆包未运行时使用本机自动运行入口参数与隐藏启动请求，随后直接唤起字幕。此方式尽量避免打开聊天主界面；豆包首次登录、更新或必要提示仍可能自行显示界面，本程序不会自动处理这些提示。

后台启动仅在点击“一键开启字幕”时发生，不会添加、启用、关闭或修改任何 Windows 开机启动项，也不更改豆包的开机启动偏好。

字幕窗口可以被其他普通窗口遮挡，但应保持展开，避免最小化、关闭或收起。字幕窗口被重建时，本程序会重新查找并连接。若开启了多个无标题工具窗口，请在下拉列表中手动确认正确来源；自动选择使用尺寸与窗口属性评分，无法保证适配豆包所有未来版本。

此版本显示的是豆包已有的字幕画面，不重新排版文字，也不额外进行语音识别。只在 SteamVR 会话中提供悬浮层；不支持绕过 SteamVR 的原生 OpenXR 会话。

## 技术

- 通过 Win32 `EnumWindows` 查找豆包进程的窗口，包含无标题和 `WS_EX_TOOLWINDOW` 窗口，句柄动态获取。
- 通过 Windows Graphics Capture 按 HWND 捕获目标窗口。没有桌面截取回退，也不修改窗口样式。
- 保留 BGRA 捕获的透明度，裁剪后转换为 RGBA，上传至 SteamVR 所在显卡的 D3D11 纹理，通过 OpenVR `IVROverlay.SetOverlayTexture` 提交。
- 悬浮层相对于头显定位，使用 `VRApplication_Overlay` 与游戏并行运行。
- 控制界面使用 Tkinter 与 Sun Valley ttk 主题，以 WinUI 3 / Fluent 风格组织；下载版已包含主题资源，无需额外安装 Windows App SDK。
- 复用三张 GPU 纹理，上传完成后才提交；普通更新不隐藏、清空或重新创建悬浮层。上传超时会保留上一帧。窗口尺寸变化时重新分配纹理，旧纹理延迟释放。
- 每次 `SetOverlayTexture` 后立即刷新 D3D11 命令，确保 SteamVR 的复制操作不等待下一次字幕更新。只在上传前刷新不足以保证最后一帧及时显示。
- VR 更新上限约 20 FPS；桌面预览独立限为 10 FPS，最小化时停止预览重绘。关闭 VR 且控制窗口隐藏时跳过像素处理，恢复后使用最新帧，不积压字幕。
- 同一帧只转换一次；调整大小、距离等仅更新悬浮参数，不重复转换或上传纹理。透明裁边按行列计算范围，避免为所有不透明像素生成大型整数索引。
- 启动时限制 OpenCV 工作线程，并为 NumPy 默认使用单个 BLAS 线程（保留用户显式环境配置），减少无用的线程和私有内存提交；不修改系统环境变量。捕获到上传之间仍有 CPU 像素复制，并非全程 GPU 零拷贝。
- 不读取麦克风、不上传字幕、不注入游戏进程。日志只记录连接与错误信息；日常运行不会保存字幕截图。

## 从源代码运行

需要 Windows 10 1903 或更新版本、64 位 Python 3.12，以及支持 WGC 的图形环境。推荐使用当前 Windows 11。

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe app.py
```

## 打包

```powershell
.\.venv\Scripts\python.exe -m pip install pyinstaller==6.20.0
.\.venv\Scripts\python.exe -m PyInstaller --noconfirm --clean --windowed --onedir --name DoubaoVR --icon assets/bridge.ico --collect-all openvr --collect-all windows_capture --collect-all sv_ttk --add-data "assets/bridge.png;assets" --add-data "assets/bridge.ico;assets" --add-data "assets/caption-close.png;assets" app.py
```

分发时需保留整个 `dist/DoubaoVR` 文件夹，不能只拷贝 EXE。日志和设置保存在可执行文件旁，程序应放在有写入权限的文件夹。

生成用于 Releases 的 ZIP 和 SHA256 文件：

```powershell
.\.venv\Scripts\python.exe package_release.py
```

输出在 `releases/`，包含运行程序、说明和第三方许可。打包脚本仅选择运行所需文件，不包含本机设置、日志、测试截图或录音。第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 排错

- 没找到窗口：点击“一键开启字幕”，或在豆包打开实时字幕并展开字幕条后刷新。
- 预览为空：确认选择的是字幕窗口，保持展开；检查 `logs/bridge.log`。
- VR 连接失败：确认 SteamVR 与头显已就绪，再点击开启。
- 游戏内看不到：确认游戏运行于同一 SteamVR 会话，并检查位置、距离、不透明度；本机 API 成功不代表已完成头显目视验证。
- 字幕不更新：先查看豆包桌面字幕是否更新。静止画面只采集一帧是正常行为。

## 参考

- [Microsoft：按 HWND 创建 GraphicsCaptureItem](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [Windows Capture Python 实现](https://github.com/NiiightmareXD/windows-capture/tree/main/windows-capture-python)
- [Valve：OpenVR Overlay](https://github.com/ValveSoftware/openvr/blob/master/headers/openvr.h)
- [Microsoft：D3D11 Flush](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)
