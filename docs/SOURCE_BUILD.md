# 对应源码与重建

本项目原创实现采用 GNU GPL version 3 only。发布安装 EXE 或免安装 ZIP 时，需要同时提供对应版本的完整源码、必要构建脚本及许可证；本项目打包流程同时生成 `DoubaoVR-v<版本>-source.zip` 与 SHA256，不把私有测试代码、个人配置、开发指令或缓存放入该源码包。

对应源码须与实际二进制相匹配。不要用未来变更后的主分支替代历史发行版源码，也不要把历史无许可证的发行包描述为已经重新打包、验证的 GPL 版本。第三方组件保留其自身许可。

## 构建程序

在 Windows x64 安装 PowerShell 7 和 .NET SDK 8.0.424。解压源码，在源码根目录执行：

```powershell
.\scripts\build.ps1 -PrepareToolchain -SourceBuild
```

原生工具链从固定地址下载并校验 SHA256；只使用 MSVC、Windows SDK、CMake 与 .NET 自带 NuGet 的固定依赖恢复，不要求 Python、Git、vcpkg、Conan 或 npm。已有便携工具链时可省略 `-PrepareToolchain`。

完整可运行目录为 `native-rewrite/out/source-app/`，运行其中的 `DoubaoVR.exe`，同目录 DLL、资源及许可证必须保留。重建程序不包含动效测试类，也不要求私有测试资料。`BUILD-INFO.txt` 明确标识其为源码重建，而非已经完成维护者验收的正式发行版。

## 重建 ZIP 和安装 EXE

安装 Inno Setup 6.7 或更新版本后：

```powershell
.\scripts\package-release.ps1 -SourceBuild -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

默认输出到 `artifacts/source-build/`，包含免安装 ZIP、安装 EXE、对应源码 ZIP 与各自的 SHA256。源码重建模式禁止输出到正式 `releases/` 目录，不改写既有发行包。安装包仍使用原 AppId，不建议把未经自己核验的重建版本覆盖到正在使用的正式安装目录。

普通源码构建以及源码重建不修改系统音频默认设备、不自动注册 SteamVR，也不会默认开启 PICO 麦克风消费。相关功能只由用户在程序设置中明确操作；PICO 开关的既有选择会在正常运行时恢复。

## 正式发布与本地测试

维护者正常使用 `-Publish` 或不带 `-SourceBuild` 的打包入口时，仍必须运行完整本地 GPU、ABI、生命周期和设置动效验收。缺少本地验证资料会拒绝正式发布，不能把源码重建模式当作通过了这些测试。公开源码可独立生成、安装和运行程序，私有测试不是重建所必需的源文件。
