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
using Random = UnityEngine.Random;

namespace Silksong.CyclesTest;

[HarmonyPatch]
[BepInAutoPlugin(id: "com.spacemonkeyy.cyclestest")]
public partial class CyclesTestPlugin : BaseUnityPlugin
{
    private static CyclesTestPlugin instance;

    private ConfigEntry<bool> normalizeLoads;
    private ConfigEntry<float> extraLoadTime;
    private ConfigEntry<bool> forceClearMemory;
    private ConfigEntry<float> clearMemoryDelay;

    private float oldTimeScale;

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
        clearMemoryDelay = Config.Bind(
            "General",
            "Clear Memory Delay",
            0.5f,
            "Minimum time in seconds that clearing memory will take."
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
            instance.oldTimeScale = Time.timeScale;
            if (instance.normalizeLoads.Value)
            {
                Time.timeScale = 0f;
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
         * <-- BEGIN INJECTED -->
         * ResetTimeScale();
         * if (GetLoadDelay() > 0f) {
         *     yield return new WaitForSecondsRealtime(GetLoadDelay());
         * }
         * <-- END INJECTED -->
         * this.IsFinished = true;
         */

        static void ResetTimeScale()
        {
            Time.timeScale = instance.oldTimeScale;
        }

        matcher.Start();
        matcher.MatchStartForward(new CodeMatch(OpCodes.Switch));
        Label[] labels = (Label[])matcher.Operand;
        int stateValue = labels.Length;
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

    [HarmonyPostfix, HarmonyPatch(typeof(SceneLoad), nameof(SceneLoad.TryClearMemory))]
    private static IEnumerator SceneLoad_TryClearMemory(IEnumerator __result)
    {
        float endTime = Time.realtimeSinceStartup + instance.clearMemoryDelay.Value;

        while (__result.MoveNext())
        {
            yield return __result.Current;
        }

        if (instance.clearMemoryDelay.Value > 0f)
        {
            // Simulate slow Start() methods of components in the scene
            while (Time.realtimeSinceStartup < endTime)
            {
                float timeLeft = Time.realtimeSinceStartup - endTime;
                yield return new WaitForSecondsRealtime(Mathf.Min(Random.Range(0f, 0.3f), timeLeft));
            }
        }
    }
}
