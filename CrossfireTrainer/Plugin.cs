using System;
using System.Collections.Concurrent;
using System.Reflection;
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
        public bool MoveSpeed;     // friendly units at x2 movement speed
        public bool PopCap;        // population cap raised past the skirmish limit
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
            MoveSpeed = false;
            PopCap = false;
            Speed = 1f;
        }
    }

    [BepInPlugin(Guid, "Crossfire Legion Trainer", BuildInfo.Version)]
    public class TrainerPlugin : BaseUnityPlugin
    {
        public const string Guid = "roger.crossfirelegion.trainer";

        /// <summary>
        /// Damage the "super attack" toggle applies. The shipped buff asset
        /// (ba_max_attack) carries 1,000,000, which is well past anything a unit
        /// needs and makes every damage number in the UI meaningless; the asset is
        /// retuned to this value once per session instead.
        /// </summary>
        internal const int DamageAmount = 1000;

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
        private readonly System.Diagnostics.Stopwatch _fastWatch = new System.Diagnostics.Stopwatch();
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

        // Set when the Lua side never finished loading (Trainer exists but has no
        // functions). Nothing it is asked to do can work in that state, so every
        // later command is dropped instead of being retried: a retry loop here is
        // what once turned one broken script into 1.9 GB of log and a 12 s stall.
        private bool _luaBroken;
        private int _luaFaultLogged;
        private int _cmdDropped;

        // profiling
        private int _tickCount;
        private double _tickSumMs;
        private double _tickMaxMs;
        private int _lastHitchFrame = -1;
        private double _frameMsSum;
        private int _frameCount;
        private float _fpsReportAt;
        private int _gc0, _gc1, _gc2;

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
        /// <summary>Drains without executing (ConcurrentQueue has no Clear on this runtime).</summary>
        private void ClearQueue()
        {
            while (Queue.TryDequeue(out _)) { }
        }

        internal void Enqueue(string luaCode)
        {
            if (string.IsNullOrEmpty(luaCode)) return;
            if (_luaBroken) { _cmdDropped++; return; }
            // Backpressure: a handler that feeds the queue faster than it drains must
            // not be able to grow it without bound.
            if (Queue.Count > 256) { _cmdDropped++; return; }
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
            if (_luaBroken) { ClearQueue(); return; }
            if (Lua == null) return;

            bool ran = false;
            // Bounded drain. A fault handler can queue a follow-up command, and an
            // unbounded while-loop then never leaves this frame: the simulation thread
            // stalls, every unit stops moving and the whole match freezes. Whatever is
            // left over simply runs on the next tick.
            int budget = 64;
            while (budget-- > 0 && Queue.TryDequeue(out var code))
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
                    _fastWatch.Restart();
                    RunCached(ref _fastClosure, "if Trainer then Trainer.FastTick() end", "TrainerFastTick");
                    _fastWatch.Stop();
                    double fms = _fastWatch.Elapsed.TotalMilliseconds;
                    if (fms > 5.0) Log.LogWarning($"[PERF] slow fast tick {fms:0.0} ms");
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
            if (Lua == null || _luaBroken) return;

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
                try { Lua.DisposeLuaClosure(closure.ClosureId); } catch { }
                closure = null;

                if (IsScriptMissing(e.Message))
                {
                    TripLuaBreaker(e.Message);
                    return;
                }

                if (_luaFaultLogged < 5)
                {
                    _luaFaultLogged++;
                    Log.LogWarning("Trainer " + token + ": " + e.Message);
                }
                OnTickFault();
            }
        }

        /// <summary>
        /// True when the Lua side is a stub: the table is there but the functions are
        /// not, which is what a chunk that aborted half-way through leaves behind.
        /// </summary>
        private static bool IsScriptMissing(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            if (message.IndexOf("attempt to call a nil value", System.StringComparison.Ordinal) < 0) return false;
            return message.IndexOf("field 'Set'", System.StringComparison.Ordinal) >= 0
                || message.IndexOf("field 'Tick'", System.StringComparison.Ordinal) >= 0
                || message.IndexOf("field 'Compat'", System.StringComparison.Ordinal) >= 0
                || message.IndexOf("field 'FastTick'", System.StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// One-way latch: from here on no Lua command is queued at all, the pending
        /// ones are dropped and the panel switches back to "off". A broken script is
        /// reported once; it must never turn into a per-frame retry loop.
        /// </summary>
        internal void TripLuaBreaker(string reason)
        {
            if (_luaBroken) return;
            _luaBroken = true;

            ClearQueue();
            State.Resources = false;
            State.God = false;
            State.Build = false;
            State.Damage = false;
            State.SkillEnergy = false;
            State.Mana = false;
            State.UnitCd = false;
            State.Vision = false;
            State.MoveSpeed = false;
            State.PopCap = false;

            Log.LogError("Trainer: lua script is not usable for this match, all features disabled -> " + reason);
            PendingToast = "脚本未加载成功，功能已全部关闭";
        }

        /// <summary>Repeating tick faults: stop the expensive per-entity features.</summary>
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
                case "movespeed": return "移动速度翻倍";
                case "popcap": return "一键200人口";
                case "speed": return "游戏速度";
                default: return key;
            }
        }

        private bool IsFeatureOn(string key)
        {
            switch (key)
            {
                case "resources": return State.Resources;
                case "god": return State.God;
                case "build": return State.Build;
                case "damage": return State.Damage;
                case "energy": return State.SkillEnergy;
                case "mana": return State.Mana;
                case "unitcd": return State.UnitCd;
                case "vision": return State.Vision;
                case "movespeed": return State.MoveSpeed;
                case "popcap": return State.PopCap;
                default: return false;
            }
        }

        /// <summary>
        /// Switches exactly one feature off after its own fault. The Lua side may have
        /// already flipped its mirror of the state before the throw, so the off command
        /// is re-sent to keep both sides in sync.
        /// </summary>
        internal void DisableFeature(string key)
        {
            // Already off means there is nothing to undo. Without this the off command
            // is queued again, faults again, and calls back into here -- an endless
            // queue/execute loop that floods the log file and stalls the game.
            if (!IsFeatureOn(key)) return;

            switch (key)
            {
                case "resources": State.Resources = false; break;
                case "god": State.God = false; break;
                case "build": State.Build = false; break;
                case "damage": State.Damage = false; break;
                case "energy": State.SkillEnergy = false; break;
                case "mana": State.Mana = false; break;
                case "unitcd": State.UnitCd = false; break;
                case "movespeed": State.MoveSpeed = false; break;
                case "popcap": State.PopCap = false; break;
                default: return;
            }

            Enqueue($"if Trainer then Trainer.Set('{key}', false) end");
            PendingToast = FeatureLabel(key) + "在当前对局不可用，已自动关闭";
            Log.LogWarning("Trainer: feature '" + key + "' faulted -> turned off");
        }

        private void ExecuteLua(string code)
        {
            if (Lua == null) return;
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
                // "attempt to call a nil value (field 'Set')" is not a feature fault:
                // it means Trainer exists but the script never finished loading, so
                // retrying (or turning features off one by one) can only make things
                // worse. Trip the breaker once and stay quiet afterwards.
                if (IsScriptMissing(e.Message))
                {
                    TripLuaBreaker(e.Message);
                    return;
                }

                if (_luaFaultLogged < 5)
                {
                    _luaFaultLogged++;
                    Log.LogWarning("Trainer lua: " + e.Message + "  [" + code + "]");
                }
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
        /// <summary>
        /// Retunes the damage buff asset from the shipped 1,000,000 down to
        /// <see cref="DamageAmount"/>. The value lives on a plain asset object
        /// (Value + the IntValue cache filled in by its OnLoad), so this is a
        /// property write instead of shipping a patched Attributes.zip. Only the
        /// asset the trainer itself uses is touched, and it is touched once.
        /// </summary>
        private void RetuneDamageAsset(SimScriptProcessor processor)
        {
            const string assetName = "ba_max_attack";
            try
            {
                var field = typeof(SimScriptProcessor).GetField("mAttributeDatabase",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var db = field?.GetValue(processor) as Carbon.Attributes.IAttributeDatabase;
                if (db == null)
                {
                    Log.LogWarning("Trainer: attribute database not reachable, damage stays 1000000");
                    return;
                }

                var asset = db.GetObject(assetName) as Carbon.Simulation.Data.BuffAttributes;
                if (asset == null)
                {
                    Log.LogWarning($"Trainer: {assetName} not found, damage stays 1000000");
                    return;
                }

                asset.Value = Carbon.Core.Numerics.Q64.Real.FromInt(DamageAmount);
                asset.IntValue = DamageAmount;      // cached in OnLoad, used by int attributes
                Log.LogInfo($"Trainer: damage asset {assetName} = {DamageAmount}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Trainer: damage retune failed -> {e.Message}");
            }
        }

        internal void OnSessionStart(SimScriptProcessor processor)
        {
            Lua = PatchOnInitialize.LuaField(processor);
            Sim = PatchOnInitialize.FrameCtrl(processor) as SimController;

            RetuneDamageAsset(processor);

            Log.LogInfo($"Trainer: session start (lua={(Lua != null)}, sim={(Sim != null)})");
            _lastLuaMsg = "";
            _compatLogged = false;
            _visionBroken = false;
            _luaBroken = false;
            _luaFaultLogged = 0;
            _cmdDropped = 0;
            PlayerTag = "";
            _frameMsSum = 0.0;
            _frameCount = 0;
            _fpsReportAt = 0f;

            // A stub table still carries .version (it is assigned near the top of the
            // script), so "version is readable" does not mean "the script loaded".
            // Only loadedFlag is written after the last statement of the chunk.
            string ver = TryGetLuaString("Trainer.version");
            if (TryGetLuaString("Trainer.loadedFlag") != "ok")
            {
                Log.LogWarning("Trainer: lua script did not finish loading (version=" +
                               (string.IsNullOrEmpty(ver) ? "<none>" : ver) +
                               "), reloading from disk to surface the real error");
                ExecuteLua("Trainer = nil");     // drop whatever half-built table exists
                LoadTrainerScript();             // a fault here is reported with its message
                ver = TryGetLuaString("Trainer.version");
            }

            bool loadedOk = TryGetLuaString("Trainer.loadedFlag") == "ok";
            Log.LogInfo("Trainer: lua script = " + (string.IsNullOrEmpty(ver) ? "<NOT LOADED>" : "v" + ver));

            // Say which engine interfaces this build still has, so a game patch shows up
            // in the log instead of a feature silently doing nothing.
            if (!loadedOk)
            {
                TripLuaBreaker("script aborted while loading");
                _applySpeedNextFrame = true;   // still restore x1 in case it was changed
                return;
            }

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
            if (State.MoveSpeed) SetFeature("movespeed", true);
            if (State.PopCap) SetFeature("popcap", true);
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
            // A once-per-30 s frame-rate line: "the game feels slow" is otherwise
            // impossible to confirm or deny from the log, because hitches alone say
            // nothing about the steady state. The window start is anchored on the first
            // frame after the match begins -- leaving it at 0 made the very first report
            // cover a single frame and print a meaningless average.
            if (_fpsReportAt <= 0f) _fpsReportAt = Time.unscaledTime;
            _frameMsSum += dt;
            _frameCount++;
            if (Time.unscaledTime - _fpsReportAt >= 30f && _frameCount > 0)
            {
                double avg = _frameMsSum * 1000.0 / _frameCount;
                Log.LogInfo($"[PERF] fps {1000.0 / avg:0.0}  ({avg:0.0} ms/frame over {_frameCount} frames)" +
                            $"  gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  on: {ActiveFeatures()}");
                _frameMsSum = 0.0;
                _frameCount = 0;
                _fpsReportAt = Time.unscaledTime;
            }

            if (dt < 0.15f) return;
            if (Time.frameCount - _lastHitchFrame < 2) return;   // one event per stall
            _lastHitchFrame = Time.frameCount;

            // Which features were on matters: a stall with everything off is the
            // game, a stall that only happens with one toggle on is the trainer.
            // The GC counters say whether the stall was a stop-the-world collection
            // (very common in Unity/Mono) instead of a slow frame of our own making.
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            Log.LogWarning($"[HITCH] {dt * 1000f:0} ms  @ {Time.unscaledTime:0.0}s  on: {ActiveFeatures()}  gc +{g0 - _gc0}/+{g1 - _gc1}/+{g2 - _gc2}");
            _gc0 = g0; _gc1 = g1; _gc2 = g2;
        }

        private string ActiveFeatures()
        {
            var sb = new System.Text.StringBuilder(32);
            if (State.Resources) sb.Append("res,");
            if (State.God) sb.Append("god,");
            if (State.Build) sb.Append("build,");
            if (State.Damage) sb.Append("dmg,");
            if (State.SkillEnergy) sb.Append("energy,");
            if (State.Mana) sb.Append("mana,");
            if (State.UnitCd) sb.Append("unitcd,");
            if (State.Vision) sb.Append("vision,");
            if (State.MoveSpeed) sb.Append("speed,");
            if (State.PopCap) sb.Append("pop,");
            if (sb.Length == 0) return "none";
            sb.Length--;                       // drop the trailing comma
            return sb.ToString();
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
                case "movespeed":
                    State.MoveSpeed = !State.MoveSpeed;
                    SetFeature(key, State.MoveSpeed);
                    ShowToast(label + (State.MoveSpeed ? " 开" : " 关"));
                    break;
                case "popcap":
                    State.PopCap = !State.PopCap;
                    SetFeature(key, State.PopCap);
                    ShowToast(label + (State.PopCap ? " 开" : " 关"));
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
