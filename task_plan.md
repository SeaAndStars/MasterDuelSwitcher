# 任务计划

目标：D:\repos\MasterDuelSwitcher 中交付 C# WPF + WPF UI 的换号、资源共享、备份还原工具；私有 GitHub 仓库并向 master 发 PR。

1. 现成工具研究与用户方案确认：complete。
2. 设计、项目与接口：complete；master 3b56eab，dev 26ff504。
3. Steam 后端：complete；120项测试，范围内行/分支100%，dev 75e7026。
4. 资源共享后端：in_progress，由 resource_backend 实现并测试。
5. Fluent UI 改 MVVM/DI、SQLite 及管理员启动：in_progress；SQLite 10项测试、行/分支100%，App编译通过、61项测试通过；真实窗口13项检查通过，主题视觉问题修复中。
6. 独立审查、EXE/源码/说明/校验、PR：pending。

新增验收：所有自写生产代码（包括App）行和分支覆盖均100%；必须生成真实覆盖率证据，不进行人工排除。系统data目录已明确为%LOCALAPPDATA%\MasterDuelSwitcher。

最新约束：Views分离，App.xaml仅引用Views组件；全部官方Fluent控件与主题，无淡蓝背景；新增持久化运行/Debug日志并纳入全覆盖测试。

授权：用户已要求新建 GitHub private 仓库和 D 盘 repos 下项目；私有仓库 https://github.com/SeaAndStars/MasterDuelSwitcher 已创建。

迁移记录：D盘Git完整可用，原工作目录残留空.git，不影响项目或交付。
