# GoodCar 影棚 v2.9 联动审计

**总体结论：REJECT。** 1518 写保护在本次工作树和随仓华腾 DLL 中通过静态核查；但当前版本可能在未起动或未停稳时拍照、重试时多走一步、把旧下载计入新角度，以及缺片后仍报成功。不能用于生产联动。

审计覆盖指定八个文件、整库应用调用点和随仓 `libs/Huateng.Turntable.Sdk.dll` 的相关 IL。下文行号基于**未应用候选补丁**的当前工作树。未修改业务源码，也未覆盖审计前已有的 `CLAUDE.md`、`include/SonyCrBridge.h` 改动。

## 八维结果

| # | 判定 | 证据与影响 |
|---|---|---|
| 1. 1518 安全 | **PASS** | `TurntableStudio/Services/TurntableService.cs:106` 正确映射 `Forward`；`TurntableStudio/Services/TurntableService.cs:112` 只代理 SDK `Start()`；`TurntableStudio/Services/CaptureOrchestrator.cs:115` 每次下发步进前显式设正转。应用源码对 SDK `MoveTo` / `WriteTargetAngle` 零调用；`CLAUDE.md:83`、`CLAUDE.md:118` 仍有违反约束的示例，不能照抄。见下方 IL 证据。 |
| 2. 步进完整性 | **FAIL** | `TurntableStudio/Services/CaptureOrchestrator.cs:93` 首张标签是 `startAngle`，实际首张在 `:117` 脉冲之后；从 0° 正常步进 5° 时，首张实测 5° 却命名为 0°。`:98` 把方向、脉冲、运动等待、拍照和文件验证放在同一个重试体内；第二次尝试会再发脉冲。`:82` 的 5°/10° 只用于计算标签，`:151` 的实测值不约束放行。`TurntableStudio/Services/TurntableService.cs:136` 也忽略所谓目标角度，`TurntableStudio/ViewModels/MainViewModel.cs:470` 只等到下发操作结束就报告到达。 |
| 3. 停稳检测 | **FAIL** | `TurntableStudio/Services/CaptureOrchestrator.cs:120` 和 `:134` 在 5 秒未起动、30 秒未停稳时都直接落到 `:151`、`:159`，不报错、不停机。起动只用绝对差，反转也算起动。`:138` 的 0.3° 低于 SDK 回读分辨率：等价于观察若干相同整数角度，低速或停顿可被误判。`TurntableStudio/Services/TurntableService.cs:26` 配置 1500 ms I/O 超时、3 次读重试；IL 显示最多 4 次读，单个读调用在连续超时时就可能超过 5 秒。5 秒是否覆盖真实启动和回读延迟，不能靠源码判定。 |
| 4. 拍摄时序 | **FAIL** | `TurntableStudio/Services/CaptureOrchestrator.cs:159` 在两个循环退出后无条件拍照，未确认成功停稳。30 秒是原生下载等待预算；`:162`、`:215` 还可能再等 30 秒，因此不是单张总上限。`src/SonyCrBridge.cpp:194` 不校验请求、类型或空文件名；`:803` 仅清全局标志，`:857` 仅等该标志。A 超时后，A 的迟到回调可在 B 清标志后唤醒 B，B 会将 A 的图改名为 B。`src/SonyCrBridge.cpp:163` 会通知断线，但原等待谓词不看断线或回调错误。`TurntableStudio/Services/CameraService.cs:47` 按 ANSI 封送路径，`include/SonyCrBridge.h:77` 却规定 UTF-8，含中文路径存在失配。 |
| 5. 文件落盘 | **FAIL** | `TurntableStudio/Services/CaptureOrchestrator.cs:221` 仅看存在并固定延迟 200 ms，不证明写完；`:171` 的 20 KiB 门槛确实拒绝 0 字节，但缺失和过小在 `:166`、`:174` 都 `continue`，最后一次也不抛错。`:210` 无 `records.Count == shotCount` 校验，缺片仍写 manifest 并宣告完成。`src/SonyCrBridge.cpp:868` 可在空下载路径上返回成功，`:880` 不检查改名结果。 |
| 6. 异常与清理 | **FAIL** | `TurntableStudio/Services/CaptureOrchestrator.cs:85` 把读角异常静默降级为 0°；`TurntableStudio/ViewModels/MainViewModel.cs:425` 的后台回读也吞异常。`TurntableStudio/Services/CaptureOrchestrator.cs:98`、`:200` 对拍照异常是**最多 3 次尝试（含首次）**，不是初次加 3 次；与机械脉冲耦合。`:199`、`:204` 取消/终止没有统一 Stop 清理。`TurntableStudio/Services/TurntableService.cs:84` 的旧且当前未调用封装吞掉 SDK `Stop()` 异常，其后再次读角还可能覆盖原超时；`TurntableStudio/ViewModels/MainViewModel.cs:499` 的手动停止则有日志，不是同一路径。 |
| 7. 相机 / LiveView | **FAIL** | `TurntableStudio/ViewModels/MainViewModel.cs:285` 断开相机不关 LiveView 定时器；拔线后也不重置 UI 连接态。`:388` 的 100 ms `System.Timers.Timer` 没有单飞或排空；`:398` 可重叠取帧。`src/SonyCrBridge.cpp:632` 的互斥锁仅保护本次原生调用；`:662` 下一次可扩容/覆盖同一个 vector，`TurntableStudio/Services/CameraService.cs:139` 此时可能仍在 `Marshal.Copy`，导致坏帧或悬空指针读取。`src/SonyCrBridge.cpp:570` 在持回调所用的 `eventMutex` 时做连接超时释放，若 SDK 等待回调返回则可卡住；`:604` 不查设备释放状态，托管 `TurntableStudio/Services/CameraService.cs:123` 也忽略断开返回码。 |
| 8. 取消与并发 | **FAIL** | 延时传 token，但 `TurntableStudio/Services/CaptureOrchestrator.cs:159` 的同步 P/Invoke、`:258` 的 manifest 写入、同步 PLC I/O/哈希不受取消；`TurntableStudio/ViewModels/MainViewModel.cs:355` 从 WPF UI 上下文启动，长调用可阻塞按钮。`TurntableStudio/Views/MainWindow.xaml:118` 绑定到 `CanStartCapture`，而 `TurntableStudio/ViewModels/MainViewModel.cs:224` 拍摄期间必为假；同一按钮还被 `:576` 的 `AsyncRelayCommand` 锁拒绝，`:316` 的停止分支从 UI 不可达。`:458`、`:493`、`:502`、`:513`、`:522` 的写参数/启停/回零/点动及 `:264`、`:285` 的设备断开未与联动互斥。`TurntableStudio/Views/MainWindow.xaml.cs:13` 的鼠标事件直接 `Execute`，也未捕获鼠标来保证松开时停止。`:28` 关闭窗口直接 `Dispose`，`TurntableStudio/ViewModels/MainViewModel.cs:557` 只释放 CTS、不取消也不等待联动，随后即释放设备。 |

## 华腾 DLL 核验

审计制品：`libs/Huateng.Turntable.Sdk.dll`，SHA-256 `cc35439764daedf6bee04d0306625e2291c280c453b917ed7b94c33875859973`。项目引用对应 `TurntableStudio/TurntableStudio.csproj:13`。

- `Start`（RVA `0x2709`）仅 `ldc.i4 3272 → call PulseCoil → ret`。`PulseCoil`（RVA `0x27a3`）对同一线圈写 `true → Sleep → false`，没有寄存器写。`Stop` 为线圈 3278，`GoHome` 为线圈 3095，`SetDirection` 只写线圈 3874。
- `Configure` 只下发速度/脉冲/加减速寄存器 1512/1514/1516。`WriteTargetAngle`（RVA `0x26a7`）才写 1518；SDK 自身的 `MoveTo` 可调用它，应用没有进入该链。应用内的 `MoveToAsync`、`MoveToWithDirectionAsync` 是不同的本地包装名，不是 SDK API 调用。
- `ReadAngleDegrees`（RVA `0x2458`）读取 1522，`RawToDegrees`（RVA `0x3a24`）做有符号整数转换后才正规化到一圈，没有小数缩放。`ReadStatus`（RVA `0x2614`）也未给出运动/到位状态位；不可把其配置速度字段当作当前速度。
- PASS 限于源码与该 DLL；尚未对生产 PLC/HMI 抓包，不能据此声称物理 1518 值或现场步进模式已验证。

## FAIL 修复代码与落点

`AUDIT-v2.9.patch:1` 是对当前工作树的精确 unified diff，包含以下修改，**未应用**。可先以 `git apply --check AUDIT-v2.9.patch` 核验当前版本，再评审。托管与 native 必须一起构建发布，不能只替换一侧；保留旧 `CrBridge_TakePicture` 导出并增加带取消的 `CrBridge_TakePictureEx`。

| 修复 | 原文件落点 | 候选代码行为 |
|---|---|---|
| 2、3、6：运动只提交一次 | `TurntableStudio/Services/CaptureOrchestrator.cs:58`、`:98`、`:120`；`TurntableStudio/Services/TurntableService.cs:52`、`:118`、`:136` | 每张 `SetDirection(true)` / `StartRotation()` 在文件重试之外；检查正向起动、PLC 停稳反馈、连续稳定角度、本步 5°/10° 及相对整圈目标。首张目标为 `start + step`。任一运动失败、超时、取消统一尝试 Stop 和确认静止，报告原异常与清理异常。移除不会真正按目标定位的封装及误导性 UI，不增添任何写角指令。 |
| 4：一张对应一请求 | `src/SonyCrBridge.cpp:91`、`:194`、`:781`；`include/SonyCrBridge.h:77`；`TurntableStudio/Services/CameraService.cs:46`、`:147` | 每次分配独立目录和 SDK 文件前缀；只接纳匹配本次前缀、目录及 JPEG 扩展名的回调，原生等待识别取消/断线/错误。提交后结果不确定时锁住会话，要求重连；不得盲目补拍。确认本次下载后才允许对文件错误最多 3 次同角度尝试。路径改用 UTF-8。 |
| 5：落盘和整轮成功 | `src/SonyCrBridge.cpp:868`；`TurntableStudio/Services/CaptureOrchestrator.cs:161`、`:210`、`:240` | 原生检查文件非空及改名状态；托管用同一文件句柄检查至少 20 KiB、计算可取消的哈希。只有通过才发布到最终文件名。重试耗尽即报错；实际张数精确匹配才发布 manifest。每轮独立目录，不用旧文件填补失败。 |
| 7：断开和帧生命周期 | `src/SonyCrBridge.cpp:570`、`:589`、`:627`；`include/SonyCrBridge.h:45`；`TurntableStudio/Services/CameraService.cs:123`；`TurntableStudio/ViewModels/MainViewModel.cs:285`、`:370` | 停止定时器并等进行中的取景退出后才断开；单飞调度，切换和断线清理画面；原生帧存储改为调用线程独享，托管在同一线程立即复制。连接超时释放不再持有回调锁，设备与 SDK 释放错误上报。 |
| 8：互斥、取消、关闭 | `TurntableStudio/ViewModels/MainViewModel.cs:224`、`:241`、`:314`、`:452`、`:557`；`TurntableStudio/Views/MainWindow.xaml:118`、`:393`；`TurntableStudio/Views/MainWindow.xaml.cs:13`、`:28` | WPF 串行持有设备操作权；开始/取消用独立的同步入口触发异步任务，不受长命令运行锁拦截。联动中的转盘停止按钮请求取消；点动捕获鼠标、丢失捕获同样收尾。关闭先取消、等待任务及轮询排空，再释放设备。SDK 阻塞 I/O 不能被 `CancellationToken` 抢占；原生下载等待按 50 ms 观察取消，实际时延仍受同步 SDK 调用制约。 |

### 应用前必须补齐的现场契约

1. **PLC 停稳反馈与 5°/10° 模式。** 仓库无可信状态位地址；补丁不猜地址。默认 XAML 仍构造无反馈的 `MainViewModel`，因此拍摄预检会明确报错且**不发脉冲**。现场应把已验收、只读的“机械已停稳/允许曝光”反馈接入 `MainViewModel(Func<bool>? readPlcIdle)`；验证反馈无陈旧值，超时、断线或 PLC 报警时返回失败。选择或联锁 PLC 已有 5°/10° 步进模式，不通过 1518 切换。候选的 5 秒、30 秒、最大采样间隙 300 ms 与 500 ms 稳定窗是待整机确认的参数，不是已测指标。验证正转反馈角度递增以及 359°→0° 的编码行为。
2. **Sony 单张 JPEG 合同。** 用实际相机/固件验证 `SetSaveInfo` 的逐拍路径和前缀何时绑定到文件，`OnCompleteDownload` 给出 basename 或完整路径，且回调后文件可读；验收使用单张、仅 JPEG、下载至 PC 模式。若 SDK 不保留逐拍标识或会把前一张按新设置下载，不能放宽匹配条件；需使用 SDK 可验证的关联标识或会话边界。还应验证断线重连时 `ReleaseDevice` 的回调退出契约。30 秒应按现场 USB/图质/存储条件测量，而不是简单加长后自动重拍。
3. **停机确认与硬件保护。** `Stop` 是 PLC 指令，不等于独立急停。断网后任何软件停止都可能失败。保持物理急停/安全联锁，失败日志不得作为电机已停止的证明。

## 验证记录与放行门槛

- 已跑：整库源码搜索、华腾 DLL IL 核验、`git apply --check AUDIT-v2.9.patch`、候选 XAML XML 解析、候选 C++17 非 Windows 分支语法检查。使用模拟 SDK 验证：正确回调、旧回调先到仍选本张、匹配回调但缺文件、超时后不允许再次下发快门、取消、断线。
- 未跑：Windows x64/MSVC、.NET Framework 4.8 WPF 构建及真实设备运行。本地模拟不能确认 Sony 文件命名/回调时机、PLC 地址、机械停稳或整圈角度。
- 放行：两档各跑完整一轮，核对每张前方向与**恰好一次** 3272 脉冲，实测角度、命名、哈希、`savedCount` 一一对应。抓包确认没有对 1518 的写入；注入未起动、持续运动、读超时、迟到回调、文件缺失/过小、Stop 失败、USB 拔插、关闭窗口及并发手动命令。故障必须停止后续脉冲、快门或成功发布，且不能出现错配、缺片报成功或设备提前释放。
