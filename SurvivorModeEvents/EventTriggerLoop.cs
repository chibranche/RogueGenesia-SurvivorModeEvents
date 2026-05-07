using System;
using System.Collections.Generic;
using RogueGenesia.Data;
using RogueGenesia.GameManager;
using RogueGenesia.GameManager.Menu;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SurvivorModeEvents;

/// <summary>
/// Tracks survivor-run elapsed time and triggers a random enabled event each interval.
/// </summary>
public static class EventTriggerLoop
{
    /// <summary>True while a Survivor-mode event is open. Set when we trigger, cleared after cleanup.</summary>
    public static bool SurvivorEventInProgress { get; private set; }

    private const string EventSceneName = "Event";
    // Same pattern as PauseMenu, which calls GameData.SetMenuTimeScale("Pause", 0f).
    // Our modifier name needs to be unique so we don't collide with vanilla pause.
    private const string TimeScaleModifierName = "SurvivorModeEvents";

    private static float _lastTriggerTime = 0f;
    private static System.Random _rng = new System.Random();

    /// <summary>Reset on a new Survivor run.</summary>
    public static void ResetForNewRun()
    {
        _lastTriggerTime = 0f;
        SurvivorEventInProgress = false;
    }

    /// <summary>Called every frame from the GameManagerSurvival.Update postfix.</summary>
    public static void TickFromSurvival(GameManagerSurvival gms)
    {
        if (gms == null) return;

        float now = gms.SurvivorsModeTiming;

        if (SurvivorEventInProgress) return;
        // Overdrive guard: ReachedRedTimer flips when survival time crosses the
        // difficulty's Duration (final boss defeated, timer goes red), and stays true
        // through the eventual ReachedInfinity (7-day) phase. Cuts off both states
        // with one check.
        if (gms.ReachedRedTimer || GameData.ReachedInfinity) return;
        if (gms.SelectingArtifact) return;
        // Don't trigger during the death sequence: SurvivorsModeTiming freezes when the
        // player is dead, so `now - _lastTriggerTime` could already be over the threshold,
        // and arming our 0.001x menu time scale here would stall the EndOfGame fader.
        if (gms.LocalAvatarData != null && gms.LocalAvatarData.Dead) return;

        float intervalSeconds = Plugin.GetIntervalMinutes() * 60f;
        if (intervalSeconds <= 0f) return;

        if (now - _lastTriggerTime < intervalSeconds) return;

        TryTriggerEvent(gms, now);
    }

    private struct Candidate
    {
        public Plugin.EventEntry Entry;
        public EventRogue Instance;
        public float Weight;
    }

    private static void TryTriggerEvent(GameManagerSurvival gms, float now)
    {
        var enabled = Plugin.GetEnabledEvents();
        if (enabled.Count == 0)
        {
            // Don't keep trying every frame if the player disabled everything.
            _lastTriggerTime = now;
            return;
        }

        // Weighted pick using each event's vanilla GetChanceToGetEvent(). Mirrors
        // EventManager.GetRandomEvent(): every candidate contributes a weight, and
        // the chance of picking it is its_weight / sum_of_weights. Events whose
        // weight calculation throws (Rog-mode-only state) or returns 0 (gated by
        // gold, zone, achievements, etc.) are excluded from this roll.
        var candidates = new List<Candidate>(enabled.Count);
        float totalWeight = 0f;
        foreach (var entry in enabled)
        {
            EventRogue ev;
            try { ev = (EventRogue)Activator.CreateInstance(entry.EventType); }
            catch (Exception e)
            {
                Debug.LogError($"[SurvivorModeEvents] Could not instantiate {entry.EventType.Name}: {e}");
                continue;
            }

            float w;
            try { w = ev.GetChanceToGetEvent(); }
            catch { w = 0f; }

            if (float.IsNaN(w) || float.IsInfinity(w) || w <= 0f) continue;

            candidates.Add(new Candidate { Entry = entry, Instance = ev, Weight = w });
            totalWeight += w;
        }

        if (totalWeight <= 0f)
        {
            // Every candidate is gated out right now (no gold, wrong zone, etc).
            // Skip this milestone and try again at the next one — same behavior
            // as when zero events are toggled on.
            Debug.Log("[SurvivorModeEvents] No event has weight > 0 right now, skipping until next interval");
            _lastTriggerTime = now;
            return;
        }

        float roll = (float)(_rng.NextDouble() * totalWeight);
        Candidate picked = candidates[candidates.Count - 1]; // fallback for rounding edge
        float cumulative = 0f;
        foreach (var c in candidates)
        {
            cumulative += c.Weight;
            if (roll < cumulative) { picked = c; break; }
        }

        Debug.Log($"[SurvivorModeEvents] Triggering '{picked.Entry.DisplayName}' (weight {picked.Weight:F2} / total {totalWeight:F2}) at {now:F1}s");
        _lastTriggerTime = now;
        SurvivorEventInProgress = true;

        // Match the level-up screen's pause: GameManagerFight.GetCardPickUpTimeScale()
        // returns 0.001f (full freeze visually but technically not zero) below soul level
        // 50 and 0f above. The level-up flow lerps to this via TimeControl(); we just
        // snap to it for simplicity. The game's own time-scale system (same one PauseMenu
        // uses with "Pause") routes this through UpdateTimeScale → Time.timeScale.
        float pauseScale = (GameDataGetter.GetLocalPlayer()?._soulLevel?.GetLevel ?? 0) > 50 ? 0f : 0.001f;
        GameData.SetMenuTimeScale(TimeScaleModifierName, pauseScale);

        EventManager.NextEvent = picked.Instance;

        var op = SceneManager.LoadSceneAsync(EventSceneName, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError("[SurvivorModeEvents] LoadSceneAsync returned null - aborting");
            EndEvent();
        }
    }

    /// <summary>
    /// Tear down whatever event state is currently active and resume the run. Safe to
    /// call from any path: normal close (Next_Stage_Confirm / NextStage prefixes),
    /// scene-load failure mid-trigger, or player death with the event still open.
    /// No-op when no event is in progress.
    /// </summary>
    public static void EndEvent()
    {
        if (!SurvivorEventInProgress) return;

        var scene = SceneManager.GetSceneByName(EventSceneName);
        if (scene.IsValid() && scene.isLoaded)
        {
            // Fire-and-forget; Unity drains the unload over the next few frames.
            SceneManager.UnloadSceneAsync(scene);
        }

        // Setting the modifier to 1 removes it from the dict (vanilla pause does the same).
        GameData.SetMenuTimeScale(TimeScaleModifierName, 1f);
        EventManager.NextEvent = null;
        SurvivorEventInProgress = false;
    }
}
