# 任务计划

目标：D:\repos\MasterDuelSwitcher 中交付 C# WPF + WPF UI 的换号、资源共享、备份还原工具；私有 GitHub 仓库并向 master 发 PR。

1. 现成工具研究与用户方案确认：complete。
2. 设计、项目与接口：complete；master 3b56eab，dev 26ff504。
3. Steam 后端：in_progress，由 steam_backend 实现并测试。
4. 资源共享后端：in_progress，由 resource_backend 实现并测试。
5. Fluent UI、设置与实际窗口验证：in_progress，fluent_ui 与主代理。
6. 独立审查、EXE/源码/说明/校验、PR：pending。

授权：用户已要求新建 GitHub private 仓库和 D 盘 repos 下项目；私有仓库 https://github.com/SeaAndStars/MasterDuelSwitcher 已创建。

问题：Move-Item 报告隐藏 .git 目录迁移错误；实际 D 盘 Git 完整可用并已提交。旧目录仅残留 .git，检查后只清理空目录，不删除任何未知文件。
