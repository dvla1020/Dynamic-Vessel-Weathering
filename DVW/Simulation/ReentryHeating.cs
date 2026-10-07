using UnityEngine;

namespace DVW
{
    // Répartition physique de l'échauffement de rentrée sur les 6 faces d'une pièce.
    //
    // KSP fournit une seule température de peau "exposée" par pièce. On la redistribue :
    //  1. Distribution newtonienne / de Lees : le flux convectif local suit
    //     q/q_max ≈ cos(θ)^1.5, θ = angle entre la normale et le vent relatif.
    //     Les faces sous le vent reçoivent ~3 % (écoulement séparé : mesures navette 1-5 %).
    //  2. Ombre aérodynamique : une face dont l'amont est masqué par une autre pièce du
    //     vaisseau est dans le sillage (raycasts vers l'amont).
    //  3. Rayon de courbure (Sutton-Graves) : q ∝ 1/√R. Un bord d'attaque ou un nez
    //     fin chauffe plus qu'un ventre plat.
    //  4. Équilibre radiatif : T⁴ - T_air⁴ ∝ q, donc T ∝ q^¼.
    public static class ReentryHeating
    {
        public const float LeewardFraction = 0.03f;
        const float WakeFraction = 0.05f;
        const float RefRadius = 1.25f;          // rayon de référence (corps de 2,5 m)
        const float MinRadiusFactor = 0.5f, MaxRadiusFactor = 3f;
        const float RayLength = 60f;
        static readonly float[] SampleOffsets = { 0f, -0.6f, 0.6f };
        static readonly RaycastHit[] hits = new RaycastHit[16];

        static readonly Vector3[] Axes =
            { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        // part du flux maximal reçue par une face selon son orientation (cos θ)
        public static float GeometricFraction(float cosTheta)
        {
            float c = Mathf.Max(0f, cosTheta);
            return LeewardFraction + (1f - LeewardFraction) * c * Mathf.Sqrt(c);
        }

        // Sutton-Graves : q ∝ R^-½ ; R ≈ plus petite demi-dimension de la face
        public static float RadiusFactor(Vector3 halfExtents, int face)
        {
            int axis = face / 2;
            float a = axis == 0 ? halfExtents.y : halfExtents.x;
            float b = axis == 2 ? halfExtents.y : halfExtents.z;
            float r = Mathf.Max(0.05f, Mathf.Min(a, b));
            return Mathf.Clamp(Mathf.Sqrt(RefRadius / r), MinRadiusFactor, MaxRadiusFactor);
        }

        // fraction (0..1) de la face réellement exposée au flux : 3 rayons lancés vers l'amont
        public static float ExposedFraction(Part part, Vector3 center, Vector3 halfExtents, int face, Vector3 upstreamWorld)
        {
            int axis = face / 2;
            Vector3 n = Axes[face];
            // axe tangent le plus long : les échantillons s'étalent dessus
            Vector3 t;
            if (axis == 0) t = halfExtents.y >= halfExtents.z ? Vector3.up : Vector3.forward;
            else if (axis == 1) t = halfExtents.x >= halfExtents.z ? Vector3.right : Vector3.forward;
            else t = halfExtents.x >= halfExtents.y ? Vector3.right : Vector3.up;

            float normalExt = Vector3.Scale(halfExtents, n).magnitude;
            float tangentExt = Vector3.Scale(halfExtents, t).magnitude;

            int exposed = 0;
            for (int s = 0; s < SampleOffsets.Length; s++)
            {
                Vector3 local = center + n * normalExt + t * (tangentExt * SampleOffsets[s]);
                Vector3 origin = part.transform.TransformPoint(local) + upstreamWorld * 0.05f;
                if (!HitsOtherPart(part, origin, upstreamWorld)) exposed++;
            }
            return exposed / (float)SampleOffsets.Length;
        }

        static bool HitsOtherPart(Part part, Vector3 origin, Vector3 dir)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, RayLength,
                                            LayerUtil.DefaultEquivalent, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Part hp = FlightGlobals.GetPartUpwardsCached(hits[i].collider.gameObject);
                if (hp != null && hp != part && hp.vessel == part.vessel) return true;
            }
            return false;
        }

        // Températures des 6 faces. tExp = peau exposée KSP (référence : face la plus au vent).
        // exposed[i] = fraction hors sillage, radius[i] = facteur Sutton-Graves.
        public static void FaceTemperatures(Vector3 flowLocal, float tExp, float tAir,
                                            float[] exposed, float[] radius, float[] outT)
        {
            float fRef = LeewardFraction;
            for (int i = 0; i < 6; i++)
                fRef = Mathf.Max(fRef, GeometricFraction(Vector3.Dot(flowLocal, Axes[i])));

            float air4 = tAir * tAir * tAir * tAir;
            float delta4 = Mathf.Max(0f, tExp * tExp * tExp * tExp - air4);

            for (int i = 0; i < 6; i++)
            {
                float c = Vector3.Dot(flowLocal, Axes[i]);
                float geo = GeometricFraction(c);
                float shadowed = c > 0f ? WakeFraction : LeewardFraction;
                float f = Mathf.Lerp(shadowed, geo, exposed[i]);
                if (c > 0f) f *= Mathf.Lerp(1f, radius[i], c);   // la courbure compte surtout face au vent
                outT[i] = Mathf.Sqrt(Mathf.Sqrt(air4 + delta4 * f / fRef));
            }
        }

        // Cinétique d'Arrhenius : vitesse de dégradation relative à celle à tRef.
        // k(T)/k(Tref) = exp(-Ea/R · (1/T - 1/Tref))
        public static float ArrheniusRate(float T, float activationEnergy, float tRef)
        {
            const float R = 8.314f;
            if (T <= 1f) return 0f;
            return Mathf.Exp(-activationEnergy / R * (1f / T - 1f / tRef));
        }
    }
}
