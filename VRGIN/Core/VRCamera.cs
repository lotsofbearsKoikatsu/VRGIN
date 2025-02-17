﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRGIN.Helpers;

namespace VRGIN.Core
{
    public class InitializeCameraEventArgs : EventArgs
    {
        public readonly Camera Camera;
        public readonly Camera Blueprint;

        public InitializeCameraEventArgs(Camera camera, Camera blueprint)
        {
            Camera = camera;
            Blueprint = blueprint;
        }
    }

    public class CopiedCameraEventArgs : EventArgs
    {
        public readonly Camera Camera;

        public CopiedCameraEventArgs(Camera camera)
        {
            Camera = camera;
        }
    }

    public class CameraKiller : ProtectedBehaviour
    {
        MonoBehaviour[] _CameraEffects = new MonoBehaviour[0];
        Camera _Camera;

        protected override void OnStart()
        {
            base.OnStart();
            _CameraEffects = gameObject.GetCameraEffects().ToArray();
            _Camera = GetComponent<Camera>();


            _Camera.cullingMask = 0;
            _Camera.depth = -9999;
            _Camera.useOcclusionCulling = false;
            _Camera.clearFlags = CameraClearFlags.Nothing;
        }


        public void OnPreCull()
        {
            _Camera.enabled = false;
            //VRLog.Info("Disable");
        }

        public void OnGUI()
        {
            if (Event.current.type == EventType.Repaint)
            {
                //VRLog.Info("Enable");

                _Camera.enabled = true;
            }
        }
        //protected override void OnUpdate()
        //{
        //    base.OnUpdate();

        //    foreach(var fx in _CameraEffects.Where(fx => fx.enabled))
        //    {
        //        fx.enabled = false;
        //        VRLog.Info("Disabled camera effect: {0}", fx.GetType().Name);
        //    }
        //}
    }

    public class CameraSlave : ProtectedBehaviour
    {
        protected override void OnAwake()
        {
            base.OnAwake();

            var camera = Camera;
            if(!camera)
            {
                VRLog.Error("No camera found! {0}", name);
                Destroy(this);
                return;
            }

            nearClipPlane = camera.nearClipPlane;
            farClipPlane = camera.farClipPlane;
            clearFlags = camera.clearFlags;
            renderingPath = camera.renderingPath;
            clearStencilAfterLightingPass = camera.clearStencilAfterLightingPass;
            depthTextureMode = camera.depthTextureMode;
            layerCullDistances = camera.layerCullDistances;
            layerCullSpherical = camera.layerCullSpherical;
            useOcclusionCulling = camera.useOcclusionCulling;
            backgroundColor = camera.backgroundColor;
            cullingMask = camera.cullingMask;
        }
        
        public void OnEnable()
        {
            try
            {
                VR.Camera.RegisterSlave(this);
            } catch(Exception e)
            {
                VRLog.Error(e);
            }
        }

        public void OnDisable()
        {
            if (VR.Quitting)
            {
                return;
            }
            try
            {
                VR.Camera.UnregisterSlave(this);
            } catch(Exception e)
            {
                VRLog.Error(e);
            }
        }

        public Camera Camera
        {
            get
            {
                return GetComponent<Camera>();
            }
        }

        public float nearClipPlane { get; private set; }
        public float farClipPlane { get; private set; }
        public CameraClearFlags clearFlags { get; private set; }
        public RenderingPath renderingPath { get; private set; }
        public bool clearStencilAfterLightingPass { get; private set; }
        public DepthTextureMode depthTextureMode { get; private set; }
        public float[] layerCullDistances { get; private set; }
        public bool layerCullSpherical { get; private set; }
        public bool useOcclusionCulling { get; private set; }
        public Color backgroundColor { get; private set; }
        public int cullingMask { get; private set; }
        public bool canBeMainCamera { get; set; }
    }

    /// <summary>
    /// Handles the insertion of a OpenVR camera into an existing scene. The camera is controlled by a ControlMode.
    /// </summary>
    public class VRCamera : ProtectedBehaviour
    {
        private delegate void CameraOperation(Camera camera);

        private static VRCamera _Instance;
        public SteamVR_Camera SteamCam { get; private set; }
        public Camera Blueprint
        {
            get
            {
                return _Blueprint && _Blueprint.isActiveAndEnabled ? _Blueprint : Slaves.Select(s => s.Camera).FirstOrDefault(c => !VR.GUI.Owns(c));
            }
        }
        private Camera _Blueprint { get; set; }
        private IList<CameraSlave> Slaves = new List<CameraSlave>();
        private const float MIN_FAR_CLIP_PLANE = 10f;

        public bool HasValidBlueprint { get { return Slaves.Count > 0; } }

        public Transform Origin
        {
            get
            {
                return SteamCam.origin;
            }
        }

        public Transform Head
        {
            get
            {
                return SteamCam.head;
            }
        }

        private Camera _Camera;

        /// <summary>
        /// Called when the main camera has been initialized.
        /// </summary>
        public event EventHandler<InitializeCameraEventArgs> InitializeCamera = delegate { };

        /// <summary>
        /// Called when a camera is copied.
        /// </summary>
        public event EventHandler<CopiedCameraEventArgs> CopiedCamera = delegate { };

        /// <summary>
        /// Gets the current instance of VRCamera or creates one if need be.
        /// </summary>
        public static VRCamera Instance
        {
            get
            {
                if (_Instance == null)
                {
                    _Instance = new GameObject("VRGIN_Camera").AddComponent<AudioListener>().gameObject.AddComponent<VRCamera>();
                }
                return _Instance;
            }
        }

        protected override void OnAwake()
        {
            VRLog.Info("Creating VR Camera");
            _Camera = gameObject.AddComponent<Camera>();
            gameObject.AddComponent<SteamVR_Camera>();
            SteamCam = GetComponent<SteamVR_Camera>();

            // Set render scale to the value defined by the user
            SteamVR_Camera.sceneResolutionScale = VR.Settings.RenderScale;

            // Needed for the Camera Modifications mod to work. It's an artifact from DK2 days
            var legacyAnchor = new GameObject("CenterEyeAnchor");
            legacyAnchor.transform.SetParent(SteamCam.head);

            DontDestroyOnLoad(SteamCam.origin.gameObject);
        }

        /// <summary>
        /// Copies the values of a in-game camera to the VR camera.
        /// </summary>
        /// <param name="blueprint">The camera to copy.</param>
        public void Copy(Camera blueprint, bool canBeMain = false, bool hasOtherConsumers = false)
        {
            VRLog.Info("Copying camera: {0}", blueprint ? blueprint.name : "NULL");

            if (blueprint && blueprint.GetComponent<CameraSlave>())
            {
                VRLog.Warn("Is already slave -- NOOP");
                return;
            }

            if (canBeMain && UseNewCamera(blueprint))
            {
                ChangeBlueprint(blueprint);
            }

            if(blueprint)
            {
                blueprint.gameObject.AddComponent<CameraSlave>().canBeMainCamera = canBeMain;

                // Highlander principle
                var listener = blueprint.GetComponent<AudioListener>();
                if (listener)
                {
                    Destroy(listener);
                }

                // Prevent Unity from moving this camera around.
                blueprint.stereoTargetEye = StereoTargetEyeMask.None;

                if (!hasOtherConsumers && blueprint.targetTexture == null && VR.Interpreter.IsIrrelevantCamera(blueprint))
                {
                    //StartCoroutine(ExecuteDelayed(delegate { CopyFX(Blueprint); }));
                    //CopyFX(Blueprint);

                    blueprint.gameObject.AddComponent<CameraKiller>();
                    //blueprint.enabled = false;
                    //blueprint.nearClipPlane = Blueprint.farClipPlane = 0;

                    //Blueprint.targetTexture = _MiniTexture;
                    //Blueprint.gameObject.AddComponent<BlacklistThrottler>();
                }
            }

            if(canBeMain)
            {
                // Hook
                InitializeCamera(this, new InitializeCameraEventArgs(GetComponent<Camera>(), Blueprint));
            }

            CopiedCamera(this, new CopiedCameraEventArgs(blueprint));
        }

        private void ChangeBlueprint(Camera blueprint)
        {
            _Blueprint = blueprint;

            var camera = SteamCam.GetComponent<Camera>();
            camera.nearClipPlane = VR.Context.NearClipPlane;
            camera.farClipPlane = Mathf.Max(Blueprint.farClipPlane, MIN_FAR_CLIP_PLANE);
            camera.clearFlags = Blueprint.clearFlags == CameraClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.renderingPath = Blueprint.renderingPath;
            camera.clearStencilAfterLightingPass = Blueprint.clearStencilAfterLightingPass;
            camera.depthTextureMode = Blueprint.depthTextureMode;
            camera.layerCullDistances = Blueprint.layerCullDistances;
            camera.layerCullSpherical = Blueprint.layerCullSpherical;
            camera.useOcclusionCulling = Blueprint.useOcclusionCulling;
            camera.allowHDR = Blueprint.allowHDR;

            camera.backgroundColor = Blueprint.backgroundColor;

            var skybox = Blueprint.GetComponent<Skybox>();
            if (skybox != null)
            {
                var vrSkybox = camera.gameObject.GetComponent<Skybox>();
                if (vrSkybox == null) vrSkybox = vrSkybox.gameObject.AddComponent<Skybox>();

                vrSkybox.material = skybox.material;
            }
        }

        private bool UseNewCamera(Camera blueprint)
        {
            if(_Blueprint && _Blueprint != _Camera && _Blueprint != blueprint)
            {
                // We already have a main camera
                if(_Blueprint.name == "Main Camera")
                {
                    VRLog.Info("Using {0} over {1} as main camera", _Blueprint.name, blueprint.name);
                    return false;
                }
            }

            return true;
        }

        private void UpdateCameraConfig()
        {

            int cullingMask = Slaves.Aggregate(0, (cull, cam) => cull | cam.cullingMask);

            // Remove layers that are captured by other cameras (see VRGUI)
            cullingMask |= VR.Interpreter.DefaultCullingMask;
            cullingMask &= ~(LayerMask.GetMask(VR.Context.UILayer, VR.Context.InvisibleLayer));
            cullingMask &= ~(VR.Context.IgnoreMask);

            VRLog.Info("The camera sees {0} ({1})", string.Join(", ", UnityHelper.GetLayerNames(cullingMask)), string.Join(", ", Slaves.Select(s => s.name).ToArray()));

            GetComponent<Camera>().cullingMask = cullingMask;

            if (!Slaves.Any((slave) => slave.Camera == _Blueprint))
            {
                // Try to find a new main camera.
                var bestSlave = Enumerable.Reverse(Slaves).FirstOrDefault((slave) => slave.canBeMainCamera);
                if (bestSlave)
                {
                    ChangeBlueprint(bestSlave.Camera);
                }
            }
        }

        /// <summary>
        /// Doesn't really work yet.
        /// </summary>
        /// <param name="blueprint"></param>
        public void CopyFX(Camera blueprint)
        {

            CopyFX(blueprint.gameObject, gameObject, false);
            FixEffectOrder();  
        }

        public void FixEffectOrder()
        {
            if (!SteamCam)
            {
                SteamCam = GetComponent<SteamVR_Camera>();
            }
            SteamCam.ForceLast();
            SteamCam = GetComponent<SteamVR_Camera>();
        }

        private void CopyFX(GameObject source, GameObject target, bool disabledSourceFx = false)
        {
            // Clean
            foreach (var fx in target.GetCameraEffects())
            {
                DestroyImmediate(fx);
            }
            int comps = target.GetComponents<Component>().Length;

            VRLog.Info("Copying FX from {1} to {0}...", target.name, source.name);
            // Rebuild
            foreach (var fx in source.GetCameraEffects())
            {
                if (VR.Interpreter.IsAllowedEffect(fx))
                {
                    VRLog.Info("Copy FX: {0} (enabled={1})", fx.GetType().Name, fx.enabled);
                    var attachedFx = target.CopyComponentFrom(fx);
                    if (attachedFx)
                    {
                        VRLog.Info("Attached!");
                    }
                    attachedFx.enabled = fx.enabled;
                } else
                {
                    VRLog.Info("Skipping image effect {0}", fx.GetType().Name);
                }

                if (disabledSourceFx)
                {
                    fx.enabled = false;
                }
            }
            VRLog.Info("{0} components before the additions, {1} after", comps, target.GetComponents<Component>().Length);
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (SteamCam.origin)
            {
                // Make sure the scale is right
                SteamCam.origin.localScale = Vector3.one * VR.Settings.IPDScale;
            }
        }

        public void Refresh()
        {
            CopyFX(Blueprint);
        }

        internal Camera Clone(bool copyEffects = true)
        {
            var clone = new GameObject("VRGIN_Camera_Clone").CopyComponentFrom(SteamCam.GetComponent<Camera>());

            if (copyEffects)
            {
                CopyFX(SteamCam.gameObject, clone.gameObject);
            }
            clone.transform.position = transform.position;
            clone.transform.rotation = transform.rotation;
            clone.nearClipPlane = 0.01f;

            return clone;
        }

        internal void RegisterSlave(CameraSlave slave)
        {
            //VRLog.Info("Camera went online: {0}", slave.name);
            if (!Slaves.Contains(slave))
            {
                Slaves.Add(slave);
            }
            UpdateCameraConfig();
        }

        internal void UnregisterSlave(CameraSlave slave)
        {
            //VRLog.Info("Camera went offline: {0}", slave.name);
            if (Slaves.Remove(slave))
            {
                UpdateCameraConfig();
            }
        }
    }
}
