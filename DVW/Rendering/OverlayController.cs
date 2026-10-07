using System.Collections.Generic;
using UnityEngine;

namespace DVW
{
    // Overlays d'une pièce : un MeshRenderer enfant par renderer d'origine, qui redessine
    // le même mesh avec le matériau partagé DVW. Tout ce qui varie (état de salissure,
    // matrices, texture d'origine) passe par MaterialPropertyBlock, par sous-mesh.
    public class OverlayController
    {
        class Entry
        {
            public Renderer src;
            public MeshRenderer overlay;
            public int subMeshes;
        }

        // canaux d'état (repère pièce), remplis par ModuleWeathering
        public Vector4 PeakPos, PeakNeg, SootPos, SootNeg, DustPos, DustNeg;   // Peak : kelvins
        public Vector4 CharPos, CharNeg;
        public Vector3 FlowDir;
        public Color DustColor = new Color(0.55f, 0.45f, 0.33f);
        public float Fade;
        public SurfaceMaterial Surface = SurfaceMaterial.Paint;

        public int Count { get { return entries.Count; } }

        static readonly int PeakPosId    = Shader.PropertyToID("_PeakPos");
        static readonly int PeakNegId    = Shader.PropertyToID("_PeakNeg");
        static readonly int CharPosId    = Shader.PropertyToID("_CharPos");
        static readonly int CharNegId    = Shader.PropertyToID("_CharNeg");
        static readonly int FlowDirId    = Shader.PropertyToID("_FlowDir");
        static readonly int MatTypeId    = Shader.PropertyToID("_MatType");
        static readonly int SootPosId    = Shader.PropertyToID("_SootPos");
        static readonly int SootNegId    = Shader.PropertyToID("_SootNeg");
        static readonly int DustPosId    = Shader.PropertyToID("_DustPos");
        static readonly int DustNegId    = Shader.PropertyToID("_DustNeg");
        static readonly int DustColorId  = Shader.PropertyToID("_DustColor");
        static readonly int FadeId       = Shader.PropertyToID("_Fade");
        static readonly int ObjToPartId  = Shader.PropertyToID("_ObjToPart");
        static readonly int ObjToPartNId = Shader.PropertyToID("_ObjToPartN");
        static readonly int BaseTexId    = Shader.PropertyToID("_BaseTex");
        static readonly int BaseTexSTId  = Shader.PropertyToID("_BaseTex_ST");
        static readonly int BaseColorId  = Shader.PropertyToID("_BaseColor");
        static readonly int BaseCutoffId = Shader.PropertyToID("_BaseCutoff");
        static readonly int MainTexId    = Shader.PropertyToID("_MainTex");
        static readonly int ColorId      = Shader.PropertyToID("_Color");
        static readonly int CutoffId     = Shader.PropertyToID("_Cutoff");

        // shaders d'effets (plumes Waterfall, particules...) : pas d'overlay dessus
        static readonly string[] ExcludedShaderTokens =
            { "Waterfall", "Particle", "Additive", "Unlit", "FX", "Hidden" };

        // tampons réutilisés : aucune allocation par mise à jour
        static MaterialPropertyBlock mpb;
        static readonly List<Material> tmpMats = new List<Material>();

        readonly Part part;
        readonly List<Entry> entries = new List<Entry>();
        bool visible;

        public OverlayController(Part part)
        {
            this.part = part;
            if (mpb == null) mpb = new MaterialPropertyBlock();
        }

        public void Build()
        {
            Material mat = ShaderLibrary.OverlayMaterial;
            if (mat == null) return;   // l'erreur est déjà dans le log

            foreach (var r in part.FindModelComponents<Renderer>())
            {
                var mr = r as MeshRenderer;
                if (mr == null) continue;
                if (r.name == "FLAG" || r.name == "DVW_Overlay") continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                if (IsExcluded(r)) continue;

                var go = new GameObject("DVW_Overlay");
                go.layer = r.gameObject.layer;
                go.transform.SetParent(r.transform, false);

                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var orend = go.AddComponent<MeshRenderer>();

                int subs = Mathf.Max(1, mf.sharedMesh.subMeshCount);
                var arr = new Material[subs];
                for (int i = 0; i < subs; i++) arr[i] = mat;
                orend.sharedMaterials = arr;
                orend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                orend.receiveShadows = true;
                orend.enabled = false;

                entries.Add(new Entry { src = r, overlay = orend, subMeshes = subs });
            }
        }

        static bool IsExcluded(Renderer r)
        {
            r.GetSharedMaterials(tmpMats);
            for (int m = 0; m < tmpMats.Count; m++)
            {
                if (tmpMats[m] == null || tmpMats[m].shader == null) continue;
                string sn = tmpMats[m].shader.name;
                for (int t = 0; t < ExcludedShaderTokens.Length; t++)
                    if (sn.IndexOf(ExcludedShaderTokens[t], System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            return false;
        }

        public void SetVisible(bool vis)
        {
            visible = vis;
            SyncVisibility();
        }

        // l'overlay n'est dessiné que s'il y a quelque chose à montrer
        // et si le renderer d'origine est lui-même actif
        void SyncVisibility()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.overlay == null || e.src == null) continue;
                e.overlay.enabled = visible && e.src.enabled;
            }
        }

        // Pousse l'état + matrices + texture d'origine vers chaque renderer.
        // Appelé à chaque tick : les matrices suivent ainsi les pièces animées.
        public void Refresh()
        {
            SyncVisibility();
            if (!visible) return;

            Matrix4x4 worldToPart = part.transform.worldToLocalMatrix;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.overlay == null || e.src == null) continue;

                Matrix4x4 objToPart = worldToPart * e.src.transform.localToWorldMatrix;
                Matrix4x4 objToPartN = objToPart.inverse.transpose;

                e.src.GetSharedMaterials(tmpMats);
                for (int s = 0; s < e.subMeshes; s++)
                {
                    Material baseMat = tmpMats.Count > 0 ? tmpMats[Mathf.Min(s, tmpMats.Count - 1)] : null;

                    mpb.Clear();
                    mpb.SetVector(PeakPosId, PeakPos); mpb.SetVector(PeakNegId, PeakNeg);
                    mpb.SetVector(CharPosId, CharPos); mpb.SetVector(CharNegId, CharNeg);
                    mpb.SetVector(FlowDirId, FlowDir);
                    mpb.SetFloat(MatTypeId, (float)Surface);
                    mpb.SetVector(SootPosId, SootPos); mpb.SetVector(SootNegId, SootNeg);
                    mpb.SetVector(DustPosId, DustPos); mpb.SetVector(DustNegId, DustNeg);
                    mpb.SetColor(DustColorId, DustColor);
                    mpb.SetFloat(FadeId, Fade);
                    mpb.SetMatrix(ObjToPartId, objToPart);
                    mpb.SetMatrix(ObjToPartNId, objToPartN);
                    SetBaseProperties(baseMat);

                    e.overlay.SetPropertyBlock(mpb, s);
                }
            }
        }

        // simple référence vers la texture d'origine : rien n'est copié ni modifié
        static void SetBaseProperties(Material m)
        {
            Texture tex = null;
            Vector4 st = new Vector4(1, 1, 0, 0);
            Color col = Color.white;
            float cutoff = 0f;

            if (m != null)
            {
                if (m.HasProperty(MainTexId))
                {
                    tex = m.GetTexture(MainTexId);
                    Vector2 sc = m.GetTextureScale(MainTexId), of = m.GetTextureOffset(MainTexId);
                    st = new Vector4(sc.x, sc.y, of.x, of.y);
                }
                if (m.HasProperty(ColorId)) col = m.GetColor(ColorId);
                if (m.HasProperty(CutoffId)) cutoff = m.GetFloat(CutoffId);
            }

            mpb.SetTexture(BaseTexId, tex != null ? tex : Texture2D.whiteTexture);
            mpb.SetVector(BaseTexSTId, st);
            mpb.SetColor(BaseColorId, col);
            mpb.SetFloat(BaseCutoffId, cutoff);
        }

        public void Destroy()
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].overlay != null)
                    Object.Destroy(entries[i].overlay.gameObject);
            entries.Clear();
        }
    }
}
