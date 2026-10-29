# PCL 下载功能解锁器

恢复 **Plain Craft Launcher 2 2.99 系列** 中被 `RestrictMcDownload` 常量屏蔽、隐藏的 Minecraft 下载功能。

支持版本（运行时自动检测）：

- **2.99.0.0**（413 正式版 / 414 快照版）
- **2.99.0.1**（415 正式版 / 416 快照版）
- **2.99.1 及更高版本**：按 2.99.0.1 的形状尝试；锚点不匹配的补丁自动跳过，写盘前的 JIT 校验保证不会输出非法 IL

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

### 两个版本共有的稳定性修复（补丁 13）

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 13 | `PageDownloadInstall.LoadOptiFineGetError` / `LoadFabricGetError` / `LoadNeoForgeGetError` / `LoadLiteLoaderGetError` | 从 MC 更新弹窗直接点“下载”时，主版本列表先完成，`ReloadSelected` 会在组件加载器的 `Output.Value` 还没赋值时读取它，抛 `NullReferenceException`，整个下载页显示红叉 | 入口增加空值保护，数据未就绪时返回“获取中……”，加载器完成后再由 PCL 自己刷新 |

这个空引用只在“更新弹窗 → 下载”的快速路径上稳定复现；手动进入下载页、慢慢选版本时，组件加载器通常已经完成，所以不容易遇到。

### 两个版本共有的启动优化（补丁 14）

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 14 | `FormMain` 初始化线程里的 `DlClientListMojangLoader.Start(1)` | 启动预取的输入是 `1`，下载页主加载器用 `null`；进入下载页时输入不一致，会取消正在进行的 manifest 请求并重新发一次，表现为先卡在 0% 几秒 | 启动预取也改用 `null`，两边复用同一次请求：还在加载就等它，已经完成就直接用 |

0% 本身仍然是 PCL 在拉 Mojang 的版本清单（网络正常也要 1–4 秒）；补丁 14 只是避免“刚启动就点下载”时把预取取消掉再重发。如果网络到 Mojang 很慢，可以在 PCL 设置里把下载源改成镜像。

### 解锁版的自更新保护（补丁 15）

| # | 位置 | 原状 | 补丁后 |
|---|------|------|--------|
| 15 | `ModSecret.UpdateStart` / `ModSecret.UpdateCheckByButton` | 官方更新器拿当前进程路径当目标，会把 `Plain Craft Launcher 2.unlocked.exe` 直接覆盖成官方新版本，补丁全部丢失 | 解锁版跳过自动/静默更新；手动点“检查更新”会提示先更新原版 |

原版 `Plain Craft Launcher 2.exe` 不受影响，仍然可以正常更新。更新完原版后重新运行一次 `PCLUnlock.exe`，就会生成带新版本补丁的解锁版。

恢复后的完整链路：

- 下载页：正式版 / 预览版 / 远古版 / 愚人节版，原版游戏安装，Forge / NeoForge / Fabric / OptiFine / LiteLoader
- 整合包：资源页搜索 → 点进整合包 → **点版本行一键安装**；资源页“安装已有整合包”选本地文件；版本选择页“导入整合包”；或直接把 zip 拖进窗口

## 版本检测

补丁前会读取 `ModBase.VersionCode` / `VersionBaseName`（VB 的 `Public Const`，编译期内联，但元数据里仍保留为 literal 字段）判断目标 exe 的版本：

- 413/414 → 按 2.99.0.0 处理，跳过补丁 6–12（补丁 13–15 仍然套用）
- 415/416 → 按 2.99.0.1 处理，全部套用
- 417+（2.99.1 等）→ 仍按 2.99.0.1 的形状尝试，结构不符的位置打印 `[跳过]`
- 识别失败 → 尝试全部补丁，不匹配的位置自动跳过并打印 `[跳过]`

所有补丁都可重复执行：已打过补丁的文件再跑一遍，已恢复的位置会自动跳过。

写盘后还会对本次修改过的所有方法做一次强制 JIT 校验；如果补丁写出的 IL 不合法（例如求值栈不平衡），工具会删除输出并报错，而不是留下一个切到对应页面就崩溃的 exe。

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
- 解锁版内置自更新保护：点“检查更新”会提示，不会下载或覆盖 `Plain Craft Launcher 2.unlocked.exe`。
- 更新 PCL 的方法：退出解锁版 → 运行官方 `Plain Craft Launcher 2.exe` 更新到最新 → 重新运行 `PCLUnlock.exe` 生成新的解锁版。PCL 自动更新不会碰原版以外的文件，版本变了也没关系，工具会自动检测。
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
- 补丁 13 真机实测：从 MC 更新弹窗点“下载”快速进入下载页，不再出现 “未将对象引用设置到对象的实例”，组件列表随后正常填充
- 相关 4 个类型的 435 个方法全部通过强制 JIT 校验，无 `InvalidProgramException`
- 2.99.0.1 (415/416)：补丁 6–12 按官方 commit `e3d1cff` 的源码逐个核对了编译产物形状；并把真机 2.99.0.0 的 IL 人工改造成 2.99.0.1 形状（模拟 DCE 与常量折叠）做了回环验证——“导入整合包”重插的 Add 调用与原版逐指令一致，启动按钮状态、空状态按钮的翻转位置正确。遇到形状不符会打印 `[跳过]` 并保持文件完好，欢迎实测反馈
- 2.99.0.1 (416) 真机：修复 `PageDownloadLeft..ctor` 的 `InvalidProgramException`（补丁 6 改为截断到 `InitializeComponent` 后补 `ret`，不再逐条 nop 留下悬空 `ldarg.0`）；下载页可正常打开
- 2.99.0.1 (416)：补丁 7 按真实的 `ldarg.1; ldc.i4.2; pop; pop; ldc.i4.2` 形状重建为运行时分支，并保留原 `ret` 作为开头 `beq.s` 的目标，避免分支指向已删除指令
- 补丁 15：解锁版的 `UpdateStart` 入口直接 `ret`，`UpdateCheckByButton` 提示后返回；写盘后的 JIT 校验通过
- 补丁 6 修复后在 416 原件上重新生成：19 个被修改的方法全部通过 `PrepareMethod` 校验；对已解锁文件重复运行会逐项打印 `[跳过]`，不再改动已恢复的位置

## 致谢

- [PCL](https://github.com/Meloong-Git/PCL) / [MeloongCore](https://github.com/Meloong-Git/MeloongCore) —— 被研究的对象
- [dnlib](https://github.com/0xd4d/dnlib) —— IL 读写

## License

[MIT](LICENSE)
