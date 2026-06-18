using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace Silksong.CyclesMod;

[HarmonyPatch]
[BepInAutoPlugin(id: "com.spacemonkeyy.cyclestest")]
public partial class CyclesMod : BaseUnityPlugin
{
    private static CyclesMod instance;

    private ConfigEntry<bool> normalizeLoads;
    private ConfigEntry<float> extraLoadTime;
    private ConfigEntry<bool> forceClearMemory;

    private TimeManager.TimeControlInstance? timeControl;

    private void Awake()
    {
        instance = this;

        normalizeLoads = Config.Bind(
            "General",
            "Normalize Loads",
            true,
            "Prevents cycles from starting until after the load has finished."
        );
        extraLoadTime = Config.Bind(
            "General",
            "Extra Load Time",
            0f,
            "Time in seconds to let cycles run before giving player control."
        );
        forceClearMemory = Config.Bind(
            "General",
            "Force Clear Memory",
            false,
            "Forces an extra load step which typically desyncs cycles."
        );

        Harmony harmony = new(Id);
        harmony.PatchAll();
    }

    [HarmonyTranspiler, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.BeginRoutine), MethodType.Enumerator)]
#pragma warning disable HARMONIZE001
    private static IEnumerable<CodeInstruction> SceneLoad_BeginRoutine(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
#pragma warning restore HARMONIZE001
    {
        CodeMatcher matcher = new(instructions, generator);

        Label afterWaitLabel = generator.DefineLabel();
        Label afterFirstYield = generator.DefineLabel();

        matcher.MatchStartForward(new CodeMatch(OpCodes.Leave));
        Label returnLabel = (Label)matcher.Operand;
        matcher.MatchStartForward(new CodeMatch(i => i.operand is FieldInfo info && info.Name.EndsWith("__state")));
        FieldInfo stateField = (FieldInfo)matcher.Operand;
        matcher.MatchStartForward(new CodeMatch(i => i.operand is FieldInfo info && info.Name.EndsWith("__current")));
        FieldInfo currentField = (FieldInfo)matcher.Operand;

        /*
         * RecordBeginTime(Phases.Activation);
         * <-- BEGIN INJECTED -->
         * FreezeTimeScale();
         * <-- END INJECTED -->
         */

        static void FreezeTimeScale()
        {
            if (instance.normalizeLoads.Value)
            {
                instance.timeControl = new TimeManager.TimeControlInstance(0f, TimeManager.TimeControlInstance.Type.Multiplicative);
            }
        }

        matcher.Start();
        matcher.MatchEndForward(
            new CodeMatch(OpCodes.Ldloc_2),
            new CodeMatch(OpCodes.Ldc_I4_4),
            new CodeMatch(OpCodes.Call, typeof(SceneLoad).GetMethod(nameof(SceneLoad.RecordEndTime)))
        );
        matcher.Advance(1);
        matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, ((Delegate)FreezeTimeScale).Method));

        /*
         * RecordBeginTime(SceneLoad.Phases.StartCall);
         * yield return null;
         * <-- BEGIN INJECTED -->
         * if (NormalizeLoadsEnabled())
         * {
         *     yield return null;
         * }
         * <-- END INJECTED -->
         * RecordEndTime(SceneLoad.Phases.StartCall);
         */

        static bool NormalizeLoadsEnabled()
        {
            return instance.normalizeLoads.Value;
        }

        matcher.Start();
        matcher.MatchStartForward(new CodeMatch(OpCodes.Switch));
        Label[] labels = (Label[])matcher.Operand;
        int stateValue = labels.Length;
        matcher.Operand = (Label[])[.. labels, afterFirstYield];

        matcher.MatchStartForward(
            new CodeMatch(OpCodes.Ldarg_0),
            new CodeMatch(OpCodes.Ldc_I4_M1),
            new CodeMatch(OpCodes.Stfld, stateField),
            new CodeMatch(OpCodes.Ldloc_2),
            new CodeMatch(OpCodes.Ldc_I4_7),
            new CodeMatch(OpCodes.Call, typeof(SceneLoad).GetMethod(nameof(SceneLoad.RecordEndTime)))
        );
        Label oldLabel = matcher.Instruction.labels[0];
        int oldState = labels.IndexOf(oldLabel);
        matcher.Insert(
            new CodeInstruction(OpCodes.Call, ((Delegate)NormalizeLoadsEnabled).Method),
            new CodeInstruction(OpCodes.Brfalse, oldLabel),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldnull),
            new CodeInstruction(OpCodes.Stfld, currentField),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldc_I4, oldState),
            new CodeInstruction(OpCodes.Stfld, stateField),
            new CodeInstruction(OpCodes.Ldc_I4_1),
            new CodeInstruction(OpCodes.Stloc_0),
            new CodeInstruction(OpCodes.Leave, returnLabel)
        );
        matcher.Instruction.labels.Add(afterFirstYield);
        matcher.MatchStartBackwards(new CodeMatch(OpCodes.Stfld, stateField));
        matcher.MatchStartBackwards(new CodeMatch(OpCodes.Stfld, stateField));
        matcher.Advance(-1);
        matcher.SetInstruction(new CodeInstruction(OpCodes.Ldc_I4, stateValue));

        /*
         * <-- BEGIN INJECTED -->
         * ResetTimeScale();
         * if (GetLoadDelay() > 0f)
         * {
         *     yield return new WaitForSecondsRealtime(GetLoadDelay());
         * }
         * <-- END INJECTED -->
         * this.IsFinished = true;
         */

        static void ResetTimeScale()
        {
            instance.timeControl?.Release();
        }

        matcher.Start();
        matcher.MatchStartForward(new CodeMatch(OpCodes.Switch));
        labels = (Label[])matcher.Operand;
        stateValue = labels.Length;
        matcher.Operand = (Label[])[.. labels, afterWaitLabel];

        matcher.MatchStartForward(
            new CodeMatch(OpCodes.Ldloc_2),
            new CodeMatch(OpCodes.Ldc_I4_1),
            new CodeMatch(OpCodes.Call, typeof(SceneLoad).GetProperty(nameof(SceneLoad.IsFinished)).SetMethod)
        );
        matcher.Instruction.labels.Add(afterWaitLabel);
        matcher.InsertAndAdvance(
            new CodeInstruction(OpCodes.Call, ((Delegate)ResetTimeScale).Method),
            new CodeInstruction(OpCodes.Call, ((Delegate)GetLoadDelay).Method),
            new CodeInstruction(OpCodes.Ldc_R4, 0f),
            new CodeInstruction(OpCodes.Ble, afterWaitLabel),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, ((Delegate)GetLoadDelay).Method),
            new CodeInstruction(OpCodes.Newobj, typeof(WaitForSecondsRealtime).GetConstructor([typeof(float)])),
            new CodeInstruction(OpCodes.Stfld, currentField),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldc_I4, stateValue),
            new CodeInstruction(OpCodes.Stfld, stateField),
            new CodeInstruction(OpCodes.Ldc_I4_1),
            new CodeInstruction(OpCodes.Stloc_0),
            new CodeInstruction(OpCodes.Leave, returnLabel)
        );

        static float GetLoadDelay()
        {
            return instance.extraLoadTime.Value;
        }

        return matcher.InstructionEnumeration();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.IsClearMemoryRequired))]
    private static bool SceneLoad_IsClearMemoryRequired(ref bool __result)
    {
        if (instance.forceClearMemory.Value)
        {
            __result = true;
            return false;
        }

        return true;
    }

    #region logging

    [HarmonyPostfix, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.BeginRoutine))]
    private static IEnumerator SceneLoad_BeginRoutine_Postfix(IEnumerator __result, SceneLoad __instance)
    {
        while (__result.MoveNext())
        {
            yield return __result.Current;
        }

        instance.Logger.LogDebug("");

        string header = $"Scene load stats for {__instance.TargetSceneName}";
        instance.Logger.LogDebug(header);
        instance.Logger.LogDebug(new string('=', header.Length));

        string[] phaseNames = Enum.GetNames(typeof(SceneLoad.Phases));

        for (int i = 0; i < phaseNames.Length; i++)
        {
            SceneLoad.PhaseInfo info = __instance.phaseInfos[i];
            if (info.BeginTime != null && info.EndTime != null)
            {
                instance.Logger.LogDebug($"{phaseNames[i]}: {info.EndTime - info.BeginTime}");
            }
        }

        instance.Logger.LogDebug($"TOTAL: {Time.realtimeSinceStartup - __instance.BeginTime}");

        instance.Logger.LogDebug("");
    }

    // Extra verbose logging, meant for development only

    // [HarmonyPostfix, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.RecordBeginTime))]
    // private static void SceneLoad_RecordBeginTime(SceneLoad __instance, SceneLoad.Phases phase)
    // {
    //     instance.Logger.LogDebug($"Begin {phase} at {Time.realtimeSinceStartup}");
    // }
    //
    // [HarmonyPostfix, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.RecordEndTime))]
    // private static void SceneLoad_RecordEndTime(SceneLoad __instance, SceneLoad.Phases phase)
    // {
    //     instance.Logger.LogDebug($"End {phase} at {Time.realtimeSinceStartup} (took {__instance.GetDuration(phase)})");
    // }
    //
    // [HarmonyPrefix, HarmonyPatch(typeof(PlayMakerFSM), nameof(PlayMakerFSM.Start))]
    // private static void PlayMakerFSM_Start()
    // {
    //     instance.Logger.LogDebug($"FSM Start() at {Time.realtimeSinceStartup}");
    // }

    #endregion
}
