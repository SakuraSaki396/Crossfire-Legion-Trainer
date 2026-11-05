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
CALLS = { reset = {}, fog = {}, mana = {}, spawn = 0, despawn = 0, buff = 0, setinv = 0 }

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
  ModifyBuff                  = function() CALLS.buff = CALLS.buff + 1 end,
  ModifyTransactionHandling   = function() end,
  HasInventoryOfType          = function() return true end,
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

# ---- status / toggles ----
T.Status()
T.Set("unitcd", False)
print("state   :", T["state"]["unitcd"], T["state"]["vision"])
print("ALL OK")
