"""Offline smoke test for zzz_trainer.lua, with the engine API mocked in Lua.

The mocks are written in Lua rather than Python on purpose: lupa wraps values
crossing the boundary, so `owner == player` comparisons inside the script would
see two different proxies and fail for the wrong reason.
"""
import os
from lupa import LuaRuntime

HERE = os.path.dirname(os.path.abspath(__file__))
LUA = os.path.join(os.path.dirname(HERE), "CrossfireTrainer", "zzz_trainer.lua")
src = open(LUA, encoding="utf-8").read()

lua = LuaRuntime(unpack_returned_tuples=True)

MOCK = r"""
CALLS = { reset = {}, fog = {}, mana = {}, bufflog = {}, god = {}, spawn = 0, despawn = 0, buff = 0, setinv = 0 }
MANA_CUR = 30          -- current value of ia_mana on the units that carry it
MANA_CAP = 100

transactionHandlingMode = { Normal = 0, IgnoreRemove = 1 }
modificationOperation   = { Add = 0, Remove = 1 }
assetTypes = { InventoryAttributes = "ia", BuffAttributes = "ba", EntityBlueprint = "ebp" }

-- The engine hands out one cached wrapper per asset, so identity compares matter:
-- cache them here too, otherwise k == A.mana would never be true.
local assetCache = {}
local function asset(name, ty)
  local k = ty .. ":" .. name
  if assetCache[k] == nil then assetCache[k] = { name = name, ty = ty } end
  return assetCache[k]
end
assets = { GetCarbonAssetByName = function(name, ty) return asset(name, ty) end }

local cmd, u1, u2, enemy = { id = "cmd" }, { id = "u1" }, { id = "u2" }, { id = "e1" }
commanders = { [6] = cmd }

local alive = { u1, u2, enemy, cmd }

utilities = {
  IsLocalPlayer          = function(c) return c == cmd end,
  GetAllAliveEntities    = function() CALLS.scans = (CALLS.scans or 0) + 1 return alive end,
  GetCommanderOwnership  = function(e) if e == enemy then return enemy end return cmd end,
  ResetAbilityCooldowns  = function(e) table.insert(CALLS.reset, e.id) end,
  CompleteConstruction   = function() end,
  IsEntityAlive          = function() return true end,
  CreateEntityTarget     = function(e) return e end,
  GetEntityInventories   = function(p, t)
                             t[asset("ia_resource3", "ia")] = { CurrentAmountInt = 100 }
                             if p.id == "u1" or p.id == "u2" then
                               t[asset("ia_mana", "ia")] = { CurrentAmountInt = MANA_CUR, ActiveCapInt = MANA_CAP }
                             end
                           end,
  ToggleFogOfWar         = function(visible, c) table.insert(CALLS.fog, tostring(visible)) end,
}

actions = {
  SetInventoryValue           = function(e, a, v)
                                  if a ~= nil and a.name == "ia_mana" then
                                    table.insert(CALLS.mana, e.id)
                                  else
                                    CALLS.setinv = CALLS.setinv + 1
                                  end
                                end,
  ModifyBuff                  = function(e, b, op)
                                  CALLS.buff = CALLS.buff + 1
                                  table.insert(CALLS.bufflog, {
                                    id = e and e.id or "?",
                                    buff = b and b.name or "?",
                                    op = op,
                                  })
                                end,
  ModifyTransactionHandling   = function(e, a, mode)
                                  table.insert(CALLS.god, (e and e.id or "?") .. ":" .. tostring(mode))
                                end,
  -- only some units carry the purple bar in the real game, so mimic that here:
  -- the commander and the enemy do not have ia_mana. Population lives on the
  -- commander entity only, exactly like the real engine books it.
  HasInventoryOfType          = function(e, a)
                                  if a == nil or e == nil then return false end
                                  if a.name == "ia_mana" then
                                    return e.id == "u1" or e.id == "u2"
                                  end
                                  if a.name == "ia_population" then
                                    return e.id == "cmd"
                                  end
                                  if a.name == "ia_health" or a.name == "ia_shield1" then
                                    return e.id ~= "e1"
                                  end
                                  return false
                                end,
  DespawnEntity               = function() CALLS.despawn = CALLS.despawn + 1 end,
  SpawnEntityFromBlueprint    = function() CALLS.spawn = CALLS.spawn + 1 return { id = "vision" } end,
}
"""

lua.execute(MOCK)
lua.execute(src)

T = lua.globals().Trainer
print("version :", T["version"])
print("compat  :", T["compatLine"])

assert T["compatLine"] == "compat: ok (10 api)", T["compatLine"]

CALLS = lua.globals().CALLS


def reset_ids():
    out = []
    for i in range(1, len(CALLS["reset"]) + 1):
        out.append(CALLS["reset"][i])
    return out


def mana_ids():
    out = []
    for i in range(1, len(CALLS["mana"]) + 1):
        out.append(CALLS["mana"][i])
    return out


# ---- unit cooldowns ----
T.Set("unitcd", True)
got = reset_ids()
print("reset   :", got)
assert "u1" in got and "u2" in got and "cmd" in got, got
assert "e1" not in got, "enemy was reset: " + str(got)

# ---- unit mana (ia_mana, separate from the commander's ia_resource3) ----
T.Set("mana", True)
got = mana_ids()
print("mana    :", got)
assert "u1" in got and "u2" in got, got
assert "e1" not in got, "enemy mana was filled: " + str(got)
assert T["manaCount"] == 2, T["manaCount"]
print("mana count:", T["manaCount"])

# A full bar must not be written again. Every SetInventoryValue posts an event
# into the simulation, and doing that for the whole army five times a second is
# what made the frame times spiky, so the trainer now reads before writing.
lua.execute("MANA_CUR = MANA_CAP")
lua.execute("CALLS.mana = {}")
CALLS = lua.globals().CALLS
T.FastTick()
T.FastTick()
assert len(CALLS["mana"]) == 0, "wrote a full mana bar: " + str(mana_ids())

# ...and a bar that actually dropped is filled again
lua.execute("MANA_CUR = 0")
T.FastTick()
T.FastTick()
print("mana topup:", mana_ids())
assert sorted(mana_ids()) == ["u1", "u2"], mana_ids()
print("mana    : full bars are left alone, empty ones are refilled")

CALLS["reset"] = lua.eval("{}")
lua.execute("CALLS.reset = {}")
CALLS = lua.globals().CALLS
T.FastTick()                      # fastNo 1 -> skipped
assert len(CALLS["reset"]) == 0, "should be throttled on odd ticks"
T.FastTick()                      # fastNo 2 -> runs
assert len(CALLS["reset"]) == 3, len(CALLS["reset"])
print("throttle: ok (skips odd fast ticks)")

# ---- vision ----
T.Set("vision", True)
fog = [CALLS["fog"][i] for i in range(1, len(CALLS["fog"]) + 1)]
assert fog == ["false"], fog          # isVisible=false hides the fog
print("vision  : on -> ToggleFogOfWar(isVisible=false)")

T.Set("vision", False)
fog = [CALLS["fog"][i] for i in range(1, len(CALLS["fog"]) + 1)]
assert fog == ["false", "true"], fog
print("vision  : off -> ToggleFogOfWar(isVisible=true)")

# ---- mana and vision must not interfere ----
# Regression: the mana path used to raise a C# NRE that the plugin blamed on
# vision, which killed the fog feature for the rest of the match.
T.Set("vision", True)
before = len(CALLS["fog"])
T.Set("mana", True)
T.FastTick()
T.FastTick()
after = len(CALLS["fog"])
assert after == before, "mana tick changed the fog state: %d -> %d" % (before, after)
assert T["state"]["vision"] is True and T["state"]["mana"] is True
print("coexist : vision + mana both on, fog untouched")
T.Set("vision", False)

# ---- build / damage buffs ----
# BuffBucket.AddBuff does not de-duplicate, so every extra Add stacks another
# entry and doubles the bonus. These checks pin down the caching that prevents it.
lua.execute("CALLS.bufflog = {}")
CALLS = lua.globals().CALLS


def buff_entries():
    out = []
    bl = CALLS["bufflog"]
    for i in range(1, len(bl) + 1):
        item = bl[i]
        out.append((item["id"], item["buff"], item["op"]))
    return out


ADD, REMOVE = 0, 1

T.Set("damage", True)
entries = buff_entries()
adds = [x for x in entries if x[2] == ADD]
print("buff add :", sorted(x[0] for x in adds))
assert sorted(x[0] for x in adds) == ["cmd", "u1", "u2"], adds
assert all(x[1] == "ba_max_attack" for x in adds), adds

# Five more ticks let applyBuffs run again (tickNo % 5); every unit is already
# marked, so nothing may be re-added.
for _ in range(5):
    T.Tick()
entries = buff_entries()
adds = [x for x in entries if x[2] == ADD]
assert len(adds) == 3, "buff stacked on repeat: " + str(adds)

# Turning it off has to actually strip what was applied (3 passes x 3 units).
T.Set("damage", False)
removes = [x for x in buff_entries() if x[2] == REMOVE]
print("buff del :", len(removes))
assert len(removes) == 9, "expected 9 removes, got " + str(len(removes))
print("buffs   : applied once per unit, stripped on off")

# ---- move speed x2 ----
lua.execute("CALLS.bufflog = {}")
CALLS = lua.globals().CALLS

T.Set("movespeed", True)
adds = [x for x in buff_entries() if x[2] == ADD and x[1] == "ba_mod_speed_200pc"]
print("speed add:", sorted(x[0] for x in adds))
assert sorted(x[0] for x in adds) == ["cmd", "u1", "u2"], adds

# same de-duplication rule as the other buffs: ticks must not stack it
for _ in range(5):
    T.Tick()
adds = [x for x in buff_entries() if x[2] == ADD and x[1] == "ba_mod_speed_200pc"]
assert len(adds) == 3, "speed buff stacked: " + str(adds)

T.Set("movespeed", False)
removes = [x for x in buff_entries() if x[2] == REMOVE and x[1] == "ba_mod_speed_200pc"]
print("speed del:", len(removes))
assert len(removes) == 9, removes
print("speed   : x2 once per unit, stripped on off")

# ---- population cap ----
# Only the entity that actually owns ia_population may be buffed: the path
# Inventories[ia_population] resolves to nothing on a line unit, and buffing it
# there would just be a wasted attribute recompute.
lua.execute("CALLS.bufflog = {}")
CALLS = lua.globals().CALLS

T.Set("popcap", True)
adds = [x for x in buff_entries() if x[2] == ADD and x[1] == "ba_pop_increase_200"]
print("pop add  :", sorted(x[0] for x in adds))
assert sorted(x[0] for x in adds) == ["cmd"], adds

for _ in range(5):
    T.Tick()
adds = [x for x in buff_entries() if x[2] == ADD and x[1] == "ba_pop_increase_200"]
assert len(adds) == 1, "pop buff stacked: " + str(adds)

T.Set("popcap", False)
removes = [x for x in buff_entries() if x[2] == REMOVE and x[1] == "ba_pop_increase_200"]
print("pop del  :", len(removes))
assert len(removes) == 3, removes
print("popcap  : cap buff only on the commander, stripped on off")

# ---- god mode is incremental ----
# ModifyTransactionHandling is idempotent, so re-asserting it on the whole army
# every 1.5 s was pure overhead. Only newcomers may be touched.
lua.execute("CALLS.god = {}")
CALLS = lua.globals().CALLS

T.Set("god", True)
first = [CALLS["god"][i] for i in range(1, len(CALLS["god"]) + 1)]
print("god set  :", first)
assert len(first) == 6, first          # 3 friendly units x (health + shield)

for _ in range(3):
    T.Tick()                           # tickNo % 3 == 0 -> god pass runs
again = [CALLS["god"][i] for i in range(1, len(CALLS["god"]) + 1)]
assert len(again) == 6, "god re-applied to the same units: " + str(again)

# the periodic re-assert pass is still there, so a unit that lost the flag recovers
for _ in range(60):
    T.Tick()
reasserted = [CALLS["god"][i] for i in range(1, len(CALLS["god"]) + 1)]
assert len(reasserted) > 6, "no re-assert pass"
T.Set("god", False)
print("god      : only newcomers, re-asserted every 30 s")

# ---- the army scan must be throttled, not run once per feature ----
# GetAllAliveEntities() allocates a wrapper for every entity in the match; calling it
# on every driver entry point is what made a full GC fire every ~15 s. One scan per
# two ticks is the contract now.
lua.execute("CALLS.scans = 0")
CALLS = lua.globals().CALLS
T.Set("god", True)                      # a user action forces one fresh scan
start = CALLS["scans"]
for _ in range(4):
    T.Tick()
ticks = CALLS["scans"] - start
assert ticks <= 2, ("4 ticks should rescan at most twice, got", ticks)

lua.execute("CALLS.scans = 0")
CALLS = lua.globals().CALLS
T.Set("mana", True)
start = CALLS["scans"]
for _ in range(6):
    T.FastTick()
fast = CALLS["scans"] - start
assert fast <= 1, ("fast ticks must reuse the tick scan, got", fast)
print("throttle: army list rescanned per tick window, not per feature")

# ---- status / toggles ----
T.Status()
T.Set("unitcd", False)
print("state   :", T["state"]["unitcd"], T["state"]["vision"])

# ---- regression: the engine's Lua box has no OS module ----
# SimScriptProcessor builds its LuaEnv with LuaStandardModules.EssentialPlus, which
# has no OS flag, so `os` is nil in the real game. v1.13.0 read os.clock near the
# top of the file: the chunk aborted there, Trainer survived as a stub holding only
# .version, and every feature went dead. Load the script again with os/io removed.
sandbox = LuaRuntime(unpack_returned_tuples=True)
sandbox.execute(MOCK)
sandbox.execute("os = nil io = nil package = nil require = nil dofile = nil loadfile = nil")
sandbox.execute(src)
sb = sandbox.globals()
assert sb.Trainer["loadedFlag"] == "ok", sb.Trainer["loadedFlag"]
for fn in ("Set", "Tick", "FastTick", "Compat", "Status"):
    assert sb.Trainer[fn] is not None, "missing Trainer." + fn
expected = open(os.path.join(os.path.dirname(HERE), "version.txt"), encoding="utf-8").read().strip()
assert sb.Trainer["version"] == expected, (sb.Trainer["version"], expected)
print("sandbox : loads with no os/io, all entry points present")

# Static guard: any future use of os./io. would break the whole script at load time,
# which is far more expensive than failing here.
import re
body = "\n".join(l for l in src.splitlines() if not l.lstrip().startswith("--"))
hits = re.findall(r"\b(os|io|package|require)\s*[.(]", body)
assert not hits, "script must not touch the stdlib that the sandbox lacks: " + str(hits)
print("sandbox : no stdlib calls outside the sandbox's module set")
print("ALL OK")
