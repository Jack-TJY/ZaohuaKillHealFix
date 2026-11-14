using System;
using System.Collections.Generic;

namespace ZaohuaKillHealFix
{
    /// <summary>
    /// 把一条“击杀时机”效果（已经过倍率展开的参数字符串）换算成“这一次击杀回复多少气血”。
    /// 只做纯计算，不碰游戏对象，方便离线测试。
    /// </summary>
    internal static class KillEffectMath
    {
        /// <summary>击杀条件本身；外层已按 TbEquipEffectCfg.effTime == Kill 过滤，这里只放行这一个条件。</summary>
        internal const string KillCondition = "JudEffTimeInBattle";

        /// <summary>游戏里 PutObjectEnum：2=自己，16=队友或自己，99=所有人（都包含主角）。</summary>
        private static bool TargetsSelf(int putObject)
        {
            return putObject == 2 || putObject == 16 || putObject == 99;
        }

        internal static int HealFor(string functionType, IList<string> parameters, IList<string> conditionTypes, int currentHp, int maxHp)
        {
            if (string.IsNullOrEmpty(functionType))
            {
                return 0;
            }

            // 战斗里 isBattle=true 时不会判断效果自带的 @ 条件；但这里是在战斗外补算，
            // 只认“击杀”这个触发条件，其余条件（血量、目标种族等）无法可靠复现就放弃，避免多加。
            if (conditionTypes != null)
            {
                for (int i = 0; i < conditionTypes.Count; i++)
                {
                    if (conditionTypes[i] != KillCondition)
                    {
                        return 0;
                    }
                }
            }

            switch (functionType)
            {
                case "UpdateNowHp":
                    return HealFromNowHp(parameters, currentHp);
                case "UpdateHp":
                    return HealFromHp(parameters);
                case "UpdateHpPer":
                    return HealFromHpPer(parameters, maxHp);
                default:
                    return 0;
            }
        }

        // UpdateNowHp,<对象>,<数值>,#npcTmpId#,#SelectedNpcId#,<是否累加>
        // 第五个参数为 1：直接加血；为 0/缺省：把气血“设为”数值（只补差额）。
        private static int HealFromNowHp(IList<string> parameters, int currentHp)
        {
            if (parameters == null || parameters.Count < 2)
            {
                return 0;
            }

            int putObject;
            int value;
            if (!int.TryParse(parameters[0], out putObject) || !TargetsSelf(putObject))
            {
                return 0;
            }
            if (!int.TryParse(parameters[1], out value) || value <= 0)
            {
                return 0;
            }

            bool setToValue = parameters.Count <= 4;
            if (!setToValue)
            {
                int addFlag;
                if (!int.TryParse(parameters[4], out addFlag))
                {
                    return 0;
                }
                setToValue = addFlag == 0;
            }

            if (!setToValue)
            {
                return value;
            }

            int delta = value - currentHp;
            return delta > 0 ? delta : 0;
        }

        // UpdateHp,<数值>
        private static int HealFromHp(IList<string> parameters)
        {
            if (parameters == null || parameters.Count < 1)
            {
                return 0;
            }

            int value;
            if (!int.TryParse(parameters[0], out value))
            {
                return 0;
            }
            return value > 0 ? value : 0;
        }

        // UpdateHpPer,<百分比>
        private static int HealFromHpPer(IList<string> parameters, int maxHp)
        {
            if (parameters == null || parameters.Count < 1 || maxHp <= 0)
            {
                return 0;
            }

            float percent;
            if (!TryParsePercent(parameters[0], out percent) || percent <= 0f)
            {
                return 0;
            }

            int heal = (int)Math.Round(maxHp * percent, MidpointRounding.AwayFromZero);
            // 与 TbPlayerImpl.UpdateHpPer 一致：正数百分比至少回 1 点
            return heal > 0 ? heal : 1;
        }

        private static bool TryParsePercent(string text, out float percent)
        {
            percent = 0f;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string raw = text.EndsWith("%", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text;
            float value;
            if (!float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            percent = value / 100f;
            return true;
        }
    }
}
