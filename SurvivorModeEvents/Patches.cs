using System;
using HarmonyLib;
using RogueGenesia.Data;
using RogueGenesia.GameManager;
using RogueGenesia.GameManager.Menu;
using RogueGenesia.UI;
using UnityEngine;

namespace SurvivorModeEvents;

/// <summary>Reset trigger state when a new Survivor run starts.</summary>
[HarmonyPatch(typeof(GameManagerSurvival), nameof(GameManagerSurvival.Start))]
public static class GameManagerSurvivalStartPatch
{
    public static void Postfix()
    {
        EventTriggerLoop.ResetForNewRun();
    }
}

/// <summary>
/// The vanilla Event scene contains its own BlackFader/WhiteFader. When we additively
/// load it, those duplicate Awake()s overwrite <c>Instance</c> with their own self-ref.
/// On UnloadSceneAsync the duplicate is destroyed, so <c>Instance</c> is left pointing
/// at a destroyed Unity object — and the next caller (player death's LoadAsyncScene
/// → BlackFader.Instance.StartTransitionToBlack) NPEs inside StartCoroutine, which
/// hangs the game in a "dead but world still ticking" state.
///
/// Make Awake first-wins: if there's already a live Instance, skip. Unity's overloaded
/// equality treats destroyed objects as equal to null, so genuine scene transitions
/// (where the previous fader is destroyed before the new scene's Awake fires) still
/// install the new instance correctly.
/// </summary>
[HarmonyPatch(typeof(BlackFader), "Awake")]
public static class BlackFaderAwakePatch
{
    public static bool Prefix(BlackFader __instance)
    {
        if (BlackFader.Instance != null && BlackFader.Instance != __instance)
        {
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(WhiteFader), "Awake")]
public static class WhiteFaderAwakePatch
{
    public static bool Prefix(WhiteFader __instance)
    {
        if (WhiteFader.Instance != null && WhiteFader.Instance != __instance)
        {
            return false;
        }
        return true;
    }
}

/// <summary>Each survivor-update tick, give our loop a chance to fire an event.</summary>
[HarmonyPatch(typeof(GameManagerSurvival), nameof(GameManagerSurvival.Update))]
public static class GameManagerSurvivalUpdatePatch
{
    public static void Postfix(GameManagerSurvival __instance)
    {
        EventTriggerLoop.TickFromSurvival(__instance);
    }
}

/// <summary>
/// If the player dies with an event still open (possible via DOT during our 0.001x
/// pause), the additively-loaded Event scene would sit on top of EndOfGame and our
/// menu time-scale modifier would stall its fader. Tear our state down before vanilla
/// starts the death transition coroutine.
/// </summary>
[HarmonyPatch(typeof(GameManagerSurvival), nameof(GameManagerSurvival.OnPlayerDeath))]
public static class GameManagerSurvivalOnPlayerDeathPatch
{
    public static void Prefix()
    {
        EventTriggerLoop.EndEvent();
    }
}

/// <summary>
/// Vanilla flow: clicking "Continue" on an event eventually calls Next_Stage_Confirm,
/// which loads the next stage / zone select / end-of-game scene. None of those fit
/// when the event was triggered mid-Survivor-run, so when our flag is set we skip
/// the vanilla scene-load and instead unload the additively-loaded Event scene and
/// restore the time scale.
/// </summary>
[HarmonyPatch(typeof(EventManager), nameof(EventManager.Next_Stage_Confirm))]
public static class EventManagerNextStageConfirmPatch
{
    public static bool Prefix()
    {
        if (!EventTriggerLoop.SurvivorEventInProgress) return true; // vanilla
        EventTriggerLoop.EndEvent();
        return false; // skip vanilla scene transition
    }
}

/// <summary>
/// Some events (ShrineOfBalance, BankerEvent, CrystalMineEvent, JaaldTempleEvent,
/// LegacyEvent, DefaultEvent) call <c>EventManager.NextStage()</c> directly from their
/// <c>NextAnswer()</c> override instead of going through <c>Next_Stage()</c>. Vanilla
/// <see cref="EventManager.NextStage"/> has the same Rog-only assumption: it casts
/// <c>ActiveDifficulty</c> to <c>RogueModeDifficultySO</c> which is null in Survivor
/// mode → the cast either NPEs or the method's scene-load branch tries to leave the
/// Survivor run. We intercept here and route to the same cleanup that the underscore
/// path uses.
/// </summary>
[HarmonyPatch(typeof(EventManager), nameof(EventManager.NextStage))]
public static class EventManagerNextStagePatch
{
    public static bool Prefix()
    {
        if (!EventTriggerLoop.SurvivorEventInProgress) return true; // vanilla
        EventTriggerLoop.EndEvent();
        return false; // skip vanilla scene transition
    }
}

/// <summary>
/// Vanilla <see cref="BankerEvent.OnLevelLoaded"/> casts <c>ActiveDifficulty</c> to
/// <c>RogueModeDifficultySO</c> and then dereferences it on the next line — fine in Rog
/// mode, NPE in Survivor mode (where the difficulty is a <c>SurvivorsModeDifficultySO</c>).
/// Vanilla executes the first ~7 lines successfully (setting <c>_bankMoneyAvailable</c>
/// and adding Withdraw/Deposit options to the choices list) before the throw — we use a
/// Finalizer to swallow the exception, fill in the missing fields with a survivor-mode
/// safe withdrawal/deposit cap, and append the missing DoNothing choice.
/// </summary>
[HarmonyPatch(typeof(BankerEvent), nameof(BankerEvent.OnLevelLoaded))]
public static class BankerEventOnLevelLoadedPatch
{
    private const double SurvivorModeValCap = 100_000.0;

    public static Exception Finalizer(BankerEvent __instance, Exception __exception)
    {
        if (__exception == null) return null;
        if (!EventTriggerLoop.SurvivorEventInProgress) return __exception;

        try
        {
            var t = Traverse.Create(__instance);
            double bankAvail = t.Field("_bankMoneyAvailable").GetValue<double>();
            double playerGold = GameData.PlayerDatabase[0].Gold;

            t.Field("_maxGoldToWithdraw").SetValue(System.Math.Floor(System.Math.Min(SurvivorModeValCap, bankAvail) * 0.002) * 500.0);
            t.Field("_maxGoldToDeposit").SetValue(System.Math.Floor(System.Math.Min(SurvivorModeValCap, playerGold) * 0.002) * 500.0);

            // Vanilla appends EBankerChoice.DoNothing as the final option after the
            // crash point. The choices list is List<BankerEvent+EBankerChoice>; we
            // resolve the enum type from the list's generic parameter.
            object choices = t.Field("_eventChoices").GetValue();
            if (choices != null)
            {
                var listType = choices.GetType();
                var enumType = listType.GetGenericArguments()[0];
                object doNothing = Enum.Parse(enumType, "DoNothing");
                listType.GetMethod("Add").Invoke(choices, new[] { doNothing });
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[SurvivorModeEvents] BankerEvent finalizer recovery failed: " + e);
        }

        return null; // swallow original exception
    }
}
