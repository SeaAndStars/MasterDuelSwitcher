# Master Duel Switcher 设计

已批准范围：Windows Steam 版，C# WPF + WPF UI，自动发现 Steam 与 Master Duel，管理本机记住的账号，共享游戏资源，一键切号并启动，备份还原。

结构：Core 承担 Steam VDF、安装发现、切号、设置及资源事务；App 承担 Fluent 窗口与用户交互；Tests 验证临时目录中的真实文件和 Windows junction；UI 验证打开实际窗口。

账号：读取本机 loginusers.vdf，不读取密码或令牌。每个账号手工绑定 LocalData 目录，避免推测账号哈希。切换前检查游戏进程；正常退出 Steam，备份 loginusers.vdf 与 AutoLoginUser，修改 MostRecent / AllowAutoLogin 与 HKCU AutoLoginUser，再使用 Steam.exe -applaunch 1449850。启动并不代表已登录，额外验证由 Steam 提示。

资源：用户选择已完成更新的资源来源；其余账号的 0000 使用 NTFS junction 指向来源。来源保持原目录；目标旧目录移至唯一备份位置，清单落盘后再创建 junction。重复启用幂等；还原核验清单、路径和 junction 目标，逐条复原。记录异常中断以便恢复。游戏运行时不变更目录。

界面：1100×760 可缩放 FluentWindow，浅色云白 #F5F6FA，深色蓝灰 #142131，蓝色 #326DE6，正文 #202B3A，次要文字 #69778A，绿色 #23825B。标题使用 Segoe UI Semibold，正文 Microsoft YaHei UI，路径 Cascadia Mono。左侧功能导航，右侧账号列表与主要切换动作；资源与备份分页面。无虚构账号数据。

验收：自动发现真实 Steam 库路径；VDF 保留其他配置；资源测试验证链接读写同一文件、旧目录无损、幂等、还原、路径边界；真实 WPF 窗口启动与交互；win-x64 自包含发布；ZIP 校验和；未提供远端时保留 dev 提交及 PR 补丁并明确待补远端。
