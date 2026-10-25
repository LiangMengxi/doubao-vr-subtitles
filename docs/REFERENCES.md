# 借鉴与实现说明

## PICO 麦克风延迟改善

功能思路借鉴 Kill11 的 [PicoStreamingMicrophoneKeeper](https://github.com/Kill11/PicoStreamingMicrophoneKeeper)：持续消费 PICO 串流麦克风的共享音频流，尝试避免部分驱动在其他程序未持续取包时产生的延迟累积。感谢原作者公开说明这一问题及处理思路。

截至 2026-10-21，参考仓库未声明可用于复制源码的开源许可证。本项目没有复制、修改或包含该项目的源码、图标或二进制，也不声称有权将其代码重新授权为 GPL。本功能按照微软公开的 Windows WASAPI 接口文档独立实现，仅借鉴上述功能思路。

- 只匹配名称中包含 `PicoStreamingMicrophone` 的录音设备，忽略大小写及空格、下划线、连字符；不会退回打开电脑的普通麦克风。
- 默认关闭，由用户在“设置 → 音频设备”中明确开启；开启状态会保存。
- 使用共享模式，不修改默认设备、设备格式、音量或其他应用的路由，不抢占独占模式。
- 持续取得并释放音频包后直接丢弃，不保存音频、不向网络发送音频。Windows 可能显示麦克风使用指示，这是保持输入流所必需的。
- 设备断开时释放音频资源并等待重新连接；关闭开关或退出软件时停止。该机制是兼容性改善尝试，不保证所有 PICO 驱动、串流方式或游戏都能降低延迟。

接口依据：[IAudioCaptureClient::GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)、[IAudioClient::Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)。

## SteamVR 启动注册

使用项目已固定版本的 Valve OpenVR SDK，通过应用清单和 `IVRApplications` 注册本程序。注册操作在本程序的独立短时进程中执行，避免初始化或关闭 OpenVR 时影响主进程正在运行的 VR 字幕。清单只包含本程序，不修改其他 SteamVR 应用或 Windows 开机启动项。

## 空间截图交互

- 作者：**AIRsLight**。
- 项目名称：**SteamVRTranslator**。
- 仓库：https://github.com/AIRsLight/SteamVRTranslator 。

借鉴范围仅为：短按左摇杆进入截图模式，按住双扳机，以双手位置作为空间选框对角点，松开任意扳机后捕获。空间平面、投影采样、输入状态机、资源管理及豆包窗口交接由本项目独立实现，没有引入参考仓库的源码、资源、二进制、依赖、API 上传、翻译、语音或模型服务。截至 2026-10-25 未找到参考仓库主体代码的明确许可授予，公开可阅读不等于允许复制或重新授权。

截图通过 Valve OpenVR 的单眼镜像接口按需取得，不使用第三方翻译 API；图片粘贴到用户明确选择并核验的豆包窗口后，由用户确认附件并发送。豆包自身可能向其服务上传附件，这不属于“完全离线”功能。

## 第三方许可

上述借鉴声明不是第三方源码的许可授予。Valve OpenVR、微软运行时、Windows App SDK 和 Inno Setup 仍按各自许可证分发，详见 [第三方组件](../THIRD_PARTY_NOTICES.md)。本项目原创实现的 GPL-3.0-only 许可不覆盖这些第三方组件，也不改变豆包、PICO 或 SteamVR 本身的权利。
