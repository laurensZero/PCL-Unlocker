# PCL 2.99 Unlocker

恢复 **Plain Craft Launcher 2 2.99.0.0 (414)** 中被 `RestrictMcDownload` 常量屏蔽的 Minecraft 下载功能。

只做 IL 层面的补丁，**不修改原文件**，输出一个新的 exe，两者共用同一份配置。

> 仅供学习与逆向研究使用。PCL 的功能删减是作者出于合规原因主动做出的，是否使用本工具请自行判断，并遵守当地法律法规与软件许可协议。本仓库不包含 PCL 的任何代码或二进制文件。

---

## 恢复了什么

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 1 | `PageDownloadInstall.LoaderInit` | 版本列表加载器被换成固定抛异常的 “关闭下载” 占位任务 | 换回真正的 `ModDownload.DlClientListLoader` |
| 2 | `ModModpack.ModpackInstall(File, InstanceName, Logo)` | 识别完压缩包类型后直接 `throw`，后面的 `Select Case` 被编译器删除 | 重建 7 路分派（CurseForge / HMCL / MMC / MCBBS / Modrinth / 启动器包 / 普通压缩包） |
| 3 | `ModModpack.ModpackInstall()` | 只剩 19 字节：一句提示 + `ret` | 恢复“选择整合包压缩文件”对话框 + 后台线程安装 |
| 4 | `PageDownloadModify.LoadInstanceFinished` | `CardMinecraft.Visibility = Collapsed` | 改回 `Visible` |
| 5 | `PageDownloadResourceDetail.InitializeStackResourceVersions` | 版本行点击被固定成“保存”，图标按钮为空 | 整合包下改为“点版本行 = 一键下载安装，图标 = 保存” |

恢复后的完整链路：

- 下载页：正式版 / 预览版 / 远古版 / 愚人节版，原版游戏安装，Forge / NeoForge / Fabric / OptiFine / LiteLoader
- 整合包：资源页搜索 → 点进整合包 → **点版本行一键安装**，或“安装已有整合包”选本地文件，或直接把 zip 拖进窗口

## 用法

1. 从 [Releases](../../releases) 下载 `PCLUnlock.exe`
2. 把它放到 PCL 所在目录（和 `Plain Craft Launcher 2.exe` 同级）
3. 运行：

```bat
PCLUnlock.exe "Plain Craft Launcher 2.exe" "Plain Craft Launcher 2.unlocked.exe"
```

不带参数时默认就是上面这对文件名。之后运行生成的 `Plain Craft Launcher 2.unlocked.exe` 即可。

也可以直接双击运行，参数默认值已经配好。

### 注意

- **原版文件不会被修改**，可以随时对比、回滚。
- PCL 自动更新会覆盖原版 exe；更新后重新跑一次本工具就行（已打过补丁的文件会自动跳过，可重复执行）。
- 需要 .NET Framework 4.x（PCL 本身也依赖它）。

## 自己编译

```powershell
.\build.ps1
```

脚本会自动从 NuGet 拉取 dnlib 4.4.0，用 Windows 自带的 `csc.exe` 编译，并把 dnlib 以资源形式内嵌，产出单文件 `PCLUnlock.exe`。不需要安装 .NET SDK。

## 原理

`RestrictMcDownload` 在源码里是：

```vb
Public Const RestrictMcDownload As Boolean = True
```

`Const` 会在编译期内联，于是 `If RestrictMcDownload Then ... Return` 之后的所有代码都成了死代码，被 VB 优化器**整段删除**。所以这不是运行时开关，而是相关 IL 根本没有生成——这也是为什么 `ModpackInstall()` 只剩 19 字节、`ModpackInstall(File, ...)` 只剩 751 字节。

补丁就是按官方 2.99.0.0 的源码（commit `e4b0489`）把这些 IL 重新写回去。

细节见 [docs/internals.md](docs/internals.md)：怎么定位、每个补丁的 IL 前后对照、try/finally 与 `leave` 的坑、以及用 `RuntimeHelpers.PrepareMethod` 做 JIT 校验的方法。

## 验证情况

- 5 处补丁生成的文件与手工验证过的版本 **逐字节一致**（`sha256 F8AE81A3…`）
- 相关 4 个类型的 435 个方法全部通过强制 JIT 校验，无 `InvalidProgramException`
- 真机实测：
  - 下载页成功拉取版本列表（26.3 正式版 / 26.4-snapshot-5 预览版），Forge / Fabric / NeoForge / OptiFine / LiteLoader 列表全部加载完成
  - 整合包详情页点版本行 → 弹出“输入版本名称”对话框（即 `Install_Click` 已接上）
  - “安装已有整合包”正常弹出文件选择框

## 致谢

- [PCL](https://github.com/Meloong-Git/PCL) / [MeloongCore](https://github.com/Meloong-Git/MeloongCore) —— 被研究的对象
- [dnlib](https://github.com/0xd4d/dnlib) —— IL 读写

## License

[MIT](LICENSE)
