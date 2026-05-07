using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RogueGenesia.Data;
using RogueGenesia.GameManager.Menu;
using UnityEngine;

namespace SurvivorModeEvents;

public enum EventCategory
{
    /// <summary>Vanilla event known to be safe — toggle defaults ON.</summary>
    VanillaSafe,
    /// <summary>Vanilla event we don't recognize (new in a future game patch). Toggle defaults OFF until we audit it.</summary>
    VanillaUnknown,
    /// <summary>Modded event from outside RogueGenesia.dll. Toggle defaults OFF — opt-in.</summary>
    Modded,
}

internal sealed class DiscoveryReport
{
    public List<Plugin.EventEntry> Entries = new();
    public List<(string TypeName, string Reason)> Blocked = new();
    public List<(string TypeName, string Reason)> ProbeFailed = new();
}

/// <summary>
/// Finds every concrete <see cref="EventRogue"/> subclass in loaded assemblies, applies
/// our hand-curated vanilla safelist/blocklist, runs the safety probe over the rest, and
/// returns a partitioned report. Run once at <c>OnRegisterModdedContent</c> time.
/// </summary>
internal static class EventClassifier
{
    /// <summary>Vanilla events manually verified as safe in survivor mode. Friendly names used in the UI.</summary>
    private static readonly Dictionary<string, string> VanillaSafelist = new()
    {
        { "BankerEvent",                "Banker" },
        { "BardEvent",                  "Bard" },
        { "BonFireEvent",               "Bonfire" },
        { "GamblingMoney_Event",        "Gambling" },
        { "GodStatuetteEvent",          "God Statuette" },
        { "GodStelleEvent",             "God Stele" },
        { "GoldenAltarEvent",           "Golden Altar" },
        { "JaaldTempleEvent",           "Jaald Temple" },
        { "PriestEvent",                "Priest" },
        { "RogueGoblinEvent",           "Rogue Goblin" },
        { "ShopKeeperInvestmentEvent",  "Shopkeeper Investment" },
        { "ShrineOfBalance",            "Shrine of Balance" },
        { "ShrineOfDisorder",           "Shrine of Disorder" },
        { "ShrineOfElementalOrder",     "Shrine of Elemental Order" },
        { "ShrineOfOrder",              "Shrine of Order" },
        { "ShrineOfReconstruction",     "Shrine of Reconstruction" },
        { "SoulForgeEvent",             "Soul Forge" },
        { "TrainerEvent",               "Trainer" },
        { "WanderingCaravanEvent",      "Wandering Caravan" },
    };

    /// <summary>
    /// Hand-curated blocklist for events that *aren't* covered by the ICombatEvent
    /// interface check below — broken / placeholder / Rog-flow-coupled events that the
    /// IL scan also can't always catch.
    /// </summary>
    private static readonly Dictionary<string, string> NonCombatBlocklist = new()
    {
        { "CrystalMineEvent", "Calls EventManager.NextStage with Rog-only ZoneSO state, terminates run" },
        { "DefaultEvent",     "Hardcoded English placeholder text — not a real event" },
        { "LegacyEvent",      "Always returns weight 0; OnLevelLoaded is unfinished dev code" },
    };

    /// <summary>
    /// Combat events implement <c>RogueGenesia.Data.ICombatEvent</c> (vanilla marker).
    /// Resolved by name so a future game patch that drops or renames the interface
    /// only loses this filter (we still have the IL scan + name blocklist).
    /// </summary>
    private static readonly Lazy<Type> CombatEventInterface = new(() =>
        typeof(EventRogue).Assembly.GetType("RogueGenesia.Data.ICombatEvent"));

    /// <summary>
    /// Events that already have a specialized recovery patch (Banker's finalizer fixes
    /// fields rather than aborting). The wildcard finalizer skips these to avoid
    /// double-handling exceptions.
    /// </summary>
    private static readonly HashSet<string> EventsWithSpecificFinalizer = new()
    {
        "BankerEvent",
    };

    public static bool HasSpecificFinalizer(Type eventType) =>
        EventsWithSpecificFinalizer.Contains(eventType.Name);

    /// <summary>
    /// Every concrete EventRogue subclass that declares its own OnLevelLoaded —
    /// includes both primary events (in <c>EventManager.ConstructorInfos</c>) and
    /// sub-events spawned via <c>EventManager.SetEvent(...)</c> mid-flow (e.g.
    /// ShareTalesToBard, ListenToBard). Used to wire the wildcard finalizer broadly
    /// so any sub-event throwing in survivor mode aborts cleanly instead of
    /// soft-locking the player.
    /// </summary>
    public static IEnumerable<Type> AllEventRogueSubclassesWithOwnOnLevelLoaded()
    {
        var eventRogueType = typeof(EventRogue);
        var seen = new HashSet<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!IsScannableAssembly(asm)) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null || t.IsAbstract) continue;
                if (!eventRogueType.IsAssignableFrom(t)) continue;
                if (!seen.Add(t)) continue;
                // Only yield types that override OnLevelLoaded themselves — patching
                // the inherited base method would fire on every subclass via virtual
                // dispatch, which we don't want.
                if (AccessTools.DeclaredMethod(t, "OnLevelLoaded") == null) continue;
                yield return t;
            }
        }
    }

    public static DiscoveryReport DiscoverAndClassify()
    {
        var report = new DiscoveryReport();
        var seen = new HashSet<Type>();
        var rogueGenesiaAssembly = typeof(EventRogue).Assembly;

        var combatInterface = CombatEventInterface.Value;
        foreach (var type in DiscoverEventTypes())
        {
            if (!seen.Add(type)) continue;
            string name = type.Name;

            // Block all combat events, vanilla and modded, in one shot.
            if (combatInterface != null && combatInterface.IsAssignableFrom(type))
            {
                report.Blocked.Add((type.FullName ?? name, "Implements ICombatEvent (combat-style — terminates run)"));
                continue;
            }

            if (NonCombatBlocklist.TryGetValue(name, out var blockReason))
            {
                report.Blocked.Add((name, blockReason));
                continue;
            }

            // Vanilla safelist still goes through the probe: if a future game patch
            // breaks one of the 19, we want it to drop out automatically rather than
            // crash a survivor run.
            var probe = SafetyProbe.Probe(type);
            if (!probe.IsSafe)
            {
                report.ProbeFailed.Add((type.FullName ?? name, probe.Reason));
                continue;
            }

            EventCategory category;
            string displayName;
            if (VanillaSafelist.TryGetValue(name, out var friendlyName))
            {
                category = EventCategory.VanillaSafe;
                displayName = friendlyName;
            }
            else if (type.Assembly == rogueGenesiaAssembly)
            {
                category = EventCategory.VanillaUnknown;
                displayName = "[New] " + name;
            }
            else
            {
                category = EventCategory.Modded;
                displayName = "[Modded] " + name;
            }

            report.Entries.Add(new Plugin.EventEntry
            {
                DisplayName = displayName,
                EventType = type,
                Category = category,
            });
        }

        return report;
    }

    /// <summary>
    /// Vanilla's <see cref="EventManager.ConstructorInfos"/> is the authoritative
    /// list of "primary" events — what the weighted picker rolls between. Sub-events
    /// (e.g. SaveVillagersGiveCardEvent, spawned by SaveVillagersEvent's choice flow)
    /// are intentionally NOT in this list, so they don't pollute our toggle UI.
    ///
    /// <para>Modded events show up automatically as long as the mod calls
    /// <c>EventManager.RegisterEvent(...)</c> at load time, which is the supported
    /// vanilla path for plugging in a new triggerable event.</para>
    ///
    /// <para>Falls back to a full assembly scan only if the registry is somehow
    /// empty (e.g. our discovery ran before <c>GameData.RegisterEventsList</c>).</para>
    /// </summary>
    private static IEnumerable<Type> DiscoverEventTypes()
    {
        var registry = EventManager.ConstructorInfos;
        if (registry != null && registry.Count > 0)
        {
            var seen = new HashSet<Type>();
            foreach (var ctor in registry)
            {
                var t = ctor?.DeclaringType;
                if (t == null || t.IsAbstract) continue;
                if (!typeof(EventRogue).IsAssignableFrom(t)) continue;
                if (seen.Add(t)) yield return t;
            }
            yield break;
        }

        Debug.LogWarning("[SurvivorModeEvents] EventManager.ConstructorInfos is empty — falling back to assembly scan. Modded events that register themselves later may be missed until next launch.");
        foreach (var t in AssemblyScanFallback()) yield return t;
    }

    private static IEnumerable<Type> AssemblyScanFallback()
    {
        var eventRogueType = typeof(EventRogue);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!IsScannableAssembly(asm)) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract) continue;
                if (!eventRogueType.IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                yield return t;
            }
        }
    }

    /// <summary>Skip framework / Unity / Harmony assemblies — they can't host EventRogue subclasses.</summary>
    private static bool IsScannableAssembly(Assembly asm)
    {
        var name = asm.GetName().Name;
        if (string.IsNullOrEmpty(name)) return false;
        return !(
            name == "mscorlib" || name == "netstandard" || name == "System" || name == "0Harmony" ||
            name.StartsWith("System.") || name.StartsWith("Unity.") ||
            name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") ||
            name.StartsWith("Microsoft.") || name.StartsWith("Mono.") ||
            name.StartsWith("nunit.")
        );
    }
}

internal readonly struct ProbeVerdict
{
    public bool IsSafe { get; }
    public string Reason { get; }
    public ProbeVerdict(bool safe, string reason) { IsSafe = safe; Reason = reason; }
    public static ProbeVerdict Safe() => new(true, null);
    public static ProbeVerdict Unsafe(string reason) => new(false, reason);
}

/// <summary>
/// Static safety check for an EventRogue subclass. Two checks: the type is constructible
/// (no ctor exception), and its OnLevelLoaded / NextAnswer don't directly call
/// SceneManager.LoadScene[Async] for a scene other than the additive "Event" scene we
/// already manage. The IL scan is a heuristic: events that hide scene loads in helper
/// methods will pass the probe and only get caught by the runtime finalizer.
/// </summary>
internal static class SafetyProbe
{
    private static readonly string[] MethodsToScan = { "OnLevelLoaded", "NextAnswer" };

    public static ProbeVerdict Probe(Type eventType)
    {
        try { Activator.CreateInstance(eventType); }
        catch (Exception e)
        {
            return ProbeVerdict.Unsafe($"ctor threw {e.GetType().Name}: {Truncate(e.Message, 80)}");
        }

        foreach (var methodName in MethodsToScan)
        {
            var method = AccessTools.Method(eventType, methodName);
            if (method == null) continue;
            var problem = ScanForSceneLoads(method);
            if (problem != null) return ProbeVerdict.Unsafe($"{methodName} {problem}");
        }

        return ProbeVerdict.Safe();
    }

    private static string ScanForSceneLoads(MethodInfo method)
    {
        List<CodeInstruction> instructions;
        try { instructions = PatchProcessor.GetCurrentInstructions(method); }
        catch (Exception e) { return $"IL read failed ({e.GetType().Name})"; }

        for (int i = 0; i < instructions.Count; i++)
        {
            var ci = instructions[i];
            if (ci.opcode != OpCodes.Call && ci.opcode != OpCodes.Callvirt) continue;
            if (ci.operand is not MethodBase target) continue;
            if (!IsSceneLoad(target)) continue;

            // Heuristic: walk back a few instructions for the scene-name string. Works
            // for the common literal pattern; flags everything else conservatively.
            string sceneName = LookBackForLoadedString(instructions, i, 6);
            if (sceneName != "Event")
            {
                return $"calls {target.DeclaringType.Name}.{target.Name}({sceneName ?? "?"}) — only the additive 'Event' scene is supported";
            }
        }
        return null;
    }

    private static bool IsSceneLoad(MethodBase m) =>
        m.DeclaringType?.FullName == "UnityEngine.SceneManagement.SceneManager"
        && (m.Name == "LoadScene" || m.Name == "LoadSceneAsync");

    private static string LookBackForLoadedString(List<CodeInstruction> instructions, int start, int maxLookback)
    {
        for (int i = start - 1; i >= Math.Max(0, start - maxLookback); i--)
        {
            var op = instructions[i].opcode;
            if (op == OpCodes.Ldstr) return instructions[i].operand as string;
            // Constants pushed before the string arg are fine — keep walking back.
            if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1 ||
                op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3 || op == OpCodes.Ldc_I4_4 ||
                op == OpCodes.Ldc_I4_5 || op == OpCodes.Ldc_I4_6 || op == OpCodes.Ldc_I4_7 ||
                op == OpCodes.Ldc_I4_8 || op == OpCodes.Ldc_I4_S || op == OpCodes.Nop)
            {
                continue;
            }
            // Anything else is a non-literal source — give up.
            return null;
        }
        return null;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

/// <summary>
/// Programmatically applied to <c>OnLevelLoaded</c> on every enabled event that doesn't
/// already have a specialized finalizer (currently just BankerEvent). If a modded or
/// new vanilla event throws during its level-load setup while we're driving the flow,
/// swallow the exception and abort cleanly so the player isn't stuck.
/// </summary>
internal static class OnLevelLoadedFinalizer
{
    public static Exception Finalizer(EventRogue __instance, Exception __exception)
    {
        if (__exception == null) return null;
        if (!EventTriggerLoop.SurvivorEventInProgress) return __exception;

        var typeName = __instance != null ? __instance.GetType().Name : "?";
        Debug.LogError($"[SurvivorModeEvents] {typeName}.OnLevelLoaded threw {__exception.GetType().Name}: {__exception.Message} — aborting event");
        EventTriggerLoop.EndEvent();
        return null; // swallow
    }
}
