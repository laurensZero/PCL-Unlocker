// PCL 下载功能解锁器
//
// 恢复 PCL 2.99 系列里被 `RestrictMcDownload` 常量屏蔽、隐藏的 MC 下载功能。
//
// 原理：RestrictMcDownload 在源码里是 Public Const Boolean = True，
// VB 编译器会在编译期内联常量并把不可达分支整段删除（DCE）。
// 所以这不是"运行时开关"，而是代码本身没有生成。本工具把这些被删掉的 IL
// 重新写回，并把 2.99.0.1 新增的几处界面隐藏改回来。
//
// 支持版本（运行时自动检测 ModBase.VersionCode，见 DetectVersion）：
//   2.99.0.0（413 正式版 / 414 快照版）
//   2.99.0.1（415 正式版 / 416 快照版）
//
// 本仓库只包含下载相关补丁。构建脚本会把 src/ 下所有 .cs 一起编译，
// 私有补丁（例如联机恢复）放一个 src/LinkPatches.cs 定义
// PclUnlocker.LinkPatches.Run() 即可，运行时会被自动发现并调用。
//
// 原文件不会被修改，只输出一个新的 exe。

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                Console.WriteLine("目标文件可能不是受支持的 PCL 2.99 版本（2.99.0.0 / 2.99.0.1），或已经被其他工具修改过。");
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
            Console.WriteLine("PCL 下载功能解锁器");
            Console.WriteLine();
            Console.WriteLine("支持: PCL 2.99.0.0 (413/414) 与 2.99.0.1 (415/416)，版本自动检测");
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

    internal static partial class Patcher
    {
        // PCL 自己的日志里带的源码路径，写 Logger.Info 时保持一致
        private const string SourceFile =
            @"E:\Projects\PCL\本体代码\Plain Craft Launcher 2\Modules\Minecraft\ModModpack.vb";

        internal static ModuleDefMD _module;

        // 检测到的版本：VersionCode 字面值（413/414 = 2.99.0.0，415/416 = 2.99.0.1），0 = 未知
        internal static int DetectedVersion;
        internal static string DetectedName = "";

        private static int _reportIndex;

        internal static void Report(string msg)
        {
            _reportIndex++;
            Console.WriteLine("[" + _reportIndex.ToString("00") + "] " + msg);
        }

        internal static void Skip(string msg)
        {
            Console.WriteLine("[跳过] " + msg);
        }

        public static void Run(string src, string dst)
        {
            _module = ModuleDefMD.Load(src);

            DetectVersion();

            // ---------- 2.99.0.0 / 2.99.0.1 共有的下载恢复 ----------
            PatchDownloadPage();
            PatchModpackDispatch();
            PatchModpackEntry();
            PatchModifyCard();
            PatchResourceDetailInstall();

            // ---------- 2.99.0.1 新增的限制点（>= 415 才需要） ----------
            // 版本未知时也尝试，各补丁内部找不到目标会自动跳过
            if (DetectedVersion == 0 || DetectedVersion >= 415)
            {
                PatchDownloadLeft();
                PatchResourcePackButton();
                PatchSelectLeftImport();
                PatchLaunchButtonState();
                PatchModDisabledButton();
                PatchEmptyDownloadButton();
            }
            else
            {
                Skip("2.99.0.1 专属补丁不适用于 " + DetectedName);
            }

            // MC 更新提示的“下载”按钮：2.99.0.1 特有，按模式匹配，任何版本都可安全尝试
            PatchUpdateHintDownload();

            // 组件加载器 Output.Value 空引用保护：从 MC 更新弹窗直接进入下载页时，
            // 主版本列表刚完成就会进入选择页，而 OptiFine 等加载器可能还处于
            // Finished 但 Value 尚未赋值的瞬间。两个版本都有这个竞态。
            PatchLoaderOutputGuards();

            // 私有扩展补丁（如联机恢复），存在 src/LinkPatches.cs 时自动接入
            RunLocalExtensions();

            _module.Write(dst);
        }

        // ---------------------------------------------------------------- 版本检测

        // ModBase.VersionCode / VersionBaseName 是 VB 的 Public Const，
        // 编译期内联，但在元数据里仍保留为 literal 字段（与 RestrictMcDownload 同机制），
        // 读 Constant 表即可拿到真实版本号。
        private static void DetectVersion()
        {
            TypeDef modbase = _module.GetTypes().FirstOrDefault(t => t.Name == "ModBase");
            if (modbase == null)
            {
                Console.WriteLine("[版本] 未找到 ModBase，无法识别版本，将尝试全部补丁（不匹配处自动跳过）");
                return;
            }

            FieldDef fCode = null, fName = null;
            foreach (FieldDef f in modbase.Fields)
            {
                if (!f.HasConstant) continue;
                if (f.Name == "VersionCode") fCode = f;
                else if (f.Name == "VersionBaseName") fName = f;
            }

            if (fCode != null)
            {
                DetectedVersion = Convert.ToInt32(fCode.Constant.Value);
                string name = fName != null ? fName.Constant.Value as string : null;
                DetectedName = name ?? (DetectedVersion >= 415 ? "2.99.0.1" : "2.99.0.0");
                Console.WriteLine("[版本] 检测到 PCL " + DetectedName + " (VersionCode " + DetectedVersion + ")");
            }
            else
            {
                string name = fName != null ? fName.Constant.Value as string : null;
                if (name != null)
                {
                    DetectedName = name;
                    Console.WriteLine("[版本] 检测到 PCL " + name + "（未读到 VersionCode，按 2.99.0.1+ 处理）");
                    DetectedVersion = 0;
                }
                else
                {
                    Console.WriteLine("[版本] 未能识别版本，将尝试全部补丁（不匹配处自动跳过）");
                }
            }
        }

        // ---------------------------------------------------------------- 扩展点

        // 私有补丁文件（src/LinkPatches.cs）不进 git，编译进来后由这里反射调用。
        // 公开源码不直接引用它，保证没有该文件也能编译。
        private static void RunLocalExtensions()
        {
            try
            {
                Type ext = Assembly.GetExecutingAssembly().GetType("PclUnlocker.LinkPatches", false);
                MethodInfo run = ext != null ? ext.GetMethod("Run", BindingFlags.Public | BindingFlags.Static) : null;
                if (run == null)
                    return;
                run.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[跳过] 本地扩展执行失败: " + (ex.InnerException ?? ex).Message);
            }
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

        // 找到整个程序集里包含指定字符串常量的方法
        private static MethodDef FindMethodContainingString(string text)
        {
            foreach (TypeDef t in _module.GetTypes())
            {
                foreach (MethodDef m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (Instruction i in m.Body.Instructions)
                    {
                        if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == text)
                            return m;
                    }
                }
            }
            return null;
        }

        // 在指定类型里找包含指定字符串的方法。像“无可用版本”这种文案在
        // PageDownloadModify 里也出现过，全局搜会命中错误的方法，必须限定类型。
        private static MethodDef FindMethodContainingString(TypeDef type, string text)
        {
            foreach (MethodDef m in type.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (i.OpCode == OpCodes.Ldstr && (string)i.Operand == text)
                        return m;
                }
            }
            return null;
        }

        // “控件.Visibility = X” 的 IL 形状是 [取控件][常量][set_Visibility]。
        // 取控件可能是 ldfld（普通字段）或 call get_XXX（VB WithEvents 生成的属性）。
        private static string LoadedControlName(IList<Instruction> ins, int i)
        {
            if (i - 1 < 0) return null;
            Instruction p = ins[i - 1];
            if (p.OpCode == OpCodes.Ldfld)
            {
                FieldDef f = p.Operand as FieldDef;
                return f != null ? f.Name : null;
            }
            if (p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt)
            {
                IMethod m = p.Operand as IMethod;
                if (m == null) return null;
                // IMethod.Name 是 UTF8String，与 null 比较会命中歧义运算符，先转 string
                string n = ReferenceEquals(m.Name, null) ? null : m.Name.ToString();
                if (n != null && n.StartsWith("get_"))
                    return n.Substring(4);
            }
            return null;
        }

        private static bool IsVisibilitySetter(Instruction ins)
        {
            IMethod m = ins.Operand as IMethod;
            return (ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call)
                   && m != null && m.Name == "set_Visibility";
        }

        private static bool IsStloc(Instruction i)
        {
            return i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Stloc_S
                || i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Stloc_1
                || i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Stloc_3;
        }

        private static bool IsLdloc(Instruction i)
        {
            return i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloc_S
                || i.OpCode == OpCodes.Ldloc_0 || i.OpCode == OpCodes.Ldloc_1
                || i.OpCode == OpCodes.Ldloc_2 || i.OpCode == OpCodes.Ldloc_3;
        }

        // 取 stloc/ldloc 对应的局部变量槽位，找不到返回 -1
        private static int SlotOf(CilBody body, Instruction i)
        {
            if (i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Ldloc_0) return 0;
            if (i.OpCode == OpCodes.Stloc_1 || i.OpCode == OpCodes.Ldloc_1) return 1;
            if (i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Ldloc_2) return 2;
            if (i.OpCode == OpCodes.Stloc_3 || i.OpCode == OpCodes.Ldloc_3) return 3;
            if ((i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Stloc_S
                 || i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloc_S))
            {
                Local l = i.Operand as Local;
                return l != null ? body.Variables.IndexOf(l) : -1;
            }
            return -1;
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
                Skip("下载页 LoaderInit 已被修改过");
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

            Report("下载页 LoaderInit -> DlClientListLoader");
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
                Skip("整合包安装分派已恢复");
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

            Report("整合包安装分派已恢复");
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
                Skip("整合包文件选择入口已恢复");
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
            Report("整合包文件选择入口已恢复");
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
                    Report("版本修改页 CardMinecraft -> Visible");
                    return;
                }
            }

            Skip("版本修改页未找到目标");
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
                Skip("整合包详情页安装按钮已恢复");
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
                Skip("整合包详情页未找到目标调用");
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
            Report("整合包详情页“点版本行一键安装”已恢复");
        }

        // ---------------------------------------------------------------- 6
        // [2.99.0.1+] 下载页左侧入口：2.99.0.1 新增了 PageDownloadLeft 的构造函数，把
        // “安装”与“社区资源”两个导航项硬编码成 Collapsed，并强制落在 Mod 页：
        //
        //   ItemInstall.Visibility  = Visibility.Collapsed
        //   LabResource.Visibility  = Visibility.Collapsed
        //   PageID = FormMain.PageSubType.DownloadMod
        //   ItemMod.Checked = True
        //
        // 把两处 Visibility 改回 Visible(0)，nop 掉 PageID 赋值（保留字段初始化的
        // DownloadInstall）与 ItemMod.Checked = True（避免强制切页），行为回到 2.99.0.0。
        private static void PatchDownloadLeft()
        {
            TypeDef type = Type("PageDownloadLeft");
            MethodDef ctor = type.FindMethod(".ctor");
            if (ctor == null || !ctor.HasBody)
            {
                Skip("下载页左侧入口：没有 .ctor");
                return;
            }

            IList<Instruction> ins = ctor.Body.Instructions;
            int patched = 0;
            for (int i = 1; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4_2 || !IsVisibilitySetter(ins[i + 1])) continue;
                string ctrl = LoadedControlName(ins, i);
                if (ctrl == "ItemInstall" || ctrl == "LabResource")
                {
                    ins[i].OpCode = OpCodes.Ldc_I4_0;
                    patched++;
                }
            }

            if (patched != 2)
            {
                Skip("下载页左侧入口：Visibility 目标不完整（patched=" + patched + "）");
                return;
            }

            // PageID = DownloadMod：nop 掉最后一个 stfld PageID 及其常量（前面字段初始化的保留）
            int last = -1;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Stfld)
                {
                    FieldDef f = ins[i].Operand as FieldDef;
                    if (f != null && f.Name == "PageID")
                        last = i;
                }
            }
            if (last > 0 && ins[last - 1].OpCode.Name.StartsWith("ldc.i4"))
            {
                ins[last - 1].OpCode = OpCodes.Nop;
                ins[last - 1].Operand = null;
                ins[last].OpCode = OpCodes.Nop;
                ins[last].Operand = null;
            }

            // ItemMod.Checked = True：nop 掉常量与 setter，避免强制切到 Mod 页
            for (int i = 1; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4_1) continue;
                IMethod setter = ins[i + 1].Operand as IMethod;
                if (setter == null || setter.Name != "set_Checked") continue;
                if (LoadedControlName(ins, i) != "ItemMod") continue;
                ins[i].OpCode = OpCodes.Nop;
                ins[i].Operand = null;
                ins[i + 1].OpCode = OpCodes.Nop;
                ins[i + 1].Operand = null;
                break;
            }

            Report("下载页左侧“安装/社区资源”入口 -> Visible（默认落回安装页）");
        }

        // ---------------------------------------------------------------- 7
        // [2.99.0.1+] 资源页：set_PageType 里“安装已有整合包”按钮的显示条件被改成
        //   If(Value = ModPack AndAlso Not RestrictMcDownload, Visible, Collapsed)
        // 常量折叠后恒为 Collapsed：[取控件][ldc.i4.2][set_Visibility]。
        // 2.99.0.0 是运行时分支，不存在该形状，因此匹配不到就跳过。
        private static void PatchResourcePackButton()
        {
            TypeDef type = Type("PageResource");
            MethodDef setter = type.Methods.FirstOrDefault(x => x.Name == "set_PageType");
            if (setter == null || !setter.HasBody)
            {
                Skip("资源页“安装已有整合包”：找不到 set_PageType");
                return;
            }

            IList<Instruction> ins = setter.Body.Instructions;
            for (int i = 1; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4_2 || !IsVisibilitySetter(ins[i + 1])) continue;
                if (LoadedControlName(ins, i) != "BtnSearchInstallModPack") continue;
                ins[i].OpCode = OpCodes.Ldc_I4_0;
                Report("资源页“安装已有整合包”按钮 -> Visible");
                return;
            }

            Skip("资源页“安装已有整合包”：未找到目标（2.99.0.0 无需此补丁，或已修改过）");
        }

        // ---------------------------------------------------------------- 8
        // [2.99.0.1+] 版本选择页：构建列表的代码里“导入整合包”项的
        //   FrmSelectLeft.PanList.Children.Add(ItemInstall)
        // 被 DCE 删掉了，只剩构造与 AddHandler。项本体还在，把 Add 调用写回去。
        //
        // 真实 IL 形状（414 实测）：对象初始化器以一条 stloc 结束（把构造完的
        // ItemInstall 存入局部变量），2.99.0.0 上 Add 调用就紧跟在这条 stloc 之后；
        // AddHandler 走编译器生成的宽松委托闭包（_Lambda$__R4-8），不能按
        // ldftn ModpackInstall 定位。这里在 stloc 之后插回克隆的 Add 调用。
        private static void PatchSelectLeftImport()
        {
            MethodDef m = FindMethodContainingString(Type("PageSelectLeft"), "导入整合包");
            if (m == null || !m.HasBody)
            {
                Skip("版本选择页“导入整合包”：未找到目标方法");
                return;
            }
            IList<Instruction> ins = m.Body.Instructions;

            int idx = -1;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Ldstr && (string)ins[i].Operand == "导入整合包")
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                Skip("版本选择页“导入整合包”：未找到目标字符串");
                return;
            }

            // 对象初始化器的收尾：ldstr 之后第一个 stloc 把 ItemInstall 存进局部变量
            int stlocIdx = -1;
            for (int i = idx + 1; i < ins.Count; i++)
            {
                if (IsStloc(ins[i]))
                {
                    stlocIdx = i;
                    break;
                }
            }
            if (stlocIdx < 0 || stlocIdx + 1 >= ins.Count)
            {
                Skip("版本选择页“导入整合包”：找不到 ItemInstall 的存储点");
                return;
            }

            // 2.99.0.0 上 Add 调用本来就紧跟在 stloc 之后；检测到即无需处理
            FieldDef nextField = ins[stlocIdx + 1].Operand as FieldDef;
            if (ins[stlocIdx + 1].OpCode == OpCodes.Ldsfld && nextField != null && nextField.Name == "FrmSelectLeft")
            {
                Skip("版本选择页“导入整合包”：Add 调用已存在（2.99.0.0 无需此补丁）");
                return;
            }

            // 克隆前面其他项（ItemAdd）的 Add 调用：
            //   [ldsfld FrmSelectLeft][get_PanList][get_Children][ldloc V][Add][pop?]
            int addIdx = -1;
            for (int i = idx; i >= 0; i--)
            {
                if (ins[i].OpCode != OpCodes.Callvirt) continue;
                MemberRef mr = ins[i].Operand as MemberRef;
                if (mr != null && mr.Name == "Add"
                    && mr.DeclaringType.FullName.Contains("UIElementCollection"))
                {
                    addIdx = i;
                    break;
                }
            }
            if (addIdx < 4)
            {
                Skip("版本选择页“导入整合包”：找不到可克隆的 Children.Add 调用");
                return;
            }

            FieldDef frm = ins[addIdx - 4].Operand as FieldDef;
            IMethod getPan = ins[addIdx - 3].Operand as IMethod;
            IMethod getChildren = ins[addIdx - 2].Operand as IMethod;
            if (frm == null || frm.Name != "FrmSelectLeft"
                || getPan == null || getPan.Name != "get_PanList"
                || getChildren == null || getChildren.Name != "get_Children")
            {
                Skip("版本选择页“导入整合包”：Add 调用结构不符合预期");
                return;
            }
            bool hasPop = addIdx + 1 < ins.Count && ins[addIdx + 1].OpCode == OpCodes.Pop;

            // 与 stloc 同槽位的 ldloc
            Instruction stlocIns = ins[stlocIdx];
            Instruction ldlocItem;
            if (stlocIns.OpCode == OpCodes.Stloc_0) ldlocItem = Instruction.Create(OpCodes.Ldloc_0);
            else if (stlocIns.OpCode == OpCodes.Stloc_1) ldlocItem = Instruction.Create(OpCodes.Ldloc_1);
            else if (stlocIns.OpCode == OpCodes.Stloc_2) ldlocItem = Instruction.Create(OpCodes.Ldloc_2);
            else if (stlocIns.OpCode == OpCodes.Stloc_3) ldlocItem = Instruction.Create(OpCodes.Ldloc_3);
            else
            {
                Local local = stlocIns.Operand as Local;
                if (local == null)
                {
                    Skip("版本选择页“导入整合包”：stloc 无局部变量");
                    return;
                }
                ldlocItem = Instruction.Create(OpCodes.Ldloc_S, local);
            }

            var block = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, frm),
                Instruction.Create(OpCodes.Callvirt, getPan),
                Instruction.Create(OpCodes.Callvirt, getChildren),
                ldlocItem,
                Instruction.Create(OpCodes.Callvirt, (IMethod)ins[addIdx].Operand)  // Children.Add
            };
            if (hasPop)
                block.Add(Instruction.Create(OpCodes.Pop));

            for (int k = 0; k < block.Count; k++)
                ins.Insert(stlocIdx + 1 + k, block[k]);

            Report("版本选择页“导入整合包”入口已恢复");
        }

        // ---------------------------------------------------------------- 9
        // [2.99.0.1+] 启动页：无版本时 RefreshButtonsUI 的状态判断被常量折叠成
        //   If RestrictMcDownload OrElse (...) Then CurrentState = 1   ' “下载已禁用”
        // Select Case 的 Case 2（“下载游戏”按钮）还在 IL 里，只是永远走不到。
        // 把 CurrentState = 1 的常量改成 2 即可恢复。副作用：忽略“隐藏下载”设置项，
        // 与本工具其余补丁的行为一致。
        private static void PatchLaunchButtonState()
        {
            TypeDef type = Type("PageLaunchLeft");
            MethodDef m = type.Methods.FirstOrDefault(x => x.Name == "RefreshButtonsUI");
            if (m == null || !m.HasBody)
            {
                Skip("启动按钮：找不到 RefreshButtonsUI");
                return;
            }

            IList<Instruction> ins = m.Body.Instructions;
            CilBody body = m.Body;

            // 需要 Case 2（“下载游戏”）还在
            bool hasCase2 = false;
            foreach (Instruction x in ins)
            {
                if (x.OpCode == OpCodes.Ldstr && (string)x.Operand == "下载游戏")
                {
                    hasCase2 = true;
                    break;
                }
            }
            if (!hasCase2)
            {
                Skip("启动按钮：状态 2 分支不存在，结构不同");
                return;
            }

            // 定位 CurrentState = 1：[ldc.i4.1][stloc V]（415 折叠后各分支的 stloc
            // 与合并点之间隔着 br.s），合并点处 [ldloc V] 后 3 条内比较 BtnLaunchState。
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4_1 || !IsStloc(ins[i + 1])) continue;
                int slot = SlotOf(body, ins[i + 1]);
                if (slot < 0) continue;

                bool hit = false;
                for (int j = i + 2; j < ins.Count && j <= i + 8; j++)
                {
                    if (!IsLdloc(ins[j]) || SlotOf(body, ins[j]) != slot) continue;
                    for (int k = j + 1; k < ins.Count && k <= j + 3; k++)
                    {
                        if (ins[k].OpCode == OpCodes.Ldfld)
                        {
                            FieldDef f = ins[k].Operand as FieldDef;
                            if (f != null && f.Name == "BtnLaunchState")
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                    if (hit) break;
                }
                if (!hit) continue;

                ins[i].OpCode = OpCodes.Ldc_I4_2;
                Report("启动按钮：无版本时恢复“下载游戏”状态");
                return;
            }

            Skip("启动按钮：未找到 CurrentState 赋值点（已修改过？）");
        }

        // ---------------------------------------------------------------- 10
        // [2.99.0.1+] Mod 禁用页：BtnDownload_Loaded 的可见性条件
        //   If(RestrictMcDownload OrElse (...), Collapsed, Visible)
        // 折叠成 NewVisibility = 2 常量。改回 0。
        private static void PatchModDisabledButton()
        {
            TypeDef type = Type("PageInstanceModDisabled");
            MethodDef m = type.Methods.FirstOrDefault(x => x.Name == "BtnDownload_Loaded");
            if (m == null || !m.HasBody)
            {
                Skip("Mod 禁用页：找不到 BtnDownload_Loaded");
                return;
            }

            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldc_I4_2 || !IsStloc(ins[i + 1])) continue;

                // 后面紧跟对 BtnDownload.Visibility 的比较
                bool sees = false;
                for (int j = i + 2; j < ins.Count && j <= i + 6; j++)
                {
                    IMethod mm = ins[j].Operand as IMethod;
                    if (ins[j].OpCode == OpCodes.Callvirt && mm != null && mm.Name == "get_Visibility")
                    {
                        sees = true;
                        break;
                    }
                }
                if (!sees) continue;

                ins[i].OpCode = OpCodes.Ldc_I4_0;
                Report("Mod 禁用页“下载 Mod”按钮 -> Visible");
                return;
            }

            Skip("Mod 禁用页：未找到 NewVisibility 常量（2.99.0.0 无需此补丁，或已修改过）");
        }

        // ---------------------------------------------------------------- 11
        // [2.99.0.1+] 版本列表空状态：受限的那处赋值
        //   BtnEmptyDownload.Visibility = If(RestrictMcDownload OrElse (...), Collapsed, Visible)
        // 折叠成常量。方法里还有一处 ShowHidden 分支的 Collapsed（有意保留），
        // 两者 IL 形状相同，只改第二处（Else 分支）。
        private static void PatchEmptyDownloadButton()
        {
            MethodDef m = FindMethodContainingString(Type("PageSelectRight"), "无可用版本");
            if (m == null || !m.HasBody)
            {
                Skip("空状态下载按钮：未找到目标方法");
                return;
            }

            IList<Instruction> ins = m.Body.Instructions;
            var hits = new List<int>();
            for (int i = 1; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Ldc_I4_2 && IsVisibilitySetter(ins[i + 1])
                    && LoadedControlName(ins, i) == "BtnEmptyDownload")
                    hits.Add(i);
            }

            if (hits.Count != 2)
            {
                Skip("空状态下载按钮：目标数量异常（" + hits.Count + "，应为 2）");
                return;
            }

            ins[hits[1]].OpCode = OpCodes.Ldc_I4_0;
            Report("版本列表空状态“下载游戏”按钮 -> Visible");
        }

        // ---------------------------------------------------------------- 12
        // MC 更新提示：2.99.0.1 把弹窗第二个按钮的文本改成了
        //   If(RestrictMcDownload, "", "下载")
        // 折叠后 ldstr ""，按钮被隐藏。结果处理代码（MsgResult = 2 -> 跳转下载页）完好，
        // 把 "" 换回 "下载" 即可。2.99.0.0 编译出来本来就是 "下载"，匹配不到自动跳过。
        private static void PatchUpdateHintDownload()
        {
            MethodDef m = FindMethodContainingString(Type("ModMinecraft"), "Minecraft 更新提示");
            if (m == null || !m.HasBody)
            {
                Skip("MC 更新提示：未找到目标方法");
                return;
            }

            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 1 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldstr) continue;
                if ((string)ins[i].Operand != "确定") continue;

                if (ins[i + 1].OpCode == OpCodes.Ldstr && (string)ins[i + 1].Operand == "")
                {
                    ins[i + 1].Operand = "下载";
                    Report("MC 更新提示弹窗恢复“下载”按钮");
                    return;
                }
                if (ins[i + 1].OpCode == OpCodes.Ldstr && (string)ins[i + 1].Operand == "下载")
                {
                    Skip("MC 更新提示弹窗无需修改");
                    return;
                }
            }

            Skip("MC 更新提示：未找到按钮文本");
        }

        // ---------------------------------------------------------------- 13
        // 下载页组件加载器的 Output.Value 空引用保护。
        //
        // 从 MC 更新弹窗点“下载”时，VersionWaitingSelect 会立刻触发
        // TrySelectWaitingVersion -> EnterSelectPage -> ReloadSelected。
        // 此时 DlClientListLoader 已完成，但 OptiFine/Fabric/NeoForge/LiteLoader
        // 可能正处在“State 已变 Finished、Output.Value 还没赋值”的瞬间。
        // LoadXGetError 里的 Output.Value 直接解引用就会抛
        // NullReferenceException，整个 DlClientList Main 被标记为 Failed。
        //
        // 在这些方法开头加：
        //   If Loader.Output.Value Is Nothing Then Return "获取中……"
        // 加载器完成时 PCL 自己会再次调用 ReloadSelected，届时正常刷新。
        private static void PatchLoaderOutputGuards()
        {
            int patched = 0;
            patched += PatchOutputGuard("LoadOptiFineGetError", "DlOptiFineListLoader") ? 1 : 0;
            patched += PatchOutputGuard("LoadFabricGetError", "DlFabricListLoader") ? 1 : 0;
            patched += PatchOutputGuard("LoadNeoForgeGetError", "DlNeoForgeListLoader") ? 1 : 0;
            patched += PatchOutputGuard("LoadLiteLoaderGetError", "DlLiteLoaderListLoader") ? 1 : 0;

            if (patched > 0)
                Report("下载页组件加载器空输出保护（" + patched + " 处）");
            else
                Skip("下载页组件加载器空输出保护已存在");
        }

        private static bool PatchOutputGuard(string methodName, string loaderFieldName)
        {
            MethodDef m = Type("PageDownloadInstall").Methods.FirstOrDefault(x => x.Name == methodName);
            if (m == null || !m.HasBody)
                return false;

            IList<Instruction> ins = m.Body.Instructions;

            // 已经加过：入口就是 ldsfld loader -> ldflda Output -> ldfld Value -> brtrue
            IField firstField = ins.Count > 0 ? ins[0].Operand as IField : null;
            if (ins.Count >= 4
                && ins[0].OpCode == OpCodes.Ldsfld
                && firstField != null && firstField.Name == loaderFieldName
                && ins[1].OpCode == OpCodes.Ldflda
                && ins[2].OpCode == OpCodes.Ldfld
                && ins[3].OpCode == OpCodes.Brtrue)
            {
                return false;
            }

            // 找 Loader.Output.Value 的真实 IL 形状，直接复用它的字段引用
            int pat = -1;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Ldsfld) continue;
                IField lf = ins[i].Operand as IField;
                if (lf == null || lf.Name != loaderFieldName) continue;
                if (ins[i + 1].OpCode != OpCodes.Ldflda) continue;
                IField of = ins[i + 1].Operand as IField;
                if (of == null || of.Name != "Output") continue;
                IField vf = ins[i + 2].Operand as IField;
                if (ins[i + 2].OpCode != OpCodes.Ldfld || vf == null || vf.Name != "Value") continue;
                pat = i;
                break;
            }

            if (pat < 0)
                return false;

            Instruction originalStart = ins[0];
            var guard = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, (IField)ins[pat].Operand),
                Instruction.Create(OpCodes.Ldflda, (IField)ins[pat + 1].Operand),
                Instruction.Create(OpCodes.Ldfld, (IField)ins[pat + 2].Operand),
                Instruction.Create(OpCodes.Brtrue, originalStart),
                Instruction.Create(OpCodes.Ldstr, "获取中……"),
                Instruction.Create(OpCodes.Ret)
            };

            for (int k = 0; k < guard.Count; k++)
                ins.Insert(k, guard[k]);

            m.Body.MaxStack = 8;
            return true;
        }
    }
}
