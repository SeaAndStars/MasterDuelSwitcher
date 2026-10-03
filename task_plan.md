# 任务计划

目标：D:\repos\MasterDuelSwitcher 中交付 C# WPF + WPF UI 的换号、资源共享、备份还原工具；私有 GitHub 仓库并向 master 发 PR。

1. 现成工具研究与用户方案确认：complete。
2. 设计、项目与接口：complete；master 3b56eab，dev 26ff504。
3. Steam 后端：complete；120项测试，范围内行/分支100%，dev 75e7026。
4. 资源共享后端：complete；186项聚焦测试，3个生产文件行/分支100%，103caee。
5. Fluent UI 改 MVVM/DI、SQLite 及管理员启动：in_progress；当前339+70全测及100%合并覆盖通过、真实窗口13项通过，eb6581a；用户新增NavigationView替换正在实现。
6. 独立审查、EXE/源码/说明/校验、PR：pending。

新增验收：所有自写生产代码（包括App）行和分支覆盖均100%；必须生成真实覆盖率证据，不进行人工排除。系统data目录已明确为%LOCALAPPDATA%\MasterDuelSwitcher。

最新约束：Views分离，App.xaml仅引用Views组件；全部官方Fluent控件与主题，无淡蓝背景；新增持久化运行/Debug日志并纳入全覆盖测试。

最新页面约束：每个导航页拆独立View与PageViewModel，使用官方NavigationView和DI页面导航；Segoe UI Variable正文、FluentSystemIcons内嵌图标字体。当前409测试/100%为拆页前验证基线，拆页后重新完整验收。

实际故障修复阶段：检查系统数据日志、游戏 Player 日志及真实目录身份。21:11共享事务成功；用户确认随后手动删除目录，现目标是新建普通目录且原备份丢失。保留当前目标，增加显式失效事务修复及归档记录；全部通知和确认使用官方ContentDialog。完成后再实际恢复共享、验证链接和游戏反馈，重新运行全覆盖与发布验收。

授权：用户已要求新建 GitHub private 仓库和 D 盘 repos 下项目；私有仓库 https://github.com/SeaAndStars/MasterDuelSwitcher 已创建。

迁移记录：D盘Git完整可用，原工作目录残留空.git，不影响项目或交付。
