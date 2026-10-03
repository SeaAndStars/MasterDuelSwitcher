# 证据与决策

- 用户确认 Windows Steam；自写 WPF UI 方案已批准。
- WPF UI NuGet 当前4.3.0，官方源 https://github.com/lepoco/wpfui。
- 本机 SDK10.0.201，Windows10 19045；提供 Windows11 Fluent 设计并兼容 Windows10，无 Mica 时采用普通窗口背景。
- Steam 通过 HKLM InstallPath 定位到 C:\Program Files (x86)\Steam；未进行真实账号切换。
- 资源 0000 与账号数据分离；整个账号目录 junction 的社区教程存在账号混淆反馈，采用仅共享0000。
- 新账号需要先通过 Steam 登录，在游戏生成账号目录后绑定；不猜测目录与SteamID映射。
- 所有资源目录变更先写清单并移动原始目录到同层备份；还原验证junction目标，禁止递归删除共享来源。
- 设置5项测试已在stub上观察预期 NotImplementedException 失败，随后实施原子设置读写。
