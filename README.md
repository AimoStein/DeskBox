# 简桌 · DeskBox

Windows 10/11 桌面整理工具：分区盒子、拖拽排序、一键整理、双击隐藏图标与任务栏，可选 Liquid Glass 玻璃背景。

**[下载 Windows x64 单文件版](https://github.com/AimoStein/DeskBox/releases/latest/download/DeskBox.exe)** · [更新记录](https://github.com/AimoStein/DeskBox/releases)

## 使用

1. 下载后双击 `DeskBox.exe`，无需安装或另装运行时。
2. 把文件拖进盒子；双击托盘图标打开设置，修改自动保存。
3. 设置 → **排布** 可排齐盒子；**整理** 可归类桌面文件、撤销上次整理。
4. 双击桌面空白处隐藏或恢复图标；设置 → **图标** 可关闭此功能或任务栏联动。
5. 设置 → **更新** 可检查、下载并重启更新，自动检查可关闭。

Liquid Glass 需要 Wallpaper Engine 视频壁纸及支持 Direct3D 11 的显卡，不可用时使用普通背景。

## 数据

- 盒子是真实文件夹，默认在桌面的 `桌面整理` 中；解散盒子会将文件移回桌面。
- 配置和日志保存在程序旁，目录不可写时使用 `%APPDATA%\DeskBox`。升级只替换程序，保留配置和盒子文件。
- 无账号、无同步、无遥测；仅检查与下载更新时联网。

## 构建

Windows + .NET 8 SDK，在仓库根目录运行：

```powershell
dotnet publish src/DeskBox/DeskBox.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

## 许可

[MIT](LICENSE)
