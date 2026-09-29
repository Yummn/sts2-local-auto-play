using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace LocalAutoPlay;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    internal static MegaCrit.Sts2.Core.Logging.Logger Log { get; } =
        new("LocalAutoPlay", LogType.Generic);

    public static void Initialize()
    {
        new Harmony("LocalAutoPlay").PatchAll();
        Log.Info("[LocalAutoPlay] v0.5.8 loaded; bounded forecast and validated plan reuse, no potions.");
    }
}

[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
internal static class CombatUiActivatePatch
{
    [HarmonyPostfix]
    private static void Postfix(NCombatUi __instance, CombatState state)
    {
        try { AutoPlayPanel.Attach(__instance); }
        catch (Exception ex) { MainFile.Log.Error($"[LocalAutoPlay] UI attach failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Deactivate))]
internal static class CombatUiDeactivatePatch
{
    [HarmonyPostfix]
    private static void Postfix(NCombatUi __instance)
    {
        try { __instance.GetNodeOrNull<AutoPlayPanel>(AutoPlayPanel.NodeName)?.QueueFree(); }
        catch { }
    }
}
