using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SharedEnergy;

// ========================================
// Patch 1: RemoveFullBar（銃系）
// ========================================
[HarmonyPatch(typeof(ItemBattery), nameof(ItemBattery.RemoveFullBar))]
public class PatchBatteryConsume
{
    private const string PowerCrystalItemName = "Item Power Crystal";
    private const int FallbackEnergyPerCrystal = 10;
    private const int FallbackMaxCrystals = 10;

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

        SharedEnergy.Logger.LogInfo($"[DrainStation] 消費:{cost}, 現在のchargeTotal:{ChargingStation.instance.chargeTotal}, itemsPurchased:{SemiFunc.StatGetItemsPurchased("Item Power Crystal")}");
    
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

        if (ChargingStation.instance != null)
        {
            ChargingStation.instance.chargeTotal = syncedTotal;
            float chargeFloat = (float)syncedTotal / maxChargeTotal;
            Traverse.Create(ChargingStation.instance).Field("chargeFloat").SetValue(chargeFloat);
            Traverse.Create(ChargingStation.instance).Field("chargeInt").SetValue(syncedCrystalCount);
        }

        StatsManager.instance.runStats["chargingStationChargeTotal"] = syncedTotal;
        StatsManager.instance.runStats["chargingStationCharge"] = syncedCrystalCount;
        StatsManager.instance.itemsPurchased[PowerCrystalItemName] = syncedCrystalCount;

        SharedEnergy.Logger.LogInfo(
            $"[SyncStationState] total={syncedTotal}/{maxChargeTotal}, crystals={syncedCrystalCount}/{GetMaxCrystals()}, runCharge={StatsManager.instance.runStats["chargingStationCharge"]}, purchased={StatsManager.instance.itemsPurchased[PowerCrystalItemName]}");
    }
}

// ========================================
// Patch 2: ItemBattery.Update（連続ドレイン系）
// ========================================
[HarmonyPatch(typeof(ItemBattery), "Update")]
public class PatchBatteryContinuousDrain
{
    internal static readonly Dictionary<int, float> Saved = new();
    internal static readonly Dictionary<int, float> Debt = new();

    static void Prefix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (SemiFunc.RunIsShop()) return;
        Saved[__instance.GetInstanceID()] = __instance.batteryLife;
    }

    static void Postfix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;

        int id = __instance.GetInstanceID();
        if (!Saved.TryGetValue(id, out float before)) return;

        float drained = before - __instance.batteryLife;
        if (drained <= 0f) return;

        if (ChargingStation.instance.chargeTotal <= 0)
        {
            Debt.Remove(id);
            return;
        }

        __instance.batteryLife = before;

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
        PatchBatteryContinuousDrain.Saved[__instance.GetInstanceID()] = __instance.batteryLife;
    }

    static void Postfix(ItemBattery __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;

        int id = __instance.GetInstanceID();
        if (!PatchBatteryContinuousDrain.Saved.TryGetValue(id, out float before)) return;

        float drained = before - __instance.batteryLife;
        if (drained <= 0f) return;

        if (ChargingStation.instance.chargeTotal <= 0)
        {
            PatchBatteryContinuousDrain.Debt.Remove(id);
            return;
        }

        __instance.batteryLife = before;

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

// ========================================
// Patch 3: メレー武器（SwingHitRPC）
// ========================================
[HarmonyPatch(typeof(ItemMelee), "SwingHitRPC")]
public class PatchMeleeSwingHit
{
    private static readonly Dictionary<int, float> Debt = new();

    // durabilityDrainのFieldInfoをキャッシュ
    private static readonly FieldInfo DrainField =
        typeof(ItemMelee).GetField("durabilityDrain", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo CooldownField =
        typeof(ItemMelee).GetField("durabilityLossCooldown", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Postfix(ItemMelee __instance, bool durabilityLoss, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (!durabilityLoss) return;
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;

        float cooldown = (float)CooldownField.GetValue(__instance);
        if (cooldown <= 0f) return;

        float drain = (float)DrainField.GetValue(__instance);

        // バッテリーを戻す（上限クランプあり）
        ___itemBattery.batteryLife = Mathf.Min(100f, ___itemBattery.batteryLife + drain);

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

    internal static void Remove(int id) => Debt.Remove(id);
}

// ========================================
// Patch 4: メレー武器（EnemyOrPVPSwingHitRPC）
// ========================================
[HarmonyPatch(typeof(ItemMelee), "EnemyOrPVPSwingHitRPC")]
public class PatchMeleeEnemySwingHit
{
    private static readonly Dictionary<int, float> Debt = new();

    private static readonly FieldInfo EnemyDrainField =
        typeof(ItemMelee).GetField("durabilityDrainOnEnemiesAndPVP", BindingFlags.Public | BindingFlags.Instance);

    static void Postfix(ItemMelee __instance, bool _playerHit, ItemBattery ___itemBattery)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;

        float drain = (float)EnemyDrainField.GetValue(__instance);

        ___itemBattery.batteryLife = Mathf.Min(100f, ___itemBattery.batteryLife + drain);

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

    internal static void Remove(int id) => Debt.Remove(id);
}

[HarmonyPatch(typeof(StatsManager), nameof(StatsManager.ItemPurchase))]
public class PatchCrystalPurchase
{
    static void Postfix(string itemName)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (itemName != "Item Power Crystal") return;
        if (StatsManager.instance == null) return;

        int current = StatsManager.instance.runStats["chargingStationChargeTotal"];
        int crystalCount = StatsManager.instance.itemsPurchased["Item Power Crystal"];
        int energyPerCrystal = PatchBatteryConsume.GetEnergyPerCrystal();
        int maxChargeForCurrentCrystals = Mathf.Min(crystalCount * energyPerCrystal, PatchBatteryConsume.GetMaxChargeTotal());
        int newTotal = Mathf.Clamp(current + energyPerCrystal, 0, maxChargeForCurrentCrystals);

        SharedEnergy.Logger.LogInfo(
            $"[PatchCrystalPurchase] beforeTotal={current}, energyPerCrystal={energyPerCrystal}, crystalsAfterPurchase={crystalCount}, maxForCrystals={maxChargeForCurrentCrystals}, targetTotal={newTotal}");

        PatchBatteryConsume.SyncStationState(newTotal, crystalCount);
    }
}
