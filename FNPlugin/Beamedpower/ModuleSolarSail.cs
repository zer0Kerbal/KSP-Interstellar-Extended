﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FNPlugin.Extensions;

namespace FNPlugin.Beamedpower
{
    class ModuleSolarSail : PartModule
    {
        // Persistent Variables
        [KSPField(isPersistant = true)]
        public bool IsEnabled = false;
        [KSPField(isPersistant = true)]
        public double previousPeA;
        [KSPField(isPersistant = true)]
        public double previousAeA;
        [KSPField(isPersistant = true)]
        public float previousFixedDeltaTime;

        // Persistent False
        [KSPField]
        public double reflectedPhotonRatio = 1f;
        [KSPField(guiActiveEditor = true, guiName = "Surface Area", guiUnits = " m\xB2")]
        public double surfaceArea = 144400;
        [KSPField]
        public string animName;
        [KSPField]
        public float effectSize1 = 2.5f;
        [KSPField]
        public float effectSize2 = 5;

        // GUI
        // Force and Acceleration
        [KSPField(guiActive = true, guiName = "Solar Flux", guiFormat = "F3")]
        public double averageSolarFluxInWatt;
        [KSPField(guiActive = true, guiName = "Calculated Solar Force")]
        public string solarForceAtDistance;
        [KSPField(guiActive = true, guiName = "Measured Solar Force")]
        public string solarForceBasedOnFlux;
        [KSPField(guiActive = true, guiName = "Sail Force")]
        public string forceAcquired = "";
        [KSPField(guiActive = true, guiName = "Acceleration")]
        public string solarAcc = "";
        [KSPField(isPersistant = true, guiActive = true, guiName = "Sailed Delta V", guiFormat = "F3", guiUnits = " m/s")]
        public double sailedDeltaV;

        [KSPField(guiActive = true, guiName = "Cosine Angle", guiFormat = "F6")]
        public double cosConeAngle;
        [KSPField(guiActive = true, guiName = "Orbital Force Angle", guiFormat = "F6")]
        public double orbitalHeadingForceAngle;

        [KSPField(guiActive = true, guiName = "Abs Periapsis Change", guiFormat = "F5")]
        public double periapsisChange;
        [KSPField(guiActive = true, guiName = "Abs Apapsis Change", guiFormat = "F5")]
        public double apapsisChange;
        [KSPField(guiActive = true, guiName = "Orbit size Change", guiFormat = "F5")]
        public double orbitSizeChange;

        protected Transform surfaceTransform = null;
        protected Animation solarSailAnim = null;

        // Reference distance for calculating thrust: sun to Kerbin (m)
        const double kerbin_distance = 13599840256;
        // average solar radiance at earth in W/m2
        const double solarConstant = 1360.8;
        // Solar Presure  P = W / c =  9.08e-6;
        const double thrust_coeff = 2 * solarConstant / GameConstants.speedOfLight;		

        // Display numbers for force and acceleration
        protected double solar_force_d = 0;
        protected double solar_acc_d = 0;
        protected double solarForceAtDistance_d = 0;
        protected double solarForceBasedOnFlux_d = 0;

        protected CelestialBody localStar;

        private Queue<double> periapsisChangeQueue = new Queue<double>(10);
        private Queue<double> apapsisChangeQueue = new Queue<double>(10);
        private Queue<double> solarFluxQueue = new Queue<double>(100);

        private GameObject force_effect;
        private Renderer force_effect_renderer;
        private Collider force_effect_collider;

        // GUI to deploy sail
        [KSPEvent(guiActive = true, guiName = "Deploy Sail", active = true)]
        public void DeploySail()
        {
            if (animName == null || solarSailAnim == null)
                return;

            solarSailAnim[animName].speed = 1f;
            solarSailAnim[animName].normalizedTime = 0f;
            solarSailAnim.Blend(animName, 0.1f);

            IsEnabled = true;
        }

        // GUI to retract sail
        [KSPEvent(guiActive = true, guiName = "Retract Sail", active = false)]
        public void RetractSail()
        {
            if (animName == null || solarSailAnim == null)
                return;

            solarSailAnim[animName].speed = -1f;
            solarSailAnim[animName].normalizedTime = 1f;
            solarSailAnim.Blend(animName, 0.1f);

            IsEnabled = false;
        }

        // Initialization
        public override void OnStart(PartModule.StartState state)
        {
            if (state == StartState.None || state == StartState.Editor)
                return;

            if (animName != null)
                solarSailAnim = part.FindModelAnimators(animName).FirstOrDefault();

            // start with deployed sail  when enabled
            if (IsEnabled)
            {
                solarSailAnim[animName].speed = 1f;
                solarSailAnim[animName].normalizedTime = 0f;
                solarSailAnim.Blend(animName, 0.1f);
            }

            this.part.force_activate();

            InitializeBeam();
        }

        private void InitializeBeam()
        {
            force_effect = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            force_effect_renderer = force_effect.GetComponent<Renderer>();
            force_effect_collider = force_effect.GetComponent<Collider>();
            force_effect_collider.enabled = false;

            var zero = Vector3.zero;

            force_effect.transform.localScale = new Vector3(0, 0, 0);
            force_effect.transform.position = new Vector3(zero.x, zero.y + zero.y, zero.z);
            force_effect.transform.rotation = part.transform.rotation;
            force_effect_renderer.material.shader = Shader.Find("Unlit/Transparent");
            force_effect_renderer.material.color = new Color(Color.cyan.r, Color.cyan.g, Color.cyan.b, 0.5f);

            force_effect_renderer.material.mainTexture = GameDatabase.Instance.GetTexture("WarpPlugin/ParticleFX/warp2", false);
            force_effect_renderer.receiveShadows = false;
            force_effect_renderer.material.renderQueue = 1000;
        }

        /// <summary>
        /// Is called by KSP while the part is active
        /// </summary>
        public override void OnUpdate()
        {
            localStar = PluginHelper.GetCurrentStar();            

            // Sail deployment GUI
            Events["DeploySail"].active = !IsEnabled;
            Events["RetractSail"].active = IsEnabled;

            // Text fields (acc & force)
            Fields["solarAcc"].guiActive = IsEnabled;
            Fields["forceAcquired"].guiActive = IsEnabled;

            forceAcquired = solar_force_d.ToString("E") + " N";
            solarAcc = solar_acc_d.ToString("E") + " m/s";
            solarForceAtDistance = solarForceAtDistance_d.ToString("E") + " N/m\xB2";
            solarForceBasedOnFlux = solarForceBasedOnFlux_d.ToString("E") + " N/m\xB2";
        }

        public override void OnFixedUpdate()
        {
            UpdateSolarFlux();

            solarForceAtDistance_d = 0;
            solar_force_d = 0;
            solar_acc_d = 0;
            cosConeAngle = 0;

            if (FlightGlobals.fetch == null || localStar == null || part == null || vessel == null ||  !IsEnabled)
                return;

            UpdateChangeGui();

            double universalTime = Planetarium.GetUniversalTime();
            Vector3d positionSun = localStar.getPositionAtUT(universalTime);
            Vector3d positionVessel = vessel.orbit.getPositionAtUT(universalTime);

            // Not in sunlight
            if (!inSun(universalTime, positionSun, positionVessel))
                return;

            // calculate vector between vessel and star
            Vector3d ownsunPosition = positionVessel - positionSun;

            // take part vector 
            Vector3d partNormal = this.part.transform.up;

            // If normal points away from sun, negate so our force is always away from the sun
            // so that turning the backside towards the sun thrusts correctly
            if (Vector3d.Dot(partNormal, ownsunPosition) < 0)
                partNormal = -partNormal;

            // Magnitude of force proportional to cosine-squared of angle between sun-line and normal
            cosConeAngle = Vector3d.Dot(ownsunPosition.normalized, partNormal);

            // calculate solar light force at current location
            solarForceAtDistance_d = SolarForceAtDistance(positionSun, positionVessel);
            solarForceBasedOnFlux_d = averageSolarFluxInWatt / GameConstants.speedOfLight;

            // Force from sunlight
            Vector3d solarForce = CalculateSolarForce(this, partNormal, cosConeAngle, solarForceBasedOnFlux_d);

            // calculate angle between current vessel orbital heading and solarForce
            orbitalHeadingForceAngle = Vector3d.Dot(vessel.orbit.getOrbitalVelocityAtUT(universalTime).normalized, solarForce);

            // Calculate acceleration from sunlight
            Vector3d solarAccel = solarForce / vessel.GetTotalMass() / 1000d;

            // Add To overal Sailed Delta V
            sailedDeltaV += solarAccel.magnitude;

            // Acceleration from sunlight per second
            Vector3d fixedSolatAccel = solarAccel * TimeWarp.fixedDeltaTime;

            // Apply acceleration
            if (this.vessel.packed)
                vessel.orbit.Perturb(fixedSolatAccel, universalTime);
            else
                vessel.ChangeWorldVelocity(fixedSolatAccel);

            // Update displayed force & acceleration
            solar_force_d = solarForce.magnitude;
            solar_acc_d = solarAccel.magnitude;

            UpdateBeams(solarForce, ownsunPosition);
        }

        private void UpdateBeams(Vector3d force3d, Vector3d ownsunPosition)
        {
            var shipPos = new Vector3(part.transform.position.x, part.transform.position.y, part.transform.position.z);

            var force3 = new Vector3((float)force3d.x, (float)force3d.y, (float)force3d.z);
            var forceEndBeamPos = shipPos + force3 * 1000;

            force_effect.transform.rotation = part.transform.rotation;
            force_effect.transform.localScale = new Vector3(effectSize1, forceEndBeamPos.magnitude, effectSize1);
            force_effect.transform.position = new Vector3(shipPos.x + forceEndBeamPos.x, shipPos.y + forceEndBeamPos.y, shipPos.z + forceEndBeamPos.z);
        }

        private void UpdateSolarFlux()
        {
            if (vessel.solarFlux > 0)
            {
                solarFluxQueue.Enqueue(vessel.solarFlux);
                if (solarFluxQueue.Count > 100)
                    solarFluxQueue.Dequeue();
                averageSolarFluxInWatt = solarFluxQueue.Average();
            }
            else
            {
                if (solarFluxQueue.Count > 0)
                    solarFluxQueue.Dequeue();
                averageSolarFluxInWatt = 0;
            }
        }

        private void UpdateChangeGui()
        {
            var averageFixedDeltaTime = (previousFixedDeltaTime + TimeWarp.fixedDeltaTime) / 2f;

            periapsisChangeQueue.Enqueue((vessel.orbit.PeA - previousPeA) / averageFixedDeltaTime);
            if (periapsisChangeQueue.Count > 10)
                periapsisChangeQueue.Dequeue();
            periapsisChange = periapsisChangeQueue.Average();

            apapsisChangeQueue.Enqueue((vessel.orbit.ApA - previousAeA) / averageFixedDeltaTime);
            if (apapsisChangeQueue.Count > 10)
                apapsisChangeQueue.Dequeue();
            apapsisChange = apapsisChangeQueue.Average();

            orbitSizeChange = periapsisChange + apapsisChange;

            previousPeA = vessel.orbit.PeA;
            previousAeA = vessel.orbit.ApA;
            previousFixedDeltaTime = TimeWarp.fixedDeltaTime;
        }

        // Test if an orbit at UT is in sunlight
        public static bool inSun(double UT, Vector3d positionSun, Vector3d positionVessel)
        {
            Vector3d bminusa = positionSun - positionVessel;

            foreach (CelestialBody referenceBody in FlightGlobals.Bodies)
            {
                // the sun should not block line of sight to the sun
                if (referenceBody.flightGlobalsIndex == 0)
                    continue;

                Vector3d refminusa = referenceBody.getPositionAtUT(UT) - positionVessel;                

                if (Vector3d.Dot(refminusa, bminusa) > 0 && Vector3d.Dot(refminusa, bminusa.normalized) < bminusa.magnitude)
                {
                    Vector3d tang = refminusa - Vector3d.Dot(refminusa, bminusa.normalized) * bminusa.normalized;
                    if (tang.magnitude < referenceBody.Radius)
                        return false;
                }
            }
            return true;
        }

        private static double SolarForceAtDistance(Vector3d sunPosition, Vector3d ownPosition)
        {
            double distance_from_sun = Vector3.Distance(sunPosition, ownPosition);
            return thrust_coeff * kerbin_distance * kerbin_distance / distance_from_sun / distance_from_sun;
        }

        // Calculate solar force as function of
        // sail, orbit, transform, and UT
        public static Vector3d CalculateSolarForce(ModuleSolarSail sail, Vector3d partNormal, double cosConeAngle, double solarForceAtDistance)
        {
            return partNormal * cosConeAngle * cosConeAngle * sail.surfaceArea * sail.reflectedPhotonRatio * solarForceAtDistance;
        }
    }
}