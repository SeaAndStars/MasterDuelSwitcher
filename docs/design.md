# Master Duel Switcher 设计

已批准范围：Windows Steam 版，C# WPF + WPF UI，自动发现 Steam 与 Master Duel，管理本机记住的账号，共享游戏资源，一键切号并启动，备份还原。

结构：Core 承担 Steam VDF、安装发现、切号、SQLite 存储及资源事务；App 使用 MVVM，ViewModel 经 DI 接口调用服务，窗口仅承担视图；Tests 验证真实 SQLite、临时文件、Windows junction 与可注入系统边界；UI 验证打开实际窗口。

视图分层：窗口、Fluent资源及可复用界面组件放入App/Views；App.xaml仅引用Views层组件，ViewModels、Services、启动DI组合各自独立。

页面结构：AccountsPage、ResourcesPage、BackupsPage、SettingsPage分别位于Views/Pages并对应独立页面ViewModel；通过官方NavigationView、页面解析与DI导航，Shell仅负责应用外框。跨页的发现、资源状态、操作忙碌和日志由独立共享协调服务管理。

新增已批准约束：.NET 10；Microsoft.Extensions.DependencyInjection；账号元数据、备注、隐藏状态、绑定与偏好存入 `%LOCALAPPDATA%\\MasterDuelSwitcher\\accounts.db`，位于系统数据目录，不随 EXE 删除；manifest requireAdministrator 使用标准 UAC；全部自写生产代码行覆盖率和分支覆盖率均为 100%，度量包括 Core、ViewModel、视图自身逻辑与启动代码，不使用人为覆盖排除来伪造结果。

账号：读取本机 loginusers.vdf，不读取密码或令牌。每个账号手工绑定 LocalData 目录，避免推测账号哈希。切换前检查游戏进程；正常退出 Steam，备份 loginusers.vdf 与 AutoLoginUser，修改 MostRecent / AllowAutoLogin 与 HKCU AutoLoginUser，再使用 Steam.exe -applaunch 1449850。启动并不代表已登录，额外验证由 Steam 提示。

资源：用户选择已完成更新的资源来源；其余账号的 0000 使用 NTFS junction 指向来源。来源保持原目录；目标旧目录移至唯一备份位置，清单落盘后再创建 junction。重复启用幂等；还原核验清单、路径和 junction 目标，逐条复原。记录异常中断以便恢复。游戏运行时不变更目录。

界面：1100×760可缩放FluentWindow；全部控件与明暗配色采用WPF UI官方Fluent主题，强调色跟随Windows，不使用自定义淡蓝背景。正文Segoe UI Variable，中文Microsoft YaHei UI，旧系统回退Segoe UI；标准Fluent字号层级。图标采用WPF UI内嵌FluentSystemIcons字体，自包含发布无需安装图标字体。左侧官方导航栏，右侧账号列表与主要切换动作；资源与备份分页面。无虚构账号数据。

日志：系统数据目录下logs持久保存滚动运行日志与Debug日志；记录启动、操作、事务进度、恢复结果与异常栈，设置容量及保留上限；不记录Steam凭据、VDF正文或账号备注。CLI隔离状态目录同时隔离日志。

账号列表：按昵称、登录名、SteamID和保存的备注进行忽略大小写的有序字符模糊搜索，多词逐项匹配。星标存于SQLite附加表，兼容既有备注、隐藏及绑定；排序为星标、MostRecent、登录时间降序、名称和SteamID。筛选与排序保留未保存的账号编辑内容。ListBox独立虚拟化滚动，搜索栏固定，右侧详情单独滚动。

头像：优先系统缓存和Steam本地avatarcache；公开社区XML缺少头像时读取公开页面的头像容器，限制为官方CDN的静态图片。异步请求最多四并发、每项取得处理槽后预算八秒，排队使用应用退出取消信号；资料和图片均有读取上限，不占用全局操作忙碌状态。图片在WPF实际解码，失败显示账号名称首字；关闭应用取消后台请求。系统avatars缓存与程序文件分离，不保存登录Cookie或API密钥。

失效共享修复：用户手动删除旧链接及备份后，新生成的普通目标目录可通过明确确认修复。限定单目标事务、原备份和暂存缺失、目标身份与旧目录不同、独立完整来源且无活动依赖；原清单原样归档，当前目标保留为新事务备份，再创建链接。归档后再次检查目标身份，拒绝覆盖第三方替换目录。

验收：自动发现真实 Steam 库路径；VDF 保留其他配置；资源测试验证链接读写同一文件、旧目录无损、幂等、还原、路径边界；真实 SQLite 持久化和事务；MVVM/DI 结构审查；UAC manifest 与运行令牌检查；100% 行和分支覆盖报告；真实 WPF 窗口启动与交互；win-x64 自包含发布；ZIP 校验和；向已授权私有仓库的 master 发 PR。
