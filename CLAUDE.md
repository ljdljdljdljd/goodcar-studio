# GoodCar 影棚拍摄系统 — 前端派单（Claude Code 执行）

## 任务目标

重构 `TurntableStudio/` 下的 WPF 前端，达到**工业级影棚拍摄软件**的交互与视觉标准。
后端 Services 层（CameraService、TurntableService、CaptureOrchestrator、SmokeTest）**不许改动**。

---

## 当前前端状态（已有基础，需重构）

- `ViewModels/MainViewModel.cs`：已有 LogEntry/LogLevel、分步引导状态、角度轮询、彩色日志流、CanStartCapture 守卫（基础完整，可直接复用）
- `Views/MainWindow.xaml`：旧版 210 行，布局需全部重写
- `ViewModels/Converters.cs`：现有三个 Converter，可保留并扩展

---

## 验收标准（AC）

### AC-1 布局结构（1100×720，不可调小）
```
┌────────────────────────────────────────────────────────────────────┐
│  顶部状态栏：PLC状态圆点+文字 ｜ 相机状态圆点+文字 ｜ 右对齐标题  │
├──────────────────────────────────┬─────────────────────────────────┤
│                                  │  ① 分步引导卡（Step 1-4）       │
│   实时取景预览（左 2/3）         │  ② 配置区（PLC IP / 张数 / 目录）│
│   无取景时显示角度大表盘         │  ③ 实时角度大字（xxx.x°）       │
│   （SVG 矢量指针转盘）           │  ④ 进度区（进度条 + xx/72）     │
│                                  │  ⑤ 彩色日志流（近200条）        │
├──────────────────────────────────┴─────────────────────────────────┤
│  底部操作栏：[连接PLC] [连接相机] [实时取景]  ║  [开始/停止拍摄]  │
└────────────────────────────────────────────────────────────────────┘
```

### AC-2 分步引导卡（右侧顶部）
显示操作步骤，高亮当前激活步骤，完成步骤显示✓：
- Step 1：连接 PLC（绑定 `Step1Brush`）
- Step 2：连接相机（绑定 `Step2Brush`）
- Step 3：开启实时取景（绑定 `Step3Brush`）
- Step 4：开始拍摄（绑定 `Step4Brush`）

颜色规范：
- 未激活：`#45475A`（暗灰）
- 当前激活：`#CBA6F7`（紫色）
- 已完成：`#A6E3A1`（绿色）

### AC-3 角度表盘（取景区左侧，取景关闭时显示）
- 画布 300×300 SVG 风格：深色圆盘背景，白色刻度线（每 30° 一大刻度，每 10° 一小刻度）
- 红色指针随 `DialRotation` 属性实时旋转（`RotateTransform` 绑定）
- 盘心大字显示当前角度（绑定 `CurrentAngleText`）
- 外圈标注 0°/90°/180°/270° 四个方位

### AC-4 实时取景区
- 取景开启时显示相机 JPEG 帧（绑定 `LiveViewImage`）
- 取景关闭时显示角度表盘（见 AC-3）
- 两者通过 `LiveViewActive` DataTrigger 切换 Visibility

### AC-5 配置区
- PLC IP 输入框（绑定 `PlcIp`，连接中置灰）
- 拍摄张数 ComboBox（72张/36张，绑定 `SelectedShotCount`，拍摄中 `IsEnabled=False`）
- 步进角只读显示（绑定 `StepAngle`，自动计算 "5.0°/张"）
- 保存目录输入框 + [浏览] 按钮

### AC-6 进度区
- 大字进度（绑定 `ProgressText`，字号 28，绿色加粗）
- ProgressBar（绑定 `ProgressCurrent` / `ProgressTotal`，高度 10px，绿色填充）

### AC-7 彩色日志流
使用 `ItemsControl` 或 `ListBox`（不用 TextBox），绑定 `LogEntries`（`ObservableCollection<LogEntry>`）
每条日志一行：`[时间] 消息` 颜色由 `LogEntry.Color`（已是 Brush 类型）直接绑定 `Foreground`
- 成功：`#A6E3A1`（绿）
- 错误：`#F38BA8`（红）
- 警告：`#F9E2AF`（黄）
- 普通：`#A6ADC8`（灰）

### AC-8 底部操作栏
四个按钮从左到右，视觉层级分明：
- `[连接PLC]`：文字绑定 `ConnectPlcLabel`，点击 `ConnectPlcCommand`
- `[连接相机]`：文字绑定 `ConnectCameraLabel`，点击 `ConnectCameraCommand`
- `[实时取景]`：文字绑定 `LiveViewLabel`，点击 `ToggleLiveViewCommand`
- `[开始/停止拍摄]`（突出显示）：文字绑定 `StartStopLabel`，背景色绑定 `StartStopColor`，点击 `StartStopCommand`，`IsEnabled` 绑定 `CanStartCapture`（false 时置灰）

### AC-9 顶部状态栏
- PLC 状态圆点（12px，颜色绑定 `PlcStatusColor`）+ 文字绑定 `PlcStatusText`（"已连接"/"未连接"）
- 相机状态同上（`CameraStatusColor` / `CameraStatusText`）
- 右对齐版本号："GoodCar 影棚拍摄系统 v1.0"

---

## 颜色令牌（Catppuccin Mocha 深色主题）

```
背景底色：     #1E1E2E
次级背景：     #181825
卡片背景：     #313244
边框线：       #45475A
正文文字：     #CDD6F4
次要文字：     #A6ADC8
品牌紫：       #CBA6F7
成功绿：       #A6E3A1
错误红：       #F38BA8
警告黄：       #F9E2AF
```

---

## 工程要求

1. **只改 `Views/MainWindow.xaml` 和 `ViewModels/Converters.cs`**，不动其他文件
2. `Converters.cs` 中：
   - 保留现有三个 Converter（`BoolToColorConverter`、`BoolToTextConverter`、`InvertBoolConverter`）
   - 新增 `BrushToColorConverter`（将 `Brush` 转为 `Color` 供 `Ellipse.Fill` 使用，或直接用 `SolidColorBrush`）
3. `MainWindow.xaml.cs` 只保留 `OnClosed` 中的 Dispose 调用，不加其他逻辑
4. 所有绑定路径必须与 `MainViewModel.cs` 中的属性名完全一致（见上方 AC 中的属性名清单）
5. SVG/表盘用纯 XAML 实现（`Canvas` + `Ellipse` + `Line` + `RotateTransform`），不引入第三方库
6. 完成后运行 `git add -A && git commit -m "feat(ui): rewrite MainWindow with dial, step-guide, color-log, improved layout"`

---

## 属性速查表（MainViewModel 已有，直接绑定）

| 属性名 | 类型 | 用途 |
|--------|------|------|
| `PlcConnected` | bool | PLC 连接状态 |
| `CameraConnected` | bool | 相机连接状态 |
| `IsCapturing` | bool | 拍摄进行中 |
| `LiveViewActive` | bool | 取景开启中 |
| `PlcStatusText` | string | "已连接"/"未连接" |
| `PlcStatusColor` | Brush | 绿/红 |
| `CameraStatusText` | string | "已连接"/"未连接" |
| `CameraStatusColor` | Brush | 绿/红 |
| `ConnectPlcLabel` | string | "连接PLC"/"断开PLC" |
| `ConnectCameraLabel` | string | "连接相机"/"断开相机" |
| `LiveViewLabel` | string | "开启取景"/"关闭取景" |
| `StartStopLabel` | string | "开始拍摄"/"停止拍摄" |
| `StartStopColor` | Brush | 绿/红 |
| `CanStartCapture` | bool | 两端已连接且未拍摄 |
| `CanChangeShotCount` | bool | !IsCapturing |
| `PlcIp` | string | PLC IP 输入 |
| `SaveFolder` | string | 保存目录 |
| `SelectedShotCount` | int | 36 或 72 |
| `ShotCountOptions` | ObservableCollection<int> | {72, 36} |
| `StepAngle` | string | "5.0° / 张" |
| `CurrentAngle` | double | 当前转盘角度 |
| `CurrentAngleText` | string | "123.4°" |
| `DialRotation` | double | 表盘指针旋转角度 |
| `ProgressCurrent` | int | 已拍张数 |
| `ProgressTotal` | int | 总张数 |
| `ProgressText` | string | "12 / 72" |
| `ProgressPercent` | double | 0-100 |
| `LiveViewImage` | BitmapImage? | 取景帧 |
| `LogEntries` | ObservableCollection<LogEntry> | 日志流 |
| `Step1Brush`~`Step4Brush` | Brush | 分步引导颜色 |
| `ConnectPlcCommand` | ICommand | 连接/断开 PLC |
| `ConnectCameraCommand` | ICommand | 连接/断开相机 |
| `StartStopCommand` | ICommand | 开始/停止拍摄 |
| `ToggleLiveViewCommand` | ICommand | 切换实时取景 |
| `BrowseFolderCommand` | ICommand | 选择保存目录 |
