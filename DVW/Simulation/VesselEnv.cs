using UnityEngine;

namespace DVW
{
    // Environnement d'un vaisseau, calculé une fois par tick et partagé par toutes ses pièces
    public struct VesselEnv
    {
        public double ut;
        public bool physics;       // physique active : températures et vitesses fiables
        public bool inAtmo;
        public bool landed;
        public double srfSpeed;
        public double mach;
        public Vector3 srfVel;     // monde
        public Vector3 up;         // monde, verticale locale
        public float uvExposure;   // multiplicateur UV : ombre, distance au soleil, atmosphère
        public BodyDust dust;

        public static VesselEnv Capture(Vessel v, double ut)
        {
            var e = new VesselEnv();
            CelestialBody body = v.mainBody;

            e.ut = ut;
            e.physics = v.loaded && !v.packed;
            e.inAtmo = body.atmosphere && v.atmDensity > 1e-4;
            e.landed = v.Landed || v.Splashed;
            e.srfSpeed = v.srfSpeed;
            e.mach = v.mach;
            e.srfVel = (Vector3)v.srf_velocity;
            e.up = ((Vector3)(v.GetWorldPos3D() - body.position)).normalized;
            e.dust = WeatheringSettings.GetDust(body.bodyName);

            // sur rails, KSP ne recalcule plus l'ombre : on prend une fraction moyenne
            float sunlit;
            if (e.physics) sunlit = v.directSunlight ? 1f : 0f;
            else sunlit = e.landed ? 0.5f : WeatheringSettings.SunlitFractionOnRails;

            e.uvExposure = sunlit * SolarDistanceFactor(v)
                         * (e.inAtmo ? WeatheringSettings.AtmosphereUVFactor : 1f);
            return e;
        }

        // flux solaire relatif à celui reçu à l'orbite de Kerbin (loi en 1/d²)
        static float SolarDistanceFactor(Vessel v)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (sun == null || home == null || home.orbit == null) return 1f;

            double d = (v.GetWorldPos3D() - sun.position).magnitude;
            double d0 = home.orbit.semiMajorAxis;
            if (d < 1.0) return 1f;
            return Mathf.Clamp((float)(d0 * d0 / (d * d)), 0f, 10f);
        }
    }
}
