﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using VRGIN.Helpers;
using VRGIN.Native;
using VRGIN.Visuals;

namespace VRGIN.Core
{
    /// <summary>
    /// <para>Singleton class that records the 2D GUI and renders it into a RenderTexture. Will only render when at least one listener is present.</para>
    /// 
    /// This works in two layers:
    ///   - the new UI is caught by redirecting all canvas to be renderer by the VRGUI camera into <see cref="uGuiTexture"/>
    ///   - the old UI is caught by setting the global render texture to <see cref="IMGuiTexture"/> while the system is rendering
    /// </summary>
    public class VRGUI : ProtectedBehaviour, IScreenGrabber
    {

#if UNITY_4_5
        class CursorBlocker : ProtectedBehaviour
        {
    
            private bool _focused = false;

            protected override void OnAwake()
            {
                WindowManager.Activate();
                WindowManager.ConfineCursor();
            }

            void OnApplicationFocus(bool hasFocus)
            {
                _focused = hasFocus;
                WindowManager.ConfineCursor();
            }
        }    
#endif

        private static VRGUI _Instance;
        private IDictionary _Registry;

        /// <summary>
        /// Gets the real width of the game window.
        /// </summary>
        public static int Width { get; private set; }

        /// <summary>
        /// Gets the real height of the game window.
        /// </summary>
        public static int Height { get; private set; }

        public SimulatedCursor SoftCursor { get; private set; }

        private List<IScreenGrabber> _ScreenGrabbers = new List<IScreenGrabber>();


        /// <summary>
        /// Gets an instance of VRGUI.
        /// </summary>
        public static VRGUI Instance
        {
            get
            {
                if (!_Instance)
                {
                    _Instance = new GameObject("VRGIN_GUI").AddComponent<VRGUI>();

#if UNITY_4_5
                    if(VR.Context.ConfineMouse) {
                        _Instance.gameObject.AddComponent<CursorBlocker>();
                    }
#endif
                    if (VR.Context.SimulateCursor)
                    {
                        _Instance.SoftCursor = SimulatedCursor.Create();
                        _Instance.SoftCursor.transform.SetParent(_Instance.transform, false);

                        VRLog.Info("Cursor is simulated");
                    }
                }
                return _Instance;
            }
        }

        /// <summary>
        /// Gets the texture used for uGUI rendering. (Canvas)
        /// </summary>
        public RenderTexture uGuiTexture { get; private set; }

        public bool IsInterested(Camera camera)
        {
            return FindCameraMapping(camera) != null;
        }

        /// <summary>
        /// Gets the texture used for immediate GUI rendering. (Legacy)
        /// </summary>
        public RenderTexture IMGuiTexture { get; private set; }

        private FieldInfo _Graphics;

        private RenderTexture _PrevRT = null;

        private Camera _VRGUICamera;
        private int _Listeners;

        internal bool Owns(Camera cam)
        {
            return _CameraMappings.ContainsKey(cam);
        }

        private IDictionary<Camera, IScreenGrabber> _CameraMappings = new Dictionary<Camera, IScreenGrabber>();
        private HashSet<Camera> _CheckedCameras = new HashSet<Camera>();

        public void Listen()
        {
            _Listeners++;
        }

        public void Unlisten()
        {
            _Listeners--;
        }

        protected override void OnAwake()
        {
            var window = WindowManager.GetClientRect();
            Width = window.Right - window.Left;
            Height = window.Bottom - window.Top;

            uGuiTexture = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Default);
            uGuiTexture.Create();

            IMGuiTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.Default);
            IMGuiTexture.Create();

            transform.localPosition = Vector3.zero;// new Vector3(0, 0, distance);
            transform.localRotation = Quaternion.identity;
            transform.gameObject.AddComponent<FastGUI>();
            transform.gameObject.AddComponent<SlowGUI>();

            // Add GUI camera
            var halfHeight = Screen.height * 0.5f;
            var halfWidth = Screen.width * 0.5f;

            _VRGUICamera = new GameObject("VRGIN_GUICamera").AddComponent<Camera>();
            _VRGUICamera.transform.SetParent(transform, false);

            if (VR.Context.PreferredGUI == GUIType.IMGUI)
            {
                _VRGUICamera.transform.position = new Vector3(halfWidth, halfHeight, -1f);
                _VRGUICamera.transform.rotation = Quaternion.identity;
                _VRGUICamera.orthographicSize = halfHeight;
            }

            _VRGUICamera.cullingMask = VR.Context.UILayerMask;
            _VRGUICamera.depth = 1;
            _VRGUICamera.nearClipPlane = VR.Context.GuiNearClipPlane;
            _VRGUICamera.farClipPlane = VR.Context.GuiFarClipPlane;
            _VRGUICamera.targetTexture = uGuiTexture;
            _VRGUICamera.backgroundColor = Color.clear;
            _VRGUICamera.clearFlags = CameraClearFlags.SolidColor;
            _VRGUICamera.orthographic = true;
            _VRGUICamera.useOcclusionCulling = false;
            
            _Graphics = typeof(GraphicRegistry).GetField("m_Graphics", BindingFlags.NonPublic | BindingFlags.Instance);
            _Registry = _Graphics.GetValue(GraphicRegistry.instance) as IDictionary;

            DontDestroyOnLoad(gameObject);
        }

        private bool IsUnprocessed(Canvas c)
        {
            return c.renderMode == RenderMode.ScreenSpaceOverlay 
                || (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera != _VRGUICamera && c.worldCamera.targetTexture == null);
        }

        protected void CatchCanvas()
        {
            _VRGUICamera.targetTexture = uGuiTexture;
            var canvasList = (_Registry.Keys as ICollection<Canvas>).Where(c => c).ToList();
            //var canvasList = GameObject.FindObjectsOfType<Canvas>();
            foreach (var canvas in canvasList.Where(IsUnprocessed))
            {
                if (VR.Interpreter.IsIgnoredCanvas(canvas)) continue;

                VRLog.Info("Add {0} [Layer: {1}, SortingLayer: {2}, SortingOrder: {3}, RenderMode: {4}, Camera: {5}, Position: {6} ]", canvas.name, LayerMask.LayerToName(canvas.gameObject.layer), canvas.sortingLayerName, canvas.sortingOrder, canvas.renderMode, canvas.worldCamera, canvas.transform.position);

                //if (canvas.name.Contains("TexFade")) continue;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _VRGUICamera;

                // Make sure that all Canvas are in the UI layer
                if (((1 << canvas.gameObject.layer) & VR.Context.UILayerMask) == 0)
                {
                    var uiLayer = LayerMask.NameToLayer(VR.Context.UILayer);
                    canvas.gameObject.layer = uiLayer;
                    foreach (var child in canvas.gameObject.GetComponentsInChildren<Transform>())
                    {
                        child.gameObject.layer = uiLayer;
                    }
                }

                if (VR.Context.EnforceDefaultGUIMaterials)
                {
                    foreach (var child in canvas.gameObject.GetComponentsInChildren<Graphic>())
                    {
                        child.material = child.defaultMaterial;
                    }
                }

                if (VR.Context.GUIAlternativeSortingMode)
                {
                    var raycaster = canvas.GetComponent<GraphicRaycaster>();
                    if (raycaster)
                    {
                        GameObject.DestroyImmediate(raycaster);
                        var newRaycaster = canvas.gameObject.AddComponent<SortingAwareGraphicRaycaster>();

                        // These fields turned into properties in Unity 4.7+
                        UnityHelper.SetPropertyOrField(newRaycaster, "ignoreReversedGraphics", UnityHelper.GetPropertyOrField(raycaster, "ignoreReversedGraphics"));
                        UnityHelper.SetPropertyOrField(newRaycaster, "blockingObjects", UnityHelper.GetPropertyOrField(raycaster, "blockingObjects"));
                        UnityHelper.SetPropertyOrField(newRaycaster, "m_BlockingMask", UnityHelper.GetPropertyOrField(raycaster, "m_BlockingMask"));
                    }
                }
            }
        }

        protected override void OnUpdate()
        {
#if !UNITY_4_5
            if (VR.Context.ConfineMouse)
            {
                Cursor.lockState = CursorLockMode.Confined;
            }
#endif
            EnsureCameraTargets();

            if (_Listeners > 0)
            {
                //Logger.Info(Time.time);
                //var watch = System.Diagnostics.Stopwatch.StartNew();
                CatchCanvas();
                //Logger.Info(watch.ElapsedTicks);
            }
            if (_Listeners < 0)
            {
                Logger.Warn("Numbers don't add up!");
            }
        }

        private void OnLevelWasLoaded(int level)
        {
            try
            {
                _CheckedCameras.Clear();
                _CameraMappings.Clear();
            }
            catch(Exception ex)
            {
                VRLog.Error(ex);
            }

            //var firstCam = _NGUICameras.OrderBy(c => c.depth).FirstOrDefault();
            //if(firstCam)
            //{
            //    firstCam.clearFlags = CameraClearFlags.SolidColor;
            //    firstCam.backgroundColor = Color.clear;
            //}
        }

        internal void OnAfterGUI()
        {
            if (Event.current.type == EventType.Repaint)
            {
                RenderTexture.active = _PrevRT;
            }
        }

        internal void OnBeforeGUI()
        {
            if (Event.current.type == EventType.Repaint)
            {
                _PrevRT = RenderTexture.active;
                RenderTexture.active = IMGuiTexture;
                GL.Clear(true, true, Color.clear);
            }
        }

        #region ScreenGrabber implementation

        public void AddGrabber(IScreenGrabber grabber)
        {
            if (!_ScreenGrabbers.Contains(grabber))
            {
                _ScreenGrabbers.Insert(0, grabber);
                RejudgeAll();
            }
        }

        public void RemoveGrabber(IScreenGrabber grabber)
        {
            _ScreenGrabbers.Remove(grabber);
            RejudgeAll();
        }

        public IEnumerable<IScreenGrabber> ScreenGrabbers
        {
            get
            {
                return _ScreenGrabbers;
            }
        }

        private void RejudgeAll()
        {
            var cameras = _CheckedCameras.ToList();
            foreach(var camera in cameras)
            {
                AddCamera(camera);
            }
        }

        public void AddCamera(Camera camera)
        {
            VRLog.Info("Trying to find a GUI mapping for camera {0}", camera.name);
            var grabber = FindCameraMapping(camera);
            if (grabber != null)
            {
                _CameraMappings[camera] = grabber;
                grabber.OnAssign(camera);

                VRLog.Info("Assigned camera {0} to {1}", camera.name, grabber);
            }
            _CheckedCameras.Add(camera);
        }

        private void EnsureCameraTargets()
        {
            var removalList = new List<Camera>();

            foreach(var entry in _CameraMappings)
            {
                if(!entry.Key)
                {
                    removalList.Add(entry.Key);
                } else if(entry.Key.targetTexture != entry.Value.GetTextures().FirstOrDefault())
                {
                    entry.Key.targetTexture = entry.Value.GetTextures().FirstOrDefault();
                }
            }

            foreach(var cam in removalList)
            {
                _CameraMappings.Remove(cam);
            }
        }

        private IScreenGrabber FindCameraMapping(Camera camera)
        {
            foreach(var grabber in _ScreenGrabbers)
            {
                if(grabber.Check(camera))
                {
                    return grabber;
                }
            }

            if (Check(camera)) return this;
            return null;
        }

        public bool Check(Camera camera)
        {
            // We're only interested in these
            return VR.Interpreter.IsUICamera(camera);
        }

        public IEnumerable<RenderTexture> GetTextures()
        {
            yield return uGuiTexture;
            yield return IMGuiTexture;
        }

        public void OnAssign(Camera camera)
        {
            _VRGUICamera.depth = Mathf.Min(_VRGUICamera.depth, camera.depth - 1);
        }

        #endregion

        /// <summary>
        /// Notifies VRGUI when the legacy GUI starts rendering.
        /// </summary>
        private class FastGUI : MonoBehaviour
        {
            private void OnGUI()
            {
                GUI.depth = int.MaxValue;

                if (Event.current.type == EventType.Repaint)
                {
                    SendMessage("OnBeforeGUI");
                }
            }
        }

        /// <summary>
        /// Notifies VRGUI when the legacy GUI stops rendering.
        /// </summary>
        private class SlowGUI : MonoBehaviour
        {
            private void OnGUI()
            {
                GUI.depth = int.MinValue;

                if (Event.current.type == EventType.Repaint)
                {
                    SendMessage("OnAfterGUI");
                }
            }
        }

        /**
         * Makes sure that canvas are sorted the right order.
         */
        private class SortingAwareGraphicRaycaster : GraphicRaycaster
        {
            private Canvas _Canvas;
            private Canvas Canvas
            {
                get
                {
                    if (_Canvas != null)
                        return _Canvas;

                    _Canvas = GetComponent<Canvas>();
                    return _Canvas;
                }
            }

            public override int priority
            {
                get
                {
                    return GetOrder();
                }
            }
            public override int sortOrderPriority
            {
                get
                {
                    return GetOrder();
                }
            }

            public override int renderOrderPriority
            {
                get
                {
                    return GetOrder();
                }
            }

            private int GetOrder()
            {
                return -Canvas.sortingOrder;
            }
        }
    }
}
