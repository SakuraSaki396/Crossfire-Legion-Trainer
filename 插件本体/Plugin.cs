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
        public bool SkillCd;       // ability cooldowns cleared continuously
        public float Speed = 1f;
        public int Amount = 200000;

        public void Reset()
        {
            Resources = false;
            God = false;
            Build = false;
            Damage = false;
            SkillEnergy = false;
            SkillCd = false;
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
            if (State.SkillEnergy || State.SkillCd)
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
            }
            catch (Exception e)
            {
                Log.LogWarning("Trainer " + token + ": " + e.Message);
                try { Lua.DisposeLuaClosure(closure.ClosureId); } catch { }
                closure = null;
            }
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
            if (State.SkillCd) SetFeature("nocd", true);
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

        internal void ChangeSpeed(int dir)
        {
            // 0.1 per press so the hotkeys line up with the slider's granularity.
            float s = State.Speed + dir * 0.1f;
            State.Speed = Mathf.Clamp(Mathf.Round(s * 10f) / 10f, 0.5f, 5f);
            ApplySpeed();
            PushSpeed();
            ShowToast("游戏速度 x" + State.Speed.ToString("0.0"));
        }

        internal void ShowToast(string msg)
        {
            Toast = msg;
            ToastUntil = Time.unscaledTime + 2.5f;
        }

        // ------------------------------------------------------------------
        // hotkeys
        // ------------------------------------------------------------------
        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                MenuVisible = !MenuVisible;
            }

            if (Input.GetKeyDown(KeyCode.F2)) ToggleFeature("resources", "无限资源");
            if (Input.GetKeyDown(KeyCode.F3)) ToggleFeature("god", "无敌 / 锁血");
            if (Input.GetKeyDown(KeyCode.F4)) ToggleFeature("build", "建造加速");
            if (Input.GetKeyDown(KeyCode.F5)) ToggleFeature("damage", "超级攻击力");
            if (Input.GetKeyDown(KeyCode.F10)) ToggleFeature("energy", "技能无限能量");
            if (Input.GetKeyDown(KeyCode.F11)) ToggleFeature("nocd", "技能无需冷却");
            if (Input.GetKeyDown(KeyCode.F6)) ChangeSpeed(-1);
            if (Input.GetKeyDown(KeyCode.F7)) ChangeSpeed(+1);
            if (Input.GetKeyDown(KeyCode.F8))
            {
                State.Speed = 1f;
                ApplySpeed();
                PushSpeed();
                ShowToast("游戏速度重置为 x1.0");
            }
            if (Input.GetKeyDown(KeyCode.F9))
            {
                RunLua("Trainer.InstantBuild()");
                ShowToast("立即完成建造");
            }
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
                case "nocd":
                    State.SkillCd = !State.SkillCd;
                    SetFeature(key, State.SkillCd);
                    ShowToast(label + (State.SkillCd ? " 开" : " 关"));
                    break;
            }
        }
    }
}
