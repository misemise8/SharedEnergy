using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SharedEnergy;

internal readonly struct BatterySnapshot
{
    internal BatterySnapshot(float batteryLife, int batteryLifeInt, int batteryLifeCountBars, int batteryLifeCountBarsPrev, int currentBars)
    {
        BatteryLife = batteryLife;
        BatteryLifeInt = batteryLifeInt;
        BatteryLifeCountBars = batteryLifeCountBars;
        BatteryLifeCountBarsPrev = batteryLifeCountBarsPrev;
        CurrentBars = currentBars;
    }

    internal float BatteryLife { get; }
    internal int BatteryLifeInt { get; }
    internal int BatteryLifeCountBars { get; }
    internal int BatteryLifeCountBarsPrev { get; }
    internal int CurrentBars { get; }
}

internal static class BatteryStateUtil
{
    private static readonly MethodInfo BatteryUpdateBarsMethod =
        typeof(ItemBattery).GetMethod("BatteryUpdateBars", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static BatterySnapshot Capture(ItemBattery itemBattery)
    {
        var traverse = Traverse.Create(itemBattery);
        return new BatterySnapshot(
            itemBattery.batteryLife,
            itemBattery.batteryLifeInt,
            traverse.Field("batteryLifeCountBars").GetValue<int>(),
            traverse.Field("batteryLifeCountBarsPrev").GetValue<int>(),
            itemBattery.currentBars);
    }

    internal static void Restore(ItemBattery itemBattery, BatterySnapshot state)
    {
        itemBattery.batteryLife = state.BatteryLife;
        itemBattery.batteryLifeInt = state.BatteryLifeInt;
        itemBattery.currentBars = state.CurrentBars;

        var traverse = Traverse.Create(itemBattery);
        traverse.Field("batteryLifeCountBars").SetValue(state.BatteryLifeCountBars);
        traverse.Field("batteryLifeCountBarsPrev").SetValue(state.BatteryLifeCountBarsPrev);

        ItemAttributes? attributes = itemBattery.GetComponent<ItemAttributes>();
        if (!string.IsNullOrEmpty(attributes?.instanceName))
        {
            SemiFunc.StatSetBattery(attributes.instanceName, Mathf.RoundToInt(state.BatteryLife));
        }

        BatteryUpdateBarsMethod?.Invoke(itemBattery, new object[] { state.BatteryLifeInt });
    }
}

// ========================================
// Patch 1: RemoveFullBar（銃系）
// ========================================
[HarmonyPatch(typeof(ItemBattery), nameof(ItemBattery.RemoveFullBar))]
public class PatchBatteryConsume
{
    private const string PowerCrystalItemName = "Item Power Crystal";
    private const int FallbackEnergyPerCrystal = 10;
    private const int FallbackMaxCrystals = 10;
    private static int? LastSyncedChargeTotal;
    private static int? LastSyncedCrystalCount;

    static bool Prefix(ItemBattery __instance, int _bars)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return true;
        if (SemiFunc.RunIsShop()) return true;
        if (ChargingStation.instance == null) return true;
        if (StatsManager.instance == null) return true;

        int cost = Mathf.Max(1, Mathf.RoundToInt((float)_bars * 20f / __instance.batteryBars));

        if (ChargingStation.instance.chargeTotal >= cost)
        {
            DrainStation(cost);
            return false;
        }

        return true;
    }

    internal static void DrainStation(int cost)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;
        if (StatsManager.instance == null) return;

        SharedEnergy.LogDebug($"[DrainStation] 消費:{cost}, 現在のchargeTotal:{ChargingStation.instance.chargeTotal}, itemsPurchased:{SemiFunc.StatGetItemsPurchased("Item Power Crystal")}");
    
        int newTotal = Mathf.Max(0, ChargingStation.instance.chargeTotal - cost);
        SyncStationState(newTotal);
    }

    internal static int GetEnergyPerCrystal()
    {
        if (ChargingStation.instance == null) return FallbackEnergyPerCrystal;
        return Traverse.Create(ChargingStation.instance).Field("energyPerCrystal").GetValue<int>();
    }

    internal static int GetMaxCrystals()
    {
        if (ChargingStation.instance == null) return FallbackMaxCrystals;
        return Traverse.Create(ChargingStation.instance).Field("maxCrystals").GetValue<int>();
    }

    internal static int GetMaxChargeTotal()
    {
        return GetEnergyPerCrystal() * GetMaxCrystals();
    }

    internal static int CalculateCrystalCount(int chargeTotal)
    {
        int energyPerCrystal = GetEnergyPerCrystal();
        int maxCrystals = GetMaxCrystals();
        return Mathf.Clamp(Mathf.CeilToInt((float)chargeTotal / energyPerCrystal), 0, maxCrystals);
    }

    internal static void SyncStationState(int chargeTotal, int? crystalCount = null)
    {
        if (StatsManager.instance == null) return;

        int maxChargeTotal = GetMaxChargeTotal();
        int syncedTotal = Mathf.Clamp(chargeTotal, 0, maxChargeTotal);
        int syncedCrystalCount = crystalCount ?? CalculateCrystalCount(syncedTotal);
        syncedCrystalCount = Mathf.Clamp(syncedCrystalCount, 0, GetMaxCrystals());
        bool shouldBreakCrystals = false;

        if (ChargingStation.instance != null)
        {
            ChargingStation.instance.chargeTotal = syncedTotal;
            float chargeFloat = (float)syncedTotal / maxChargeTotal;
            Traverse.Create(ChargingStation.instance).Field("chargeFloat").SetValue(chargeFloat);
            int currentVisualCrystals = ChargingStation.instance.crystals.Count;
            shouldBreakCrystals = currentVisualCrystals > syncedCrystalCount;
        }

        if (shouldBreakCrystals)
        {
            SyncCrystalVisuals(syncedCrystalCount);
        }

        if (ChargingStation.instance != null)
        {
            Traverse.Create(ChargingStation.instance).Field("chargeInt").SetValue(syncedCrystalCount);
        }

        StatsManager.instance.runStats["chargingStationChargeTotal"] = syncedTotal;
        StatsManager.instance.runStats["chargingStationCharge"] = syncedCrystalCount;
        StatsManager.instance.itemsPurchased[PowerCrystalItemName] = syncedCrystalCount;
        LastSyncedChargeTotal = syncedTotal;
        LastSyncedCrystalCount = syncedCrystalCount;

        SharedEnergy.LogDebug(
            $"[SyncStationState] total={syncedTotal}/{maxChargeTotal}, crystals={syncedCrystalCount}/{GetMaxCrystals()}, runCharge={StatsManager.instance.runStats["chargingStationCharge"]}, purchased={StatsManager.instance.itemsPurchased[PowerCrystalItemName]}");
    }

    internal static void ResetCachedSync()
    {
        LastSyncedChargeTotal = null;
        LastSyncedCrystalCount = null;
    }

    internal static int GetPurchaseBaselineTotal(int rawTotal)
    {
        int clampedRawTotal = Mathf.Clamp(rawTotal, 0, GetMaxChargeTotal());
        if (!LastSyncedChargeTotal.HasValue) return clampedRawTotal;
        return Mathf.Min(clampedRawTotal, LastSyncedChargeTotal.Value);
    }

    internal static int GetPurchaseBaselineCrystalCount(int purchasedAfter)
    {
        int prePurchaseRaw = Mathf.Clamp(purchasedAfter - 1, 0, GetMaxCrystals());
        if (!LastSyncedCrystalCount.HasValue) return prePurchaseRaw;
        return Mathf.Min(prePurchaseRaw, LastSyncedCrystalCount.Value);
    }

    private static void SyncCrystalVisuals(int targetCrystalCount)
    {
        if (ChargingStation.instance == null) return;
        if (SemiFunc.RunIsShop()) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        int currentVisualCrystals = ChargingStation.instance.crystals.Count;
        int crystalsToBreak = Mathf.Max(0, currentVisualCrystals - targetCrystalCount);
        if (crystalsToBreak <= 0) return;

        int logicalCountBeforeBreak = Mathf.Clamp(targetCrystalCount + crystalsToBreak, 0, GetMaxCrystals());
        Traverse.Create(ChargingStation.instance).Field("chargeInt").SetValue(logicalCountBeforeBreak);
        StatsManager.instance.runStats["chargingStationCharge"] = logicalCountBeforeBreak;
        StatsManager.instance.itemsPurchased[PowerCrystalItemName] = logicalCountBeforeBreak;

        MethodInfo? destroyCrystal = typeof(ChargingStation).GetMethod("DestroyCrystal", BindingFlags.NonPublic | BindingFlags.Instance);
        if (destroyCrystal == null) return;

        for (int i = 0; i < crystalsToBreak; i++)
        {
            destroyCrystal.Invoke(ChargingStation.instance, null);
        }
    }
}

[HarmonyPatch(typeof(ChargingStation), "Start")]
public class PatchChargingStationStartNormalize
{
    static void Prefix(ChargingStation __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (StatsManager.instance == null) return;

        int energyPerCrystal = Traverse.Create(__instance).Field("energyPerCrystal").GetValue<int>();
        int maxCrystals = Traverse.Create(__instance).Field("maxCrystals").GetValue<int>();
        int maxChargeTotal = energyPerCrystal * maxCrystals;

        int rawTotal = StatsManager.instance.runStats["chargingStationChargeTotal"];
        int rawCrystalCount = StatsManager.instance.itemsPurchased["Item Power Crystal"];

        int normalizedTotal = Mathf.Clamp(rawTotal, 0, maxChargeTotal);
        int requiredCrystalCount = Mathf.Clamp(Mathf.CeilToInt((float)normalizedTotal / energyPerCrystal), 0, maxCrystals);
        int normalizedCrystalCount = Mathf.Clamp(Mathf.Max(rawCrystalCount, requiredCrystalCount), 0, maxCrystals);

        if (rawTotal != normalizedTotal || rawCrystalCount != normalizedCrystalCount)
        {
            SharedEnergy.LogDebug(
                $"[ChargingStationStartNormalize] rawTotal={rawTotal}, rawCrystals={rawCrystalCount} -> total={normalizedTotal}, crystals={normalizedCrystalCount}");
        }

        PatchBatteryConsume.SyncStationState(normalizedTotal, normalizedCrystalCount);
    }
}

// ========================================
// Patch 2: ItemBattery.Update（連続ドレイン系）
// ========================================
[HarmonyPatch(typeof(ItemBattery), "Update")]
public class PatchBatteryContinuousDrain
{
    internal static readonly Dictionary<int, BatterySnapshot> Saved = new();
    internal static readonly Dictionary<int, float> Debt = new();

    static void Prefix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (SemiFunc.RunIsShop()) return;
        Saved[__instance.GetInstanceID()] = BatteryStateUtil.Capture(__instance);
    }

    static void Postfix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;

        int id = __instance.GetInstanceID();
        if (!Saved.TryGetValue(id, out BatterySnapshot before)) return;

        float drained = before.BatteryLife - __instance.batteryLife;
        if (drained <= 0f) return;

        if (ChargingStation.instance.chargeTotal <= 0)
        {
            Debt.Remove(id);
            return;
        }

        BatteryStateUtil.Restore(__instance, before);

        if (!Debt.TryGetValue(id, out float currentDebt))
            currentDebt = 0f;
        currentDebt += drained * 20f / 100f;

        if (currentDebt >= 1f)
        {
            int costInt = Mathf.FloorToInt(currentDebt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            currentDebt -= costInt;
        }

        Debt[id] = currentDebt;
    }
}


[HarmonyPatch(typeof(ItemBattery), "FixedUpdate")]
public class PatchBatteryFixedUpdate
{
    static void Prefix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (SemiFunc.RunIsShop()) return;
        PatchBatteryContinuousDrain.Saved[__instance.GetInstanceID()] = BatteryStateUtil.Capture(__instance);
    }

    static void Postfix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;

        int id = __instance.GetInstanceID();
        if (!PatchBatteryContinuousDrain.Saved.TryGetValue(id, out BatterySnapshot before)) return;

        float drained = before.BatteryLife - __instance.batteryLife;
        if (drained <= 0f) return;

        if (ChargingStation.instance.chargeTotal <= 0)
        {
            PatchBatteryContinuousDrain.Debt.Remove(id);
            return;
        }

        BatteryStateUtil.Restore(__instance, before);

        if (!PatchBatteryContinuousDrain.Debt.TryGetValue(id, out float currentDebt))
            currentDebt = 0f;
        currentDebt += drained * 20f / 100f;

        if (currentDebt >= 1f)
        {
            int costInt = Mathf.FloorToInt(currentDebt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            currentDebt -= costInt;
        }

        PatchBatteryContinuousDrain.Debt[id] = currentDebt;
    }
}

[HarmonyPatch(typeof(ItemBattery), "OnDestroy")]
public class PatchBatteryCleanup
{
    static void Postfix(ItemBattery __instance)
    {
        int id = __instance.GetInstanceID();
        PatchBatteryContinuousDrain.Saved.Remove(id);
        PatchBatteryContinuousDrain.Debt.Remove(id);
        PatchDroneDirectBatteryDrain.Remove(__instance);
    }
}

// ========================================
// Patch 3: メレー武器（SwingHitRPC）
// ========================================
[HarmonyPatch(typeof(ItemMelee), "SwingHitRPC")]
public class PatchMeleeSwingHit
{
    private static readonly Dictionary<int, float> Debt = new();
    private static readonly Dictionary<int, BatterySnapshot> SavedBattery = new();

    // durabilityDrainのFieldInfoをキャッシュ
    private static readonly FieldInfo DrainField =
        typeof(ItemMelee).GetField("durabilityDrain", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo CooldownField =
        typeof(ItemMelee).GetField("durabilityLossCooldown", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Prefix(ItemMelee __instance, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (___itemBattery == null) return;
        SavedBattery[__instance.GetInstanceID()] = BatteryStateUtil.Capture(___itemBattery);
    }

    static void Postfix(ItemMelee __instance, bool durabilityLoss, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!durabilityLoss) return;
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;
        if (!SavedBattery.TryGetValue(__instance.GetInstanceID(), out var before)) return;

        float cooldown = (float)CooldownField.GetValue(__instance);
        if (cooldown <= 0f) return;

        float drain = before.BatteryLife - ___itemBattery.batteryLife;
        if (drain <= 0f) return;

        // バッテリーを戻す（上限クランプあり）
        BatteryStateUtil.Restore(___itemBattery, before);

        int id = __instance.GetInstanceID();
        if (!Debt.TryGetValue(id, out float debt)) debt = 0f;
        debt += drain * 20f / 100f;

        if (debt >= 1f)
        {
            int costInt = Mathf.FloorToInt(debt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            debt -= costInt;
        }

        Debt[id] = debt;
    }

    internal static void Remove(int id)
    {
        Debt.Remove(id);
        SavedBattery.Remove(id);
    }

    internal static void Clear()
    {
        Debt.Clear();
        SavedBattery.Clear();
    }
}

// ========================================
// Patch 4: メレー武器（EnemyOrPVPSwingHitRPC）
// ========================================
[HarmonyPatch(typeof(ItemMelee), "EnemyOrPVPSwingHitRPC")]
public class PatchMeleeEnemySwingHit
{
    private static readonly Dictionary<int, float> Debt = new();
    private static readonly Dictionary<int, BatterySnapshot> SavedBattery = new();

    private static readonly FieldInfo EnemyDrainField =
        typeof(ItemMelee).GetField("durabilityDrainOnEnemiesAndPVP", BindingFlags.Public | BindingFlags.Instance);

    static void Prefix(ItemMelee __instance, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (___itemBattery == null) return;
        SavedBattery[__instance.GetInstanceID()] = BatteryStateUtil.Capture(___itemBattery);
    }

    static void Postfix(ItemMelee __instance, bool _playerHit, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;
        if (!SavedBattery.TryGetValue(__instance.GetInstanceID(), out var before)) return;

        float drain = before.BatteryLife - ___itemBattery.batteryLife;
        if (drain <= 0f) return;

        BatteryStateUtil.Restore(___itemBattery, before);

        int id = __instance.GetInstanceID();
        if (!Debt.TryGetValue(id, out float debt)) debt = 0f;
        debt += drain * 20f / 100f;

        if (debt >= 1f)
        {
            int costInt = Mathf.FloorToInt(debt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            debt -= costInt;
        }

        Debt[id] = debt;
    }

    internal static void Remove(int id)
    {
        Debt.Remove(id);
        SavedBattery.Remove(id);
    }

    internal static void Clear()
    {
        Debt.Clear();
        SavedBattery.Clear();
    }
}

[HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ItemPurchase))]
public class PatchCrystalPurchase
{
    static void Postfix(string itemName)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (itemName != "Item Power Crystal") return;
        if (StatsManager.instance == null) return;

        int rawTotal = StatsManager.instance.runStats["chargingStationChargeTotal"];
        int purchasedAfter = Mathf.Max(StatsManager.instance.itemsPurchased["Item Power Crystal"], 0);
        int current = PatchBatteryConsume.GetPurchaseBaselineTotal(rawTotal);
        int energyPerCrystal = PatchBatteryConsume.GetEnergyPerCrystal();
        int baselineCrystalCount = PatchBatteryConsume.GetPurchaseBaselineCrystalCount(purchasedAfter);
        int crystalCount = Mathf.Min(PatchBatteryConsume.GetMaxCrystals(), baselineCrystalCount + 1);
        int maxChargeForCurrentCrystals = Mathf.Min(crystalCount * energyPerCrystal, PatchBatteryConsume.GetMaxChargeTotal());
        int newTotal = Mathf.Clamp(current + energyPerCrystal, 0, maxChargeForCurrentCrystals);

        SharedEnergy.LogDebug(
            $"[PatchCrystalPurchase] rawTotal={rawTotal}, baselineTotal={current}, purchasedAfter={purchasedAfter}, baselineCrystals={baselineCrystalCount}, energyPerCrystal={energyPerCrystal}, crystalsAfterPurchase={crystalCount}, maxForCrystals={maxChargeForCurrentCrystals}, targetTotal={newTotal}");

        PatchBatteryConsume.SyncStationState(newTotal, crystalCount);
    }
}

[HarmonyPatch]
public static class PatchDroneDirectBatteryDrain
{
    private static readonly Dictionary<int, BatterySnapshot> Saved = new();
    private static readonly Dictionary<int, float> Debt = new();

    internal static void Capture(ItemBattery itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (itemBattery == null) return;

        Saved[itemBattery.GetInstanceID()] = BatteryStateUtil.Capture(itemBattery);
    }

    internal static void Redirect(ItemBattery itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (itemBattery == null) return;

        int id = itemBattery.GetInstanceID();
        if (!Saved.TryGetValue(id, out var before)) return;

        float drained = before.BatteryLife - itemBattery.batteryLife;
        if (drained <= 0f) return;

        if (ChargingStation.instance == null || ChargingStation.instance.chargeTotal <= 0)
        {
            return;
        }

        BatteryStateUtil.Restore(itemBattery, before);

        if (!Debt.TryGetValue(id, out float currentDebt))
            currentDebt = 0f;
        currentDebt += drained * 20f / 100f;

        if (currentDebt >= 1f)
        {
            int costInt = Mathf.FloorToInt(currentDebt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            currentDebt -= costInt;
        }

        Debt[id] = currentDebt;
    }

    internal static void Remove(ItemBattery itemBattery)
    {
        if (itemBattery == null) return;

        int id = itemBattery.GetInstanceID();
        Saved.Remove(id);
        Debt.Remove(id);
    }

    internal static void Clear()
    {
        Saved.Clear();
        Debt.Clear();
    }
}

[HarmonyPatch(typeof(ItemDroneFeather), "BatteryDrain")]
public class PatchDroneFeatherBatteryDrain
{
    static void Prefix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Capture(___itemBattery);
    }

    static void Postfix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Redirect(___itemBattery);
    }
}

[HarmonyPatch(typeof(ItemDroneTorque), "BatteryDrain")]
public class PatchDroneTorqueBatteryDrain
{
    static void Prefix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Capture(___itemBattery);
    }

    static void Postfix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Redirect(___itemBattery);
    }
}

[HarmonyPatch(typeof(ItemDroneZeroGravity), "Update")]
public class PatchDroneZeroGravityUpdate
{
    static void Prefix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Capture(___itemBattery);
    }

    static void Postfix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Redirect(___itemBattery);
    }
}

[HarmonyPatch(typeof(ItemDroneZeroGravity), "FixedUpdate")]
public class PatchDroneZeroGravityFixedUpdate
{
    static void Prefix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Capture(___itemBattery);
    }

    static void Postfix(ItemBattery ___itemBattery)
    {
        PatchDroneDirectBatteryDrain.Redirect(___itemBattery);
    }
}

[HarmonyPatch(typeof(ChargingStation), "ChargingStationCrystalBrokenRPC")]
public class PatchChargingStationCrystalBrokenClamp
{
    static void Postfix()
    {
        if (StatsManager.instance == null) return;

        int current = StatsManager.instance.itemsPurchased["Item Power Crystal"];
        if (current >= 0) return;

        StatsManager.instance.itemsPurchased["Item Power Crystal"] = 0;
        SharedEnergy.LogDebug("[ChargingStationCrystalBrokenClamp] corrected negative crystal count back to 0");
    }
}

[HarmonyPatch(typeof(ItemMelee), "OnDestroy")]
public class PatchMeleeCleanup
{
    static void Postfix(ItemMelee __instance)
    {
        int id = __instance.GetInstanceID();
        PatchMeleeSwingHit.Remove(id);
        PatchMeleeEnemySwingHit.Remove(id);
    }
}

[HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ResetAllStats))]
public class PatchStatsResetCleanup
{
    static void Prefix()
    {
        PatchBatteryConsume.ResetCachedSync();
        PatchBatteryContinuousDrain.Saved.Clear();
        PatchBatteryContinuousDrain.Debt.Clear();
        PatchDroneDirectBatteryDrain.Clear();
        PatchMeleeSwingHit.Clear();
        PatchMeleeEnemySwingHit.Clear();
    }
}

[HarmonyPatch(typeof(StatsManager), nameof(StatsManager.LoadGame))]
public class PatchStatsLoadCleanup
{
    static void Prefix()
    {
        PatchBatteryConsume.ResetCachedSync();
        PatchBatteryContinuousDrain.Saved.Clear();
        PatchBatteryContinuousDrain.Debt.Clear();
        PatchDroneDirectBatteryDrain.Clear();
        PatchMeleeSwingHit.Clear();
        PatchMeleeEnemySwingHit.Clear();
    }
}
