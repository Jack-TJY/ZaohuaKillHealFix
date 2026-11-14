using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ZaohuaKillHealFix
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "zaohua.killhealfix";
        public const string Name = "境界碾压击杀回血/上限修复";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> HudMessage;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<bool> SelfTest;
        internal static ConfigEntry<KeyboardShortcut> DebugKey;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind(
                "通用",
                "Enabled",
                true,
                "是否启用。true = 境界/修为碾压跳过战斗时，补发功法/法宝的击杀回血和永久气血上限提升。");
            HudMessage = Config.Bind(
                "通用",
                "HudMessage",
                true,
                "补血时是否在左上角 HUD 显示“气血：+N”。");
            Verbose = Config.Bind(
                "通用",
                "Verbose",
                false,
                "详细日志：列出被跳过的击杀效果和解析失败原因，排查配置时打开。");
            SelfTest = Config.Bind(
                "通用",
                "SelfTest",
                true,
                "配置表加载完后自检一次“夺命杀诀 +100/击杀、10041 +1、100400→100401 叠层”，结果写进 BepInEx 日志。只写日志，不改存档。");
            DebugKey = Config.Bind(
                "通用",
                "DebugKey",
                new KeyboardShortcut(KeyCode.F7),
                "按此键把当前生效的击杀回血/气血上限效果写进 BepInEx 日志并显示在 HUD，用来确认功法是否被识别。");

            ApplyPatches();

            // BepInEx 的管理物体在这个游戏里会被销毁，自己建一个常驻物体跑 Update（调试键用）
            var runner = new GameObject("ZaohuaKillHealFix");
            runner.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<DebugRunner>();
        }

        private static void ApplyPatches()
        {
            try
            {
                MethodInfo doDefeat = AccessTools.Method(typeof(BsBattleImpl), nameof(BsBattleImpl.DoDefeat), new[] { typeof(int) });
                if (doDefeat == null)
                {
                    Log.LogError("找不到 BsBattleImpl.DoDefeat(int)，补丁未应用。游戏版本可能已更新，需要重新编译本 mod。");
                    return;
                }

                var harmony = new Harmony(Guid);
                harmony.Patch(doDefeat, prefix: new HarmonyMethod(typeof(DoDefeatPatch), nameof(DoDefeatPatch.Prefix)));
                Log.LogInfo("补丁已应用：BsBattleImpl.DoDefeat(int) -> 境界碾压跳过战斗时补发击杀回血/气血上限。");
            }
            catch (Exception exception)
            {
                Log.LogError("应用 Harmony 补丁失败：" + exception);
            }
        }
    }

    /// <summary>把 DoDefeat 作为“击杀一次”的挂钩点。真战斗会走游戏自己的 Kill 事件，这里跳过。</summary>
    internal static class DoDefeatPatch
    {
        internal static void Prefix(BsBattleImpl __instance, int npcStoId)
        {
            KillHeal.OnEnemyDefeated(__instance, npcStoId);
        }
    }

    internal sealed class DebugRunner : MonoBehaviour
    {
        private float nextSelfTestAt;

        private void Update()
        {
            try
            {
                if (Plugin.SelfTest != null && Plugin.SelfTest.Value && !KillHeal.SelfTestFinished)
                {
                    if (Time.realtimeSinceStartup >= nextSelfTestAt)
                    {
                        nextSelfTestAt = Time.realtimeSinceStartup + 1f;
                        KillHeal.TryRunSelfTest();
                    }
                }

                if (Plugin.DebugKey != null && Plugin.DebugKey.Value.IsDown())
                {
                    KillHeal.DumpActiveKillEffects();
                }
            }
            catch (Exception exception)
            {
                if (Plugin.Log != null)
                {
                    Plugin.Log.LogError("调试键/自检处理出错：" + exception);
                }
            }
        }
    }
}
