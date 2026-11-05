--====================================================================--
--  Crossfire Legion Trainer - Lua core
--  Loaded by the plugin at the start of every match.
--  NOTE: keep this file pure ASCII. The engine feeds the source to Lua
--  using Encoding.ASCII.GetByteCount(), so any non-ASCII character
--  truncates the buffer and the chunk fails to compile.
--====================================================================--

-- guard against double loading (engine extension scan + plugin loader)
if Trainer ~= nil and Trainer._loaded == true then
    return
end

Trainer = Trainer or {}
local T = Trainer

T.version = "1.11.0"

-- ia_resource3 (commander energy) is capped by AttributeValueMax = 100
local ENERGY_MAX = 100

T.state = T.state or {
    resources = false,   -- unlimited resources
    god       = false,   -- friendly units invulnerable
    build     = false,   -- construction / production speed
    damage    = false,   -- super weapon damage
    energy    = false,   -- commander energy (ia_resource3) pinned to max
    mana      = false,   -- unit energy / mana (ia_mana) pinned to max
    unitcd    = false,   -- clear ability cooldowns for every friendly entity
    vision    = false,   -- fog of war toggle for the local commander
    speed     = 1.0,     -- game speed (handled by the plugin)
    amount    = 200000,  -- resource amount
}

local frame    = 0
local A        = {}      -- asset cache
local assetsOk = false
local lastMsg  = ""

local function log(msg)
    lastMsg = tostring(msg)
    T.lastMsg = lastMsg          -- mirrored to the plugin log
    pcall(function() print("[Trainer] " .. lastMsg) end)
end

local function safe(fn, ...)
    local ok, err = pcall(fn, ...)
    if not ok then log("ERR: " .. tostring(err)) end
    return ok
end

--------------------------------------------------------------------
-- assets
--------------------------------------------------------------------
local function getAsset(name, typeName)
    local ok, asset = pcall(function()
        return assets.GetCarbonAssetByName(name, assetTypes[typeName])
    end)
    if ok then return asset end
    return nil
end

local function loadAssets()
    if assetsOk then return true end

    A.mat    = getAsset("ia_resource1", "InventoryAttributes")
    A.fuel   = getAsset("ia_resource2", "InventoryAttributes")
    -- commander skill energy: "Resource Commander Energy (3)", max 100
    A.energy = getAsset("ia_resource3", "InventoryAttributes")
    -- unit skill energy: "Energy Inventory (Unit Mana)", purple bar, max 100 per unit
    A.mana   = getAsset("ia_mana",      "InventoryAttributes")
    A.health = getAsset("ia_health",   "InventoryAttributes")
    A.shield = getAsset("ia_shield1",  "InventoryAttributes")

    -- ConstructorAttributes.ConstructionRate x50 (worker build speed)
    A.bBuild = getAsset("ba_campaign_production_time_fast", "BuffAttributes")
    -- ProductionAttributes.ProductionTime = 1s (unit production time)
    A.bProd  = getAsset("ba_Production_set_to_2",           "BuffAttributes")
    -- WeaponAttributes.Amount = 1000000 (damage)
    A.bDmg   = getAsset("ba_max_attack",                    "BuffAttributes")

    assetsOk = (A.health ~= nil and A.bDmg ~= nil and A.mat ~= nil)
    if assetsOk then
        log("assets ready")
    else
        log(string.format("assets missing: mat=%s fuel=%s eng=%s mana=%s hp=%s shield=%s build=%s prod=%s dmg=%s",
            tostring(A.mat ~= nil), tostring(A.fuel ~= nil), tostring(A.energy ~= nil),
            tostring(A.mana ~= nil), tostring(A.health ~= nil), tostring(A.shield ~= nil),
            tostring(A.bBuild ~= nil), tostring(A.bProd ~= nil), tostring(A.bDmg ~= nil)))
    end
    return assetsOk
end

--------------------------------------------------------------------
-- player resolution
--------------------------------------------------------------------
-- Campaign scripts hardcode commanders[1] as "the player", which is only true
-- where the map says so. Ask the engine instead: utilities.IsLocalPlayer() is
-- backed by the commander's network id, so it works in any slot / any mode.
local player      = nil
local playerLabel = nil

local function scanPlayer()
    if commanders == nil then return nil, nil end

    local best, bestIdx = nil, nil
    pcall(function()
        for k, c in pairs(commanders) do
            if c ~= nil then
                local ok, isLocal = pcall(utilities.IsLocalPlayer, c)
                if ok and isLocal then
                    -- lowest slot wins, so the choice is deterministic
                    if best == nil or (type(k) == "number" and type(bestIdx) == "number" and k < bestIdx) then
                        best, bestIdx = c, k
                    end
                end
            end
        end
    end)

    if best ~= nil then return best, "localhost slot " .. tostring(bestIdx), bestIdx, true end

    -- nothing is flagged as local: keep the old convention instead of doing nothing
    local first = commanders[1]
    if first ~= nil then return first, "fallback slot 1", 1, false end
    return nil, nil, nil, false
end

local function adoptPlayer(p, label, idx, fromEngine)
    player, playerLabel = p, label
    -- short tag mirrored to the HUD status line ("P2" = engine says slot 2)
    T.playerTag = "P" .. tostring(idx or 1) .. (fromEngine and "" or "*")
    log("player = " .. label)
end

local function getPlayer()
    if player ~= nil then return player end
    local p, label, idx, fromEngine = scanPlayer()
    if p ~= nil then adoptPlayer(p, label, idx, fromEngine) end
    return p
end

-- Re-checks the cached commander occasionally (slot change, respawn, late join).
local function ensurePlayer()
    if player == nil then return getPlayer() end

    local ok, isLocal = pcall(utilities.IsLocalPlayer, player)
    if ok and isLocal then return player end

    local p, label, idx, fromEngine = scanPlayer()
    if p ~= nil and p ~= player then adoptPlayer(p, label, idx, fromEngine) end
    return player
end

local function forEachPlayerEntity(fn)
    local p = getPlayer()
    if p == nil then return 0 end
    local list = utilities.GetAllAliveEntities()
    if list == nil then return 0 end

    local n = 0
    for _, e in pairs(list) do
        if e ~= nil then
            local ok, owner = pcall(utilities.GetCommanderOwnership, e)
            if ok and owner == p then
                pcall(fn, e)
                n = n + 1
            end
        end
    end
    return n
end

--------------------------------------------------------------------
-- features
--------------------------------------------------------------------
local function applyResources()
    local p = getPlayer()
    if p == nil then
        log("no player commander yet")
        return
    end
    local v = T.state.amount
    if A.mat  then actions.SetInventoryValue(p, A.mat,  v) end
    if A.fuel then actions.SetInventoryValue(p, A.fuel, v) end
    log("resources set to " .. tostring(v))
end

local function setInvuln(e, on)
    local mode = transactionHandlingMode.Normal
    if on then mode = transactionHandlingMode.IgnoreRemove end

    if A.health and actions.HasInventoryOfType(e, A.health) then
        actions.ModifyTransactionHandling(e, A.health, mode, 0)
    end
    if A.shield and actions.HasInventoryOfType(e, A.shield) then
        actions.ModifyTransactionHandling(e, A.shield, mode, 0)
    end
end

local function applyGod()
    local n = forEachPlayerEntity(function(e) setInvuln(e, true) end)
    if n > 0 then log("god: " .. n .. " units") end
end

local function clearGod()
    forEachPlayerEntity(function(e) setInvuln(e, false) end)
end

-- BuffBucket.AddBuff never de-duplicates: every Add pushes another
-- AppliedBuffEntry and each entry re-applies the modification, so buffing the
-- same unit twice doubles the bonus. These weak tables remember which entities
-- already carry a buff, which keeps the steady state at exactly one entry per
-- unit -- and that is also what makes Remove work, since Remove pops one entry.
local buffedBuild = setmetatable({}, { __mode = "k" })
local buffedDamage = setmetatable({}, { __mode = "k" })

local function applyBuffs()
    local wantBuild = T.state.build
    local wantDamage = T.state.damage
    if not (wantBuild or wantDamage) then return end

    local added = 0
    forEachPlayerEntity(function(e)
        if wantBuild and not buffedBuild[e] then
            if A.bBuild then
                actions.ModifyBuff(e, A.bBuild, modificationOperation.Remove)
                actions.ModifyBuff(e, A.bBuild, modificationOperation.Add)
            end
            if A.bProd then
                actions.ModifyBuff(e, A.bProd, modificationOperation.Remove)
                actions.ModifyBuff(e, A.bProd, modificationOperation.Add)
            end
            if A.bBuild or A.bProd then
                buffedBuild[e] = true
                added = added + 1
            end
        end
        if wantDamage and not buffedDamage[e] then
            if A.bDmg then
                actions.ModifyBuff(e, A.bDmg, modificationOperation.Add)
                buffedDamage[e] = true
                added = added + 1
            end
        end
    end)
    if added > 0 then log("buffs: +" .. added) end
end

--------------------------------------------------------------------
-- buff removal
--------------------------------------------------------------------
-- Remove pops one AppliedBuffEntry per call, so a unit that was buffed N times
-- needs N calls. Builds before 1.10.0 re-added every 2.5 s and left hundreds of
-- entries behind; the extra passes clear those leftovers too.
local function stripBuffs(buffs, mark)
    for _ = 1, 3 do
        forEachPlayerEntity(function(e)
            for i = 1, #buffs do
                local b = buffs[i]
                if b ~= nil then
                    actions.ModifyBuff(e, b, modificationOperation.Remove)
                end
            end
        end)
    end
    if mark ~= nil then
        for k in pairs(mark) do mark[k] = nil end
    end
end

--------------------------------------------------------------------
-- commander skill energy
--------------------------------------------------------------------
-- Reads one inventory back from the entity. GetEntityInventories fills a plain
-- Lua table keyed by InventoryAttributes, so compare keys with == instead of
-- indexing by our cached asset handle (the wrappers are not guaranteed to be
-- the same userdata object).
local function readInventory(p, asset)
    if p == nil or asset == nil then return nil end
    local t = {}
    if not pcall(utilities.GetEntityInventories, p, t) then return nil end
    for k, v in pairs(t) do
        if k == asset and v ~= nil then
            local n = v.CurrentAmountInt
            if n == nil then n = v.CurrentAmount end
            return n
        end
    end
    return nil
end

-- Commander energy regenerates slowly and every ability spends it, so the only
-- way to feel "infinite" is to keep it pinned at the cap. Written only when it
-- actually dropped, otherwise we would post a transaction every fast tick.
local function applyEnergy()
    local p = getPlayer()
    if p == nil then return end
    if A.energy == nil then
        if not T.energyWarned then
            T.energyWarned = true
            log("energy asset missing")
        end
        return
    end

    local cur = readInventory(p, A.energy)
    if cur ~= nil and cur >= ENERGY_MAX then
        T.energyValue = cur
        return
    end

    if pcall(actions.SetInventoryValue, p, A.energy, ENERGY_MAX) then
        T.energyValue = ENERGY_MAX
        -- no log() here: this runs after every cast and would flood the mirror
        T.energyFills = (T.energyFills or 0) + 1
    end
end

--------------------------------------------------------------------
-- per-entity army pass (cooldowns + unit mana)
--------------------------------------------------------------------
-- utilities.ResetAbilityCooldowns(entity) only ever touches the entity it is
-- given: the commander's three skills and each unit's own ability collection are
-- all separate CooldownAbilityState objects. Walking the whole army is the only
-- way to reach them, because the script API has no "get abilities of entity".
-- One walk also covers the commander himself, so a single feature is enough.
local function resetEntityCooldowns(e)
    if e == nil then return false end
    return pcall(utilities.ResetAbilityCooldowns, e)
end

-- Unit mana is ia_mana ("Energy Inventory (Unit Mana)", purple bar in the UI).
-- That is a DIFFERENT inventory from the commander's ia_resource3 energy: every
-- unit that owns an active ability carries its own ia_mana with its own cap
-- (100 on the units defined in Attributes.zip), so it cannot be filled by
-- writing to the commander. SetInventoryValue() clamps to the per-entity cap, so
-- asking for a number above any cap always lands exactly on "full", whatever the
-- unit's own maximum happens to be.
local MANA_FULL = 1000000

-- HasInventoryOfType() is the only inventory query that is null-safe on the C#
-- side (it uses the null-conditional operator). utilities.GetEntityInventories()
-- dereferences the entity's Store component without a check and throws a C# NRE
-- for entities that carry no inventories at all -- and a C# exception crossing
-- the binding is NOT catchable by Lua pcall, it unwinds the whole Trainer.Set()
-- call. So every inventory lookup here goes through this helper.
local function hasMana(e)
    if A.mana == nil then return false end
    local ok, has = pcall(actions.HasInventoryOfType, e, A.mana)
    return ok and has == true
end

local function refillMana(e)
    if not hasMana(e) then return false end
    return pcall(actions.SetInventoryValue, e, A.mana, MANA_FULL)
end

-- One walk serves every per-entity feature, so turning both on costs the same as
-- turning one on. Features that are off are simply skipped per entity.
local function applyArmyTick()
    local p = getPlayer()
    if p == nil then return end
    local list = utilities.GetAllAliveEntities()
    if list == nil then return end

    local n = 0
    for _, e in pairs(list) do
        if e ~= nil then
            local ok, owner = pcall(utilities.GetCommanderOwnership, e)
            if ok and owner == p then
                if T.state.unitcd then pcall(resetEntityCooldowns, e) end
                if T.state.mana then pcall(refillMana, e) end
                n = n + 1
            end
        end
    end
    T.armyCount = n
end

-- How many of our entities actually carry the mana inventory. Run only when the
-- toggle is switched on: it is what makes "nothing happened" visible in the log
-- (0 units = this army has no purple-bar unit yet).
local function countManaUnits()
    local n = 0
    forEachPlayerEntity(function(e)
        if hasMana(e) then n = n + 1 end
    end)
    T.manaCount = n
    log("mana: " .. n .. " units")
end

--------------------------------------------------------------------
-- whole map vision
--------------------------------------------------------------------
-- The engine exposes utilities.ToggleFogOfWar(isVisible, commander), which is the
-- same call the campaign uses and is NOT listed in ScriptingAPI.json. It flips the
-- fog overlay for one commander, so the whole map becomes visible without touching
-- any unit. The old approach (spawning a campaign vision-provider entity) works
-- only in campaign maps: in a skirmish the engine throws inside the binding.
local function setFog(hidden)
    local p = getPlayer()
    if p == nil then return false end
    local fn = utilities.ToggleFogOfWar
    if fn == nil then return false end
    return pcall(fn, not hidden, p)
end

local function applyVision()
    if setFog(true) then
        log("vision: fog off")
    else
        log("vision: unsupported on this build")
    end
end

local function clearVision()
    setFog(false)
    log("vision: fog restored")
end

--------------------------------------------------------------------
-- public API
--------------------------------------------------------------------
function T.Set(key, value)
    if key == "resources" then
        T.state.resources = value and true or false
        if T.state.resources then applyResources() end

    elseif key == "god" then
        T.state.god = value and true or false
        if T.state.god then applyGod() else clearGod() end

    elseif key == "build" then
        local was = T.state.build
        T.state.build = value and true or false
        if not T.state.build and was then
            stripBuffs({ A.bBuild, A.bProd }, buffedBuild)
        elseif T.state.build then
            applyBuffs()
        end

    elseif key == "damage" then
        local was = T.state.damage
        T.state.damage = value and true or false
        if not T.state.damage and was then
            stripBuffs({ A.bDmg }, buffedDamage)
        elseif T.state.damage then
            applyBuffs()
        end

    elseif key == "energy" then
        T.state.energy = value and true or false
        if T.state.energy then applyEnergy() end

    elseif key == "mana" then
        T.state.mana = value and true or false
        if T.state.mana then
            applyArmyTick()
            countManaUnits()
        end

    elseif key == "unitcd" then
        T.state.unitcd = value and true or false
        if T.state.unitcd then applyArmyTick() end

    elseif key == "vision" then
        T.state.vision = value and true or false
        if T.state.vision then applyVision() else clearVision() end

    elseif key == "speed" then
        T.state.speed = tonumber(value) or 1.0

    elseif key == "amount" then
        T.state.amount = math.floor(tonumber(value) or 200000)
        if T.state.resources then applyResources() end
    end

    T.lastAction = tostring(key)
    log(string.format("set %s = %s", tostring(key), tostring(T.state[key])))
    return T.state[key]
end

function T.Toggle(key)
    local cur = T.state[key]
    if type(cur) == "boolean" then return T.Set(key, not cur) end
    return cur
end

function T.Status()
    log(string.format("res=%s god=%s build=%s dmg=%s eng=%s mana=%s unitcd=%s vis=%s speed=%.1f",
        tostring(T.state.resources), tostring(T.state.god),
        tostring(T.state.build), tostring(T.state.damage),
        tostring(T.state.energy), tostring(T.state.mana),
        tostring(T.state.unitcd), tostring(T.state.vision), T.state.speed))
    return T.state
end

-- instantly finish everything currently under construction
function T.InstantBuild()
    local n = 0
    forEachPlayerEntity(function(e)
        if pcall(utilities.CompleteConstruction, e) then n = n + 1 end
    end)
    log("instant build: " .. n)
end

-- Logs which engine script interfaces this build still exposes. A game patch
-- that renames one of them would otherwise make a feature look enabled but do
-- nothing, so we say it out loud once per match.
function T.Compat()
    local names = {
        "utilities.IsLocalPlayer",
        "utilities.GetAllAliveEntities",
        "utilities.GetEntityInventories",
        "utilities.ResetAbilityCooldowns",
        "utilities.CompleteConstruction",
        "utilities.ToggleFogOfWar",
        "actions.SetInventoryValue",
        "actions.ModifyBuff",
        "actions.ModifyTransactionHandling",
        "actions.HasInventoryOfType",
    }
    local missing, okCount = {}, 0
    for _, path in ipairs(names) do
        local node = _G
        for part in string.gmatch(path, "[^.]+") do
            if node == nil then break end
            local good, nxt = pcall(function() return node[part] end)
            if good then node = nxt else node = nil end
        end
        if node == nil then
            missing[#missing + 1] = path
        else
            okCount = okCount + 1
        end
    end
    T.compatMissing = #missing
    if #missing == 0 then
        T.compatLine = "compat: ok (" .. okCount .. " api)"
    else
        T.compatLine = "compat: missing " .. table.concat(missing, ", ")
    end
    log(T.compatLine)
end

-- which commander the trainer considers "mine" (diagnostics)
function T.Who()
    local p = getPlayer()
    if p == nil then
        log("player = none")
        return nil
    end
    log("player = " .. tostring(playerLabel))
    return p
end

function T.ResetCooldowns()
    forEachPlayerEntity(function(e) pcall(utilities.ResetAbilityCooldowns, e) end)
    log("cooldowns reset")
end

--------------------------------------------------------------------
-- tick drivers
--   Tick     : every 500 ms (resources / god / buffs)
--   FastTick : every 100 ms, only while a skill feature is on, so a cast is
--              usable again almost immediately instead of one tick later
--------------------------------------------------------------------
local tickNo = 0
local fastNo = 0

function T.Tick()
    tickNo = tickNo + 1

    if not assetsOk then
        loadAssets()
        return
    end

    if T.state.resources then
        safe(applyResources)
    end
    if T.state.god and tickNo % 3 == 0 then
        safe(applyGod)
    end
    if (T.state.build or T.state.damage) and tickNo % 5 == 0 then
        safe(applyBuffs)
    end
    if tickNo % 40 == 0 then
        safe(ensurePlayer)
    end
end

function T.FastTick()
    if not assetsOk then return end
    fastNo = fastNo + 1
    if T.state.energy then pcall(applyEnergy) end
    -- the army walk is the expensive one: 200 ms is still far below any ability
    -- cooldown / mana cost, and half the cost of doing it on every fast tick.
    -- Both per-entity features share that single walk.
    if (T.state.unitcd or T.state.mana) and fastNo % 2 == 0 then pcall(applyArmyTick) end
end

--------------------------------------------------------------------
-- bootstrap
--------------------------------------------------------------------
safe(loadAssets)
safe(T.Compat)
T._loaded = true
log("loaded v" .. T.version)
