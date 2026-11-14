# 境界碾压击杀回血 / 气血上限修复（ZaohuaKillHealFix）

《造化仙缘》BepInEx 插件：用「境界碾压」跳过战斗时，把功法/法宝的**击杀回血**和**击杀提升气血上限**补回来。

- 下载：见 [Releases](https://github.com/Jack-TJY/ZaohuaKillHealFix/releases)（`ZaohuaKillHealFix.dll` 或压缩包）
- 源码：<https://github.com/Jack-TJY/ZaohuaKillHealFix>
- 许可：[MIT](LICENSE)

## 问题

游戏里带「击杀时回复气血 / 提升气血上限」的效果只在实际战斗里生效：

- 正常战斗：敌人死亡 → `BattleObject.DoDie` → `DoBattleEvent(EffTimeEnum.Kill)` → `TbBattleModifierImpl` 触发效果。
- 境界碾压跳过战斗：只调用 `BsBattleImpl.DoDefeat(npcStoId)` 直接结算，没有 `BattleObject`，也没有 Kill 事件，所以整条效果链被吞掉。

受影响的典型功法/法宝：

| 来源 | 配置链 | 效果 |
| --- | --- | --- |
| 夺命杀诀 | `TbArtCfg 992189` → `10054 × 100` | 每次击杀回复 100 气血 |
| 万古青帝典（相邻术法） | 绘卷状态 `80056` → `10041` | 每次击杀气血上限 +1（记录挂在具体术法上时无法判定击杀招式，见「已知边界」） |
| 血神典 | `TbArtCfg 605` → `100400` → `100401` | 每次击杀气血上限 +5，最多 20 层 |
| 赤血魔功 | `TbArtCfg 992036` → `101300` → `101301` | 每次击杀气血上限 +10，最多 20 层 |
| 血煞真解 | `TbArtCfg 995208` → `101302` → `101303` | 每次击杀气血上限 +500，最多 20 层 |

`境界碾压，跳过战斗` 的两条路径都会中招：

1. 弹「境界碾压」面板后点「一键击败」：`QuickBattlePanel.BtnQuickBattleFunction`；
2. 设置里开了「快速战斗」时地图上自动跳过：`BsScanImpl.ScanMapEvent`。

两条路径都会对每个敌人调用一次 `BsBattleImpl.DoDefeat(int)`，而真实战斗里敌人死亡时也会调用它（`BattleEnemy.DieAnimator`）。本插件就以 `DoDefeat` 为挂钩点。

## 修法

`DoDefeat` 前缀里：

1. 判断当前是不是真实战斗（`BsBattleImpl.IsInBattle()` 或 `BattleController.Players` 非空）。是则直接返回，真实战斗仍由游戏自己结算，避免重复加减。
2. 从 `BsSaveDataImpl.NowActor.equipEffectRecordStoList` 里挑出挂在主角（`npcStoId 10000`）身上、且已装备的效果；触发时机按战斗里的真实判定解析 `condition` 里的 `JudEffTimeInBattle,15`（不是只看 `TbEquipEffectCfg.effTime`，两者可能不一致）。
3. 用游戏自己的 `BsEquipEffectImpl.GetFinalEff` 展开倍率，再用 `GenericMethods.GetEffectListByStr` 解析。
4. 按命令分别补算：
   - `UpdateNowHp` / `UpdateHp` / `UpdateHpPer` → `TbPlayerImpl.UpdateHp`，按当前上限截断；
   - `UpdateHpLimit`（且第二个参数不是 1）→ `BsUpdateImpl.UpdateHpLimit`，永久加气血上限并回等量气血；
   - `UpdateEquipEffectSto` → 若叠层目标最终是永久 `UpdateHpLimit`（血神典/赤血魔功/血煞真解），调游戏自己的 `UpdateEquipEffectSto` 加一层，由游戏的 `maxMultiplier` 控制 20 层封顶并执行嵌套上限效果。
5. 效果上若还有击杀以外的额外条件（血量、种族等）则保守跳过，避免多加。

记录筛选只接受「任何击杀都会触发」的承载类型：主角自身、功法、技能、装备、套装。`MagicId/PackStoId` 记录只在「用对应术法/法宝击杀」时才触发，碾压跳过没有击杀招式可匹配，因此不补（例如万古青帝典记录挂在相邻术法上时会被跳过；挂在功法/技能上时正常补）。

因此不只是上面表里的功法：任何走「挂在主角身上 + 击杀时机 + 回血/永久上限命令」的功法、法宝或 mod 配置都会一并修复。

## 安装

1. 游戏已装好 BepInEx 5（x64）。
2. 把 `ZaohuaKillHealFix.dll` 放进 `游戏目录\BepInEx\plugins\`。
3. 启动游戏，`BepInEx\LogOutput.log` 里应出现：
   - `补丁已应用：BsBattleImpl.DoDefeat(int) -> 境界碾压跳过战斗时补发击杀回血/气血上限。`
   - 配置加载完后还会自检一次，例如：`自检通过：夺命杀诀 击杀回血 100/次；气血上限 10041 +1、100400→100401 +5 叠层解析正常。`
4. 装备「夺命杀诀 / 血神典 / 赤血魔功 / 血煞真解」等功法，在境界碾压跳过战斗时即可看到回血、上限提升和日志。

## 配置

`BepInEx\config\zaohua.killhealfix.cfg`：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| Enabled | true | 开关 |
| HudMessage | true | 补当前气血时在左上角 HUD 显示「气血：+N」；上限提升由游戏自己的 HUD 提示 |
| Verbose | false | 详细日志：列出被跳过的击杀效果和原因 |
| SelfTest | true | 配置加载后自检「夺命杀诀 +100/击杀、10041 +1、100400→100401 叠层」，只写日志 |
| DebugKey | F7 | 按 F7 把当前生效的击杀回血/上限效果写进日志/HUD，用来确认功法有没有被识别 |

排查时先按 F7：如果日志里没有「击杀效果」，说明当前没有装备带击杀回血/上限的功法、法宝，或该效果没进 `equipEffectRecordStoList`。

## 验证

- 离线单测：`dotnet run --project tests -c Release`（21 个用例，覆盖加血/设值/目标/额外条件、永久上限/临时上限/非法参数等）。
- 真实配置展开（用游戏自己的 `BsEquipEffectImpl.GetFinalEff` 跑过）：
  - 夺命杀诀 `equipEff = 10054,100,0,1` → `UpdateNowHp,2,100,...,1@JudEffTimeInBattle,15,-1,0,0` → 100/击杀；
  - 血神典 `equipEff = 100400,1,0,0` → `UpdateEquipEffectSto,1,10000,605,1,10000,605,1,0,1,1,100401,0,1@...`，13 个参数正确，嵌套 `100401 = UpdateHpLimit,5` → 5/层。
- 存档数据核对：击杀回血记录形如 `{"fromUpId":"1,<npcStoId>,992189","beUpId":"8,<npcStoId>,0","equipEffectCfgId":10054,"multiplier":100}`；血神典/赤血魔功/血煞真解的上限叠层记录由 `100400/101300/101302` 动态添加，`maxMultiplier=20`。
- 游戏内自检：本插件启动后打出的 `自检通过` 日志（见上）。

## 构建

`lib/` 下放以下 dll（不随仓库分发，从游戏目录复制）：

- `Zaohua_Data\Managed\`：`Assembly-CSharp.dll`、`UnityEngine.dll`、`UnityEngine.CoreModule.dll`
- `BepInEx\core\`：`BepInEx.dll`、`0Harmony.dll`

```
dotnet build src -c Release
dotnet run --project tests -c Release
```

> 游戏更新后若 `Assembly-CSharp.dll` 里的方法签名变化，需要按新 dll 重新编译；补丁目标找不到时日志会明确报错，不会影响游戏启动。

## 已知边界

- 只补**回血**和**永久气血上限**。击杀回精力、击杀叠攻击/护甲、夺基大法的偷天赋等效果属于战斗内数值或另一套机制，跳过战斗后没有对应载体，本插件不模拟。
- `MagicId/PackStoId` 承载的击杀效果（只在用对应术法/法宝击杀时触发）不补，避免碾压时凭空选中一个击杀招式。
- 真实战斗不干预（游戏自己会触发击杀效果）。
- 回血在 `DoDefeat` 时立即入账，按当前气血上限截断；上限提升由游戏的 `UpdateHpLimit` 永久写入并同步当前气血。满血时不会溢出。

## 许可

MIT
