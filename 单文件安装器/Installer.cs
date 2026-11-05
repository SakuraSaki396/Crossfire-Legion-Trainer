using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace TrainerSetup
{
    internal struct OpResult
    {
        public bool Ok;
        public string Message;

        public static OpResult Success(string m) { return new OpResult { Ok = true, Message = m }; }
        public static OpResult Fail(string m) { return new OpResult { Ok = false, Message = m }; }
    }

    internal struct InstallStatus
    {
        public string State;    // "installed" | "partial" | "missing" | "nogame"
        public string Detail;
        public string DetailCn
        {
            get
            {
                switch (State)
                {
                    case "installed": return "已安装 · 直接启动游戏即可";
                    case "partial": return "安装不完整 · 建议重新点“安装 / 修复”";
                    case "missing": return "未安装";
                    default: return "未检测到游戏";
                }
            }
        }
    }

    internal static class Installer
    {
        private const string LuaRel = @"Data\Scripts\Source\Extensions\zzz_trainer.lua";
        private const string PluginRel = @"BepInEx\plugins\CrossfireTrainer.dll";

        private static readonly string[] RootFiles =
        {
            "winhttp.dll",
            "doorstop_config.ini",
            ".doorstop_version",
        };

        private static readonly string[] RequiredFiles =
        {
            "winhttp.dll",
            "doorstop_config.ini",
            @"BepInEx\core\BepInEx.Preloader.dll",
            @"BepInEx\core\0Harmony.dll",
            PluginRel,
            LuaRel,
        };

        internal static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("Crossfire_Legion").Length > 0; }
            catch { return false; }
        }

        internal static InstallStatus GetStatus(string gameDir)
        {
            if (!SteamFinder.IsGameDir(gameDir))
                return new InstallStatus { State = "nogame", Detail = "not a game folder" };

            int found = 0;
            foreach (string rel in RequiredFiles)
            {
                if (File.Exists(Path.Combine(gameDir, rel))) found++;
            }

            if (found == RequiredFiles.Length)
                return new InstallStatus { State = "installed", Detail = found + "/" + RequiredFiles.Length };
            if (found == 0)
                return new InstallStatus { State = "missing", Detail = "0/" + RequiredFiles.Length };
            return new InstallStatus { State = "partial", Detail = found + "/" + RequiredFiles.Length };
        }

        internal static OpResult Install(string gameDir, Action<string> log)
        {
            if (IsGameRunning())
                return OpResult.Fail("游戏正在运行，请先完全退出游戏（包括后台进程）再安装。");

            if (!SteamFinder.IsGameDir(gameDir))
                return OpResult.Fail("这个目录里没有找到 Crossfire_Legion.exe，请选择包含游戏的文件夹。");

            try
            {
                byte[] payload = ReadPayload();
                log("安装到: " + gameDir);

                int count = 0;
                using (var ms = new MemoryStream(payload))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string target = Path.Combine(gameDir, rel);

                        string parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                            Directory.CreateDirectory(parent);

                        if (File.Exists(target))
                        {
                            try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                        }

                        entry.ExtractToFile(target, true);
                        count++;
                    }
                }

                log("已释放 " + count + " 个文件");

                // v1.6.0 and older dropped a .txt note next to the game exe; the note lives
                // inside this program now, so drop the leftover if an older build made one.
                try
                {
                    string legacy = Path.Combine(gameDir, GameVersion.LegacyDocFile);
                    if (File.Exists(legacy))
                    {
                        File.SetAttributes(legacy, FileAttributes.Normal);
                        File.Delete(legacy);
                        log("清理旧版说明文件: " + GameVersion.LegacyDocFile);
                    }
                }
                catch (Exception e) { log("旧说明文件清理失败（可忽略）: " + e.Message); }

                var st = GetStatus(gameDir);
                if (st.State != "installed")
                {
                    log("校验失败: " + st.Detail);
                    return OpResult.Fail("安装后校验未通过（" + st.Detail + "），可能是杀毒软件拦截了文件写入。");
                }

                log("校验通过: " + st.Detail);

                var probe = GameVersion.Probe(gameDir);
                log("游戏版本: " + (probe.ProductVersion.Length > 0 ? probe.ProductVersion : "未读到")
                    + (probe.BuildId.Length > 0 ? "  / build " + probe.BuildId : ""));
                log("适配状态: " + probe.VerdictCode + " | " + probe.VerdictCn);
                log("（适配说明内置于本程序，随时可点“适配说明…”查看）");

                string msg = "修改器已安装到游戏目录。直接从 Steam 启动游戏，进入对局后按 F1 打开界面。";
                if (!probe.VersionExact)
                    msg += Environment.NewLine + Environment.NewLine
                        + "注意：本修改器是按 " + GameVersion.VerifiedProduct + "（build " + GameVersion.VerifiedBuildId
                        + "）验证的，当前游戏版本不同。" + Environment.NewLine
                        + "多数功能仍可用；若某项无效，点本窗口里的「适配说明…」可以看到是哪一项、为什么失效。";
                return OpResult.Success(msg);
            }
            catch (UnauthorizedAccessException)
            {
                return OpResult.Fail("没有写入权限。请把安装器右键“以管理员身份运行”后重试。");
            }
            catch (Exception e)
            {
                log("异常: " + e);
                return OpResult.Fail("安装失败: " + e.Message);
            }
        }

        internal static OpResult Uninstall(string gameDir, Action<string> log)
        {
            if (IsGameRunning())
                return OpResult.Fail("游戏正在运行，请先退出游戏再卸载。");

            if (!Directory.Exists(gameDir))
                return OpResult.Fail("目录不存在。");

            int removed = 0;

            foreach (string rel in RootFiles)
            {
                try
                {
                    string p = Path.Combine(gameDir, rel);
                    if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); removed++; log("删除 " + rel); }
                }
                catch (Exception e) { log("跳过 " + rel + ": " + e.Message); }
            }

            // older builds dropped a .txt note next to the game exe; the note now lives in
            // this program, so just clean the leftover up if it is still there.
            try
            {
                string doc = Path.Combine(gameDir, GameVersion.LegacyDocFile);
                if (File.Exists(doc)) { File.SetAttributes(doc, FileAttributes.Normal); File.Delete(doc); removed++; log("删除 " + GameVersion.LegacyDocFile); }
            }
            catch (Exception e) { log("跳过旧说明文件: " + e.Message); }

            try
            {
                string lua = Path.Combine(gameDir, LuaRel);
                if (File.Exists(lua)) { File.Delete(lua); removed++; log("删除 " + LuaRel); }
            }
            catch (Exception e) { log("跳过 lua: " + e.Message); }

            try
            {
                string bep = Path.Combine(gameDir, "BepInEx");
                if (Directory.Exists(bep))
                {
                    Directory.Delete(bep, true);
                    removed++;
                    log("删除 BepInEx 目录");
                }
            }
            catch (Exception e) { log("跳过 BepInEx: " + e.Message); }

            if (removed == 0)
                return OpResult.Fail("这个目录里没有找到修改器文件，可能已经被 Steam 随游戏一起删除了。");

            log("卸载完成，共移除 " + removed + " 项");
            return OpResult.Success("已卸载。游戏目录已恢复原状，可直接从 Steam 启动原版游戏。");
        }

        internal static OpResult Launch(string gameDir)
        {
            if (!SteamFinder.IsGameDir(gameDir))
                return OpResult.Fail("目录无效。");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(gameDir, SteamFinder.ExeName),
                    WorkingDirectory = gameDir,
                    UseShellExecute = true,
                });
                return OpResult.Success("游戏已启动。");
            }
            catch (Exception e)
            {
                return OpResult.Fail("启动失败: " + e.Message);
            }
        }

        private static byte[] ReadPayload()
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase)) continue;
                using (var s = asm.GetManifestResourceStream(name))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            throw new InvalidOperationException("安装包资源缺失（payload.zip）");
        }

        internal static IEnumerable<string> RequiredFileList { get { return RequiredFiles; } }
    }
}
