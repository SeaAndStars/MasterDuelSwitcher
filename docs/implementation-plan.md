# Master Duel Switcher Implementation Plan

> For agentic workers: use subagent-driven-development; execute the checked tasks within the approved scope.

**Goal:** 交付 WPF UI 的 Master Duel 一键换号及资源复用工具。
**Architecture:** Core 与 WPF App 分离；真实临时目录测试文件变更；启动与实际登录区分。
**Tech Stack:** C# / .NET 10 Windows / WPF UI 4.3.0 / xUnit。
**Spec:** docs/design.md

## Global Constraints
- 中文注释覆盖每个方法、字段、属性。
- 资源共享仅 0000，账号目录保留独立，任何替换可还原。
- dev 开发，分阶段提交；不含凭证、密码或示例账号。

## Task 1：项目基础及接口
- [ ] 建立 Core、App、Tests 项目及公共模型；Core 提供 VDF、SteamDiscoveryService、SteamAccountService、ResourceSharingService、SettingsStore。
- [ ] 设计和接口提交到 dev；master 保留初始项目约定基线。

## Task 2：Steam 发现与切号（独立代理）
- [ ] 先写 VDF 保留字段、Steam 库发现、账号读取及切换配置测试并确认失败。
- [ ] 实现公共接口，使用实际临时文件验证，禁止操作用户当前登录状态。
- [ ] 正常退出 Steam，有超时则中止；成功准备配置后 -applaunch 1449850。

## Task 3：资源共享与还原（独立代理）
- [ ] 先写真实 junction 共享、备份与还原、重复启用、非法路径与中断恢复测试并确认失败。
- [ ] 实现资源目录扫描、事务清单与按条还原；拒绝覆盖未知链接。

## Task 4：WPF UI 与配置（主代理）
- [ ] 设置原子读写测试；实现账号备注/隐藏、目录绑定、路径自动/手动选择、资源来源、主题和日志。
- [ ] 账号、资源、备份三个页面；所有操作展示准确结果与下一步。
- [ ] 运行实际窗口验证布局与至少配置保存、导航和异常状态。

## Task 5：验证与交付
- [ ] 独立代码审查，修复影响数据完整性、切号正确性和界面的发现。
- [ ] Release 构建、全部测试、自包含 publish、实际 EXE 启动验证。
- [ ] 中文说明、EXE ZIP、源码 ZIP、SHA256；记录真实客户端尚待的检查。
- [ ] 远端获知后向 master 建 PR 并附到当前聊天；否则保留待办。
