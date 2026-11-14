using System;
using System.Collections.Generic;
using ZaohuaKillHealFix;

internal static class Program
{
    private static int failures;

    private static void Main()
    {
        // 夺命杀诀：equipEff = 10054,100,0,1
        // 展开后：UpdateNowHp,2,100,#npcTmpId#,#SelectedNpcId#,1@JudEffTimeInBattle,15,-1,0,0
        Check(
            "夺命杀诀每次击杀 +100",
            100,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "满血时仍按配置值计算（实际入账由 UpdateHp 截断）",
            100,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle" },
                5000, 5000));

        Check(
            "UpdateNowHp 第五位为 0：补到指定值（只补差额）",
            70,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "100", "#npcTmpId#", "#SelectedNpcId#", "0" },
                new List<string> { "JudEffTimeInBattle" },
                30, 5000));

        Check(
            "UpdateNowHp 设为指定值但当前已高于它：不扣血",
            0,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "100", "#npcTmpId#", "#SelectedNpcId#", "0" },
                new List<string> { "JudEffTimeInBattle" },
                150, 5000));

        Check(
            "目标为敌人：不给自己加血",
            0,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "0", "100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "目标为“所有人”时包含自己",
            100,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "99", "100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "除击杀外还有额外条件：保守跳过",
            0,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle", "JudHpLess" },
                50, 5000));

        Check(
            "UpdateHp 直接加固定值",
            20,
            KillEffectMath.HealFor(
                "UpdateHp",
                new List<string> { "20" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "UpdateHpPer 按最大气血百分比",
            50,
            KillEffectMath.HealFor(
                "UpdateHpPer",
                new List<string> { "1%" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "非回血命令：返回 0",
            0,
            KillEffectMath.HealFor(
                "UpdateHpLimit",
                new List<string> { "5" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "负值不当作回血",
            0,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2", "-100", "#npcTmpId#", "#SelectedNpcId#", "1" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        Check(
            "参数缺失：返回 0",
            0,
            KillEffectMath.HealFor(
                "UpdateNowHp",
                new List<string> { "2" },
                new List<string> { "JudEffTimeInBattle" },
                50, 5000));

        int hpLimit;
        CheckBool(
            "UpdateHpLimit 永久上限：+5",
            true,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "5" }, out hpLimit));
        Check("UpdateHpLimit 数值", 5, hpLimit);

        CheckBool(
            "UpdateHpLimit 第二个参数为 0 仍是永久：+5",
            true,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "5", "0" }, out hpLimit));
        Check("UpdateHpLimit 数值（显式 0）", 5, hpLimit);

        CheckBool(
            "UpdateHpLimit 临时上限（第二个参数 1）不按永久处理",
            false,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "5", "1" }, out hpLimit));

        CheckBool(
            "UpdateHpLimit 负数也保留（可能是有代价的功法）",
            true,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "-5" }, out hpLimit));
        Check("UpdateHpLimit 负数值", -5, hpLimit);

        CheckBool(
            "UpdateHpLimit 非法数值：跳过",
            false,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "abc" }, out hpLimit));

        CheckBool(
            "UpdateHpLimit 非法临时标记：跳过（游戏 int.Parse 会抛异常）",
            false,
            KillEffectMath.TryGetPermanentHpLimit(new List<string> { "5", "x" }, out hpLimit));

        Console.WriteLine(failures == 0 ? "全部通过" : string.Format("失败 {0} 项", failures));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void CheckBool(string name, bool expected, bool actual)
    {
        if (expected == actual)
        {
            Console.WriteLine("[OK]   " + name + " = " + actual);
            return;
        }

        failures++;
        Console.WriteLine("[FAIL] " + name + "：期望 " + expected + "，实际 " + actual);
    }

    private static void Check(string name, int expected, int actual)
    {
        if (expected == actual)
        {
            Console.WriteLine("[OK]   " + name + " = " + actual);
            return;
        }

        failures++;
        Console.WriteLine("[FAIL] " + name + "：期望 " + expected + "，实际 " + actual);
    }
}
