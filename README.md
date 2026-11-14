# 境界碾压击杀回血修复（ZaohuaKillHealFix）

《造化仙缘》BepInEx 插件：用「境界碾压」跳过战斗时，把功法/法宝的**击杀回血**补回来。

- 下载：见 [Releases](https://github.com/Jack-TJY/ZaohuaKillHealFix/releases)（`ZaohuaKillHealFix.dll` 或压缩包）
- 源码：<https://github.com/Jack-TJY/ZaohuaKillHealFix>
- 许可：[MIT](LICENSE)

## 问题

游戏里带「击杀时回复气血」的效果（典型是功法**夺命杀诀**，`TbArtCfg 992189`，效果 `10054 × 100`，每次击杀回 100 气血）只在实际战斗里生效：

- 正常战斗：敌人死亡 → `BattleObject.DoDie` → `DoBattleEvent(EffTimeEnum.Kill)` → `TbBattleModifierImpl` 触发效果 → 回血。
- 境界碾压跳过战斗：只调用 `BsBattleImpl.DoDefeat(npcStoId)` 直接结算，没有 `BattleObject`，也没有 Kill 事件，所以回血被吞掉。

`境界碾压，跳过战斗` 的两条路径都会中招：

1. 弹「境界碾压」面板后点「一键击败」：`QuickBattlePanel.BtnQuickBattleFunction`；
2. 设置里开了「快速战斗」时地图上自动跳过：`BsScanImpl.ScanMapEvent`。

两条路径都会对每个敌人调用一次 `BsBattleImpl.DoDefeat(int)`，而真实战斗里敌人死亡时也会调用它（`BattleEnemy.DieAnimator`）。本插件就以 `DoDefeat` 为挂钩点。

## 修法

`DoDefeat` 前缀里：

1. 判断当前是不是真实战斗（`BsBattleImpl.IsInBattle()` 或 `BattleController.Players` 非空）。是则直接返回，真实战斗仍由游戏自己结算，避免重复加血。
2. 从 `BsSaveDataImpl.NowActor.equipEffectRecordStoList` 里挑出挂在主角（`npcStoId 10000`）身上、且已装备的效果；触发时机按战斗里的真实判定解析 `condition` 里的 `JudEffTimeInBattle,15`（不是只看 `TbEquipEffectCfg.effTime`，两者可能不一致）。
3. 用游戏自己的 `BsEquipEffectImpl.GetFinalEff` 展开倍率，再用 `GenericMethods.GetEffectListByStr` 解析。
4. 只对可安全复算的回血命令求和：`UpdateNowHp`、`UpdateHp`、`UpdateHpPer`；目标必须是自己/含自己。效果上若还有击杀以外的额外条件（血量、种族等）则保守跳过，避免多加。
5. 调 `TbPlayerImpl.UpdateHp` 入账（会按当前气血上限截断），可选弹 HUD「气血：+N」。

因此不只是「夺命杀诀」：任何走「挂在主角身上 + 击杀时机 + 回血命令」的功法/法宝/mod 配置都会一并修复。

## 安装

1. 游戏已装好 BepInEx 5（x64）。
2. 把 `ZaohuaKillHealFix.dll` 放进 `游戏目录\BepInEx\plugins\`。
3. 启动游戏，`BepInEx\LogOutput.log` 里应出现：
   - `补丁已应用：BsBattleImpl.DoDefeat(int) -> 境界碾压跳过战斗时补发击杀回血。`
   - 配置加载完后还会自检一次：`自检通过：夺命杀诀(992189) 每次击杀回复 100 气血；展开后 [UpdateNowHp,2,100,#npcTmpId#,#SelectedNpcId#,1@JudEffTimeInBattle,15,-1,0,0]。`
4. 装备「夺命杀诀」等带击杀回血的功法，在境界碾压跳过战斗时即可看到回血和日志。

## 配置

`BepInEx\config\zaohua.killhealfix.cfg`：

| 项 | 默认 | 说明 |
| --- | --- | --- |
| Enabled | true | 开关 |
| HudMessage | true | 补血时在左上角 HUD 显示「气血：+N」 |
| Verbose | false | 详细日志：列出被跳过的击杀效果和原因 |
| SelfTest | true | 配置加载后自检「夺命杀诀 = 100/击杀」，只写日志 |
| DebugKey | F7 | 按 F7 把当前生效的击杀回血效果写进日志/HUD，用来确认功法有没有被识别 |

排查时先按 F7：如果日志里没有「击杀回血效果」，说明当前没有装备带击杀回血的功法/法宝，或该效果没进 `equipEffectRecordStoList`。

## 验证

- 离线单测：`dotnet run --project tests -c Release`（`KillEffectMath` 的 12 个用例，覆盖加血/设值/目标/额外条件等）。
- 真实配置展开（用游戏自己的 `BsEquipEffectImpl.GetFinalEff` 跑过）：夺命杀诀 `equipEff = 10054,100,0,1` → `UpdateNowHp,2,100,...,1@JudEffTimeInBattle,15,-1,0,0` → 100/击杀。
- 存档数据核对：存档 `equipEffectRecordStoList` 中该效果记录形如 `{"fromUpId":"1,<npcStoId>,992189","beUpId":"8,<npcStoId>,0","equipEffectCfgId":10054,"multiplier":100}`；主角的 `npcStoId` 为 10000。
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

- 只在「跳过战斗」时补**回血**。击杀回精力、击杀加气血上限、击杀叠状态等效果属于战斗内数值/临时状态，跳过战斗后没有对应载体，本插件不模拟。
- 真实战斗不干预（游戏自己会触发击杀效果）。
- 回血在 `DoDefeat` 时立即入账，按当前气血上限截断；满血时不会溢出。

## 许可

MIT
