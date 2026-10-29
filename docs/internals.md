# 内部实现笔记

记录这次补丁的定位过程、每个补丁的 IL 前后对照，以及踩到的坑。目标是让后来的人能照着改下一个版本。

## 目标文件

```
Plain Craft Launcher 2.exe
  PE32, .NET Framework 4.0.30319, VB.NET
  5,985,280 字节
  TypeDef 665 / MethodDef 6339
  无壳、无混淆
```

依赖（MeloongCore、PCLCS、Newtonsoft.Json 等）以 `Byte[]` 形式内嵌在 `PCL.Resources.resources` 里，运行时通过 `AssemblyResolve` 从内存加载，所以磁盘上看不到这些 dll。

## 为什么不能"改一个开关"

源码：

```vb
Public Const RestrictMcDownload As Boolean = True
```

三个直接证据说明它是编译期内联：

1. **字段是 literal**：`RestrictMcDownload` 在 Field 表里是 `Public Static Literal Boolean`，值是常量 1，不占运行时存储。
2. **全程序集没有任何 `ldsfld` 引用它**。如果它是普通字段，至少会有一处读取。
3. **方法被物理缩小**：

   | 方法 | 字节数 | 说明 |
   |------|--------|------|
   | `ModModpack.ModpackInstall()` | 19 | 源码 26 行，只剩 `Hint` + `ret` |
   | `ModModpack.ModpackInstall(File, InstanceName, Logo)` | 751 | 只剩类型识别 + 一句 `throw` |
   | `ModModpack.InstallPackCurseForge` | 1992 | 未受影响 |
   | `ModModpack.InstallPackModrinth` | 1916 | 未受影响 |

也就是说：`If True Then Hint : Return` 之后的所有代码都成了不可达代码，被 VB 优化器整段删掉，`Select Case` 连分支带方法调用一起没了。**没有任何运行时开关可以打开它。**

## 定位过程

用 `dnfile` + `dncil` 先把 PE 解析出来做静态勘察（方法体积、字符串、调用关系），确认"代码确实被删了"之后，写回阶段换成 `dnlib`——因为 dnlib 里分支目标是**指令对象的引用**，插入/替换指令后偏移和异常处理范围会自动重算。

关键检索手段：

- 按限制提示字符串反查：`为遵守相关法律法规` 只出现在 5 个方法里，其中 2 个就是 `ModpackInstall` 的两个重载。
- 按方法体积排序，异常小的就是被 DCE 过的。
- 按字段/类型反查调用者（`ModpackInstall` 的调用者里有 `FormMain` 的拖放 lambda，说明拖放路径一直都在，只是内部会 `throw`）。

## 补丁 1/5：下载页 LoaderInit

原版把版本列表加载器换成了一个必定抛异常的占位任务：

```il
ldnull                       // arg5
ldstr "关闭下载"
ldarg.0
ldftn _Closure$__::_Lambda$__3-0   // 里面 throw 限制提示
newobj LoaderTask`2::.ctor
...
```

补丁把 `ldstr "关闭下载"` 起的整段构造替换成一条字段读取：

```il
ldsfld ModDownload::DlClientListLoader
```

栈形状保持不变（`this, LoadMinecraft, PanLoad, PanAllBack, Nothing, loader, onFinish, Nothing, 1`），所以后半段完全不用动。

`ModDownload.DlClientListLoader` 这个字段一直都还在，只是没人用了。

## 补丁 2/5：整合包分派

类型识别部分（读 zip、找 `manifest.json` / `mcbbs.packmeta` / `mmc-pack.json` / `modrinth.index.json`）是完好的，最后落到：

```il
IL_02DA ldstr "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能……"
IL_02DF newobj Exception::.ctor(String)
IL_02E4 throw
```

方法里所有的 `leave`（`Exit Try`）目标都是 `IL_02DA` 这条 `ldstr`。

补丁做了三件事：

1. **原地**把这条 `ldstr` 改成 `ldloc.s V_4`（`V_4` 是编译器留下的 `PackType` 局部变量）——不能删掉重建，否则所有 `leave` 会指向空气。
2. 后面接 `switch`，7 个分支分别调用 `InstallPackCurseForge/HMCL/MMC/MCBS/Modrinth/LauncherPack/Compress`，每个分支 `stloc.0`（返回值）后 `leave` 到末尾。
3. 在 `endfinally` 之后追加 `endLabel: ldloc.0; ret`。

## 补丁 3/5：整合包文件选择入口

原版 19 字节：

```il
newobj _Closure$__0-0::.ctor
stloc.0
ldstr "为遵守相关法律法规……"
ldc.i4.2
ldc.i4.1
call Hint
ret
```

注意第一行：**闭包对象还是被创建了**，说明原始代码是 `RunInThread(Sub() ModpackInstall(File) End Sub)`，只是后面的部分被删了。而闭包 `_Closure$__0-0` 和它的 Worker `_Lambda$__0`（含 try/catch + 日志）**都完整保留**，所以直接复用：

```il
newobj _Closure$__0-0::.ctor
stloc.0
// Dialogs.SelectFile("选择整合包压缩文件", False, Nothing, Nothing)
ldstr "选择整合包压缩文件"
ldc.i4.0
ldnull
ldloca V_2 ; initobj Nullable(Of OneOf(...)) ; ldloc V_2
call Dialogs::SelectFile
stloc.1
// 取第一个文件
ldloc.1 ; brfalse ret
ldloc.1 ; callvirt List`1<String>::get_Count ; brfalse ret
ldloc.0
ldloc.1 ; ldc.i4.0 ; callvirt List`1<String>::get_Item
stfld _Closure$__0-0::$VB$Local_File
// RunInThread(AddressOf Worker)
ldloc.0 ; ldftn _Lambda$__0 ; newobj Action::.ctor ; call ModBase::RunInThread
ret
```

`SelectFile` 的第 4 个参数是 `Nullable(Of OneOf(Of IEnumerable(Of ValueTuple(Of String(), String)), String))`，构造过滤器的代价太大，这里传 `default`（无过滤器，显示所有文件），选 zip 即可。

## 补丁 4/5：版本修改页

```il
callvirt get_CardMinecraft
ldc.i4.2            // Collapsed
callvirt set_Visibility
```

改成 `ldc.i4.0`（`Visible`）即可。注意 WPF 的枚举值：`Visible=0, Hidden=1, Collapsed=2`。

## 补丁 5/5：整合包详情页一键安装

原版源码：

```vb
If Not RestrictMcDownload AndAlso TargetResourceType = ResourceTypes.ModPack Then
    Stack.Children.Add(Version.ToListItem(AddressOf Install_Click, AddressOf Save_Click, IsBadDisplay:=HasDuplicates))
Else
    Stack.Children.Add(Version.ToListItem(AddressOf Save_Click, IsBadDisplay:=HasDuplicates))
End If
```

编译后只剩 `Else` 分支：

```il
ldarg.1 ; callvirt Panel::get_Children
ldloc.s V_7
ldarg.0 ; ldftn Save_Click ; newobj MyListItem/ClickEventHandler::.ctor
ldnull                                             // 图标按钮没有处理器
ldloc.1
callvirt ResourceVersion::ToListItem
callvirt UIElementCollection::Add
pop
```

但 **`Install_Click`（750 字节）和它内部的下载+安装闭包都完好**，闭包最后调用 `ModModpack.ModpackInstall(file, name, logo)`——正是补丁 2 恢复的那个方法。

补丁在这段前面插入一个判断，整合包走新路径，其它资源类型（Mod / 资源包 / 光影）走原来的路径：

```il
ldarg.0 ; ldfld TargetResourceType ; ldc.i4.2 ; bne.un <原路径>   // ModPack = 2
ldarg.1 ; callvirt get_Children
ldloc.s V_7
ldarg.0 ; ldftn Install_Click ; newobj MyListItem/ClickEventHandler::.ctor
ldarg.0 ; ldftn Save_Click    ; newobj MyIconButton/ClickEventHandler::.ctor
ldloc.1
callvirt ResourceType::ToListItem
callvirt Add
pop
br <循环尾>
<原路径>
```

`ResourceTypes` 枚举值：`Mod=1, ModPack=2, ResourcePack=4, Shader=8, DataPack=16, Plugin=32, Map=64`。

## 五个坑

1. **`leave` 指向的是指令对象。**
   替换 throw 时如果 `RemoveAt` 再插入新指令，其它 `leave` 的 `Operand` 就悬空了。必须原地改 `OpCode` / `Operand`。

2. **`HandlerEnd == null` 表示"到方法末尾"。**
   在方法尾部追加 `ret` 时，如果 finally 的 `HandlerEnd` 还是 null，新指令会落进异常处理器范围里。要显式 `eh.HandlerEnd = endLabel`。

3. **栈平衡不会总在 JIT 时暴露。**
   给 `switch` 分支压参数时，必须按目标方法**真实参数个数**压。`InstallPackCompress` 只有 `(File, Archive)` 两个参数，多压一个 `ArchiveBaseFolder` 会让 `leave` 时求值栈非空——`PrepareMethod` 照样通过，运行时才炸。所以参数个数要从 `MethodSig.Params.Count` 推导，别写死。
   但“方法返回时栈高不为 0”这种错误 JIT 是会拒绝的（见下面的 `PageDownloadLeft..ctor` 案例），所以补丁写完后统一做一次 `PrepareMethod` 仍然很有价值。

4. **`try` 里不能直接 `ret`。**
   要么 `leave` 到 finally 之后再 `ret`，要么把返回值先存局部变量。本补丁统一用前者。

5. **`MaxStack` 要手动给够。**
   插入指令后 dnlib 不会重算，改完统一设成 8。

## 验证方法

PCL 的依赖 dll 在 `PCL.Resources.resources` 里，可以先解出来：

```csharp
var res = (EmbeddedResource)module.Resources.First(r => r.Name == "PCL.Resources.resources");
var reader = new ResourceReader(res.CreateReader().AsStream());
foreach (DictionaryEntry e in reader) File.WriteAllBytes(dir + "/" + e.Key + ".dll", (byte[])e.Value);
```

然后写个宿主，挂 `AssemblyResolve` 指向解出来的目录，逐个强制 JIT：

```csharp
Assembly.LoadFrom(patched).GetType("PCL.ModModpack", true)
    .GetMethods(...).ToList()
    .ForEach(mi => RuntimeHelpers.PrepareMethod(mi.MethodHandle));
```

非法 IL 会抛 `InvalidProgramException` / `TypeLoadException`。这次对 `ModModpack`、`PageDownloadInstall`、`PageDownloadModify`、`PageDownloadResourceDetail` 四个类型共 **435 个方法**做了校验，全部通过。

但 JIT 只验证 IL 合法性和类型，**验证不了语义**（比如参数顺序、分支条件写反）。所以最终还是用 UIAutomation 在真机上点了一遍：下载页拉列表、整合包详情页点版本行弹出"输入版本名称"对话框、本地整合包选择框正常打开。

## 版本适配

补丁不依赖硬编码偏移，全部靠结构定位：

- 补丁 1：找 `ldstr "关闭下载"`
- 补丁 2：找含 `为遵守相关法律法规` 的 `ldstr`，并断言后面是 `newobj Exception` + `throw`
- 补丁 3：检查方法体里是否已有 `ldftn`（已打过就跳过）
- 补丁 4：找 `get_CardMinecraft` + `ldc.i4.2` + `set_Visibility`
- 补丁 5：找 `ldftn Save_Click` 并断言后面的 `ToListItem` 调用结构

每处都带断言，结构不对会明确报错而不是写坏文件；已打过补丁的文件会自动跳过，可以重复运行。

换版本时大概率仍然可用，但**务必重新对照该版本源码**确认分支内容（尤其是 `Case` 的编号和新增的安装器类型）。

## 2.99.0.1（415/416）的适配

官方在 `e3d1cff`（2.99.0.1）里把 `RestrictMcDownload` 的限制铺到了更多 UI 入口，全是同一种模式：原来走运行时判断的三元表达式被常量折叠成 `Visibility.Collapsed(2)`，或者整段代码被 DCE 删掉。对应的补丁大多就是把 `ldc.i4.2` 改回 `ldc.i4.0`，模式与补丁 4 相同。

原有的 5 处下载补丁锚点在这个版本全部保留（两处文案从"暂时不再提供"改成"不再提供"，但锚定的是 `关闭下载` / `为遵守相关法律法规`，不受影响），联机相关的 `PageSetupUI` / `PageLinkMain` 没有改动。

### 版本检测

`ModBase.VersionCode` / `VersionBaseName` 是 VB 的 `Public Const`，编译期内联，但在元数据里仍保留为 literal 字段（和 `RestrictMcDownload` 一样），读 Constant 表即可拿到 413/414/415/416。≥415 才套用新增补丁；识别失败时全部尝试、靠结构匹配自动跳过。

### 新增补丁要点

| 补丁 | 定位 | 动作 |
|------|------|------|
| 下载页左侧入口 | `PageDownloadLeft..ctor`：`get/ldfld ItemInstall\|LabResource` + `ldc.i4.2` + `set_Visibility` | `ldc.i4.2 -> 0`；nop 掉最后一个 `stfld PageID`（保留字段初始化的 DownloadInstall）和 `ItemMod.Checked = True` |
| 安装已有整合包 | `PageResource.set_PageType`：`BtnSearchInstallModPack` 的三元折叠 | `ldc.i4.2 -> 0`（2.99.0.0 是运行时分支，无此形状，自动跳过） |
| 导入整合包 | `PageSelectLeft.McFolderListUI`：对象初始化器收尾的 `stloc`（把构造完的 ItemInstall 存入局部变量） | 克隆同方法里 ItemAdd 的 `[ldsfld FrmSelectLeft][get_PanList][get_Children][ldloc V][Add][pop]`，插回 `stloc` 之后（2.99.0.0 上 Add 本来就在这个位置） |
| 启动按钮状态 | `PageLaunchLeft.RefreshButtonsUI`：`ldc.i4.1; stloc V`，窗口内出现 `ldloc V` + `ldfld BtnLaunchState` | `ldc.i4.1 -> ldc.i4.2`（Case 2"下载游戏"分支本来就在 IL 里，只是永远走不到） |
| Mod 禁用页按钮 | `BtnDownload_Loaded`：`ldc.i4.2; stloc`（NewVisibility 常量折叠），后 6 条内出现 `get_Visibility` | `ldc.i4.2 -> 0` |
| 空状态按钮 | 含 `无可用版本` 的方法：`BtnEmptyDownload` 的两处 Collapsed 三元组，取第二处（Else 分支里受限的那处，第一处是 ShowHidden 分支、有意保留） | `ldc.i4.2 -> 0` |
| MC 更新提示 | `McDownloadClientUpdateHint`：`ldstr "确定"` 后紧跟 `ldstr ""` | `"" -> "下载"`（跳转下载页的 `MsgResult = 2` 分支完好） |

几个坑：

- **控件访问形状**：XAML 的 `x:Name` 在 VB 里生成 `WithEvents` 字段 + 包装属性，IL 里可能是 `ldfld` 也可能是 `call get_XXX`，两种都要认（`LoadedControlName`）。414 实测全部走 `get_XXX` 属性（如 `get_BtnEmptyDownload`）。
- **按字符串找方法必须限定类型**：`"无可用版本"` 在 `PageDownloadModify.ShowComparison` 里也出现过，全局搜索会先命中错误的方法。`"导入整合包"`、`"无可用版本"`、`"Minecraft 更新提示"` 三处都改为 `FindMethodContainingString(类型, 文案)`。
- **`AddHandler ItemInstall.Click` 走宽松委托闭包**：`AddressOf ModpackInstall` 与 Click 的签名不一致，VB 生成 `_Lambda$__R4-8` 包装方法，IL 里是 `ldftn 闭包方法` 而非 `ldftn ModpackInstall`，不能按后者定位。改为锚定对象初始化器收尾的 `stloc`——2.99.0.0 上 `Children.Add` 本来就紧跟在这条 `stloc` 之后，插入位置与原版逐指令一致（已在 414 上做过"删除-重插"回环验证）。
- **`CurrentState` 的比较**：`CurrentState = BtnLaunchState` 先 `ldloc` 再 `ldarg.0; ldfld`，且各分支 `stloc` 与合并点之间隔着 `br.s`，定位时要给窗口。
- **`PageSelectRight` 有两处形状相同的 Collapsed**：`If ShowHidden` 分支里那处是有意保留的，只能改 Else 分支里受限的那处，靠"取第二处"区分，并断言总数恰好为 2。
- **`PageSelectLeft` 的 Add 回写**：`UIElementCollection.Add` 返回 `int`，VB 以语句调用时会跟一个 `pop`，克隆时按源位置是否有 `pop` 决定。
- **`PageDownloadLeft..ctor` 的 `stfld PageID` 有两条**：字段初始化器（`DownloadInstall`）在前、限制赋值（`DownloadMod`）在后，只能 nop 最后一条。
- **`FrmSelectLeft` 声明在 `ModMain` 上**而非 `PageSelectLeft`，克隆时直接复用源位置的真实操作数，不要按声明类型解析。

新增补丁按官方 2.99.0.1 源码逐个核对过编译产物形状，并把真机 414 的 IL 人工改造成 2.99.0.1 形状（模拟 DCE 与常量折叠）做过回环验证：补丁 8 重插的 Add 与原版逐指令一致、补丁 9/11 翻转位置正确；全部带结构断言与自动跳过。尚未在真机 415/416 上完整验证。
