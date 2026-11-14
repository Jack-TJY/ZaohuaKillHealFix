using System;
using System.Collections.Generic;
using System.Text;

namespace ZaohuaKillHealFix
{
    /// <summary>
    /// 修为/境界碾压跳过战斗时，把玩家身上“击杀时回复气血 / 提升气血上限”的效果补算回来。
    ///
    /// 正常战斗里，击杀由 BattleObject.DoDie -> DoBattleEvent(EffTimeEnum.Kill) 触发，
    /// 装备/功法效果（condition 里的 JudEffTimeInBattle,15）会走 TbBattleModifierImpl 结算。
    /// 碾压跳过战斗时只调用 BsBattleImpl.DoDefeat，没有战斗对象，所以这条链不会执行。
    /// 这里在 DoDefeat 前按同一套配置复算：
    ///   1. UpdateNowHp / UpdateHp / UpdateHpPer -> TbPlayerImpl.UpdateHp
    ///   2. UpdateHpLimit -> TbNpcImpl.UpdateHpLimit（永久上限）
    ///   3. UpdateEquipEffectSto -> 若叠层效果最终是 UpdateHpLimit，走游戏自己的叠层接口
    ///      （血神典 100400→100401 +5、赤血魔功 101300→101301 +10、血煞真解 101302→101303 +500）
    /// </summary>
    internal static class KillHeal
    {
        private const long PlayerNpcStoId = 10000L;
        private const int PlayerNpcStoIdInt = 10000;

        /// <summary>判断当前是不是真的在战斗场景里（真战斗由游戏自己结算击杀效果，不能重复加减）。</summary>
        internal static bool IsRealBattleActive(BsBattleImpl battle)
        {
            try
            {
                if (battle != null && battle.IsInBattle())
                {
                    return true;
                }

                var controller = Singleton<BattleController>.Instance;
                if (controller != null)
                {
                    var players = controller.Players;
                    if (players != null && players.Count > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning("判断战斗状态失败，按“非战斗”处理：" + exception.Message);
            }

            return false;
        }

        /// <summary>每次 DoDefeat 调用一次：相当于该敌人被击杀一次。</summary>
        internal static void OnEnemyDefeated(BsBattleImpl battle, int npcStoId)
        {
            if (!Plugin.Enabled.Value)
            {
                return;
            }

            try
            {
                if (IsRealBattleActive(battle))
                {
                    return;
                }

                var actor = BsSaveDataImpl.NowActor;
                if (actor == null || actor.equipEffectRecordStoList == null || actor.equipEffectRecordStoList.Count == 0)
                {
                    return;
                }

                var player = Singleton<TbPlayerImpl>.Instance;
                var mainNpcSto = Singleton<TbNpcImpl>.Instance.GetNpcSto(PlayerNpcStoId);
                var data = Singleton<TbDataImpl>.Instance;
                var equip = Singleton<BsEquipEffectImpl>.Instance;
                if (player == null || mainNpcSto == null || data == null || equip == null)
                {
                    return;
                }

                int beforeHp = player.GetPlayerStoHpNow();
                int beforeMaxHp = mainNpcSto.hp;
                int healForHud = 0;
                var details = new List<string>();

                // UpdateEquipEffectSto 会往列表里加叠层记录，先做快照，避免边遍历边改
                var records = new List<TbEquipEffectRecordSto>(actor.equipEffectRecordStoList);
                records.Sort((a, b) => GetPriority(data, a).CompareTo(GetPriority(data, b)));

                for (int i = 0; i < records.Count; i++)
                {
                    TbEquipEffectRecordSto record = records[i];
                    if (!IsAnyKillEffectRecord(record))
                    {
                        continue;
                    }

                    TbEquipEffectCfg cfg = data.GetEquipEffCfg(record.equipEffectCfgId);
                    if (cfg == null || string.IsNullOrEmpty(cfg.condition) || !IsKillTrigger(cfg))
                    {
                        continue;
                    }

                    if (!SafeCheckEquiped(equip, record))
                    {
                        continue;
                    }

                    string finalEffect = SafeGetFinalEffect(equip, record);
                    if (string.IsNullOrEmpty(finalEffect))
                    {
                        continue;
                    }

                    string sourceName = DescribeSource(record);
                    List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(finalEffect);
                    for (int j = 0; j < effects.Count; j++)
                    {
                        TbEffCfg effect = effects[j];
                        if (effect == null)
                        {
                            continue;
                        }

                        if (!ConditionsAllow(effect))
                        {
                            if (Plugin.Verbose.Value)
                            {
                                Plugin.Log.LogInfo(string.Format(
                                    "跳过击杀效果 {0}（配置 {1}，命令 {2}）：带击杀以外的额外条件，战斗外无法可靠复现。展开后 [{3}]",
                                    sourceName, record.equipEffectCfgId, effect.functionType, finalEffect));
                            }
                            continue;
                        }

                        string functionType = effect.functionType;
                        if (functionType == "UpdateNowHp" || functionType == "UpdateHp" || functionType == "UpdateHpPer")
                        {
                            int heal = KillEffectMath.HealFor(
                                functionType, effect.parameter, GetConditionTypes(effect),
                                player.GetPlayerStoHpNow(), mainNpcSto.hp);
                            if (heal <= 0)
                            {
                                continue;
                            }

                            int hpBefore = player.GetPlayerStoHpNow();
                            player.UpdateHp(heal);
                            int gained = player.GetPlayerStoHpNow() - hpBefore;
                            if (gained > 0)
                            {
                                healForHud += gained;
                                details.Add(string.Format("{0} 气血+{1}", sourceName, gained));
                            }
                            continue;
                        }

                        if (functionType == "UpdateHpLimit")
                        {
                            int value;
                            if (!KillEffectMath.TryGetPermanentHpLimit(effect.parameter, out value))
                            {
                                if (Plugin.Verbose.Value)
                                {
                                    Plugin.Log.LogInfo(string.Format(
                                        "跳过击杀效果 {0}（配置 {1}）：UpdateHpLimit 是临时上限或参数非法。展开后 [{2}]",
                                        sourceName, record.equipEffectCfgId, finalEffect));
                                }
                                continue;
                            }

                            int maxBefore = mainNpcSto.hp;
                            Singleton<BsUpdateImpl>.Instance.UpdateHpLimit(effect.parameter, true);
                            int gained = mainNpcSto.hp - maxBefore;
                            if (gained != 0)
                            {
                                details.Add(string.Format("{0} 气血上限{1}{2}", sourceName, gained > 0 ? "+" : "", gained));
                            }
                            continue;
                        }

                        if (functionType == "UpdateEquipEffectSto")
                        {
                            if (!IsMaxHpStackSource(data, effect.parameter))
                            {
                                // 百魂幡/千魂幡这类叠攻击的击杀效果属于战斗内数值，战斗外没有载体，跳过
                                if (Plugin.Verbose.Value)
                                {
                                    Plugin.Log.LogInfo(string.Format(
                                        "跳过击杀叠层效果 {0}（配置 {1}）：叠层目标不是永久气血上限。展开后 [{2}]",
                                        sourceName, record.equipEffectCfgId, finalEffect));
                                }
                                continue;
                            }

                            int maxBefore = mainNpcSto.hp;
                            if (ApplyMaxHpStack(effect.parameter))
                            {
                                int gained = mainNpcSto.hp - maxBefore;
                                if (gained > 0)
                                {
                                    details.Add(string.Format("{0} 气血上限+{1}（叠层）", sourceName, gained));
                                }
                                else if (Plugin.Verbose.Value)
                                {
                                    Plugin.Log.LogInfo(string.Format(
                                        "击杀叠层效果 {0} 已达上限，本次不再提升气血上限。", sourceName));
                                }
                            }
                            continue;
                        }

                        if (Plugin.Verbose.Value && LooksLikeHp(functionType))
                        {
                            Plugin.Log.LogInfo(string.Format(
                                "跳过击杀效果 {0}（配置 {1}，命令 {2}）：暂不模拟该命令。展开后 [{3}]",
                                sourceName, record.equipEffectCfgId, functionType, finalEffect));
                        }
                    }
                }

                int afterHp = player.GetPlayerStoHpNow();
                int afterMaxHp = mainNpcSto.hp;
                if (details.Count > 0)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "境界碾压跳过战斗：击杀 NPC {0}，补发 {1}；气血 {2} -> {3}/{4}，上限 {5} -> {6}",
                        npcStoId, string.Join("，", details), beforeHp, afterHp, afterMaxHp, beforeMaxHp, afterMaxHp));
                }
                else if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "境界碾压跳过战斗：击杀 NPC {0}，没有可补算的气血/上限变化（满血或已达上限）。", npcStoId));
                }

                // 上限提升的 HUD 由游戏的 UpdateHpLimit 自己打；这里只补“当前气血”提示
                if (healForHud > 0 && Plugin.HudMessage.Value)
                {
                    ShowHudMessage(healForHud);
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("补发击杀回血/气血上限时出错（已忽略，不影响战斗结算）：" + exception);
            }
        }

        /// <summary>
        /// 只处理“击杀时任何来源都会触发”的记录：主角自己、功法/术法/技能/装备/套装作为承载。
        /// MagicId/PackStoId 记录只在“用对应术法/法宝击杀”时触发，碾压跳过没有击杀招式可匹配，故跳过。
        /// </summary>
        private static bool IsAnyKillEffectRecord(TbEquipEffectRecordSto record)
        {
            if (record == null || record.beNpcStoId != PlayerNpcStoIdInt)
            {
                return false;
            }

            switch (record.beUpTargetType)
            {
                case BlendEnum.NpcStoId:
                case BlendEnum.ArtId:
                case BlendEnum.FusionArtId:
                case BlendEnum.Skill:
                case BlendEnum.FusionSkill:
                case BlendEnum.ItemId:
                case BlendEnum.FusionItemId:
                case BlendEnum.SuitCfgId:
                    return true;
                default:
                    return false;
            }
        }

        private static int GetPriority(TbDataImpl data, TbEquipEffectRecordSto record)
        {
            try
            {
                TbEquipEffectCfg cfg = data.GetEquipEffCfg(record.equipEffectCfgId);
                return cfg != null ? cfg.priority : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool SafeCheckEquiped(BsEquipEffectImpl equip, TbEquipEffectRecordSto record)
        {
            try
            {
                return equip.CheckIsEquiped(record);
            }
            catch
            {
                return true;
            }
        }

        private static string SafeGetFinalEffect(BsEquipEffectImpl equip, TbEquipEffectRecordSto record)
        {
            try
            {
                return equip.GetFinalEff(record);
            }
            catch (Exception exception)
            {
                if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogWarning(string.Format("展开击杀效果 {0} 失败：{1}", record.equipEffectCfgId, exception.Message));
                }
                return null;
            }
        }

        /// <summary>按战斗里的真实判定，从 condition 解析击杀触发；没有 condition 时退回 effTime。</summary>
        private static bool IsKillTrigger(TbEquipEffectCfg cfg)
        {
            bool found = false;
            int trigger = cfg.effTime;
            if (!string.IsNullOrEmpty(cfg.condition))
            {
                List<TbEffCfg> conditions = GenericMethods.GetEffectListByStr(cfg.condition);
                for (int i = 0; i < conditions.Count; i++)
                {
                    TbEffCfg condition = conditions[i];
                    if (condition == null || condition.functionType != nameof(EventEnum.JudEffTimeInBattle))
                    {
                        continue;
                    }
                    if (condition.parameter.Count >= 1 && int.TryParse(condition.parameter[0], out int parsed))
                    {
                        found = true;
                        trigger = parsed; // 与游戏一致：多个 JudEffTimeInBattle 时后一个覆盖前一个
                    }
                }
            }

            if (!found)
            {
                return cfg.effTime == (int)EffTimeEnum.Kill;
            }
            return trigger == (int)EffTimeEnum.Kill;
        }

        private static bool ConditionsAllow(TbEffCfg effect)
        {
            if (effect.conditionCfgs == null)
            {
                return true;
            }

            for (int i = 0; i < effect.conditionCfgs.Count; i++)
            {
                TbEffCfg condition = effect.conditionCfgs[i];
                if (condition == null)
                {
                    continue;
                }
                if (condition.functionType != nameof(EventEnum.JudEffTimeInBattle))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<string> GetConditionTypes(TbEffCfg effect)
        {
            var types = new List<string>();
            if (effect.conditionCfgs == null)
            {
                return types;
            }
            for (int i = 0; i < effect.conditionCfgs.Count; i++)
            {
                if (effect.conditionCfgs[i] != null)
                {
                    types.Add(effect.conditionCfgs[i].functionType);
                }
            }
            return types;
        }

        private static bool LooksLikeHp(string functionType)
        {
            return functionType != null && functionType.IndexOf("Hp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>叠层来源（UpdateEquipEffectSto）是否最终落到永久气血上限。</summary>
        private static bool IsMaxHpStackSource(TbDataImpl data, List<string> parameters)
        {
            if (parameters == null || parameters.Count != 13)
            {
                return false;
            }

            int nestedCfgId;
            if (!int.TryParse(parameters[10], out nestedCfgId))
            {
                return false;
            }

            TbEquipEffectCfg nested = data.GetEquipEffCfg(nestedCfgId);
            return nested != null && IsPermanentHpLimitOnly(nested);
        }

        /// <summary>叠层目标必须整条效果都是永久 UpdateHpLimit，避免顺带执行战斗内的其它命令。</summary>
        private static bool IsPermanentHpLimitOnly(TbEquipEffectCfg cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.eff))
            {
                return false;
            }

            List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(cfg.eff);
            if (effects.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < effects.Count; i++)
            {
                TbEffCfg effect = effects[i];
                if (effect == null || effect.functionType != "UpdateHpLimit")
                {
                    return false;
                }
                int value;
                if (!KillEffectMath.TryGetPermanentHpLimit(effect.parameter, out value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasPermanentHpLimit(TbEquipEffectCfg cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.eff))
            {
                return false;
            }

            List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(cfg.eff);
            for (int i = 0; i < effects.Count; i++)
            {
                TbEffCfg effect = effects[i];
                if (effect == null || effect.functionType != "UpdateHpLimit")
                {
                    continue;
                }
                int value;
                if (KillEffectMath.TryGetPermanentHpLimit(effect.parameter, out value))
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetFirstPermanentHpLimitValue(TbEquipEffectCfg cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.eff))
            {
                return 0;
            }

            List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(cfg.eff);
            for (int i = 0; i < effects.Count; i++)
            {
                TbEffCfg effect = effects[i];
                if (effect == null || effect.functionType != "UpdateHpLimit")
                {
                    continue;
                }
                int value;
                if (KillEffectMath.TryGetPermanentHpLimit(effect.parameter, out value))
                {
                    return value;
                }
            }
            return 0;
        }

        /// <summary>
        /// 调游戏自己的叠层接口：会合并/新增 TbEquipEffectRecordSto，按 maxMultiplier 封顶，
        /// 并在 effTime=200 时执行嵌套的 UpdateHpLimit，永久加气血上限并回等量气血。
        /// </summary>
        private static bool ApplyMaxHpStack(List<string> parameters)
        {
            if (parameters == null || parameters.Count != 13)
            {
                return false;
            }

            try
            {
                var nonBattleParameters = new List<string>(parameters);
                nonBattleParameters[12] = "0"; // isBattle=false，只执行持久化部分
                Singleton<BsUpdateImpl>.Instance.UpdateEquipEffectSto(nonBattleParameters, true);
                return true;
            }
            catch (Exception exception)
            {
                if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogWarning("执行击杀叠层效果失败：" + exception.Message);
                }
                return false;
            }
        }

        internal static bool SelfTestFinished;

        /// <summary>
        /// 配置表加载完后跑一次自检：用游戏自己的 GetEquipEffectStr/GetEffectListByStr 展开
        /// 夺命杀诀（回血 100）和血神典（100400→100401 叠层永久气血上限），只写日志，不改存档。
        /// </summary>
        internal static void TryRunSelfTest()
        {
            try
            {
                var data = Singleton<TbDataImpl>.Instance;
                if (data == null || data.equipEffectCfgList == null || data.equipEffectCfgList.Count == 0)
                {
                    return; // 配置还没加载好，下一轮再试
                }

                TbArtCfg killHealArt = Singleton<TbArtImpl>.Instance.GetArtCfg(new BlendId(BlendEnum.ArtId, "992189"));
                bool hasKillHealArt = killHealArt != null;
                int healPerKill = 0;
                string healExpanded = "";
                if (hasKillHealArt)
                {
                    healExpanded = Singleton<BsEquipEffectImpl>.Instance.GetEquipEffectStr(killHealArt.equipEff);
                    healPerKill = SumHealFromEffectString(healExpanded, 0, 10000);
                }

                TbEquipEffectCfg directHpLimit = data.GetEquipEffCfg(10041);
                bool directMaxOk = HasPermanentHpLimit(directHpLimit);

                TbDrawStateCfg drawState = data.GetDrawStateCfg(80056);
                bool drawStateOk = drawState != null && !string.IsNullOrEmpty(drawState.equipEff)
                                   && drawState.equipEff.StartsWith("10041", StringComparison.Ordinal);

                TbEquipEffectCfg stackEffect = data.GetEquipEffCfg(100401);
                bool stackMaxOk = IsPermanentHpLimitOnly(stackEffect);
                int stackValue = GetFirstPermanentHpLimitValue(stackEffect);

                bool stackExpandOk = false;
                string stackExpanded = "";
                try
                {
                    var fakeRecord = new TbEquipEffectRecordSto("1,10000,605", "1,10000,605", 100400, 1f, 0, 1);
                    stackExpanded = Singleton<BsEquipEffectImpl>.Instance.GetFinalEff(fakeRecord);
                    List<TbEffCfg> stackEffects = GenericMethods.GetEffectListByStr(stackExpanded);
                    stackExpandOk = stackEffects.Count == 1
                                    && stackEffects[0].functionType == "UpdateEquipEffectSto"
                                    && stackEffects[0].parameter.Count == 13
                                    && stackEffects[0].parameter[10] == "100401";
                }
                catch (Exception exception)
                {
                    stackExpanded = "展开失败：" + exception.Message;
                }

                SelfTestFinished = true;
                bool allOk = hasKillHealArt && healPerKill == 100 && directMaxOk && drawStateOk && stackMaxOk && stackExpandOk;
                if (allOk)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "自检通过：夺命杀诀 击杀回血 {0}/次；气血上限 10041 +1、100400→100401 +{1} 叠层解析正常。回血展开 [{2}]，叠层展开 [{3}]",
                        healPerKill, stackValue, healExpanded, stackExpanded));
                }
                else
                {
                    Plugin.Log.LogWarning(string.Format(
                        "自检异常：回血功法={0} 回血量={1} 直接上限(10041)={2} 帝典状态(80056)={3} 叠层上限(100401)={4} 叠层展开={5}。回血展开 [{6}]，叠层展开 [{7}]。游戏更新过配置的话请把日志发给 mod 作者。",
                        hasKillHealArt, healPerKill, directMaxOk, drawStateOk, stackMaxOk, stackExpandOk, healExpanded, stackExpanded));
                }
            }
            catch (Exception exception)
            {
                SelfTestFinished = true;
                Plugin.Log.LogWarning("自检过程出错（不影响使用）：" + exception.Message);
            }
        }

        private static int SumHealFromEffectString(string finalEffect, int currentHp, int maxHp)
        {
            if (string.IsNullOrEmpty(finalEffect))
            {
                return 0;
            }

            int total = 0;
            List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(finalEffect);
            for (int i = 0; i < effects.Count; i++)
            {
                TbEffCfg effect = effects[i];
                if (effect == null)
                {
                    continue;
                }
                total += KillEffectMath.HealFor(effect.functionType, effect.parameter, GetConditionTypes(effect), currentHp, maxHp);
            }

            return total;
        }

        /// <summary>调试用：把当前生效的击杀回血/气血上限效果列进日志，方便确认功法有没有被识别。</summary>
        internal static void DumpActiveKillEffects()
        {
            try
            {
                var actor = BsSaveDataImpl.NowActor;
                if (actor == null)
                {
                    Plugin.Log.LogWarning("尚未进入存档，无法检查击杀效果。");
                    return;
                }

                var player = Singleton<TbPlayerImpl>.Instance;
                var data = Singleton<TbDataImpl>.Instance;
                var equip = Singleton<BsEquipEffectImpl>.Instance;
                int currentHp = player != null ? player.GetPlayerStoHpNow() : 0;
                int maxHp = 0;
                try
                {
                    var mainNpcSto = Singleton<TbNpcImpl>.Instance.GetNpcSto(PlayerNpcStoId);
                    if (mainNpcSto != null)
                    {
                        maxHp = mainNpcSto.hp;
                    }
                }
                catch
                {
                }

                var lines = new List<string>();
                var seen = new HashSet<string>();
                int totalHeal = 0;
                int totalMax = 0;

                var records = new List<TbEquipEffectRecordSto>(actor.equipEffectRecordStoList);
                for (int i = 0; i < records.Count; i++)
                {
                    TbEquipEffectRecordSto record = records[i];
                    if (!IsAnyKillEffectRecord(record))
                    {
                        continue;
                    }

                    TbEquipEffectCfg cfg = data.GetEquipEffCfg(record.equipEffectCfgId);
                    if (cfg == null || string.IsNullOrEmpty(cfg.condition) || !IsKillTrigger(cfg))
                    {
                        continue;
                    }
                    if (!SafeCheckEquiped(equip, record))
                    {
                        continue;
                    }

                    string finalEffect = SafeGetFinalEffect(equip, record);
                    if (string.IsNullOrEmpty(finalEffect))
                    {
                        continue;
                    }

                    string sourceName = DescribeSource(record);
                    List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(finalEffect);
                    for (int j = 0; j < effects.Count; j++)
                    {
                        TbEffCfg effect = effects[j];
                        if (effect == null || !ConditionsAllow(effect))
                        {
                            continue;
                        }

                        if (effect.functionType == "UpdateNowHp" || effect.functionType == "UpdateHp" || effect.functionType == "UpdateHpPer")
                        {
                            int heal = KillEffectMath.HealFor(
                                effect.functionType, effect.parameter, GetConditionTypes(effect), currentHp, maxHp);
                            if (heal > 0)
                            {
                                totalHeal += heal;
                                AddLine(lines, seen, string.Format("{0}：击杀回血 +{1}", sourceName, heal));
                            }
                        }
                        else if (effect.functionType == "UpdateHpLimit")
                        {
                            int value;
                            if (KillEffectMath.TryGetPermanentHpLimit(effect.parameter, out value))
                            {
                                totalMax += value;
                                AddLine(lines, seen, string.Format("{0}：击杀气血上限 {1}{2}", sourceName, value > 0 ? "+" : "", value));
                            }
                        }
                        else if (effect.functionType == "UpdateEquipEffectSto" && IsMaxHpStackSource(data, effect.parameter))
                        {
                            int nestedCfgId;
                            if (!int.TryParse(effect.parameter[10], out nestedCfgId))
                            {
                                continue;
                            }
                            TbEquipEffectCfg nested = data.GetEquipEffCfg(nestedCfgId);
                            int value = GetFirstPermanentHpLimitValue(nested);
                            int cap = nested != null ? (int)nested.maxMultiplier : 0;
                            totalMax += value;
                            AddLine(lines, seen, string.Format(
                                "{0}：击杀气血上限 +{1}（叠层 {2}，最多 {3} 层）",
                                sourceName, value, nestedCfgId, cap));
                        }
                    }
                }

                if (lines.Count == 0)
                {
                    Plugin.Log.LogInfo("当前没有生效的击杀回血/气血上限效果（确认已装备夺命杀诀、血神典、赤血魔功、血煞真解等）。");
                    if (Plugin.HudMessage.Value)
                    {
                        ShowHudMessageText("当前没有击杀回血/上限效果");
                    }
                    return;
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    Plugin.Log.LogInfo(string.Format("击杀效果 {0}：{1}", i + 1, lines[i]));
                }
                Plugin.Log.LogInfo(string.Format(
                    "合计：每次击杀回复 {0} 气血、提升 {1} 气血上限（当前 {2}/{3}）",
                    totalHeal, totalMax, currentHp, maxHp));
                if (Plugin.HudMessage.Value)
                {
                    ShowHudMessageText(string.Format("击杀：回血+{0}，上限+{1}", totalHeal, totalMax));
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("检查击杀效果时出错：" + exception);
            }
        }

        private static void AddLine(List<string> lines, HashSet<string> seen, string line)
        {
            if (seen.Add(line))
            {
                lines.Add(line);
            }
        }

        private static string DescribeSource(TbEquipEffectRecordSto record)
        {
            try
            {
                if (TbArtImpl.IsArt(record.fromUpTargetType))
                {
                    TbArtCfg artCfg = Singleton<TbArtImpl>.Instance.GetArtCfg(new BlendId(record.fromUpTargetType, record.fromSedId));
                    if (artCfg != null && !string.IsNullOrEmpty(artCfg.GetName))
                    {
                        return artCfg.GetName;
                    }
                }
                else if (TbMagicImpl.IsMagic(record.fromUpTargetType))
                {
                    TbMagicCfg magicCfg = Singleton<TbMagicImpl>.Instance.GetMagicCfg(new BlendId(record.fromUpTargetType, record.fromSedId));
                    if (magicCfg != null && !string.IsNullOrEmpty(magicCfg.GetName))
                    {
                        return magicCfg.GetName;
                    }
                }
            }
            catch
            {
                // 名字取不到就退回 fromUpId，不影响功能
            }

            return record.fromUpId;
        }

        private static void ShowHudMessage(int heal)
        {
            try
            {
                GenericMethods.HUDPingLog(string.Format(
                    "{0}<color=#3eae1c>+{1}</color>",
                    Singleton<LocalizationManager>.Instance.GetLocalizationData("142"), heal), true);
            }
            catch (Exception exception)
            {
                if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogWarning("显示 HUD 消息失败：" + exception.Message);
                }
            }
        }

        private static void ShowHudMessageText(string text)
        {
            try
            {
                GenericMethods.HUDPingLog(string.Format("<color=#3eae1c>{0}</color>", text), true);
            }
            catch (Exception exception)
            {
                if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogWarning("显示 HUD 消息失败：" + exception.Message);
                }
            }
        }
    }
}
