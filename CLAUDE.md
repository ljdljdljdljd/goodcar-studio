# TurntableStudio — CC 前端任务规范

## 项目结构
- `TurntableStudio/ViewModels/MainViewModel.cs` — VM，Services 不得改动
- `TurntableStudio/Views/MainWindow.xaml` — 主界面 XAML
- `TurntableStudio/Views/MainWindow.xaml.cs` — 只有 Dispose，不改
- `TurntableStudio/Services/` — **禁止修改**
- Target: net48, x64, WPF+MVVM

## 当前任务：新增"转盘手动控制"Tab 页

### 功能需求（来自华腾 HMI 界面对照）
新增一个 Tab 页（标签"转盘控制"），包含以下所有控件：

**参数区（顶部，三列横排）**
- 旋转速度：数字输入框，绑定 VM 属性 `TurntableSpeed`（ushort），默认 230
- 每度脉冲数：数字输入框，绑定 `TurntablePulse`（ushort），默认 24690
- 加减速时间（ms）：数字输入框，绑定 `TurntableAccelDecel`（ushort），默认 1000

**写入参数按钮**：点击调用 `WriteParamsCommand`，把三个参数写入设备（调用 TurntableService.Configure）

**目标角度区**
- 目标角度输入框：绑定 `TargetAngle`（double），范围 0–360
- "正转到目标"按钮：绑定 `MoveForwardCommand`
- "反转到目标"按钮：绑定 `MoveReverseCommand`

**手动点动区（模拟 HMI 的手动+/手动-）**
- "手动正转"按钮（按住时正转，松开停止）：绑定 `ManualForwardCommand`
- "手动反转"按钮（按住时反转，松开停止）：绑定 `ManualReverseCommand`
- 实现：Button MouseDown 触发 SetDirection+Start，MouseUp 触发 Stop

**启停区**
- "启动"按钮：绑定 `StartRotationCommand`（使用当前方向启动）
- "停止"按钮：绑定 `StopRotationCommand`

**回零**
- "零点确认"按钮：绑定 `GoHomeCommand`，调用 TurntableService.GoHome()

**方向选择**
- 两个 RadioButton："正转"/"反转"，绑定 `IsForwardDirection`（bool，true=正转）

**状态显示**
- 当前角度显示（只读），绑定已有 `CurrentAngleText`
- 操作日志（只读），复用已有 `LogEntries`

---

## MainViewModel.cs 需要新增的属性和命令

在 VM 中追加（禁止改动已有逻辑）：

```csharp
// ── 转盘手动控制属性 ──────────────────────────────────────────
private ushort _turntableSpeed = 230;
public ushort TurntableSpeed { get => _turntableSpeed; set => Set(ref _turntableSpeed, value); }

private ushort _turntablePulse = 24690;
public ushort TurntablePulse { get => _turntablePulse; set => Set(ref _turntablePulse, value); }

private ushort _turntableAccelDecel = 1000;
public ushort TurntableAccelDecel { get => _turntableAccelDecel; set => Set(ref _turntableAccelDecel, value); }

private double _targetAngle = 90;
public double TargetAngle { get => _targetAngle; set => Set(ref _targetAngle, value); }

private bool _isForwardDirection = true;
public bool IsForwardDirection { get => _isForwardDirection; set => Set(ref _isForwardDirection, value); }

// 命令
public ICommand WriteParamsCommand   { get; }
public ICommand MoveForwardCommand   { get; }
public ICommand MoveReverseCommand   { get; }
public ICommand StartRotationCommand { get; }
public ICommand StopRotationCommand  { get; }
public ICommand GoHomeCommand        { get; }
public ICommand ManualForwardCommand { get; }
public ICommand ManualReverseCommand { get; }
```

命令实现要点（全部要有 try-catch，失败写 AddLog）：

- `WriteParamsCommand`：`_turntable.Configure(TurntableSpeed, TurntablePulse, TurntableAccelDecel)`
- `MoveForwardCommand`：`await _turntable.MoveToAsync(TargetAngle)` — 使用 SDK 的 MoveTo(正转)
  - 实现：先调 `_controller.SetDirection(MovementDirection.Forward)`，再 `_controller.MoveTo(TargetAngle)`
  - **注意**：TurntableService 现在只有 MoveToAsync，需要在 TurntableService 中新增 SetDirectionAndMoveTo(double, MovementDirection) 方法
- `MoveReverseCommand`：方向反转版本
- `StartRotationCommand`：`SetDirection(IsForwardDirection ? Forward : Reverse); Start()`
- `StopRotationCommand`：`Stop()`
- `GoHomeCommand`：`GoHome()`
- `ManualForwardCommand`：`SetDirection(Forward); Start()`（Button MouseDown 触发）
- `ManualReverseCommand`：`SetDirection(Reverse); Start()`（Button MouseDown 触发）
- 手动停止：在 XAML 的 MouseUp 事件绑定 `StopRotationCommand`

---

## TurntableService.cs 需要新增的公开方法（允许修改此文件）

```csharp
public void SetDirection(bool forward)
    => _controller!.SetDirection(forward ? MovementDirection.Forward : MovementDirection.Reverse);

public void Start()
    => _controller!.Start();

public void Stop()
    => _controller!.Stop();

public void GoHome()
    => _controller!.GoHome();

public void Configure(ushort speed, ushort pulse, ushort accelDecel)
    => _controller!.Configure(speed, pulse, accelDecel);

public async Task MoveToWithDirectionAsync(double targetDeg, bool forward)
{
    EnsureConnected();
    _controller!.SetDirection(forward ? MovementDirection.Forward : MovementDirection.Reverse);
    _controller!.WriteTargetAngle((ushort)targetDeg);
    _controller!.Start();
    // 复用停稳轮询逻辑
    await MoveToAsync(targetDeg);
}
```

---

## 验收标准（AC）
1. 新 Tab 页在未连接 PLC 时，所有转盘控制按钮不可点击（`IsEnabled="{Binding PlcConnected}"`）
2. WriteParams 按钮点击后日志显示"参数已写入：速度 XXX / 脉冲 XXXXX / 加减速 XXXX ms"
3. 正转/反转到目标按钮使用 `IsEnabled` 守卫（非拍摄中 + PLC 连接）
4. 手动正/反转：MouseDown 触发转动，MouseUp 触发停止
5. 停止按钮随时可点（只要 PLC 已连接）
6. 零点确认按钮有二次确认 MessageBox（"确认执行回零操作？"）
7. 所有异常都 catch 并 AddLog（Error 级别）
8. 不改动已有拍摄逻辑、Step 引导、连接流程
9. 编译通过（net48 x64）

## 完成后
提交：`git add -A && git commit -m "feat: add turntable manual control tab"`
