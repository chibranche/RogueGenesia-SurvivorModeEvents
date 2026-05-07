using System;
using System.Collections.Generic;
using HarmonyLib;
using ModGenesia;
using RogueGenesia.Data;
using UnityEngine;

namespace SurvivorModeEvents;

public class Plugin : RogueGenesiaMod
{
    public const string IntervalOptionId = "sme_interval_minutes";
    public const float DefaultIntervalMinutes = 5f;
    public const float MinIntervalMinutes = 1f;
    public const float MaxIntervalMinutes = 60f;

    private const string HarmonyId = "chibranche.survivormodeevents";
    private const string OptionsTab = "Survivor Mode Events";

    private static Harmony _harmony;

    /// <summary>
    /// One entry per event the mod can fire in Survivor mode. Display name is what the
    /// player sees on the toggle, EventType is the EventRogue subclass we instantiate
    /// when the event triggers, Category drives the default-on/off behaviour.
    /// </summary>
    public sealed class EventEntry
    {
        public string DisplayName;
        public Type EventType;
        public EventCategory Category;
    }

    private static EventEntry[] _allEvents;

    /// <summary>
    /// All events offered as toggles. Built once at first access via reflection scan
    /// over loaded assemblies (so modded events join automatically) plus a static
    /// safety probe (so events that look unsafe never appear).
    /// </summary>
    public static EventEntry[] AllEvents => _allEvents ??= DiscoverAndLog();

    private static EventEntry[] DiscoverAndLog()
    {
        var report = EventClassifier.DiscoverAndClassify();

        int safe = 0, vanillaUnknown = 0, modded = 0;
        foreach (var e in report.Entries)
        {
            switch (e.Category)
            {
                case EventCategory.VanillaSafe:    safe++; break;
                case EventCategory.VanillaUnknown: vanillaUnknown++; break;
                case EventCategory.Modded:         modded++; break;
            }
        }

        Debug.Log($"[SurvivorModeEvents] Event discovery: {safe} vanilla-safe (default ON), " +
                  $"{vanillaUnknown} new vanilla (default OFF), {modded} modded (default OFF), " +
                  $"{report.Blocked.Count} blocklisted, {report.ProbeFailed.Count} probe-failed");

        foreach (var (typeName, reason) in report.Blocked)
            Debug.Log($"[SurvivorModeEvents]   blocked: {typeName} — {reason}");
        foreach (var (typeName, reason) in report.ProbeFailed)
            Debug.LogWarning($"[SurvivorModeEvents]   rejected: {typeName} — {reason}");
        foreach (var entry in report.Entries)
        {
            if (entry.Category == EventCategory.Modded || entry.Category == EventCategory.VanillaUnknown)
                Debug.Log($"[SurvivorModeEvents]   discovered ({entry.Category}): {entry.EventType.FullName}");
        }

        return report.Entries.ToArray();
    }

    public override void OnModLoaded(ModData modData)
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(Plugin).Assembly);
        }
        catch (Exception e)
        {
            Debug.LogError("[SurvivorModeEvents] Failed to apply Harmony patches: " + e);
        }
    }

    public override void OnRegisterModdedContent()
    {
        RegisterIntervalSlider();

        var entries = AllEvents; // triggers discovery + log
        foreach (var entry in entries)
        {
            RegisterEventToggle(entry);
        }

        // Wildcard OnLevelLoaded finalizer over *all* EventRogue subclasses (primary
        // events AND sub-events reachable via EventManager.SetEvent — e.g. the bard's
        // ShareTalesToBard / ListenToBard). If anything throws during setup while our
        // flow is driving, abort cleanly instead of leaving the player stuck.
        ApplyOnLevelLoadedFinalizer();

        // OptionData is constructed before mods load, so our keys aren't in its dicts
        // even though the modded options exist. Seed defaults for any keys that haven't
        // been written by previous saves.
        if (GameData.OptionData != null)
        {
            if (!GameData.OptionData.HasValue(IntervalOptionId))
            {
                GameData.OptionData.SetValueFloat(IntervalOptionId, DefaultIntervalMinutes);
            }
            foreach (var entry in entries)
            {
                string key = OptionIdForEvent(entry);
                if (!GameData.OptionData.HasValue(key))
                {
                    GameData.OptionData.SetValueInt(key, DefaultEnabledFor(entry) ? 1 : 0);
                }
            }
        }
    }

    public override void OnModUnloaded()
    {
        if (_harmony != null)
        {
            _harmony.UnpatchAll(HarmonyId);
            _harmony = null;
        }
    }

    private static void ApplyOnLevelLoadedFinalizer()
    {
        var finalizerMethod = AccessTools.Method(typeof(OnLevelLoadedFinalizer), nameof(OnLevelLoadedFinalizer.Finalizer));
        if (finalizerMethod == null)
        {
            Debug.LogError("[SurvivorModeEvents] Could not resolve OnLevelLoadedFinalizer.Finalizer — wildcard guard disabled");
            return;
        }
        var hmFinalizer = new HarmonyMethod(finalizerMethod);

        int patched = 0;
        foreach (var type in EventClassifier.AllEventRogueSubclassesWithOwnOnLevelLoaded())
        {
            if (EventClassifier.HasSpecificFinalizer(type)) continue;

            var target = AccessTools.DeclaredMethod(type, "OnLevelLoaded");
            if (target == null) continue;

            try
            {
                _harmony.Patch(target, finalizer: hmFinalizer);
                patched++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SurvivorModeEvents] Failed to apply wildcard finalizer to {type.FullName}: {e.Message}");
            }
        }
        Debug.Log($"[SurvivorModeEvents] Wildcard OnLevelLoaded finalizer applied to {patched} event types");
    }

    private static bool DefaultEnabledFor(EventEntry entry) =>
        entry.Category == EventCategory.VanillaSafe;

    public static string OptionIdForEvent(EventEntry entry) => "sme_event_" + entry.EventType.Name;

    public static float GetIntervalMinutes()
    {
        if (GameData.OptionData == null) return DefaultIntervalMinutes;
        float v = GameData.OptionData.GetValueFloat(IntervalOptionId);
        if (v < MinIntervalMinutes) return DefaultIntervalMinutes;
        return Mathf.Clamp(v, MinIntervalMinutes, MaxIntervalMinutes);
    }

    public static bool IsEventEnabled(EventEntry entry)
    {
        // Pre-OptionData fallback (e.g. we're queried before OnRegisterModdedContent) —
        // and post-seed fallback if a key somehow isn't in the dict — both fall through
        // to the entry's category-default so unknown/modded events stay opt-in.
        if (GameData.OptionData == null) return DefaultEnabledFor(entry);
        string key = OptionIdForEvent(entry);
        if (!GameData.OptionData.HasValue(key)) return DefaultEnabledFor(entry);
        return GameData.OptionData.GetValueInt(key) >= 1;
    }

    public static List<EventEntry> GetEnabledEvents()
    {
        var list = new List<EventEntry>();
        foreach (var entry in AllEvents)
        {
            if (IsEventEnabled(entry)) list.Add(entry);
        }
        return list;
    }

    private static void RegisterIntervalSlider()
    {
        var name = LocList("Event Interval (minutes)");
        var tooltip = LocList(
            "How often (in minutes of survived time) an event triggers in Survivor mode. "
            + "Default 5 minutes. Events do not fire during Overdrive (after the final boss).");

        var slider = ModOption.MakeSliderDisplayValueOption(
            name: IntervalOptionId,
            LocalisedName: name,
            minValue: MinIntervalMinutes,
            maxValue: MaxIntervalMinutes,
            defaultValue: DefaultIntervalMinutes,
            steps: 59,
            displayPercent: false,
            LocalisedTooltip: tooltip);

        ModOption.AddModOption(slider, OptionsTab, OptionsTab);
    }

    private static void RegisterEventToggle(EventEntry entry)
    {
        var name = LocList(entry.DisplayName);
        var tooltip = LocList(BuildTooltip(entry));

        var toggle = ModOption.MakeToggleOption(
            name: OptionIdForEvent(entry),
            LocalisedName: name,
            defaultValue: DefaultEnabledFor(entry),
            LocalisedTooltip: tooltip);

        ModOption.AddModOption(toggle, OptionsTab, OptionsTab);
    }

    private static string BuildTooltip(EventEntry entry) => entry.Category switch
    {
        EventCategory.VanillaSafe =>
            $"Allow the {entry.DisplayName} event to trigger in Survivor mode.",
        EventCategory.VanillaUnknown =>
            $"Allow {entry.EventType.Name} (a vanilla event our compatibility list doesn't recognize yet) to trigger in Survivor mode. Default off — enable at your own risk.",
        EventCategory.Modded =>
            $"Allow {entry.EventType.Name} (a modded event from {entry.EventType.Assembly.GetName().Name}) to trigger in Survivor mode. Default off — enable at your own risk.",
        _ => $"Allow {entry.EventType.Name} to trigger in Survivor mode.",
    };

    private static LocalizationDataList LocList(string englishValue)
    {
        return new LocalizationDataList(englishValue)
        {
            localization = new List<LocalizationData> {
                new LocalizationData { Key = "en", Value = englishValue }
            }
        };
    }
}
