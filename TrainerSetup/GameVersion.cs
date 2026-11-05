using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TrainerSetup
{
    /// <summary>One trainer feature and the engine assets / API symbols it needs.</summary>
    internal sealed class FeatureCheck
    {
        public string Name;
        public string Hotkey;
        public readonly List<string> Missing = new List<string>();
        public bool Ok { get { return Missing.Count == 0; } }
    }

    internal sealed class GameProbe
    {
        public string Dir = "";
        public bool GameFound;
        public string ExeFileVersion = "";
        public string ProductVersion = "";
        public string BuildId = "";
        public string BuildIdSource = "";

        public bool ProductMatch;
        public bool BuildMatch;
        public bool Layout01;                 // the files the trainer patches are where we expect
        public readonly List<string> MissingAnchors = new List<string>();
        public readonly List<FeatureCheck> Features = new List<FeatureCheck>();

        public bool VersionExact { get { return ProductMatch && BuildMatch; } }

        public List<FeatureCheck> Broken
        {
            get { return Features.FindAll(f => !f.Ok); }
        }

        public string VerdictCode
        {
            get
            {
                if (!GameFound) return "NOGAME";
                if (!Layout01) return "LAYOUT";
                if (ProductMatch && BuildMatch) return "MATCH";
                if (ProductVersion.Length > 0) return "DIFFERENT";
                return "UNKNOWN";
            }
        }

        public string VerdictCn
        {
            get
            {
                switch (VerdictCode)
                {
                    case "MATCH": return "与修改器验证过的版本完全一致";
                    case "LAYOUT": return "游戏目录结构对不上（可能不是同一个游戏或安装不完整）";
                    case "DIFFERENT": return "游戏版本与验证版本不同（大概率仍可用，但可能有个别功能失效）";
                    default: return "未能读取游戏版本";
                }
            }
        }
    }

    internal static class GameVersion
    {
        // ---- what this trainer was built and tested against ------------------
        /// <summary>
        /// Read from the assembly (which the build stamped from ..\version.txt), so the
        /// installer window, the exe's file version and the in-game panel never disagree.
        /// </summary>
        internal static readonly string Trainer = ResolveTrainerVersion();

        private static string ResolveTrainerVersion()
        {
            try
            {
                var asm = typeof(GameVersion).Assembly;
                foreach (object o in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                {
                    var m = (System.Reflection.AssemblyMetadataAttribute)o;
                    if (string.Equals(m.Key, "TrainerVersion", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(m.Value))
                        return m.Value.Trim();
                }
                var vi = FileVersionInfo.GetVersionInfo(asm.Location);
                string v = (vi.ProductVersion ?? "").Trim();
                int plus = v.IndexOf('+');
                if (plus > 0) v = v.Substring(0, plus);
                if (v.Length > 0) return v;
                if (!string.IsNullOrEmpty(vi.FileVersion))
                    return vi.FileVersion.Replace(".0", "").Trim();
            }
            catch { }
            return "0.0.0";
        }

        internal const string GameName = "Crossfire: Legion (Demo)";
        internal const string AppId = "1072190";
        internal const string VerifiedProduct = "custom-build#178338-344121-Master";
        internal const string VerifiedFileVersion = "2018.2.15f1_65e0713a5949";
        internal const string VerifiedBuildId = "13307800";
        internal const string VerifiedDate = "2026-11-05";

        // ---- paths the trainer depends on -----------------------------------
        private const string ExeName = "Crossfire_Legion.exe";
        private const string SimDll = @"Crossfire_Legion_Data\Managed\Carbon.Simulation.dll";
        private const string ApiJson = @"Data\Scripts\ScriptingAPI.json";
        private const string AttrZip = @"Data\Attributes.zip";
        private const string ExtDir = @"Data\Scripts\Source\Extensions";
        internal const string LegacyDocFile = "修改器说明.txt";

        /// <summary>Reads the game build, then checks every asset / API the trainer touches.</summary>
        internal static GameProbe Probe(string gameDir)
        {
            var p = new GameProbe { Dir = gameDir ?? "" };
            if (string.IsNullOrEmpty(gameDir) || !SteamFinder.IsGameDir(gameDir)) return p;
            p.GameFound = true;

            // 1. build strings from the executable itself
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(Path.Combine(gameDir, ExeName));
                p.ExeFileVersion = (vi.FileVersion ?? "").Trim();
                p.ProductVersion = (vi.ProductVersion ?? "").Trim();
            }
            catch { }

            // 2. Steam build id from the app manifest (two levels above the game folder)
            p.BuildId = ReadBuildId(gameDir, out p.BuildIdSource);

            p.ProductMatch = string.Equals(p.ProductVersion, VerifiedProduct, StringComparison.OrdinalIgnoreCase);
            p.BuildMatch = string.Equals(p.BuildId, VerifiedBuildId, StringComparison.Ordinal);

            // 3. layout: the files this trainer patches must exist
            if (!File.Exists(Path.Combine(gameDir, SimDll))) p.MissingAnchors.Add(SimDll);
            if (!File.Exists(Path.Combine(gameDir, AttrZip))) p.MissingAnchors.Add(AttrZip);
            if (!File.Exists(Path.Combine(gameDir, ApiJson))) p.MissingAnchors.Add(ApiJson);
            if (!Directory.Exists(Path.Combine(gameDir, ExtDir))) p.MissingAnchors.Add(ExtDir + @"\");
            p.Layout01 = p.MissingAnchors.Count == 0;

            // 4. what the current build still exports / still ships
            HashSet<string> apis = ReadApiSymbols(Path.Combine(gameDir, SimDll));
            HashSet<string> assets = ReadAssetPaths(Path.Combine(gameDir, AttrZip));

            p.Features.Add(Make("资源修改", "面板",
                new[] { "Attributes/InventoryAttributes/ia_resource1.txt", "Attributes/InventoryAttributes/ia_resource2.txt" },
                new[] { "SetInventoryValue", "GetAllAliveEntities" }, assets, apis));

            p.Features.Add(Make("友方无敌 / 锁血", "面板",
                new[] { "Attributes/InventoryAttributes/ia_health.txt", "Attributes/InventoryAttributes/ia_shield1.txt" },
                new[] { "ModifyTransactionHandling", "HasInventoryOfType" }, assets, apis));

            p.Features.Add(Make("建造加速", "面板",
                new[] { "Attributes/BuffAttributes/ba_campaign_production_time_fast.txt", "Attributes/BuffAttributes/ba_Production_set_to_2.txt" },
                new[] { "ModifyBuff" }, assets, apis));

            p.Features.Add(Make("超级攻击力", "面板",
                new[] { "Attributes/BuffAttributes/ba_max_attack.txt" },
                new[] { "ModifyBuff" }, assets, apis));

            p.Features.Add(Make("立即完成建造", "面板",
                new string[0],
                new[] { "CompleteConstruction" }, assets, apis));

            p.Features.Add(Make("指挥官能量无限", "面板",
                new[] { "Attributes/InventoryAttributes/ia_resource3.txt" },
                new[] { "GetEntityInventories", "SetInventoryValue" }, assets, apis));

            p.Features.Add(Make("兵种能量无限", "面板",
                new[] { "Attributes/InventoryAttributes/ia_mana.txt" },
                new[] { "SetInventoryValue", "GetAllAliveEntities" }, assets, apis));

            p.Features.Add(Make("技能无冷却（含兵种）", "面板",
                new string[0],
                new[] { "ResetAbilityCooldowns", "GetAllAliveEntities" }, assets, apis));

            p.Features.Add(Make("全图视野", "面板",
                new string[0],
                new[] { "ToggleFogOfWar" }, assets, apis));

            p.Features.Add(Make("我方自动判定", "状态栏",
                new string[0],
                new[] { "IsLocalPlayer" }, assets, apis));

            return p;
        }

        private static FeatureCheck Make(string name, string hotkey, string[] assets, string[] apis,
                                         HashSet<string> haveAssets, HashSet<string> haveApis)
        {
            var f = new FeatureCheck { Name = name, Hotkey = hotkey };
            // null means "could not read that source", in which case we do not blame the build
            if (haveAssets != null)
            {
                foreach (string a in assets)
                    if (!haveAssets.Contains(a)) f.Missing.Add("属性资源缺失: " + Path.GetFileNameWithoutExtension(a));
            }
            if (haveApis != null)
            {
                foreach (string a in apis)
                    if (!haveApis.Contains(a)) f.Missing.Add("脚本接口缺失: " + a);
            }
            return f;
        }

        private static string ReadBuildId(string gameDir, out string source)
        {
            source = "";
            try
            {
                string steamapps = Path.GetFullPath(Path.Combine(gameDir, "..", ".."));
                string acf = Path.Combine(steamapps, "appmanifest_" + AppId + ".acf");
                if (!File.Exists(acf)) return "";
                string text = File.ReadAllText(acf);
                Match m = Regex.Match(text, "\"buildid\"\\s+\"(\\d+)\"");
                if (!m.Success) return "";
                source = Path.GetFileName(acf);
                return m.Groups[1].Value;
            }
            catch { return ""; }
        }

        private static HashSet<string> ReadApiSymbols(string dllPath)
        {
            try
            {
                if (!File.Exists(dllPath)) return null;
                byte[] data = File.ReadAllBytes(dllPath);
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in ApiNames)
                {
                    byte[] needle = Encoding.Unicode.GetBytes(name);
                    if (IndexOf(data, needle) >= 0) set.Add(name);
                }
                return set;
            }
            catch { return null; }
        }

        private static readonly string[] ApiNames =
        {
            "SetInventoryValue", "GetAllAliveEntities", "ModifyTransactionHandling",
            "HasInventoryOfType", "ModifyBuff", "CompleteConstruction",
            "GetEntityInventories", "ResetAbilityCooldowns", "IsLocalPlayer",
            "ToggleFogOfWar",
        };

        private static HashSet<string> ReadAssetPaths(string zipPath)
        {
            try
            {
                if (!File.Exists(zipPath)) return null;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var e in zip.Entries) set.Add(e.FullName);
                }
                return set;
            }
            catch { return null; }
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            int last = hay.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // texts
        // ------------------------------------------------------------------
        internal static string ShortVerdict(GameProbe p)
        {
            if (p == null) return "未检测";
            if (!p.GameFound) return "未检测到游戏";

            var broken = p.Broken;
            string head = p.VersionExact ? "✔ " + p.VerdictCn
                                         : "⚠ " + p.VerdictCn;
            if (!p.Layout01)
                return "⚠ " + p.VerdictCn;
            if (broken.Count == 0) return head + " · " + p.Features.Count + " 项功能自检全部通过";
            return head + " · " + broken.Count + " 项功能可能失效";
        }

        internal static string BrokenListCn(GameProbe p)
        {
            if (p == null || p.Broken.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var f in p.Broken)
            {
                string hk = f.Name.Contains("我方自动判定") ? "槽位判定" : f.Hotkey;
                sb.Append("· ").Append(f.Name).Append("（").Append(hk).Append("）：")
                  .Append(string.Join("，", f.Missing.ToArray())).AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Full note: which build is supported, what breaks when it differs, how to self-check.</summary>
        internal static string DocText(GameProbe p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Crossfire Legion 修改器  v" + Trainer + "  适配说明");
            sb.AppendLine("=========================================================");
            sb.AppendLine();
            sb.AppendLine("一、适配的游戏版本");
            sb.AppendLine("   游戏           " + GameName + "（Steam AppID " + AppId + "）");
            sb.AppendLine("   版本代号       " + VerifiedProduct);
            sb.AppendLine("   可执行文件     " + VerifiedFileVersion);
            sb.AppendLine("   Steam build    " + VerifiedBuildId);
            sb.AppendLine("   验证日期       " + VerifiedDate);
            sb.AppendLine();
            sb.AppendLine("   修改器不写死内存地址，而是走游戏自己的脚本层（Data\\Scripts 下的");
            sb.AppendLine("   Lua 扩展）和引擎接口（Carbon 的 SimController、utilities、actions），");
            sb.AppendLine("   所以同一大版本内的小更新通常仍然可用。");
            sb.AppendLine();
            sb.AppendLine("二、本次检测");
            if (p == null || !p.GameFound)
            {
                sb.AppendLine("   没有检测到游戏目录，无法判断游戏版本。");
            }
            else
            {
                sb.AppendLine("   游戏目录       " + p.Dir);
                sb.AppendLine("   检测到版本     " + Show(p.ProductVersion) + "（exe " + Show(p.ExeFileVersion) + "）");
                sb.AppendLine("   Steam build    " + Show(p.BuildId) + (p.BuildIdSource.Length > 0 ? "（" + p.BuildIdSource + "）" : ""));
                sb.AppendLine("   结论           " + ShortVerdict(p));
                if (!p.Layout01)
                {
                    sb.AppendLine("   缺少文件:");
                    foreach (string m in p.MissingAnchors) sb.AppendLine("     · " + m);
                }
                if (p.Broken.Count > 0)
                {
                    sb.AppendLine("   可能失效的功能:");
                    foreach (string line in BrokenListCn(p).Split('\n'))
                        sb.AppendLine("   " + line.TrimEnd());
                }
            }
            sb.AppendLine();
            sb.AppendLine("三、版本对不上时，哪些功能会先失效");
            sb.AppendLine("   1) 游戏大版本更新（Unity 或 Carbon 引擎升级）");
            sb.AppendLine("      → BepInEx 无法注入，游戏里完全不出现面板，所有功能失效。");
            sb.AppendLine("      表现：游戏目录里没有生成 BepInEx\\LogOutput.log，或日志里没有");
            sb.AppendLine("            \"Crossfire Trainer: harmony patches applied\"。");
            sb.AppendLine();
            sb.AppendLine("   2) 脚本目录或自动加载方式变化");
            sb.AppendLine("      （Data\\Scripts\\Source\\Extensions 不再自动执行）");
            sb.AppendLine("      → 面板能开、开关能点，但游戏里毫无变化。");
            sb.AppendLine("      表现：日志出现 \"lua script = <NOT LOADED>\"。");
            sb.AppendLine();
            sb.AppendLine("   3) 属性资源改名或删除（Data\\Attributes.zip 内）");
            sb.AppendLine("      · ia_resource1 / ia_resource2 → 资源修改无效");
            sb.AppendLine("      · ia_health / ia_shield1     → 无敌失效");
            sb.AppendLine("      · ba_campaign_production_time_fast / ba_Production_set_to_2 → 建造不变快");
            sb.AppendLine("      · ba_max_attack              → 攻击力不变");
            sb.AppendLine("      · ia_resource3               → 指挥官能量不回复");
            sb.AppendLine("      · ia_mana                    → 兵种能量不回复（紫条）");
            sb.AppendLine("      表现：日志出现 \"assets missing: mat=... dmg=...\"。");
            sb.AppendLine();
            sb.AppendLine("   4) 引擎脚本接口被移除或改名（Carbon.Simulation.dll）");
            sb.AppendLine("      · utilities.GetAllAliveEntities  → 资源 / 无敌 / 建造 / 攻击 全部失效");
            sb.AppendLine("      · actions.ModifyBuff             → 建造加速、超级攻击力失效");
            sb.AppendLine("      · utilities.CompleteConstruction → 立即完成建造失效");
            sb.AppendLine("      · utilities.GetEntityInventories → 指挥官能量不回复");
            sb.AppendLine("      · utilities.ResetAbilityCooldowns→ 技能无冷却失效（含兵种技能，冷却照常）");
            sb.AppendLine("      · utilities.ToggleFogOfWar       → 全图视野失效（迷雾照常）");
            sb.AppendLine("      · utilities.IsLocalPlayer        → 我方判定退回 1 号位（状态栏显示 P1*）");
            sb.AppendLine("      表现：日志出现 \"compat: missing ...\"。");
            sb.AppendLine();
            sb.AppendLine("   5) SimController.TimeScale 改名");
            sb.AppendLine("      → 游戏速度滑条无效，其余功能正常。");
            sb.AppendLine();
            sb.AppendLine("四、版本不符时的表现汇总（按严重程度）");
            sb.AppendLine("   完全不出现面板     引擎注入失败，等于没装（重装游戏后先跑一次安装器）");
            sb.AppendLine("   面板正常、无效果   脚本层或属性资源对不上，见上面第 2、3 条");
            sb.AppendLine("   个别功能失效       接口被改名，面板上该行仍然亮，但游戏里没反应");
            sb.AppendLine("   只有速度没反应     SimController.TimeScale 变化，其余正常");
            sb.AppendLine();
            sb.AppendLine("   修改器不会因为版本不符而阻止安装：小更新多数功能仍然可用，");
            sb.AppendLine("   先装上、进对局试一下，比反复重装更省事。");
            sb.AppendLine();
            sb.AppendLine("五、怎么自己确认");
            sb.AppendLine("   · 本安装器会读取游戏版本并显示适配状态；");
            sb.AppendLine("   · 命令行：  <本程序> --status    查看安装状态 + 版本结论");
            sb.AppendLine("              <本程序> --compat    只看版本适配说明");
            sb.AppendLine("   · 游戏内：  面板状态栏显示 \"对局中 · 已连接 · P6\"，");
            sb.AppendLine("              P 后面是我方槽位，带 * 表示退回了 1 号位兜底；");
            sb.AppendLine("   · 快捷键：  只有 F4 一个（显示 / 隐藏面板），其余功能全部用鼠标点，");
            sb.AppendLine("              不占用游戏的其他按键。");
            sb.AppendLine("   · 游戏日志：<游戏目录>\\BepInEx\\LogOutput.log");
            sb.AppendLine("              搜 \"compat:\" / \"assets missing:\" / \"lua script\"。");
            sb.AppendLine();
            sb.AppendLine("六、游戏更新后建议的做法");
            sb.AppendLine("   1. 先正常启动一次游戏，看面板是否出现；");
            sb.AppendLine("   2. 没出现 → 重新运行本安装器（会覆盖成适配当前引擎的 BepInEx 文件）；");
            sb.AppendLine("   3. 出现了但某几项无效 → 把 BepInEx\\LogOutput.log 里的");
            sb.AppendLine("      \"compat:\" 与 \"assets missing:\" 两行发给修改器作者，按日志补。");
            sb.AppendLine();
            sb.AppendLine("七、这份说明存在哪");
            sb.AppendLine("   全部内置在本程序（exe）内部，不会往游戏目录写任何说明文件，");
            sb.AppendLine("   所以游戏卸载、重装都不影响它。");
            sb.AppendLine("   查看方式：双击本程序 →「游戏版本」卡片 →「适配说明…」；");
            sb.AppendLine("             或命令行  <本程序> --compat。");
            return sb.ToString();
        }

        private static string Show(string s)
        {
            return string.IsNullOrEmpty(s) ? "（未读到）" : s;
        }
    }
}
