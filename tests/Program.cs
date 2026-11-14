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

        Console.WriteLine(failures == 0 ? "全部通过" : string.Format("失败 {0} 项", failures));
        Environment.ExitCode = failures == 0 ? 0 : 1;
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
