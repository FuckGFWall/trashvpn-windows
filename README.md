<p align="center">
  <img src="src/Xehn.App/Assets/logo-readme.png" alt="TrashVPN 图标" width="128">
</p>
<h1 align="center">TrashVPN for Windows</h1>
<p align="center">一个简洁的 Windows WARP 客户端</p>
<p align="center"><strong>简体中文</strong> · <a href="README.en.md">English</a> · <a href="README.fa.md">فارسی</a></p>

---

Windows 上的第三方 Cloudflare WARP。

## 功能

- **自动准备与节点优选**：自动注册并筛选节点；可暂停、继续或重新优选，也可以调整扫描强度和缓存有效期。
- **多种协议**：支持 WireGuard、AmneziaWG、MASQUE（HTTP/3 和 HTTP/2）及嵌套连接。
- **灵活的连接模式**：选择系统代理或 TUN，以及全局或分流路由。
- **节点配置导出**：将有效的缓存节点复制为适用的 Clash（Mihomo）、Xray 或 sing-box 配置片段。
- **桌面体验**：支持系统托盘、开机自启动、浅色和深色主题，以及简体中文、英语和波斯语界面。

## 下载与安装

支持 **64 位 Windows 10 和 Windows 11**。

前往 [官方下载页面](https://itmanager.top/download.html) 或 [GitHub Releases](https://github.com/FuckGFWall/trashvpn-windows/releases) 查看可用版本。

- **安装版**：运行安装程序并按照向导操作。默认将程序安装到 `%ProgramFiles%\trashvpn-windows`，用户数据保存在 `%LOCALAPPDATA%\trashvpn-windows`。
- **便携版**：将 ZIP 完整解压到独立目录，运行 `Xehn.App.exe`。数据保存在程序旁的 `data` 文件夹。建议选择可写目录；如果目录不可写，程序会自动请求管理员权限。

安装包使用自签名证书，SmartScreen 仍可能显示警告。

## 开始使用

1. 打开 TrashVPN，等待账号和节点准备完成。首次准备需要网络，耗时取决于当前网络环境。
2. 在**设置**中选择隧道协议；如有需要，可调整优选强度、缓存和其他选项。
3. 在首页选择**代理 / TUN**和**全局 / 分流**，点击电源按钮连接。
4. 再次点击电源按钮断开。关闭窗口可能只是将应用隐藏到托盘；要完全退出，请使用托盘菜单中的退出选项。

连接失败时，可以查看**日志**，或在首页重新优选节点。连接失败可以切换协议或提高优选强度。

## 从源码构建

需要 Windows、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Go 1.26.3。在仓库根目录运行：

```powershell
.\build.ps1
```

输出位于 `artifacts\app`。如果构建目录中的客户端正在运行，请先退出，避免覆盖正在使用的程序。安装包构建还需要 Inno Setup、Windows SDK SignTool 以及准备好的 mihomo 和 GeoFiles；参数与要求请参阅 `build-installer.ps1`。

## 相关项目与致谢

- [小何](https://github.com/hezhanleiok/xiaohe) 提供的优选引擎
- [TrashVPN for iOS](https://github.com/FuckGFWall/trashvpn-ios)
- [第三方组件及许可证](engine/THIRD_PARTY_NOTICES.md)
