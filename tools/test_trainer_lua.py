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
CALLS = { reset = {}, fog = {}, mana = {}, bufflog = {}, spawn = 0, despawn = 0, buff = 0, setinv = 0 }

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
  GetAllAliveEntities    = function() return alive end,
  GetCommanderOwnership  = function(e) if e == enemy then return enemy end return cmd end,
  ResetAbilityCooldowns  = function(e) table.insert(CALLS.reset, e.id) end,
  CompleteConstruction   = function() end,
  IsEntityAlive          = function() return true end,
  CreateEntityTarget     = function(e) return e end,
  GetEntityInventories   = function(p, t)
                             t[asset("ia_resource3", "ia")] = { CurrentAmountInt = 100 }
                             if p.id == "u1" or p.id == "u2" then
                               t[asset("ia_mana", "ia")] = { CurrentAmountInt = 30 }
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
  ModifyTransactionHandling   = function() end,
  -- only some units carry the purple bar in the real game, so mimic that here:
  -- the commander and the enemy do not have ia_mana.
  HasInventoryOfType          = function(e, a)
                                  return a ~= nil and a.name == "ia_mana"
                                     and e ~= nil and (e.id == "u1" or e.id == "u2")
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

# ---- status / toggles ----
T.Status()
T.Set("unitcd", False)
print("state   :", T["state"]["unitcd"], T["state"]["vision"])
print("ALL OK")
