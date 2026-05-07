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
    /// when the event triggers.
    /// </summary>
    public sealed class EventEntry
    {
        public string DisplayName;
        public Type EventType;
    }

    // (display name, type name). Resolved at first access via reflection so a vanilla
    // class rename only loses that one event with a logged warning instead of refusing
    // to load the whole mod (which is what happens when typeof(...) fails at JIT time).
    // LegacyEvent intentionally not listed: vanilla GetChanceToGetEvent returns 0f and
    // its OnLevelLoaded computes m_experienceReward via Math.Floor(...) without
    // assigning — looks like dev code abandoned mid-edit.
    private static readonly (string DisplayName, string TypeName)[] EventDescriptors =
    {
        ("Banker",                    "BankerEvent"),
        ("Bard",                      "BardEvent"),
        ("Bonfire",                   "BonFireEvent"),
        ("Gambling",                  "GamblingMoney_Event"),
        ("God Statuette",             "GodStatuetteEvent"),
        ("God Stele",                 "GodStelleEvent"),
        ("Golden Altar",              "GoldenAltarEvent"),
        ("Jaald Temple",              "JaaldTempleEvent"),
        ("Priest",                    "PriestEvent"),
        ("Rogue Goblin",              "RogueGoblinEvent"),
        ("Shopkeeper Investment",     "ShopKeeperInvestmentEvent"),
        ("Shrine of Balance",         "ShrineOfBalance"),
        ("Shrine of Disorder",        "ShrineOfDisorder"),
        ("Shrine of Elemental Order", "ShrineOfElementalOrder"),
        ("Shrine of Order",           "ShrineOfOrder"),
        ("Shrine of Reconstruction",  "ShrineOfReconstruction"),
        ("Soul Forge",                "SoulForgeEvent"),
        ("Trainer",                   "TrainerEvent"),
        ("Wandering Caravan",         "WanderingCaravanEvent"),
    };

    private const string EventTypeNamespace = "RogueGenesia.Data";

    private static EventEntry[] _allEvents;
    public static EventEntry[] AllEvents => _allEvents ??= ResolveEvents();

    private static EventEntry[] ResolveEvents()
    {
        var asm = typeof(EventRogue).Assembly;
        var resolved = new List<EventEntry>(EventDescriptors.Length);
        foreach (var (display, typeName) in EventDescriptors)
        {
            var type = asm.GetType(EventTypeNamespace + "." + typeName);
            if (type == null || !typeof(EventRogue).IsAssignableFrom(type))
            {
                Debug.LogWarning($"[SurvivorModeEvents] Event type '{typeName}' not found or not an EventRogue — skipping. Game update may have renamed/removed it.");
                continue;
            }
            resolved.Add(new EventEntry { DisplayName = display, EventType = type });
        }
        return resolved.ToArray();
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
        foreach (var entry in AllEvents)
        {
            RegisterEventToggle(entry);
        }

        // OptionData is constructed before mods load, so our keys aren't in its
        // dicts even though the modded options exist. Seed defaults for any keys
        // that haven't been written by previous saves so the menu UI and our
        // runtime reads see the right state.
        if (GameData.OptionData != null)
        {
            if (!GameData.OptionData.HasValue(IntervalOptionId))
            {
                GameData.OptionData.SetValueFloat(IntervalOptionId, DefaultIntervalMinutes);
            }
            foreach (var entry in AllEvents)
            {
                string key = OptionIdForEvent(entry);
                if (!GameData.OptionData.HasValue(key))
                {
                    GameData.OptionData.SetValueInt(key, 1); // default ON
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
        if (GameData.OptionData == null) return true;
        string key = OptionIdForEvent(entry);
        // Defensive: if the key isn't in the dict (e.g. on first launch before
        // OnRegisterModdedContent has run, or if seeding failed) treat it as ON
        // since true is the default.
        if (!GameData.OptionData.HasValue(key)) return true;
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
        var tooltip = LocList("Allow the " + entry.DisplayName + " event to trigger in Survivor mode.");

        var toggle = ModOption.MakeToggleOption(
            name: OptionIdForEvent(entry),
            LocalisedName: name,
            defaultValue: true,
            LocalisedTooltip: tooltip);

        ModOption.AddModOption(toggle, OptionsTab, OptionsTab);
    }

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
