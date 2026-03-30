using HarmonyLib;
using UnityEngine;

namespace SharedEnergy;

[HarmonyPatch(typeof(ItemBattery), nameof(ItemBattery.RemoveFullBar))]
public class PatchBatteryConsume
{
    static bool Prefix(ItemBattery __instance, int _bars)
    {
        // ChargingStationがなければ元の処理
        if (ChargingStation.instance == null) return true;

        int cost = Mathf.Max(1, Mathf.RoundToInt((float)_bars * 20f / __instance.batteryBars));

        if (ChargingStation.instance.chargeTotal >= cost)
        {
            // ステーションのエネルギーを消費
            int newTotal = ChargingStation.instance.chargeTotal - cost;
            ChargingStation.instance.chargeTotal = newTotal;

            // chargeFloat（private）も更新
            float newFloat = (float)newTotal / 100f;
            Traverse.Create(ChargingStation.instance).Field("chargeFloat").SetValue(newFloat);

            // StatsManagerも更新（これがないと毎フレーム上書きされる）
            StatsManager.instance.runStats["chargingStationChargeTotal"] = newTotal;

            SharedEnergy.Logger.LogInfo($"[SharedEnergy] ステーション消費: {cost}, 残量: {newTotal}");

            // 武器バッテリーはそのまま（消費しない）
            return false;
        }

        // ステーションが足りなければ武器バッテリーを通常通り消費
        SharedEnergy.Logger.LogInfo($"[SharedEnergy] ステーション不足、武器バッテリーを消費");
        SharedEnergy.Logger.LogInfo($"[SharedEnergy] bars:{_bars}/{__instance.batteryBars}, cost:{cost}, pool:{ChargingStation.instance.chargeTotal}");
        return true;
    }
}