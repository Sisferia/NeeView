# 多页锁定（Multi Page Lock）设计 —— 行首排列参照区

> 状态：**已实现（2026-11-25）。构建 0 警告 0 错误；PageLock 相关单测 38 例通过；运行时 API 级验证通过；GUI 视觉验证待人工执行（见附录 C）。**
> 目标仓库：`Sisferia/NeeView`（fork of `neelabo/NeeView`，分支 `master`）
> 前置：`design/page-lock.md`（单页固定版，已实现）；本文档描述将其扩展为多页锁定。
> 实现时同步修订 `design/page-lock.md` 中被取代的条款（见 §9）。

---

## 0. 结论摘要（TL;DR）

- 在双页 / 自适应显示模式下，用户可以**同时锁定多个页面**（上限 8）。
- 锁定页从主视图翻页画面中**移出**，显示在主视图**行首的参照区**：
  - 横排显示 → 参照区在**左侧**，锁定页按**锁定顺序从左往右**排列；
  - 纵排显示 → 参照区在**上方**，锁定页按**锁定顺序从上往下**排列。
- 锁定第 k 个显示页时：该页加入参照区；主视图（剩余区域）页面重排，原页面依次右移（横排）。
  示例：三页 `[A][B][C]` → 锁定 B → 视觉 `[B🔒][A][C]`。
- 约束「至少保留一个未锁定的画面」：
  - **R1**：锁定时永远不允许锁定当前画面「最后一个未锁定页」（否则拒绝并提示）；
  - **R2**：任何触发显示页数重算的操作（翻页、跳转、切书、模式/窗口变化、锁定/解除）后，
    主视图**永远至少显示 1 个未锁定页**；宽度不足也不降为 0，而是**整个显示行统一缩放**（决策 1-C）。
- 宽度不足时不再拒绝锁定，改为显示行统一等比缩小（参照格 + 主视图一起缩小）。
- 跨书保持（快照）、单页模式自动全部解除、不持久化、不新增设置项 —— 沿用原设计。

---

## 1. 决策记录（2026-11-25 定稿）

| 编号 | 决策 | 说明 |
|---|---|---|
| D1 | **行首参照区布局** | 锁定页从主视图移出，在行首按锁定顺序排列；横排→左，纵排→上 |
| D2 | 跨书保持 | 逐项快照，来源书名逐项标注 |
| D3 | 单页模式自动解除 | 切到单页模式解除**全部**锁定并提示数量 |
| D4 | 多页锁定 | 同一页重复锁定 = 解除；锁定新页 = 追加，不再替换 |
| D5 | 交互入口 | Ctrl+L、右键菜单、页面菜单、地址栏按钮、参照格 ×、新增「解除全部」 |
| D6 | 不持久化 | 状态仅本次运行有效 |
| D7 | 不新增设置项 | 容量/缩放参数都使用内部常量 |
| D8 | **未锁定页保底（R1/R2）** | 取代旧 C1/C2/C3；宽度不再作为拒绝条件 |
| D9 | **宽度不足统一缩放**（决策 1-C） | 参照格与主视图页一起等比缩小，保证至少一页未锁定页可见 |
| D10 | **当前页被锁定 → 自动前进**（决策 4-B） | 当前页指针移动到下一个未锁定页 |
| D11 | **全锁定画面跳过**（决策 5-A） | 导航自动跳到最近的含未锁定页的画面，不出现空白 |
| D12 | **锁定页从主视图移出**（决策 6-A） | 不重复显示 |
| D13 | **锁定数量上限 8**（决策 2-A） | 内部常量 `MaxLockCount = 8`，内存保护 |
| D14 | 方向映射（决策 3） | `StackOrientation == Horizontal` → 左；`Vertical` → 上 |

---

## 2. 现状核对（单页固定版，代码实测）

| 设计条目 | 实现位置 | 结论 |
|---|---|---|
| 单例服务/快照/跨书 | `NeeView/PageLock/PageLock.cs`（单值字段 `_page` / `_imageSource` / `_paneWidth`） | ✅ 单页状态 |
| 参照位 UI | `MainView.xaml` L32–68 单个 `PinnedPagePane`；`View.Margin` 让位 | ✅ 单参照位、按页面原侧 |
| 入口 5 个 | `TogglePageLockCommand`（Ctrl+L）、`MenuTree.cs` L175、`AddressBarViewModel` / `AddressBarView.xaml`、`MainView.xaml.cs` 关闭按钮 | ✅ |
| 目标解析 | 右键页→鼠标页→当前页（`PageLock.ResolveTargetViewContent` + `PageFrameBox.GetViewContentAt`） | ✅ |
| 单页互斥 | `Toggle` 替换语义 + `PageLock.Replaced` 消息 | ✅ 需改为多页 |
| 自动解除 | 单页模式 / 全景 / 无书 | ✅ |
| 单测 | `NeeView.UnitTest/PageLockToolsTest.cs` 15 例 | ✅ |

**需打破的单页假设**：`PageLock` 单值字段、替换语义、`IsChecked` 绑定、`AddressBarViewModel.IsLocked` 分支、
单个 `PinnedPagePane`、`GetPaneWidth` 单页宽度语义、关闭按钮无参解除、`GetSide` 分侧逻辑。

---

## 3. 显示模型

### 3.1 显示行

```
横排（StackOrientation=Horizontal）：
┌────────┬────────┬──────────────────────────────────┐
│ 🔒P.2  │ 🔒P.5  │        主视图（未锁定页）          │
│ 快照    │ 快照    │   当前帧中除锁定页外的页面，按原顺序  │
└────────┴────────┴──────────────────────────────────┘
└──── 参照区（锁定页，按锁定顺序从左往右）────┘ └── 主视图 ──┘

纵排（StackOrientation=Vertical）：
┌──────────────────────────────┐
│        🔒P.2 快照             │  ← 参照区（按锁定顺序从上往下）
├──────────────────────────────┤
│        🔒P.5 快照             │
├──────────────────────────────┤
│        主视图（未锁定页）       │
└──────────────────────────────┘
```

- 参照格 = 锁定页整页高度的快照（宽 = 图像宽高比 × 画布高度；纵排用高度），**不做单侧 50% 上限**。
- 主视图 = 画布扣掉参照区后的区域；内部按现有双页/自适应逻辑显示**未锁定页**。
- 锁定页**不在主视图重复显示**（决策 D12）。

### 3.2 三页场景（用户示例）

```
初始：        [A][B][C]
锁定 B：      [B🔒][A][C]     B 移到最前；A 右移到 B 原位置；C 保持
再锁定 A：    [B🔒][A🔒][C]   A 直接就地转为参照格
再锁定 C：    拒绝（C 是当前画面最后一个未锁定页，R1）
解除 B：      [A🔒][B][C]     B 回到主视图（按页面顺序）
```

### 3.3 排列与插入规则

- 参照区顺序 = **锁定时间顺序**（第 1 次锁定在最前；后续追加到参照区末尾）。
- 每次锁定后主视图页面整体右移一位（横排）/ 下移（纵排），由主视图重新布局自然完成。
- 解除参照格：参照区缩短，主视图变宽，该页按页面顺序回到主视图。

### 3.4 当前页（决策 D10）

- 锁定页保留在页面序列与页码/书签/脚本语义中（不改变原有页面索引空间）。
- 当前页被锁定时：**当前页指针自动前进到下一个未锁定页**（翻页方向），并提示；
  若前进方向无未锁定页则反向查找；全画面无未锁定页的情况由 R1 排除。
- 参照格高亮：当前页若出现在参照区，对应参照格显示高亮描边。

---

## 4. 约束实现

### 4.1 R1：锁定操作检查

- **当前画面** = 当前帧所显示的页面集合（对应当前帧的页面范围；含参照区锁定页与主视图未锁定页）。
- 判定：当前画面未锁定页数 ≥ 2 才允许锁定；否则拒绝，提示「至少保留一个未锁定页面」。
  - 目标页已锁定 → 解除该格（Toggle 语义，不受 R1 限制）。
- 性质：任何锁定后当前画面必有 ≥1 未锁定页；因此**全书不可能被锁光**（最后一次锁定必然发生在有 ≥2 未锁定页的画面上），无需全书计数检查。

### 4.2 R2：显示页数重算不变量

- **触发时机**：翻页、跳页码、页面滑块、幻灯片、切换书籍、页面模式切换、窗口/视图尺寸变化、锁定/解除。
- **要求**：主视图显示的未锁定页数量 **≥ 1**；即使可用宽度不足一页，也强制显示 1 页（显示行统一缩放，见 §5）。
- 实现口径：
  - 页面帧装填结果的页数做 `Math.Max(1, computed)`（`AdaptivePagePacker.GetFramePageCount` 已保证 ≥1）；
  - 页面帧内容过滤后若一个未锁定页都没有（画面全锁定），按 D11 跳到最近的含未锁定页的画面；
  - 保证最终帧内至少 1 个未锁定元素，否则视为不可显示帧并跳过。

### 4.3 数量上限（D13）

- `MaxLockCount = 8`（内部常量）。达到上限时拒绝并提示「已达锁定上限」。
- 其余宽度相关拒绝全部删除。

---

## 5. 宽度不足时的统一缩放（D9）

### 5.1 长度定义（以横排为例；纵排把「宽/高」互换）

```
cellLength_i   = 参照格 i 的自然长度（横排=宽：imageW/imageH × 画布高；纵排=高）
stripLength    = Σ cellLength_i                     // 参照区总长
liveMinLength  = 一个未锁定页的自然长度（按当前页/最近未锁定页的宽高比计算）
liveLength     = max(liveMinLength, 画布长 − stripLength)
rowLength      = stripLength + liveLength           // 显示行自然总长
scale          = min(1, 画布长 / rowLength)         // 统一缩放系数
```

- `scale == 1`：空间充足，正常布局（主视图占 `画布长 − stripLength`，自适应填页）。
- `scale < 1`：空间不足 → 参照区 + 主视图整体按 `scale` 等比缩小；
  主视图自然长度固定为 `liveMinLength`（保证 1 页），缩放后仍可见（R2）。
- 缩放后显示行的视觉长度 = 画布长；高度/宽度方向居中。

示例（画布 1400×800，三页各宽 467）：
| 操作 | stripLength | liveLength | rowLength | scale |
|---|---|---|---|---|
| 锁定 B | 467 | 933 | 1400 | 1.0 |
| 锁定 A | 934 | 466 | 1400 | 1.0 |
| 跨帧/跨书累计 4 格（各 467） | 1868 | 467（保底 1 页） | 2335 | 0.60 |

### 5.2 实现方式

- `MainViewCanvas` 内新增「显示行」容器：`[参照区 ItemsControl][主视图 View]` 横向排列（纵排为纵向）。
- 显示行使用 `LayoutTransform = ScaleTransform(scale)`（或等价渲染缩放），布局按自然尺寸计算，
  WPF 负责命中测试/输入坐标换算；主视图内部布局（页数、缩放）仍按自然尺寸计算，不受缩放影响。
- 缩放不改变主视图内部页面数量计算（R2 的「1 页保底」由自然长度 `liveMinLength` 保证）。
- 缩放下限：不设（R2 要求「无论宽度是否足够都显示」）；极端情况下页面会很小。

---

## 6. 交互设计

### 6.1 入口

| 入口 | 目标 | 行为 |
|---|---|---|
| `Ctrl+L` | 右键页 → 鼠标所指页 → 当前页 | 目标未锁定 → 追加到参照区；目标已锁定 → 解除该格 |
| 主视图右键菜单「页面锁定」 | 右键点中的页 | 同上（`ByMenu` 时使用 `GetContextMenuTargetViewContent`） |
| 菜单栏「页面」→「页面锁定」 | 鼠标/当前页 | 同上 |
| 地址栏锁按钮 | 鼠标/当前页 | 同上；任一锁定高亮，tooltip 显示数量 |
| 参照格 **×** | 该格 | 只解除该页 |
| 菜单/右键「解除全部页面锁定」 | — | 解除全部（新命令 `UnlockAllPageLocks`，建议 `Ctrl+Shift+L`） |
| 命令/脚本 | — | `TogglePageLock` 名称/参数不变 |

### 6.2 参照格 UI

- 整格 = 页面快照；左上角小标签 `P.{n}`（跨书时附来源书名，TextTrimming）；右上角悬浮 `×`（悬停时显示）。
- 当前页对应的参照格高亮描边。
- 不做整条标题栏（保持“页面行”观感）。

### 6.3 状态与消息

- 勾选：多值状态不适合布尔勾选 → **移除** `TogglePageLock.CreateIsCheckedBinding`；状态用地址栏高亮 + 参照格表达。
- 消息（`InfoMessageType.Command`）：
  - 锁定成功：「已锁定页面：P.12（共 2 页）」
  - 解除单格：「已解除页面锁定：P.12」
  - 解除全部：「已解除全部页面锁定（共 2 页）」
  - R1 拒绝：「至少保留一个未锁定页面」
  - 上限拒绝：「已达锁定上限（8 页）」
  - 无法锁定：「该页面无法锁定」（内容未加载/非图像）
  - 单页模式自动解除：「已切换到单页显示，已解除页面锁定（共 2 页）」
- 删除：`PageLock.Replaced`（替换语义移除）。

### 6.4 边界行为

| 场景 | 行为 |
|---|---|
| 翻页 / 跳页 / 滑块 / 幻灯片 | 参照区不变；主视图在未锁定页中按现有逻辑翻页 |
| 当前页被锁定 | 当前页自动前进到下一个未锁定页（D10）；参照格高亮 |
| 当前画面全部页已锁定（后续帧间接造成） | 导航自动跳到最近的含未锁定页的画面（D11），不显示空白 |
| 锁定页重复锁定 | 解除该格 |
| 切换书籍 | 参照区全部保持（快照 + 来源书名）；新书按 R2 正常显示 |
| 关闭书籍 / 媒体书 / 全景 / 单页模式 | 全部解除（单页模式：提示数量） |
| 窗口缩放 / 全屏 / 侧栏 / 模式切换 | 重算布局；宽度不足 → 统一缩放（D9）；自适应按现有规则重新装填 |
| 页面未加载 / 非图像页 | 拒绝并提示 |
| 重新加载书籍 | 快照保持 |

---

## 7. 架构设计

### 7.1 新增 `NeeView/PageLock/PageLockItem.cs`

| 成员 | 类型 | 说明 |
|---|---|---|
| `Page` | `Page` | 来源页（仅用于同一性判断） |
| `ImageSource` | `ImageSource` | 冻结快照（含自动旋转） |
| `ImageSize` | `Size` | 旋转后像素尺寸（布局用） |
| `PageLabel` | `string` | `P.{IndexPlusOne}` |
| `SourceBookPath` / `SourceBookLabel` | `string` | 来源书籍 |
| `IsSourceBookVisible` | `bool` | 服务在书籍切换时更新并通知 |
| `CellLength` | `double` | 由服务计算的参照格长度（横排=宽，纵排=高） |
| `IsCurrentPage` | `bool` | 当前页高亮 |

### 7.2 `PageLock` 服务改造

```csharp
public ObservableCollection<PageLockItem> Items { get; }   // 顺序 = 锁定顺序 = 行首排列顺序
public int LockedCount { get; }
public bool IsLocked => LockedCount > 0;                    // 兼容旧绑定（任一锁定）
public bool IsAvailable { get; }
public bool IsFull { get; }                                 // 达到 MaxLockCount
public double StripLength { get; }                          // 参照区总长
public double RowScale { get; }                             // 统一缩放系数（§5）
public Thickness ViewMargin { get; }                        // 主视图让位
public event EventHandler? LayoutChanged;                   // 主视图/页面帧刷新
public void Toggle(ToggleMode, ViewContent? target = null);
public bool Lock(ViewContent viewContent);
public bool UnlockItem(PageLockItem item);
public void UnlockAll();
public void Clear();
public void SetViewSize(Size viewSize);
```

- 快照/BitmapSource 冻结/自动旋转逻辑沿用现有 `CreateSnapshot`/`GetImageSize`/`GetPageFrameAngle`。
- 锁定/解除后：更新集合 → 重新计算布局（§5）→ 触发 `LayoutChanged`；
  若锁定的是当前页，按 D10 前进当前页；解除后恢复页面到主视图（由页面帧过滤自动完成）。
- 自动解除：单页模式/全景/无书/媒体书 → `Clear()`（单页模式提示）。
- 不持久化、不新增设置项。

### 7.3 `PageLockTools` 纯函数（可单测）

```csharp
public static PageFrameOrientation GetRowOrientation(PageFrameContext context);   // 映射 D14（或由调用方传入）
public static double GetCellLength(Size viewSize, Size imageSize, PageFrameOrientation orientation);
public static double GetStripLength(IReadOnlyList<double> cellLengths);
public static double GetLiveMinLength(Size viewSize, Size imageSize, PageFrameOrientation orientation);
public static double GetRowScale(double canvasLength, double stripLength, double liveLength);
public static Thickness GetViewMargin(double stripLength, PageFrameOrientation orientation);
public static bool CanLockPage(int unlockedPageCountInCurrentView);               // R1: >= 2
public static int EnsureVisiblePageCount(int computedCount);                      // R2: max(1, n)
```

- 删除 `PageLockSide` / `GetSide` / 原 `GetPaneWidth` 的“单页 50%”语义（测试同步迁移）。

### 7.4 主视图渲染（关键改动）

- **锁定页从主视图画面移出**（D12）：在页面帧构建时过滤锁定页（已实现）。
  - `PageFrameFactory` 生成 `PageFrameElement` 列表时跳过锁定页
    （`CreatePageFrameWithPageLock` / `CreateAdaptiveFrameCore`）。
  - `PageFrame.FrameRange` 由剩余元素重新计算（**不保留**原范围）：先头/末尾的锁定页因此从帧范围移除，
    当前页自然前进到下一个未锁定页（D10）；中间锁定页保留在范围内但不渲染。
  - 帧内未锁定页按现有 layout 重排（`PageFrameContent` 基于 elements 计算尺寸，天然支持）。
  - 过滤后无未锁定元素的帧 → 工厂前进/后退到最近的含未锁定页的帧（D11）。
  - 新增 `PageFrame.IsAutoRotateEnabled`，过滤后重建帧时沿用原自动旋转设置。
  - `PageFrame.AssertSources` 放宽为「页面顺序递增」（允许锁定页间隔）；`AssertValid` 放宽帧范围断言。
  - 锁定集合变化时：`PageLock` 触发 `LockChanged` → `MainView` 调用
    `PageFrameBox.RefreshPageLockFilter()`（`SetDirty(Heavy)` + 从当前页 `MoveTo`），按新过滤重建帧；
    当前页被锁定时由帧范围收缩自动前进（D10）。
  - **顺序翻页（下一张/上一张）在导航层跳过锁定页**：`PageFrameBox.MoveToNextPage` / `MoveToNextFrame`
    通过 `PageLockTools.FindNextUnlockedPageIndex` 查找指定方向的未锁定页；若前方（或后方）已无未锁定页，
    触发页面终点事件（如「最后一页」提示），不再依赖帧过滤器的就近重定向。
    这避免了「锁定末页/末段全部固定时，向前翻页回弹到前一页」的卡住问题（2026-11-25 修复）。
  - `PageLock` 维护线程安全的锁定页快照（`volatile Page[]`），供页面帧工厂在任意线程查询。
- **R2 保底**：装填页数 `Math.Max(1, …)`；由上述过滤保证每帧至少 1 未锁定元素。
- **统一缩放（D9）**：`MainView.xaml` 显示行容器 + `LayoutTransform`；`PageLock.RowScale` 变化时更新。

### 7.5 UI 改造（`MainView.xaml`）

```xml
<Grid x:Name="MainViewCanvas" ...>
    <Grid x:Name="DisplayRow">
        <Grid.LayoutTransform><ScaleTransform ScaleX="{Binding PageLock.RowScale}" ScaleY="{Binding PageLock.RowScale}"/></Grid.LayoutTransform>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>   <!-- 参照区（横排）/行：纵排时改为 RowDefinition -->
            <ColumnDefinition Width="*"/>      <!-- 主视图 -->
        </Grid.ColumnDefinitions>
        <ItemsControl Grid.Column="0" ItemsSource="{Binding PageLock.Items}">
            <ItemsControl.ItemsPanel><StackPanel Orientation="Horizontal"/></ItemsControl.ItemsPanel>
            <!-- ItemTemplate：Image + P.{n} 标签 + 悬浮 × + 当前页高亮 -->
        </ItemsControl>
        <Grid Grid.Column="1" x:Name="View" Margin="{Binding PageLock.ViewMargin}" .../>
    </Grid>
</Grid>
```

- `MainView.xaml.cs`：`PinnedPageCloseButton_Click` → `UnlockItem((PageLockItem)DataContext)`；
  `MainViewCanvas_SizeChanged` → `PageLock.SetViewSize`；`GetViewCanvasSize` 保持。
- `MainViewViewModel` 已暴露 `PageLock`，无需大改。

### 7.6 命令 / 菜单 / 地址栏 / 语言

- `TogglePageLockCommand`：保留 Ctrl+L 与 Toggle 参数；移除勾选绑定；Execute 仍走 `PageLock.Toggle`。
- 新增 `UnlockAllPageLocksCommand`：`CommandGroup.PageSetting`，`Ctrl+Shift+L`，`CanExecute = IsLocked`。
- `CommandTable` 注册；`MenuTree`「页面」组新增「解除全部页面锁定」。
- `AddressBarViewModel.TogglePageLock` 改为 `Toggle(ToggleMode.Toggle)`；`AddressBarView` 高亮条件 `IsLocked`。
- 9 语言资源键新增/修改/删除（见附录 A）。

### 7.7 变更文件清单

**新增**

| 文件 | 内容 |
|---|---|
| `NeeView/PageLock/PageLockItem.cs` | 锁定项模型 |
| `NeeView/PageLock/PageLockTools.cs` 扩展 | 纯计算（§7.3） |
| `NeeView/Command/Commands/UnlockAllPageLocksCommand.cs` | 解除全部命令 |
| `NeeView.UnitTest/PageLockLayoutTest.cs` | 布局/约束单测 |
| `design/page-lock-multi.md` | 本文档 |

**修改**

| 文件 | 内容 |
|---|---|
| `NeeView/PageLock/PageLock.cs` | 单页 → 集合、布局、R1、统一缩放、自动解除 |
| `NeeView/MainView/MainView.xaml` / `.xaml.cs` | 显示行 + 参照区 ItemsControl + 缩放 + 逐格关闭 |
| `NeeView/PageFrames/PageFrameFactory.cs` | 帧元素过滤锁定页（+ 空帧跳过） |
| `NeeView/PageFrames/PageFrame.cs` | 放宽连续性断言（允许锁定页间隔） |
| `NeeView/PageFrames/PageFrameBox.cs` | 锁定变化时的帧刷新入口、当前页前进辅助 |
| `NeeView/Command/Commands/TogglePageLockCommand.cs` | 去勾选 |
| `NeeView/Command/CommandTable.cs`、`NeeView/Menu/MenuTree.cs` | 注册新命令/菜单 |
| `NeeView/AddressBar/AddressBarViewModel.cs` / `AddressBarView.xaml` | 多页语义 |
| `NeeView/Languages/*.restext`（9 种） | 资源键 |
| `design/page-lock.md` | 修订被取代条款 |

---

## 8. 测试计划

### 8.1 新增单测（`PageLockLayoutTest`）

| # | 用例 | 期望 |
|---|---|---|
| 1 | 三页场景锁定第 2 页 | R1 允许；顺序/参照格长度正确 |
| 2 | 连续锁定 | 参照区顺序 = 锁定顺序（左→右） |
| 3 | 解除中间格 | 顺序保持、长度重算 |
| 4 | 当前画面只剩 1 个未锁定页 | R1 拒绝 |
| 5 | 全书未锁定页数始终 ≥1（模拟随机序列） | 不变量成立 |
| 6 | 横排/纵排 cellLength / liveMinLength / margin | 计算正确 |
| 7 | 空间充足 / 恰好 / 不足三态 RowScale | 1.0 / 1.0 / <1 且行总长缩后=画布长 |
| 8 | R2 页数 clamp | 0 → 1，1 → 1，3 → 3 |
| 9 | 旋转 90° 页面尺寸 | 宽高互换参与计算 |
| 10 | 上限 8 | 第 9 页被拒 |
| 11 | 空参照区 / 空画布 | 边界安全 |
| 12 | 跨书参照格不计入当前书 R1 | 不误判 |

### 8.2 回归

- `dotnet build NeeView.sln -c Debug`：0 警告 0 错误
- `dotnet test --filter "FullyQualifiedName~PageLock"`：全通过
- `dotnet test --filter "FullyQualifiedName~Adaptive"`：既有 16 例不回归
- 页面帧相关：宽页模式、单页分割、假想页（dummy）、循环浏览、全景、媒体书
- 全量测试（既有 2 例环境相关失败除外）

### 8.3 人工 GUI 验收

1. 三页场景锁定第二页 → 视觉 `[B🔒][A][C]`
2. 依次锁定 → 参照区从左往右按锁定顺序排列；当前页自动前进
3. 锁定当前画面最后一个未锁定页 → 拒绝并提示
4. 宽高比不同/旋转页的参照格尺寸正确
5. 解除单格/全部 → 页面回流、顺序正确
6. 翻页/跳页/滑块/幻灯片：参照区不变，主视图跳过锁定页
7. 当前页为锁定页 → 自动前进 + 参照格高亮
8. 累计锁定超过画布宽度 → 显示行统一缩小，至少 1 个未锁定页可见
9. 达到上限 8 → 拒绝并提示
10. 跨书、关闭书、媒体书、全景、单页模式
11. 窗口缩放/全屏/侧栏/页面模式切换后布局正确（含缩放）
12. 9 语言资源显示正常

---

## 9. 风险与取舍

| 风险 | 说明/缓解 |
|---|---|
| **页面帧过滤改动面大** | `PageFrameFactory` 过滤 + 帧刷新 + 空帧跳过涉及页面帧核心；spike 结论表明可行（`PageFrame` 支持任意元素列表、`FrameRange=Marge` 保持导航语义），但需完整回归（宽页模式/分割/假想页/循环） |
| 统一缩放交互 | `LayoutTransform` 缩放后主视图输入命中由 WPF 换算；需验证拖拽/滚轮/右键菜单行为 |
| 锁定页仍占页面位置 | 主视图可能显示“缺口跳过”的页面（如 A、C 相邻）；符合“移出”语义 |
| 自适应重装填 | 参照区占宽后主视图收窄，页面数量按现有规则变化；统一缩放兜底 |
| 内存 | 每格全分辨率冻结快照，上限 8 |
| 页码/书签语义 | 保持原索引空间；当前页自动前进是唯一的位置变化（D10） |

---

## 10. 实施顺序

1. **Phase A**：`PageLockItem` + `PageLockTools` 新纯函数 + 单测（不改变现有行为，构建绿）
2. **Phase B**：`PageLock` 改为多页集合（快照、R1、布局/缩放计算、解除/自动解除），
   `MainView` 显示行 + 参照区 UI + 缩放，命令/菜单/地址栏/语言
3. **Phase C**：页面帧过滤（D12）、空帧跳过（D11）、当前页自动前进（D10）、R2 保底
4. **Phase D**：单测/回归/人工验收；更新 `design/page-lock.md`

---

## 附录 A：资源键与文案（ja / en / zh-Hans 基准）

| 键 | ja | en | zh-Hans |
|---|---|---|---|
| `PageLock.LockedCount` | ページを固定しました: {0}（{1} 件） | Page pinned: {0} ({1} pinned) | 已锁定页面：{0}（共 {1} 页） |
| `PageLock.UnlockedItem` | ページ固定を解除しました: {0} | Page unpinned: {0} | 已解除页面锁定：{0} |
| `PageLock.UnlockedAll` | すべてのページ固定を解除しました（{0} 件） | All pinned pages unpinned ({0}) | 已解除全部页面锁定（共 {0} 页） |
| `PageLock.KeepOneUnlocked` | 未固定のページを 1 ページ以上残してください | At least one unlocked page must remain | 至少保留一个未锁定页面 |
| `PageLock.Full` | 固定できる上限（{0} 件）に達しました | Page lock limit reached ({0}) | 已达锁定上限（{0} 页） |
| `PageLock.ToolTip` | ページ固定 | Page lock | 页面锁定 |
| `PageLock.ToolTipCount` | ページ固定（{0}） | Page lock ({0}) | 页面锁定（{0}） |
| `PageLock.Close` | ページ固定を解除 | Unpin page | 解除页面锁定 |
| `PageLock.PageFormat` | P.{0} | P.{0} | P.{0} |
| `PageLock.Locked` | ページを固定しました: {0} | Page pinned: {0} | 已锁定页面：{0} |
| `PageLock.Unlocked` | ページ固定を解除しました | Page unpinned | 已解除页面锁定 |
| `PageLock.CannotLock` | このページは固定できません | This page cannot be pinned | 该页面无法锁定 |
| `PageLock.AutoUnlocked` | 単ページ表示のためページ固定を解除しました（{0} 件） | All pinned pages unpinned because single-page mode is active ({0}) | 已切换到单页显示，已解除页面锁定（共 {0} 页） |
| `UnlockAllPageLocksCommand` | すべてのページ固定を解除 | Unpin all pages | 解除全部页面锁定 |
| `UnlockAllPageLocksCommand.Menu` | すべてのページ固定を解除 | Unpin all pages | 解除全部页面锁定 |
| `UnlockAllPageLocksCommand.Remarks` | 固定しているすべてのページを解除します。 | Unpin all pinned pages. | 解除所有已锁定的页面。 |

删除：`PageLock.Replaced`。

---

## 附录 B：技术可行性验证（spike，2026-11-25）

| 验证点 | 结论 |
|---|---|
| `PageFrame` 能否由「过滤后的元素列表」构成 | ✅ 构造函数接收任意 `PageFrameElement` 列表；`FrameRange = PageRange.Marge(elements)`（锁定页造成的间隔被覆盖）→ 导航/页码语义不变 |
| 帧内容重排 | ✅ `PageFrameContent.CreateContents/UpdateElementLayout` 按 `_pageFrame.Elements` 生成/排布 → 元素减少即自然重排 |
| 帧刷新时机 | `PageFrameContainerCollection.SetDirty(PageFrameDirtyLevel.Heavy)` + `PageFrameBox.UpdateContainers` 可强制重建；需在 `PageFrameBox` 增加公开刷新入口 |
| 全锁定帧 | 元素为空 → 由工厂跳过并前进到下一含未锁定页的范围（D11） |
| 自适应页数下限 | `AdaptivePagePacker.GetFramePageCount` 已保证 `Math.Max(count,1)` → R2 页数保底天然成立；过滤后再校验元素非空 |
| 统一缩放 | 显示行容器 + `LayoutTransform`；WPF 自动换算命中测试。主视图内部布局按自然尺寸计算，缩放只影响渲染 |
| 需放宽的断言 | `PageFrame.AssertSources`（DEBUG）要求元素页面连续 → 允许锁定页间隔 |
| 需注意的既有行为 | `PageFrameContent.SetSource` 使用 `Zip(_pageFrame.Elements)` 数量对齐；过滤后帧元素数量变化必须走「重建 content」而非 `SetSource` |

---

## 附录 C：实现记录（2026-11-25）

### C.1 实际变更文件

**新增**

| 文件 | 内容 |
|---|---|
| `NeeView/PageLock/PageLockItem.cs` | 锁定项模型（快照、来源书、参照格尺寸、当前页高亮） |
| `NeeView/Command/Commands/UnlockAllPageLocksCommand.cs` | 「解除全部页面锁定」命令（Ctrl+Shift+L） |
| `NeeView.UnitTest/PageLockLayoutTest.cs` | 布局 / R1 / R2 单测 23 例 |
| `design/page-lock-multi.md` | 本文档 |

**修改**

| 文件 | 内容 |
|---|---|
| `NeeView/PageLock/PageLock.cs` | 单页 → 多页集合；R1；布局/统一缩放；`LockChanged`；线程安全快照；自动解除 |
| `NeeView/PageLock/PageLockTools.cs` | 参照格长度、参照区长度、主视图保底、`RowScale`、`ViewMargin`、R1/R2 判定、上限 8 |
| `NeeView/MainView/MainView.xaml` / `.xaml.cs` | 显示行（DockPanel + 参照区 ItemsControl + LayoutTransform）、逐格关闭、`LockChanged` → 帧刷新 |
| `NeeView/PageFrames/PageFrameFactory.cs` | 锁定页过滤、全锁定帧跳过、非连续元素帧生成 |
| `NeeView/PageFrames/PageFrame.cs` | 帧范围可指定、`IsAutoRotateEnabled`、放宽断言 |
| `NeeView/PageFrames/PageFrameBox.cs` | `RefreshPageLockFilter()` |
| `NeeView/Command/Commands/TogglePageLockCommand.cs` | 移除勾选绑定（多值状态） |
| `NeeView/Command/CommandTable.cs`、`NeeView/Menu/MenuTree.cs` | 新命令注册 / 菜单项 |
| `NeeView/AddressBar/AddressBarViewModel.cs` | 多页 Toggle 语义 |
| `NeeView/Languages/*.restext`（9 种） | 新增 `PageLock.*` / `UnlockAllPageLocksCommand*` 资源键，删除 `PageLock.Replaced` |
| `design/page-lock.md` | 标注被取代条款 |

### C.2 验证结果

- `dotnet build NeeView.sln -c Debug`：**0 警告 / 0 错误**
- `dotnet test --filter "FullyQualifiedName~PageLock"`：**38 通过**
- 全量测试：**174 通过，2 失败**（均为既有环境依赖用例：`FileTreeBasicTest` 固定路径、`Sandbox.TextResourceTest` 语言）
- **运行时 API 级验证**（最小化窗口 + 事件脚本，不干扰桌面）：
  - 双页画面（2 页）锁定当前页后，主视图 `ViewPages` 从 2 页变为 1 页（锁定页不再显示）✅
  - 锁定当前页后触发页面变更事件（当前页自动前进）✅
  - 单页画面锁定被 R1 拒绝（锁定后仍为 1 页、无异常）✅
  - 上述操作期间进程无崩溃/断言失败 ✅
- **GUI 视觉验证未执行**（验证时本机桌面被其他应用占用）；请按 §8.3 人工确认参照区排列、统一缩放与解除流程。

### C.3 已知实现差异 / 限制

1. 帧范围不保留原范围：先头/末尾锁定页会使帧范围收窄（用于 D10 自动前进）；中间锁定页保留在范围内但不渲染。
2. 自适应显示的帧装填仍按原始页面分组，锁定页过滤后该帧可见页数变少（不会自动补页）。后续可考虑在 `AdaptivePageFrameMap` 层面按未锁定页装填。
3. 页面重新加载（ReLoad）后 Page 实例变化，旧快照不会过滤新页面（既有跨书快照语义的延伸）。
4. GUI 缩放的输入命中由 WPF `LayoutTransform` 换算，需人工确认拖拽/滚轮/右键菜单在缩放状态下的行为。

