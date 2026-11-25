# 页面锁定（Page Lock / 参照位）实现规划

> 状态：**已实现（构建 0 警告 0 错误，新增单测 15 例通过；人工 GUI 验证待进行）**
> 目标仓库：`Sisferia/NeeView`（fork of `neelabo/NeeView`，分支 `master`）
> 前置：`design/adaptive-page-mode.md`（自适应显示已实现并提交）
> 影响范围：`NeeView/MainView`、`NeeView/PageLock`、`NeeView/PageFrames`、`NeeView/ViewContents`、`NeeView/Command`、`NeeView/Menu`、`NeeView/AddressBar`、`NeeView/Languages`
> 本文中 `文件:行号` 均基于实现时实测代码，行号仅作导航锚点。实现记录见 **附录 B**。

---

## 0. 结论摘要（TL;DR）

- **目标**：在双页显示 / 自适应显示模式下，用户可以“锁定”当前画面中的**某一页**。
  锁定后，该页固定在主视图一侧的**参照位（pane）**中，翻页、跳页码、切换书籍都不再影响它；
  其余未锁定的页仍在缩小后的主视图区域里**按现有逻辑正常翻页**（双页仍是双页成组，自适应仍按宽度重新装填）。
- **交互核心**：锁定是“针对某一页”的操作，双页/自适应会同时显示多页，所以必须解决**目标页指定**。
  - 右键点页 → 上下文菜单「页面锁定」（精确指定）；
  - `Ctrl+L` → 锁定鼠标所指的页（鼠标不在页面上时锁定当前页）；
  - 地址栏锁形按钮 → 状态显示 + 一键锁定/解除；
  - 菜单栏「页面」→「页面锁定」→ 勾选状态。
- **跨书保持**：换书后锁定页**继续显示**。实现上在锁定瞬间对已加载的页面位图做**快照**（含自动旋转），
  此后不再依赖原书籍/压缩包（旧书关闭时其 `Book` 会被 `Book.Dispose()` 释放，见 `BookHub.cs:751`）。
- **自动解除**：切换到单页显示时解除（决策③）；书籍关闭（无当前书）时解除。
- **单页互斥**：同一时刻只锁定一页，锁定新页会替换旧页并提示。
- **已知取舍**：锁定页仍然属于正常翻页序列（参照位模型不改变翻页逻辑），所以翻到该页时画面会同时出现两张
  （主视图 + 参照位）；这是“B 参照位”模型的自然结果，不是 bug。
- **纯算法可单测**：参照位宽度计算、左右侧判定、旋转后宽高比等抽到 `PageLockTools`，新增单元测试。

---

## 1. 需求与决策

### 1.1 需求原文

> 在双页或自适应显示模式下，增加一个功能：用户可指定当前正在显示的某个单页，锁定该页，
> 而后进行翻页、跳转页码、切换到其他书籍时，该页保持不变，其他未锁定的页按照现有逻辑正常切换。

### 1.2 已确认决策（2026-11-25）

| 编号 | 决策 | 说明 |
|---|---|---|
| D1 | **B 参照位** | 锁定页固定显示在主视图一侧的参照位；主视图剩余区域继续按现有双页/自适应逻辑翻页 |
| D2 | **跨书保持显示** | 切换书籍后锁定页仍显示（使用快照，不依赖旧书是否仍打开） |
| D3 | **切到单页显示时自动解除** | 单页模式不允许锁定；切到单页模式立即解除 |
| D4 | 单页互斥 | 同时只锁定一页；锁定新页 = 替换旧页 |
| D5 | 交互入口 | 右键菜单 + 快捷键（默认 `Ctrl+L`）+ 菜单栏 + 地址栏按钮 + 参照位关闭按钮 |
| D6 | 不持久化 | 锁定状态只存在于本次运行，重启后清除 |
| D7 | 不新增设置项 | 参照位宽度上限等使用内部常量，先不暴露到设置界面 |

### 1.3 规则化

| 编号 | 规则 |
|---|---|
| R1 | 仅在**双页显示 / 自适应显示**模式下可锁定；单页模式下命令禁用，且切换到单页时自动解除 |
| R2 | 锁定目标 = 右键点中的页 / 鼠标所指的页 / 当前页（`Book.CurrentPage`），依次回退 |
| R3 | 锁定页显示在**参照位**：宽度 = 页面宽高比 × 主视图高度，上限为主视图宽度的 50%；图像按 Uniform 适配 |
| R4 | 参照位位置：跟随锁定页在原画面中的左右侧（页面在帧内偏左 → 左侧，否则右侧；纵向堆叠时默认右侧） |
| R5 | 主视图区域因参照位而缩小，其内部仍按当前页面显示模式的现有逻辑布局、翻页 |
| R6 | 翻页、跳转页码、页面滑块、幻灯片、循环浏览**不影响**锁定页 |
| R7 | 切换书籍后锁定页保持（快照），参照位上显示来源书籍名 |
| R8 | 切到单页显示 / 书籍关闭 / 媒体书籍 → 自动解除并提示（单页模式时） |
| R9 | 再次对锁定页执行锁定命令 = 解除；锁定其他页 = 替换 |
| R10 | 页面内容尚未加载完成（非图像内容、加载中）时不允许锁定，提示失败原因 |
| R11 | 锁定页快照包含当时的自动旋转角度；之后视图的缩放/旋转/特效变化不影响参照位 |
| R12 | 不破坏现有行为：单页/双页/自适应模式、书籍锁定、书签、页面列表、脚本 API 保持兼容 |

### 1.4 验收清单

- [ ] 双页模式下右键某页 →「页面锁定」，该页出现在侧边参照位，主视图仍按双页翻页
- [ ] 自适应模式下锁定其中一页，剩余宽度按自适应规则重新装填
- [ ] `Ctrl+L`：鼠标悬停在某页上时锁定该页；鼠标移出主视图时锁定当前页
- [ ] 锁定后翻页/跳页/拖动页面滑块，锁定页不变
- [ ] 切换到其他书籍，锁定页（旧书页面）仍显示，并标注来源书籍
- [ ] 切换到单页显示，锁定自动解除并提示
- [ ] 地址栏锁形按钮：可用时显示，锁定时高亮，点击可切换
- [ ] 菜单栏「页面」→「页面锁定」勾选状态正确
- [ ] 参照位右上角关闭按钮可解除锁定
- [ ] 关闭书籍（`Unload`）时锁定解除
- [ ] 现有单元测试 + 新增单测全部通过；构建 0 警告 0 错误

---

## 2. 交互设计

### 2.1 目标页指定（关键问题）

双页/自适应会同时显示多页，锁定命令必须知道“锁哪一页”。判定优先级：

1. **上下文菜单**：右键落点所在的页（菜单打开时记录，菜单命令执行时使用）
2. **鼠标悬停**：命令执行时，鼠标位于主视图某页内 → 该页
3. **当前页**：`Book.CurrentPage`（当前帧的第一页）
4. 都没有 → 不执行（`CanExecute` 为 false）

实现：
- `PageFrameBox.GetViewContentAt(Point)`：对可见 `ViewContent` 做几何命中（`TransformToAncestor` 后的包围盒）。
- `MainView` 在右键菜单打开时记录 `ContextMenuTargetViewContent`，命令执行时若 `CommandContext.ByMenu` 且上下文菜单仍打开则使用它。

### 2.2 锁定 / 解锁入口

| 入口 | 操作 | 目标页 | 说明 |
|---|---|---|---|
| 快捷键 `Ctrl+L`（默认） | 锁定/解除切换 | 鼠标所指页 → 当前页 | 用户可在设置中改键；支持 Toggle 参数（On/Off） |
| 主视图右键菜单「页面锁定」 | 切换，勾选态显示 | 右键点中的页 | 默认菜单树中位于「页面」组 |
| 菜单栏「页面」→「页面锁定」 | 切换 | 当前页/鼠标所指页 | 与右键共用同一命令与勾选绑定 |
| 地址栏锁形按钮 | 切换 | 当前页/鼠标所指页 | 仅双页/自适应模式可见；锁定时高亮 |
| 参照位头部 × 按钮 | 解除 | 锁定页 | 最直观的解除入口 |
| 命令（脚本/手势） | `TogglePageLock` | 同上 | 支持 `ToggleCommandParameter`（Toggle/On/Off） |

### 2.3 参照位 UI

```
┌──────────────────────────────────────────────┬──────────────┐
│                                              │ 🔒 P.12   ×  │
│             主视图（按原模式翻页）            │              │
│             双页：[4|5] / 自适应：[6|7|8]      │   锁定页快照  │
│                                              │  （等高、等宽比）│
└──────────────────────────────────────────────┴──────────────┘
```

- 参照位与主视图同高；宽度按锁定页宽高比 × 高度计算，最大占主视图宽度 50%。
- 参照位头部（半透明条）：锁图标 + 页码（`P.12`）+ 来源书籍名（跨书时显示）+ 关闭按钮。
- 主视图区域通过 `Margin` 让出参照位，`PageFrameBox` 画布随之缩小，双页/自适应自动重排。
- 侧别：锁定页在原帧内偏左 → 参照位在左；偏右 → 在右；纵向堆叠帧默认右侧。

### 2.4 状态显示

- 参照位头部（见上）
- 地址栏锁按钮：`ic_lock_24px`（锁定时）/ `ic_lock_open_24px`（未锁定），tooltip「页面锁定」
- 菜单项勾选（`CreateIsCheckedBinding` → `PageLock.IsLocked`）
- 提示消息（`InfoMessageType.Command`）：
  - 成功：「已锁定页面：P.12」
  - 解除：「已解除页面锁定」
  - 替换：「已锁定页面：P.30（替换 P.12）」
  - 失败：「该页面无法锁定」（内容未加载/非图像）
  - 自动解除：「切换到单页显示，已解除页面锁定」

### 2.5 边界行为

| 场景 | 行为 |
|---|---|
| 翻页 / 跳页码 / 页面滑块 | 锁定页不变；主视图正常移动 |
| 循环浏览到书尾 | 与现有逻辑一致；锁定页不变 |
| 切换到其他书籍 | 锁定页保持（快照 + 来源书籍名） |
| 切到单页显示 | 自动解除并提示 |
| 书籍关闭（Unload / 无书） | 解除 |
| 媒体书籍 | 不可锁定；若当前有锁定则解除 |
| 页面列表/胶片条变化（排序、删除） | 锁定页为快照，不受影响（除非解除） |
| 重新加载书籍（ReLoad） | 锁定页仍显示（快照）；如需刷新画面请解除后重新锁定 |
| 锁定页仍出现在正常翻页序列 | 是（B 模型）；翻到该页时主视图和参照位各显示一次 |
| 参照位显示时打开幻灯片 | 正常播放，锁定页保持 |
| 全屏 / 浮动主视图 | 参照位随 MainView 一起显示 |

---

## 3. 架构设计

### 3.1 新增 `PageLock` 服务（`NeeView/PageLock/PageLock.cs`）

单例 `PageLock.Current`，`ObservableObject` + `IDisposable`，持有：

| 成员 | 类型 | 说明 |
|---|---|---|
| `IsLocked` | `bool` | 是否已锁定 |
| `IsAvailable` | `bool` | 当前是否可锁定（书已加载、非媒体、非单页模式） |
| `ImageSource` | `ImageSource?` | 锁定页快照 |
| `PageLabel` | `string` | `P.{IndexPlusOne}` |
| `SourceBookLabel` | `string` | 来源书籍名（跨书时显示） |
| `IsSourceBookVisible` | `bool` | 当前书与来源书不同 |
| `PaneWidth` | `double` | 参照位宽度（按主视图尺寸实时计算） |
| `PaneAlignment` | `HorizontalAlignment` | 左/右 |
| `ViewMargin` | `Thickness` | 主视图让出的边距 |
| `PaneVisibility` | `Visibility` | 是否显示参照位 |

方法：
- `SetViewSize(Size)`：主视图画布尺寸变化时更新参照位宽度。
- `Toggle(ToggleMode, ViewContent?)` / `Lock(ViewContent)` / `Unlock()`。
- `ResolveTarget()`：按 §2.1 规则解析目标页。
- `UpdateAvailable()`：刷新可用状态，并在不可用时自动解除。

订阅：
- `Config.Current.BookSetting.SubscribePropertyChanged(nameof(BookSettingConfig.PageMode))` → 单页时自动解除
- `BookOperation.Current.SubscribeBookChanged(...)` → 无书/媒体书时解除；有书时刷新来源书籍标注

### 3.2 快照与跨书保留

- 锁定时从 `ViewContent.ImageSource` 取出当前已加载的位图（`BitmapFactory` 使用 `BitmapCacheOption.OnLoad` + `Freeze()`，
  见 `NeeView/Bitmap/DefaultBitmapDecoder.cs:27`，因此持有 `BitmapSource` 即可在旧书关闭后继续显示）。
- 使用 `PageFrameBox.GetPageFrameAngle(viewContent)` 取得自动旋转角度（取自 `PageFrameContent.PageFrame.Angle`，
  比 `ViewContent` 内部的 `_angle` 更可靠：后者在帧刚创建时可能仍为 0）：
  - `BitmapSource` → `TransformedBitmap(bitmap, new RotateTransform(angle))`，冻结后保存；
  - 非 `BitmapSource`（如 SVG 的 `DrawingImage`）→ 直接保存，角度按 0 处理。
- `ViewContent.RenderAngle`（新增，暴露内部 `_angle`）仅作兜底。
- 保存快照像素尺寸用于参照位宽高比（旋转后宽高互换）。
- `ImageSource == null`（页面未加载 / 非图像）→ 提示失败，不锁定。

### 3.3 主视图参照位布局（`NeeView/MainView/MainView.xaml`）

```xml
<Grid x:Name="MainViewCanvas" Margin="{Binding MainViewMargin}" SizeChanged="MainViewCanvas_SizeChanged">
    <local:ActiveMarker .../>
    <Grid x:Name="View" ... Margin="{Binding PageLock.ViewMargin}">
        ...
    </Grid>
    <Border x:Name="PinnedPagePane"
            HorizontalAlignment="{Binding PageLock.PaneAlignment}"
            Width="{Binding PageLock.PaneWidth}"
            Visibility="{Binding PageLock.PaneVisibility}">
        ...
    </Border>
</Grid>
```

- `View` 的 `Margin` 让出参照位宽度；`PageFrameBox` 尺寸变化驱动双页/自适应重排。
- `MainViewCanvas.SizeChanged` → `PageLock.SetViewSize(e.NewSize)`。
- 主视图 `StretchWindow` / `AutoStretchWindow` 使用的画布尺寸改为 `View` 的实际尺寸（`MainView.xaml.cs:222,247`）。

### 3.4 目标页命中与命令

- `NeeView/ViewContents/ViewContent.cs`：新增 `public double RenderAngle => _angle;`
- `NeeView/PageFrames/PageFrameBox.cs`：新增
  ```csharp
  public ViewContent? GetViewContentAt(Point point)
  ```
  对 `_containers` 中每个 `PageFrameContent` 的 `ViewContents`，
  用 `TransformToAncestor(this).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight))` 判断点是否落在页面上。
- `NeeView/MainView/MainView.xaml.cs`：
  - `GetViewContentAtMousePosition()`
  - 右键菜单打开时记录目标页，菜单关闭时清除
  - `PinnedPageCloseButton_Click` → `PageLock.Current.Unlock()`
- `NeeView/Command/Commands/TogglePageLockCommand.cs`：
  - 名称 `TogglePageLock`，Group `CommandGroup.PageSetting`，默认快捷键 `Ctrl+L`，`ToggleCommandParameter`
  - `CreateIsCheckedBinding` → `PageLock.IsLocked`
  - `CanExecute` → `PageLock.IsAvailable`
  - `Execute` → 解析目标（`ByMenu` + 上下文菜单目标优先）后 `PageLock.Current.Toggle(...)`

### 3.5 自动解除

- 页面模式变化：`PageMode == SinglePage` → `Unlock()` + 提示
- 书籍变化：`Book == null` 或 `Book.IsMedia` → `Unlock()`
- 其余情况保持（含跨书）

### 3.6 参照位尺寸计算（可单测）

`PageLockTools`（`NeeView/PageLock/PageLockTools.cs`，纯静态）：

```csharp
public static Size GetRotatedPixelSize(Size pixelSize, double angle);
public static double GetPaneWidth(Size viewSize, Size imageSize, double maxRate);
public static PageLockSide GetSide(IReadOnlyList<double> elementWidths, int index);
```

规则：
- `GetPaneWidth = min(imageAspect × viewHeight, viewWidth × maxRate)`；`maxRate = 0.5`
- `GetSide`：元素中心在帧内中心左侧 → `Left`，否则 `Right`（纵向堆叠时调用方传 `null` → `Right`）
- 角度 90/270 → 宽高互换

### 3.7 变更文件清单

**新增**
- `NeeView/PageLock/PageLock.cs`（服务/模型、`PageLockSide`）
- `NeeView/PageLock/PageLockTools.cs`（纯计算）
- `NeeView/Command/Commands/TogglePageLockCommand.cs`
- `NeeView.UnitTest/PageLockToolsTest.cs`
- `design/page-lock.md`（本文）

**修改**
- `NeeView/Command/CommandTable.cs`（注册命令）
- `NeeView/Menu/MenuTree.cs`（「页面」组菜单项）
- `NeeView/MainView/MainView.xaml` / `.xaml.cs`（参照位 UI、目标记录、关闭按钮、画布尺寸）
- `NeeView/MainView/MainViewViewModel.cs`（暴露 `PageLock`）
- `NeeView/AddressBar/AddressBarViewModel.cs` / `AddressBarView.xaml`（锁形按钮）
- `NeeView/PageFrames/PageFrameBox.cs`（`GetViewContentAt`）
- `NeeView/ViewContents/ViewContent.cs`（`RenderAngle`）
- `NeeView/Languages/ja|en|zh-Hans|zh-Hant|ko|ru|es|pt-BR|it-IT.restext`（新增资源键）

---

## 4. 已知限制

1. 参照位模型不改变翻页序列，因此翻到锁定页时该页会同时出现在主视图与参照位（B 模型的自然结果）。
2. 参照位显示的是锁定瞬间的快照：窗口放大到远超锁定分辨率时会变模糊；重新加载书籍后不会自动刷新。
3. 视图的缩放、旋转（锁定后）、图像特效变化不会反映到参照位。
4. 非图像页面（媒体页、压缩包图标页等）或仍在加载的页面无法锁定。
5. 参照位宽度上限 50%，超宽页面会被缩放到宽度上限内（上下留白）。
6. 页面锁定状态不持久化。
7. 暂未实现：页面列表/胶片条缩略图上的锁定标记与右键入口（后续可加）。

---

## 5. 测试计划

### 5.1 单元测试（新增 `PageLockToolsTest`，共 15 例）

| # | 用例 | 期望 |
|---|---|---|
| 1 | 800×1200 图像、1600×1000 主视图 | 参照位宽度 = 800/1200×1000 ≈ 666.7（< 800 上限） |
| 2 | 横长图（宽高比 3）在主视图 1000×800 | 宽度被上限 500 截断 |
| 3 | 同上，maxRate 指定 0.25 | 宽度被截断为 250 |
| 4 | 角度 0° / 180° / 360° 的 800×1200 | 像素尺寸不变 |
| 5 | 角度 90° / -90° / 270° / -270° 的 800×1200 | 旋转后像素尺寸 1200×800 |
| 6 | 空图像尺寸 | 返回 `Size.Empty` |
| 7 | 双页左页矩形（0,0,400,800）、视图 1200×800 | `Left` |
| 8 | 双页右页矩形（700,0,500,800）、视图 1200×800 | `Right` |
| 9 | 空视图尺寸 | `Right` |
| 10 | 空视图尺寸的参照位宽度 | 0 |

### 5.2 回归

- `dotnet build NeeView.sln -c Debug`：0 警告 0 错误
- `dotnet test --filter "FullyQualifiedName~PageLock"`：全部通过
- `dotnet test --filter "FullyQualifiedName~Adaptive"`：既有 16 个用例不回归

### 5.3 人工验证（GUI）

按 §1.4 验收清单逐项操作，重点：
- 双页/自适应两种模式下的锁定与重排
- 跨书保持
- 切单页自动解除
- 全屏 / 侧面板开关后参照位宽度随主视图尺寸变化

---

## 附录 A：资源键与文案（ja / en / zh-Hans 基准）

| 键 | ja | en | zh-Hans |
|---|---|---|---|
| `TogglePageLockCommand` | ページを固定 | Lock page | 锁定页面 |
| `TogglePageLockCommand.Menu` | ページ固定 | Page lock | 页面锁定 |
| `TogglePageLockCommand.Remarks` | 表示中のページを固定し、参照表示します。 | Pin the displayed page as a reference. | 将当前显示的页面固定为参照显示。 |
| `PageLock.ToolTip` | ページ固定 | Page lock | 页面锁定 |
| `PageLock.Close` | ページ固定を解除 | Unpin page | 解除页面锁定 |
| `PageLock.PageFormat` | P.{0} | P.{0} | P.{0} |
| `PageLock.Locked` | ページを固定しました: {0} | Page pinned: {0} | 已锁定页面：{0} |
| `PageLock.Replaced` | ページを固定しました: {0}（{1} を置換） | Page pinned: {0} (replaced {1}) | 已锁定页面：{0}（替换 {1}） |
| `PageLock.Unlocked` | ページ固定を解除しました | Page unpinned | 已解除页面锁定 |
| `PageLock.CannotLock` | このページは固定できません | This page cannot be pinned | 该页面无法锁定 |
| `PageLock.AutoUnlocked` | 単ページ表示のためページ固定を解除しました | Page unpinned because single-page mode is active | 已切换到单页显示，页面锁定已解除 |

> 其他语言按同义翻译；缺失键回退到英语可接受。

---

## 附录 B：实现记录（2026-11-25）

### B.1 实际变更文件

**新增**

| 文件 | 内容 |
|---|---|
| `NeeView/PageLock/PageLock.cs` | 页面固定服务（状态、快照、跨书保持、自动解除、消息） |
| `NeeView/PageLock/PageLockTools.cs` | 纯计算（参照位宽度、旋转后尺寸、左右侧判定），附 `PageLockSide` |
| `NeeView/Command/Commands/TogglePageLockCommand.cs` | 命令（`Ctrl+L`、Toggle 参数、勾选绑定、菜单目标页） |
| `NeeView.UnitTest/PageLockToolsTest.cs` | 15 例单测 |
| `design/page-lock.md` | 本文 |

**修改**

| 文件 | 内容 |
|---|---|
| `NeeView/Command/CommandTable.cs` | 注册 `TogglePageLockCommand` |
| `NeeView/Menu/MenuTree.cs` | 「页面」组新增「页面锁定」 |
| `NeeView/MainView/MainView.xaml` | 参照位 UI（`PinnedPagePane`）、`View` 的 `ViewMargin`、`MainViewCanvas` SizeChanged |
| `NeeView/MainView/MainView.xaml.cs` | 鼠标位置命中、右键目标记录、关闭按钮、画布尺寸通知；窗口适配尺寸改用 `View` 实际尺寸 |
| `NeeView/MainView/MainViewViewModel.cs` | 暴露 `PageLock` |
| `NeeView/AddressBar/AddressBarViewModel.cs` | 暴露 `PageLock` + `TogglePageLockCommand` |
| `NeeView/AddressBar/AddressBarView.xaml` | 地址栏锁形按钮（仅可用时显示，锁定时高亮） |
| `NeeView/PageFrames/PageFrameBox.cs` | `GetViewContentAt(Point)`、`GetPageFrameAngle(ViewContent)` |
| `NeeView/ViewContents/ViewContent.cs` | `RenderAngle` 属性（兜底用） |
| `NeeView/Languages/*.restext`（9 种语言） | 新增 `PageLock.*` / `TogglePageLockCommand*` 资源键 |

### B.2 实现要点与设计差异

1. **自动旋转角度来源**：设计时计划用 `ViewContent.RenderAngle`，实现时发现帧刚创建时该值可能仍为 0，
   改用 `PageFrameBox.GetPageFrameAngle(viewContent)` 从 `PageFrameContent.PageFrame.Angle` 取得，`RenderAngle` 仅作兜底。
2. **目标页命中**：采用 `ViewContent` 屏幕包围盒（`TransformToAncestor(box).TransformBounds()`）几何判定，
   不依赖元素是否可命中测试；优先检查当前选中容器。
3. **参照位侧别**：比较页面矩形中心与 `PageFrameBox` 宽度中心，左半则参照位在左，否则在右。
4. **全景（Panorama）模式**：不可锁定；切换到全景时自动解除。
5. **资源键**：9 种语言全部补齐，无回退；`shared.restext` 未改动。

### B.3 验证结果

- `dotnet build NeeView.sln -c Debug`：**0 警告 / 0 错误**
- `dotnet test NeeView.sln -c Debug --no-build --filter "FullyQualifiedName~PageLock"`：**15 通过**
- `dotnet test NeeView.sln -c Debug --no-build --filter "FullyQualifiedName~Adaptive"`：**16 通过**（无回归）
- 全量测试：149 通过，2 失败均为既有的环境依赖用例（`FileTreeBasicTest` 的固定路径、`Sandbox.TextResourceTest` 的语言）
- **人工 GUI 验证未执行**（本机有正在运行的打包版 NeeView 实例，避免多开干扰），请按 §1.4 验收清单手工确认。

### B.4 后续可选项

- 页面列表 / 胶片条缩略图上的锁定标记与右键入口
- 参照位宽度可拖动调整、位置可固定为左/右
- 锁定页在主视图中的重复显示提示（可选）
