using System;
using System.Collections.Generic;
using System.Text;

namespace ZaohuaKillHealFix
{
    /// <summary>
    /// 修为/境界碾压跳过战斗时，把玩家身上“击杀时回复气血”的效果补算回来。
    ///
    /// 正常战斗里，击杀由 BattleObject.DoDie -> DoBattleEvent(EffTimeEnum.Kill) 触发，
    /// 装备/功法效果（effTime=15）会走 TbBattleModifierImpl 结算。
    /// 碾压跳过战斗时只调用 BsBattleImpl.DoDefeat，没有战斗对象，所以这条链不会执行。
    /// 这里在 DoDefeat 前按同一套配置复算回复量，再交给 TbPlayerImpl.UpdateHp 入账。
    /// </summary>
    internal static class KillHeal
    {
        private const long PlayerNpcStoId = 10000L;
        private const int PlayerNpcStoIdInt = 10000;

        private sealed class HealSource
        {
            internal string Name;
            internal int ConfigId;
            internal string FinalEffect;
            internal int Heal;
        }

        /// <summary>判断当前是不是真的在战斗场景里（真战斗由游戏自己结算击杀效果，不能重复加）。</summary>
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
                if (player == null)
                {
                    return;
                }

                var playerSto = player.GetPlayerSto();
                var mainNpcSto = Singleton<TbNpcImpl>.Instance.GetNpcSto(PlayerNpcStoId);
                if (playerSto == null || mainNpcSto == null)
                {
                    return;
                }

                int currentHp = player.GetPlayerStoHpNow();
                int maxHp = mainNpcSto.hp;
                List<HealSource> sources = CollectSources(actor, currentHp, maxHp);
                if (sources.Count == 0)
                {
                    return;
                }

                int plannedHeal = 0;
                for (int i = 0; i < sources.Count; i++)
                {
                    plannedHeal += sources[i].Heal;
                }

                if (plannedHeal <= 0)
                {
                    return;
                }

                player.UpdateHp(plannedHeal);
                int gained = player.GetPlayerStoHpNow() - currentHp;
                if (gained < 0)
                {
                    gained = 0;
                }

                string sourceText = DescribeSources(sources);
                Plugin.Log.LogInfo(string.Format(
                    "境界碾压跳过战斗：击杀 NPC {0}，补发击杀回血 {1}（{2}），气血 {3} -> {4}/{5}",
                    npcStoId, plannedHeal, sourceText, currentHp, player.GetPlayerStoHpNow(), maxHp));

                if (gained > 0 && Plugin.HudMessage.Value)
                {
                    ShowHudMessage(gained);
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("补发击杀回血时出错（已忽略，不影响战斗结算）：" + exception);
            }
        }

        internal static bool SelfTestFinished;

        /// <summary>
        /// 配置表加载完后跑一次自检：用游戏自己的 GetEquipEffectStr/GetEffectListByStr 展开“夺命杀诀”，
        /// 确认本 mod 能算出 +100/击杀。只写日志，不改存档。
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

                TbArtCfg artCfg = Singleton<TbArtImpl>.Instance.GetArtCfg(new BlendId(BlendEnum.ArtId, "992189"));
                if (artCfg == null)
                {
                    SelfTestFinished = true;
                    Plugin.Log.LogInfo("自检跳过：当前配置表里没有功法 992189（夺命杀诀）。");
                    return;
                }

                TbEquipEffectCfg effectCfg = data.GetEquipEffCfg(10054);
                if (effectCfg == null)
                {
                    SelfTestFinished = true;
                    Plugin.Log.LogWarning("自检失败：找不到 TbEquipEffectCfg 10054。");
                    return;
                }

                string finalEffect = Singleton<BsEquipEffectImpl>.Instance.GetEquipEffectStr(artCfg.equipEff);
                int heal = SumHealFromEffectString(finalEffect, 0, 10000);
                SelfTestFinished = true;

                if (heal == 100)
                {
                    Plugin.Log.LogInfo(string.Format(
                        "自检通过：夺命杀诀(992189) 每次击杀回复 {0} 气血；展开后 [{1}]。", heal, finalEffect));
                }
                else
                {
                    Plugin.Log.LogWarning(string.Format(
                        "自检异常：夺命杀诀 展开后 [{0}]，算出的每次击杀回复是 {1}（预期 100）。如果游戏刚更新过配置，请把日志发给 mod 作者。",
                        finalEffect, heal));
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

                var conditionTypes = new List<string>();
                if (effect.conditionCfgs != null)
                {
                    for (int j = 0; j < effect.conditionCfgs.Count; j++)
                    {
                        if (effect.conditionCfgs[j] != null)
                        {
                            conditionTypes.Add(effect.conditionCfgs[j].functionType);
                        }
                    }
                }

                total += KillEffectMath.HealFor(effect.functionType, effect.parameter, conditionTypes, currentHp, maxHp);
            }

            return total;
        }

        /// <summary>调试用：把当前生效的击杀回血效果列进日志，方便确认“夺命杀诀”有没有被识别。</summary>
        internal static void DumpActiveKillEffects()
        {
            try
            {
                var actor = BsSaveDataImpl.NowActor;
                if (actor == null)
                {
                    Plugin.Log.LogWarning("尚未进入存档，无法检查击杀回血效果。");
                    return;
                }

                var player = Singleton<TbPlayerImpl>.Instance;
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

                List<HealSource> sources = CollectSources(actor, currentHp, maxHp);
                if (sources.Count == 0)
                {
                    Plugin.Log.LogInfo("当前没有生效的击杀回血效果（确认已装备“夺命杀诀”等带击杀回血的功法/法宝）。");
                    if (Plugin.HudMessage.Value)
                    {
                        ShowHudMessageText("当前没有击杀回血效果");
                    }
                    return;
                }

                StringBuilder builder = new StringBuilder();
                int total = 0;
                for (int i = 0; i < sources.Count; i++)
                {
                    total += sources[i].Heal;
                    Plugin.Log.LogInfo(string.Format(
                        "击杀回血效果 {0}：{1}，配置 {2}，展开后 [{3}]，每次击杀 +{4}",
                        i + 1, sources[i].Name, sources[i].ConfigId, sources[i].FinalEffect, sources[i].Heal));
                    if (i > 0)
                    {
                        builder.Append("、");
                    }
                    builder.Append(sources[i].Name);
                }

                Plugin.Log.LogInfo(string.Format("合计：每次击杀回复 {0} 气血（当前 {1}/{2}）", total, currentHp, maxHp));
                if (Plugin.HudMessage.Value)
                {
                    ShowHudMessageText(string.Format("击杀回血：+{0}/击杀（{1}）", total, builder));
                }
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError("检查击杀回血效果时出错：" + exception);
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

        private static List<HealSource> CollectSources(TbActor actor, int currentHp, int maxHp)
        {
            var result = new List<HealSource>();
            var records = actor.equipEffectRecordStoList;
            if (records == null)
            {
                return result;
            }

            var data = Singleton<TbDataImpl>.Instance;
            var equip = Singleton<BsEquipEffectImpl>.Instance;
            if (data == null || equip == null)
            {
                return result;
            }

            for (int i = 0; i < records.Count; i++)
            {
                TbEquipEffectRecordSto record = records[i];
                if (record == null)
                {
                    continue;
                }

                // 只处理挂在主角本人身上的效果；战斗里这类效果的 magic/pack/state 过滤都是 0，任何击杀都会触发。
                if (record.beUpTargetType != BlendEnum.NpcStoId || record.beNpcStoId != PlayerNpcStoIdInt)
                {
                    continue;
                }

                TbEquipEffectCfg cfg = data.GetEquipEffCfg(record.equipEffectCfgId);
                if (cfg == null)
                {
                    continue;
                }

                // 战斗里触发时机由 condition 里的 JudEffTimeInBattle 决定（TbBattleModifierImpl.GetBattleEffTime），
                // cfg.effTime 只是配置表里的展示/非战斗时机，二者可能不一致（例如 100206：effTime=14 但条件=15）。
                if (!IsKillTrigger(cfg))
                {
                    continue;
                }

                // TbBattleModifierImpl.Init 只收 condition 不为 null 的记录
                if (string.IsNullOrEmpty(cfg.condition))
                {
                    continue;
                }

                bool equipped;
                try
                {
                    equipped = equip.CheckIsEquiped(record);
                }
                catch
                {
                    equipped = true;
                }
                if (!equipped)
                {
                    continue;
                }

                string finalEffect;
                try
                {
                    finalEffect = equip.GetFinalEff(record);
                }
                catch (Exception exception)
                {
                    if (Plugin.Verbose.Value)
                    {
                        Plugin.Log.LogWarning(string.Format("展开击杀效果 {0} 失败：{1}", record.equipEffectCfgId, exception.Message));
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(finalEffect))
                {
                    continue;
                }

                List<TbEffCfg> effects = GenericMethods.GetEffectListByStr(finalEffect);
                for (int j = 0; j < effects.Count; j++)
                {
                    TbEffCfg effect = effects[j];
                    if (effect == null)
                    {
                        continue;
                    }

                    var conditionTypes = new List<string>();
                    if (effect.conditionCfgs != null)
                    {
                        for (int k = 0; k < effect.conditionCfgs.Count; k++)
                        {
                            TbEffCfg condition = effect.conditionCfgs[k];
                            if (condition != null)
                            {
                                conditionTypes.Add(condition.functionType);
                            }
                        }
                    }

                    int heal = KillEffectMath.HealFor(effect.functionType, effect.parameter, conditionTypes, currentHp, maxHp);
                    if (heal <= 0)
                    {
                        if (Plugin.Verbose.Value && LooksLikeHeal(effect.functionType))
                        {
                            Plugin.Log.LogInfo(string.Format(
                                "跳过击杀效果 {0}（配置 {1}，命令 {2}）：不是可安全补算的回血，或目标不是自己/数值非正。展开后 [{3}]",
                                DescribeSource(record), record.equipEffectCfgId, effect.functionType, finalEffect));
                        }
                        continue;
                    }

                    result.Add(new HealSource
                    {
                        Name = DescribeSource(record),
                        ConfigId = record.equipEffectCfgId,
                        FinalEffect = finalEffect,
                        Heal = heal
                    });
                }
            }

            return result;
        }

        private static bool LooksLikeHeal(string functionType)
        {
            return functionType != null && functionType.IndexOf("Hp", StringComparison.OrdinalIgnoreCase) >= 0
                   && !functionType.StartsWith("UpdateHpLimit", StringComparison.Ordinal);
        }

        private static string DescribeSources(List<HealSource> sources)
        {
            var builder = new StringBuilder();
            string lastName = null;
            for (int i = 0; i < sources.Count; i++)
            {
                string name = sources[i].Name;
                if (name == lastName)
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.Append("、");
                }
                builder.Append(name);
                lastName = name;
            }
            return builder.ToString();
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
