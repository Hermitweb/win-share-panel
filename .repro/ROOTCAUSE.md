# T1c 渲染根因定位报告（第 2 次重派）

日期：2026-10-22 · 执行：子代理 · 全部证据落盘于本目录

## 1. 结论（三句话）

1. **任务书的假设变量（卡片 DropShadowEffect / 外层 ScrollViewer / 运行期插入时机 / 内容产生方式）在离屏模型与一次实屏冒烟中全部无区分度——不是根因**（证据：matrix2.csv，36 个有效组合全部 visible=True）。
2. 前手 `matrix-live.csv` 的「dynamic 全塌陷」是**复现工程自身三处缺陷**造成的假证据：RTRB 渲染目标含 margin 坐标偏移、插入后裸 Measure 不清中间层缓存、进度条填充探针错配。已逐一修复（§3）。
3. 根因必须落在**复现未建模的主仓专属因素**上。头号候选：**主窗口根级 `WindowFrame` Border 的 DropShadowEffect（MainWindow.xaml L22-26）与卡片 CardStyle 的 Effect 形成嵌套 Effect 祖先链**——WPF 中挂 Effect 的元素子树渲染进中间位图（MS docs: `Effect` 语义），若 live 合成器下「effect 中间面 + 新增子 visual 的 dirty 区」存在失效缺口，则恰好解释症状指纹（静态 XAML 正常 / 自绘 OnRender 正常，因其自身 `InvalidateVisual` 走有效路径 / 容器运行期生成内容不重绘）。**该假设在复现中未能复现**（§4），需在主仓上做最小 diff 验证（§6）。

## 2. 基线/对照组修复记录（步骤 1、2 完成）

| # | 缺陷（前手遗留） | 修复 | 验证 |
|---|---|---|---|
| 1 | RTRB 渲染 `host.Child`（Grid）：位图坐标系含 Grid 自身 margin(24,22)，探针按 `TransformToAncestor(Grid)` 采样 → 恒偏 24/22px，static 基线永远 visible=False（内容其实画了，蓝像素在 y≈170，探针找 y≈147） | 渲染目标改 `host`（Decorator，margin=0），探针改 `TransformToAncestor(host)` | static fx=0 sv=0 → `visible=True sample=(104,157,241)≈#3E82F0` ✅ |
| 2 | 运行期插入后驱动只做裸 `host.Measure/Arrange`：离屏树无 dispatcher 根布局 pass，中间层（UniformGrid/ItemsControl 链）measure 缓存 stale → 内容 ActualSize=0 → 全矩阵「layout-collapsed」假象 | 插入后 `InvalidateAll(host)`（全链 InvalidateMeasure，模拟真窗口 RootVisual.UpdateLayout 的根失效语义） | dynadd/items after=0 → visible=True ✅ |
| 3 | items 探针采样 ProgressBar 填充：RTRB 无动画 pump，指示器 width 停在 0，只画轨道色 | 探针改 item 首 TextBlock（"C:" 黑字）+ 全探针 ±6px 邻域搜索（与 GUI 断言同口径） | items after=0 → visible=True sample=(29,29,30)≈#000 ✅ |
| 4 | real 结构 11 卡纵排 > 600px 画布，协议卡被夹到画布外，探针落透明区读 (0,0,0) → 第一轮 real 矩阵 16 行全假 False | `--real` 时画布 H=1400 | real 结构 16 组合全部 visible=True ✅ |

修复 1/2/3 后：基线（static）、对照组（dynadd/items after=0）全绿，符合任务书 §1、§2 门槛。

## 3. matrix2.csv 全表与有效性说明

完整 52 行见 `.repro\matrix2.csv`（列：combo,visible,sample,region,elapsed_s）。有效性标注：

- **L2-L21（20 行，简结构）**：全 True，有效。变量=effect×scroll×content(static/dynadd/items)×after。
- **L22-L37（第一轮 real，16 行，center=(63,599)）**：**无效**（H=600 画布假象，§3-#4），勿采信。
- **L38-L53（real H=1400 + cycles，16 行，center=(63,652)/(49,730)）**：全 True，有效。
- 附加有效证据（未入 csv）：rootfx（根级 Effect）3 组合全 True（stepE-out.txt）；live GUI ×2 截图自扫：首插（stepF）与 cycles=3 重插（stepG）后协议 tag 蓝像素 4861/4170，内容均在屏。

**判定：无区分度 → 按任务书 §4 扩读主仓并换变量（两轮扩展：结构保真 real + 重复重插 cycles；根级 rootfx），仍无区分度。**

## 4. 主仓扩读证据（为什么机制候选是「嵌套 Effect」）

- `MainWindow.xaml` L22-26：`WindowFrame` Border（**全窗口内容祖先**）挂 `DropShadowEffect BlurRadius=24 ShadowDepth=0`。
- `App.xaml` L14-21 + `ThemeTokens.Light.xaml` L72：卡片 `CardStyle`（TargetType=Border）挂 `Effect={DynamicResource ShadowCard}`（DropShadowEffect BlurRadius=16 ShadowDepth=4）——**每张卡又是一个 Effect 层**。
- 即主仓渲染链：`Window → [Effect 根面] → … → 卡片[Effect 面] → 运行期挂入的 UniformGrid/ItemsControl 内容`（双嵌套 Effect 祖先）。
- 症状指纹对照（MS docs `Effect`/`InvalidateVisual` 语义 + dotnet/wpf 行为）：
  - 静态 XAML：首次全量渲染，✅；
  - `TrendChartControl`/`TopSharesChartControl`：自身 `OnRender` + 数据变更时 `InvalidateVisual()`（TrendChartControl.cs L48-49），元素自身 visual 脏化走合成器有效路径 → ✅；
  - `BuildProtocolGrid`/`BuildDiskList`（DashboardPageView.xaml.cs L118-168）：新 visual 运行期挂入，无自身脏化调用 → 若 effect 中间面的 dirty 区传播在 live 下有缺口 → ❌。
  - 旁证：码后 L123 注释「运行期集合渲染在某些容器下不可见，静态节点必定绘制」——前人已知此症状并只能用静态控件绕。
- 复现侧建模（rootfx=1 等价 WindowFrame Effect；real 结构等价全页）+ 一次实屏（gui=1，含 3 轮重插）：**内容均正常绘制**。即该机制在复现中未成立，属「未复现的头号候选」，不可当既成事实引用。

## 5. 已被排除的变量（带证据）

| 变量 | 证据 | 判定 |
|---|---|---|
| 卡片 DropShadowEffect（ShadowCard） | matrix2 L11-21（effect=1 全 True）+ rootfx 组合 | 排除（单因素） |
| 外层 ScrollViewer | scroll=0/1 全 True | 排除 |
| 插入时机（首帧前/后、多轮重插） | after=0/1、cycles=3 离屏+live 全 True | 排除 |
| 内容产生方式（UniformGrid.Add vs ItemsSource） | dynadd/items 全 True | 排除 |
| 页面结构保真度（11 卡纵排、嵌套 Grid RowSpan、Empty 兄弟节点、自绘对照卡、ContentControl 宿主链） | real=1 全 True | 排除 |
| 根级 Effect（WindowFrame 等价） | rootfx=1 全 True（离屏+live） | 单因素排除；**嵌套 Effect + live 合成器的组合效应未穷尽**（复现 live 冒烟窗口 800×1000、96DPI、首插 1.5s 后即截图；主仓为 1240×820、LoadingOverlay 覆盖 2-3s、用户 DPI 未知） |

## 6. 对 WinShare.App 的修复建议（按成本升序，先验证后修）

1. **验证性最小 diff（10 分钟，先做）**：`MainWindow.xaml` L24-26 注释掉 `WindowFrame` 的 `DropShadowEffect`（仅验证，不留），启动主仓观察两卡动态内容是否恢复。恢复 → 嵌套 Effect 机制坐实，进入 2。
2. **若坐实：去掉根级 Effect，阴影改「兄弟层假阴影」**：`WindowFrame` 自身不挂 Effect，改在窗口外层兄弟（Grid 下、PageHost 之前）放一个同尺寸圆角 Border 画阴影（ShadowSoft 参数），或直接用 `WindowChrome.GlassFrameThickness=1` 的系统阴影；卡片 Effect 可保留。
3. **不依赖根因的兜底修法（无论验证结果都可上）**：`DashboardPageView.ApplyVmCore()`（L100-101 已有 `InvalidateVisual` 先例）在 `BuildProtocolGrid/BuildDiskList` 之后对两张卡的 **Border 本体** 调 `InvalidateVisual()`（自绘图表控件已证明「自身脏化」路径有效），强制 effect 中间面重绘 dirty 区。

## 7. 复现工程去留

**保留** `.repro\`：
- `RenderRepro\`（已修 4 处缺陷）：支持 `--effect/--scroll/--content/--after/--gui/--diag/--probe/--real/--cycles/--rootfx`，离屏 RTRB 像素断言口径可信；
- `Matrix2.ps1`（20 组合简结构）、`Round1.ps1`（real/cycles 22 组合）、`matrix2.csv`（含有效性标注）；
- `scanbox.cs/exe`（PNG 蓝橙像素包围盒扫描，C#5 兼容）。

**弃用**：`matrix-live.csv` 与旧 `matrix.csv`（缺陷 1/2/3 产物，全部不可采信）。

**GUI 内置断言（[GUIASSERT]）不可采信**：disk 探针的 `windowPt` 恒被 proto 坐标覆盖（stepG-out 两行 windowPt 几乎相同为证）；GUI 证据一律用「截图 + 自扫像素」，开完必杀进程（本轮已杀，§8 复查）。

## 8. 残留进程复查

收尾时执行 `Get-Process RenderRepro` 复查（本轮每次 gui=1 后均已杀）；最终清零。
