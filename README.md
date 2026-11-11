# 简桌 · DeskBox

Windows 10/11 桌面整理工具：分区盒子、拖拽排序、一键整理、双击隐藏图标与任务栏，可选 Liquid Glass 玻璃背景。

**[下载 Windows x64 单文件版](https://github.com/AimoStein/DeskBox/releases/latest/download/DeskBox.exe)** · [更新记录](https://github.com/AimoStein/DeskBox/releases)

## 使用

1. 下载后双击 `DeskBox.exe`，无需安装或另装运行时。
2. 把文件拖进盒子；双击托盘图标打开设置，修改自动保存。
3. 设置 → **排布** 可排齐盒子；**整理** 可归类桌面文件、撤销上次整理。
4. 双击桌面空白处隐藏或恢复图标；设置 → **图标** 可关闭此功能或任务栏联动。
5. 设置 → **更新** 可检查、下载并重启更新，自动检查可关闭。
6. 简桌运行时统一显示桌面图标，同一个启动入口只显示一次。对无法自动识别的快捷方式，可在右键菜单中选择 **合并同应用的快捷方式**。

Liquid Glass 支持 Windows 静态壁纸和可捕获的动态壁纸，需要支持 Direct3D 11 的显卡。壁纸纹理不可用时，盒子使用普通背景。

## 数据

- 盒子使用虚拟分组：拖入只记录原路径，解散分组不移动或删除文件。旧盒子自动转换，原文件夹、内容和布局保留。
- 配置和日志保存在程序旁，目录不可写时使用 `%APPDATA%\DeskBox`。升级只替换程序，保留配置和盒子文件。
- 无账号、无同步、无遥测；仅检查与下载更新时联网。

## 登录启动

开启开机自启后，简桌优先使用零延迟登录任务。已有桌面缓存时，简桌先显示上次的盒子布局和可点击图标，再切换到完整界面。

使用内置 Administrator 且普通注册失败时，可以在管理员 PowerShell 中运行 `tools/install-fast-start.ps1 -Executable "C:\路径\DeskBox.exe"`。此操作安装启动器，简桌使用交互桌面的普通权限运行。设置里的开机自启开关仍可控制启动。需要移除快速启动任务时，使用相同脚本并传入 `-Uninstall`。

## 构建

Windows + .NET 8 SDK，在仓库根目录运行：

```powershell
dotnet publish src/DeskBox/DeskBox.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

## 许可

[MIT](LICENSE)
