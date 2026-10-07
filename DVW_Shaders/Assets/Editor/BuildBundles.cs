using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildBundles
{
    // ADAPTE ce chemin à ton installation de KSP (même valeur que KSPDir dans DVW.csproj)
    const string KSPDir = @"E:\SteamLibrary\steamapps\common\KSP_clean";
    const string OutRoot = "Bundles";   // hors de Assets/ : Unity ne réimporte pas les bundles

    [MenuItem("DVW/Build AssetBundle (Windows)")]
    static void BuildWindows()
    {
        Build(BuildTarget.StandaloneWindows64, "windows",
              GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore);
    }

    [MenuItem("DVW/Build AssetBundles (toutes plateformes)")]
    static void BuildAll()
    {
        BuildWindows();
        Build(BuildTarget.StandaloneLinux64, "linux", GraphicsDeviceType.OpenGLCore);
        Build(BuildTarget.StandaloneOSX, "macos",
              GraphicsDeviceType.Metal, GraphicsDeviceType.OpenGLCore);
    }

    static void Build(BuildTarget target, string suffix, params GraphicsDeviceType[] apis)
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
        {
            Debug.LogWarning("[DVW] module de build absent pour " + target + " : ignoré");
            return;
        }

        // compile les variantes de shader pour toutes ces API (ex. joueurs en -force-glcore)
        PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
        PlayerSettings.SetGraphicsAPIs(target, apis);

        string dir = Path.Combine(OutRoot, suffix);
        Directory.CreateDirectory(dir);
        BuildPipeline.BuildAssetBundles(dir, BuildAssetBundleOptions.None, target);

        string src = Path.Combine(dir, "dvw");
        string dstDir = Path.Combine(KSPDir, "GameData/DynamicVesselWeathering/Shaders");
        if (!File.Exists(src)) { Debug.LogError("[DVW] bundle non produit : " + src); return; }
        if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

        string dst = Path.Combine(dstDir, "dvw-" + suffix + ".bundle");
        File.Copy(src, dst, true);
        Debug.Log("[DVW] bundle copié : " + dst);
    }
}
