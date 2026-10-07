Shader "DVW/WeatheringOverlay"
{
    Properties
    {
        // 6 directions repère pièce : Pos = (+X,+Y,+Z)  Neg = (-X,-Y,-Z)
        _PeakPos ("Temp max + (K)", Vector) = (0,0,0,0)
        _PeakNeg ("Temp max - (K)", Vector) = (0,0,0,0)
        _CharPos ("Carbonisation +", Vector) = (0,0,0,0)
        _CharNeg ("Carbonisation -", Vector) = (0,0,0,0)
        _SootPos ("Soot +", Vector) = (0,0,0,0)
        _SootNeg ("Soot -", Vector) = (0,0,0,0)
        _DustPos ("Dust +", Vector) = (0,0,0,0)
        _DustNeg ("Dust -", Vector) = (0,0,0,0)
        _DustColor ("Dust color", Color) = (0.55,0.45,0.35,1)
        _FlowDir ("Flux de rentree (repere piece)", Vector) = (0,0,0,0)
        _Fade ("Decoloration (UV)", Range(0,1)) = 0
        _MatType ("Materiau 0 peinture 1 inox 2 ablatif 3 solaire 4 ruban", Float) = 0
        _TempJitter ("Irregularite thermique (K)", Float) = 70
        _NoiseScale ("Noise scale (par metre)", Float) = 4
        _Strength ("Intensite globale", Range(0,1)) = 1

        // copiés depuis le matériau d'origine par MaterialPropertyBlock (simple référence)
        _BaseTex ("Base texture (origine)", 2D) = "white" {}
        _BaseColor ("Base color (origine)", Color) = (1,1,1,1)
        _BaseCutoff ("Base alpha cutoff (origine)", Float) = 0
    }
    SubShader
    {
        // file <= 2500 : Unity considère l'objet comme opaque et lui donne
        // les ombres en espace écran ; +50 pour passer après les pièces (2000)
        Tags { "Queue"="AlphaTest+50" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Offset -1, -1
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            float4 _PeakPos, _PeakNeg, _CharPos, _CharNeg, _SootPos, _SootNeg, _DustPos, _DustNeg;
            float4 _FlowDir;
            fixed4 _DustColor;
            float _Fade, _MatType, _TempJitter, _NoiseScale, _Strength;

            sampler2D _BaseTex;
            float4 _BaseTex_ST;
            fixed4 _BaseColor;
            float _BaseCutoff;

            // renderer -> repère de la pièce (position, et inverse-transposée pour les normales).
            // Relatif à la pièce : constant quand le vaisseau bouge, mis à jour par le
            // plugin pour suivre les pièces animées (panneaux solaires, trains...).
            float4x4 _ObjToPart;
            float4x4 _ObjToPartN;

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float2 uv      : TEXCOORD0;
                float3 partPos : TEXCOORD1;
                float3 partNrm : TEXCOORD2;
                float3 wNrm    : TEXCOORD3;
                SHADOW_COORDS(4)
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos     = UnityObjectToClipPos(v.vertex);
                o.uv      = TRANSFORM_TEX(v.texcoord, _BaseTex);
                o.partPos = mul(_ObjToPart, v.vertex).xyz;
                o.partNrm = mul((float3x3)_ObjToPartN, v.normal);
                o.wNrm    = UnityObjectToWorldNormal(v.normal);
                TRANSFER_SHADOW(o);
                return o;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1); p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }
            float vnoise(float3 x)
            {
                float3 i = floor(x); float3 f = frac(x); f = f*f*(3.0-2.0*f);
                return lerp(
                    lerp(lerp(hash(i+float3(0,0,0)), hash(i+float3(1,0,0)), f.x),
                         lerp(hash(i+float3(0,1,0)), hash(i+float3(1,1,0)), f.x), f.y),
                    lerp(lerp(hash(i+float3(0,0,1)), hash(i+float3(1,0,1)), f.x),
                         lerp(hash(i+float3(0,1,1)), hash(i+float3(1,1,1)), f.x), f.y), f.z);
            }
            float fbm(float3 p)
            {
                float v = 0.0, a = 0.5;
                for (int k = 0; k < 3; k++) { v += a * vnoise(p); p *= 2.03; a *= 0.5; }
                return v / 0.875;
            }

            // poids "cube d'ambiance" : n*n somme a 1 -> interpolation des 6 faces
            float dirMask(float3 n, float4 pos, float4 neg)
            {
                float3 w = n * n;
                float3 sp = step(0.0, n);
                return dot(w * sp, pos.xyz) + dot(w * (1.0 - sp), neg.xyz);
            }

            float coverage(float mask, float noise)
            {
                float t = mask * 1.5 + (noise - 0.5) * 0.6;
                return smoothstep(0.35, 0.75, t);
            }

            float4 over(float4 dst, float3 col, float a)
            {
                float outA = a + dst.a * (1.0 - a);
                float3 rgb = (col * a + dst.rgb * dst.a * (1.0 - a)) / max(outA, 1e-4);
                return float4(rgb, outA);
            }

            // matériaux (doit suivre l'enum SurfaceMaterial du plugin)
            #define MAT_PAINT   0
            #define MAT_STEEL   1
            #define MAT_ABLATOR 2
            #define MAT_SOLAR   3
            #define MAT_FOIL    4

            float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

            // Couleurs de revenu de l'inox (film d'oxyde par interférence) selon la
            // température max (K). Sur une vraie tôle les teintes restent ternes et
            // métalliques : pourpre et bleu sont des reflets gris, jamais saturés.
            float3 temperRamp(float T)
            {
                float3 c = float3(1.00, 0.96, 0.84);                                  // paille pâle  ~560 K
                c = lerp(c, float3(0.96, 0.82, 0.55), smoothstep(590.0, 625.0, T));   // paille / or  ~610 K
                c = lerp(c, float3(0.78, 0.58, 0.38), smoothstep(630.0, 660.0, T));   // bronze       ~650 K
                c = lerp(c, float3(0.58, 0.46, 0.46), smoothstep(665.0, 700.0, T));   // brun pourpré ~690 K
                c = lerp(c, float3(0.46, 0.50, 0.60), smoothstep(720.0, 790.0, T));   // gris bleuté  ~800 K
                c = lerp(c, float3(0.56, 0.58, 0.62), smoothstep(820.0, 880.0, T));   // gris acier   ~870 K
                c = lerp(c, float3(0.36, 0.34, 0.33), smoothstep(900.0, 980.0, T));   // oxyde gris   ~970 K
                c = lerp(c, float3(0.16, 0.13, 0.11), smoothstep(1050.0, 1250.0, T)); // calamine     >1150 K
                return c;
            }

            // Ablatif (type Avcoat / PICA) : la couleur suit la carbonisation (charge
            // thermique cumulée). Un bouclier carbonisé reste brun très sombre, pas noir neutre.
            float4 ablatorChar(float ch, float3 baseCol)
            {
                float3 col = lerp(baseCol * float3(0.88, 0.76, 0.60), float3(0.45, 0.31, 0.18),
                                  smoothstep(0.05, 0.35, ch));                                  // roussi -> brun clair
                col = lerp(col, float3(0.22, 0.14, 0.08), smoothstep(0.30, 0.65, ch));        // brun foncé
                col = lerp(col, float3(0.09, 0.065, 0.05), smoothstep(0.60, 0.95, ch));       // carbonisé
                return float4(col, smoothstep(0.0, 0.12, ch) * 0.95);
            }

            // Ruban Kapton aluminisé sur ablatif (module de commande Apollo). D'après les
            // capsules récupérées : l'argent vire à l'or puis au bronze en roussissant ; le
            // ruban part ensuite par lambeaux à bords nets et découvre l'ablatif, couleur
            // rouille terne, qui se carbonise. Les zones peu chauffées restent argentées.
            float4 foilScorch(float ch, float3 baseCol, float tear)
            {
                float l = max(lum(baseCol), 0.3);
                float3 film = lerp(float3(1.00, 0.88, 0.60), float3(0.86, 0.64, 0.34), smoothstep(0.08, 0.25, ch));
                film = lerp(film, float3(0.58, 0.40, 0.22), smoothstep(0.22, 0.45, ch));
                float3 scorched = l * film * 1.25;      // le film roussi garde son éclat métallique

                float3 bare = lerp(float3(0.42, 0.27, 0.17), float3(0.11, 0.075, 0.055), smoothstep(0.50, 0.90, ch));
                float threshold = 0.30 + tear * 0.45;   // chaque lambeau cède à une charge différente
                float torn = smoothstep(threshold, threshold + 0.03, ch);

                float3 col = lerp(scorched, bare, torn);
                float a = max(smoothstep(0.02, 0.10, ch) * 0.85, torn * 0.97);
                return float4(col, a);
            }

            // trace thermique selon le matériau : rgb + couverture
            float4 heatTint(int mat, float T, float ch, float3 baseCol, float tear)
            {
                if (mat == MAT_ABLATOR) return ablatorChar(ch, baseCol);
                if (mat == MAT_FOIL)    return foilScorch(ch, baseCol, tear);
                if (mat == MAT_STEEL)
                {
                    // le film d'oxyde teinte le métal sans masquer ses détails,
                    // la calamine (> ~1000 K) finit par le recouvrir
                    float l = max(lum(baseCol), 0.25);
                    float3 tint = temperRamp(T);
                    float3 col = lerp(l * tint * 1.3, tint, smoothstep(950.0, 1150.0, T));
                    return float4(col, smoothstep(540.0, 600.0, T) * 0.85);
                }
                // peinture / panneaux : dégradation cumulée (Arrhenius), jaunit, brunit, puis carbonise
                float3 c = lerp(baseCol * float3(1.0, 0.88, 0.62), float3(0.36, 0.22, 0.12),
                                smoothstep(0.12, 0.45, ch));
                c = lerp(c, float3(0.07, 0.055, 0.045), smoothstep(0.45, 0.90, ch));
                return float4(c, smoothstep(0.01, 0.10, ch) * 0.9);
            }

            // vieillissement UV selon le matériau
            float3 fadedColor(int mat, float3 baseCol)
            {
                float l = lum(baseCol);
                if (mat == MAT_SOLAR)                       // panneau solaire : terne, grisâtre, moins de contraste
                    return lerp(baseCol, l * float3(0.95, 0.96, 1.0) * 0.8 + 0.12, 0.7);
                if (mat == MAT_STEEL || mat == MAT_FOIL)    // métal : léger ternissement gris
                    return lerp(baseCol, l * 0.85 + 0.05, 0.3);
                return lerp(baseCol, l * float3(1.0, 0.95, 0.82) * 0.92 + 0.05, 0.75);   // peinture : farinage jauni
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 base = tex2D(_BaseTex, i.uv);
                // seulement pour les matériaux "cutout" : ailleurs l'alpha KSP sert au spéculaire
                if (_BaseCutoff > 0.0) clip(base.a - _BaseCutoff);
                float3 baseCol = base.rgb * _BaseColor.rgb;

                float3 n = normalize(i.partNrm);
                float3 p = i.partPos;

                // stries : on écrase la fréquence du bruit le long du flux de rentrée
                float3 fd = _FlowDir.xyz;
                float3 ps = p - fd * dot(p, fd) * 0.85;

                float noise  = fbm(p * _NoiseScale);
                float noiseS = fbm(ps * _NoiseScale * 1.3 + 5.1);
                float noise2 = fbm(ps * _NoiseScale * 1.7 + 11.3);

                // température max interpolée entre les faces, rendue irrégulière par le bruit
                float T = dirMask(n, _PeakPos, _PeakNeg) + (noiseS - 0.5) * 2.0 * _TempJitter;
                // ±40 % autour de la valeur de la face : plaques irrégulières, 0 reste 0
                float ch = saturate(dirMask(n, _CharPos, _CharNeg) * (0.6 + 0.8 * noiseS));
                int mat = (int)(_MatType + 0.5);
                float tear = fbm(ps * _NoiseScale * 2.5 + 23.7);   // lambeaux du ruban (Foil)
                float4 heat = heatTint(mat, T, ch, baseCol, tear);

                float covS = coverage(dirMask(n, _SootPos, _SootNeg), noise2);
                float covD = coverage(dirMask(n, _DustPos, _DustNeg), noise) * 0.8;
                float covF = _Fade * lerp(0.7, 1.0, noise2);

                // ordre physique : vieillissement de fond, chaleur, suie, puis poussière déposée
                float4 c = float4(0,0,0,0);
                c = over(c, fadedColor(mat, baseCol), covF);
                c = over(c, heat.rgb,                heat.a);
                c = over(c, float3(0.04,0.04,0.04),  covS * 0.9);
                c = over(c, _DustColor.rgb,          covD);

                // éclairage Unity : lumière directionnelle principale (le soleil) + ombres + ambiante
                float3 wn = normalize(i.wNrm);
                float ndl = saturate(dot(wn, _WorldSpaceLightPos0.xyz));
                float atten = SHADOW_ATTENUATION(i);
                float3 light = _LightColor0.rgb * ndl * atten + ShadeSH9(float4(wn, 1.0));

                return fixed4(c.rgb * light, c.a * _Strength);
            }
            ENDCG
        }
    }
    FallBack Off
}
