using System.Collections.Generic;
using UnityEngine;

namespace DVW
{
    // Horloge du vieillissement : un tick par vaisseau chargé, l'environnement est
    // calculé une seule fois puis transmis à chaque pièce.
    public class DVWVesselModule : VesselModule
    {
        readonly List<ModuleWeathering> modules = new List<ModuleWeathering>();
        int cachedPartCount = -1;
        float nextTick;

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || !vessel.loaded) return;
            if (Time.time < nextTick) return;

            WeatheringSettings.EnsureLoaded();
            nextTick = Time.time + WeatheringSettings.TickInterval;

            RefreshModules();
            if (modules.Count == 0) return;

            VesselEnv env = VesselEnv.Capture(vessel, Planetarium.GetUniversalTime());
            for (int i = 0; i < modules.Count; i++)
                if (modules[i] != null) modules[i].Tick(env);
        }

        // la liste ne change qu'au découplage / amarrage / destruction de pièces
        void RefreshModules()
        {
            if (vessel.parts.Count == cachedPartCount) return;
            cachedPartCount = vessel.parts.Count;

            modules.Clear();
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                var m = vessel.parts[i].FindModuleImplementing<ModuleWeathering>();
                if (m != null) modules.Add(m);
            }
        }
    }
}
