// PCL 2.99 Unlocker
//
// 把 PCL 2.99.0.0 正式版里被 `RestrictMcDownload` 常量屏蔽掉的下载功能补回来。
//
// 原理：RestrictMcDownload 在源码里是 Public Const Boolean = True，
// VB 编译器会在编译期内联常量并把不可达分支整段删除（DCE）。
// 所以这不是"运行时开关"，而是代码本身没有生成。本工具按官方源码
// （commit e4b0489）把这些被删掉的 IL 重新写回，共 5 处。
//
// 原文件不会被修改，只输出一个新的 exe。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclUnlocker
{
    internal static class Program
    {
        private const string DefaultSource = "Plain Craft Launcher 2.exe";
        private const string DefaultOutput = "Plain Craft Launcher 2.unlocked.exe";

        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDnlib;

            if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
            {
                PrintUsage();
                return 0;
            }

            string src = args.Length > 0 ? args[0] : DefaultSource;
            string dst = args.Length > 1 ? args[1] : DefaultOutput;

            if (!File.Exists(src))
            {
                Console.WriteLine("找不到输入文件: " + Path.GetFullPath(src));
                PrintUsage();
                return 2;
            }

            try
            {
                Console.WriteLine("输入: " + Path.GetFullPath(src));
                Patcher.Run(src, dst);
                Console.WriteLine("输出: " + Path.GetFullPath(dst));
                Console.WriteLine("完成。");
                return 0;
            }
            catch (PatchException ex)
            {
                Console.WriteLine("[失败] " + ex.Message);
                Console.WriteLine("目标文件可能不是 PCL 2.99.0.0 (414)，或已经被其他工具修改过。");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[失败] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("PCL 2.99 Unlocker");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  PCLUnlock.exe [输入.exe] [输出.exe]");
            Console.WriteLine();
            Console.WriteLine("不带参数时:");
            Console.WriteLine("  输入 = " + DefaultSource);
            Console.WriteLine("  输出 = " + DefaultOutput);
        }

        // dnlib 以资源形式内嵌，保证 PCLUnlock.exe 是单文件
        private static Assembly ResolveDnlib(object sender, ResolveEventArgs e)
        {
            if (new AssemblyName(e.Name).Name != "dnlib")
                return null;

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dnlib.dll"))
            {
                if (s == null)
                    return null;

                var buffer = new byte[s.Length];
                int read = 0;
                while (read < buffer.Length)
                    read += s.Read(buffer, read, buffer.Length - read);
                return Assembly.Load(buffer);
            }
        }
    }

    internal sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    internal static class Patcher
    {
        // PCL 自己的日志里带的源码路径，写 Logger.Info 时保持一致
        private const string SourceFile =
            @"E:\Projects\PCL\本体代码\Plain Craft Launcher 2\Modules\Minecraft\ModModpack.vb";

        private static ModuleDefMD _module;

        public static void Run(string src, string dst)
        {
            _module = ModuleDefMD.Load(src);

            PatchDownloadPage();
            PatchModpackDispatch();
            PatchModpackEntry();
            PatchModifyCard();
            PatchResourceDetailInstall();

            _module.Write(dst);
        }

        // ---------------------------------------------------------------- 工具

        private static TypeDef Type(string name)
        {
            TypeDef t = _module.GetTypes().FirstOrDefault(x => x.Name == name);
            if (t == null)
                throw new PatchException("找不到类型 " + name);
            return t;
        }

        private static MethodDef Method(TypeDef type, string name, int paramCount)
        {
            MethodDef m = type.Methods.FirstOrDefault(
                x => x.Name == name && x.Parameters.Count(p => p.IsNormalMethodParameter) == paramCount);
            if (m == null)
                throw new PatchException("找不到方法 " + type.Name + "." + name);
            return m;
        }

        private static Instruction Nop()
        {
            return Instruction.Create(OpCodes.Nop);
        }

        // ---------------------------------------------------------------- 1/5
        // 下载页：把占位的 "关闭下载" LoaderTask 换回真正的版本列表加载器。
        //
        // 原版：PageLoaderInit(..., New LoaderTask(Of String, DlClientListResult)("关闭下载", 抛异常的 lambda), ...)
        // 补丁：第 6 个参数改成 ModDownload.DlClientListLoader
        private static void PatchDownloadPage()
        {
            MethodDef m = Method(Type("PageDownloadInstall"), "LoaderInit", 0);
            IList<Instruction> ins = m.Body.Instructions;
            FieldDef loaderField = Type("ModDownload").Fields.First(f => f.Name == "DlClientListLoader");

            int idx = -1;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Ldstr && (string)ins[i].Operand == "关闭下载")
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                Console.WriteLine("[跳过] 下载页 LoaderInit 已被修改过");
                return;
            }

            // 占位 LoaderTask 的构造参数从这里开始，到真正的 OnFinish 回调之前全部作废
            ins[idx].OpCode = OpCodes.Ldsfld;
            ins[idx].Operand = loaderField;

            int j = idx + 1;
            while (j < ins.Count)
            {
                bool isCallbackStart = ins[j].OpCode == OpCodes.Ldarg_0
                                       && j + 1 < ins.Count
                                       && ins[j + 1].OpCode == OpCodes.Ldftn;
                if (isCallbackStart)
                    break;

                ins[j].OpCode = OpCodes.Nop;
                ins[j].Operand = null;
                j++;
            }

            Console.WriteLine("[1/5] 下载页 LoaderInit -> DlClientListLoader");
        }

        // ---------------------------------------------------------------- 2/5
        // 整合包安装的分派。
        //
        // 原版在识别完压缩包类型后直接 throw，后面的 Select Case 被编译器删掉：
        //   Select Case PackType
        //       Case 0 : Return InstallPackCurseForge(...)
        //       Case 1 : Return InstallPackHMCL(...)
        //       ...
        // 这里用 switch 重建，返回值复用编译器留下的返回值局部变量。
        private static void PatchModpackDispatch()
        {
            TypeDef module = Type("ModModpack");
            MethodDef m = Method(module, "ModpackInstall", 3);
            CilBody body = m.Body;
            IList<Instruction> ins = body.Instructions;

            int idx = -1;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Ldstr
                    && ((string)ins[i].Operand).Contains("为遵守相关法律法规"))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                Console.WriteLine("[跳过] 整合包安装分派已恢复");
                return;
            }

            if (ins[idx + 1].OpCode != OpCodes.Newobj || ins[idx + 2].OpCode != OpCodes.Throw)
                throw new PatchException("ModpackInstall: 限制分支结构不符合预期");

            // 复用方法里已有的 Logger.Info(string, LogBehavior, string)
            IMethod loggerInfo = ins.Select(x => x.Operand).OfType<IMethod>()
                                   .First(x => x.Name == "Info" && x.MethodSig.Params.Count == 3);

            Instruction dispatch = ins[idx];          // 所有 leave 的目标，保持对象不变
            ins.RemoveAt(idx + 2);
            ins.RemoveAt(idx + 1);
            ins.RemoveAt(idx);

            Instruction endLabel = Nop();
            Instruction case0 = Nop(), case1 = Nop(), case2 = Nop(), case3 = Nop();
            Instruction case4 = Nop(), case9 = Nop(), fallback = Nop();

            dispatch.OpCode = OpCodes.Ldloc_S;
            dispatch.Operand = body.Variables[4];     // PackType

            var block = new List<Instruction>
            {
                dispatch,
                Instruction.Create(OpCodes.Switch, new[]
                {
                    case0, case1, case2, case3, case4,
                    fallback, fallback, fallback, fallback, case9
                })
            };

            block.Add(case0); EmitCase(block, "整合包种类：CurseForge", Method(module, "InstallPackCurseForge", 5), loggerInfo, endLabel);
            block.Add(case1); EmitCase(block, "整合包种类：HMCL", Method(module, "InstallPackHMCL", 3), loggerInfo, endLabel);
            block.Add(case2); EmitCase(block, "整合包种类：MMC", Method(module, "InstallPackMMC", 3), loggerInfo, endLabel);
            block.Add(case3); EmitCase(block, "整合包种类：MCBBS", Method(module, "InstallPackMCBBS", 4), loggerInfo, endLabel);
            block.Add(case4); EmitCase(block, "整合包种类：Modrinth", Method(module, "InstallPackModrinth", 5), loggerInfo, endLabel);
            block.Add(case9); EmitCase(block, "整合包种类：带启动器的压缩包", Method(module, "InstallPackLauncherPack", 3), loggerInfo, endLabel);
            block.Add(fallback); EmitCase(block, "整合包种类：未能识别，假定为压缩包", Method(module, "InstallPackCompress", 2), loggerInfo, endLabel);

            for (int k = 0; k < block.Count; k++)
                ins.Insert(idx + k, block[k]);

            // 返回语句必须放在 finally 之后
            foreach (ExceptionHandler eh in body.ExceptionHandlers)
            {
                if (eh.HandlerEnd == null)
                    eh.HandlerEnd = endLabel;
                if (eh.TryEnd == null)
                    eh.TryEnd = endLabel;
            }

            ins.Add(endLabel);
            ins.Add(Instruction.Create(OpCodes.Ldloc_0));
            ins.Add(Instruction.Create(OpCodes.Ret));
            body.MaxStack = 8;

            Console.WriteLine("[2/5] 整合包安装分派已恢复");
        }

        // 所有 InstallPackXxx 的参数顺序都是 (File, Archive, ArchiveBaseFolder, InstanceName, Logo)，
        // 按目标方法的参数个数决定压几个参数。
        private static void EmitCase(List<Instruction> block, string logMessage, IMethod target,
                                     IMethod loggerInfo, Instruction endLabel)
        {
            block.Add(Instruction.Create(OpCodes.Ldstr, logMessage));
            block.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            block.Add(Instruction.Create(OpCodes.Ldstr, SourceFile));
            block.Add(Instruction.Create(OpCodes.Call, loggerInfo));

            int argc = target.MethodSig.Params.Count;
            block.Add(Instruction.Create(OpCodes.Ldarg_0));              // File
            block.Add(Instruction.Create(OpCodes.Ldloc_1));              // Archive
            if (argc >= 3) block.Add(Instruction.Create(OpCodes.Ldloc_2));   // ArchiveBaseFolder
            if (argc >= 4) block.Add(Instruction.Create(OpCodes.Ldarg_1));   // InstanceName
            if (argc >= 5) block.Add(Instruction.Create(OpCodes.Ldarg_2));   // Logo

            block.Add(Instruction.Create(OpCodes.Call, target));
            block.Add(Instruction.Create(OpCodes.Stloc_0));
            block.Add(Instruction.Create(OpCodes.Leave, endLabel));
        }

        // ---------------------------------------------------------------- 3/5
        // 整合包入口：选择文件 -> 塞进编译器留下的闭包 -> RunInThread。
        // 闭包 (_Closure$__0-0) 和它的 Worker 方法都还在，直接复用。
        private static void PatchModpackEntry()
        {
            TypeDef module = Type("ModModpack");
            MethodDef m = Method(module, "ModpackInstall", 0);

            if (m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn))
            {
                Console.WriteLine("[跳过] 整合包文件选择入口已恢复");
                return;
            }

            TypeDef closure = _module.GetTypes().First(t => t.FullName == "PCL.ModModpack/_Closure$__0-0");
            MethodDef ctor = closure.FindMethod(".ctor");
            MethodDef worker = closure.Methods.First(x => x.Name == "_Lambda$__0");
            FieldDef fileField = closure.Fields.First(f => f.Name == "$VB$Local_File");

            MemberRef selectFile = _module.GetMemberRefs()
                .First(mr => mr.Name == "SelectFile" && mr.MethodSig.Params.Count == 4);
            MemberRef actionCtor = _module.GetMemberRefs()
                .First(mr => mr.Name == ".ctor"
                             && mr.DeclaringType.FullName == "System.Action"
                             && mr.MethodSig.Params.Count == 2);
            MethodDef runInThread = Method(Type("ModBase"), "RunInThread", 1);

            string listName = selectFile.MethodSig.RetType.FullName;
            MemberRef getCount = _module.GetMemberRefs()
                .First(mr => mr.Name == "get_Count" && mr.DeclaringType.FullName == listName);
            MemberRef getItem = _module.GetMemberRefs()
                .First(mr => mr.Name == "get_Item" && mr.DeclaringType.FullName == listName);

            CilBody body = m.Body;
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();

            IList<Instruction> ins = body.Instructions;
            ins.Clear();

            var vClosure = new Local(closure.ToTypeSig());
            var vList = new Local(selectFile.MethodSig.RetType);
            var vFilter = new Local(selectFile.MethodSig.Params[3]);   // Nullable(Of OneOf(...))
            body.Variables.Add(vClosure);
            body.Variables.Add(vList);
            body.Variables.Add(vFilter);

            Instruction ret = Instruction.Create(OpCodes.Ret);

            ins.Add(Instruction.Create(OpCodes.Newobj, ctor));
            ins.Add(Instruction.Create(OpCodes.Stloc, vClosure));

            ins.Add(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));                 // MultiSelect = False
            ins.Add(Instruction.Create(OpCodes.Ldnull));                   // InitialDirectory
            ins.Add(Instruction.Create(OpCodes.Ldloca, vFilter));
            ins.Add(Instruction.Create(OpCodes.Initobj, vFilter.Type.ToTypeDefOrRef()));
            ins.Add(Instruction.Create(OpCodes.Ldloc, vFilter));           // Filter 留空 = 显示所有文件
            ins.Add(Instruction.Create(OpCodes.Call, selectFile));
            ins.Add(Instruction.Create(OpCodes.Stloc, vList));

            ins.Add(Instruction.Create(OpCodes.Ldloc, vList));
            ins.Add(Instruction.Create(OpCodes.Brfalse, ret));
            ins.Add(Instruction.Create(OpCodes.Ldloc, vList));
            ins.Add(Instruction.Create(OpCodes.Callvirt, getCount));
            ins.Add(Instruction.Create(OpCodes.Brfalse, ret));

            ins.Add(Instruction.Create(OpCodes.Ldloc, vClosure));
            ins.Add(Instruction.Create(OpCodes.Ldloc, vList));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            ins.Add(Instruction.Create(OpCodes.Callvirt, getItem));
            ins.Add(Instruction.Create(OpCodes.Stfld, fileField));

            ins.Add(Instruction.Create(OpCodes.Ldloc, vClosure));
            ins.Add(Instruction.Create(OpCodes.Ldftn, worker));
            ins.Add(Instruction.Create(OpCodes.Newobj, actionCtor));
            ins.Add(Instruction.Create(OpCodes.Call, runInThread));
            ins.Add(ret);

            body.MaxStack = 8;
            Console.WriteLine("[3/5] 整合包文件选择入口已恢复");
        }

        // ---------------------------------------------------------------- 4/5
        // 版本修改页：CardMinecraft.Visibility 由 Collapsed(2) 改回 Visible(0)
        private static void PatchModifyCard()
        {
            MethodDef m = Type("PageDownloadModify").Methods.First(x => x.Name == "LoadInstanceFinished");
            IList<Instruction> ins = m.Body.Instructions;

            for (int i = 0; i < ins.Count - 2; i++)
            {
                var getter = ins[i].Operand as IMethod;
                var setter = ins[i + 2].Operand as IMethod;
                if (getter != null && getter.Name == "get_CardMinecraft"
                    && ins[i + 1].OpCode == OpCodes.Ldc_I4_2
                    && setter != null && setter.Name == "set_Visibility")
                {
                    ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                    Console.WriteLine("[4/5] 版本修改页 CardMinecraft -> Visible");
                    return;
                }
            }

            Console.WriteLine("[跳过] 版本修改页未找到目标");
        }

        // ---------------------------------------------------------------- 5/5
        // 资源详情页：整合包的版本行点击 = 一键下载并安装。
        //
        // 原版源码：
        //   If Not RestrictMcDownload AndAlso TargetResourceType = ResourceTypes.ModPack Then
        //       Stack.Children.Add(Version.ToListItem(AddressOf Install_Click, AddressOf Save_Click, ...))
        //   Else
        //       Stack.Children.Add(Version.ToListItem(AddressOf Save_Click, ...))
        //   End If
        //
        // 编译后只剩 Else 分支。Install_Click 本身完好，补回条件与第一个委托即可。
        private static void PatchResourceDetailInstall()
        {
            TypeDef type = Type("PageDownloadResourceDetail");
            MethodDef m = type.Methods.First(x => x.Name == "InitializeStackResourceVersions");
            CilBody body = m.Body;
            IList<Instruction> ins = body.Instructions;

            if (ins.Any(i => i.OpCode == OpCodes.Ldftn
                             && i.Operand is IMethod && ((IMethod)i.Operand).Name == "Install_Click"))
            {
                Console.WriteLine("[跳过] 整合包详情页安装按钮已恢复");
                return;
            }

            int saveIdx = -1;
            for (int i = 0; i < ins.Count; i++)
            {
                var target = ins[i].Operand as IMethod;
                if (ins[i].OpCode == OpCodes.Ldftn && target != null && target.Name == "Save_Click")
                {
                    saveIdx = i;
                    break;
                }
            }

            if (saveIdx < 0 || saveIdx + 6 >= ins.Count)
            {
                Console.WriteLine("[跳过] 整合包详情页未找到目标调用");
                return;
            }

            if (ins[saveIdx + 1].OpCode != OpCodes.Newobj
                || ins[saveIdx + 2].OpCode != OpCodes.Ldnull
                || ins[saveIdx + 4].OpCode != OpCodes.Callvirt
                || ins[saveIdx + 6].OpCode != OpCodes.Pop)
                throw new PatchException("InitializeStackResourceVersions: ToListItem 调用结构不符合预期");

            // 向前找到 Stack.Children.Add(...) 的起点
            int start = saveIdx;
            while (start > 0 && ins[start].OpCode != OpCodes.Ldarg_1)
                start--;
            if (ins[start].OpCode != OpCodes.Ldarg_1)
                throw new PatchException("InitializeStackResourceVersions: 找不到 Children.Add 起始点");

            Instruction originalStart = ins[start];
            Instruction endLabel = ins[saveIdx + 7];

            FieldDef typeField = type.Fields.First(f => f.Name == "TargetResourceType");
            var getChildren = (IMethod)ins[start + 1].Operand;
            var listItemCtor = (IMethod)ins[saveIdx + 1].Operand;      // MyListItem.ClickEventHandler
            var toListItem = (IMethod)ins[saveIdx + 4].Operand;
            var addMethod = (IMethod)ins[saveIdx + 5].Operand;
            MethodDef installClick = type.Methods.First(x => x.Name == "Install_Click");
            MethodDef saveClick = type.Methods.First(x => x.Name == "Save_Click");
            Local version = body.Variables[7];

            IMethod iconCtor = _module.GetMemberRefs().FirstOrDefault(
                mr => mr.Name == ".ctor"
                      && mr.DeclaringType.FullName.Contains("MyIconButton")
                      && mr.DeclaringType.FullName.Contains("ClickEventHandler")
                      && mr.MethodSig.Params.Count == 2);
            if (iconCtor == null)
            {
                TypeDef iconType = _module.GetTypes()
                    .FirstOrDefault(x => x.FullName.Contains("MyIconButton/ClickEventHandler"));
                if (iconType == null)
                    throw new PatchException("找不到 MyIconButton.ClickEventHandler");
                iconCtor = iconType.FindMethod(".ctor");
            }

            var block = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, typeField),
                Instruction.Create(OpCodes.Ldc_I4_2),               // ResourceTypes.ModPack
                Instruction.Create(OpCodes.Bne_Un, originalStart),  // 非整合包 -> 原逻辑
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Callvirt, getChildren),
                Instruction.Create(OpCodes.Ldloc, version),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldftn, installClick),
                Instruction.Create(OpCodes.Newobj, listItemCtor),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldftn, saveClick),
                Instruction.Create(OpCodes.Newobj, iconCtor),
                Instruction.Create(OpCodes.Ldloc_1),                // IsBadDisplay
                Instruction.Create(OpCodes.Callvirt, toListItem),
                Instruction.Create(OpCodes.Callvirt, addMethod),
                Instruction.Create(OpCodes.Pop),
                Instruction.Create(OpCodes.Br, endLabel)
            };

            for (int k = 0; k < block.Count; k++)
                ins.Insert(start + k, block[k]);

            body.MaxStack = 8;
            Console.WriteLine("[5/5] 整合包详情页“点版本行一键安装”已恢复");
        }
    }
}
