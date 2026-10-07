using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DVW
{
    // Nature de la surface : pilote la réaction à la chaleur et aux UV (shader + simulation)
    // Foil : ruban métallisé (Kapton aluminisé) collé sur un ablatif, comme le module de commande Apollo
    public enum SurfaceMaterial { Paint = 0, Steel = 1, Ablator = 2, SolarPanel = 3, Foil = 4 }

    public class BodyDust
    {
        public Color color = new Color(0.55f, 0.45f, 0.33f);
        public float rate = 0.5f;
    }

    // Lit le nœud WEATHERING_CONFIG (Config/Weathering.cfg) ; valeurs par défaut sinon.
    public static class WeatheringSettings
    {
        // général
        public static float TickInterval = 1f;
        public static float MaxPhysicsDt = 5f;

        // vieillissement spatial (UV / radiations)
        public static bool  UVEnabled = true;
        public static float UVRate = 1f;
        public static float UVTimeConstantYears = 4f;
        public static float UVMax = 0.6f;
        public static float SunlitFractionOnRails = 0.7f;
        public static float AtmosphereUVFactor = 0.1f;

        // rentrée
        public static bool  ReentryEnabled = true;
        public static float SootRate = 1f;
        public static float AblatorCharLoad = 250000f;   // K·s au-dessus de la pyrolyse pour ~63 % de carbonisation

        // cinétique d'Arrhenius : ~63 % de dégradation après RefTime secondes à RefTemp
        public static float PaintActivationEnergy = 100000f;   // J/mol (résines époxy / polyuréthane)
        public static float PaintRefTemp = 650f;               // K
        public static float PaintRefTime = 120f;               // s
        public static float FoilActivationEnergy = 150000f;    // J/mol (Kapton + adhésif)
        public static float FoilRefTemp = 700f;
        public static float FoilRefTime = 120f;

        // poussière
        public static bool  DustEnabled = true;
        public static float DustRate = 1f;

        // panneaux solaires : 0 = purement visuel
        public static float SolarPanelDegradation = 0f;

        static readonly float[] materialUV = { 1f, 0.15f, 0.5f, 1.5f, 0.3f };   // indexé par SurfaceMaterial
        static readonly Dictionary<string, BodyDust> dust = new Dictionary<string, BodyDust>();
        static readonly BodyDust defaultDust = new BodyDust();
        static bool loaded;

        public static float MaterialUVRate(SurfaceMaterial m) { EnsureLoaded(); return materialUV[(int)m]; }

        public static BodyDust GetDust(string body)
        {
            EnsureLoaded();
            BodyDust d;
            return dust.TryGetValue(body, out d) ? d : defaultDust;
        }

        public static void EnsureLoaded()
        {
            if (loaded || GameDatabase.Instance == null) return;
            loaded = true;

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("WEATHERING_CONFIG");
            if (nodes == null || nodes.Length == 0)
            {
                Debug.LogWarning("[DVW] WEATHERING_CONFIG absent : valeurs par défaut");
                return;
            }
            ConfigNode n = nodes[0];

            Read(n, "TickInterval", ref TickInterval);
            Read(n, "MaxPhysicsDt", ref MaxPhysicsDt);
            Read(n, "UVEnabled", ref UVEnabled);
            Read(n, "UVRate", ref UVRate);
            Read(n, "UVTimeConstantYears", ref UVTimeConstantYears);
            Read(n, "UVMax", ref UVMax);
            Read(n, "SunlitFractionOnRails", ref SunlitFractionOnRails);
            Read(n, "AtmosphereUVFactor", ref AtmosphereUVFactor);
            Read(n, "ReentryEnabled", ref ReentryEnabled);
            Read(n, "SootRate", ref SootRate);
            Read(n, "AblatorCharLoad", ref AblatorCharLoad);
            Read(n, "PaintActivationEnergy", ref PaintActivationEnergy);
            Read(n, "PaintRefTemp", ref PaintRefTemp);
            Read(n, "PaintRefTime", ref PaintRefTime);
            Read(n, "FoilActivationEnergy", ref FoilActivationEnergy);
            Read(n, "FoilRefTemp", ref FoilRefTemp);
            Read(n, "FoilRefTime", ref FoilRefTime);
            Read(n, "DustEnabled", ref DustEnabled);
            Read(n, "DustRate", ref DustRate);
            Read(n, "SolarPanelDegradation", ref SolarPanelDegradation);

            TickInterval = Mathf.Max(0.1f, TickInterval);
            UVTimeConstantYears = Mathf.Max(0.01f, UVTimeConstantYears);
            AblatorCharLoad = Mathf.Max(1f, AblatorCharLoad);
            PaintRefTemp = Mathf.Max(1f, PaintRefTemp);
            PaintRefTime = Mathf.Max(0.01f, PaintRefTime);
            FoilRefTemp = Mathf.Max(1f, FoilRefTemp);
            FoilRefTime = Mathf.Max(0.01f, FoilRefTime);

            foreach (ConfigNode m in n.GetNodes("MATERIAL"))
            {
                SurfaceMaterial sm;
                if (!TryParseMaterial(m.GetValue("name"), out sm)) continue;
                Read(m, "uvRate", ref materialUV[(int)sm]);
            }

            foreach (ConfigNode b in n.GetNodes("BODY_DUST"))
            {
                string name = b.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                var d = new BodyDust();
                if (b.HasValue("color")) d.color = ConfigNode.ParseColor(b.GetValue("color"));
                Read(b, "rate", ref d.rate);
                dust[name] = d;
            }

            Debug.Log("[DVW] réglages chargés (" + dust.Count + " corps)");
        }

        public static bool TryParseMaterial(string s, out SurfaceMaterial m)
        {
            m = SurfaceMaterial.Paint;
            if (string.IsNullOrEmpty(s)) return false;
            try { m = (SurfaceMaterial)Enum.Parse(typeof(SurfaceMaterial), s, true); return true; }
            catch (ArgumentException) { return false; }
        }

        static void Read(ConfigNode n, string key, ref float v)
        {
            float r;
            string s = n.GetValue(key);
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) v = r;
        }

        static void Read(ConfigNode n, string key, ref bool v)
        {
            bool r;
            string s = n.GetValue(key);
            if (s != null && bool.TryParse(s, out r)) v = r;
        }
    }
}
