# 视觉规范速查（docs/DESIGN.md）

> 完整版见交付物《青枢工具箱 · 架构设计与视觉规范》PDF。本文为工程速查版，所有键名可直接在 XAML 中以 `{ThemeResource ...}` 引用。

## 1. 品牌色（青枢蓝）

| 用途 | Light | Dark | XAML Key |
|------|-------|------|----------|
| 主色 | `#2563EB` | `#3B82F6` | `BrandPrimaryBrush`（另覆写 `SystemAccentColor` 系列，原生控件自动生效） |
| 主色 Hover | `#1D4ED8` | `#60A5FA` | `BrandPrimaryHoverBrush` |
| 主色 Pressed | `#1E40AF` | `#2563EB` | `BrandPrimaryPressedBrush` |

## 2. 中性色

| 用途 | Light | Dark | Key |
|------|-------|------|-----|
| 页面背景 | `#F8FAFC` | `#0F172A` | `PageBackgroundBrush` |
| 页面背景（亚克力透出用，半透明） | `#B3F8FAFC` | `#D90F172A` | `PageBackgroundAcrylicBrush` |
| 卡片背景 | `#FFFFFF` | `#1E293B` | `CardBackgroundBrush` |
| 卡片边框 / 分割线 | `#E2E8F0` | `#334155` | `CardBorderBrush` / `DividerStrokeBrush` |
| 一级正文 | `#1E293B` | `#F1F5F9` | `TextPrimaryBrush` |
| 二级正文 | `#64748B` | `#94A3B8` | `TextSecondaryBrush` |
| 占位文字 | `#94A3B8` | `#64748B` | `PlaceholderTextBrush` |
| Hover / Pressed | `#EFF6FF` / `#DBEAFE` | `#1E3A8A` / `#1E40AF` | `HoverFillBrush` / `PressedFillBrush` |

## 3. 状态色（双套逻辑：填充用原色，文字用 WCAG AA 变体）

| 状态 | 填充/图形 | 文字(Light) | 文字(Dark) | 浅底(Chip) |
|------|-----------|-------------|------------|------------|
| 成功 | `#10B981` | `#047857` | `#6EE7B7` | `#1AD1FAE5` |
| 警告 | `#F59E0B` | `#92400E` | `#FCD34D` | `#1AFEF3C7` |
| 错误 | `#EF4444` | `#B91C1C` | `#FCA5A5` | `#1AFEE2E5` |
| 信息 | 主色 | `#1D4ED8` | `#93C5FD` | `#1ADBEAFE` |

> 对比度说明：状态文字变体在其所在主题背景上均 ≥ 4.5:1（AA）；黄色在白底不可作正文，一律使用 `#92400E`。

## 4. 功耗标识色（项目特色）

| 状态 | Light | Dark | Key |
|------|-------|------|-----|
| 低功耗（电池/EcoQoS） | `#059669` | `#34D399` | `PowerEcoBrush` |
| 平衡 | 主色 | 主色 | `PowerBalancedBrush` |
| 高性能（插电） | `#DC2626` | `#F87171` | `PowerHighBrush` |

**使用纪律：只允许状态栏小圆点 / 小徽标场景，禁止大面积填充。**

## 5. 材质策略（Win10 优先）

- Win11（Build 22000+）：`MicaController`
- Win10：`DesktopAcrylicController`
  - 浅色：Tint `#F8FAFC`，TintOpacity 0.72
  - 深色：Tint `#0F172A`，TintOpacity 0.85（**加深不透明**，保证 AA 可读性）
  - 页面根背景切换为 `PageBackgroundAcrylicBrush`，卡片保持不透明形成层次
- 高对比度/不支持材质：回退不透明页面背景
- 实现：`Services/WindowBackdropService.cs`，设置页可一键关闭

## 6. 字体与字号

- 系统默认字型栈（中文自动落到微软雅黑）；等宽 `Consolas`（路径/日志）。
- 层级：页标题 24 SemiBold / 节标题 16 SemiBold / 正文 13 / 辅助 12 / 数字指标 28 SemiBold。

## 7. 动效

- 页面进入：`EntranceThemeTransition`（FromVerticalOffset 24，交错启用）。
- 页面导航：`EntranceNavigationTransitionInfo`。
- 其余控件沿用 Fluent 原生动效（按压涟漪/展开），不自绘装饰动画，保证低配机房流畅。

## 8. 组件约定

- 卡片：`CardStyle`（圆角 8、1px 边框、内边距 16）。
- 状态 Chip：胶囊形（CornerRadius 99），ChipFill 底 + 对应文字色。
- 对话框：ContentDialog 原生样式；危险操作前置确认并明示后果。
