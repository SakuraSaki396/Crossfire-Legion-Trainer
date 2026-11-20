# Crossfire Legion 修改器 v2.0.0

针对 **Crossfire: Legion (Demo)**（Unity + Carbon 引擎 + Mono）的单人修改器安装器。
**v2.0.0 是全新图形界面版本**，内置修改器本体仍是 **v1.16.3** 的 BepInEx 插件 + Lua 扩展（共 24 个文件），
二者**功能一致，只是界面不同**。如本安装器用不了，可到 GitHub 下载 v1.16.3 版本。

以 BepInEx 插件 + Lua 脚本扩展实现，不硬改内存地址、不改游戏本体文件，卸载即恢复原版。
Steam / Epic / GOG / Xbox / 独立版安装均可使用。

> ⚠️ **只在单人 / 离线 AI 遭遇战 / 战役里用。** 联机对局请勿开启；此类工具通常违反游戏服务条款，风险自负。
> 本软件不包含任何游戏文件，使用时需要你自备正版游戏。

## v2.0.0 更新内容

1. 本安装器为 v2.0 版本，如用不了请到 github 上下载 v1.16.3 版本。二者功能一致，只是界面不同。
2. 游戏内 F4 打开/关闭修改器面板。鼠标操作。
3. 功能支持：资源修改 / 友方无敌 / 建造及生产加速 / 超级攻击力 / 能量无限 / 技能无冷却 / 全图视野 / 移动速度翻倍 / 一键 200 人口 / 游戏速度修改 / 一键完成建造 / 经验值修改 / 卡牌一键升级。

相比 v1.16.3，安装器改为 WPF 图形界面（原创封面与图标）、**单文件自包含 exe**、
逐文件 sha256 校验与失败自动回滚，并内置十章适配说明。

## 功能

| 功能 | 说明 |
|---|---|
| 设定资源（不可逆） | 每 0.5 秒把材料 / 燃料拉回设定值（10k / 50k / 200k / 999k 可切）。**不可逆**：开启后原数值不会被保存，关闭开关也不会还原，只能重开一局或读档恢复 |
| 友方无敌 / 锁血 | 对全部友方实体设 `IgnoreRemove` 交易模式，伤害无法扣除 HP / 护盾，每 1.5 秒刷新以覆盖新造单位 |
| 建造 / 生产加速 | 复用官方 buff `ba_campaign_production_time_fast`（ConstructionRate ×50）与 `ba_Production_set_to_2`（生产 1 秒） |
| 超级攻击力 | 官方 buff `ba_max_attack`，进入对局时把它的伤害值从自带的 1,000,000 改成 1,000（够秒任何单位，数值也不至于离谱） |
| 游戏速度 | 改引擎自己的 `SimController.TimeScale` + `Time.timeScale`，面板上是 0.5×–5× 的拖动滑条 |
| 立即完成建造 | `utilities.CompleteConstruction` |
| 指挥官能量无限 | 每 100 ms 读回 `ia_resource3`（上限 100），掉了就补满 |
| 兵种能量无限 | 兵种技能用的是另一条库存 `ia_mana`（面板上的紫条，每个单位各自一份、上限 100），每 200 ms 对**我方每个实体**写满；引擎按各自上限截断，所以不用管是哪个兵种 |
| 技能无冷却（含兵种） | 每 200 ms 对**我方每一个实体**（含指挥官本人）调 `utilities.ResetAbilityCooldowns(entity)`，指挥官技能与各兵种的主动技能冷却一起清 |
| 全图视野 | 调引擎未公开的 `utilities.ToggleFogOfWar(false, player)` 关掉战争迷雾，整张地图（含小地图）直接可见；再点一次恢复 |
| 移动速度翻倍 | 官方 buff `ba_mod_speed_200pc`（`MoverAttributes.MaximumSpeed`，乘法 ×2），我方每个单位各自翻倍、兵种之间原有快慢关系不变；新造单位 0.5 秒内自动补上，关闭后摘除 |
| 一键200人口 | 官方 buff `ba_pop_increase_200`（把 `Inventories[ia_population].Maximum` 覆盖成 200）；人口是挂在指挥官实体上的库存，所以只对有我方 `ia_population` 的实体生效，敌方不受影响 |
| 经验值直接编辑（≤5万） | 点面板上的「经验 N」栏弹出数字键盘，**会预填你当前的数值**，改完点「确定修改」直接把余额**替换**成新值（不是累加）；右侧「设为 5 万」可一键拉满。范围 0 - 50000，输入超过按 50000 处理，**大厅里就能用，不用打局**。经验值存在统计项 `isa_progressionxp_possesion_current`（`onlinedatacache\playerstats.json` 里那条，打一局胜利 +50）。游戏每次完成统计请求都会用缓存快照整体重建内存数据，直接改那条会被抹回 0，所以改由插件自己的余额账户托管：叠加到卡牌界面读取的 `ProgressionTotal` 上，升级扣费时对等扣减，余额存在 `BepInEx\config\CrossfireTrainer.xp.txt`，重启游戏不清零；面板底部实时显示当前持有量 |
| 卡牌全部升到20级 | 卡牌等级不是独立字段，而是**由每张卡的经验统计值对照兵种进阶表算出来的**（`rpfa_infantry` 12707 / `rpfa_air`·`rpfa_vehicle`·`rpfa_commander` 19061，共 20 档）。按钮把档案里所有卡的 `ProgressionXP` 统计一次性推到 19061，再走游戏自己的 `IStatisticsService.UpdatePlayerStats` 写入（同一路径会 `CachePlayerStats()` 落盘并触发 `OnPlayerStatisticsUpdated`），所以点完当场全部 20 级、不用重启。需要先打开一次卡牌界面，让插件拿到 stat-id → 卡牌的映射 |
| 我方自动判定 | 用 `utilities.IsLocalPlayer` 找本地玩家所在槽位，与排在第几号位无关；状态栏显示 `P6` 这类结果，`P1*` 表示退回兜底 |

> **只有一个快捷键：`F4` 显示 / 隐藏面板。**
> 其余功能全部用鼠标点面板，不再占用游戏自己的 F 键。

## 下载与安装

1. 解压本 zip，得到 `Crossfire Legion 修改器v2.0.0.exe`（约 1 MB）。
   **Win10 1809+ / Win11 自带 .NET Framework 4.8，不需要额外安装**；Win7 SP1 / 8.1 请先手动安装 .NET Framework 4.8，否则双击毫无反应。
2. 双击运行，它会自动搜索常见安装位置（Steam 库 / Epic / GOG / Xbox / 自定义目录均可），
   找不到就点「更改」手动选（目录里应有 `Crossfire_Legion.exe`，或有 Carbon 引擎的 `Data\Scripts` 布局）。
   **不依赖 Steam 客户端、注册表、libraryfolders.vdf 或 appmanifest，也不校验 buildid。**
3. 点「开始安装」，等它显示「修改器已安装」。
4. 用你下载游戏的平台正常启动游戏，进入对局后按 `F4` 打开 / 隐藏面板。

游戏正在运行时不能安装（`winhttp.dll` 会被占用），安装器会提示结束游戏后重试。
提示没有写入权限时，右键 exe →「以管理员身份运行」。

### 单文件，真的只有一个 exe

config、封面、24 个修改器文件（共 32 项）都压进了 exe。启动时会：

- exe 旁边**有** `Assets\`、`config\`、`payload\` → 直接用外面那份（方便离线排查 / 改配置）；
- exe 旁边**没有** → 自动解压到 `%LOCALAPPDATA%\CFLTrainer\Runtime\v2.0.0\` 再从那里加载。

所以只拷一个 exe 到 U 盘 / 网盘也能跑，游戏卸载重装后重新跑一次即可一键恢复。
每次启动会重新解压，但 `config/ui.json` 在目标已存在时跳过覆盖，你选的深浅主题不会被重置。

### 适配说明就在 exe 里

右上角「**适配说明…**」弹出完整十章说明：适配的版本资料（仅供参考、不参与判定）、
“版本对不上时哪些功能先失效”的属性 / 接口对照、日志关键字、四种失效表现、更新后怎么办、
非 Steam 平台说明、安装前检查与排查步骤。点「复制全文」可把说明带走。

安装器**只按目录结构 + 24 个文件的 sha256 判定**是否可安装；不读注册表 / appmanifest / buildid，
不同平台的版本标识不同或读不到 buildid 都不算不兼容。游戏内 14 项功能自检看面板状态栏。
这份说明只存在于 exe 内部，不会往游戏目录写任何说明文件。

命令行模式（排查用）：

```
"Crossfire Legion 修改器v2.0.0.exe" --detect     把目录发现结果写到 %TEMP%\cfl-trainer-detect.txt
"Crossfire Legion 修改器v2.0.0.exe" --selftest   临时目录里跑安装 / 校验 / 回滚 / 卸载，结果写 selftest.log
"Crossfire Legion 修改器v2.0.0.exe" --render     生成 7 张界面截图到 render\
```

`--selftest` 完全不碰真实游戏目录。本次发版结果：**RESULT: PASS**
（24/24 文件校验通过、故意写坏哈希后回滚恢复 24/24、卸载残留 0）。
单文件模式下 `selftest.log` 和 `render\` 写在 `%LOCALAPPDATA%\CFLTrainer\Runtime\v2.0.0\`；
文件夹模式下写在 exe 旁边。安装器运行日志在 `%LOCALAPPDATA%\CFLTrainer\cfl-trainer-<时间>.log`。

## 卸载

双击 exe →「卸载修改器」→「确认卸载」。会：

- 删除安装时新增的 24 个文件；
- 清掉 `BepInEx\` 运行时目录（日志、经验钱包、缓存等清单外的产物）、
  `.cfl-trainer-state.json` 和 `.cfl-trainer-backup\` 备份目录。

游戏本体不受影响。通过平台的「验证 / 修复游戏文件」也能恢复 Lua 部分。

> 如果游戏目录里还装着**旧版单文件安装器**放过的修改器，直接用本安装器点「开始安装」即可，
> 不需要先卸旧的：这 24 个文件全是修改器新增的，安装不做备份，卸载也会直接删干净。

## 实现原理

1. **注入**：BepInEx 5.4（Mono + winhttp / Doorstop），插件随游戏启动加载。
2. **拿 Lua 环境**：Harmony 挂在 `Carbon.Simulation.SimScriptProcessor.OnInitialize`，
   反射取私有字段 `mLuaEnv`（LuaEnv）与 `mFrameController`（`SimController`）。
3. **线程安全**：Lua 只能在模拟线程调用，所有指令排队，在 `SimScriptProcessor.DoProcess` 的 Postfix 里执行。
4. **游戏速度**：`SimController.TimeScale` 是引擎自带接口（改 `"SimController Tick"` 协程周期），
   原版大厅的 Game Speed 选项走的就是这条路。
5. **建造 / 攻击 / 无敌**：全部复用游戏官方遗留的 Buff 与库存属性（可在 `Data/Attributes.zip` 里查到定义），
   只做 `ModifyBuff` / `SetInventoryValue`，不碰内存地址，所以游戏小版本更新通常仍然可用。

## 已知限制

- **Lua 文件必须是纯 ASCII**：引擎用 `Encoding.ASCII.GetByteCount()` 算长度却按 UTF-8 喂给 Lua，
  中文注释会让缓冲区被截断并报 `near <eof>`。
- 不能依赖 `events.AddListener(eventType.EveryFrame, ...)`：扩展加载时机注册的监听不会触发，改由 C# 侧定时驱动。
- 无敌是「伤害无法扣除」，不是把血条锁满；血条会停在当前值不再下降。
- 所有功能只作用于**本地玩家**一方，不碰 AI。
- 游戏大版本更新后，属性资源名或脚本接口可能变化；安装器与面板自检会提示是哪一项失效。

## 常见问题

- **游戏里不出现面板**：先看 `游戏目录\BepInEx\LogOutput.log`。
  没有这个文件 = 注入没生效，重新点一次安装；杀毒软件有时会把 `winhttp.dll`（Doorstop 注入器）当木马清掉，
  给游戏目录加白名单后重装即可。
- **面板出来了但某项没反应**：安装器里点「适配说明…」，对照里面列出的第 2、3、4 类原因，
  或把 `LogOutput.log` 里 `compat:` / `assets missing:` 两行发给作者。
- **游戏更新后**：先正常启动一次看面板是否出现；没出现就重新跑安装器；
  出现了但个别无效，把日志里 `compat:` / `assets missing:` 两行发来按日志补。
- **提示没有写入权限**：右键 exe →「以管理员身份运行」。
- **经验钱包**：`BepInEx\config\CrossfireTrainer.xp.txt` 是本地插件文件，**不在 Steam 账号同步范围**；
  卡牌等级、经验统计项和战役进度由游戏在线统计服务同步，卸载游戏后不会丢。

## 许可

本软件**仅供交流与学习**，为改善软件功能、提升用户体验等合法目的而制作。
本软件为**专有软件，保留所有权利**（见 [LICENSE](LICENSE)）。仅授权取得本软件的本人用于个人、非商业用途；
**禁止私自传播、修改、出售、再授权**，也禁止反编译、逆向工程、移除版权标识，或以任何形式打包进其他项目与合集。
完整条款见 [LICENSE](LICENSE)。

安装器内嵌的 BepInEx / Doorstop / HarmonyX / MonoMod / Mono.Cecil
遵循各自的许可，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
游戏本体与游戏资源版权归其开发商 / 发行商所有。
