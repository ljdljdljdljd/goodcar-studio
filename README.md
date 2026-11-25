# GoodCar 影棚拍摄系统

Sony A7M5 + 华腾转盘自动化拍摄上位机，支持 72 张（5°/张）/ 36 张（10°/张）一键切换。

---

## 目录结构

```
turntable_studio/
├── CMakeLists.txt                   # SonyCrBridge.dll C++ 工程（cmake）
├── TurntableStudio.sln              # Visual Studio 2022 解决方案
├── include/
│   └── SonyCrBridge.h               # C 导出函数声明（11个接口）
├── src/
│   └── SonyCrBridge.cpp             # 桥接 DLL 实现（Sony SDK C-Wrapper，已通过审计）
├── libs/                            # 运行时 DLL 存放目录（构建后自动填入）
│   ├── Huateng.Turntable.Sdk.dll    # 华腾转盘 SDK（厂商提供，已有）
│   ├── SonyCrBridge.dll             # 本工程编译产物（步骤二编译后复制到此）
│   └── Cr_Core.dll                  # 索尼官方运行时（CrSDK 包内提取）
└── TurntableStudio/
    ├── TurntableStudio.csproj
    ├── App.xaml / App.xaml.cs
    ├── Services/
    │   ├── CameraService.cs         # P/Invoke 封装（11个导出函数，cdecl/ANSI）
    │   ├── TurntableService.cs      # 转盘连接与停稳确认轮询
    │   ├── CaptureOrchestrator.cs   # 步进精拍编排（重试3次、manifest.json）
    │   └── SmokeTest.cs             # 命令行自检工具（8项验证）
    ├── ViewModels/
    │   ├── MainViewModel.cs         # MVVM 主逻辑，含 72/36 切换锁定
    │   └── Converters.cs            # bool→Color/Text/Invert
    └── Views/
        ├── MainWindow.xaml          # 深色主题 UI
        └── MainWindow.xaml.cs
```

---

## Export ABI

The C ABI and calling convention are declared in `include/SonyCrBridge.h`. All exports use `extern "C"` and `__cdecl`:

1. `CrBridge_Initialize`
2. `CrBridge_GetDeviceCount`
3. `CrBridge_GetDeviceDescriptor`
4. `CrBridge_Connect`
5. `CrBridge_Disconnect`
6. `CrBridge_StartLiveView`
7. `CrBridge_StopLiveView`
8. `CrBridge_GetLiveViewJpeg`
9. `CrBridge_FreeBuffer`
10. `CrBridge_TakePicture`
11. `CrBridge_LastError`

Operations return `CrBridge_Ok` (`0`) or a negative `CrBridgeResult`; `GetDeviceCount` returns the count or a negative error. A zero connect timeout uses 15 seconds; a zero live-view timeout uses 1 second; a zero shutter hold uses 100 ms. The returned JPEG allocation must be released with `CrBridge_FreeBuffer`.

---

## Windows 编译步骤

### 前置条件

- Visual Studio 2022（含 C++ 桌面开发 + .NET 桌面开发组件）
- CMake 3.21+
- 索尼 CrSDK v2.02.00（解压后记下 RemoteCli 路径，下文称 `<CRSDK_DIR>`）
- .NET SDK 6.0+

---

### 步骤一：编译 SonyCrBridge.dll

在 **Visual Studio 开发者命令提示符（x64）** 中执行：

```cmd
cd turntable_studio

cmake -S . -B build ^
      -G "Visual Studio 17 2022" -A x64 ^
      -DCRSDK_ROOT="<CRSDK_DIR>"

cmake --build build --config Release
```

`<CRSDK_DIR>` 须包含 `app/CRSDK/CameraRemote_SDK.h` 和 `external/crsdk/Cr_Core.lib`。

编译成功后 `build\Release\` 下生成：
- `SonyCrBridge.dll`
- `Cr_Core.dll`（cmake 自动复制）
- `CrAdapter\`（索尼适配器目录）

**将以上全部复制到 `libs\` 目录。**

---

### 步骤二：编译上位机

```cmd
dotnet build TurntableStudio\TurntableStudio.csproj ^
    -c Release -r win-x64 --self-contained false
```

输出目录：`TurntableStudio\bin\Release\net48\win-x64\`

---

### 步骤三：SmokeTest 自检

插上转盘 USB + 相机 USB，在输出目录执行：

```cmd
:: 默认 PLC IP 192.168.1.111
TurntableStudio.exe --smoketest

:: 指定 PLC IP
TurntableStudio.exe --smoketest 192.168.1.100
```

8 项全 PASS 后再进行正式拍摄。报告保存至 `smoketest_<时间戳>.txt`。

---

### 步骤四：正式拍摄

```cmd
TurntableStudio.exe
```

界面操作顺序：连接PLC → 连接相机 → 选张数（72/36）→ 选保存目录 → 开始拍摄。

拍摄完成后保存目录下生成 `manifest.json`（含每张的目标角度、实测角度、文件名、大小、SHA256、耗时ms）。

---

## 相机设置（Sony A7M5）

菜单确认以下三项：
- **传输 & 遥控 → USB 连接模式** → `PC 遥控（RAW）`
- **传输 & 遥控 → PC 遥控功能** → 开启
- **传输 & 遥控 → 保存目标** → `PC` 或 `PC+相机`

USB 推荐使用 USB 3.2 Type-A to Type-C。

---

## 常见问题

| 现象 | 原因 | 处理 |
|------|------|------|
| `DllNotFoundException: SonyCrBridge.dll` | DLL 不在 exe 同目录 | 确认 `libs\` 内文件已复制到输出目录 |
| `发现 0 台相机` | 相机 USB 模式不对 | 相机菜单设置 PC 遥控后重插 USB |
| PLC 连接超时 | IP 错误或网络不通 | 确认转盘控制器 IP，同子网直连 |
| 文件大小校验失败（<20KB） | 相机画质设置过低 | 推荐 L Fine JPEG |
| 转盘停稳超时 | 负载过重或步进角太小 | 检查转盘硬件，可调大 `stabilizeMs`（默认300ms）|

---

## 技术规格

- **目标平台**：Windows 10/11 x64
- **框架**：.NET Framework 4.8，WPF，MVVM
- **C++ 标准**：C++17
- **相机 SDK**：Sony Camera Remote SDK v2.02.00
- **转盘 SDK**：华腾转盘 SDK（Huateng.Turntable.Sdk.dll）
- **默认拍摄**：72 张（每 5° 一张），可切换 36 张（每 10° 一张）
- **停稳容差**：±0.5°，确认时间窗口 300ms
- **文件校验**：落盘大小 >20KB + SHA256

