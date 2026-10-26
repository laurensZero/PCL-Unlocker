# PCL 下载功能解锁器

恢复 **Plain Craft Launcher 2 2.99 系列** 中被 `RestrictMcDownload` 常量屏蔽、隐藏的 Minecraft 下载功能。

支持版本（运行时自动检测）：

- **2.99.0.0**（413 正式版 / 414 快照版）
- **2.99.0.1**（415 正式版 / 416 快照版）

只做 IL 层面的补丁，**不修改原文件**，输出一个新的 exe，两者共用同一份配置。

> 仅供学习与逆向研究使用。PCL 的功能删减是作者出于合规原因主动做出的，是否使用本工具请自行判断，并遵守当地法律法规与软件许可协议。本仓库不包含 PCL 的任何代码或二进制文件。

---

## 恢复了什么

### 两个版本共有的恢复（补丁 1–5）

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 1 | `PageDownloadInstall.LoaderInit` | 版本列表加载器被换成固定抛异常的 “关闭下载” 占位任务 | 换回真正的 `ModDownload.DlClientListLoader` |
| 2 | `ModModpack.ModpackInstall(File, InstanceName, Logo)` | 识别完压缩包类型后直接 `throw`，后面的 `Select Case` 被编译器删除 | 重建 7 路分派（CurseForge / HMCL / MMC / MCBBS / Modrinth / 启动器包 / 普通压缩包） |
| 3 | `ModModpack.ModpackInstall()` | 只剩 19 字节：一句提示 + `ret` | 恢复“选择整合包压缩文件”对话框 + 后台线程安装 |
| 4 | `PageDownloadModify.LoadInstanceFinished` | `CardMinecraft.Visibility = Collapsed` | 改回 `Visible` |
| 5 | `PageDownloadResourceDetail.InitializeStackResourceVersions` | 版本行点击被固定成“保存”，图标按钮为空 | 整合包下改为“点版本行 = 一键下载安装，图标 = 保存” |

### 2.99.0.1 新增的限制点（检测到版本 ≥ 415 才套用，补丁 6–12）

2.99.0.1 把 `RestrictMcDownload` 的限制铺到了更多 UI 入口，模式全部相同：原来走运行时判断的代码被常量折叠成 `Visibility.Collapsed` 或直接删除。

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 6 | `PageDownloadLeft..ctor` | 下载页左侧的“安装”“社区资源”入口被硬编码 `Collapsed`，强制落在 Mod 页 | 恢复 `Visible`，默认落回安装页 |
| 7 | `PageResource.set_PageType` | 资源页“安装已有整合包”按钮恒为 `Collapsed` | 整合包下恢复 `Visible` |
| 8 | `PageSelectLeft`（构建文件夹列表） | “导入整合包”项的 `Children.Add` 调用被编译器删除 | 写回 Add 调用（克隆同方法里其他项的调用） |
| 9 | `PageLaunchLeft.RefreshButtonsUI` | 无版本时启动按钮固定为“下载已禁用”死状态 | 恢复“下载游戏”状态（Case 2 分支本来就在 IL 里） |
| 10 | `PageInstanceModDisabled.BtnDownload_Loaded` | “下载 Mod”按钮恒为 `Collapsed` | 改回 `Visible` |
| 11 | `PageSelectRight`（刷新版本列表） | 空状态的“下载游戏”按钮恒为 `Collapsed` | 改回 `Visible`（只改受限的那一处） |
| 12 | `ModMinecraft.McDownloadClientUpdateHint` | MC 更新提示弹窗的“下载”按钮文本被换成 `""` | 恢复“下载”，跳转下载页的逻辑本来就在 |

恢复后的完整链路：

- 下载页：正式版 / 预览版 / 远古版 / 愚人节版，原版游戏安装，Forge / NeoForge / Fabric / OptiFine / LiteLoader
- 整合包：资源页搜索 → 点进整合包 → **点版本行一键安装**；资源页“安装已有整合包”选本地文件；版本选择页“导入整合包”；或直接把 zip 拖进窗口

## 版本检测

补丁前会读取 `ModBase.VersionCode` / `VersionBaseName`（VB 的 `Public Const`，编译期内联，但元数据里仍保留为 literal 字段）判断目标 exe 的版本：

- 413/414 → 按 2.99.0.0 处理，跳过补丁 6–12
- 415/416 → 按 2.99.0.1 处理，全部套用
- 识别失败 → 尝试全部补丁，不匹配的位置自动跳过并打印 `[跳过]`

所有补丁都可重复执行：已打过补丁的文件再跑一遍，已恢复的位置会自动跳过。

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
- PCL 自动更新会覆盖原版 exe；更新后重新跑一次本工具就行（版本变了也没关系，会自动检测）。
- 需要 .NET Framework 4.x（PCL 本身也依赖它）。

## 自己编译

```powershell
.\build.ps1
```

脚本会自动从 NuGet 拉取 dnlib 4.4.0，用 Windows 自带的 `csc.exe` 编译，并把 dnlib 以资源形式内嵌，产出单文件 `PCLUnlock.exe`。不需要安装 .NET SDK。

编译脚本会把 `src/` 下**所有** `.cs` 一起编译：如果你想加自己的私有补丁（比如本仓库刻意不放的联机恢复），写一个 `src/MyPatches.cs`、在 `PclUnlocker` 命名空间下定义任意类型即可；主程序对下载补丁之外的部分不做假设，私有补丁可以通过反射扩展点（定义 `PclUnlocker.LinkPatches.Run()` 静态方法）自动接入。不想公开的文件记得自己加进 `.gitignore`。

## 原理

`RestrictMcDownload` 在源码里是：

```vb
Public Const RestrictMcDownload As Boolean = True
```

`Const` 会在编译期内联，于是 `If RestrictMcDownload Then ... Return` 之后的所有代码都成了死代码，被 VB 优化器**整段删除**。所以这不是运行时开关，而是相关 IL 根本没有生成——这也是为什么 `ModpackInstall()` 只剩 19 字节、`ModpackInstall(File, ...)` 只剩 751 字节。

补丁就是按官方源码（2.99.0.0 对应 commit `e4b0489`，2.99.0.1 对应 commit `e3d1cff`）把这些 IL 重新写回去，并把新版本折叠成常量的 UI 隐藏改回来。

细节见 [docs/internals.md](docs/internals.md)：怎么定位、每个补丁的 IL 前后对照、try/finally 与 `leave` 的坑、以及用 `RuntimeHelpers.PrepareMethod` 做 JIT 校验的方法。

## 验证情况

- 2.99.0.0 (414)：前 5 处下载补丁生成的文件与手工验证过的版本 **逐字节一致**（`sha256 F8AE81A3…`）
- 补丁 1–5 真机实测：下载页成功拉取版本列表（26.3 正式版 / 26.4-snapshot-5 预览版），Forge / Fabric / NeoForge / OptiFine / LiteLoader 列表全部加载完成；整合包详情页点版本行 → 弹出“输入版本名称”对话框；“安装已有整合包”正常弹出文件选择框
- 相关 4 个类型的 435 个方法全部通过强制 JIT 校验，无 `InvalidProgramException`
- 2.99.0.1 (415/416)：补丁 6–12 按官方 commit `e3d1cff` 的源码逐个核对了编译产物形状；并把真机 2.99.0.0 的 IL 人工改造成 2.99.0.1 形状（模拟 DCE 与常量折叠）做了回环验证——“导入整合包”重插的 Add 调用与原版逐指令一致，启动按钮状态、空状态按钮的翻转位置正确。遇到形状不符会打印 `[跳过]` 并保持文件完好，欢迎实测反馈

## 致谢

- [PCL](https://github.com/Meloong-Git/PCL) / [MeloongCore](https://github.com/Meloong-Git/MeloongCore) —— 被研究的对象
- [dnlib](https://github.com/0xd4d/dnlib) —— IL 读写

## License

[MIT](LICENSE)
