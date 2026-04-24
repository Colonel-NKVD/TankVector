using System;
using System.Collections.Generic;
using System.Linq;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using HarmonyLib;
using Logger = Rocket.Core.Logging.Logger;

namespace TankVectors
{
    public class TankVectorsPlugin : RocketPlugin<TankVectorsConfig>
    {
        public static TankVectorsPlugin Instance;
        public const string HarmonyInstanceId = "com.tankvectors.patch";
        private Harmony _harmony;
        
        public Dictionary<uint, TankArmorProfile> ActiveTankSensors = new Dictionary<uint, TankArmorProfile>();
        private HashSet<uint> _debugIgnored = new HashSet<uint>();
        private float _timer = 0f;

        protected override void Load()
        {
            Instance = this;
            Logger.Log("!!! [DEBUG] TANK_VECTORS: ЗАПУСК СИСТЕМЫ (HARMONY PATCH) !!!");
            
            _harmony = new Harmony(HarmonyInstanceId);
            _harmony.PatchAll(); 
            
            Logger.Log("[ВЕКТОР] Метод нанесения урона успешно перехвачен.");
        }

        protected override void Unload()
        {
            _harmony.UnpatchAll(HarmonyInstanceId);
            
            ActiveTankSensors.Clear();
            _debugIgnored.Clear();
            Logger.Log("[ДАТЧИК] Система отключена.");
        }

        public void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;

            if (VehicleManager.vehicles == null) return;

            for (int i = VehicleManager.vehicles.Count - 1; i >= 0; i--)
            {
                try 
                {
                    var vehicle = VehicleManager.vehicles[i];
                    if (vehicle == null || vehicle.asset == null) continue;

                    if (vehicle.isExploded || vehicle.health == 0)
                    {
                        if (ActiveTankSensors.ContainsKey(vehicle.instanceID))
                            ActiveTankSensors.Remove(vehicle.instanceID);
                        continue;
                    }

                    ushort currentId = vehicle.asset.id;
                    var profile = Configuration.Instance.TrackedTanks.FirstOrDefault(t => t.VehicleId == currentId);

                    if (profile == null) 
                    {
                        if (ActiveTankSensors.ContainsKey(vehicle.instanceID))
                            ActiveTankSensors.Remove(vehicle.instanceID);
                        continue;
                    }

                    if (!ActiveTankSensors.ContainsKey(vehicle.instanceID))
                    {
                        ActiveTankSensors.Add(vehicle.instanceID, profile);
                        Logger.Log($"[ОБНАРУЖЕНИЕ] Векторная матрица захватила: {vehicle.asset.name} ({vehicle.instanceID})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[WATCHER ERROR] Сбой датчика: {ex.Message}");
                }
            }
        }
    }

    // --- ИСПРАВЛЕННЫЙ HARMONY PATCH ---
    // Указываем точные типы аргументов метода damage
    [HarmonyPatch(typeof(VehicleManager), "damage", 
        new Type[] { typeof(InteractableVehicle), typeof(float), typeof(float), typeof(bool), typeof(CSteamID), typeof(EDamageOrigin) })]
    public static class VehicleDamagePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InteractableVehicle vehicle, ref float damage, float times, bool canRepair, CSteamID instigatorSteamID, EDamageOrigin damageOrigin)
        {
            if (vehicle == null || damage <= 0 || TankVectorsPlugin.Instance == null) return true;
            if (!TankVectorsPlugin.Instance.ActiveTankSensors.TryGetValue(vehicle.instanceID, out TankArmorProfile armorProfile)) return true;

            Player attacker = PlayerTool.getPlayer(instigatorSteamID);
            if (attacker == null) return true;

            Vector3 originPos = attacker.movement.getVehicle()?.transform.position ?? attacker.transform.position;
            Vector3 dirToAttacker = (originPos - vehicle.transform.position).normalized;

            // Расчет углов
            float dotForward = Vector3.Dot(vehicle.transform.forward, dirToAttacker);
            float dotUp = Vector3.Dot(vehicle.transform.up, dirToAttacker);
            
            // Определение зоны
            string hitZone = "SIDE";
            float finalMultiplier = armorProfile.SideMultiplier;

            if (dotUp > 0.7f || dotForward < -0.5f) 
            { 
                hitZone = "REAR/ROOF"; 
                finalMultiplier = armorProfile.RearAndRoofMultiplier; 
            }
            else if (dotForward > 0.5f) 
            { 
                hitZone = "FRONT"; 
                finalMultiplier = armorProfile.FrontMultiplier; 
            }

            // --- ЛОГИРОВАНИЕ ---
            Logger.Log($"[VECTOR DEBUG] Target: {vehicle.asset.name} | Zone: {hitZone}");
            Logger.Log($"[VECTOR DEBUG] DotForward: {dotForward:F2} | DotUp: {dotUp:F2}");
            Logger.Log($"[VECTOR DEBUG] Damage: {damage:F1} -> {damage * finalMultiplier:F1} (x{finalMultiplier})");
            // -------------------

            damage *= finalMultiplier;

            return true;
        }
    }
}
