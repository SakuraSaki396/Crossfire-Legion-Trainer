using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Logging;
using Carbon.Scripting.Lua;
using Carbon.Simulation;
using HarmonyLib;
using UnityEngine;

namespace CrossfireTrainer
{
    /// <summary>Feature state shared between the UI and the Lua layer.</summary>
    public class TrainerState
    {
        public bool Resources;
        public bool God;
        public bool Build;
        public bool Damage;
        public bool SkillEnergy;   // commander energy pinned to max
        public bool Mana;          // unit energy (purple bar) pinned to max
        public bool UnitCd;        // ability cooldowns cleared for the whole army
        public bool Vision;        // fog of war off
        public float Speed = 1f;
        public int Amount = 200000;

        public void Reset()
        {
            Resources = false;
            God = false;
            Build = false;
            Damage = false;
            SkillEnergy = false;
            Mana = false;
            UnitCd = false;
            Vision = false;
            Speed = 1f;
        }
    }

    [BepInPlugin(Guid, "Crossfire Legion Trainer", BuildInfo.Version)]
    public class TrainerPlugin : BaseUnityPlugin
    {
        public const string Guid = "roger.crossfirelegion.trainer";

        internal static TrainerPlugin Instance;
        internal static ManualLogSource Log;

        internal readonly TrainerState State = new TrainerState();
        internal readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();

        // Only touched from the simulation thread.
        internal LuaEnv Lua;
        internal SimController Sim;

        internal bool InMatch => Lua != null;
        /// <summary>Slot the Lua side treats as the local player ("P2", "P1*" for the fallback).</summary>
        internal string PlayerTag = "";
        internal bool MenuVisible = true;
        internal string Toast = "";
        internal float ToastUntil;
        /// <summary>Queued from the simulation thread, shown by Update on the main thread.</summary>
        internal volatile string PendingToast;


        private Harmony _harmony;
        private float _speedCheckTimer;
        private volatile bool _applySpeedNextFrame;
        private string _lastLuaMsg = "";
        private int _luaMsgWarned;
        private readonly System.Diagnostics.Stopwatch _tickWatch = System.Diagnostics.Stopwatch.StartNew();
        private long _lastTickMs;

        private LuaClosure _tickClosure;
        private LuaClosure _fastClosure;
        private long _lastFastMs;
        private bool _compatLogged;
        // Set when the fog call throws inside the engine binding. It is a C#-level
        // exception, so a Lua pcall cannot catch it and every retry would damage the
        // script state again; one failure disables the feature for the whole match.
        // Only ever set by a fault that actually came from the vision toggle.
        private bool _visionBroken;
        // Consecutive faults raised by the per-frame/per-tick Lua drivers. A single
        // fault is not fatal (the closure is re-created), but a repeating one means
        // one of the army features is broken on this build.
        private int _tickFaults;

        // profiling
        private int _tickCount;
        private double _tickSumMs;
        private double _tickMaxMs;
        private int _lastHitchFrame = -1;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _harmony = new Harmony(Guid);
            try
            {
                _harmony.PatchAll(typeof(TrainerPlugin).Assembly);
                Log.LogInfo("Crossfire Trainer: harmony patches applied.");
            }
            catch (Exception e)
            {
                Log.LogError("Crossfire Trainer: patch failure -> " + e);
            }
        }

        private void Update()
        {
            ProfileFrame();
            Hud.Tick(this);
            HandleHotkeys();

            if (_applySpeedNextFrame)
            {
                _applySpeedNextFrame = false;
                ApplySpeed();
            }

            if (!string.IsNullOrEmpty(PendingToast))
            {
                ShowToast(PendingToast);
                PendingToast = null;
            }

            EnforceSpeed();
        }

        // ------------------------------------------------------------------
        // Lua bridge
        // ------------------------------------------------------------------
        internal void Enqueue(string luaCode)
        {
            if (string.IsNullOrEmpty(luaCode)) return;
            Queue.Enqueue(luaCode);
        }

        internal void SetFeature(string key, bool on)
        {
            Enqueue($"if Trainer then Trainer.Set('{key}', {(on ? "true" : "false")}) end");
        }

        internal void PushSpeed()
        {
            Enqueue($"if Trainer then Trainer.Set('speed', {State.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}) end");
        }

        internal void PushAmount()
        {
            Enqueue($"if Trainer then Trainer.Set('amount', {State.Amount}) end");
        }

        internal void RunLua(string call)
        {
            Enqueue("if Trainer then " + call + " end");
        }

        /// <summary>Drains the queue; called from the simulation thread only.</summary>
        internal void Drain()
        {
            if (Lua == null) return;

            bool ran = false;
            while (Queue.TryDequeue(out var code))
            {
                ran = true;
                ExecuteLua(code);
            }

            // One-shot: report which engine interfaces this build still exposes. Kept
            // separate from the lastMsg mirror, which later ticks overwrite.
            if (!_compatLogged)
            {
                try
                {
                    string line = Lua.Get<string>("Trainer.compatLine");
                    if (!string.IsNullOrEmpty(line))
                    {
                        _compatLogged = true;
                        if (line.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0)
                            Log.LogWarning("Trainer: " + line);
                        else
                            Log.LogInfo("Trainer: " + line);
                    }
                }
                catch { }
            }

            // Periodic driver: refreshes god mode / buffs so newly built units are covered.
            long now = _tickWatch.ElapsedMilliseconds;
            if (now - _lastTickMs >= 500)
            {
                _lastTickMs = now;
                ran = true;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                TickLua();
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                _tickCount++;
                _tickSumMs += ms;
                if (ms > _tickMaxMs) _tickMaxMs = ms;
                if (ms > 5.0) Log.LogWarning($"[PERF] slow tick {ms:0.0} ms (tick #{_tickCount})");
                if (_tickCount % 100 == 0)
                {
                    Log.LogInfo($"[PERF] tick #{_tickCount}  avg={_tickSumMs / 100.0:0.00} ms  max={_tickMaxMs:0.0} ms");
                    _tickSumMs = 0;
                    _tickMaxMs = 0;
                }
            }

            // Skill features need to react to a single cast, so they get their own
            // 100 ms driver instead of waiting for the 500 ms upkeep tick.
            if (State.SkillEnergy || State.Mana || State.UnitCd)
            {
                if (now - _lastFastMs >= 100)
                {
                    _lastFastMs = now;
                    RunCached(ref _fastClosure, "if Trainer then Trainer.FastTick() end", "TrainerFastTick");
                }
            }

            if (!ran) return;

            // Mirroring costs a managed string each call, so sample it instead of doing it per tick.
            if (_tickCount % 4 != 0) return;

            try
            {
                // Separate guard: a nil field would otherwise skip the message mirroring below.
                try
                {
                    string tag = Lua.Get<string>("Trainer.playerTag");
                    if (!string.IsNullOrEmpty(tag)) PlayerTag = tag;
                }
                catch { }

                string msg = Lua.Get<string>("Trainer.lastMsg");
                if (!string.IsNullOrEmpty(msg) && msg != _lastLuaMsg)
                {
                    _lastLuaMsg = msg;
                    Log.LogInfo("[Lua] " + msg);
                }
                else if (string.IsNullOrEmpty(msg) && _luaMsgWarned < 2)
                {
                    _luaMsgWarned++;
                    Log.LogWarning("[Lua] command ran but lastMsg is empty");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Lua] probe failed: " + e.Message);
            }
        }

        /// <summary>Runs Trainer.Tick() through a closure parsed once per session.</summary>
        private void TickLua()
        {
            RunCached(ref _tickClosure, "if Trainer then Trainer.Tick() end", "TrainerTick");
        }

        /// <summary>Parses <paramref name="code"/> once and reuses the closure until it dies.</summary>
        private void RunCached(ref LuaClosure closure, string code, string token)
        {
            if (Lua == null) return;

            if (closure == null || !closure.IsValid)
            {
                try { closure = Lua.Parse(code, token, false); }
                catch (Exception e)
                {
                    Log.LogWarning("Trainer: " + token + " closure failed -> " + e.Message);
                    return;
                }
                if (closure == null || !closure.IsValid) return;
            }

            try
            {
                Lua.LuaCallBegin(closure);
                Lua.LuaCallInvoke();
                Lua.LuaCallEnd();
                _tickFaults = 0;
            }
            catch (Exception e)
            {
                Log.LogWarning("Trainer " + token + ": " + e.Message);
                try { Lua.DisposeLuaClosure(closure.ClosureId); } catch { }
                closure = null;
                OnTickFault();
            }
        }

        /// <summary>
        /// A repeatable Lua fault is not fatal: the closure is disposed and re-parsed on
        /// the next tick, and the game keeps running. Three in a row, however, means one
        /// of the per-entity features is broken on this build, so stop it instead of
        /// failing forever. Whatever the cause, other features (vision in particular)
        /// must keep working -- they share nothing but this driver.
        /// </summary>
        private void OnTickFault()
        {
            _tickFaults++;
            if (_tickFaults < 3) return;

            _tickFaults = 0;
            bool off = false;
            if (State.Mana) { DisableFeature("mana"); off = true; }
            if (State.UnitCd) { DisableFeature("unitcd"); off = true; }
            if (off) Log.LogWarning("Trainer: army features stopped after 3 consecutive tick faults");
        }

        /// <summary>
        /// An exception thrown by a C# binding skips the Lua pcall and surfaces here.
        /// It is attributed to the feature whose toggle raised it, so one broken
        /// feature can never take an unrelated one down with it.
        /// </summary>
        private void RecoverFromLuaFault(string key)
        {
            if (string.IsNullOrEmpty(key)) return;   // a plain command (Compat / InstantBuild)

            if (key == "vision")
            {
                if (!State.Vision) return;
                State.Vision = false;
                _visionBroken = true;
                PendingToast = "全图视野在当前对局不可用，已自动关闭";
                Log.LogWarning("Trainer: fog-of-war toggle failed -> disabled for this match");
                Enqueue("if Trainer then Trainer.Set('vision', false) end");
                return;
            }

            DisableFeature(key);
        }

        /// <summary>Key of the feature a queued command toggles, or null for plain calls.</summary>
        private static string FeatureKeyOf(string code)
        {
            const string marker = "Trainer.Set('";
            int i = code.IndexOf(marker, System.StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            int j = code.IndexOf('\'', i);
            return j > i ? code.Substring(i, j - i) : null;
        }

        private static string FeatureLabel(string key)
        {
            switch (key)
            {
                case "resources": return "无限资源";
                case "god": return "无敌 / 锁血";
                case "build": return "建造 / 生产加速";
                case "damage": return "超级攻击力";
                case "energy": return "指挥官能量无限";
                case "mana": return "兵种能量无限";
                case "unitcd": return "技能无冷却";
                case "speed": return "游戏速度";
                default: return key;
            }
        }

        /// <summary>
        /// Switches exactly one feature off after its own fault. The Lua side may have
        /// already flipped its mirror of the state before the throw, so the off command
        /// is re-sent to keep both sides in sync.
        /// </summary>
        internal void DisableFeature(string key)
        {
            switch (key)
            {
                case "resources": State.Resources = false; break;
                case "god": State.God = false; break;
                case "build": State.Build = false; break;
                case "damage": State.Damage = false; break;
                case "energy": State.SkillEnergy = false; break;
                case "mana": State.Mana = false; break;
                case "unitcd": State.UnitCd = false; break;
                default: return;
            }

            Enqueue($"if Trainer then Trainer.Set('{key}', false) end");
            PendingToast = FeatureLabel(key) + "在当前对局不可用，已自动关闭";
            Log.LogWarning("Trainer: feature '" + key + "' faulted -> turned off");
        }

        private void ExecuteLua(string code)
        {
            LuaClosure closure = null;
            try
            {
                closure = Lua.Parse(code, "TrainerPlugin", false);
                Lua.LuaCallBegin(closure);
                Lua.LuaCallInvoke();
                Lua.LuaCallEnd();
            }
            catch (Exception e)
            {
                Log.LogWarning("Trainer lua: " + e.Message + "  [" + code + "]");
                RecoverFromLuaFault(FeatureKeyOf(code));
            }
            finally
            {
                if (closure != null)
                {
                    try { Lua.DisposeLuaClosure(closure.ClosureId); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // session hooks
        // ------------------------------------------------------------------
        internal void OnSessionStart(SimScriptProcessor processor)
        {
            Lua = PatchOnInitialize.LuaField(processor);
            Sim = PatchOnInitialize.FrameCtrl(processor) as SimController;

            Log.LogInfo($"Trainer: session start (lua={(Lua != null)}, sim={(Sim != null)})");
            _lastLuaMsg = "";
            _compatLogged = false;
            _visionBroken = false;
            PlayerTag = "";

            string ver = TryGetLuaString("Trainer.version");
            if (string.IsNullOrEmpty(ver))
            {
                Log.LogInfo("Trainer: engine did not load the script, loading it from disk...");
                LoadTrainerScript();
                ver = TryGetLuaString("Trainer.version");
            }
            Log.LogInfo("Trainer: lua script = " + (string.IsNullOrEmpty(ver) ? "<NOT LOADED>" : "v" + ver));

            // Say which engine interfaces this build still has, so a game patch shows up
            // in the log instead of a feature silently doing nothing.
            if (string.IsNullOrEmpty(ver))
                Log.LogWarning("Trainer: compat probe skipped, lua side not loaded");
            else
                RunLua("Trainer.Compat()");

            // Re-apply whatever was toggled before, the new Lua env is fresh.
            if (State.Resources) SetFeature("resources", true);
            if (State.God) SetFeature("god", true);
            if (State.Build) SetFeature("build", true);
            if (State.Damage) SetFeature("damage", true);
            if (State.SkillEnergy) SetFeature("energy", true);
            if (State.Mana) SetFeature("mana", true);
            if (State.UnitCd) SetFeature("unitcd", true);
            if (State.Vision) SetFeature("vision", true);
            PushAmount();
            PushSpeed();
            // Time.timeScale must be touched from the Unity main thread.
            _applySpeedNextFrame = true;
        }

        internal void OnSessionEnd()
        {
            if (Lua != null && _tickClosure != null)
            {
                try { Lua.DisposeLuaClosure(_tickClosure.ClosureId); } catch { }
            }
            _tickClosure = null;

            if (Lua != null && _fastClosure != null)
            {
                try { Lua.DisposeLuaClosure(_fastClosure.ClosureId); } catch { }
            }
            _fastClosure = null;

            Lua = null;
            Sim = null;
            PlayerTag = "";
            _compatLogged = false;
            _applySpeedNextFrame = true;   // restore x1 when the match is over
        }

        internal static string TrainerScriptPath =>
            System.IO.Path.Combine(Paths.GameRootPath, "Data", "Scripts", "Source", "Extensions", "zzz_trainer.lua");

        /// <summary>Reads a Lua global/table path, returns null instead of throwing.</summary>
        internal string TryGetLuaString(string path)
        {
            if (Lua == null) return null;
            try { return Lua.Get<string>(path); }
            catch { return null; }
        }

        /// <summary>Loads the Lua trainer file explicitly; must run on the simulation thread.</summary>
        internal void LoadTrainerScript()
        {
            if (Lua == null) return;

            string path = TrainerScriptPath;
            if (!System.IO.File.Exists(path))
            {
                Log.LogError("Trainer: lua file missing -> " + path);
                return;
            }

            string code;
            try { code = System.IO.File.ReadAllText(path); }
            catch (Exception e) { Log.LogError("Trainer: cannot read lua file -> " + e.Message); return; }

            LuaClosure closure = null;
            try
            {
                closure = Lua.Parse(code, "zzz_trainer", false);
                Lua.LuaCallBegin(closure);
                Lua.LuaCallInvoke();
                Lua.LuaCallEnd();
                Log.LogInfo("Trainer: lua script executed from " + path);
            }
            catch (Exception e)
            {
                Log.LogError("Trainer: lua load failed -> " + e.Message);
            }
            finally
            {
                if (closure != null)
                {
                    try { Lua.DisposeLuaClosure(closure.ClosureId); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // game speed
        // ------------------------------------------------------------------
        internal void ApplySpeed(bool toUnity = true)
        {
            if (Sim != null)
            {
                try { Sim.TimeScale = State.Speed; }
                catch (Exception e) { Log.LogWarning("TimeScale: " + e.Message); }
            }
            if (toUnity) Time.timeScale = Sim != null ? State.Speed : 1f;
        }

        /// <summary>Logs frame spikes so Lua-side work can be correlated with visible stutter.</summary>
        private void ProfileFrame()
        {
            if (!InMatch) return;

            float dt = Time.unscaledDeltaTime;
            if (dt < 0.15f) return;
            if (Time.frameCount - _lastHitchFrame < 2) return;   // one event per stall
            _lastHitchFrame = Time.frameCount;
            Log.LogWarning($"[HITCH] {dt * 1000f:0} ms  @ {Time.unscaledTime:0.0}s");
        }

        private void EnforceSpeed()
        {
            if (Sim == null) return;

            _speedCheckTimer += Time.unscaledDeltaTime;
            if (_speedCheckTimer < 1f) return;
            _speedCheckTimer = 0f;

            // Re-apply if the game reset it (pause / unpause / mission script).
            if (Time.timeScale > 0.001f && Math.Abs(Time.timeScale - State.Speed) > 0.01f)
                ApplySpeed();
        }

        internal void ShowToast(string msg)
        {
            Toast = msg;
            ToastUntil = Time.unscaledTime + 2.5f;
        }

        // ------------------------------------------------------------------
        // hotkeys
        // ------------------------------------------------------------------
        /// <summary>
        /// F4 is the only binding left: it just shows / hides the panel. Everything
        /// else is click-only, so nothing collides with the game's own F-keys.
        /// </summary>
        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F4))
                MenuVisible = !MenuVisible;
        }

        internal void ToggleFeature(string key, string label)
        {
            switch (key)
            {
                case "resources":
                    State.Resources = !State.Resources;
                    SetFeature(key, State.Resources);
                    ShowToast(label + (State.Resources ? " 开" : " 关"));
                    break;
                case "god":
                    State.God = !State.God;
                    SetFeature(key, State.God);
                    ShowToast(label + (State.God ? " 开" : " 关"));
                    break;
                case "build":
                    State.Build = !State.Build;
                    SetFeature(key, State.Build);
                    ShowToast(label + (State.Build ? " 开" : " 关"));
                    break;
                case "damage":
                    State.Damage = !State.Damage;
                    SetFeature(key, State.Damage);
                    ShowToast(label + (State.Damage ? " 开" : " 关"));
                    break;
                case "energy":
                    State.SkillEnergy = !State.SkillEnergy;
                    SetFeature(key, State.SkillEnergy);
                    ShowToast(label + (State.SkillEnergy ? " 开" : " 关"));
                    break;
                case "mana":
                    State.Mana = !State.Mana;
                    SetFeature(key, State.Mana);
                    ShowToast(label + (State.Mana ? " 开" : " 关"));
                    break;
                case "unitcd":
                    State.UnitCd = !State.UnitCd;
                    SetFeature(key, State.UnitCd);
                    ShowToast(label + (State.UnitCd ? " 开" : " 关"));
                    break;
                case "vision":
                    if (_visionBroken)
                    {
                        ShowToast("全图视野在当前对局不可用");
                        break;
                    }
                    State.Vision = !State.Vision;
                    SetFeature(key, State.Vision);
                    ShowToast(label + (State.Vision ? " 开" : " 关"));
                    break;
            }
        }
    }
}
