using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SharedEnergy;

// ========================================
// Patch 1: RemoveFullBar（離散消費 - 銃など）
// ========================================
[HarmonyPatch(typeof(ItemBattery), nameof(ItemBattery.RemoveFullBar))]
public class PatchBatteryConsume
{
    static bool Prefix(ItemBattery __instance, int _bars)
    {
        if (ChargingStation.instance == null) return true;

        int cost = Mathf.Max(1, Mathf.RoundToInt((float)_bars * 20f / __instance.batteryBars));

        if (ChargingStation.instance.chargeTotal >= cost)
        {
            DrainStation(cost);
            SharedEnergy.Logger.LogInfo(
                $"[SharedEnergy] RemoveFullBar: ステーション消費 {cost}, 残量: {ChargingStation.instance.chargeTotal}");
            return false; // 武器バッテリーは減らさない
        }

        SharedEnergy.Logger.LogInfo("[SharedEnergy] ステーション不足、武器バッテリーを消費");
        return true;
    }

    internal static void DrainStation(int cost)
    {
        int newTotal = ChargingStation.instance.chargeTotal - cost;
        ChargingStation.instance.chargeTotal = newTotal;
        float newFloat = (float)newTotal / 100f;
        Traverse.Create(ChargingStation.instance).Field("chargeFloat").SetValue(newFloat);
        StatsManager.instance.runStats["chargingStationChargeTotal"] = newTotal;
    }
}

// ========================================
// Patch 2: Update（連続ドレイン - ドローンなど）
// ========================================
[HarmonyPatch(typeof(ItemBattery), "Update")]
public class PatchBatteryContinuousDrain
{
    // フレームごとのドレイン量は小さいので、端数を蓄積して
    // 整数1単位以上になったらステーションから引く
    private static readonly Dictionary<int, float> _saved = new();
    private static readonly Dictionary<int, float> _debt  = new();

    static void Prefix(ItemBattery __instance)
    {
        _saved[__instance.GetInstanceID()] = __instance.batteryLife;
    }

    static void Postfix(ItemBattery __instance)
    {
        if (ChargingStation.instance == null) return;

        int id = __instance.GetInstanceID();
        if (!_saved.TryGetValue(id, out float before)) return;

        float drained = before - __instance.batteryLife;
        if (drained <= 0f) return; // ドレインなし or 充電中

        // ステーションが空なら通常消費させる
        if (ChargingStation.instance.chargeTotal <= 0)
        {
            _debt.Remove(id);
            return;
        }

        // バッテリーを元に戻す（ステーションが肩代わり）
        __instance.batteryLife = before;

        // ステーションコストに換算して蓄積
        // RemoveFullBar の換算レートに合わせる: 全バー(100%) = 20 ステーションチャージ
        float stationCost = drained * 20f / 100f;

        if (!_debt.TryGetValue(id, out float currentDebt))
            currentDebt = 0f;
        currentDebt += stationCost;

        // 整数単位以上たまったらステーションから引く
        if (currentDebt >= 1f)
        {
            int costInt = Mathf.FloorToInt(currentDebt);
            costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
            PatchBatteryConsume.DrainStation(costInt);
            currentDebt -= costInt;

            SharedEnergy.Logger.LogInfo(
                $"[SharedEnergy] 連続ドレイン: ステーション消費 {costInt}, 残量: {ChargingStation.instance.chargeTotal}");
        }

        _debt[id] = currentDebt;
    }
}

// ========================================
// Patch 3: メレー武器のヒット時消費
// ========================================
[HarmonyPatch(typeof(ItemMelee), "SwingHitRPC")]
public class PatchMeleeSwingHit
{
    static void Prefix(ItemMelee __instance, bool durabilityLoss,
        ref float ___durabilityDrain, ref float ___durabilityLossCooldown, ItemBattery ___itemBattery)
    {
        if (!durabilityLoss) return;
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;

        // ステーションが肩代わり → バッテリーを一時的に満タンにしてdrain後に戻す
        // durabilityDrainをステーションコストに換算: 100batteryLife = 20station
        float stationCost = ___durabilityDrain * 20f / 100f;
        int costInt = Mathf.Max(1, Mathf.RoundToInt(stationCost));
        costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
        PatchBatteryConsume.DrainStation(costInt);

        // バッテリーが減らないように満タンに保つ
        ___itemBattery.batteryLife = 100f;
    }
}

[HarmonyPatch(typeof(ItemMelee), "EnemyOrPVPSwingHitRPC")]
public class PatchMeleeEnemySwingHit
{
    static void Prefix(ItemMelee __instance, bool _playerHit,
        ref float ___durabilityDrainOnEnemiesAndPVP, ItemBattery ___itemBattery)
    {
        if (ChargingStation.instance == null) return;
        if (___itemBattery == null) return;
        if (ChargingStation.instance.chargeTotal <= 0) return;

        float stationCost = ___durabilityDrainOnEnemiesAndPVP * 20f / 100f;
        int costInt = Mathf.Max(1, Mathf.RoundToInt(stationCost));
        costInt = Mathf.Min(costInt, ChargingStation.instance.chargeTotal);
        PatchBatteryConsume.DrainStation(costInt);

        ___itemBattery.batteryLife = 100f;
    }
}