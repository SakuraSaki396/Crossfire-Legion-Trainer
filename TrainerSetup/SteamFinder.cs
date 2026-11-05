using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TrainerSetup
{
    /// <summary>Locates the game by walking the Steam library configuration.</summary>
    internal static class SteamFinder
    {
        internal const string ExeName = "Crossfire_Legion.exe";

        internal static bool IsGameDir(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir)
                       && Directory.Exists(dir)
                       && File.Exists(Path.Combine(dir, ExeName));
            }
            catch { return false; }
        }

        /// <summary>Returns the first valid game directory found, or null.</summary>
        internal static string FindGameDir()
        {
            foreach (string common in CandidateCommonDirs())
            {
                try
                {
                    if (!Directory.Exists(common)) continue;

                    foreach (string sub in Directory.GetDirectories(common))
                    {
                        if (!Path.GetFileName(sub).ToLowerInvariant().Contains("crossfire")) continue;
                        if (IsGameDir(sub)) return sub;
                    }
                }
                catch { }
            }
            return null;
        }

        internal static IEnumerable<string> CandidateCommonDirs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            // Candidates here are steamapps\common folders, not the game folder itself.
            Action<string> push = p =>
            {
                if (string.IsNullOrEmpty(p)) return;
                string c = p.Trim().TrimEnd('\\', '/');
                try
                {
                    if (Directory.Exists(c) && seen.Add(c)) result.Add(c);
                }
                catch { }
            };

            // 1) every Steam library declared by every installed Steam client
            foreach (string steamRoot in SteamRoots())
            {
                foreach (string lib in LibraryFolders(steamRoot))
                    push(Path.Combine(lib, "steamapps", "common"));

                push(Path.Combine(steamRoot, "steamapps", "common"));
            }

            // 2) common manual layouts on every fixed drive
            foreach (var drive in FixedDrives())
            {
                string root = drive + ":\\";
                push(Path.Combine(root, "SteamLibrary", "steamapps", "common"));
                push(Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common"));
                push(Path.Combine(root, "Steam", "steamapps", "common"));
                push(Path.Combine(root, "SteamLibrary", "SteamApps", "common"));
                push(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common"));
            }

            return result;
        }

        internal static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string> add = p =>
            {
                if (string.IsNullOrEmpty(p)) return;
                p = p.Trim().TrimEnd('\\', '/');
                if (Directory.Exists(p) && seen.Add(p)) roots.Add(p);
            };

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                {
                    foreach (string sub in new[]
                    {
                        @"Software\Valve\Steam",
                        @"SOFTWARE\WOW6432Node\Valve\Steam",
                        @"SOFTWARE\Valve\Steam",
                    })
                    {
                        try
                        {
                            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            using (var key = baseKey.OpenSubKey(sub))
                            {
                                if (key == null) continue;
                                add(key.GetValue("SteamPath") as string);
                                add(key.GetValue("InstallPath") as string);
                            }
                        }
                        catch { }
                    }
                }
            }

            foreach (var drive in FixedDrives())
            {
                add(drive + ":\\Steam");
                add(drive + ":\\Program Files (x86)\\Steam");
                add(drive + ":\\Program Files\\Steam");
            }

            return roots;
        }

        /// <summary>Parses libraryfolders.vdf for every library root path.</summary>
        internal static IEnumerable<string> LibraryFolders(string steamRoot)
        {
            var libs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string vdf in new[]
            {
                Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            })
            {
                string text;
                try
                {
                    if (!File.Exists(vdf)) continue;
                    text = File.ReadAllText(vdf);
                }
                catch { continue; }

                // Both the old numeric-key format and the new "path" format contain a quoted path.
                foreach (Match m in Regex.Matches(text, "\"([A-Za-z]:[\\\\/][^\"]*)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    p = p.TrimEnd('\\', '/');
                    if (Directory.Exists(p) && seen.Add(p)) libs.Add(p);
                }
            }

            return libs;
        }

        internal static IEnumerable<string> FixedDrives()
        {
            var list = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.Name.TrimEnd('\\', '/'));
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }
}
