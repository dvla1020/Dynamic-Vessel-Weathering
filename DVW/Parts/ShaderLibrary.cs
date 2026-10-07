using System.IO;
using UnityEngine;

namespace DVW
{
    public static class ShaderLibrary
    {
        static bool tried;
        static Shader overlay;
        static Shader debugUnlit;
        static Material overlayMat;

        public static Shader Overlay    { get { EnsureLoaded(); return overlay; } }
        public static Shader DebugUnlit { get { EnsureLoaded(); return debugUnlit; } }

        // UN seul matériau pour tous les overlays : les valeurs propres à chaque
        // renderer passent par MaterialPropertyBlock (voir OverlayController)
        public static Material OverlayMaterial
        {
            get
            {
                if (overlayMat == null && Overlay != null)
                {
                    overlayMat = new Material(overlay) { name = "DVW_Overlay (partagé)" };
                    overlayMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    Debug.Log("[DVW] matériau overlay partagé créé");
                }
                return overlayMat;
            }
        }

        static string BundleFileName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.LinuxPlayer: return "dvw-linux.bundle";
                case RuntimePlatform.OSXPlayer:   return "dvw-macos.bundle";
                default:                          return "dvw-windows.bundle";
            }
        }

        static void EnsureLoaded()
        {
            if (tried) return;
            tried = true;

            string dir = Path.Combine(KSPUtil.ApplicationRootPath,
                "GameData/DynamicVesselWeathering/Shaders");
            string path = Path.Combine(dir, BundleFileName());
            if (!File.Exists(path)) path = Path.Combine(dir, "dvw.bundle");   // ancien nom
            if (!File.Exists(path)) { Debug.LogError("[DVW] bundle introuvable dans " + dir); return; }

            var bundle = AssetBundle.LoadFromFile(path);   // on ne le décharge jamais
            if (bundle == null) { Debug.LogError("[DVW] bundle illisible (mauvaise version d'Unity ?)"); return; }

            foreach (var s in bundle.LoadAllAssets<Shader>())
            {
                Debug.Log("[DVW] shader dans le bundle : " + s.name + " supporté=" + s.isSupported);
                if (s.name == "DVW/WeatheringOverlay") overlay = s;
                else if (s.name == "DVW/DebugUnlit") debugUnlit = s;
            }

            if (overlay == null) Debug.LogError("[DVW] shader overlay absent du bundle");
            else Debug.Log("[DVW] shader chargé depuis " + Path.GetFileName(path));
        }
    }
}
