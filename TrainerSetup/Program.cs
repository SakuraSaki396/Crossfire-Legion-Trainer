using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TrainerSetup
{
    internal static class Program
    {
        internal static readonly string TrainerVersion = GameVersion.Trainer;

        internal static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "CrossfireTrainerSetup.log");

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // the silent modes print Chinese; without this cmd.exe would mangle it
            try { Console.OutputEncoding = Utf8NoBom; } catch { }

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
                return RunSilent(args);

            Application.Run(new SetupForm());
            return 0;
        }

        /// <summary>Headless mode, used for automated deployment and troubleshooting.</summary>
        private static int RunSilent(string[] args)
        {
            string cmd = args[0].ToLowerInvariant();
            string dir = args.Length > 1 ? args[1] : null;
            var sb = new StringBuilder();

            try
            {
                switch (cmd)
                {
                    case "--detect":
                    {
                        string found = SteamFinder.FindGameDir();
                        WriteLine(sb, found == null ? "DETECT: none" : "DETECT: " + found);
                        SaveLog(sb);
                        return found == null ? 1 : 0;
                    }
                    case "--status":
                    {
                        dir = dir ?? SteamFinder.FindGameDir();
                        if (dir == null) { WriteLine(sb, "ERROR: game not found"); break; }
                        var st = Installer.GetStatus(dir);
                        var probe = GameVersion.Probe(dir);
                        WriteLine(sb, "STATUS: " + st.State + " | " + st.Detail);
                        WriteLine(sb, "DIR: " + dir);
                        WriteLine(sb, "TRAINER: " + Program.TrainerVersion);
                        WriteLine(sb, "GAME_VERSION: " + (probe.ProductVersion.Length > 0 ? probe.ProductVersion : "unreadable"));
                        WriteLine(sb, "GAME_BUILD: " + (probe.BuildId.Length > 0 ? probe.BuildId : "unreadable"));
                        WriteLine(sb, "COMPAT: " + probe.VerdictCode + " | " + probe.VerdictCn);
                        foreach (var f in probe.Broken)
                            WriteLine(sb, "RISK: " + f.Name + " (" + f.Hotkey + ") -> " + string.Join("; ", f.Missing.ToArray()));
                        SaveLog(sb);
                        return 0;
                    }
                    case "--compat":
                    {
                        dir = dir ?? SteamFinder.FindGameDir();
                        var probe = dir == null ? null : GameVersion.Probe(dir);
                        if (probe != null)
                        {
                            WriteLine(sb, "DIR: " + probe.Dir);
                            WriteLine(sb, "COMPAT: " + probe.VerdictCode + " | " + probe.VerdictCn);
                            foreach (var f in probe.Broken)
                                WriteLine(sb, "RISK: " + f.Name + " (" + f.Hotkey + ") -> " + string.Join("; ", f.Missing.ToArray()));
                            WriteLine(sb, "");
                        }
                        WriteLine(sb, GameVersion.DocText(probe));
                        SaveLog(sb);
                        if (probe == null) return 1;
                        return probe.VersionExact ? 0 : 3;
                    }
                    case "--version":
                    {
                        WriteLine(sb, "TRAINER: " + Program.TrainerVersion);
                        WriteLine(sb, "TARGET: " + GameVersion.GameName + " " + GameVersion.VerifiedProduct
                                      + " / build " + GameVersion.VerifiedBuildId);
                        SaveLog(sb);
                        return 0;
                    }
                    case "--install":
                    {
                        dir = dir ?? SteamFinder.FindGameDir();
                        if (dir == null) { WriteLine(sb, "ERROR: game not found"); break; }
                        WriteLine(sb, "DIR: " + dir);
                        var r = Installer.Install(dir, m => WriteLine(sb, m));
                        SaveLog(sb);
                        return r.Ok ? 0 : 2;
                    }
                    case "--uninstall":
                    {
                        dir = dir ?? SteamFinder.FindGameDir();
                        if (dir == null) { WriteLine(sb, "ERROR: game not found"); break; }
                        WriteLine(sb, "DIR: " + dir);
                        var r = Installer.Uninstall(dir, m => WriteLine(sb, m));
                        SaveLog(sb);
                        return r.Ok ? 0 : 2;
                    }
                    default:
                        WriteLine(sb, "ERROR: unknown command " + cmd);
                        break;
                }
            }
            catch (Exception e)
            {
                WriteLine(sb, "ERROR: " + e);
            }

            SaveLog(sb);
            return 1;
        }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static void SaveLog(StringBuilder sb)
        {
            try { File.WriteAllText(LogPath, sb.ToString(), Utf8NoBom); } catch { }
        }

        private static void WriteLine(StringBuilder sb, string msg)
        {
            sb.AppendLine(msg);
            try { Console.WriteLine(msg); } catch { }
        }
    }
}
