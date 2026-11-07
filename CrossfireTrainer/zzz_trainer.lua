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

T.version = "1.13.2"

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
    movespeed = false,   -- friendly units move at x2 speed
    popcap    = false,   -- population cap raised past the skirmish limit
    speed     = 1.0,     -- game speed (handled by the plugin)
    amount    = 200000,  -- resource amount
}

local frame    = 0
local A        = {}      -- asset cache
local assetsOk = false
local lastMsg  = ""
local lastPrinted = ""

local function log(msg)
    local m = tostring(msg)
    lastMsg = m
    T.lastMsg = m                -- mirrored to the plugin log
    -- A repeating fault would otherwise print on every tick. The plugin already
    -- de-duplicates what it mirrors, but the engine's own print sink does not, and
    -- an unbounded log is what turned a broken feature into a multi-GB file.
    if m == lastPrinted then return end
    lastPrinted = m
    pcall(function() print("[Trainer] " .. m) end)
end

local function safe(fn, ...)
    local ok, err = pcall(fn, ...)
    if not ok then log("ERR: " .. tostring(err)) end
    return ok
end

--------------------------------------------------------------------
-- driver sections
--------------------------------------------------------------------
-- NOTE: this Lua runs with only Base/Table/String/Math/(utf8)/Debug from the
-- standard library -- the simulation builds its LuaEnv with
-- LuaStandardModules.EssentialPlus, which has no OS module. Referencing os.clock
-- therefore raises "attempt to index a nil value (global 'os')" AT LOAD TIME and
-- aborts the whole chunk, leaving Trainer as an empty stub (no Set, no Tick) and
-- every feature dead. Never touch os/io here; section timing is measured on the
-- C# side, where a real stopwatch is available.
local function step(label, fn, ...)
    local ok = safe(fn, ...)
    T.lastStep = label          -- cheapest possible breadcrumb, no clock needed
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
    -- MoverAttributes.MaximumSpeed, Multiply, 2 -> army walks twice as fast
    A.bSpeed = getAsset("ba_mod_speed_200pc",               "BuffAttributes")
    -- population: the cap itself lives on an inventory (ia_population) owned by the
    -- commander entity, so the buff has to be applied there (see applyPopCap)
    A.pop    = getAsset("ia_population",                    "InventoryAttributes")
    -- Inventories[ia_population].Maximum, Override, 200
    A.bPop   = getAsset("ba_pop_increase_200",              "BuffAttributes")

    assetsOk = (A.health ~= nil and A.bDmg ~= nil and A.mat ~= nil)
    if assetsOk then
        log("assets ready")
    else
        log(string.format("assets missing: mat=%s fuel=%s eng=%s mana=%s hp=%s shield=%s build=%s prod=%s dmg=%s speed=%s pop=%s",
            tostring(A.mat ~= nil), tostring(A.fuel ~= nil), tostring(A.energy ~= nil),
            tostring(A.mana ~= nil), tostring(A.health ~= nil), tostring(A.shield ~= nil),
            tostring(A.bBuild ~= nil), tostring(A.bProd ~= nil), tostring(A.bDmg ~= nil),
            tostring(A.bSpeed ~= nil), tostring(A.bPop ~= nil)))
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

-- Every feature used to call utilities.GetAllAliveEntities() for itself, and the
-- engine builds a fresh Lua table of *every* living entity (units, buildings,
-- projectiles) for each call. A match with a few hundred entities therefore
-- allocated a few thousand wrapped objects per second just to find a hundred
-- friendly units, which is what made the frame times spiky. The list is now
-- built at most once per driver call and shared by every feature in that call.
local armyBuf   = {}
local armyN     = 0
local armyKey   = -1

-- Declared here, assigned further down: armyList() needs it as an upvalue.
local tickNo    = 0

-- Rescans are throttled by tick instead of by call. GetAllAliveEntities() builds a
-- fresh Lua table and registers a wrapper object for every entity in the match, so
-- it is by far the most allocation-heavy call we make; running it on every driver
-- entry point (up to 9x/s) is what kept the Mono heap growing fast enough to force
-- a full GC every ~15 s, and each of those costs ~330 ms of frozen frame.
-- Two ticks = 0.5 s of staleness, which is exactly the delay new units already had.
local RESCAN_TICKS = 2

local function armyList()
    local k = math.floor(tickNo / RESCAN_TICKS)
    if armyKey == k then return armyN end
    armyKey = k

    local prev = armyN
    armyN = 0
    local p = getPlayer()
    if p == nil then
        for i = 1, prev do armyBuf[i] = nil end
        return 0
    end
    local list = utilities.GetAllAliveEntities()
    if list == nil then
        for i = 1, prev do armyBuf[i] = nil end
        return 0
    end

    local n = 0
    for _, e in pairs(list) do
        if e ~= nil then
            local ok, owner = pcall(utilities.GetCommanderOwnership, e)
            if ok and owner == p then
                n = n + 1
                armyBuf[n] = e
            end
        end
    end

    -- drop the tail the previous (longer) army left behind, so a unit that died
    -- can never be touched again by a later feature in the same scan
    for i = n + 1, prev do armyBuf[i] = nil end
    armyN = n
    return n
end

local function forEachPlayerEntity(fn)
    local n = armyList()
    for i = 1, n do pcall(fn, armyBuf[i]) end
    return n
end

-- A user action should see the match as it is now, not a scan up to 0.5 s old.
-- Only called from the public entry points, which are human-paced.
local function invalidateArmy() armyKey = -1 end

-- Reads one inventory back from an entity (amount + the entity's own cap).
-- HasInventoryOfType() must have confirmed the entity owns a store first: this
-- call dereferences it without a null check on the C# side.
local function inventoryAmount(e, asset)
    if e == nil or asset == nil then return nil, nil end
    local t = {}
    if not pcall(utilities.GetEntityInventories, e, t) then return nil, nil end
    for k, v in pairs(t) do
        if k == asset and v ~= nil then
            local cur = v.CurrentAmountInt
            if cur == nil then cur = v.CurrentAmount end
            local cap = v.ActiveCapInt
            if cap == nil then cap = v.ActiveCap end
            return cur, cap
        end
    end
    return nil, nil
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
    -- Writing an inventory posts a SetInventoryValueEvent into the simulation,
    -- so writing the same value every 500 ms is pure overhead. Read first.
    local changed = 0
    if A.mat then
        local cur = inventoryAmount(p, A.mat)
        if cur == nil or cur < v then
            actions.SetInventoryValue(p, A.mat, v)
            changed = changed + 1
        end
    end
    if A.fuel then
        local cur = inventoryAmount(p, A.fuel)
        if cur == nil or cur < v then
            actions.SetInventoryValue(p, A.fuel, v)
            changed = changed + 1
        end
    end
    if changed > 0 then
        log("resources set to " .. tostring(v))
    end
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

-- ModifyTransactionHandling is idempotent, so re-asserting it on the whole army
-- every 1.5 s was only overhead -- each call crosses the Lua/C# boundary and goes
-- through the transaction manager. Mark the units that already ignore removal and
-- only touch newcomers; a cheap re-assert pass still runs every 30 s in case the
-- flag is lost (a unit rebuilt from a blueprint starts clean).
local godOn = setmetatable({}, { __mode = "k" })

local function applyGod(reassert)
    local n = armyList()
    local touched = 0
    for i = 1, n do
        local e = armyBuf[i]
        if reassert or not godOn[e] then
            pcall(setInvuln, e, true)
            godOn[e] = true
            touched = touched + 1
        end
    end
    if touched > 0 then log("god: " .. touched .. " units") end
end

local function clearGod()
    local n = armyList()
    for i = 1, n do pcall(setInvuln, armyBuf[i], false) end
    for k in pairs(godOn) do godOn[k] = nil end
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
-- army move speed (x2)
--------------------------------------------------------------------
-- ba_mod_speed_200pc: MoverAttributes.MaximumSpeed, mode 2 (Multiply), value 2.
-- Multiply is the right mode here -- the unit keeps its own base speed and just
-- gets twice of it, so a scout stays faster than a tank. The asset carries no
-- ApplyToChildren, so units built after the toggle went on need the tick to pick
-- them up; the weak table keeps every unit at exactly one entry, because
-- BuffBucket.AddBuff does not de-duplicate.
local speedBuffed = setmetatable({}, { __mode = "k" })

local function applyMoveSpeed()
    if A.bSpeed == nil then return 0 end
    local added = 0
    forEachPlayerEntity(function(e)
        if not speedBuffed[e] then
            if pcall(actions.ModifyBuff, e, A.bSpeed, modificationOperation.Add) then
                speedBuffed[e] = true
                added = added + 1
            end
        end
    end)
    T.speedCount = added
    return added
end

local function stripMoveSpeed()
    for _ = 1, 3 do
        forEachPlayerEntity(function(e)
            if speedBuffed[e] then
                pcall(actions.ModifyBuff, e, A.bSpeed, modificationOperation.Remove)
            end
        end)
    end
    for k in pairs(speedBuffed) do speedBuffed[k] = nil end
end

--------------------------------------------------------------------
-- population cap
--------------------------------------------------------------------
-- Population is not a player setting: it is an inventory (ia_population) sitting
-- on the commander entity, and PopulationProcessor books every unit's
-- PopulationAttributes.Amount into it. Raising the cap therefore means buffing the
-- entity that owns that inventory -- and only that entity, since the buff path
-- Inventories[ia_population] resolves to nothing on ordinary units.
--
-- ba_pop_increase_200: Inventories[ia_population].Maximum, mode 3 (Override), 200.
-- Override wins over the smaller cap buffs the HQ chain applies, and 200 is far
-- above the 100-150 a skirmish normally ends at, so in practice the cap is gone.
local popBuffed = setmetatable({}, { __mode = "k" })

local function hasPopulation(e)
    if A.pop == nil then return false end
    local ok, has = pcall(actions.HasInventoryOfType, e, A.pop)
    return ok and has == true
end

local function applyPopCap()
    if A.bPop == nil then return 0 end
    local added = 0

    local function try(e)
        if e == nil or popBuffed[e] then return end
        if not hasPopulation(e) then return end
        if pcall(actions.ModifyBuff, e, A.bPop, modificationOperation.Add) then
            popBuffed[e] = true
            added = added + 1
        end
    end

    -- ia_population lives on the commander entity, and a commander does not always
    -- report itself as owned by itself -- so it can be missing from the army scan.
    -- Ask for it explicitly before walking the army.
    local p = getPlayer()
    try(p)
    forEachPlayerEntity(try)

    -- Last resort: if nothing matched, put the buff on the commander anyway. Buff
    -- paths that cannot be resolved are dropped by the engine, so this is harmless
    -- when the inventory really is absent.
    if added == 0 and p ~= nil and not popBuffed[p] then
        if pcall(actions.ModifyBuff, p, A.bPop, modificationOperation.Add) then
            popBuffed[p] = true
            added = 1
            T.popForced = true
        end
    end

    T.popCount = added
    T.popHasInv = hasPopulation(p) and true or false
    return added
end

local function stripPopCap()
    for _ = 1, 3 do
        forEachPlayerEntity(function(e)
            if popBuffed[e] then
                pcall(actions.ModifyBuff, e, A.bPop, modificationOperation.Remove)
            end
        end)
    end
    for k in pairs(popBuffed) do popBuffed[k] = nil end
end

--------------------------------------------------------------------
-- commander skill energy
--------------------------------------------------------------------
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

    local cur = inventoryAmount(p, A.energy)
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
    -- The cap differs per unit (100 on the ones defined in Attributes.zip), and
    -- SetInventoryValue clamps anyway, so "full" is decided by the entity's own
    -- ActiveCapInt: bar already full -> no write, no event, no recompute.
    local cur, cap = inventoryAmount(e, A.mana)
    if cur == nil then return false end
    if cap ~= nil and cur >= cap then return false end

    return pcall(actions.SetInventoryValue, e, A.mana, MANA_FULL)
end

-- One walk serves every per-entity feature, so turning both on costs the same as
-- turning one on. Features that are off are simply skipped per entity.
local function applyArmyTick()
    local n = armyList()

    if T.state.unitcd then
        for i = 1, n do pcall(resetEntityCooldowns, armyBuf[i]) end
    end

    if T.state.mana then
        local fills = 0
        for i = 1, n do
            if refillMana(armyBuf[i]) then fills = fills + 1 end
        end
        T.manaFills = fills
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
--
-- Toggling the fog only changes what is DRAWN; it does not make enemy units
-- attackable from across the map, and that is the documented behaviour we ship.
local function setFog(hidden)
    local p = getPlayer()
    if p == nil then return false end
    local fn = utilities.ToggleFogOfWar
    if fn == nil then return false end
    return pcall(fn, not hidden, p)
end

local function applyVision()
    if setFog(true) then
        log("vision: on")
    else
        log("vision: fog toggle unsupported on this build")
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
    invalidateArmy()

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

    elseif key == "movespeed" then
        local was = T.state.movespeed
        T.state.movespeed = value and true or false
        if T.state.movespeed then
            applyMoveSpeed()
        elseif was then
            stripMoveSpeed()
        end

    elseif key == "popcap" then
        local was = T.state.popcap
        T.state.popcap = value and true or false
        if T.state.popcap then
            applyPopCap()
        elseif was then
            stripPopCap()
        end

    elseif key == "speed" then
        T.state.speed = tonumber(value) or 1.0

    elseif key == "amount" then
        T.state.amount = math.floor(tonumber(value) or 200000)
        if T.state.resources then applyResources() end
    end

    T.lastAction = tostring(key)
    -- These two report their reach, because "switched on but nothing happened" is
    -- the failure mode that matters here: 0 units means the buff never landed.
    if key == "movespeed" then
        log(string.format("movespeed: %s (%d units)", tostring(T.state.movespeed), T.speedCount or 0))
    elseif key == "popcap" then
        log(string.format("popcap: %s (%d commanders, player has ia_population=%s%s)",
            tostring(T.state.popcap), T.popCount or 0,
            tostring(T.popHasInv == true), T.popForced and ", forced" or ""))
    else
        log(string.format("set %s = %s", tostring(key), tostring(T.state[key])))
    end
    return T.state[key]
end

function T.Toggle(key)
    local cur = T.state[key]
    if type(cur) == "boolean" then return T.Set(key, not cur) end
    return cur
end

function T.Status()
    log(string.format("res=%s god=%s build=%s dmg=%s eng=%s mana=%s unitcd=%s vis=%s spd=%s pop=%s speed=%.1f",
        tostring(T.state.resources), tostring(T.state.god),
        tostring(T.state.build), tostring(T.state.damage),
        tostring(T.state.energy), tostring(T.state.mana),
        tostring(T.state.unitcd), tostring(T.state.vision),
        tostring(T.state.movespeed), tostring(T.state.popcap), T.state.speed))
    return T.state
end

-- instantly finish everything currently under construction
function T.InstantBuild()
    invalidateArmy()
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
    invalidateArmy()
    forEachPlayerEntity(function(e) pcall(utilities.ResetAbilityCooldowns, e) end)
    log("cooldowns reset")
end

--------------------------------------------------------------------
-- tick drivers
--   Tick     : every 500 ms (resources / god / buffs)
--   FastTick : every 100 ms, only while a skill feature is on, so a cast is
--              usable again almost immediately instead of one tick later
--------------------------------------------------------------------
local fastNo = 0

function T.Tick()
    tickNo = tickNo + 1

    if not assetsOk then
        loadAssets()
        return
    end


    if T.state.resources then
        step("resources", applyResources)
    end
    if T.state.god and tickNo % 3 == 0 then
        step("god", applyGod, tickNo % 60 == 0)
    end
    if (T.state.build or T.state.damage) and tickNo % 5 == 0 then
        step("buffs", applyBuffs)
    end
    -- Units built after the toggle went on are not covered by ApplyToChildren on
    -- these two assets, and a rebuilt commander brings a fresh ia_population
    -- inventory, so the same slow pass picks both kinds of newcomer up.
    if (T.state.movespeed or T.state.popcap) and tickNo % 5 == 0 then
        if T.state.movespeed then step("movespeed", applyMoveSpeed) end
        if T.state.popcap then step("popcap", applyPopCap) end
    end
    if tickNo % 40 == 0 then
        step("player", ensurePlayer)
    end
end

function T.FastTick()
    if not assetsOk then return end
    fastNo = fastNo + 1
    if T.state.energy then step("energy", applyEnergy) end
    -- the army walk is the expensive one: 200 ms is still far below any ability
    -- cooldown / mana cost, and half the cost of doing it on every fast tick.
    -- Both per-entity features share that single walk.
    if (T.state.unitcd or T.state.mana) and fastNo % 2 == 0 then
            step("army", applyArmyTick)
    end
end

--------------------------------------------------------------------
-- bootstrap
--------------------------------------------------------------------
safe(loadAssets)
safe(T.Compat)
T._loaded = true
-- Read by the plugin to tell "loaded" from "the chunk died half-way through".
-- A stub table still has .version (it is set near the top), so version alone is
-- not proof of a working script.
T.loadedFlag = "ok"
log("loaded v" .. T.version)
