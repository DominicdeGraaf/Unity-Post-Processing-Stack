using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine.Assertions;
using static System.Net.Mime.MediaTypeNames;

namespace UnityEngine.Rendering.PostProcessing
{
#if(ENABLE_VR_MODULE && ENABLE_VR)
    using XRSettings = UnityEngine.XR.XRSettings;
#endif

    /// <summary>
    /// This is the component responsible for rendering post-processing effects. It must be put on
    /// every camera you want post-processing to be applied to.
    /// </summary>
#if UNITY_2018_3_OR_NEWER
    [ExecuteAlways]
#else
    [ExecuteInEditMode]
#endif
    [DisallowMultipleComponent, ImageEffectAllowedInSceneView]
    [AddComponentMenu("Rendering/Post-process Layer", 1000)]
    [RequireComponent(typeof(Camera))]
    public sealed class PostProcessLayer : MonoBehaviour
    {
        /// <summary>
        /// Builtin anti-aliasing methods.
        /// </summary>
        public enum Antialiasing
        {
            /// <summary>
            /// No anti-aliasing.
            /// </summary>
            None,

            /// <summary>
            /// Fast Approximate Anti-aliasing (FXAA). Fast but low quality.
            /// </summary>
            FastApproximateAntialiasing,

            /// <summary>
            /// Subpixel Morphological Anti-aliasing (SMAA). Slower but higher quality than FXAA.
            /// </summary>
            SubpixelMorphologicalAntialiasing,

            /// <summary>
            /// Temporal Anti-aliasing (TAA). As fast as SMAA but generally higher quality. Because
            /// of it's temporal nature, it can introduce ghosting artifacts on fast moving objects
            /// in highly contrasted areas.
            /// </summary>
            TemporalAntialiasing,

            /// <summary>
            /// Snapdragon Game Super Resolution
            /// </summary>
            SGSR,

            /// <summary>
            /// FidelityFX Super Resolution 1 (FSR1).
            /// </summary>
            FSR1,

            /// <summary>
            /// FidelityFX Super Resolution 3 (FSR3).
            /// </summary>
            FSR3,

            /// <summary>
            /// Deep Learning Super Sampling  (DLSS).
            /// </summary>
            DLSS,

            /// <summary>
            /// Xe Super Sampling (XeSS).
            /// </summary>
            XeSS,

            /// <summary>
            /// Snapdragon Game Super Resolution 2
            /// </summary>
            SGSR2,
        }

        /// <summary>
        /// This is transform that will be drive the volume blending feature. In some cases you may
        /// want to use a transform other than the camera, e.g. for a top down game you'll want the
        /// player character to drive the blending instead of the actual camera transform.
        /// Setting this field to <c>null</c> will disable local volumes for this layer (global ones
        /// will still work).
        /// </summary>
        public Transform volumeTrigger;

        /// <summary>
        /// A mask of layers to consider for volume blending. It allows you to do volume filtering
        /// and is especially useful to optimize volume traversal. You should always have your
        /// volumes in dedicated layers instead of the default one for best performances.
        /// </summary>
        public LayerMask volumeLayer;

        /// <summary>
        /// If <c>true</c>, it will kill any invalid / NaN pixel and replace it with a black color
        /// before post-processing is applied. It's generally a good idea to keep this enabled to
        /// avoid post-processing artifacts cause by broken data in the scene.
        /// </summary>
        public bool stopNaNPropagation = true;

        /// <summary>
        /// If <c>true</c>, it will render straight to the backbuffer and save the final blit done
        /// by the engine. This has less overhead and will improve performance on lower-end platforms
        /// (like mobiles) but breaks compatibility with legacy image effect that use OnRenderImage.
        /// </summary>
        public bool finalBlitToCameraTarget = false;

        /// <summary>
        /// The anti-aliasing method to use for this camera. By default it's set to <c>None</c>.
        /// </summary>
        public Antialiasing antialiasingMode = Antialiasing.None;

        /// <summary>
        /// Temporal Anti-aliasing settings for this camera.
        /// </summary>
        public TemporalAntialiasing temporalAntialiasing;

        /// <summary>
        /// SGSR upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public SGSR sgsr;

        /// <summary>
        /// SGSR upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public SGSR2 sgsr2;
        public SGSR2 sgsr2Stereo;

        /// <summary>
        /// FSR1 upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public FSR1 fsr1;

        /// <summary>
        /// FSR3 upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public FSR3 fsr3;
        //TND FOR VR!
        public FSR3 fsr3Stereo;

        /// <summary>
        /// DLSS upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public DLSS dlss;
        public DLSS dlssStereo;

        /// <summary>
        /// XeSS upscaling & anti-aliasing settings for this camera.
        /// </summary>
        public XeSS xess;
        //public XeSS xessStereo;

        /// <summary>
        /// Subpixel Morphological Anti-aliasing settings for this camera.
        /// </summary>
        public SubpixelMorphologicalAntialiasing subpixelMorphologicalAntialiasing;

        /// <summary>
        /// Fast Approximate Anti-aliasing settings for this camera.
        /// </summary>
        public FastApproximateAntialiasing fastApproximateAntialiasing;

        /// <summary>
        /// Fog settings for this camera.
        /// </summary>
        public Fog fog;

        Dithering dithering;

        /// <summary>
        /// The debug layer is reponsible for rendering debugging information on the screen. It will
        /// only be used if this layer is referenced in a <see cref="PostProcessDebug"/> component.
        /// </summary>
        /// <seealso cref="PostProcessDebug"/>
        public PostProcessDebugLayer debugLayer;

        [SerializeField]
        PostProcessResources m_Resources;

        // Some juggling needed to track down reference to the resource asset when loaded from asset
        // bundle (guid conflict)
        [NonSerialized]
        PostProcessResources m_OldResources;

        // UI states
        [UnityEngine.Scripting.Preserve]
        [SerializeField]
        bool m_ShowToolkit;

        [UnityEngine.Scripting.Preserve]
        [SerializeField]
        bool m_ShowCustomSorter;

        /// <summary>
        /// If <c>true</c>, it will stop applying post-processing effects just before color grading
        /// is applied. This is used internally to export to EXR without color grading.
        /// </summary>
        public bool breakBeforeColorGrading = false;

        // Pre-ordered custom user effects
        // These are automatically populated and made to work properly with the serialization
        // system AND the editor. Modify at your own risk.

        /// <summary>
        /// A wrapper around bundles to allow their serialization in lists.
        /// </summary>
        [Serializable]
        public sealed class SerializedBundleRef
        {
            /// <summary>
            /// The assembly qualified name used for serialization as we can't serialize the types
            /// themselves.
            /// </summary>
            public string assemblyQualifiedName; // We only need this at init time anyway so it's fine

            /// <summary>
            /// A reference to the bundle itself.
            /// </summary>
            public PostProcessBundle bundle; // Not serialized, is set/reset when deserialization kicks in
        }

        [SerializeField]
        List<SerializedBundleRef> m_BeforeTransparentBundles;

        [SerializeField]
        List<SerializedBundleRef> m_BeforeUpscalingBundles;

        [SerializeField]
        List<SerializedBundleRef> m_BeforeStackBundles;

        [SerializeField]
        List<SerializedBundleRef> m_AfterStackBundles;

        /// <summary>
        /// Pre-ordered effects mapped to available injection points.
        /// </summary>
        public Dictionary<PostProcessEvent, List<SerializedBundleRef>> sortedBundles
        {
            get; private set;
        }

        /// <summary>
        /// The current flags set on the camera for the built-in render pipeline.
        /// </summary>
        public DepthTextureMode cameraDepthFlags
        {
            get; private set;
        }

        // We need to keep track of bundle initialization because for some obscure reason, on
        // assembly reload a MonoBehavior's Editor OnEnable will be called BEFORE the MonoBehavior's
        // own OnEnable... So we'll use it to pre-init bundles if the layer inspector is opened and
        // the component hasn't been enabled yet.

        /// <summary>
        /// Returns <c>true</c> if the bundles have been initialized properly.
        /// </summary>
        public bool haveBundlesBeenInited
        {
            get; private set;
        }

        // Settings/Renderer bundles mapped to settings types
        Dictionary<Type, PostProcessBundle> m_Bundles;

        PropertySheetFactory m_PropertySheetFactory;
        CommandBuffer m_LegacyCmdBufferBeforeReflections;
        CommandBuffer m_LegacyCmdBufferBeforeLighting;
        CommandBuffer m_LegacyCmdBufferOpaque;
        CommandBuffer m_LegacyCmdBufferTransparent;
        CommandBuffer m_LegacyCmdBuffer;
        Camera m_Camera;
        PostProcessRenderContext m_CurrentContext;
        LogHistogram m_LogHistogram;

        RenderTexture m_opaqueOnly;
        RenderTexture m_upscaledOutput;
        RenderTexture m_originalTargetTexture;

        bool m_SettingsUpdateNeeded = true;
        bool m_IsRenderingInSceneView = false;

        TargetPool m_TargetPool;

        bool m_NaNKilled = false;

        // Recycled list - used to reduce GC stress when gathering active effects in a bundle list
        // on each frame
        readonly List<PostProcessEffectRenderer> m_ActiveEffects = new List<PostProcessEffectRenderer>();
        readonly List<RenderTargetIdentifier> m_Targets = new List<RenderTargetIdentifier>();

        void OnEnable()
        {
            Init(null);

            if (!haveBundlesBeenInited)
                InitBundles();

            m_LogHistogram = new LogHistogram();
            m_PropertySheetFactory = new PropertySheetFactory();
            m_TargetPool = new TargetPool();

            debugLayer.OnEnable();

            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;

            InitLegacy();
        }

        void InitLegacy()
        {
            m_LegacyCmdBufferBeforeReflections = new CommandBuffer { name = "Deferred Ambient Occlusion" };
            m_LegacyCmdBufferBeforeLighting = new CommandBuffer { name = "Deferred Ambient Occlusion" };
            m_LegacyCmdBufferOpaque = new CommandBuffer { name = "Opaque Only Post-processing" };
            m_LegacyCmdBufferTransparent = new CommandBuffer { name = "Before Transparent Only Post-processing" };
            m_LegacyCmdBuffer = new CommandBuffer { name = "Post-processing" };

            m_Camera = GetComponent<Camera>();

#if !UNITY_2019_1_OR_NEWER // OnRenderImage (below) implies forceIntoRenderTexture
            m_Camera.forceIntoRenderTexture = true; // Needed when running Forward / LDR / No MSAA
#endif

            m_Camera.AddCommandBuffer(CameraEvent.BeforeReflections, m_LegacyCmdBufferBeforeReflections);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeLighting, m_LegacyCmdBufferBeforeLighting);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, m_LegacyCmdBufferOpaque);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_LegacyCmdBufferTransparent);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, m_LegacyCmdBuffer);

            // Internal context used if no SRP is set
            m_CurrentContext = new PostProcessRenderContext();
        }

#if UNITY_2019_1_OR_NEWER
        bool DynamicResolutionAllowsFinalBlitToCameraTarget()
        {
            return (!RuntimeUtilities.IsDynamicResolutionEnabled(m_Camera) || (ScalableBufferManager.heightScaleFactor == 1.0 && ScalableBufferManager.widthScaleFactor == 1.0));
        }

#endif

#if UNITY_2019_1_OR_NEWER
        // We always use a CommandBuffer to blit to the final render target
        // OnRenderImage is used only to avoid the automatic blit from the RenderTexture of Camera.forceIntoRenderTexture to the actual target
#if !UNITY_EDITOR
        [ImageEffectUsesCommandBuffer]
#endif
        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (m_opaqueOnly != null)
            {
                RenderTexture.ReleaseTemporary(m_opaqueOnly);
                m_opaqueOnly = null;
            }

            if (m_CurrentContext.IsSGSRActive() || m_CurrentContext.IsFSR1Active() || m_CurrentContext.IsFSR3Active() || m_CurrentContext.IsDLSSActive() || m_CurrentContext.IsXeSSActive() || m_CurrentContext.IsSGSR2Active())
            {
                RuntimeUtilities.AllowDynamicResolution = true;
            }

            if (!finalBlitToCameraTarget && (m_CurrentContext.IsSGSRActive() || m_CurrentContext.IsFSR1Active() || m_CurrentContext.IsFSR3Active() || m_CurrentContext.IsDLSSActive() || m_CurrentContext.IsXeSSActive() || m_CurrentContext.IsSGSR2Active()))
            {
#if TND_SGSR
                if (m_CurrentContext.IsSGSRActive())
                {
                    // Set the camera back to its original parameters, so we can output at full display resolution
                    sgsr.ResetCameraViewport(m_CurrentContext);
                }
#endif
#if TND_FSR1
                if (m_CurrentContext.IsFSR1Active())
                {
                    // Set the camera back to its original parameters, so we can output at full display resolution
                    fsr1.ResetCameraViewport(m_CurrentContext);
                }
#endif
#if TND_FSR3
                if (m_CurrentContext.IsFSR3Active())
                {
                    // Set the camera back to its original parameters, so we can output at full display resolution
                    fsr3.ResetCameraViewport(m_CurrentContext);
                    if (m_CurrentContext.stereoActive)
                    {
                        fsr3Stereo.ResetCameraViewport(m_CurrentContext);
                    }
                }

#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
                if (m_CurrentContext.IsDLSSActive())
                {
                    // Set the camera back to its original parameters, so we can output at full display resolution
                    dlss.ResetCameraViewport(m_CurrentContext);
                    if (m_CurrentContext.stereoActive)
                    {
                        dlssStereo.ResetCameraViewport(m_CurrentContext);
                    }
                }
#endif
#if TND_XeSS
                if (m_CurrentContext.IsXeSSActive())
                {
                    // Set the camera back to its original parameters, so we can output at full display resolution
                    xess.ResetCameraViewport(m_CurrentContext);
                    //if (m_CurrentContext.stereoActive)
                    //{
                    //    xessStereo.ResetCameraViewport(m_CurrentContext);
                    //}
                }

#endif
#if TND_SGSR2
                if (m_CurrentContext.IsSGSR2Active())
                {
                    sgsr2.ResetCameraViewport(m_CurrentContext);
                    if (m_CurrentContext.stereoActive)
                    {
                        sgsr2Stereo.ResetCameraViewport(m_CurrentContext);
                    }
                }
#endif

                // Blit the upscaled image to the backbuffer
                if (m_originalTargetTexture != null)
                {
                    Graphics.Blit(m_upscaledOutput, m_originalTargetTexture);
                    RenderTexture.active = dst;

                    // Put the original target texture back at the end of the frame
                    m_Camera.targetTexture = m_originalTargetTexture;
                    m_originalTargetTexture = null;
                }
                else
                {
                    Graphics.Blit(m_upscaledOutput, dst);
                }

                RenderTexture.ReleaseTemporary(m_upscaledOutput);
                m_upscaledOutput = null;
                return;
            }

            if (finalBlitToCameraTarget && !m_CurrentContext.stereoActive && DynamicResolutionAllowsFinalBlitToCameraTarget())
                RenderTexture.active = dst; // silence warning
            else
                Graphics.Blit(src, dst);
        }

#endif

        /// <summary>
        /// Initializes this layer. If you create the layer via scripting you should always call
        /// this method.
        /// </summary>
        /// <param name="resources">A reference to the resource asset</param>
        public void Init(PostProcessResources resources)
        {
            if (resources != null)
                m_Resources = resources;

            RuntimeUtilities.CreateIfNull(ref temporalAntialiasing);
            RuntimeUtilities.CreateIfNull(ref sgsr);
            RuntimeUtilities.CreateIfNull(ref fsr1);
            RuntimeUtilities.CreateIfNull(ref fsr3);
            RuntimeUtilities.CreateIfNull(ref fsr3Stereo);
            RuntimeUtilities.CreateIfNull(ref dlss);
            RuntimeUtilities.CreateIfNull(ref dlssStereo);
            RuntimeUtilities.CreateIfNull(ref xess);
            //RuntimeUtilities.CreateIfNull(ref xessStereo);
            RuntimeUtilities.CreateIfNull(ref sgsr2);
            RuntimeUtilities.CreateIfNull(ref sgsr2Stereo);
            RuntimeUtilities.CreateIfNull(ref subpixelMorphologicalAntialiasing);
            RuntimeUtilities.CreateIfNull(ref fastApproximateAntialiasing);
            RuntimeUtilities.CreateIfNull(ref dithering);
            RuntimeUtilities.CreateIfNull(ref fog);
            RuntimeUtilities.CreateIfNull(ref debugLayer);
        }

        /// <summary>
        /// Initializes all the effect bundles. This is called automatically by the framework.
        /// </summary>
        public void InitBundles()
        {
            if (haveBundlesBeenInited)
                return;

            // Create these lists only once, the serialization system will take over after that
            RuntimeUtilities.CreateIfNull(ref m_BeforeTransparentBundles);
            RuntimeUtilities.CreateIfNull(ref m_BeforeUpscalingBundles);
            RuntimeUtilities.CreateIfNull(ref m_BeforeStackBundles);
            RuntimeUtilities.CreateIfNull(ref m_AfterStackBundles);

            // Create a bundle for each effect type
            m_Bundles = new Dictionary<Type, PostProcessBundle>();

            foreach (var type in PostProcessManager.instance.settingsTypes.Keys)
            {
                var settings = (PostProcessEffectSettings)ScriptableObject.CreateInstance(type);
                var bundle = new PostProcessBundle(settings);
                m_Bundles.Add(type, bundle);
            }

            // Update sorted lists with newly added or removed effects in the assemblies
            UpdateBundleSortList(m_BeforeTransparentBundles, PostProcessEvent.BeforeTransparent);
            UpdateBundleSortList(m_BeforeUpscalingBundles, PostProcessEvent.BeforeUpscaling);
            UpdateBundleSortList(m_BeforeStackBundles, PostProcessEvent.BeforeStack);
            UpdateBundleSortList(m_AfterStackBundles, PostProcessEvent.AfterStack);

            // Push all sorted lists in a dictionary for easier access
            sortedBundles = new Dictionary<PostProcessEvent, List<SerializedBundleRef>>(new PostProcessEventComparer())
            {
                { PostProcessEvent.BeforeTransparent, m_BeforeTransparentBundles },
                { PostProcessEvent.BeforeUpscaling,   m_BeforeUpscalingBundles },
                { PostProcessEvent.BeforeStack,       m_BeforeStackBundles },
                { PostProcessEvent.AfterStack,        m_AfterStackBundles }
            };

            // Done
            haveBundlesBeenInited = true;
        }

        void UpdateBundleSortList(List<SerializedBundleRef> sortedList, PostProcessEvent evt)
        {
            // First get all effects associated with the injection point
            var effects = m_Bundles.Where(kvp => kvp.Value.attribute.eventType == evt && !kvp.Value.attribute.builtinEffect)
                .Select(kvp => kvp.Value)
                .ToList();

            // Remove types that don't exist anymore
            sortedList.RemoveAll(x =>
            {
                string searchStr = x.assemblyQualifiedName;
                return !effects.Exists(b => b.settings.GetType().AssemblyQualifiedName == searchStr);
            });

            // Add new ones
            foreach (var effect in effects)
            {
                string typeName = effect.settings.GetType().AssemblyQualifiedName;

                if (!sortedList.Exists(b => b.assemblyQualifiedName == typeName))
                {
                    var sbr = new SerializedBundleRef { assemblyQualifiedName = typeName };
                    sortedList.Add(sbr);
                }
            }

            // Link internal references
            foreach (var effect in sortedList)
            {
                string typeName = effect.assemblyQualifiedName;
                var bundle = effects.Find(b => b.settings.GetType().AssemblyQualifiedName == typeName);
                effect.bundle = bundle;
            }
        }

        void OnDisable()
        {
            // Have to check for null camera in case the user is doing back'n'forth between SRP and
            // legacy
            if (m_Camera != null)
            {
                if (m_LegacyCmdBufferBeforeReflections != null)
                    m_Camera.RemoveCommandBuffer(CameraEvent.BeforeReflections, m_LegacyCmdBufferBeforeReflections);
                if (m_LegacyCmdBufferBeforeLighting != null)
                    m_Camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, m_LegacyCmdBufferBeforeLighting);
                if (m_LegacyCmdBufferOpaque != null)
                    m_Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, m_LegacyCmdBufferOpaque);
                if (m_LegacyCmdBufferTransparent != null)
                    m_Camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_LegacyCmdBufferTransparent);
                if (m_LegacyCmdBuffer != null)
                    m_Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, m_LegacyCmdBuffer);
            }

            temporalAntialiasing.Release();
#if TND_FSR1
            if (m_CurrentContext.IsFSR1Active())
            {
                fsr1.Release();
            }
#endif
#if TND_SGSR
            if (m_CurrentContext.IsSGSRActive())
            {
                sgsr.Release();
            }
#endif
#if TND_FSR3
            if (m_CurrentContext.IsFSR3Active())
            {
                fsr3.Release();

                if (fsr3Stereo != null)
                {
                    fsr3Stereo.Release();
                }
            }

#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
            if (m_CurrentContext.IsDLSSActive())
            {
                dlss.Release();
                if (dlssStereo != null)
                {
                    dlssStereo.Release();
                }
            }
#endif
#if TND_XeSS
            if (m_CurrentContext.IsXeSSActive())
            {
                xess.Release();
                //if (xessStereo != null)
                //{
                //    xessStereo.Release();
                //}
            }
#endif
#if TND_SGSR2
            if (m_CurrentContext.IsSGSR2Active())
            {
                sgsr2.Release();
                if (sgsr2Stereo != null)
                {
                    sgsr2Stereo.Release();
                }
            }
#endif

            m_LogHistogram.Release();

            foreach (var bundle in m_Bundles.Values)
                bundle.Release();

            m_Bundles.Clear();
            m_PropertySheetFactory.Release();

            if (debugLayer != null)
                debugLayer.OnDisable();

            // Might be an issue if several layers are blending in the same frame...
            TextureLerper.instance.Clear();

            haveBundlesBeenInited = false;
        }

        // Called everytime the user resets the component from the inspector and more importantly
        // the first time it's added to a GameObject. As we don't have added/removed event for
        // components, this will do fine
        void Reset()
        {
            volumeTrigger = transform;
        }

        void LateUpdate()
        {
            //TND is this still needed?
            // Temporarily take control of the camera's target texture, so that the upscaled output doesn't get clipped
            if (m_Camera.targetTexture != null && (m_CurrentContext.IsSGSRActive() || m_CurrentContext.IsFSR1Active() || m_CurrentContext.IsFSR3Active() || m_CurrentContext.IsDLSSActive() || m_CurrentContext.IsXeSSActive() || m_CurrentContext.IsSGSR2Active()))
            {

                m_originalTargetTexture = m_Camera.targetTexture;
                m_Camera.targetTexture = null;
            }
        }

        void OnPreCull()
        {
            // Unused in scriptable render pipelines
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;

            if (m_Camera == null || m_CurrentContext == null)
                InitLegacy();

            // Postprocessing does tweak load/store actions when it uses render targets.
            // But when using builtin render pipeline, Camera will silently apply viewport when setting render target,
            //   meaning that Postprocessing might think that it is rendering to fullscreen RT
            //   and use LoadAction.DontCare freely, which will ruin the RT if we are using viewport.
            // It should actually check for having tiled architecture but this is not exposed to script,
            // so we are checking for mobile as a good substitute
#if UNITY_2019_3_OR_NEWER
            if (SystemInfo.usesLoadStoreActions)
#else
            if(Application.isMobilePlatform)
#endif
            {
                Rect r = m_Camera.rect;
                if (Mathf.Abs(r.x) > 1e-6f || Mathf.Abs(r.y) > 1e-6f || Mathf.Abs(1.0f - r.width) > 1e-6f || Mathf.Abs(1.0f - r.height) > 1e-6f)
                {
                    Debug.LogWarning("When used with builtin render pipeline, Postprocessing package expects to be used on a fullscreen Camera.\nPlease note that using Camera viewport may result in visual artefacts or some things not working.", m_Camera);
                }
            }

            // Resets the projection matrix from previous frame in case TAA was enabled.
            // We also need to force reset the non-jittered projection matrix here as it's not done
            // when ResetProjectionMatrix() is called and will break transparent rendering if TAA
            // is switched off and the FOV or any other camera property changes.
            if (m_CurrentContext.IsTemporalAntialiasingActive() || m_CurrentContext.IsSGSRActive() || m_CurrentContext.IsFSR1Active() || m_CurrentContext.IsFSR3Active() || m_CurrentContext.IsDLSSActive() || m_CurrentContext.IsXeSSActive() || m_CurrentContext.IsSGSR2Active())
            {
#if UNITY_2018_2_OR_NEWER
                if (!m_Camera.usePhysicalProperties)
#endif
                {
                    m_Camera.ResetProjectionMatrix();
                    m_Camera.nonJitteredProjectionMatrix = m_Camera.projectionMatrix;
#if(ENABLE_VR_MODULE && ENABLE_VR)
                    if (m_Camera.stereoEnabled)
                    {
                        m_Camera.ResetStereoProjectionMatrices();
                        if (m_Camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                        {
                            m_Camera.CopyStereoDeviceProjectionMatrixToNonJittered(Camera.StereoscopicEye.Right);
                            m_Camera.projectionMatrix = m_Camera.GetStereoNonJitteredProjectionMatrix(Camera.StereoscopicEye.Right);
                            m_Camera.nonJitteredProjectionMatrix = m_Camera.projectionMatrix;
                            m_Camera.SetStereoProjectionMatrix(Camera.StereoscopicEye.Right, m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
                        }
                        else if (m_Camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left || m_Camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Mono)
                        {
                            m_Camera.CopyStereoDeviceProjectionMatrixToNonJittered(Camera.StereoscopicEye.Left); // device to unjittered
                            m_Camera.projectionMatrix = m_Camera.GetStereoNonJitteredProjectionMatrix(Camera.StereoscopicEye.Left);
                            m_Camera.nonJitteredProjectionMatrix = m_Camera.projectionMatrix;
                            m_Camera.SetStereoProjectionMatrix(Camera.StereoscopicEye.Left, m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left));
                        }
                    }
#endif
                }
            }
            else
            {
                m_Camera.nonJitteredProjectionMatrix = m_Camera.projectionMatrix;
            }

#if(ENABLE_VR_MODULE && ENABLE_VR)
            if (m_Camera.stereoEnabled)
            {
                Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, XRSettings.renderViewportScale);
            }
            else
#endif
            {
                Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, 1.0f);
            }

            BuildCommandBuffers();
        }

        void OnPreRender()
        {
            // Unused in scriptable render pipelines
            // Only needed for multi-pass stereo right eye
            if (RuntimeUtilities.scriptableRenderPipelineActive ||
                (m_Camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right))
                return;

            BuildCommandBuffers();
        }

        static bool RequiresInitialBlit(Camera camera, PostProcessRenderContext context)
        {
            // [ImageEffectUsesCommandBuffer] is currently broken, FIXME
            return true;

            /*
#if UNITY_2019_1_OR_NEWER
            if (camera.allowMSAA) // this shouldn't be necessary, but until re-tested on older Unity versions just do the blits
                return true;
            if (RuntimeUtilities.scriptableRenderPipelineActive) // Should never be called from SRP
                return true;

            return false;
#else
            return true;
#endif
            */
        }

        void UpdateSrcDstForOpaqueOnly(ref int src, ref int dst, PostProcessRenderContext context, RenderTargetIdentifier cameraTarget, int opaqueOnlyEffectsRemaining)
        {
            if (src > -1)
                context.command.ReleaseTemporaryRT(src);

            context.source = context.destination;
            src = dst;

            if (opaqueOnlyEffectsRemaining == 1)
            {
                context.destination = cameraTarget;
            }
            else
            {
                dst = m_TargetPool.Get();
                context.destination = dst;
                context.GetScreenSpaceTemporaryRT(context.command, dst, 0, context.sourceFormat);
            }
        }

        private bool _runRightEyeOnceCommandBuffers;
        private bool _upscalerEnabled = false;
        void BuildCommandBuffers()
        {
            var context = m_CurrentContext;
            var sourceFormat = m_Camera.targetTexture ? m_Camera.targetTexture.format : (m_Camera.allowHDR ? RuntimeUtilities.defaultHDRRenderTextureFormat : RenderTextureFormat.Default);

            if (!RuntimeUtilities.isFloatingPointFormat(sourceFormat))
                m_NaNKilled = true;

            context.Reset();
            context.camera = m_Camera;
            context.sourceFormat = sourceFormat;

            // TODO: Investigate retaining command buffers on XR multi-pass right eye
            m_LegacyCmdBufferBeforeReflections.Clear();
            m_LegacyCmdBufferBeforeLighting.Clear();
            m_LegacyCmdBufferOpaque.Clear();
            m_LegacyCmdBufferTransparent.Clear();
            m_LegacyCmdBuffer.Clear();

            SetupContext(context);

            // Modify internal rendering resolution for both the camera and the pre-upscaling post-processing effects
            if (context.IsSGSRActive())
            {
#if TND_SGSR
                _upscalerEnabled = true;
                if (!sgsr.IsSupported())
                {
                    antialiasingMode = sgsr.fallBackAA;
                }

                if (!context.stereoActive || (context.stereoActive && context.camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right))
                {
                    sgsr.ConfigureCameraViewport(context);
                }
                context.SetRenderSize(sgsr.renderSize);
#else
                antialiasingMode = sgsr.fallBackAA;
#endif
            }
            else if (context.IsFSR1Active())
            {
#if TND_FSR1
                _upscalerEnabled = true;
                if (!fsr1.IsSupported())
                {
                    antialiasingMode = fsr1.fallBackAA;
                }
                if (!context.stereoActive || (context.stereoActive && context.camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right))
                {
                    fsr1.ConfigureCameraViewport(context);
                }
                context.SetRenderSize(fsr1.renderSize);
#else
                antialiasingMode = fsr1.fallBackAA;
#endif
            }
            else if (context.IsFSR3Active())
            {
#if TND_FSR3
                _upscalerEnabled = true;
                if (!fsr3.IsSupported())
                {
                    antialiasingMode = fsr3.fallBackAA;
                }
                if (!fsr3Stereo.IsSupported())
                {
                    antialiasingMode = fsr3.fallBackAA;
                }

                fsr3.ConfigureCameraViewport(context);
                if (context.stereoActive)
                {
                    fsr3Stereo.ConfigureCameraViewportRightEye(context);
                }
                context.SetRenderSize(fsr3.renderSize);

#else
                antialiasingMode = fsr3.fallBackAA;
#endif
            }
            else if (context.IsDLSSActive())
            {
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
                _upscalerEnabled = true;
                if (!dlss.IsSupported())
                {
                    antialiasingMode = dlss.fallBackAA;
                }
                if (!dlssStereo.IsSupported())
                {
                    antialiasingMode = dlss.fallBackAA;
                }

                dlss.ConfigureCameraViewport(context);
                if (context.stereoActive)
                {
                    dlssStereo.ConfigureCameraViewportRightEye(context);
                }

                context.SetRenderSize(dlss.renderSize);
#else
                antialiasingMode = dlss.fallBackAA;
#endif
            }
            else if (context.IsXeSSActive())
            {
#if TND_XeSS
                _upscalerEnabled = true;
                if (!xess.IsSupported())
                {
                    xess.Release();
                    antialiasingMode = xess.fallBackAA;
                }
                //if (!xessStereo.IsSupported())
                //{
                //    antialiasingMode = xess.fallBackAA;
                //}

                xess.ConfigureCameraViewport(context);
                if (context.stereoActive)
                {
                    //xessStereo.ConfigureCameraViewportRightEye(context);
                }

                context.SetRenderSize(xess.renderSize);
#else
                antialiasingMode = xess.fallBackAA;
#endif
            }
            else if (context.IsSGSR2Active())
            {
#if TND_SGSR2
                _upscalerEnabled = true;
                if (!sgsr2.IsSupported())
                {
                    antialiasingMode = sgsr2.fallBackAA;
                }

                sgsr2.ConfigureCameraViewport(context);
                if (context.stereoActive)
                {
                    sgsr2Stereo.ConfigureCameraViewportRightEye(context);
                }

                context.SetRenderSize(sgsr2.renderSize);
#else
                antialiasingMode = sgsr2.fallBackAA;
#endif
            }
            else
            {
                // Ensure all upscaler resources are released when it's not in use, and only call it when upscaler has been enabled once!
                if (context.camera.cameraType == CameraType.Game && _upscalerEnabled)
                {
                    _upscalerEnabled = false;
#if TND_FSR1
                    fsr1.Release();
#endif
#if TND_SGSR
                    sgsr.Release();
#endif
#if TND_FSR3
                    fsr3.Release();
                    if (fsr3Stereo != null)
                    {
                        fsr3Stereo.Release();
                    }
#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
                    dlss.Release();
                    if (dlssStereo != null)
                    {
                        dlssStereo.Release();
                    }
#endif
#if TND_XeSS
                    xess.Release();
                    //if (xessStereo != null)
                    //{
                    //    xessStereo.Release();
                    //}
#endif
#if TND_SGSR2
                    sgsr2.Release();
                    if (sgsr2Stereo != null)
                    {
                        sgsr2Stereo.Release();
                    }
#endif
                }
                if (m_originalTargetTexture != null)
                {
                    m_Camera.targetTexture = m_originalTargetTexture;
                    m_originalTargetTexture = null;
                }
            }

            context.command = m_LegacyCmdBufferOpaque;
            TextureLerper.instance.BeginFrame(context);
            UpdateVolumeSystem(context.camera, context.command);

            // Lighting & opaque-only effects
            var aoBundle = GetBundle<AmbientOcclusion>();
            var aoSettings = aoBundle.CastSettings<AmbientOcclusion>();
            var aoRenderer = aoBundle.CastRenderer<AmbientOcclusionRenderer>();

            bool aoSupported = aoSettings.IsEnabledAndSupported(context);
            bool aoAmbientOnly = aoRenderer.IsAmbientOnly(context);
            bool isAmbientOcclusionDeferred = aoSupported && aoAmbientOnly;
            bool isAmbientOcclusionOpaque = aoSupported && !aoAmbientOnly;

            var ssrBundle = GetBundle<ScreenSpaceReflections>();
            var ssrSettings = ssrBundle.settings;
            var ssrRenderer = ssrBundle.renderer;
            bool isScreenSpaceReflectionsActive = ssrSettings.IsEnabledAndSupported(context);

#if UNITY_2019_1_OR_NEWER
            if (context.stereoActive)
                context.UpdateSinglePassStereoState(context.IsTemporalAntialiasingActive() || context.IsSGSRActive() || context.IsFSR1Active() || context.IsFSR3Active() || context.IsDLSSActive() || context.IsXeSSActive() || context.IsSGSR2Active(), aoSupported, isScreenSpaceReflectionsActive);
#endif
            // Ambient-only AO is a special case and has to be done in separate command buffers
            if (isAmbientOcclusionDeferred)
            {
                var ao = aoRenderer.Get();

                // Render as soon as possible - should be done async in SRPs when available
                context.command = m_LegacyCmdBufferBeforeReflections;
                ao.RenderAmbientOnly(context);

                // Composite with GBuffer right before the lighting pass
                context.command = m_LegacyCmdBufferBeforeLighting;
                ao.CompositeAmbientOnly(context);
            }
            else if (isAmbientOcclusionOpaque)
            {
                context.command = m_LegacyCmdBufferOpaque;
                aoRenderer.Get().RenderAfterOpaque(context);
            }

            bool isFogActive = fog.IsEnabledAndSupported(context);
            bool hasCustomOpaqueOnlyEffects = HasOpaqueOnlyEffects(context);
            int opaqueOnlyEffects = 0;
            opaqueOnlyEffects += isScreenSpaceReflectionsActive ? 1 : 0;
            opaqueOnlyEffects += isFogActive ? 1 : 0;
            opaqueOnlyEffects += hasCustomOpaqueOnlyEffects ? 1 : 0;

            // This works on right eye because it is resolved/populated at runtime
            var cameraTarget = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);


            if (opaqueOnlyEffects > 0)
            {
                var cmd = m_LegacyCmdBufferOpaque;
                context.command = cmd;
                context.source = cameraTarget;
                context.destination = cameraTarget;
                int srcTarget = -1;
                int dstTarget = -1;

                UpdateSrcDstForOpaqueOnly(ref srcTarget, ref dstTarget, context, cameraTarget, opaqueOnlyEffects + 1); // + 1 for blit

                if (RequiresInitialBlit(m_Camera, context) || opaqueOnlyEffects == 1)
                {
                    cmd.BuiltinBlit(context.source, context.destination, RuntimeUtilities.copyStdMaterial, stopNaNPropagation ? 1 : 0);
                    UpdateSrcDstForOpaqueOnly(ref srcTarget, ref dstTarget, context, cameraTarget, opaqueOnlyEffects);
                }

                if (isScreenSpaceReflectionsActive)
                {
                    ssrRenderer.RenderOrLog(context);
                    opaqueOnlyEffects--;
                    UpdateSrcDstForOpaqueOnly(ref srcTarget, ref dstTarget, context, cameraTarget, opaqueOnlyEffects);
                }

                if (isFogActive)
                {
                    fog.Render(context);
                    opaqueOnlyEffects--;
                    UpdateSrcDstForOpaqueOnly(ref srcTarget, ref dstTarget, context, cameraTarget, opaqueOnlyEffects);
                }

                if (hasCustomOpaqueOnlyEffects)
                    RenderOpaqueOnly(context);

                cmd.ReleaseTemporaryRT(srcTarget);
            }

#if TND_FSR3
            //TND Only run the right eye once, otherwise we'll get a free memory leak
            if (context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
            {
                _runRightEyeOnceCommandBuffers = !_runRightEyeOnceCommandBuffers;
                if (_runRightEyeOnceCommandBuffers)
                {
                    goto skip;
                }
            }
            // Create a copy of the opaque-only color buffer for auto-reactive mask generation
            if (context.IsFSR3Active() && (fsr3.autoGenerateReactiveMask || fsr3.autoGenerateTransparencyAndComposition))
            {
                Vector2Int scaledRenderSize = fsr3.GetScaledRenderSize(context.camera);
                m_opaqueOnly = context.GetScreenSpaceTemporaryRT(colorFormat: sourceFormat, widthOverride: scaledRenderSize.x, heightOverride: scaledRenderSize.y);
                m_LegacyCmdBufferTransparent.BuiltinBlit(cameraTarget, m_opaqueOnly);
            }
        skip:

#endif
#if TND_SGSR2
            //TND Only run the right eye once, otherwise we'll get a free memory leak
            bool allowOpaqueOnly = true;
            if (context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
            {
                _runRightEyeOnceCommandBuffers = !_runRightEyeOnceCommandBuffers;
                if (_runRightEyeOnceCommandBuffers)
                {
                    allowOpaqueOnly = false;
                }
            }
            // Create a copy of the opaque-only color buffer for three-pass compute variant
            if (allowOpaqueOnly && context.IsSGSR2Active() && sgsr2.variant == TND.SGSR2.SGSR2_Variant.ThreePassCompute)
            {
                Vector2Int scaledRenderSize = sgsr2.GetScaledRenderSize(context.camera);
                m_opaqueOnly = context.GetScreenSpaceTemporaryRT(colorFormat: sourceFormat, widthOverride: scaledRenderSize.x, heightOverride: scaledRenderSize.y);
                m_LegacyCmdBufferTransparent.BuiltinBlit(cameraTarget, m_opaqueOnly);
            }
#endif

            // Post-transparency stack
            int tempRt = -1;
            bool forceNanKillPass = (!m_NaNKilled && stopNaNPropagation && RuntimeUtilities.isFloatingPointFormat(sourceFormat));
            bool vrSinglePassInstancingEnabled = context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced;
            if (!vrSinglePassInstancingEnabled && (RequiresInitialBlit(m_Camera, context) || forceNanKillPass))
            {
                int width = context.width;
#if UNITY_2019_1_OR_NEWER && ENABLE_VR_MODULE && ENABLE_VR
                var xrDesc = XRSettings.eyeTextureDesc;
                if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
                    width = xrDesc.width;
#endif
                tempRt = m_TargetPool.Get();
                context.GetScreenSpaceTemporaryRT(m_LegacyCmdBuffer, tempRt, 0, sourceFormat, RenderTextureReadWrite.sRGB, FilterMode.Bilinear, width);
                m_LegacyCmdBuffer.BuiltinBlit(cameraTarget, tempRt, RuntimeUtilities.copyStdMaterial, stopNaNPropagation ? 1 : 0);
                if (!m_NaNKilled)
                    m_NaNKilled = stopNaNPropagation;

                context.source = tempRt;
            }
            else
            {
                context.source = cameraTarget;
            }

            context.destination = cameraTarget;
#if TND_SGSR
            if (!finalBlitToCameraTarget && m_CurrentContext.IsSGSRActive())
            {
                var displaySize = sgsr.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);

                }
                context.destination = m_upscaledOutput;
            }
#endif
#if TND_FSR1
            if (!finalBlitToCameraTarget && m_CurrentContext.IsFSR1Active())
            {
                var displaySize = fsr1.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);
                }
                context.destination = m_upscaledOutput;
            }
#endif
#if TND_FSR3
            if (!finalBlitToCameraTarget && m_CurrentContext.IsFSR3Active())
            {
                var displaySize = fsr3.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);
                }
                context.destination = m_upscaledOutput;
            }
#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
            if (!finalBlitToCameraTarget && m_CurrentContext.IsDLSSActive())
            {
                var displaySize = dlss.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);
                }
                context.destination = m_upscaledOutput;
            }
#endif
#if TND_XeSS
            if (!finalBlitToCameraTarget && m_CurrentContext.IsXeSSActive())
            {
                var displaySize = xess.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);
                }
                context.destination = m_upscaledOutput;
            }
#endif
#if TND_SGSR2
            if (!finalBlitToCameraTarget && m_CurrentContext.IsSGSR2Active())
            {
                var displaySize = sgsr2.displaySize;
                if (m_upscaledOutput == null)
                {
                    m_upscaledOutput = context.GetScreenSpaceTemporaryRT(widthOverride: displaySize.x, heightOverride: displaySize.y);
                }
                context.destination = m_upscaledOutput;
            }
#endif



#if UNITY_2019_1_OR_NEWER
            if (finalBlitToCameraTarget && !m_CurrentContext.stereoActive && !RuntimeUtilities.scriptableRenderPipelineActive && DynamicResolutionAllowsFinalBlitToCameraTarget())
            {
                if (m_Camera.targetTexture)
                {
                    context.destination = m_Camera.targetTexture.colorBuffer;
                }
                else
                {
                    context.flip = true;
                    context.destination = Display.main.colorBuffer;
                }
            }
#endif

            context.command = m_LegacyCmdBuffer;

            Render(context);

            if (tempRt > -1)
                m_LegacyCmdBuffer.ReleaseTemporaryRT(tempRt);
        }

        void OnPostRender()
        {
            // Unused in scriptable render pipelines
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;
#if TND_SGSR
            // Set the camera back to its original parameters, so we can output at full display resolution
            if (finalBlitToCameraTarget && m_CurrentContext.IsSGSRActive())
            {
                sgsr.ResetCameraViewport(m_CurrentContext);
            }
#endif
#if TND_FSR1
            // Set the camera back to its original parameters, so we can output at full display resolution
            if (finalBlitToCameraTarget && m_CurrentContext.IsFSR1Active())
            {
                fsr1.ResetCameraViewport(m_CurrentContext);
            }
#endif
#if TND_FSR3
            // Set the camera back to its original parameters, so we can output at full display resolution
            if (finalBlitToCameraTarget && m_CurrentContext.IsFSR3Active())
            {
                fsr3.ResetCameraViewport(m_CurrentContext);
                if (m_CurrentContext.stereoActive)
                {
                    fsr3Stereo.ResetCameraViewport(m_CurrentContext);
                }
            }
#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
            // Set the camera back to its original parameters, so we can output at full display resolution
            if (finalBlitToCameraTarget && m_CurrentContext.IsDLSSActive())
            {
                dlss.ResetCameraViewport(m_CurrentContext);
                if (m_CurrentContext.stereoActive)
                {
                    dlssStereo.ResetCameraViewport(m_CurrentContext);
                }
            }
#endif
#if TND_XeSS
            // Set the camera back to its original parameters, so we can output at full display resolution
            if (finalBlitToCameraTarget && m_CurrentContext.IsXeSSActive())
            {
                xess.ResetCameraViewport(m_CurrentContext);
                //if (m_CurrentContext.stereoActive)
                //{
                //    xessStereo.ResetCameraViewport(m_CurrentContext);
                //}
            }
#endif
#if TND_SGSR2
            if (finalBlitToCameraTarget && m_CurrentContext.IsSGSR2Active())
            {
                sgsr2.ResetCameraViewport(m_CurrentContext);
                if (m_CurrentContext.stereoActive)
                {
                    sgsr2Stereo.ResetCameraViewport(m_CurrentContext);
                }
            }
#endif

            if (m_CurrentContext.IsTemporalAntialiasingActive() || m_CurrentContext.IsSGSRActive() || m_CurrentContext.IsFSR1Active() || m_CurrentContext.IsFSR3Active() || m_CurrentContext.IsDLSSActive() || m_CurrentContext.IsXeSSActive() || m_CurrentContext.IsSGSR2Active())
            {
#if UNITY_2018_2_OR_NEWER
                // TAA calls SetProjectionMatrix so if the camera projection mode was physical, it gets set to explicit. So we set it back to physical.
                if (m_CurrentContext.physicalCamera)
                    m_Camera.usePhysicalProperties = true;
                else
#endif
                {
                    // The camera must be reset on precull and post render to avoid issues with alpha when toggling TAA.
                    m_Camera.ResetProjectionMatrix();
#if (ENABLE_VR_MODULE && ENABLE_VR)
                    if (m_CurrentContext.stereoActive)
                    {
                        if (RuntimeUtilities.isSinglePassStereoEnabled || m_Camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                        {
                            m_Camera.ResetStereoProjectionMatrices();
                            // copy the left eye onto the projection matrix so that we're using the correct projection matrix after calling m_Camera.ResetProjectionMatrix(); above.
                            if (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass)
                                m_Camera.projectionMatrix = m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                        }
                    }
#endif
                }
            }
        }

        /// <summary>
        /// Grabs the bundle for the given effect type.
        /// </summary>
        /// <typeparam name="T">An effect type.</typeparam>
        /// <returns>The bundle for the effect of type <typeparam name="T"></typeparam></returns>
        public PostProcessBundle GetBundle<T>()
            where T : PostProcessEffectSettings
        {
            return GetBundle(typeof(T));
        }

        /// <summary>
        /// Grabs the bundle for the given effect type.
        /// </summary>
        /// <param name="settingsType">An effect type.</param>
        /// <returns>The bundle for the effect of type <typeparam name="type"></typeparam></returns>
        public PostProcessBundle GetBundle(Type settingsType)
        {
            Assert.IsTrue(m_Bundles.ContainsKey(settingsType), "Invalid type");
            return m_Bundles[settingsType];
        }

        /// <summary>
        /// Gets the current settings for a given effect.
        /// </summary>
        /// <typeparam name="T">The type of effect to look for</typeparam>
        /// <returns>The current state of an effect</returns>
        public T GetSettings<T>()
            where T : PostProcessEffectSettings
        {
            return GetBundle<T>().CastSettings<T>();
        }

        /// <summary>
        /// Utility method to bake a multi-scale volumetric obscurance map for the current camera.
        /// This will only work if ambient occlusion is active in the scene.
        /// </summary>
        /// <param name="cmd">The command buffer to use for rendering steps</param>
        /// <param name="camera">The camera to render ambient occlusion for</param>
        /// <param name="destination">The destination render target</param>
        /// <param name="depthMap">The depth map to use. If <c>null</c>, it will use the depth map
        /// from the given camera</param>
        /// <param name="invert">Should the result be inverted?</param>
        /// <param name="isMSAA">Should use MSAA?</param>
        public void BakeMSVOMap(CommandBuffer cmd, Camera camera, RenderTargetIdentifier destination, RenderTargetIdentifier? depthMap, bool invert, bool isMSAA = false)
        {
            var bundle = GetBundle<AmbientOcclusion>();
            var renderer = bundle.CastRenderer<AmbientOcclusionRenderer>().GetMultiScaleVO();
            renderer.SetResources(m_Resources);
            renderer.GenerateAOMap(cmd, camera, destination, depthMap, invert, isMSAA);
        }

        internal void OverrideSettings(List<PostProcessEffectSettings> baseSettings, float interpFactor)
        {
            // Go through all settings & overriden parameters for the given volume and lerp values
            foreach (var settings in baseSettings)
            {
                if (!settings.active)
                    continue;

                var target = GetBundle(settings.GetType()).settings;
                int count = settings.parameters.Count;

                for (int i = 0; i < count; i++)
                {
                    var toParam = settings.parameters[i];
                    if (toParam.overrideState)
                    {
                        var fromParam = target.parameters[i];
                        fromParam.Interp(fromParam, toParam, interpFactor);
                    }
                }
            }
        }

        // In the legacy render loop you have to explicitely set flags on camera to tell that you
        // need depth, depth+normals or motion vectors... This won't have any effect with most
        // scriptable render pipelines.
        void SetLegacyCameraFlags(PostProcessRenderContext context)
        {
            var flags = DepthTextureMode.None;

            foreach (var bundle in m_Bundles)
            {
                if (bundle.Value.settings.IsEnabledAndSupported(context))
                    flags |= bundle.Value.renderer.GetCameraFlags();
            }

            // Special case for AA & lighting effects
            if (context.IsTemporalAntialiasingActive())
                flags |= temporalAntialiasing.GetCameraFlags();

#if TND_FSR3
            if (context.IsFSR3Active())
                flags |= fsr3.GetCameraFlags();
#endif
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
            if (context.IsDLSSActive())
                flags |= dlss.GetCameraFlags();
#endif
#if TND_XeSS
            if (context.IsXeSSActive())
                flags |= xess.GetCameraFlags();
#endif
#if TND_SGSR2
            if (context.IsSGSR2Active())
                flags |= sgsr2.GetCameraFlags();
#endif

            if (fog.IsEnabledAndSupported(context))
                flags |= fog.GetCameraFlags();

            if (debugLayer.debugOverlay != DebugOverlay.None)
                flags |= debugLayer.GetCameraFlags();

            context.camera.depthTextureMode |= flags;
            cameraDepthFlags = flags;
        }

        /// <summary>
        /// This method should be called whenever you need to reset any temporal effect, e.g. when
        /// doing camera cuts.
        /// </summary>
        public void ResetHistory()
        {
            foreach (var bundle in m_Bundles)
                bundle.Value.ResetHistory();

            temporalAntialiasing.ResetHistory();
#if TND_FSR3
            fsr3.OnResetCamera();
            if (fsr3Stereo != null)
            {
                fsr3Stereo.OnResetCamera();
            }
#endif
#if TND_SGSR2
            sgsr2.OnResetCamera();
            if (sgsr2Stereo != null)
            {
                sgsr2Stereo.OnResetCamera();
            }
#endif
        }

        /// <summary>
        /// Checks if this layer has any active opaque-only effect.
        /// </summary>
        /// <param name="context">The current render context</param>
        /// <returns><c>true</c> if opaque-only effects are active, <c>false</c> otherwise</returns>
        public bool HasOpaqueOnlyEffects(PostProcessRenderContext context)
        {
            return HasActiveEffects(PostProcessEvent.BeforeTransparent, context);
        }

        /// <summary>
        /// Checks if this layer has any active effect at the given injection point.
        /// </summary>
        /// <param name="evt">The injection point to look for</param>
        /// <param name="context">The current render context</param>
        /// <returns><c>true</c> if any effect at the given injection point is active, <c>false</c>
        /// otherwise</returns>
        public bool HasActiveEffects(PostProcessEvent evt, PostProcessRenderContext context)
        {
            var list = sortedBundles[evt];

            foreach (var item in list)
            {
                bool enabledAndSupported = item.bundle.settings.IsEnabledAndSupported(context);

                if (context.isSceneView)
                {
                    if (item.bundle.attribute.allowInSceneView && enabledAndSupported)
                        return true;
                }
                else if (enabledAndSupported)
                {
                    return true;
                }
            }

            return false;
        }

        void SetupContext(PostProcessRenderContext context)
        {
            // Juggling required when a scene with post processing is loaded from an asset bundle
            // See #1148230
            // Additional !RuntimeUtilities.isValidResources() to fix #1262826
            // The static member s_Resources is unset by addressable. The code is ill formed as it
            // is not made to handle multiple scene.
            if (m_OldResources != m_Resources || !RuntimeUtilities.isValidResources())
            {
                RuntimeUtilities.UpdateResources(m_Resources);
                m_OldResources = m_Resources;
            }

            m_IsRenderingInSceneView = context.camera.cameraType == CameraType.SceneView;
            context.isSceneView = m_IsRenderingInSceneView;
            context.resources = m_Resources;
            context.propertySheets = m_PropertySheetFactory;
            context.debugLayer = debugLayer;
            context.antialiasing = antialiasingMode;
            context.temporalAntialiasing = temporalAntialiasing;
            context.sgsr = sgsr;
            context.sgsr2 = sgsr2;
            context.superResolution1 = fsr1;
            context.superResolution3 = fsr3;
            context.deepLearningSuperSampling = dlss;
            context.xeSuperSampling = xess;
            context.logHistogram = m_LogHistogram;

#if UNITY_2018_2_OR_NEWER
            context.physicalCamera = context.camera.usePhysicalProperties;
#endif

            SetLegacyCameraFlags(context);

            // Prepare debug overlay
            debugLayer.SetFrameSize(context.width, context.height);

            // Unsafe to keep this around but we need it for OnGUI events for debug views
            // Will be removed eventually
            m_CurrentContext = context;
        }

        /// <summary>
        /// Updates the state of the volume system. This should be called before any other
        /// post-processing method when running in a scriptable render pipeline. You don't need to
        /// call this method when running in one of the builtin pipelines.
        /// </summary>
        /// <param name="cam">The currently rendering camera.</param>
        /// <param name="cmd">A command buffer to fill.</param>
        public void UpdateVolumeSystem(Camera cam, CommandBuffer cmd)
        {
            if (m_SettingsUpdateNeeded)
            {
                cmd.BeginSample("VolumeBlending");
                PostProcessManager.instance.UpdateSettings(this, cam);
                cmd.EndSample("VolumeBlending");
                m_TargetPool.Reset();

                // TODO: fix me once VR support is in SRP
                // Needed in SRP so that _RenderViewportScaleFactor isn't 0
                if (RuntimeUtilities.scriptableRenderPipelineActive)
                    Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, 1f);
            }

            m_SettingsUpdateNeeded = false;
        }

        /// <summary>
        /// Renders effects in the <see cref="PostProcessEvent.BeforeTransparent"/> bucket. You
        /// should call <see cref="HasOpaqueOnlyEffects"/> before calling this method as it won't
        /// automatically blit source into destination if no opaque-only effect is active.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        public void RenderOpaqueOnly(PostProcessRenderContext context)
        {
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                SetupContext(context);

            TextureLerper.instance.BeginFrame(context);

            // Update & override layer settings first (volume blending), will only be done once per
            // frame, either here or in Render() if there isn't any opaque-only effect to render.
            // TODO: should be removed, keeping this here for older SRPs
            UpdateVolumeSystem(context.camera, context.command);

            RenderList(sortedBundles[PostProcessEvent.BeforeTransparent], context, "OpaqueOnly");
        }


        //TND Fixing TAA for VR
        private bool _runRightEyeOnce = false;

        /// <summary>
        /// Renders all effects not in the <see cref="PostProcessEvent.BeforeTransparent"/> bucket.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        public void Render(PostProcessRenderContext context)
        {
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                SetupContext(context);

            TextureLerper.instance.BeginFrame(context);
            var cmd = context.command;

            // Update & override layer settings first (volume blending) if the opaque only pass
            // hasn't been called this frame.
            // TODO: should be removed, keeping this here for older SRPs
            UpdateVolumeSystem(context.camera, context.command);

            // Do a NaN killing pass if needed
            int lastTarget = -1;
            RenderTargetIdentifier cameraTexture = context.source;

#if UNITY_2019_1_OR_NEWER
            if (context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
            {
                cmd.SetSinglePassStereo(SinglePassStereoMode.None);
                cmd.DisableShaderKeyword("UNITY_SINGLE_PASS_STEREO");
            }
#endif

            for (int eye = 0; eye < context.numberOfEyes; eye++)
            {
                bool preparedStereoSource = false;

                if (stopNaNPropagation && !m_NaNKilled)
                {
                    lastTarget = m_TargetPool.Get();
                    context.GetScreenSpaceTemporaryRT(cmd, lastTarget, 0, context.sourceFormat);
                    if (context.stereoActive && context.numberOfEyes > 1)
                    {
                        if (context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                        {
                            cmd.BlitFullscreenTriangleFromTexArray(context.source, lastTarget, RuntimeUtilities.copyFromTexArraySheet, 1, false, eye);
                            preparedStereoSource = true;
                        }
                        else if (context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
                        {
                            cmd.BlitFullscreenTriangleFromDoubleWide(context.source, lastTarget, RuntimeUtilities.copyStdFromDoubleWideMaterial, 1, eye);
                            preparedStereoSource = true;
                        }
                    }
                    else
                        cmd.BlitFullscreenTriangle(context.source, lastTarget, RuntimeUtilities.copySheet, 1);
                    context.source = lastTarget;
                    m_NaNKilled = true;
                }

                if (!preparedStereoSource && context.numberOfEyes > 1)
                {
                    lastTarget = m_TargetPool.Get();
                    context.GetScreenSpaceTemporaryRT(cmd, lastTarget, 0, context.sourceFormat);
                    if (context.stereoActive)
                    {
                        if (context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                        {
                            cmd.BlitFullscreenTriangleFromTexArray(context.source, lastTarget, RuntimeUtilities.copyFromTexArraySheet, 1, false, eye);
                            preparedStereoSource = true;
                        }
                        else if (context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
                        {
                            cmd.BlitFullscreenTriangleFromDoubleWide(context.source, lastTarget, RuntimeUtilities.copyStdFromDoubleWideMaterial, stopNaNPropagation ? 1 : 0, eye);
                            preparedStereoSource = true;
                        }
                    }
                    context.source = lastTarget;
                }

                // Right before upscaling & temporal anti-aliasing
                if (HasActiveEffects(PostProcessEvent.BeforeUpscaling, context))
                    lastTarget = RenderInjectionPoint(PostProcessEvent.BeforeUpscaling, context, "BeforeUpscaling", lastTarget);

                //TND Disabling MSAA because it breaks Unity's TAA
                if (context.stereoActive)
                {
                    if (context.IsTemporalAntialiasingActive() || context.IsSGSRActive() || context.IsFSR1Active() || context.IsFSR1Active() || context.IsFSR3Active() || context.IsDLSSActive() || context.IsXeSSActive() || context.IsSGSR2Active())
                    {
                        QualitySettings.antiAliasing = 0;
                    }
#if UNITY_STANDALONE
                    //TND Fixing TAA for VR, Only for PCVR
                    if (context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                    {
                        _runRightEyeOnce = !_runRightEyeOnce;
                        if (_runRightEyeOnce)
                        {
                            goto skipTAA;
                        }
                    }
#endif
                }

                if (context.IsTemporalAntialiasingActive() || context.IsSGSRActive() || context.IsFSR1Active())
                {
                    bool _runTAA = true;
#if TND_SGSR
                    if (context.IsSGSRActive())
                    {
                        _runTAA = context.sgsr.UnityTAAEnabled;
                    }
#endif
#if TND_FSR1
                    if (context.IsFSR1Active())
                    {
                        _runTAA = context.superResolution1.UnityTAAEnabled;
                    }
#endif

                    // Do temporal anti-aliasing first
                    if (_runTAA)
                    {
                        if (!RuntimeUtilities.scriptableRenderPipelineActive)
                        {
                            if (context.stereoActive)
                            {
                                // We only need to configure all of this once for stereo, during OnPreCull
                                if (context.camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right)
                                    temporalAntialiasing.ConfigureStereoJitteredProjectionMatrices(context);
                            }
                            else
                            {
                                temporalAntialiasing.ConfigureJitteredProjectionMatrix(context);
                            }
                        }
                        var taaTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, taaTarget, 0, context.sourceFormat);
                        context.destination = taaTarget;
                        temporalAntialiasing.Render(context);
                        context.source = taaTarget;
                        context.destination = finalDestination;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = taaTarget;
                    }

                    if (context.IsSGSRActive())
                    {
#if TND_SGSR
                        context.SetRenderSize(sgsr.displaySize);
                        var sgsrTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, sgsrTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        context.destination = sgsrTarget;
                        sgsr.Render(context);
                        context.source = sgsrTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = sgsrTarget;
#endif
                    }
                    else if (context.IsFSR1Active())
                    {
#if TND_FSR1
                        // Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(fsr1.displaySize);
                        var fsrTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, fsrTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        context.destination = fsrTarget;
                        fsr1.Render(context);
                        context.source = fsrTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = fsrTarget;
#endif
                    }
                }
                else if (context.IsFSR3Active())
                {
#if TND_FSR3
                    if (!context.stereoActive || context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                    {
                        fsr3.ConfigureJitteredProjectionMatrix(context);

                        // Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(fsr3.displaySize);

                        var fsrTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        //if (context.camera.stereoEnabled)
                        //{
                        context.GetScreenSpaceTemporaryRT(cmd, fsrTarget, 0, context.sourceFormat, RenderTextureReadWrite.Linear, isUpscaleOutput: true);
                        //}
                        //else
                        //{
                        //    context.GetScreenSpaceTemporaryRT(cmd, fsrTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        //}
                        context.destination = fsrTarget;
                        fsr3.colorOpaqueOnly = m_opaqueOnly;
                        fsr3.Render(context);
                        context.source = fsrTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = fsrTarget;
                    }
                    else if (context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                    {
                        //Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(fsr3Stereo.displaySize);

                        var fsrTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, fsrTarget, 0, context.sourceFormat, RenderTextureReadWrite.Linear, isUpscaleOutput: true);
                        context.destination = fsrTarget;
                        fsr3Stereo.colorOpaqueOnly = m_opaqueOnly;
                        fsr3Stereo.Render(context, true);
                        context.source = fsrTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = fsrTarget;
                    }
#endif
                }
                else if (context.IsDLSSActive())
                {
#if TND_DLSS && UNITY_STANDALONE_WIN && UNITY_64
                    if (!context.stereoActive || context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                    {
                        dlss.ConfigureJitteredProjectionMatrix(context);

                        // Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(dlss.displaySize);

                        var dlssTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, dlssTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        context.destination = dlssTarget;
                        dlss.Render(context);
                        context.source = dlssTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = dlssTarget;
                    }
                    else if (context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                    {
                        //Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(dlssStereo.displaySize);

                        var dlssTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, dlssTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        context.destination = dlssTarget;
                        dlssStereo.Render(context, true);
                        context.source = dlssTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = dlssTarget;
                    }
#endif
                }
                else if (context.IsXeSSActive())
                {
#if TND_XeSS
                    //if (!context.stereoActive || context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                    //{
                    xess.ConfigureJitteredProjectionMatrix(context);

                    // Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                    context.SetRenderSize(xess.displaySize);

                    var xessTarget = m_TargetPool.Get();
                    var finalDestination = context.destination;
                    context.GetScreenSpaceTemporaryRT(cmd, xessTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                    context.destination = xessTarget;
                    xess.Render(context);
                    context.source = xessTarget;
                    context.destination = finalDestination;

                    // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                    RuntimeUtilities.AllowDynamicResolution = false;

                    if (lastTarget > -1)
                        cmd.ReleaseTemporaryRT(lastTarget);

                    lastTarget = xessTarget;
                    //}
                    //else if (context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                    //{
                    //    //Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                    //    context.SetRenderSize(xessStereo.displaySize);

                    //    var xessTarget = m_TargetPool.Get();
                    //    var finalDestination = context.destination;
                    //    context.GetScreenSpaceTemporaryRT(cmd, xessTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                    //    context.destination = xessTarget;
                    //    //xessStereo.Render(context, true);
                    //    context.source = xessTarget;
                    //    context.destination = finalDestination;

                    //    // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                    //    RuntimeUtilities.AllowDynamicResolution = false;

                    //    if (lastTarget > -1)
                    //        cmd.ReleaseTemporaryRT(lastTarget);

                    //    lastTarget = xessTarget;
                    //}
#endif
                }
                else if (context.IsSGSR2Active())
                {
#if TND_SGSR2
                    if (!context.stereoActive || context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                    {
                        sgsr2.ConfigureJitteredProjectionMatrix(context);

                        // Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(sgsr2.displaySize);

                        var upscaleTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        //if (context.camera.stereoEnabled)
                        //{
                        context.GetScreenSpaceTemporaryRT(cmd, upscaleTarget, 0, context.sourceFormat, RenderTextureReadWrite.Linear, isUpscaleOutput: true);
                        //}
                        //else
                        //{
                        //    context.GetScreenSpaceTemporaryRT(cmd, fsrTarget, 0, context.sourceFormat, isUpscaleOutput: true);
                        //}
                        context.destination = upscaleTarget;
                        sgsr2.colorOpaqueOnly = m_opaqueOnly;
                        sgsr2.Render(context);
                        context.source = upscaleTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = upscaleTarget;
                    }
                    else if (context.stereoActive && context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                    {
                        //Set the upscaler's output to full display resolution, as well as for all following post-processing effects
                        context.SetRenderSize(sgsr2Stereo.displaySize);

                        var upscaleTarget = m_TargetPool.Get();
                        var finalDestination = context.destination;
                        context.GetScreenSpaceTemporaryRT(cmd, upscaleTarget, 0, context.sourceFormat, RenderTextureReadWrite.Linear, isUpscaleOutput: true);
                        context.destination = upscaleTarget;
                        sgsr2Stereo.colorOpaqueOnly = m_opaqueOnly;
                        sgsr2Stereo.Render(context, true);
                        context.source = upscaleTarget;
                        context.destination = finalDestination;

                        // Disable dynamic scaling on render targets, so all subsequent effects will be applied on the full resolution upscaled image
                        RuntimeUtilities.AllowDynamicResolution = false;

                        if (lastTarget > -1)
                            cmd.ReleaseTemporaryRT(lastTarget);

                        lastTarget = upscaleTarget;
                    }
#endif
                }
            skipTAA:

                bool hasBeforeStackEffects = HasActiveEffects(PostProcessEvent.BeforeStack, context);
                bool hasAfterStackEffects = HasActiveEffects(PostProcessEvent.AfterStack, context) && !breakBeforeColorGrading;
                bool needsFinalPass = (hasAfterStackEffects
                    || (antialiasingMode == Antialiasing.FastApproximateAntialiasing) || (antialiasingMode == Antialiasing.SubpixelMorphologicalAntialiasing && subpixelMorphologicalAntialiasing.IsSupported()))
                    && !breakBeforeColorGrading;

                // Right before the builtin stack
                if (hasBeforeStackEffects)
                    lastTarget = RenderInjectionPoint(PostProcessEvent.BeforeStack, context, "BeforeStack", lastTarget);

                // Builtin stack
                lastTarget = RenderBuiltins(context, !needsFinalPass, lastTarget, eye);

                // After the builtin stack but before the final pass (before FXAA & Dithering)
                if (hasAfterStackEffects)
                    lastTarget = RenderInjectionPoint(PostProcessEvent.AfterStack, context, "AfterStack", lastTarget);

                // And close with the final pass
                if (needsFinalPass)
                    RenderFinalPass(context, lastTarget, eye);

                if (context.stereoActive)
                    context.source = cameraTexture;
            }

#if UNITY_2019_1_OR_NEWER
            if (context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
            {
                cmd.SetSinglePassStereo(SinglePassStereoMode.SideBySide);
                cmd.EnableShaderKeyword("UNITY_SINGLE_PASS_STEREO");
            }
#endif

            // Render debug monitors & overlay if requested
            debugLayer.RenderSpecialOverlays(context);
            debugLayer.RenderMonitors(context);

            // End frame cleanup
            TextureLerper.instance.EndFrame();
            debugLayer.EndFrame();
            m_SettingsUpdateNeeded = true;
            m_NaNKilled = false;
        }

        int RenderInjectionPoint(PostProcessEvent evt, PostProcessRenderContext context, string marker, int releaseTargetAfterUse = -1)
        {
            int tempTarget = m_TargetPool.Get();
            var finalDestination = context.destination;

            var cmd = context.command;
            context.GetScreenSpaceTemporaryRT(cmd, tempTarget, 0, context.sourceFormat);
            context.destination = tempTarget;
            RenderList(sortedBundles[evt], context, marker);
            context.source = tempTarget;
            context.destination = finalDestination;

            if (releaseTargetAfterUse > -1)
                cmd.ReleaseTemporaryRT(releaseTargetAfterUse);

            return tempTarget;
        }

        void RenderList(List<SerializedBundleRef> list, PostProcessRenderContext context, string marker)
        {
            var cmd = context.command;
            cmd.BeginSample(marker);

            // First gather active effects - we need this to manage render targets more efficiently
            m_ActiveEffects.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var effect = list[i].bundle;
                if (effect.settings.IsEnabledAndSupported(context))
                {
                    if (!context.isSceneView || (context.isSceneView && effect.attribute.allowInSceneView))
                        m_ActiveEffects.Add(effect.renderer);
                }
            }

            int count = m_ActiveEffects.Count;

            // If there's only one active effect, we can simply execute it and skip the rest
            if (count == 1)
            {
                m_ActiveEffects[0].RenderOrLog(context);
            }
            else
            {
                // Else create the target chain
                m_Targets.Clear();
                m_Targets.Add(context.source); // First target is always source

                int tempTarget1 = m_TargetPool.Get();
                int tempTarget2 = m_TargetPool.Get();

                for (int i = 0; i < count - 1; i++)
                    m_Targets.Add(i % 2 == 0 ? tempTarget1 : tempTarget2);

                m_Targets.Add(context.destination); // Last target is always destination

                // Render
                context.GetScreenSpaceTemporaryRT(cmd, tempTarget1, 0, context.sourceFormat);
                if (count > 2)
                    context.GetScreenSpaceTemporaryRT(cmd, tempTarget2, 0, context.sourceFormat);

                for (int i = 0; i < count; i++)
                {
                    context.source = m_Targets[i];
                    context.destination = m_Targets[i + 1];
                    m_ActiveEffects[i].RenderOrLog(context);
                }

                cmd.ReleaseTemporaryRT(tempTarget1);
                if (count > 2)
                    cmd.ReleaseTemporaryRT(tempTarget2);
            }

            cmd.EndSample(marker);
        }

        void ApplyFlip(PostProcessRenderContext context, MaterialPropertyBlock properties)
        {
            if (context.flip && !context.isSceneView)
                properties.SetVector(ShaderIDs.UVTransform, new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
            else
                ApplyDefaultFlip(properties);
        }

        void ApplyDefaultFlip(MaterialPropertyBlock properties)
        {
            properties.SetVector(ShaderIDs.UVTransform, SystemInfo.graphicsUVStartsAtTop ? new Vector4(1.0f, -1.0f, 0.0f, 1.0f) : new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        }

        int RenderBuiltins(PostProcessRenderContext context, bool isFinalPass, int releaseTargetAfterUse = -1, int eye = -1)
        {
            var uberSheet = context.propertySheets.Get(context.resources.shaders.uber);
            uberSheet.ClearKeywords();
            uberSheet.properties.Clear();
            context.uberSheet = uberSheet;
            context.autoExposureTexture = RuntimeUtilities.whiteTexture;
            context.bloomBufferNameID = -1;

            if (isFinalPass && context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                uberSheet.EnableKeyword("STEREO_INSTANCING_ENABLED");

            var cmd = context.command;
            cmd.BeginSample("BuiltinStack");

            int tempTarget = -1;
            var finalDestination = context.destination;

            if (!isFinalPass)
            {
                // Render to an intermediate target as this won't be the final pass
                tempTarget = m_TargetPool.Get();
                context.GetScreenSpaceTemporaryRT(cmd, tempTarget, 0, context.sourceFormat);
                context.destination = tempTarget;

                // Handle FXAA's keep alpha mode
                if (antialiasingMode == Antialiasing.FastApproximateAntialiasing && !fastApproximateAntialiasing.keepAlpha && RuntimeUtilities.hasAlpha(context.sourceFormat))
                    uberSheet.properties.SetFloat(ShaderIDs.LumaInAlpha, 1f);
            }

            // Depth of field final combination pass used to be done in Uber which led to artifacts
            // when used at the same time as Bloom (because both effects used the same source, so
            // the stronger bloom was, the more DoF was eaten away in out of focus areas)
            int depthOfFieldTarget = RenderEffect<DepthOfField>(context, true);

            // Motion blur is a separate pass - could potentially be done after DoF depending on the
            // kind of results you're looking for...
            int motionBlurTarget = RenderEffect<MotionBlur>(context, true);

            // Prepare exposure histogram if needed
            if (ShouldGenerateLogHistogram(context))
                m_LogHistogram.Generate(context);

            // Uber effects
            // 1336238: override xrActiveEye in multipass with the currently rendered eye to fix flickering issue.
            int xrActiveEyeBackup = context.xrActiveEye;
            if (context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.MultiPass)
                context.xrActiveEye = eye;
            RenderEffect<AutoExposure>(context);
            context.xrActiveEye = xrActiveEyeBackup; // restore the eye

            uberSheet.properties.SetTexture(ShaderIDs.AutoExposureTex, context.autoExposureTexture);

            RenderEffect<LensDistortion>(context);
            RenderEffect<ChromaticAberration>(context);
            RenderEffect<Bloom>(context);
            RenderEffect<Vignette>(context);
            RenderEffect<Grain>(context);

            if (!breakBeforeColorGrading)
                RenderEffect<ColorGrading>(context);

            if (isFinalPass)
            {
                uberSheet.EnableKeyword("FINALPASS");
                dithering.Render(context);
                ApplyFlip(context, uberSheet.properties);
            }
            else
            {
                ApplyDefaultFlip(uberSheet.properties);
            }

            if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
            {
                uberSheet.properties.SetFloat(ShaderIDs.DepthSlice, eye);
                cmd.BlitFullscreenTriangleToTexArray(context.source, context.destination, uberSheet, 0, false, eye);
            }
            else if (isFinalPass && context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
            {
                cmd.BlitFullscreenTriangleToDoubleWide(context.source, context.destination, uberSheet, 0, eye);
            }
#if LWRP_1_0_0_OR_NEWER || UNIVERSAL_1_0_0_OR_NEWER
            else if (isFinalPass)
                cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0, false, context.camera.pixelRect);
#endif
            else
                cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0);

            context.source = context.destination;
            context.destination = finalDestination;

            if (releaseTargetAfterUse > -1)
                cmd.ReleaseTemporaryRT(releaseTargetAfterUse);
            if (motionBlurTarget > -1)
                cmd.ReleaseTemporaryRT(motionBlurTarget);
            if (depthOfFieldTarget > -1)
                cmd.ReleaseTemporaryRT(depthOfFieldTarget);
            if (context.bloomBufferNameID > -1)
                cmd.ReleaseTemporaryRT(context.bloomBufferNameID);

            cmd.EndSample("BuiltinStack");

            return tempTarget;
        }

        // This pass will have to be disabled for HDR screen output as it's an LDR pass
        void RenderFinalPass(PostProcessRenderContext context, int releaseTargetAfterUse = -1, int eye = -1)
        {
            var cmd = context.command;
            cmd.BeginSample("FinalPass");

            if (breakBeforeColorGrading)
            {
                var sheet = context.propertySheets.Get(context.resources.shaders.discardAlpha);
                if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                    sheet.EnableKeyword("STEREO_INSTANCING_ENABLED");

                if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                {
                    sheet.properties.SetFloat(ShaderIDs.DepthSlice, eye);
                    cmd.BlitFullscreenTriangleToTexArray(context.source, context.destination, sheet, 0, false, eye);
                }
                else if (context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
                {
                    cmd.BlitFullscreenTriangleToDoubleWide(context.source, context.destination, sheet, 0, eye);
                }
                else
                    cmd.BlitFullscreenTriangle(context.source, context.destination, sheet, 0);
            }
            else
            {
                var uberSheet = context.propertySheets.Get(context.resources.shaders.finalPass);
                uberSheet.ClearKeywords();
                uberSheet.properties.Clear();
                context.uberSheet = uberSheet;
                int tempTarget = -1;

                if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                    uberSheet.EnableKeyword("STEREO_INSTANCING_ENABLED");

                if (antialiasingMode == Antialiasing.FastApproximateAntialiasing)
                {
                    uberSheet.EnableKeyword(fastApproximateAntialiasing.fastMode
                        ? "FXAA_LOW"
                        : "FXAA"
                    );

                    if (RuntimeUtilities.hasAlpha(context.sourceFormat))
                    {
                        if (fastApproximateAntialiasing.keepAlpha)
                            uberSheet.EnableKeyword("FXAA_KEEP_ALPHA");
                    }
                    else
                        uberSheet.EnableKeyword("FXAA_NO_ALPHA");
                }
                else if (antialiasingMode == Antialiasing.SubpixelMorphologicalAntialiasing && subpixelMorphologicalAntialiasing.IsSupported())
                {
                    tempTarget = m_TargetPool.Get();
                    var finalDestination = context.destination;
                    context.GetScreenSpaceTemporaryRT(context.command, tempTarget, 0, context.sourceFormat);
                    context.destination = tempTarget;
                    subpixelMorphologicalAntialiasing.Render(context);
                    context.source = tempTarget;
                    context.destination = finalDestination;
                }

                dithering.Render(context);

                ApplyFlip(context, uberSheet.properties);
                if (context.stereoActive && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced)
                {
                    uberSheet.properties.SetFloat(ShaderIDs.DepthSlice, eye);
                    cmd.BlitFullscreenTriangleToTexArray(context.source, context.destination, uberSheet, 0, false, eye);
                }
                else if (context.stereoActive && context.numberOfEyes > 1 && context.stereoRenderingMode == PostProcessRenderContext.StereoRenderingMode.SinglePass)
                {
                    cmd.BlitFullscreenTriangleToDoubleWide(context.source, context.destination, uberSheet, 0, eye);
                }
                else
#if LWRP_1_0_0_OR_NEWER || UNIVERSAL_1_0_0_OR_NEWER
                    cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0, false, context.camera.pixelRect);
#else
                    cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0);
#endif

                if (tempTarget > -1)
                    cmd.ReleaseTemporaryRT(tempTarget);
            }

            if (releaseTargetAfterUse > -1)
                cmd.ReleaseTemporaryRT(releaseTargetAfterUse);

            cmd.EndSample("FinalPass");
        }

        int RenderEffect<T>(PostProcessRenderContext context, bool useTempTarget = false)
            where T : PostProcessEffectSettings
        {
            var effect = GetBundle<T>();

            if (!effect.settings.IsEnabledAndSupported(context))
                return -1;

            if (m_IsRenderingInSceneView && !effect.attribute.allowInSceneView)
                return -1;

            if (!useTempTarget)
            {
                effect.renderer.RenderOrLog(context);
                return -1;
            }

            var finalDestination = context.destination;
            var tempTarget = m_TargetPool.Get();
            context.GetScreenSpaceTemporaryRT(context.command, tempTarget, 0, context.sourceFormat);
            context.destination = tempTarget;
            effect.renderer.RenderOrLog(context);
            context.source = tempTarget;
            context.destination = finalDestination;
            return tempTarget;
        }

        bool ShouldGenerateLogHistogram(PostProcessRenderContext context)
        {
            bool autoExpo = GetBundle<AutoExposure>().settings.IsEnabledAndSupported(context);
            bool lightMeter = debugLayer.lightMeter.IsRequestedAndSupported(context);
            return autoExpo || lightMeter;
        }
    }
}
