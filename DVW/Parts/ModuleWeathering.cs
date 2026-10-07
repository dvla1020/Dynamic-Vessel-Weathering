using System;
using System.Globalization;
using KSP.Localization;
using UnityEngine;

namespace DVW
{
    public class ModuleWeathering : PartModule
    {
        // --- état persistant (6 directions : +X -X +Y -Y +Z -Z, repère pièce) ---
        [KSPField(isPersistant = true)] public int reentries;
        [KSPField(isPersistant = true)] public double lastUT;
        [KSPField(isPersistant = true)] public double uvExposure;   // secondes équivalentes au soleil de Kerbin
        [KSPField(isPersistant = true)] public string material = "Auto";
        public float[] peakTemp = new float[6];   // température de peau max atteinte par face (K)
        public float[] charAcc = new float[6];    // dégradation thermique (0..1) : carbonisation, brûlure
        public float[] sootAcc = new float[6];
        public float[] dustAcc = new float[6];
        public Vector3 flowDir;                   // direction moyenne du flux de rentrée (stries)
        public float fade;                        // décoloration UV, dérivée de uvExposure

        // --- réglages de simulation ---
        const float TempStart = 500f;
        const float TempFull = 1800f;
        const float SootBaseRate = 0.0010f;
        const float ShadowCheckTemp = 450f;       // en dessous, inutile de chercher l'ombre aérodynamique
        const float MinDamageTemp = 400f;         // en dessous, dégradation thermique négligeable
        const float DustBaseRate = 0.0005f;
        const float ReentryEnter = 0.25f, ReentryExit = 0.10f;
        const float ReentryMinMach = 2.5f;
        const float VisibleTemp = 450f;           // en dessous : aucune trace de chaleur
        const float DefaultPyrolysisTemp = 600f;  // si la pièce n'a pas de ModuleAblator

        static readonly Vector3[] Axes =
            { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        // --- affichage debug (clic droit sur la pièce) ---
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Field_Material")]
        public string dbgMat = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Field_Severity")]
        public string dbgSev = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Field_PeakTemp")]
        public string dbgHeat = "";
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "#DVW_Field_Exposed")]
        public string dbgExp = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Field_Damage")]
        public string dbgChar = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Field_State")]
        public string dbgState = "";

        OverlayController overlay;
        SurfaceMaterial surface;
        ModuleDeployableSolarPanel solarPanel;
        float solarBaseEff = 1f;
        float pyrolysisTemp = DefaultPyrolysisTemp;
        float lastSig = -1f;
        bool inReentry;

        // tampons de calcul thermique (aucune allocation par tick)
        readonly float[] faceT = new float[6];
        readonly float[] exposed = { 1f, 1f, 1f, 1f, 1f, 1f };
        readonly float[] radius = { 1f, 1f, 1f, 1f, 1f, 1f };
        Vector3 localCenter, localHalfExt;
        bool boundsValid;

        // ---------- cycle de vie ----------
        public override void OnStart(StartState state)
        {
            EnsureArrays();
            WeatheringSettings.EnsureLoaded();
            ResolveMaterial();
            if (!HighLogic.LoadedSceneIsFlight && !HighLogic.LoadedSceneIsEditor) return;

            solarPanel = part.FindModuleImplementing<ModuleDeployableSolarPanel>();
            if (solarPanel != null)
            {
                // valeur du prefab : jamais déjà dégradée par nous
                var prefabPanel = part.partInfo.partPrefab.FindModuleImplementing<ModuleDeployableSolarPanel>();
                solarBaseEff = prefabPanel != null ? prefabPanel.efficiencyMult : 1f;
            }

            // la carbonisation commence là où KSP commence à consommer l'ablatif
            var ablator = part.FindModuleImplementing<ModuleAblator>();
            if (ablator != null) pyrolysisTemp = (float)ablator.ablationTempThresh;

            overlay = new OverlayController(part);
            overlay.Build();
            Debug.Log("[DVW] " + part.partInfo.name + " (" + surface + ") overlays créés : " + overlay.Count);

            if (HighLogic.LoadedSceneIsFlight)
            {
                // rattrapage du temps passé déchargé / sur rails depuis la dernière sauvegarde
                double ut = Planetarium.GetUniversalTime();
                if (lastUT > 0.0 && ut > lastUT)
                    SpaceAging(VesselEnv.Capture(vessel, ut), ut - lastUT);
                lastUT = ut;
            }
            UpdateFade();
            Apply(true);
        }

        public void OnDestroy()
        {
            if (overlay != null) overlay.Destroy();
            overlay = null;
        }

        void ResolveMaterial()
        {
            if (WeatheringSettings.TryParseMaterial(material, out surface)) { dbgMat = surface.ToString(); return; }

            var engine = part.FindModuleImplementing<ModuleEngines>();
            if (part.FindModuleImplementing<ModuleDeployableSolarPanel>() != null) surface = SurfaceMaterial.SolarPanel;
            else if (engine != null && !engine.throttleLocked) surface = SurfaceMaterial.Steel;   // pas les boosters à poudre
            else if (part.FindModuleImplementing<ModuleAblator>() != null && part.CrewCapacity == 0) surface = SurfaceMaterial.Ablator;
            else surface = SurfaceMaterial.Paint;
            dbgMat = "Auto → " + surface;
        }

        // ---------- simulation (appelée par DVWVesselModule) ----------
        public void Tick(VesselEnv env)
        {
            double dtFull = env.ut - lastUT;
            lastUT = env.ut;
            if (dtFull <= 0.0) return;

            // le vieillissement spatial accepte n'importe quel pas de temps (time warp compris)
            SpaceAging(env, dtFull);

            // le reste dépend de la physique : impossible sur rails
            if (env.physics)
            {
                float dt = (float)Math.Min(dtFull, WeatheringSettings.MaxPhysicsDt);
                if (WeatheringSettings.ReentryEnabled) Thermal(env, dt);
                if (WeatheringSettings.DustEnabled) SurfaceDust(env, dt);
            }

            Apply(false);
        }

        void SpaceAging(VesselEnv env, double dt)
        {
            if (!WeatheringSettings.UVEnabled) return;
            uvExposure += dt * env.uvExposure * WeatheringSettings.UVRate
                        * WeatheringSettings.MaterialUVRate(surface);
            UpdateFade();
        }

        // courbe qui ralentit : rapide au début, plafonnée à UVMax
        void UpdateFade()
        {
            double tau = WeatheringSettings.UVTimeConstantYears * KSPUtil.dateTimeFormatter.Year;
            fade = WeatheringSettings.UVMax * (float)(1.0 - Math.Exp(-uvExposure / tau));
        }

        void Thermal(VesselEnv env, float dt)
        {
            float tExp = (float)part.skinTemperature;
            bool flowing = env.inAtmo && env.srfSpeed > 1.0 && !part.ShieldedFromAirstream;
            Vector3 flow = flowing ? part.transform.InverseTransformDirection(env.srfVel).normalized : Vector3.zero;

            if (!flowing)
            {
                // pas d'écoulement (vide, soute, coiffe) : chaleur interne uniforme (moteurs...)
                float t = Mathf.Max(tExp, (float)part.skinUnexposedTemperature);
                for (int i = 0; i < 6; i++) faceT[i] = t;
            }
            else
            {
                EnsureBounds();
                // les raycasts d'ombre ne servent que quand ça chauffe vraiment
                bool hot = tExp > ShadowCheckTemp && env.mach > 1.5;
                Vector3 upstream = env.srfVel.normalized;
                for (int i = 0; i < 6; i++)
                {
                    float c = Vector3.Dot(flow, Axes[i]);
                    exposed[i] = (hot && c > 0.05f)
                        ? ReentryHeating.ExposedFraction(part, localCenter, localHalfExt, i, upstream) : 1f;
                    radius[i] = ReentryHeating.RadiusFactor(localHalfExt, i);
                }
                ReentryHeating.FaceTemperatures(flow, tExp, (float)vessel.atmosphericTemperature,
                                                exposed, radius, faceT);
            }

            ApplyFaceHeating(faceT, dt);

            float sev = 0f;
            if (flowing && env.mach > ReentryMinMach)
                sev = Mathf.Clamp01((tExp - TempStart) / (TempFull - TempStart));

            if (sev > 0f)
            {
                // suie = produits de pyrolyse : seulement sur un ablatif, côté au vent
                if (surface == SurfaceMaterial.Ablator) AddAblatorSoot(flow, sev, dt);
                // direction des stries : moyenne glissante, pondérée par la sévérité
                flowDir = Vector3.Lerp(flowDir, flow, Mathf.Clamp01(sev * dt * 0.1f));
            }

            if (!inReentry && sev > ReentryEnter) { inReentry = true; reentries++; }
            else if (inReentry && sev < ReentryExit) inReentry = false;

            dbgSev = sev.ToString("F2", CultureInfo.InvariantCulture);
        }

        void ApplyFaceHeating(float[] T, float dt)
        {
            for (int i = 0; i < 6; i++)
            {
                if (T[i] > peakTemp[i]) peakTemp[i] = T[i];
                AddThermalDamage(i, T[i], dt);
            }
        }

        void AddAblatorSoot(Vector3 flow, float sev, float dt)
        {
            for (int i = 0; i < 6; i++)
            {
                float w = Mathf.Max(0f, Vector3.Dot(flow, Axes[i]));
                sootAcc[i] = Mathf.Clamp01(sootAcc[i] + sev * SootBaseRate * WeatheringSettings.SootRate * dt * w);
            }
        }

        // Dégradation thermique cumulée (0..1), loi saturante 1 - e^(-dose) :
        //  - ablatif : dose ∝ charge thermique au-dessus de la pyrolyse (perte de masse ∝ ∫q dt) ;
        //  - peinture, panneaux, ruban : cinétique d'Arrhenius. Une surface tient des heures à
        //    450 K mais brûle en quelques secondes à 1000 K ; d'où un dessus quasi intact
        //    quand le ventre est carbonisé ;
        //  - inox : rien ici, sa couleur ne dépend que de la température max (couleurs de revenu).
        void AddThermalDamage(int face, float T, float dt)
        {
            float rate;
            switch (surface)
            {
                case SurfaceMaterial.Ablator:
                    float excess = T - pyrolysisTemp;
                    if (excess <= 0f) return;
                    rate = excess / WeatheringSettings.AblatorCharLoad;
                    break;
                case SurfaceMaterial.Foil:
                    if (T < MinDamageTemp) return;
                    rate = ReentryHeating.ArrheniusRate(T, WeatheringSettings.FoilActivationEnergy,
                                                        WeatheringSettings.FoilRefTemp) / WeatheringSettings.FoilRefTime;
                    break;
                case SurfaceMaterial.Steel:
                    return;
                default:
                    if (T < MinDamageTemp) return;
                    rate = ReentryHeating.ArrheniusRate(T, WeatheringSettings.PaintActivationEnergy,
                                                        WeatheringSettings.PaintRefTemp) / WeatheringSettings.PaintRefTime;
                    break;
            }
            charAcc[face] = 1f - (1f - charAcc[face]) * Mathf.Exp(-rate * dt);
        }

        // boîte englobante de la pièce dans son repère (faces, rayons de courbure)
        void EnsureBounds()
        {
            if (boundsValid) return;
            boundsValid = true;

            var b = new Bounds();
            bool any = false;
            foreach (var mf in part.FindModelComponents<MeshFilter>())
            {
                if (mf.sharedMesh == null || mf.gameObject.name == "DVW_Overlay") continue;
                Bounds mb = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents,
                        new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    Vector3 p = part.transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            localCenter = any ? b.center : Vector3.zero;
            localHalfExt = any ? b.extents : Vector3.one * 0.5f;
        }

        // poussière : se dépose sur les faces tournées vers le haut (verticale locale)
        void SurfaceDust(VesselEnv env, float dt)
        {
            if (!env.landed) return;
            Vector3 upLocal = part.transform.InverseTransformDirection(env.up).normalized;
            float rate = DustBaseRate * WeatheringSettings.DustRate * env.dust.rate;
            for (int i = 0; i < 6; i++)
            {
                float w = Mathf.Max(0f, Vector3.Dot(upLocal, Axes[i]));
                dustAcc[i] = Mathf.Clamp01(dustAcc[i] + rate * dt * (w + 0.05f));
            }
        }

        // ---------- rendu : envoi de l'état à l'OverlayController ----------
        void Apply(bool force)
        {
            ApplySolarEfficiency();
            if (overlay == null) return;

            float sig = PeakSignature() + Sum(charAcc) + Sum(sootAcc) + Sum(dustAcc) + fade;
            if (force || Mathf.Abs(sig - lastSig) >= 0.01f)
            {
                lastSig = sig;
                overlay.PeakPos = new Vector4(peakTemp[0], peakTemp[2], peakTemp[4], 0);
                overlay.PeakNeg = new Vector4(peakTemp[1], peakTemp[3], peakTemp[5], 0);
                overlay.CharPos = new Vector4(charAcc[0], charAcc[2], charAcc[4], 0);
                overlay.CharNeg = new Vector4(charAcc[1], charAcc[3], charAcc[5], 0);
                overlay.SootPos = new Vector4(sootAcc[0], sootAcc[2], sootAcc[4], 0);
                overlay.SootNeg = new Vector4(sootAcc[1], sootAcc[3], sootAcc[5], 0);
                overlay.DustPos = new Vector4(dustAcc[0], dustAcc[2], dustAcc[4], 0);
                overlay.DustNeg = new Vector4(dustAcc[1], dustAcc[3], dustAcc[5], 0);
                overlay.FlowDir = flowDir.sqrMagnitude > 1e-4f ? flowDir.normalized : Vector3.zero;
                overlay.DustColor = CurrentDust().color;
                overlay.Fade = fade;
                overlay.Surface = surface;
                overlay.SetVisible(sig > 0.005f);
            }

            // à chaque appel : les matrices suivent les pièces animées
            overlay.Refresh();

            // chaînes de debug seulement si la fenêtre clic droit est ouverte (pas d'allocations sinon)
            if (force || part.PartActionWindow != null) UpdateDebugFields();
        }

        void ApplySolarEfficiency()
        {
            if (solarPanel == null || surface != SurfaceMaterial.SolarPanel) return;
            float k = Mathf.Clamp01(WeatheringSettings.SolarPanelDegradation);
            solarPanel.efficiencyMult = solarBaseEff * (1f - k * fade);
        }

        // 10 K de plus sur une face = 0,01 : déclenche une mise à jour du rendu
        float PeakSignature()
        {
            float s = 0f;
            for (int i = 0; i < 6; i++) s += Mathf.Max(0f, peakTemp[i] - VisibleTemp) * 0.001f;
            return s;
        }

        BodyDust CurrentDust()
        {
            string body = (HighLogic.LoadedSceneIsFlight && vessel != null) ? vessel.mainBody.bodyName : "Kerbin";
            return WeatheringSettings.GetDust(body);
        }

        void UpdateDebugFields()
        {
            dbgHeat = string.Format(CultureInfo.InvariantCulture,
                "{0:F0} {1:F0} {2:F0} {3:F0} {4:F0} {5:F0}",
                peakTemp[0], peakTemp[1], peakTemp[2], peakTemp[3], peakTemp[4], peakTemp[5]);
            dbgExp = string.Format(CultureInfo.InvariantCulture,
                "{0:F0} {1:F0} {2:F0} {3:F0} {4:F0} {5:F0}",
                exposed[0] * 100f, exposed[1] * 100f, exposed[2] * 100f,
                exposed[3] * 100f, exposed[4] * 100f, exposed[5] * 100f);
            dbgChar = string.Format(CultureInfo.InvariantCulture,
                "{0:F0} {1:F0} {2:F0} {3:F0} {4:F0} {5:F0}",
                charAcc[0] * 100f, charAcc[1] * 100f, charAcc[2] * 100f,
                charAcc[3] * 100f, charAcc[4] * 100f, charAcc[5] * 100f);
            string years = (uvExposure / KSPUtil.dateTimeFormatter.Year).ToString("F1", CultureInfo.InvariantCulture);
            dbgState = string.Format(CultureInfo.InvariantCulture,
                "{0:F2} / {1:F2} / {2:P0} ({3})",
                Sum(sootAcc), Sum(dustAcc), fade, Localizer.Format("#DVW_State_Years", years));
        }

        // ---------- utilitaires ----------
        void EnsureArrays()
        {
            if (peakTemp == null || peakTemp.Length != 6) peakTemp = new float[6];
            if (charAcc == null || charAcc.Length != 6) charAcc = new float[6];
            if (sootAcc == null || sootAcc.Length != 6) sootAcc = new float[6];
            if (dustAcc == null || dustAcc.Length != 6) dustAcc = new float[6];
        }

        static float Sum(float[] a)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++) s += a[i];
            return s;
        }

        // ---------- persistance ----------
        public override void OnLoad(ConfigNode node)
        {
            EnsureArrays();
            Parse(node, "peakTemp", peakTemp);
            Parse(node, "charAcc", charAcc);
            Parse(node, "sootAcc", sootAcc);
            Parse(node, "dustAcc", dustAcc);
            var f = new float[3];
            if (Parse(node, "flowDir", f)) flowDir = new Vector3(f[0], f[1], f[2]);
        }

        public override void OnSave(ConfigNode node)
        {
            node.SetValue("peakTemp", Join(peakTemp), true);
            node.SetValue("charAcc", Join(charAcc), true);
            node.SetValue("sootAcc", Join(sootAcc), true);
            node.SetValue("dustAcc", Join(dustAcc), true);
            node.SetValue("flowDir", Join(new[] { flowDir.x, flowDir.y, flowDir.z }), true);
        }

        static string Join(float[] a)
        {
            return string.Join(",", Array.ConvertAll(a,
                x => x.ToString("R", CultureInfo.InvariantCulture)));
        }

        static bool Parse(ConfigNode n, string key, float[] dst)
        {
            if (!n.HasValue(key)) return false;
            string[] p = n.GetValue(key).Split(',');
            for (int i = 0; i < dst.Length && i < p.Length; i++)
                float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out dst[i]);
            return true;
        }

        // ---------- aides de test (éditeur ET vol) ----------
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_NextMaterial")]
        public void DebugCycleMaterial()
        {
            SurfaceMaterial m;
            if (!WeatheringSettings.TryParseMaterial(material, out m)) material = SurfaceMaterial.Paint.ToString();
            else if (m == SurfaceMaterial.Foil) material = "Auto";
            else material = (m + 1).ToString();
            ResolveMaterial();
            UpdateFade();
            Apply(true);
        }

        // Une rentrée d'orbite basse, flux arrivant par +X : 150 s, peau exposée en cloche
        // jusqu'à 1300 K, intégrée par pas de 5 s avec le même modèle physique qu'en vol
        // (sans ombre aérodynamique : la pièce est testée seule). Plusieurs clics = plusieurs missions.
        const float TestDuration = 150f, TestStep = 5f, TestPeakTemp = 1300f, TestAirTemp = 250f;

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_TestReentry")]
        public void DebugReentry()
        {
            EnsureBounds();
            Vector3 flow = Vector3.right;
            for (int i = 0; i < 6; i++)
            {
                exposed[i] = 1f;
                radius[i] = ReentryHeating.RadiusFactor(localHalfExt, i);
            }

            for (float t = 0f; t < TestDuration; t += TestStep)
            {
                float tExp = TestAirTemp + (TestPeakTemp - TestAirTemp) * Mathf.Sin(Mathf.PI * (t + TestStep * 0.5f) / TestDuration);
                ReentryHeating.FaceTemperatures(flow, tExp, TestAirTemp, exposed, radius, faceT);
                ApplyFaceHeating(faceT, TestStep);
                if (surface == SurfaceMaterial.Ablator)
                    AddAblatorSoot(flow, Mathf.Clamp01((tExp - TempStart) / (TempFull - TempStart)), TestStep);
            }
            flowDir = flow;
            reentries++;
            Apply(true);
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_Heat")]
        public void DebugHeat()
        {
            for (int i = 0; i < 6; i++) peakTemp[i] = Mathf.Max(peakTemp[i], 500f) + 50f;
            Apply(true);
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_Soot")]
        public void DebugSoot() { sootAcc[3] = Mathf.Clamp01(sootAcc[3] + 0.25f); Apply(true); }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_Dust")]
        public void DebugDust() { dustAcc[2] = Mathf.Clamp01(dustAcc[2] + 0.25f); Apply(true); }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_Year")]
        public void DebugYear()
        {
            uvExposure += KSPUtil.dateTimeFormatter.Year * WeatheringSettings.MaterialUVRate(surface);
            UpdateFade();
            Apply(true);
        }

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#DVW_Event_Clean")]
        public void DebugClean()
        {
            Array.Clear(peakTemp, 0, 6); Array.Clear(charAcc, 0, 6);
            Array.Clear(sootAcc, 0, 6); Array.Clear(dustAcc, 0, 6);
            flowDir = Vector3.zero; uvExposure = 0.0; reentries = 0;
            UpdateFade();
            Apply(true);
        }
    }
}
