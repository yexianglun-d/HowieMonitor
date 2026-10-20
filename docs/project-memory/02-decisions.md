# Decisions

## 2026-10-20 / 保留两边修改完成同步

- 决策：将远程 `a5b7e56` 与本地 `eeeaaa0` 合并，保留原有提交历史。
- 原因：两边从 `da3be64` 分叉，远程迁移 Element Plus 控件，本地已实现并提交 Toast；直接覆盖任一侧都会丢失已完成的行为。
- 影响：页面使用远程提供的控件和刷新逻辑，操作结果继续使用现有 Toast。离线、数据过期和首次加载失败的重试入口保持可见。
- 状态：accepted。没有新增产品范围或生产部署决定。

## 2026-10-20 / 区分温度读数与阈值

- 决策：温度摘要按硬件分组，优先有效的 CPU 主温度、GPU 核心和磁盘综合温度；其他传感器可展开，告警/临界阈值仅在详情以阈值名称展示。
- 原因：真实上报同时包含磁盘温度和较高的 Warning/Critical Temperature；已对照锁定版本 LibreHardwareMonitorLib 0.9.6 的源代码确认这两项来自 SMART 的 TemperatureWarning/TemperatureCritical。
- 影响：不改变原采样和历史身份，不虚构缺失读数，不把阈值当作硬件发热状态。
- 来源：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs
- 状态：accepted。
