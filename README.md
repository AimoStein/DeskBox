# 简桌 · DeskBox

Windows 10/11 应用与文件启动面板，支持拖拽归组、搜索、按打开次数排序和面板背景折射。

**[下载 Windows x64 单文件版](https://github.com/AimoStein/DeskBox/releases/latest/download/DeskBox.exe)** · [更新记录](https://github.com/AimoStein/DeskBox/releases)

## 使用

1. 下载后双击 `DeskBox.exe`，无需安装或另装运行时。
2. 单按并松开 **Win** 打开面板，也可以双击托盘图标。**Ctrl+Esc** 打开 Windows 原生开始菜单。
3. 总览将现有分组排成独立的扇区，快捷方式对齐到共用的同心环，背景和分隔线在外圈渐隐。点击圆心搜索，底部显示 **未分组** 的快捷方式。桌面图标在简桌运行时收起，双击桌面空白处切换任务栏显示。
4. 在面板里搜索、拖动项目到分组，或右键调整归属。每组里打开次数多的项目靠近圆心，次数相同按名称排序。右键快捷方式可以分别定位目标文件和快捷方式文件，也可以把快捷方式放入回收站。设置 → **整理** 可按类型归类桌面项目，或撤销上次整理。
5. 面板选项可调整背景亮度、色调和饱和度，更换图片或视频、恢复默认渐变，或关闭 Win 键接管。
6. 设置 → **版本更新** 可检查、下载并重启更新，自动检查可关闭。

面板默认使用蓝灰渐变和细描边，自定义图片或视频可开启玻璃折射。需要使用旧版桌面格子时，退出简桌后用参数 `--desktop-boxes` 启动。

## 数据

- 盒子使用虚拟分组：拖入只记录原路径，解散分组不移动或删除文件。旧盒子自动转换，原文件夹、内容和布局保留。
- 配置和日志保存在程序旁，目录不可写时使用 `%APPDATA%\DeskBox`。升级只替换程序，保留配置和盒子文件。
- 无账号、无同步、无遥测；仅检查与下载更新时联网。

## 登录启动

开启开机自启后，简桌优先使用零延迟登录任务，在后台准备面板和索引。

使用内置 Administrator 且普通注册失败时，可以在管理员 PowerShell 中运行 `tools/install-fast-start.ps1 -Executable "C:\路径\DeskBox.exe"`。此操作安装启动器，简桌使用交互桌面的普通权限运行。设置里的开机自启开关仍可控制启动。需要移除快速启动任务时，使用相同脚本并传入 `-Uninstall`。

## 构建

Windows + .NET 8 SDK，在仓库根目录运行：

```powershell
dotnet publish src/DeskBox/DeskBox.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

## 许可

[MIT](LICENSE)
