# 项目约定

- 使用中文注释覆盖所有自定义类型、方法、字段和属性。
- 仅实现 Master Duel 换号与资源复用，禁止无关重构及示例代码。
- dev 分支开发，各阶段验证后提交；master 作为 PR 基线。
- 禁止在日志、源码或交付包中保存 Steam 密码、令牌和登录凭证。
- 资源操作仅针对 LocalData/<8 位十六进制账号目录>/0000；不共享 LocalSave 或整个账号目录。
- 任何资源替换先保留原始目录并写入事务清单；还原只移除匹配的 junction，不递归删除共享源。
- 游戏运行时拒绝切号和资源变更；Steam 自行处理首次登录及过期会话验证。
- 提供 EXE、源码、中文使用说明及真实测试记录。
- 使用 MVVM 和 DI，View 不承载业务逻辑；账号与设置存 SQLite 系统数据目录。
- 窗口、XAML资源与视图组件置于App/Views；App.xaml仅引用Views层组件。
- 每个导航页具有独立Views/Pages页面及对应ViewModel，由官方NavigationView与DI页面服务导航。
- 正文采用Windows11 Segoe UI Variable字体及中文字体回退；图标使用WPF UI内嵌FluentSystemIcons字体。
- 全部界面使用WPF UI官方Fluent控件、主题资源及系统强调色；禁止自定义淡蓝背景。
- 系统数据目录保存滚动运行日志和Debug日志，记录操作结果与异常，不记录凭据或VDF正文。
- .NET 10；requireAdministrator 标准 UAC；所有自写生产代码行和分支测试覆盖均100%，禁止人为覆盖排除。
- 账号支持模糊搜索、SQLite星标、最近登录置顶；筛选与排序保留未保存的编辑。
- 左侧账号列表独立虚拟化滚动，搜索固定；头像异步加载、限时限并发，失败显示首字。
- 全部通知与确认使用官方ContentDialog及ContentDialogHost；文件夹选择使用Windows系统对话框。
