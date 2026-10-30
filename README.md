# XEHN · Fluent Edition

Windows 桌面 UI 重构：C#、WPF、.NET 10 内置 Fluent 主题，橘色强调色。

## 本轮范围

- 首页去掉顶部标题、副标题和品牌标签；连接卡片上移，居中大电源按钮无文字。
- 首页提供紧凑的「代理 / TUN」和「全局 / 分流」分段控件。分流对应 GeoIP / GeoSite 绕过大陆。
- 协议放在设置第一项；主题设置移到设置页，支持浅色、深色、跟随系统。
- 无流量时数值与曲线留白，不绘制占位短横线或提示文案。
- 不导入旧版 GUI、启动声明弹窗、机器控制码、远程停用名单或自动更新替换逻辑。
- 关于页提供 Geo 数据库的本地文件检查和缺失文件下载。**目前不是远端版本比较或强制更新**。
- 保留开源来源与第三方许可证入口。

## 连接准备与连接速度（2026-10-30）

- 启动或切换协议后，后台检查所选协议的账号，按需注册；保留已完成的注册，不重复轮换可用凭据。随后检查缓存，缺失、过期或优选强度不匹配时才扫描。
- 首页电源按钮下显示「注册」「优选」两个进度条；未知总量的网络注册阶段使用活动进度，扫描使用真实已处理候选数，不显示虚构百分比。
- 切换协议会取消旧准备任务并串行启动最新任务；准备过程中点击连接，会等待并复用该任务，不再重复注册或扫描。
- 优选强度低 / 中 / 高，默认中；分别使用每子网 1 / 3 / 8 个采样、找到 2 / 4 / 8 个可用候选后提前结束。强度不是速度或质量保证，实际耗时仍取决于网络。
- 「连接前检查节点可用性」默认关闭：核心控制接口就绪后显示「已连接（检测中）」，继续真实数据面验证，失败则立即撤回状态并停止核心。开启后，验证完成才显示连接成功。**系统代理仍在验证成功后接管**，不是跳过检测或承诺此时已有可用网络。
- 有账号、有有效缓存时跳过注册和扫描；桌面连接不重复运行协议能力探测子进程，仍对最终完整配置进行校验。
- MASQUE 的 `handshake-timeout` 已恢复为 5 秒。本轮不继续处理 H2 的连通问题。

本轮离线验证：38 项 WPF / 设置 / 异步准备流程检查通过，`go test -count=1 ./...` 与 `go vet ./...` 通过。测试覆盖准备中切换协议、排队连接、取消、过期消息隔离、快速模式验证失败、注册中间结果保留和热缓存不启动核心。测试程序禁止启动真实主窗口，并将异常输出终端，避免失败测试触发应用错误弹窗。未测量真实网络连接耗时。

## 先前验证状态（2026-10-29）

WPF 前端编译通过，离线检查程序覆盖 19 项设置校验、空流量、导航和首页布局检查，并生成真实 WPF 控件离屏渲染图。

连接引擎已使用 **Go 1.26.3** 完成构建（Windows GUI 子系统，无控制台弹窗），WPF 通过隐藏子进程调用 `engine/xehn-engine.exe`。

2026-10-29 验证结果：
- `go mod verify`：全部模块校验通过。
- `go test -count=1 ./...`：3 个包通过，53 个测试项通过；4 个需要指定真实内核的测试默认跳过。
- 单独指定本地 mihomo 后，`TestProbeConfigsAccepted` 的 WG、AWG、WARP-in-WARP、MASQUE、MASQUE-H2、MASQUE-in-MASQUE、OpenVPN 共 7 种配置通过真实内核 `-t` 解析检查。
- `Xehn.EngineChecks`：7 项 C# → Go 引擎联调检查通过，覆盖隐藏进程、UTF-8 输出、非零退出码、缺失/存在的 Geo 数据库、过期状态和回环模拟流量接口。
- WPF 构建 0 警告、0 错误；19 项离线 UI / 设置检查通过。

**以上不等同于真实 WARP 连通或网速证明。** 尚未运行实际注册、扫描连接、系统代理接管或 TUN 端到端测试；未启动 `TestMihomoAcceptsRenderedConfig` 的内核监听服务测试。实际联网请先关闭原客户端的连接，避免默认代理 / API 端口冲突。

这是一轮 UI 迁移，不是完整发布版。旧版节点收藏/固定节点选择、Psiphon/OpenVPN 出口选择、开机自启等高级界面尚未迁移；对应网络实现仍保留在 Go 源码中。未打包、发布、安装、修改系统代理或修改现有 XEHN 安装。

## 结构

```text
src/Xehn.App/       WPF 界面、设置、连接引擎适配器、流量图
engine/             从原项目导入的 Go 网络引擎，去掉旧 GUI 和远程控制
tests/Xehn.Checks/   无外部测试包的离线检查、WPF 渲染图
build.ps1           本地构建脚本（不安装、不发布）
```

原项目：[hezhanleiok/xehn](https://github.com/hezhanleiok/xehn)。Go 源码来自 2026-10-29 下载的 main 快照；本地变更为剥离旧 GUI/远程控制及客户端自动更新调用、新增 desktop.go 桥接命令与设置映射测试、将共享版本号函数移回引擎并转发连接进度日志。原第三方声明保留在 engine/THIRD_PARTY_NOTICES.md，许可文本在 engine/third_party/。

Fluent 实现遵循 [WPF 官方主题说明](https://github.com/dotnet/wpf/blob/main/Documentation/docs/using-fluent.md)，没有浏览器或 WebView。

## 构建与检查

要求 .NET 10 Windows SDK 和 Go 1.26.3+。构建脚本优先使用仓库 `.cache/toolchains/go1.26.3/go/bin/go.exe`，否则使用 PATH 中的 Go；也可以通过 `-GoExecutable` 指定工具链。此机器的独立工具链来自官方 ZIP，SHA256 已与 go.dev 发布清单核对，不修改系统 Go。

```powershell
# UI 独立构建
& 'C:\Program Files\dotnet\dotnet.exe' build .\src\Xehn.App\Xehn.App.csproj -o .\artifacts\app

# 离线检查和界面预览，不启动连接、不修改系统代理
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tests\Xehn.Checks -- .\artifacts\previews

# 完整本地构建；可选从原安装目录只复制内核与数据库
.\build.ps1 -CoreSource 'D:\program file\XEHN\core'

# 安全的引擎桥接检查（不连接、不修改系统代理）
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tests\Xehn.EngineChecks -- .\artifacts\app\engine\xehn-engine.exe .\artifacts\app\data
```

本地构建入口为 `artifacts/app/Xehn.App.exe`。本地副本数据位于该 exe 旁的 `data/`，引擎位于 `engine/xehn-engine.exe`；构建脚本只允许复制 mihomo.exe、geoip.metadb、geosite.dat，**不会复制用户账户、配置、缓存、日志或连接状态**。

网络设置仅在断开时可修改。TUN 会先检查管理员权限，未提权时不会启动。关闭前需要先断开连接；连接进行中可再点击电源取消，等待引擎清理完成。引擎取消通过标准输入传递，不强杀进程以免遗留系统代理。

数据库检查沿用原引擎的文件存在/大小初检，不宣称签名验证或内容解析验证。首次使用分流前会下载缺失的 GeoIP / GeoSite；下载失败则中止连接，不静默降级到全局。
