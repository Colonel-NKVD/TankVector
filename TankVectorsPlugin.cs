using System;
using System.Collections.Generic;
using System.Linq;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using HarmonyLib; // Не забудь добавить ссылку на 0Harmony.dll
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
            
            // Инициализируем Harmony и применяем патчи
            _harmony = new Harmony(HarmonyInstanceId);
            _harmony.PatchAll(); 
            
            Logger.Log("[ВЕКТОР] Метод нанесения урона успешно перехвачен.");
        }

        protected override void Unload()
        {
            // Снимаем патчи при выгрузке
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

    // --- HARMONY PATCH: ПЕРЕХВАТ УРОНА ---
    [HarmonyPatch(typeof(VehicleManager), "damage")]
    public static class VehicleDamagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(InteractableVehicle vehicle, ref float damage, ref float repair, ref bool canRepair, CSteamID instigator, EDamageOrigin damageOrigin)
        {
            // Если урон нулевой или машина не в списке отслеживаемых — ничего не делаем
            if (vehicle == null || damage <= 0 || TankVectorsPlugin.Instance == null) return;
            if (!TankVectorsPlugin.Instance.ActiveTankSensors.TryGetValue(vehicle.instanceID, out TankArmorProfile armorProfile)) return;

            bool log = TankVectorsPlugin.Instance.Configuration.Instance.EnableDetailedLogging;

            // 1. Определение атакующего
            Player attacker = PlayerTool.getPlayer(instigator);
            if (attacker == null) return;

            Vector3 originPos = attacker.transform.position;
            InteractableVehicle attackerVehicle = attacker.movement.getVehicle();
            if (attackerVehicle != null) originPos = attackerVehicle.transform.position;

            // 2. Векторная математика
            Vector3 dirToAttacker = (originPos - vehicle.transform.position).normalized;
            float dotForward = Vector3.Dot(vehicle.transform.forward, dirToAttacker);
            float dotUp = Vector3.Dot(vehicle.transform.up, dirToAttacker);
            float dotRight = Vector3.Dot(vehicle.transform.right, dirToAttacker);

            // 3. Определение сектора и множителя
            float finalMultiplier = 1.0f;
            string hitZone = "ЛЕВЫЙ/ПРАВЫЙ БОРТ";

            if (dotUp > 0.7f) 
            {
                hitZone = "КРЫША (КОРМА)";
                finalMultiplier = armorProfile.RearAndRoofMultiplier;
            }
            else if (dotForward > 0.5f) 
            {
                hitZone = "ЛОБ";
                finalMultiplier = armorProfile.FrontMultiplier;
            }
            else if (dotForward < -0.5f) 
            {
                hitZone = "КОРМА";
                finalMultiplier = armorProfile.RearAndRoofMultiplier;
            }
            else 
            {
                finalMultiplier = armorProfile.SideMultiplier;
            }

            // 4. Модификация урона (через ref мы меняем значение в самом методе игры)
            float originalDamage = damage;
            damage *= finalMultiplier;

            if (log)
            {
                Logger.Log("--------------------------------------------------");
                Logger.Log($"[HARMONY ВЕКТОР] Попадание по: {vehicle.asset.name}");
                Logger.Log($"[HARMONY ВЕКТОР] Зона: {hitZone} | Множитель: x{finalMultiplier}");
                Logger.Log($"[HARMONY ВЕКТОР] Урон: {originalDamage} -> {damage}");
                Logger.Log("--------------------------------------------------");
            }
        }
    }
}
