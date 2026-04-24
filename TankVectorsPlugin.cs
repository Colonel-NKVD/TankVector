using System;
using System.Collections.Generic;
using System.Linq;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace TankVectors
{
    public class TankVectorsPlugin : RocketPlugin<TankVectorsConfig>
    {
        public static TankVectorsPlugin Instance;
        
        // Словарь захваченных датчиком машин (InstanceID -> Настройки брони)
        public Dictionary<uint, TankArmorProfile> ActiveTankSensors = new Dictionary<uint, TankArmorProfile>();
        
        private HashSet<uint> _debugIgnored = new HashSet<uint>();
        private float _timer = 0f;

        protected override void Load()
        {
            Instance = this;
            Logger.Log("!!! [DEBUG] TANK_VECTORS: ЗАПУСК СИСТЕМЫ РАСЧЕТА ПРОЕКЦИЙ !!!");
            
            // Подписываемся на ПЕРЕХВАТ урона до его нанесения
            VehicleManager.onDamageVehicleRequested += OnDamageVehicleRequested;
        }

        protected override void Unload()
        {
            VehicleManager.onDamageVehicleRequested -= OnDamageVehicleRequested;
            ActiveTankSensors.Clear();
            _debugIgnored.Clear();
            Logger.Log("[ДАТЧИК] Система отключена.");
        }

        // --- ТОТ САМЫЙ ДАТЧИК (Адаптирован для кэширования профилей брони) ---
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
                        {
                            ActiveTankSensors.Remove(vehicle.instanceID);
                            if (Configuration.Instance.EnableDetailedLogging)
                                Logger.Log($"[ДАТЧИК] Сброс трекинга (Уничтожен): {vehicle.asset.name}");
                        }
                        continue;
                    }

                    ushort currentId = vehicle.asset.id;
                    var profile = Configuration.Instance.TrackedTanks.FirstOrDefault(t => t.VehicleId == currentId);

                    if (profile == null) 
                    {
                        if (ActiveTankSensors.ContainsKey(vehicle.instanceID))
                            ActiveTankSensors.Remove(vehicle.instanceID);

                        if (!_debugIgnored.Contains(vehicle.instanceID))
                        {
                            _debugIgnored.Add(vehicle.instanceID);
                            if (Configuration.Instance.EnableDetailedLogging)
                                Logger.Log($"[РАДАР] Игнорирую: {vehicle.asset.name} (ID: {currentId}) - нет профиля брони.");
                        }
                        continue;
                    }

                    // Машина подходит под профиль. Кэшируем.
                    if (!ActiveTankSensors.ContainsKey(vehicle.instanceID))
                    {
                        ActiveTankSensors.Add(vehicle.instanceID, profile);
                        Logger.Log($"[ОБНАРУЖЕНИЕ] Векторная матрица захватила: {vehicle.asset.name} (ID: {currentId}) | Инстанс: {vehicle.instanceID}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[WATCHER ERROR] Сбой обработки инстанса: {ex.Message}");
                }
            }
        }

        // --- СИСТЕМА ОБРАБОТКИ ВЕКТОРОВ И УРОНА ---
        private void OnDamageVehicleRequested(CSteamID instigatorSteamID, InteractableVehicle vehicle, ref ushort pendingTotalDamage, ref bool canDamage, EDamageOrigin damageOrigin)
        {
            if (!canDamage || vehicle == null || pendingTotalDamage == 0) return;

            // Если машина не в нашем словаре (не танк), пропускаем, урон идет стандартно
            if (!ActiveTankSensors.TryGetValue(vehicle.instanceID, out TankArmorProfile armorProfile)) return;

            bool log = Configuration.Instance.EnableDetailedLogging;

            if (log)
            {
                Logger.Log("--------------------------------------------------");
                Logger.Log($"[ВЕКТОР] Инициирован расчет попадания по {vehicle.asset.name}");
                Logger.Log($"[ВЕКТОР] Базовый урон до обработки: {pendingTotalDamage}. Источник: {damageOrigin}");
            }

            // 1. ПРОФЕССИОНАЛЬНОЕ ОПРЕДЕЛЕНИЕ ТОЧКИ АТАКИ
            Player attacker = PlayerTool.getPlayer(instigatorSteamID);
            if (attacker == null)
            {
                if (log) Logger.Log("[ВЕКТОР-АНОМАЛИЯ] Невозможно определить атакующего игрока. Урон не модифицирован.");
                return;
            }

            Vector3 originPos = attacker.transform.position;
            
            // Если стрелок сидит в технике (Танк стреляет по Танку), берем центр ЕГО техники для точного угла
            InteractableVehicle attackerVehicle = attacker.movement.getVehicle();
            if (attackerVehicle != null)
            {
                originPos = attackerVehicle.transform.position;
                if (log) Logger.Log($"[ВЕКТОР] Атакующий находится в технике {attackerVehicle.asset.name}. Используем ее позицию.");
            }

            // 2. ВЕКТОРНАЯ МАТЕМАТИКА
            // Вектор от центра атакуемого танка к источнику атаки
            Vector3 dirToAttacker = (originPos - vehicle.transform.position).normalized;

            // Расчет скалярного произведения (Dot Product)
            float dotForward = Vector3.Dot(vehicle.transform.forward, dirToAttacker);
            float dotUp = Vector3.Dot(vehicle.transform.up, dirToAttacker);
            float dotRight = Vector3.Dot(vehicle.transform.right, dirToAttacker);

            if (log)
            {
                Logger.Log($"[ВЕКТОР-МАТЕМАТИКА] DotForward: {dotForward:F2} | DotUp: {dotUp:F2} | DotRight: {dotRight:F2}");
            }

            // 3. ОПРЕДЕЛЕНИЕ СЕКТОРА ПРОБИТИЯ
            float finalMultiplier = 1.0f;
            string hitZone = "НЕИЗВЕСТНО";

            // Сначала проверяем крышу (вектор сильно направлен вверх)
            // 0.7f означает угол примерно 45 градусов сверху
            if (dotUp > 0.7f) 
            {
                hitZone = "КРЫША (СЧИТАЕТСЯ КОРМОЙ)";
                finalMultiplier = armorProfile.RearAndRoofMultiplier;
            }
            // Затем проверяем лоб (вектор указывает спереди танка)
            else if (dotForward > 0.5f) 
            {
                hitZone = "ЛОБ";
                finalMultiplier = armorProfile.FrontMultiplier;
            }
            // Затем корма (вектор указывает сзади танка)
            else if (dotForward < -0.5f) 
            {
                hitZone = "КОРМА";
                finalMultiplier = armorProfile.RearAndRoofMultiplier;
            }
            // Все остальное - борта (левый и правый математически симметричны, если брать по модулю)
            else 
            {
                hitZone = dotRight > 0 ? "ПРАВЫЙ БОРТ" : "ЛЕВЫЙ БОРТ";
                finalMultiplier = armorProfile.SideMultiplier;
            }

            // 4. ПРИМЕНЕНИЕ КОЭФФИЦИЕНТОВ
            int modifiedDamage = Mathf.RoundToInt(pendingTotalDamage * finalMultiplier);
            
            if (log)
            {
                Logger.Log($"[ВЕКТОР-РЕЗУЛЬТАТ] Зона попадания: {hitZone}");
                Logger.Log($"[ВЕКТОР-РЕЗУЛЬТАТ] Применен множитель: x{finalMultiplier}");
                Logger.Log($"[ВЕКТОР-РЕЗУЛЬТАТ] Итоговый урон: {pendingTotalDamage} -> {modifiedDamage}");
                Logger.Log("--------------------------------------------------");
            }

            // Отправляем новый урон в ядро Unturned
            pendingTotalDamage = (ushort)Mathf.Clamp(modifiedDamage, 0, ushort.MaxValue);
        }
    }
}
