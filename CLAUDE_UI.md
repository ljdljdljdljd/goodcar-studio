# GoodCar 影棚拍摄系统 UI 升级任务书 v3.0

## 目标
对 TurntableStudio WPF 前端做视觉与体验升级，不改 Services 层和 ViewModel 逻辑层。

## 禁止改动的文件（硬红线）
- TurntableStudio/Services/*.cs（全部不动）
- TurntableStudio/ViewModels/MainViewModel.cs（只允许新增属性/命令，不改现有逻辑）
- src/SonyCrBridge.cpp / include/SonyCrBridge.h
- CMakeLists.txt / .github/workflows/

---

## 改动 1：修复 ComboBox 文字不可见问题

**现象**：张数选择下拉框（36张/72张），选中项文字在深色背景下几乎看不见。

**修复**：在 MainWindow.xaml Window.Resources 里加一个 ComboBox 全局 Style，自定义 ControlTemplate：
- SelectionBox 前景色：`#CDD6F4`（TextPrimary）
- 下拉列表背景：`#313244`（BgCard）
- 下拉项 Foreground：`#CDD6F4`
- 下拉项 Hover 背景：`#45475A`（Border）
- 箭头图标颜色：`#CDD6F4`

---

## 改动 2：照片预览缩略图区域

**需求**：每拍完一张，左侧面板实时显示该张缩略图 + 编号 + 角度，拍摄中持续追加。

### VM 侧（MainViewModel.cs 新增，不改现有代码）

新增 DTO：
```csharp
public class ShotThumbnailItem
{
    public int ShotNumber { get; set; }         // 001, 002...
    public string AngleText { get; set; } = ""; // "35.0°"
    public BitmapImage? Thumbnail { get; set; }  // 缩略图
    public string NumberText => $"{ShotNumber:D3}";
}
```

新增属性：
```csharp
public ObservableCollection<ShotThumbnailItem> ShotThumbnails { get; } = new();
public ShotThumbnailItem? LatestShot { get; private set; }  // 最后一张，用于大图预览
```

新增方法（供 CaptureOrchestrator Progress 回调调用）：
```csharp
public void AddShotThumbnail(int shotNumber, double angle, string filePath)
{
    if (!File.Exists(filePath)) return;
    var bmp = new BitmapImage();
    bmp.BeginInit();
    bmp.UriSource = new Uri(filePath);
    bmp.DecodePixelWidth = 160;  // 缩略图宽度
    bmp.CacheOption = BitmapCacheOption.OnLoad;
    bmp.EndInit();
    bmp.Freeze();
    var item = new ShotThumbnailItem { ShotNumber = shotNumber, AngleText = $"{angle:F1}°", Thumbnail = bmp };
    UI(() => { ShotThumbnails.Add(item); LatestShot = item; OnPropertyChanged(nameof(LatestShot)); });
}
```

在 `_orchestrator.Progress` 订阅里调用 `AddShotThumbnail`，在 `StartStopAsync` 开始时清空 `ShotThumbnails`。

### XAML 侧

左侧面板（Grid Column=0）改为三层叠加（Grid）：
1. **实时取景层**（LiveViewActive=True 时显示，不变）
2. **角度表盘层**（LiveViewActive=False 且 ShotThumbnails 为空时显示，不变）
3. **缩略图预览层**（ShotThumbnails.Count > 0 时显示）：
   - 右侧 1/4 宽度固定为 "最新照片大图预览"（绑定 LatestShot.Thumbnail，Stretch=Uniform）
   - 左侧 3/4 为 WrapPanel 缩略图流（ScrollViewer 包裹）
   - 每格样式：
     ```
     ┌──────────────┐
     │   缩略图      │  ← 160×120
     │ 001  35.0°   │  ← 底部半透明遮罩，白字
     └──────────────┘
     ```
     用 Border + Image + Grid 叠加实现
   - 缩略图格子尺寸：160×140（含底部标签区20px）
   - 间距：4px
   - 最新一张有高亮边框（`#CBA6F7` 2px）

---

## 改动 3：版本号

MainWindow.xaml 第 100 行：`GoodCar 影棚拍摄系统 v1.0` → `GoodCar 影棚拍摄系统 v3.0`

---

## 改动 4：进度区增强

现有进度区（⑤）加两行：
- 当前角度：绑定 `CurrentAngleText`（已有属性）
- ��拍/总张：已有 ProgressText

布局：
```
拍摄进度                           [5 / 72]
████████░░░░░░░░░░░░░░░░  进度条
当前角度：  25.0°
```

---

## 颜色令牌速查
| Token | 值 |
|---|---|
| BgBase | #1E1E2E |
| BgSecondary | #181825 |
| BgCard | #313244 |
| Border | #45475A |
| TextPrimary | #CDD6F4 |
| TextSecondary | #A6ADC8 |
| BrandPurple | #CBA6F7 |
| SuccessGreen | #A6E3A1 |
| ErrorRed | #F38BA8 |
| WarningYellow | #F9E2AF |

---

## 验收标准
1. 编译通过（`dotnet build TurntableStudio/TurntableStudio.csproj -c Release`）
2. ComboBox 下拉文字清晰可见（白色字体，深色背景）
3. `ShotThumbnailItem` 类存在，`ShotThumbnails` 是 `ObservableCollection`
4. `AddShotThumbnail` 方法存在且在 Progress 回调里被调用
5. XAML 左侧有缩略图 WrapPanel 绑定 `ShotThumbnails`
6. 版本号显示 v3.0
7. 不改动任何 Services 文件
