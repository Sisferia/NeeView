# 自适应显示（Adaptive Page Mode）实现规划

> 状态：**已实现（P0–P3 完成，构建通过，单元测试通过）**
> 目标仓库：`Sisferia/NeeView`（fork of `neelabo/NeeView`，分支 `master`，HEAD `686a43362 chore: v46.3`）
> 影响范围：`NeeView/PageFrames`（核心）、`NeeView/Book`、`NeeView/Command`、`NeeView/Menu`、`NeeView/Styles`、`NeeView/Languages`
> 本文中所有 `文件:行号` 均基于上述 HEAD 的实测代码，行号仅作导航锚点，改动后需重新定位。
> 实现记录与最终决策见 **附录 D**。

---

## 0. 结论摘要（TL;DR）

- **可行性：高。** `PageFrame` / `PageFrameContent` / `ContentSizeCalculator` 本身已经能处理任意个页元素，
  改动集中在“分组算法 + 模式判定 + UI/本地化”，不需要重写渲染或布局。
- **核心新增 3 件东西：**
  1. `PageMode.Adaptive` 枚举值与模式判定（替换写死 1/2 的 `GetFramePageSize`）；
  2. `AdaptivePagePacker`（纯算法：按画布宽高比贪心装填，可单测）；
  3. `AdaptivePageFrameMap`（规范化前向分组表：保证正向/反向取帧一致，带缓存与失效）。
- **判据：** 用「把每张图缩放到画布高度后得到的宽度」累加，当再加一张会超过画布宽度时停止：
  `Σ(w_i/h_i) ≤ W/H`（严格，不含越界页）。这样整行**高度恰好铺满画布**，且张数是该约束下的最大值。
  用户示例（600/200/100/500 @ 1200×800）得到 **3 张**（900 ≤ 1200；加第四张 1400 > 1200）。
- **最大风险：** 分组必须“从书首前向推导”，否则前后翻页会出现跳页/重叠（§3.4 有反例）；
  其次是后台线程建帧（`BookPageLoader`）对分组表的并发访问。
- **环境已验证：** `dotnet build NeeView.sln -c Debug` 0 警告 / 0 错误；`dotnet test` 新增 11 个分割算法单测全部通过。

---

## 1. 需求

### 1.1 需求原文

在现有页面显示模式的“单页显示”“双页显示”基础上，新增“自适应显示”：
根据当前程序窗口宽度，按浏览顺序尽量一次显示多张图片。
例：窗口宽 1200，待显示图片宽度依次为 600、200、100、500，
则显示前三张（600+200+100=900 ≤ 1200，再加 500 则 1400 > 1200）。

### 1.2 需求规则化

| 编号 | 规则 | 说明 |
|---|---|---|
| R1 | 三态模式 | `PageMode` 增加第三态，与现有两态并列可选、可切换 |
| R2 | 顺序装填 | 从当前页开始，沿阅读顺序向后取页，前缀式（不可跳过中间页） |
| R3 | 宽度预算 | 每页按“缩放到画布高度”后的宽度累加，以主视图画布宽度为预算 |
| R4 | 填满 + 尽量多 | 严格不超预算：整行高度恰好铺满画布，张数为该约束下的最大值；至少 1 页 |
| R5 | 宽度驱动 | 窗口宽度变化后，重算分组（可能多一张或少一张） |
| R6 | 不破坏现有模式 | 单页/双页行为、设置、书签历史、脚本 API 保持兼容 |

> **决策记录（2026-11-25）**：① 以主视图画布宽度为准；② ~~允许略微缩小以铺满宽度~~ →
> **改为“高度优先”**：不纳入越界页，保证同时显示的图片高度都铺满画布；③ 单屏页数上限不暴露到设置；
> ④ 翻页按“整组”移动；⑤ 竖排（Vertical）模式改为纵向堆叠（横屏对称：宽度铺满画布）。

### 1.3 验收清单（功能）

- [x] 模式菜单 / 快捷键 / 地址栏按钮可在 单页 → 双页 → 自适应 之间切换（含循环切换命令）。
- [x] 自适应模式下，一屏内按顺序显示 N 张图，**行内等高且高度铺满画布**，间距沿用双页模式的“页间距”。
- [x] 例：1200×800 画布、四张 600×800 / 200×800 / 100×800 / 500×800 的图，显示前 3 张（900×800，高度铺满）。
- [x] 拖动窗口宽度，分组随之变化，且不发生图片重叠、闪烁、错位。
- [x] 前后翻页、页码跳转、页面滑块、循环浏览均可用。
- [x] 设置可持久化（下次启动/重新打开同一本书仍为自适应模式）。
- [ ] 关闭该模式后，单页/双页行为与改动前完全一致（回归测试待人工确认）。

---

## 2. 现状架构（为什么这个需求是可行的）

### 2.1 关键概念

```
Book(Page 列表)
  └─ PageRange        一个“位置区间”，PartSize=2 表示整页，=1 表示被分割的半页
       └─ PageFrame   一个“画面单元”，持有 1..N 个 PageFrameElement
            └─ PageFrameContainer  画面单元 + 变换 + 布局
                 └─ PageFrameBox   容器集合、翻页、滚动、填充视口
```

- **`PageFrame` 已经支持任意个元素**：构造函数接收 `IEnumerable<PageFrameElement>`（`NeeView/PageFrames/PageFrame.cs:45`），
  尺寸计算按“宽度求和 / 高度取最大”进行（`PageFrame.cs:139`），缩放按整帧计算（`PageFrame.cs:62`）。
  （唯一例外是 DEBUG 断言 `AssertValid` 仍按 1/2 元素硬编码，见 §4.4。）
- **元素排布已经支持任意个元素**：`PageFrameContent.UpdateElementLayout()` 用一条 `foreach` 依次横向排布，
  并用 `_pageFrame.Span` 作为间距（`PageFrameContent.cs:460`）。
- **等高对齐已经支持任意个元素**：`ContentSizeCalculator.CalcContentScale()` 对 `WidePageStretch.UniformHeight/UniformWidth`
  逐元素计算缩放（`ContentSizeCalcurator.cs:240`）。

**结论：渲染层、布局层、尺寸层几乎不需要改动；工作量集中在“帧工厂的分组逻辑 + 模式判定 + 交互/配置/本地化”。**

### 2.2 现有两种模式的实现链路

| 环节 | 单页显示 | 双页显示 |
|---|---|---|
| 枚举 | `PageMode.SinglePage`（`Book/PageMode.cs:7`） | `PageMode.WidePage` |
| 每帧页数 | `ConfigExtensions.GetFramePageSize()` → 1（`Config/ConfigExtensions.cs:10`） | → 2 |
| 帧生成 | `PageFrameFactory.CreatePageFrame()` 走 else 分支 → `CreateSinglePageFrame()`（`PageFrameFactory.cs:209`、`:240`） | 走 `FramePageSize == 2` 分支 → `CreateWidePageFrame()`（`PageFrameFactory.cs:175`、`:293`） |
| 特殊规则 | 横长页可分割（`IsSupportedDividePage`） | 横长页独占一帧、首/末页单页、补空白页 |
| 页面移动 | `MoveToNextPage()` 直接转 `MoveToNextFrame()`（`PageFrameBox.cs:981`） | 按帧内位置逐页推进（`PageFrameBox.cs:996`） |

### 2.3 窗口尺寸变化的现有响应链（对本需求至关重要）

```
PageFrameScrollViewer.SizeChanged
  → PageFrameContext.SetCanvasSize()      (PageFrameBox.cs:134, PageFrameContext.cs:422)
      → PageFrameProfile.CanvasSize 变更
      → PageFrameContext.OnPropertyChanged(CanvasSize) + SizeChanged 事件
  → PageFrameBox.Context_SizeChanged()    (PageFrameBox.cs:414)
      → UpdateContainers(PageFrameDirtyLevel.Moderate, ...)   (PageFrameBox.cs:453)
          → _containers.SetDirty(Moderate) → 重新调用 PageFrameFactory 生成帧
          → 帧内容与旧帧不同 → 视图内容重建
```

`PageFrameDirtyLevel.Moderate` 的语义是“PageFrame 信息有差异就更新视图”（`PageFrameDirtyLevel.cs:14`），
所以**只要帧工厂在宽度变化后返回不同的帧，现有管线就会自动重建**，这是本方案能低成本落地的第二个关键点。

### 2.4 页面尺寸的可得性（决定算法边界）

- 每页的基准显示尺寸来自 `PageFrameElement.RawSize`（`PageFrameElement.cs:31`），
  由 `PageViewSizeCalculator.GetViewSize()` 得到，已包含：宽高比归一化、自定义尺寸、裁剪（`PageViewSizeCalculator.cs:69`）。
- 图片头信息是**异步、按需**加载的（`PageContent.LoadPictureInfoAsync`，`Page/PageContent.cs:197`）。
- 尚未加载的页，`PageContent.PageDataSource.Size` 返回 `UndefinedSize`（默认 480×640，并会跟随当前选中页更新，`PageContent.cs:24`、`:145`）。
- 尺寸确定后会触发 `Page.SizeChanged`，`PageFrameContent` 通过 `IsPageFrameDirty()` 比对 `PageSize != Page.Size`
  请求重建（`PageFrameContent.cs:259`）。

→ 因此：**未知尺寸可以先用估算值参与分组，加载完成后再重排**，无需引入新的等待逻辑。

---

## 3. 核心算法设计

### 3.1 把“像素宽度和”换算成与尺寸无关的判据

设画布宽高为 `(W, H)`（DIP，`PageFrameContext.CanvasSize`，`PageFrameContext.cs:129`），
第 i 页的基准尺寸为 `(w_i, h_i)`（即 `RawSize`），宽高比 `a_i = w_i / h_i`。

帧内采用“等高对齐”（`WidePageStretch.UniformHeight`，默认值，`ContentSizeCalcurator.cs:266`）：
所有页缩放到同一高度 `h`，则第 i 页显示宽度为 `w_i · h / h_i = a_i · h`。

整帧尺寸 = `(h · Σa_i, h)`，`ContentSizeCalculator.CalcFrameStretchScale()` 以 `Uniform` 模式取
`s = min(W / (h·Σa_i), H / h)`（`ContentSizeCalcurator.cs:153`）。

- 若 `Σa_i ≤ W/H`：`s = H/h`，整行**铺满窗口高度**，宽度 = `H·Σa_i ≤ W`（右侧留白）。
- 若 `Σa_i > W/H`：`s = W/(h·Σa_i)`，整行**铺满窗口宽度**，但每张图小于窗口高度。

用户要求的“按画布高度缩放后的宽度累加”，正是第一种情形，即：

> **预算 `A = W / H`（画布宽高比），判据 `Σ a_i ≤ A`（严格）。**

> **页间距修正**：`PageFrame` 会把 `(N-1) × ContentsSpace` 计入总宽（`PageFrame.cs:61`、`:150`）。
> 严格判据应为 `Σa_i ≤ (W - (N-1)·span) / H`，即每加入一页要多扣一个间距。
> 默认 `ContentsSpace = -1`（`BookConfig.cs:30`），实际被 `Math.Max(0.0, …)` 钳成 0，
> 因此常规情况下 §3.1 的简化判据成立；实现时统一按 `span = Math.Max(0, ContentsSpace)` 代入即可。

> **范围声明：单行（或单列）装填**（一屏 = 一组 = 一行/一列，永不换行/换列）。
> 横向模式排成一行；竖向模式（`PageFrameOrientation.Vertical`）排成一列，见 §3.6 #13。
> 多行/网格式排版会破坏 `PageFrame.GetRawContentSize()` 的单轴假设，明确不在本期范围。

### 3.2 与用户示例的一致性验证

窗口 1200×800（`A = 1.5`），四张图均为 800 高：600/200/100/500 宽 → `a = 0.75, 0.25, 0.125, 0.625`。

| 步骤 | 加入页 | Σa | 判定 | 等价像素宽度（按画布高 800 缩放） |
|---|---|---|---|---|
| 1 | #1 | 0.75 | 未超预算，纳入 | 600 |
| 2 | #2 | 1.00 | 未超预算，纳入 | 800 |
| 3 | #3 | 1.125 | 未超预算，纳入 | 900 |
| 4 | #4 | 1.75 | **超预算，不纳入** | （1400 > 1200） |

结果：显示前 3 张，`Σa = 1.125 < 1.5` ⇒ 帧高度受限 ⇒ 整行高度 = 画布高度 800，宽度 900（右侧留白）。
同时这也是“高度铺满”约束下能显示的最大张数。

### 3.3 算法（单帧贪心）

```
输入: 起点页 s（组内第一页）, 画布宽高 (W,H), 页间距 span, 页数上限 K
输出: 分组 [s..e]

span   ← max(0, ContentsSpace)
sum    ← 0
count  ← 0
i      ← s
while i ≤ last and count < K:
    (w, h) ← viewSize(i)
    a ← (w > 0 && h > 0) ? w / h : 1.0          # 退化尺寸兜底
    budget(n) ← (W - (n-1)*span) / H             # n 页时的可用预算
    if count ≥ 1 and sum + a > budget(count+1): break   # 超出预算 → 不纳入
    sum ← sum + a;  e ← i;  count ← count + 1;  i ← i + 1
```

要点：
- 预算以 **画布宽高比** 表达（`W/H`），不含任何像素尺度，因此与图片分辨率无关；
  它等价于“把每张图缩放到画布高度后的宽度之和 ≤ 画布宽度”。
- **严格不超预算**：保证 `Σa ≤ W/H`，于是 `Uniform` 适配时缩放由高度决定（`s = H/h`），
  整行高度恰好等于画布高度；同时“能放下的最大张数”由前缀和贪心保证。
- 唯一例外：单张图的 `a` 本身就超过 `W/H`（超宽图）时，该帧只能按宽度适配，高度会小于画布。
- 达到页数上限 `K` 时停止（此时高度仍然铺满，只是右侧留白更多）。

参数与兜底：

| 参数 | 取值 | 说明 |
|---|---|---|
| 画布宽高 | `PageFrameContext.CanvasSize` | 布局未完成时可能为 32×32（`PageFrameProfile.cs:20`），会因 `CanvasSize` 变更而重建，可接受 |
| 页数上限 `K` | `PageFrameContext.AdaptiveMaxPageCount = 8`（常量，不暴露到设置） | 防止超长条图一帧塞入十几张导致性能/可读性下降 |
| 退化尺寸 | `w ≤ 0 || h ≤ 0 || NaN` → `a = 1.0` | `RawSize` 理论上不会为 0，但自定义尺寸/裁剪配置可能产生 0 宽 |
| 竖排 | 测量值改为 `h/w`，预算改为 `H/W` | 见 §3.6 #13 |

### 3.4 必须解决的问题：分组的一致性（本方案最关键的设计点）

`PageFrameFactory.CreatePageFrame(position, direction)` 会被**双向**调用：
向后翻页时，`EnsureLatestContainerNode(pos, Previous)` 传入的是“上一帧的最后一页”，工厂必须能反推出上一组的范围
（`PageFrameContainerCollection.cs:314`、`:348`；`PageFrameContainerFiller.cs:63`）。

若“反向也走贪心”，分组会与“正向贪心”不一致，出现**跳页/重叠/回退后画面错乱**。
反例（预算 A = 1.0，四页宽高比均为 0.3）：

| 方向 | 得到的分组 |
|---|---|
| 正向从第 0 页 | `[0,1,2]`、`[3]` |
| 反向从第 3 页 | `[1,2,3]`、`[0]` ← 与正向矛盾 |

双页模式之所以没这个问题，是因为每组页数固定（横长页/首末页等特殊情形另有 `IsStaticWidePage` 的
索引对齐兜底，`PageFrameFactory.cs:216`），双向取帧结果一致；**可变长度分组打破了这种对称性**。

#### 设计决策：规范化前向分组（canonical forward partition）

把“页流”当作文本排版中的“行”：
**行边界只从书的第一页开始向前推导，与当前浏览位置/方向无关。**

- 分组表 `G = { [s_0..e_0], [s_1..e_1], ... }` 是 (书, 画布宽度, 各页尺寸) 的纯函数。
- `CreatePageFrame(position, ±1)` 无论方向，都先求 `s = 包含 position.Index 的组起点`，再按组内顺序构建元素，
  最后按 `direction` 决定元素排列方向（沿用现有 `.Direction(direction)` 写法，`PageFrameFactory.cs:295`）。
- 这样任意方向的帧都来自同一张表 → **绝不重叠、绝不跳页**。

#### 分组表的数据结构与生命周期

新增 `NeeView/PageFrames/AdaptivePageFrameMap.cs`（约 150 行）：

```csharp
public sealed class AdaptivePageFrameMap : IDisposable
{
    // 前方扫描确定的各帧首页 index（升序）
    private readonly List<int> _frameStarts = new();

    public PageRange GetFrameRange(int index);   // 包含 index 的帧范围；必要时向前补算
    public int GetFrameStart(int index);
    public void InvalidateFrom(int index);       // 某页尺寸变化后，从该页所在帧开始失效
    public void InvalidateAll();                 // 画布宽度/页列表/显示配置变化
}
```

- **惰性前进**：只算到“当前需要的页”为止，不预先扫描全书。复杂度 `O(到当前页的组数)`，
  每组只做一次除法（宽高比），1 万页量级为毫秒级。
- **缓存**：`_frameStarts` 每个元素 4 字节；另可缓存每页 `a_i`（`double`）避免重复计算。
- **失效条件**：

| 触发 | 处理 |
|---|---|
| `CanvasSize` 宽度变化 | `InvalidateAll()`（由 `PageFrameBox.Context_SizeChanged` 之后调用，见 §5.2） |
| 页列表变化（换书、排序、增删） | `InvalidateAll()`（复用 `BookContext.SubscribePagesChanged`，`PageFrameBox.cs:67`） |
| 某页 `Page.SizeChanged`（加载完成/尺寸修正） | `InvalidateFrom(page.Index)`（订阅 `Page.SubscribeSizeChanged`） |
| 影响显示尺寸的配置变化（自定义尺寸/裁剪/宽高比归一化） | `InvalidateAll()`（这些变化已触发 `UpdateContainers(Heavy)`，`PageFrameBox.cs:640`） |
| 模式切出自适应 | 分组表停用；再次切回时 `InvalidateAll()` 重新计算 |

- **线程安全**：`BookPageLoader.LoadAheadCoreAsync()` 在 MTA 后台线程调用 `CreatePageFrame()`
  （`BookPageLoader.cs:330`、`:344`），因此分组表必须加锁（`lock`）并保证“只追加/整表失效”的单调语义。
  两侧偶发取到不同分组不会造成错误，只是多读/少读一页。

#### 备选方案（不推荐，但记录取舍）

| 方案 | 做法 | 缺点 |
|---|---|---|
| A. 锚点贪心 | 反向时以锚点页为末页向左贪心 | 交叉验证失败（§3.4 反例），会产生跳页 |
| B. 固定页数 | 由窗口宽度算出固定 k 页/帧（如双页的推广） | 不满足 R3（不能按图片宽度自适应） |
| C. 全局预计算 | 换书/换宽度时一次性算完整表 | 大书浪费；且未加载页尺寸未知，仍需惰性 |

**结论：采用“规范化前向分组 + 惰性补算”，即上面的设计。**

### 3.5 未知尺寸页的处理策略

| 场景 | 表现 | 缓解 |
|---|---|---|
| 目标页尚未加载 | 用 `UndefinedSize`（≈ 当前页尺寸）估算，分组可能与最终不同 | 加载完成触发 `Page.SizeChanged` → `InvalidateFrom` → 该帧重建（现有 `IsPageFrameDirty` 机制已覆盖单页/双页，自适应下需扩展到“帧尾后续页”） |
| 快速拖拽滑块到远端 | 沿途页尺寸估算，重排可能“跳一下” | 属可接受表现；P4 可加“动画期间暂停重排”优化 |

### 3.6 边界条件清单

| # | 场景 | 期望行为 | 实现要点 |
|---|---|---|---|
| 1 | 单页书（只有 1 页） | 1 页/帧 | 贪心自然退化为 1 页 |
| 2 | 最后一组不足预算 | 照常显示，不补空白页 | 不使用 `IsInsertDummyPage` |
| 3 | 横长页（扫描件左右两页连在一起） | 按宽高比自然占更多预算，通常独占一行 | 忽略 `IsSupportedWidePage`（它只属于双页模式） |
| 4 | 首/末页单页设置 | 不适用，忽略 | `IsSupportedSingleFirstPage/LastPage` 仅双页模式生效 |
| 5 | 页面分割（`IsSupportedDividePage`） | 不适用，忽略 | 该设置只在 `PageMode.SinglePage` 生效（`PageFrameContext.cs:119`） |
| 6 | 媒体书（视频） | 保持 1 页/帧 | 复用 `BookContext.IsMedia`（`PageFrameFactory.cs:175` 同款判断） |
| 7 | 循环浏览（`PageEndAction.SeamlessLoop`） | 分组不跨界回绕，末尾组可能较短 | `CreatePageSource` 在书末返回 null 时停止装填 |
| 8 | 连续滚动（`IsPanorama`） | 允许，分组照常；但“窗口宽度”约束意义减弱 | 文档注明；P4 可考虑在 Panorama 下退化为单页 |
| 9 | 自动旋转（`AutoRotatePolicy`） | N>1 时不旋转整帧 | `PageFrame` 目前对整帧计算旋转（`PageFrame.cs:56`），需新增开关 |
| 10 | 空白/损坏图片 | 跳过或按 1:1 处理，不崩溃 | §3.3 退化尺寸兜底 |
| 11 | 画布极小（启动瞬间 32×32） | 1 页/帧，随后重建 | 依赖 `CanvasSize` 变更事件 |
| 12 | DPI 缩放 | 无影响（全部使用 DIP） | `DpiScale` 已在 `OneScale()` 中处理（`ContentSizeCalcurator.cs:279`） |
| 13 | 竖向模式（`PageFrameOrientation.Vertical`） | **纵向堆叠**：等宽对齐，按画布宽度装填（一屏一列），列宽恰好等于画布宽度 | `PageFrame.StackOrientation` 驱动 `GetRawContentSize`/`GetStretchedContentSize`/`UpdateElementLayout`；对齐方式强制 `UniformWidth` |

---

## 4. 改动清单（按文件）

### 4.1 枚举与判定

| 文件 | 改动 |
|---|---|
| `NeeView/Book/PageMode.cs:7` | 增加 `Adaptive` 成员（`[AliasName]`），保持 `SinglePage=0` 不变（默认值兼容） |
| `PageMode.cs:19` `GetToggle` | 无需改（`Enum.GetNames().Length` 自动变 3） |
| `PageMode.cs:33` `Validate` | 上界从 `WidePage` 改为 `Adaptive` |
| `NeeView/Config/ConfigExtensions.cs:10` | **删除** `GetFramePageSize()`（对未知模式会抛 `NotSupportedException`），改为模式判定扩展方法 |

新的判定（建议放在 `PageModeExtensions`）：

```csharp
public static bool IsMultiPageFrame(this PageMode mode) => mode != PageMode.SinglePage;
public static bool IsWidePage(this PageMode mode) => mode == PageMode.WidePage;
public static bool IsAdaptive(this PageMode mode) => mode == PageMode.Adaptive;
```

`GetFramePageSize()` 的直接调用点只有 3 处（`BookSettingAccessor.cs:109`、`Book.cs:184`、`PageSlider.cs:114`），
但它返回的 `FramePageSize` 属性另有 6 处使用（`PageFrameContext.cs:82`、`:120-122`、`PageFrameFactory.cs:175`、
`PageFrameBox.cs:981`、`PageFrame.cs:225`）。全部替换方案：

| 位置 | 现值 | 改为 |
|---|---|---|
| `PageFrameContext.cs:117` `FramePageSize` | 1 / 2 | 保留但改语义为“名义最大页数”：单页 1、双页 2、自适应 `AdaptiveMaxPageCount`；**并逐一检查下面 5 个使用点** |
| `PageFrameContext.cs:82` `ContentsSpace` | `FramePageSize == 2` | `PageMode.IsMultiPageFrame()`（自适应也要页间距） |
| `PageFrameContext.cs:120-122` `IsSupportedWidePage` 等 | `FramePageSize == 2` | 追加 `PageMode == PageMode.WidePage` |
| `PageFrameBox.cs:981` `MoveToNextPage` | `FramePageSize == 1` | `PageMode == PageMode.SinglePage` |
| `PageFrame.cs:225` `AssertValid` | `FramePageSize == 2` | 按模式分支，见 §4.4 |
| `PageFrameFactory.cs:175` | `FramePageSize == 2` | 拆成 `WidePage` 分支 + `Adaptive` 分支 |
| `BookSettingAccessor.cs:109` `CanPageSizeSubSetting` | `GetFramePageSize(...) == size` | `PageMode == PageMode.WidePage && size == 2`（自适应下“横长页独占”等命令置灰） |
| `Book/Book.cs:184` `lastPageOffset` | `== 2` | 保持仅双页模式生效（自适应不适用，需加注释） |
| `PageSelect/PageSlider/PageSlider.cs:114` | `!= 2` | `PageMode != PageMode.WidePage`（自适应不做偶数对齐） |
| `Book/BookTools.cs:20` `WidwPageAlignment` | 依赖 `IsStaticWidePage` | 自适应模式下直接返回原值（其调用点 `MoveToNextStep`，`PageFrameBox.cs:1065`） |

### 4.2 分组表

新增文件：`NeeView/PageFrames/AdaptivePageFrameMap.cs`

- 依赖：`PageFrameContext`（画布尺寸、模式）、`BookPageAccessor`（页访问，`PageFrameFactory.cs:24` 已在用）、
  `PageViewSizeCalculator`（取得 `a_i` 所需的 `RawSize`）。
- 对外 API 见 §3.4。
- 单元测试友好：把纯算法抽成静态方法
  `AdaptivePagePacker.GetFrameLength(IReadOnlyList<double> aspects, double width, double height, double span, int start, int maxCount)`
  （见附录 B；不引用 WPF，可直接进 `NeeView.UnitTest`）。

### 4.3 帧工厂

`NeeView/PageFrames/PageFrameFactory.cs`

| 位置 | 改动 |
|---|---|
| `:46 CreatePageFrame` | 前置：`position` 归一化为“所在组的起点”（自适应模式）；断言 `IsValidFramePosition` 改为“位置等于组起点” |
| `:85 FixFramePosition` | 自适应分支返回 `new PagePosition(groupStart, direction < 0 ? 1 : 0)`（与现有 part 规则一致） |
| `:170-213 CreatePageFrame(source, direction)` | 新增分支：`_context.PageMode.IsAdaptive() && !_bookContext.IsMedia` → `CreateAdaptivePageFrame(...)` |
| 新增 | `CreateAdaptivePageFrame(PageFrameElement source1, int direction)`：按 §3.3 取组内全部页 → `CreateMultiPageFrame()` |
| `:293 CreateWidePageFrame` | 重命名/泛化为 `CreateMultiPageFrame(IReadOnlyList<PageFrameElement>, direction)`，供双页与自适应共用（内部逻辑不变：`CalcContentScale` + `new PageFrame(sources, bookDirection, _calculator)`） |
| `:216 IsNeedSecondPageWhenStaticWidePage` | 仅双页模式使用，保持 |
| `:252 CreateWideFillPageFrame` / `:265 CanInsertDummyPage` | 仅双页模式使用，保持 |

### 4.4 帧与元素

| 文件 | 改动 |
|---|---|
| `PageFrame.cs:223 AssertValid` | 重写为：单页模式断言 1 元素；双页模式沿用原断言；自适应模式断言 `Elements.Count ≥ 1` 且每元素 `PageRange.IsOnePage()`、`PartSize == 2` |
| `PageFrame.cs:45` 构造函数 | 增加“是否允许整帧自动旋转”的入参（或由 `IContentSizeCalculatorProfile` 提供 `IsAutoRotateEnabled`）；自适应 N>1 时角度恒为 0 |
| `PageFrameElement.cs` | 无需改动（已具备 `RawSize`/`Scale`/`Width`） |

### 4.5 容器的脏检查与视口填充

| 文件 | 改动 |
|---|---|
| `PageFrameContent.cs:106` | 现有仅订阅“下一帧页”(`_nextPage`) 的尺寸变化；自适应需订阅“帧尾之后的若干候选页”（数量 = `AdaptiveMaxPageCount - 1`），任一尺寸变化即 `DirtyLevel = Moderate` |
| `PageFrameContent.cs:270 IsWidePageFrameDirty` | 增加自适应分支：判断“下一张能否塞进当前帧”，能则请求重建 |
| `PageFrameContent.cs:360` | `limitScale = 0.10` 的 TODO（帧尺寸下限）在 N 页时更容易触发，需要确认是否影响网格线/缩放基准（该项影响 `GetFrameSize()`，不是视口布局） |
| `PageFrameContainerFiller.cs:86 IsValidContainerFormat` | **需要修正**：`partSize == 2` 只匹配“单页帧”。`PagePosition.Value = Index*2 + Part`，故双页帧的 `FrameRange.PartSize` 已是 4，N 页帧为 `2N`；此判断对多页帧恒为 false，会在每轮填充时走“需要更新”分支（源码里对应的 `Debug.Assert(result)` 已被注释，疑为既有隐患）。建议改为 `partSize % 2 == 0`，并按模式放行分割页。 |

### 4.6 导航与交互语义

| 文件 | 改动 |
|---|---|
| `PageFrameBox.cs:976 MoveToNextPage` | 自适应按 §5.1 语义处理（下一组） |
| `PageFrameBox.cs:1020 MoveToNextFrame` | 无需改（本来就是“下一组”） |
| `PageFrameBox.cs:1057 MoveToNextStep` | 目标页由分组表求，`WidwPageAlignment` 在自适应下返回原值 |
| `PageFrameBox.cs:870 CorrectPosition` | 无需改（只做越界钳制与循环归一化） |
| `PageFrameBox.cs:628-633` | `PageMode` / `FramePageSize` 变更已走 `UpdateContainers(Heavy, ...)`，自适应沿用 |
| `PageFrameBox.cs:414 Context_SizeChanged` | 在 `UpdateContainers` 前调用 `_adaptiveMap.InvalidateAll()`（若宽度变化），必要时加 100ms 防抖（P4） |
| `PageFrameBox.cs:87` | 构造 `AdaptivePageFrameMap` 并注入 `PageFrameFactory`、`BookPageLoader` |

### 4.7 配置

| 文件 | 改动 |
|---|---|
| `Config/BookConfig.cs`（双页区块附近，`:38-53`/`:98`） | 新增 `AdaptiveMaxPageCount`（`[PropertyRange(1, 20)]`，默认 8，参考 `BookThumbnailDepth` 的写法 `BookConfig.cs:291`） |
| `Setting/SettingPageBook.cs:38-53` | 在 “双页显示” 区块后新增 “自适应显示” 区块（`SettingPage.Book.Adaptive`），放 `AdaptiveMaxPageCount`；并注明“页间距/垂直对齐沿用双页设置” |
| `BookSettingConfig.cs:13` | `PageMode` 默认值仍为 `SinglePage`（不变） |
| `Config/BookSettingPolicyConfig.cs` | 无需改（`PageMode` 策略项自动适用） |

> 设计取舍：**复用** `BookConfig.ContentsSpace`（页间距）、`WidePageVerticalAlignment`（垂直对齐）、
> `WidePageStretch`（对齐方式，自适应强制按 `UniformHeight` 语义，其它值按实际配置生效）。
> 新增独立设置项会让设置界面膨胀，且对用户无实益。

### 4.8 命令 / 菜单 / 地址栏 / 图标

| 文件 | 改动 |
|---|---|
| 新增 `Command/Commands/SetPageModeAdaptiveCommand.cs` | 仿 `SetPageModeTwoCommand.cs`：`Group = CommandGroup.PageSetting`、`ShortCutKey = "Ctrl+3"`、`IsShowMessage = true`、`BindingGenerator.PageMode(PageMode.Adaptive)`、`CanExecute` 用 `BookSettings.Current.CanEdit` |
| `Command/CommandTable.cs:280` | 在 `SetPageModeTwoCommand` 之后注册 `new SetPageModeAdaptiveCommand()` |
| `Menu/MenuTree.cs:172` | “页面”菜单在 `SetPageModeTwo` 之后插入 `SetPageModeAdaptive` |
| `AddressBar/AddressBarView.xaml:113-128` | 增加第三个 `DataTrigger`（`BookSetting.PageMode == Adaptive` → 新图标 + `TextBinding PageMode.Adaptive`）；注意 Trigger 顺序（WidePage 在前、Adaptive 在后，或改用 `MultiDataTrigger`） |
| `Styles/IconGeometries.xaml:101` | 新增 `g_view_week_24px`（Material `view_week`，三竖条，最贴合“多图并排”） |
| `Styles/MenuIcons.xaml:69` | 新增 `ic_view_week_24px`（引用上面的 geometry，写法同 `ic_looks_two_24px`） |

### 4.9 本地化（10 个语言文件）

| 键 | en | ja | zh-Hans | 说明 |
|---|---|---|---|---|
| `PageMode.Adaptive` | Adaptive | アダプティブ表示 | 自适应显示 | 枚举别名 |
| `SetPageModeAdaptiveCommand`（`shared.restext`） | `@PageMode.Adaptive` | 同 | 同 | 命令名复用枚举别名 |
| `BookConfig.AdaptiveMaxPageCount` | Max pages per screen | 1 画面の最大ページ数 | 单屏最大图片数 | 设置项 |
| `BookConfig.AdaptiveMaxPageCount.Remarks` | … | … | … | 说明文字 |
| `SettingPage.Book.Adaptive`（如需分组标题） | Adaptive display | アダプティブ表示 | 自适应显示 | 设置区块 |

需同步：`en.restext`、`ja.restext`、`zh-Hans.restext`、`zh-Hant.restext`、`ko.restext`、`ru.restext`、
`es.restext`、`pt-BR.restext`、`it-IT.restext`、`shared.restext`。
（缺失时 `TextResources.GetStringRaw` 返回 null，UI 会显示 `@Key` 字面量并在 DEBUG 下输出告警，
`System/AliasNameAttribute.cs:124`。）

### 4.10 持久化与兼容

| 位置 | 改动 | 兼容性 |
|---|---|---|
| `Book/BookMemento.cs:112` `ToPropertiesString` | 已有 `PageMode != default → PageMode.ToString()`，自动输出 `Adaptive` | 旧版本读到未知 token 会命中 `default: Debug.Assert(false)`（`BookMemento.cs:262`），Release 下忽略 → 降级为单页 |
| `BookMemento.cs:201` `ParseWithProperties` | 增加 `case nameof(PageMode.Adaptive): memento.PageMode = PageMode.Adaptive;` | 必须显式加，否则书签历史里该模式丢失 |
| 配置文件（`Config`，字符串枚举 `JsonEnumFuzzyConverter.cs:44`） | 自动写入 `"PageMode": "Adaptive"` | 旧版本解析失败 → `default`（`SinglePage`），安全降级 |
| 设置格式版本 `FormatVersion` | 无需提升（新增字段，无迁移） | — |
| 书签历史（`BookHistory`） | 走 `BookMemento`，见上 | — |

### 4.11 脚本 / API 暴露面

- `Script/BookConfigAccessor.cs:14` 的 `ViewPageSize` 是 `(int)PageMode + 1`，自适应下自然变成 `3`，
  只需修正 `PageMode.Validate()`（否则 setter 会把 3 夹回 `WidePage`），无需新增 API。
- `Script/ScriptManual.cs:311` 通过 `VisibleAliasNameDictionary()` 生成枚举文档，自动包含 `Adaptive`。

### 4.12 其他耦合点（全仓审计补充，按严重度排序）

除 §4.1–§4.11 已列项外，另有一批“1~2 页”隐式假设。它们不是功能阻塞点，但会在 N 页时表现为文案/导出/缩放读数不准确，
建议在 P3/P4 一并处理或至少在 PR 描述中标注为已知限制。

| # | 严重度 | 位置 | 问题 | 建议 |
|---|---|---|---|---|
| 1 | 高 | `PageFrames/PageFrameContainerFiller.cs:86` | 见 §4.5：`partSize == 2` 对多页帧恒 false | P1 一并修正为 `partSize % 2 == 0` |
| 2 | 中 | `MainView/PageTitle.cs:83`、`MainWindow/WindowTitle.cs:85` | `contents.Count >= 2` 即用“双页标题格式”，N 页无法表达 | P3：`Count >= 2` 保持（至少可用），或新增 N 页格式 |
| 3 | 中 | `MainWindow/TitleStringFormatter.cs:149` | 占位符 `{Page2}/{Name2}` → `elements.Last()`；N 页时语义变成“最后一页” | 文档说明；或 `{PageN}` 映射到第 N 个元素 |
| 4 | 中 | `ExportImage/ExportImageFileNamePolicy.cs:50`、`ExportFileNameFormat.cs:101` | 文件名格式按 `Count >= 2` 选双页格式；`{Page2}` 同上 | 同上，P3 决定策略 |
| 5 | 中 | `PageFrames/SelectedContainer.cs:35` | “当前页” 恒取 `Elements[0]` | N 页时仍以组首为当前页（与导航语义一致），确认即可 |
| 6 | 中 | `PageFrames/PageFrameContainerCollection.cs:47` | 缩放读数用 `Elements.First().Scale`，多元素且逐元素缩放时不准 | P4：改用元素 0 的分母/加权（影响仅“原尺寸比例”显示） |
| 7 | 低 | `PageFrames/PageFrame.cs:29-37` | 仅有 1 元/2 元便捷构造 | 新增 `IReadOnlyList<PageFrameElement>` 便捷构造，避免调用方拼集合 |
| 8 | 低 | `PageFrames/PageFrame.cs:171` `IsSinglePortraitElement` | 仅在单元素或含 dummy 时返回 true | 自适应不走该分支，登记即可 |
| 9 | 低 | `PageFrames/PageFrameTransformMap.cs:22` | 逐帧变换状态以首元素为 key | N 页重排时可能复用旧变换；确认“重排后保持缩放”是否期望 |
| 10 | 低 | `MainView/MainViewComponent.cs:241` | 原尺寸显示计算只看前两个内容 | P4 轮询确认 |
| 11 | 低 | `BookOperation/BookPageMoveControl.cs:99` | “最后一页”硬编码 `part = 1` | 自适应下按组末尾处理，P2 一并核对 |
| 12 | 低 | `PageSelect/PageSlider/PageSlider.cs:112-160` | 除 `:114` 外还有 `delta % 2`、`ViewPageCount < 2` 等成对逻辑 | 自适应下全部旁路（不做对齐） |
| 13 | 低 | `PageFrames/PageFrameBox.cs:728 MoveToFixPosition` | 模式切换后只 `Truncate()` 到页首，不重新对齐组边界 | 自适应由工厂统一归一化组起点，天然覆盖 |

---

## 5. 行为规格（用户可见）

### 5.1 页面移动

| 命令 | 单页模式 | 双页模式 | 自适应模式（建议） |
|---|---|---|---|
| 次のページ (`NextPage`) | 下一页 | 2 页 | **下一组** |
| 1 ページ進む (`NextOnePage`) | 下一页 | 1 页 | **下一组**（组内页已同时可见；若目标页仍在当前组，则再进一组） |
| 設定ページ数進む (`NextSizePage`) | 跳 N 页 | 同左（含偶数对齐） | 跳 N 页后定位到所在组（无对齐） |
| `PrevPage` / `PrevOnePage` | 对称 | 对称 | **上一组** |

理由：自适应模式下组内所有页同时可见，“页内移动”没有视觉意义；把移动单位统一为“组”最符合直觉，
也与 `MoveToNextFrame()` 的既有语义一致（`PageFrameBox.cs:1020`）。

实现要点：`MoveToNextPage()` 目前把 `frameRange.Top(±1).Index ± 1` 直接当作下一帧起点（`PageFrameBox.cs:996`），
该假设只对固定 2 页分组成立；自适应下必须由分组表把任意目标页归一化到所属组的起点（见 §4.3 的 `FixFramePosition`）。

### 5.2 窗口缩放

1. 用户拖动窗口 → `CanvasSize` 变化。
2. `PageFrameBox.Context_SizeChanged` → `AdaptivePageFrameMap.InvalidateAll()` → `UpdateContainers(Moderate)`。
3. 帧工厂按新宽度重新分组 → 若帧内容变化，视图重建（现有管线）。
4. 期望：图片数量随之增减，`FrameRange` 变化不导致当前页“丢失”；`SelectedContainer` 仍指向包含当前页的组。
5. P4 优化：连续拖动时（<100ms）合并重排，避免中间态闪烁。

### 5.3 加载中的表现

- 组内未加载页显示占位（现有 `ViewContent` 状态机）。
- 加载完成 → `Page.SizeChanged` → 分组表局部失效 → 该组重建（可能由 3 张变 4 张）。
- 建议在 P4 增加“重排时不改变当前页在屏幕上的主位置”（锚定当前页），避免视觉跳动。

---

## 6. 风险与对策

| 风险 | 等级 | 说明 | 对策 |
|---|---|---|---|
| 分组表与容器集合不一致 | 高 | 帧范围重叠会导致 `RemoveConflictContainer` 删帧、画面跳变 | 分组表是纯函数 + 单向失效；写单测覆盖“正反向取帧结果一致” |
| 后台线程访问分组表 | 高 | `BookPageLoader` 在 MTA 线程创建帧（`BookPageLoader.cs:330`） | `lock` + 只追加语义；不持有 `Page` 引用过久 |
| 未加载页尺寸导致重排 | 中 | 远端跳转时估算偏差 | 加载完成即局部失效；P4 锚定当前页 |
| 绘制 N 张图带来的内存/解码压力 | 中 | 一帧 8 张 4K 图 | `AdaptiveMaxPageCount` 上限；沿用现有内存管理（`BookMemoryService`） |
| 自动旋转在 N 页时错乱 | 中 | 整帧旋转语义不适用 | N>1 禁用整帧旋转（§4.4） |
| `IsValidContainerFormat` 误判多页帧 | 中 | `partSize == 2` 对 N 页帧恒 false，导致每轮填充都走“更新”分支（双页模式已受影响） | P1 改为 `partSize % 2 == 0`（§4.5） |
| 标题/导出文件名格式不匹配 | 低 | `Count >= 2` 走双页格式，`{Page2}` 变成“最后一页” | P3 决策；至少在 PR 中标注已知限制（§4.12） |
| 与 Panorama / 竖排组合的怪异表现 | 低 | 语义叠加 | 文档标注；必要时 P4 限制组合 |
| 设置界面遗漏/资源缺失 | 低 | UI 显示 `@Key` | 10 语言文件同步 + DEBUG 校验（`TextResources.ValidateResourceKeys`） |
| 旧版本文档格式降级 | 低 | 未知枚举值 → 默认单页 | 已由 `JsonEnumFuzzyConverter` 兜底 |

---

## 7. 测试计划

### 7.1 单元测试（`NeeView.UnitTest`）

1. `AdaptivePagePacker.GetFrameLength()` 纯算法：
   - 用户示例（600/200/100/500，1200×800）→ 3 张。
   - 全部等高、恰好整除 → 边界（`Σa == budget` 应包含）。
   - 单页超宽（`a > budget`）→ 1 张。
   - 空书 / 单页书。
   - `maxCount` 截断。
   - 宽高为 0 的退化输入。
   - `span > 0` 时页数比 `span = 0` 少（间距计入预算）。
2. `AdaptivePageFrameMap`：
   - 正向顺序取帧 = 反向顺序取帧（同一页 → 同一范围）。
   - `InvalidateFrom` 后重算一致性。
   - 画布宽度变化后分组变化（如 1200 → 2400 时每组页数增加）。
3. `BookMemento` 序列化：`Adaptive` 往返；旧值（`SinglePage`/`WidePage`）不受影响。

### 7.2 手工测试矩阵

| 维度 | 取值 |
|---|---|
| 每屏页数 | 1 / 2 / 3 / 5 / 上限截断 |
| 页面尺寸组合 | 全等高；宽窄混排；超长条图；超宽图；尺寸未知（首次打开） |
| 书类型 | 文件夹图片、ZIP/CBZ、PDF、媒体（视频） |
| 模式 | 单页 / 双页 / 自适应（互切）；Panorama 开/关 |
| 方向 | 左开 / 右开；Horizontal / Vertical |
| 操作 | 前后翻页、1 页移动、跳页数、滑块拖动、拖拽窗口宽度、最大化/还原、全屏 |
| 持久化 | 重启应用、重新打开同书、切换书籍、书签历史 |

### 7.3 回归重点

- 单页 / 双页模式的翻页、首末页、横长页、补空白页行为不变。
- 双页模式的 `AssertValid` 在 DEBUG 构建下不触发断言。
- 设置界面、菜单勾选状态、地址栏图标三者与当前模式始终一致。

---

## 8. 分阶段实施计划

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P0 骨架**（0.5 天） | 枚举 + 判定扩展 + 移除 `GetFramePageSize` 并改写全部调用点 + 资源键占位（仅 en/ja/zh-Hans） | 解决方案可编译；单页/双页行为不变（回归通过） |
| **P1 分组核心**（1.5 天） | `AdaptivePagePacker`（纯算法）+ `AdaptivePageFrameMap`（缓存/失效/加锁）+ `PageFrameFactory` 自适应分支 + `PageFrame.AssertValid` + 自动旋转开关 + 单测 | 手工把 `PageMode` 改成 `Adaptive` 后，一屏显示 N 张；正反向翻页分组一致 |
| **P2 交互与容器**（1 天） | `MoveToNextPage/NextStep` 语义、`PageSlider`/`BookTools` 适配、`PageFrameContent` 后续页尺寸订阅、构造注入与生命周期 | 翻页/滑块/跳转/循环正确；窗口缩放后重排正确 |
| **P3 UI 与持久化**（1 天） | `SetPageModeAdaptiveCommand`、`CommandTable`、`MenuTree`、地址栏图标与 Trigger、设置页、`BookMemento` 解析、10 语言资源 | 三种模式可在 UI 全部路径切换；重启后保持；旧配置可读 |
| **P4 打磨**（1 天，可延后） | 缩放防抖、重排锚定当前页、Panorama/竖排的取舍、性能采样（大书 1 万页）、文档 | 拖动窗口无闪烁；1 万页书切换模式无卡顿（<100ms） |

依赖关系：P0 → P1 → P2 → P3；P4 独立。

**环境基线已实测**：本机 .NET SDK `10.0.300`，`dotnet build NeeView.sln -c Debug` 在改动前
**0 警告 / 0 错误，耗时约 59 秒**，因此 P0 的验收基线成立；后续每阶段均以该命令回归。

---

## 9. 已确认的决策

| # | 问题 | 决策 | 实现方式 |
|---|---|---|---|
| 1 | “窗口宽度”的定义 | **主视图画布宽度**（`CanvasSize.Width`，不含侧边栏/标题栏） | `AdaptivePageFrameMap` 直接读 `PageFrameContext.CanvasSize` |
| 2 | 越界页是否纳入 | **不纳入（高度优先）** — 2026-11-25 修订 | `AdaptivePagePacker` 严格按预算截断（§3.3） |
| 3 | 单屏页数上限是否暴露到设置 | **不暴露** | 常量 `PageFrameContext.AdaptiveMaxPageCount = 8` |
| 4 | `NextPage` 语义 | **整组翻页** | `PageFrameBox.MoveToNextPage()` 在单页/自适应下转 `MoveToNextFrame()` |
| 5 | 竖排（Vertical）模式 | **纵向堆叠**（宽度铺满画布，对称于横屏） | `PageFrame.StackOrientation` + `UniformWidth` 对齐（§3.6 #13） |
| 6 | 与“双页对齐方式”设置的关系 | **自适应强制等高（竖排为等宽）**，不读取 `WidePageStretch` | `PageFrameFactory.GetAdaptiveStretchMode()` |

> 说明：决策 2 早期版本为“纳入越界页以铺满宽度”，实际体验后改为“高度优先”——
> 因为“同时显示的图片高度都铺满画布”与“纳入越界页”互斥（后者会让整行按宽度缩小、高度留黑边）。
>
> 未决（可后续迭代）：标题栏 `{Page2}` 在 N>2 时仍映射到“最后一页”；导出文件名仍按“双页格式”选择。
> 这两处不影响主功能，已在 §4.12 记录。

---

## 附录 A：关键代码位置速查

| 主题 | 位置 |
|---|---|
| 模式枚举 | `NeeView/Book/PageMode.cs:7`（含 `IsAdaptive` 等判定） |
| 分组算法 | `NeeView/PageFrames/AdaptivePagePacker.cs`（新增） |
| 分组表 | `NeeView/PageFrames/AdaptivePageFrameMap.cs`（新增） |
| 上下文判定 | `NeeView/PageFrames/PageFrameContext.cs:82`、`:116-140`（`AdaptiveMaxPageCount`/`StackOrientation`） |
| 画布尺寸入口 | `NeeView/PageFrames/PageFrameBox.cs:134`、`PageFrameContext.cs:422` |
| 帧生成 | `NeeView/PageFrames/PageFrameFactory.cs:46`、`:170-213`、`:293` |
| 帧尺寸/断言 | `NeeView/PageFrames/PageFrame.cs:45`、`:139`、`:223` |
| 元素 | `NeeView/PageFrames/PageFrameElement.cs:31`、`:57`、`:72` |
| 布局 | `NeeView/PageFrames/PageFrameContent.cs:460` |
| 等高对齐 | `NeeView/PageFrames/ContentSizeCalcurator.cs:240` |
| 容器生成 | `NeeView/PageFrames/PageFrameContainerCollection.cs:314`、`:348` |
| 视口填充 | `NeeView/PageFrames/PageFrameContainerFiller.cs:63`、`:86`（`IsValidContainerFormat`） |
| 翻页 | `NeeView/PageFrames/PageFrameBox.cs:976`、`:1020`、`:1057` |
| 后台建帧 | `NeeView/BookPageLoader/BookPageLoader.cs:330` |
| 未加载尺寸 | `NeeView/Page/PageContent.cs:24`、`:145` |
| 书签历史序列化 | `NeeView/Book/BookMemento.cs:112`、`:201` |
| 命令注册 | `NeeView/Command/CommandTable.cs:278` |
| 菜单 | `NeeView/Menu/MenuTree.cs:170` |
| 地址栏按钮 | `NeeView/AddressBar/AddressBarView.xaml:113` |
| 图标 | `NeeView/Styles/MenuIcons.xaml:69`、`NeeView/Styles/IconGeometries.xaml:101` |
| 语言资源 | `NeeView/Languages/{en,ja,zh-Hans,...}.restext`（`PageMode.*` 约 1485 行附近） |
| 标题 / 导出命名 | `NeeView/MainWindow/PageTitle.cs:83`、`NeeView/MainWindow/TitleStringFormatter.cs:149`、`NeeView/ExportImage/ExportImageFileNamePolicy.cs:50` |
| 脚本 API | `NeeView/Script/BookConfigAccessor.cs:14`（`ViewPageSize`） |
| 当前页判定 | `NeeView/PageFrames/SelectedContainer.cs:35` |

## 附录 B：算法伪代码（完整版）

```csharp
// --- 纯算法：不依赖 WPF，可直接单测 ---
public static class AdaptivePagePacker
{
    /// <param name="aspects">页宽高比 a_i = w_i / h_i</param>
    /// <param name="width">画布宽（DIP）</param>
    /// <param name="height">画布高（DIP）</param>
    /// <param name="span">页间距（>= 0）</param>
    /// <param name="start">组起点页索引</param>
    /// <param name="maxCount">单帧页数上限</param>
    public static int GetFrameLength(
        IReadOnlyList<double> aspects, double width, double height,
        double span, int start, int maxCount)
    {
        if (width <= 0 || height <= 0 || maxCount < 1) return 1;

        span = Math.Max(0.0, span);
        double sum = 0;
        int count = 0;
        for (int i = start; i < aspects.Count && count < maxCount; i++)
        {
            double a = aspects[i];
            if (double.IsNaN(a) || a <= 0) a = 1.0;          // 退化兜底

            double budget = (width - count * span) / height;  // 加入下一张后的预算
            if (count > 0 && sum + a > budget) break;         // 至少 1 页
            sum += a;
            count++;
        }
        return Math.Max(count, 1);
    }
}
```

```csharp
// --- 分组表：单调追加 + 单向失效（示意） ---
public sealed class AdaptivePageFrameMap
{
    private readonly object _lock = new();
    private readonly List<int> _frameStarts = new() { 0 };   // 各帧首页 index，升序
    private int _knownCount;                                  // 已确定边界的页数

    public PageRange GetFrameRange(int index)
    {
        lock (_lock)
        {
            EnsureComputedUpTo(index);          // 惰性向前补算
            var i = LargestStartNotAfter(index);
            var start = _frameStarts[i];
            var end = (i + 1 < _frameStarts.Count ? _frameStarts[i + 1] : _pageCount) - 1;
            return new PageRange(new PagePosition(start, 0), new PagePosition(end, 1));
        }
    }

    public void InvalidateFrom(int pageIndex) { /* 截断到 pageIndex 所在帧的起点 */ }
    public void InvalidateAll() { /* 清空，仅保留起点 0 */ }
}
```

> 注意 `PageRange(position, partSize)` 的 `partSize` 以 **Value**（`index*2+part`）为单位，
> 因此多页范围必须用 `new PageRange(起始位置, 终止位置)` 构造，不要直接传页数。

## 附录 C：调研覆盖范围

本文的现状分析由两部分构成：

1. **主线程追踪**：从 `PageMode` 枚举 → `GetFramePageSize` → `PageFrameFactory` → `PageFrame`/`PageFrameContent`
   → `PageFrameContainerCollection`/`Filler` → `PageFrameBox` 的完整调用链，逐文件阅读源码。
2. **全仓隐式假设审计**（独立子代理执行，只读）：
   - 搜索范围：`NeeView/`（全部 `.cs`、`.xaml`、`.restext`）、`NeeView.UnitTest/`、`MakePackage/`、根目录 `*.ps1`/`*.props`/`*.sln`；
     排除子模块 `AnimatedImage`、`NeeLaboratory.IO.Search`、`SevenZipSharp`、`Vlc.DotNet` 与 `bin/`、`obj/`。
   - 检索式：`PageMode`、`FramePageSize`、`GetFramePageSize`、`IsWidePage*`、`IsStaticWidePage`、`WidwPageAlignment`、
     `Elements.Count|Elements[0|1]|First|Last`、`PartSize`、`IsOnePage`、`& 1`、`% 2`、`~1`、`(int)PageMode` 等。
   - 结论：`NeeView.UnitTest` 中**没有**任何 `PageMode`/`PageFrame` 相关测试（`PagePositions.cs` 仅测 `PagePosition` 的分割编码，
     已覆盖 partSize 1–3，说明 N 页范围本身可表示），因此 §7.1 的测试属于新增。
   - 审计确认的高风险项已并入 §4.1–§4.6 与 §4.12。


---

## 附录 D：实现记录（2026-11-25）

### D.1 落地清单

| 文件 | 变更 |
|---|---|
| `NeeView/Book/PageMode.cs` | 新增 `Adaptive`；`Validate` 上界；新增 `IsSinglePage`/`IsWidePage`/`IsAdaptive`/`IsMultiPageFrame` |
| `NeeView/Config/ConfigExtensions.cs` | **删除**（`GetFramePageSize` 会抛异常且无法表达动态页数） |
| `NeeView/PageFrames/AdaptivePagePacker.cs` | 新增：纯函数分割算法（高度优先 + 竖排） |
| `NeeView/PageFrames/AdaptivePageFrameMap.cs` | 新增：规范化前向分组表（惰性、缓存、失效、加锁、页面尺寸订阅） |
| `NeeView/PageFrames/PageFrameFactory.cs` | 新增 `CreateAdaptivePageFrame`、`CreateMultiPageFrame`、`GetAdaptiveStretchMode`；实现 `IDisposable`；`FixFramePosition`/`IsValidFramePosition` 适配 |
| `NeeView/PageFrames/PageFrame.cs` | 新增 `StackOrientation` 与轴相关尺寸计算；自动旋转开关；`AssertValid` 支持 1..N |
| `NeeView/PageFrames/ContentSizeCalcurator.cs` | `CalcFrameStretchScale` 增加轴参数（span 作用于并排方向）；`CalcContentScale` 增加对齐方式重载 |
| `NeeView/PageFrames/PageFrameContent.cs` | 竖向堆叠布局；自适应下帧尾尺寸变化触发重建；`StackOrientation` 变化视为脏 |
| `NeeView/PageFrames/PageFrameContext.cs` | 移除 `FramePageSize`；新增 `AdaptiveMaxPageCount`、`StackOrientation`；模式相关判定改写 |
| `NeeView/PageFrames/PageFrameBox.cs` | 自适应/单页整组翻页；移除 `FramePageSize` 事件项；释放工厂 |
| `NeeView/PageFrames/PageFrameContainerFiller.cs` | `IsValidContainerFormat` 修正为“偶数 PartSize”（同时修掉双页模式的既有误判） |
| `NeeView/Book/BookMemento.cs` | 书签历史新增 `Adaptive` 解析分支 |
| `NeeView/Book/Book.cs`、`BookTools.cs`、`BookSettingAccessor.cs`、`PageSlider.cs` | 按模式改用判定方法；非双页模式跳过成对对齐 |
| `NeeView/Command/Commands/SetPageModeAdaptiveCommand.cs` | 新增命令（Ctrl+3），注册到 `CommandTable` 与页面菜单 |
| `NeeView/AddressBar/AddressBarView.xaml`、`Styles/MenuIcons.xaml`、`Styles/IconGeometries.xaml` | 自适应图标（`view_week`）与 ToolTip 触发器 |
| `NeeView/Languages/*.restext`（9 语言 + shared） | `PageMode.Adaptive`、`SetPageModeAdaptiveCommand` |
| `NeeView.UnitTest/AdaptivePagePackerTest.cs` | 新增 12 个单测（示例、预算边界、不超预算、上限、间距、竖排、退化输入等） |

### D.2 与规划稿的差异

1. **页数上限**：规划为可配置项 `BookConfig.AdaptiveMaxPageCount`，按决策③改为常量（不暴露设置界面）。
2. **越界页**：规划为“不超预算即停”；中途按决策②改为“纳入越界页”，最终按 2026-11-25 修订
   **回到“不纳入”**（高度优先，保证图片高度铺满画布）。
3. **竖排**：规划为“始终横向”，按决策⑤改为纵向堆叠，涉及 `PageFrame` 尺寸模型与布局两处。
4. **对齐方式**：自适应模式下强制 `UniformHeight`（竖排 `UniformWidth`），不受 `WidePageStretch` 影响。
5. **`IsValidContainerFormat`**：审计发现的既有问题（`partSize == 2` 对多页帧恒 false）在本次一并修正。

### D.3 验证

```powershell
dotnet build NeeView.sln -c Debug          # 0 警告 / 0 错误
dotnet test NeeView.UnitTest\NeeView.UnitTest.csproj --filter AdaptivePagePackerTest   # 12 passed
```

手工验证建议（无法在 CI 覆盖）：

1. 打开一个含不同尺寸图片的文件夹 → 切到“自适应显示”（Ctrl+3 / 菜单 / 地址栏图标）。
2. 观察一屏 N 张、行内等高、**图片高度刚好等于画布高度**（可能右侧留白）。
3. 拖动窗口宽度 → 张数应随之变化，且不出现重叠、跳页。
4. 前后翻页（PageUp/PageDown、滚轮）→ 每次移动一整组。
5. 切回单页/双页 → 行为应与改动前一致（重点回归：双页模式的横长页、首末页、补空白页）。
6. 竖向模式（页面方向：纵向）下切到自适应 → 应纵向堆叠。
7. 重启应用 → 模式应保持（书签历史文件中的 `Adaptive` token）。
