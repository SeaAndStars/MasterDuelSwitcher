# Master Duel Switcher 设计

已批准范围：Windows Steam 版，C# WPF + WPF UI，自动发现 Steam 与 Master Duel，管理本机记住的账号，共享游戏资源，一键切号并启动，备份还原。

结构：Core 承担 Steam VDF、安装发现、切号、SQLite 存储及资源事务；App 使用 MVVM，ViewModel 经 DI 接口调用服务，窗口仅承担视图；Tests 验证真实 SQLite、临时文件、Windows junction 与可注入系统边界；UI 验证打开实际窗口。

视图分层：窗口、Fluent资源及可复用界面组件放入App/Views；App.xaml仅引用Views层组件，ViewModels、Services、启动DI组合各自独立。

新增已批准约束：.NET 10；Microsoft.Extensions.DependencyInjection；账号元数据、备注、隐藏状态、绑定与偏好存入 `%LOCALAPPDATA%\\MasterDuelSwitcher\\accounts.db`，位于系统数据目录，不随 EXE 删除；manifest requireAdministrator 使用标准 UAC；全部自写生产代码行覆盖率和分支覆盖率均为 100%，度量包括 Core、ViewModel、视图自身逻辑与启动代码，不使用人为覆盖排除来伪造结果。

账号：读取本机 loginusers.vdf，不读取密码或令牌。每个账号手工绑定 LocalData 目录，避免推测账号哈希。切换前检查游戏进程；正常退出 Steam，备份 loginusers.vdf 与 AutoLoginUser，修改 MostRecent / AllowAutoLogin 与 HKCU AutoLoginUser，再使用 Steam.exe -applaunch 1449850。启动并不代表已登录，额外验证由 Steam 提示。

资源：用户选择已完成更新的资源来源；其余账号的 0000 使用 NTFS junction 指向来源。来源保持原目录；目标旧目录移至唯一备份位置，清单落盘后再创建 junction。重复启用幂等；还原核验清单、路径和 junction 目标，逐条复原。记录异常中断以便恢复。游戏运行时不变更目录。

界面：1100×760可缩放FluentWindow；全部控件与明暗配色采用WPF UI官方Fluent主题，强调色跟随Windows，不使用自定义淡蓝背景。标题使用Segoe UI Semibold，正文Microsoft YaHei UI，路径Cascadia Mono。左侧功能导航，右侧账号列表与主要切换动作；资源与备份分页面。无虚构账号数据。

日志：系统数据目录下logs持久保存滚动运行日志与Debug日志；记录启动、操作、事务进度、恢复结果与异常栈，设置容量及保留上限；不记录Steam凭据、VDF正文或账号备注。CLI隔离状态目录同时隔离日志。

验收：自动发现真实 Steam 库路径；VDF 保留其他配置；资源测试验证链接读写同一文件、旧目录无损、幂等、还原、路径边界；真实 SQLite 持久化和事务；MVVM/DI 结构审查；UAC manifest 与运行令牌检查；100% 行和分支覆盖报告；真实 WPF 窗口启动与交互；win-x64 自包含发布；ZIP 校验和；向已授权私有仓库的 master 发 PR。
