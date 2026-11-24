<p align="center">
  <img src="src/Xehn.App/Assets/logo-readme.png" alt="TrashVPN icon" width="128">
</p>
<h1 align="center">TrashVPN for Windows</h1>
<p align="center">A third-party WARP client that runs on Windows</p>
<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong> · <a href="README.fa.md">فارسی</a></p>

---

## Features

- **Automatic preparation and node selection:** Register and select nodes automatically; pause, resume, or rescan, and adjust scan intensity and cache lifetime.
- **Multiple protocols:** WireGuard, AmneziaWG, MASQUE (HTTP/3 and HTTP/2), and nested connections.
- **Flexible connection modes:** Choose system proxy or TUN, with global or split routing.
- **Node configuration export:** Copy valid cached nodes as Clash (Mihomo), Xray, or sing-box configuration snippets.
- **Desktop experience:** System tray, launch at sign-in, light and dark themes, and Simplified Chinese, English, and Persian interfaces.

## Download and install

Supports **64-bit Windows 10 and Windows 11**.

Check the [official download page](https://itmanager.top/download.html) or [GitHub Releases](https://github.com/FuckGFWall/trashvpn-windows/releases) for available versions.

- **Installer:** Run the setup program and follow the wizard. By default, the app is installed in `%ProgramFiles%\trashvpn-windows` and user data is stored in `%LOCALAPPDATA%\trashvpn-windows`.
- **Portable:** Extract the entire ZIP to its own folder and run `Xehn.App.exe`. Data is stored in the adjacent `data` folder. Choose a writable folder; if the folder is not writable, the app automatically requests administrator privileges.

The installer uses a self-signed certificate, so SmartScreen may still display a warning.

## Getting started

1. Open TrashVPN and wait for account and node preparation. The first preparation requires network access and its duration depends on your network.
2. Choose a tunnel protocol in **Settings**. Adjust scan intensity, cache, and other options if needed.
3. On the home page, choose **Proxy / TUN** and **Global / Split**, then press the power button to connect.
4. Press the power button again to disconnect. Closing the window may only hide the app in the system tray; use the tray menu to quit completely.

If a connection fails, check **Logs** or rescan nodes on the home page. You can also try another protocol or increase the scan intensity.

## Build from source

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Go 1.26.3. From the repository root, run:

```powershell
.\build.ps1
```

The output is in `artifacts\app`. Exit any client running from that directory before building to avoid replacing files in use. Building an installer also requires Inno Setup, Windows SDK SignTool, and prepared mihomo and GeoFiles; see `build-installer.ps1` for parameters and requirements.

## Related projects and credits

- The node selection engine provided by [Xiaohe](https://github.com/hezhanleiok/xiaohe)
- The fast node selection approach provided by [Oblivion](https://github.com/bepass-org/oblivion)
- [TrashVPN for iOS](https://github.com/FuckGFWall/trashvpn-ios)
- [Third-party components and licenses](engine/THIRD_PARTY_NOTICES.md)
