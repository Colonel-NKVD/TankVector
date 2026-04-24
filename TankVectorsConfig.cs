using Rocket.API;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace TankVectors
{
    public class TankArmorProfile
    {
        [XmlAttribute]
        public ushort VehicleId;
        public float FrontMultiplier;
        public float SideMultiplier;
        public float RearAndRoofMultiplier; // Корма и крыша считаются одним параметром
    }

    public class TankVectorsConfig : IRocketPluginConfiguration
    {
        [XmlArrayItem(ElementName = "ArmorProfile")]
        public List<TankArmorProfile> TrackedTanks;

        public bool EnableDetailedLogging; // Переключатель для полного логирования

        public void LoadDefaults()
        {
            EnableDetailedLogging = true;
            TrackedTanks = new List<TankArmorProfile>
            {
                new TankArmorProfile 
                { 
                    VehicleId = 120, // Пример: Лесной танк
                    FrontMultiplier = 0.3f, // Лоб получает только 30% урона
                    SideMultiplier = 1.0f,  // Борта получают 100% урона
                    RearAndRoofMultiplier = 1.5f // Корма и крыша получают 150% урона
                },
                new TankArmorProfile 
                { 
                    VehicleId = 121, 
                    FrontMultiplier = 0.5f, 
                    SideMultiplier = 1.2f, 
                    RearAndRoofMultiplier = 2.0f 
                }
            };
        }
    }
}
