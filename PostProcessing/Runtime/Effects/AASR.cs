using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if TND_AASR
using TND.AASR;
using ArmASR;
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class AASR
    {
        [Tooltip("Fallback AA for when AASR is not supported")]
        public Antialiasing fallBackAA = Antialiasing.None;
        [Range(0, 1)]
        public float antiGhosting = 0.0f;

#if TND_AASR

        [Tooltip("Standard scaling ratio presets.")]
        [Header("AASR Settings")]
        public AASR_Variant variant = AASR_Variant.Quality;
        public AASR_Quality qualityMode = AASR_Quality.Quality;

        [Tooltip("Apply RCAS sharpening to the image after upscaling.")]
        public bool Sharpening = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.5f;

        [Header("Transparency Settings")]
        [Tooltip("Automatically generate a reactive mask based on the difference between opaque-only render output and the final render output including alpha transparencies.")]
        public bool autoGenerateReactiveMask = true;
        [Tooltip("A value to scale the output")]
        [Range(0, 1)] public float ReactiveScale = 0.9f;
        [Tooltip("A threshold value to generate a binary reactive mask")]
        [Range(0, 1)] public float ReactiveThreshold = 0.1f;
        [Tooltip("A value to set for the binary reactive mask")]
        [Range(0, 1)] public float ReactiveBinaryValue = 0.5f;
        [Tooltip("Flags to determine how to generate the reactive mask")]
        public Asr.GenerateReactiveFlags flags = Asr.GenerateReactiveFlags.ApplyTonemap | Asr.GenerateReactiveFlags.ApplyThreshold | Asr.GenerateReactiveFlags.UseComponentsMax;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2;
        [Range(0, 1)]
        public float mipMapBiasOverride = 1f;

        [HideInInspector, Tooltip("Optional texture to control the influence of the current frame on the reconstructed output. If unset, either an auto-generated or a default cleared reactive mask will be used.")]
        public Texture reactiveMask = null;
        [HideInInspector, Tooltip("Optional texture for marking areas of specialist rendering which should be accounted for during the upscaling process. If unset, a default cleared mask will be used.")]
        public Texture transparencyAndCompositionMask = null;

        [HideInInspector, Tooltip("Choose where to get the exposure value from. Use auto-exposure from either AASR or Unity, provide a manual exposure texture, or use a default value.")]
        public ExposureSource exposureSource = ExposureSource.Auto;
        [HideInInspector, Tooltip("Value by which the input signal will be divided, to get back to the original signal produced by the game.")]
        public float preExposure = 1.0f;
        [HideInInspector, Tooltip("Optional 1x1 texture containing the exposure value for the current frame.")]
        public Texture exposure = null;
        public enum ExposureSource
        {
            Default,
            Auto,
            Unity,
            Manual,
        }

        [HideInInspector, Tooltip("(Experimental) Automatically generate and use Reactive mask and Transparency & composition mask internally.")]
        public bool autoGenerateTransparencyAndComposition = false;
        [Tooltip("Parameters to control the process of auto-generating transparency and composition masks.")]
        public GenerateTcrParameters generateTransparencyAndCompositionParameters = new GenerateTcrParameters();

        [Serializable]
        public class GenerateTcrParameters
        {
            [Tooltip("Setting this value too small will cause visual instability. Larger values can cause ghosting.")]
            [Range(0, 1)] public float autoTcThreshold = 0.05f;
            [Tooltip("Smaller values will increase stability at hard edges of translucent objects.")]
            [Range(0, 2)] public float autoTcScale = 1.0f;
            [Tooltip("Larger values result in more reactive pixels.")]
            [Range(0, 10)] public float autoReactiveScale = 5.0f;
            [Tooltip("Maximum value reactivity can reach.")]
            [Range(0, 1)] public float autoReactiveMax = 0.9f;
        }

        public Vector2 jitter
        {
            get; private set;
        }
        public Vector2Int renderSize => _maxRenderSize;
        public Vector2Int displaySize => _displaySize;
        public RenderTargetIdentifier colorOpaqueOnly
        {
            get; set;
        }

        private AsrContext _asrContext;
        private AsrAssets _asrAssets;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private bool _resetHistory;

        private Asr.DispatchDescription _dispatchDescription = new Asr.DispatchDescription();
        private Asr.GenerateReactiveDescription _genReactiveDescription = new Asr.GenerateReactiveDescription();

        private AASR_Variant _prevVariantMode;
        private AASR_Quality _prevQualityMode;
        private ExposureSource _prevExposureSource;
        private Vector2Int _prevDisplaySize;

        private Rect _originalRect;

        //Mipmap variables
        protected ulong _previousLength;
        protected float _prevMipMapBias;
        protected float _mipMapTimer = float.MaxValue;

        private bool isStereoRendering = false;

        /// <summary>
        /// Resets the camera for the next frame, clearing all the buffers saved from previous frames in order to prevent artifacts.
        /// Should be called in or before PreRender oh the frame where the camera makes a jumpcut.
        /// Is automatically disabled the frame after.
        /// </summary>
        public void OnResetCamera()
        {
            _resetHistory = true;
        }

        /// <summary>
        /// Updates a single texture to the set MipMap Bias.
        /// Should be called when an object is instantiated, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipmapSingleTexture(Texture texture)
        {
            MipMapUtils.OnMipMapSingleTexture(texture, renderSize.x, displaySize.x, mipMapBiasOverride);
        }

        /// <summary>
        /// Updates all textures currently loaded to the set MipMap Bias.
        /// Should be called when a lot of new textures are loaded, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipMapAllTextures()
        {
            MipMapUtils.OnMipMapAllTextures(renderSize.x, displaySize.x, mipMapBiasOverride);
        }
        /// <summary>
        /// Resets all currently loaded textures to the default mipmap bias.
        /// </summary>
        public void OnResetAllMipMaps()
        {
            MipMapUtils.OnResetAllMipMaps(ref _prevMipMapBias);
        }

        public bool IsSupported()
        {
            return SystemInfo.supportsComputeShaders && SystemInfo.supportsMotionVectors;
        }

        internal DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        internal void Release()
        {
            DestroyAASRContext();
        }

        internal void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            if (qualityMode == AASR_Quality.Off)
            {
                Release();
                return;
            }
            ApplyJitter(context.camera, context);
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context)
        {
            if (context.camera.stereoEnabled)
            {
                if (context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                {
                    return;
                }
            }

            var camera = context.camera;
            _originalRect = camera.rect;

            // Determine the desired rendering and display resolutions
            _displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);

            Asr.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, (Asr.QualityMode)((int)qualityMode - 1));
            _maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);
        }

        public void ConfigureCameraViewportRightEye(PostProcessRenderContext context)
        {
            if (context.camera.stereoEnabled)
            {
                if (context.camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
                {
                    return;
                }
            }

            // Determine the desired rendering and display resolutions
            var camera = context.camera;

            _originalRect = context.aasr._originalRect;
            _displaySize = new Vector2Int(context.aasr._displaySize.x, context.aasr._displaySize.y);

            qualityMode = context.aasr.qualityMode;
            variant = context.aasr.variant;

            Asr.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, (Asr.QualityMode)((int)qualityMode - 1));
            _maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);
        }

        internal void ResetCameraViewport(PostProcessRenderContext context)
        {
            context.camera.rect = _originalRect;
        }

        internal void Render(PostProcessRenderContext context, bool _stereoRendering = false)
        {
            var cmd = context.command;
            if (qualityMode == AASR_Quality.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }

            if (_stereoRendering)
            {
                isStereoRendering = _stereoRendering;
                cmd.BeginSample("AASR Right Eye");
            }
            else
            {
                cmd.BeginSample("AASR");
            }

            if (autoTextureUpdate && !isStereoRendering)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }

            // Monitor for any resolution changes and recreate the AASR context if necessary
            // We can't create an AASR context without info from the post-processing context, so delay the initial setup until here
            if (_asrContext == null || _displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || variant != _prevVariantMode || qualityMode != _prevQualityMode || exposureSource != _prevExposureSource)
            {
                DestroyAASRContext();
                CreateAASRContext(context);
                _mipMapTimer = Mathf.Infinity;
            }

            SetupDispatchDescription(context);

            if (autoGenerateReactiveMask)
            {
                SetupAutoReactiveDescription(context);

                var scaledRenderSize = _genReactiveDescription.RenderSize;
                cmd.GetTemporaryRT(AsrShaderIDs.UavAutoReactive, scaledRenderSize.x, scaledRenderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
                _asrContext.GenerateReactiveMask(_genReactiveDescription, cmd);
                _dispatchDescription.Reactive = new ResourceView(AsrShaderIDs.UavAutoReactive);
            }

            _asrContext.Dispatch(_dispatchDescription, cmd);

            if (_stereoRendering)
            {
                cmd.EndSample("AASR Right Eye");
            }
            else
            {
                cmd.EndSample("AASR");
            }

            _resetHistory = false;
        }

        private void CreateAASRContext(PostProcessRenderContext context)
        {
            if (context.camera.GetComponent<AASR_BASE>() != null)
            {
                context.camera.GetComponent<AASR_BASE>().enabled = false;
                Debug.LogWarning("[AASR] Don't use the AASR_BIRP and Custom Post Processing stack at the same time!");
            }

            _prevVariantMode = variant;
            _prevQualityMode = qualityMode;
            _prevExposureSource = exposureSource;
            _prevDisplaySize = _displaySize;


            // Initialize AASR context
            Asr.InitializationFlags flags = 0;
            if (context.camera.allowHDR)
                flags |= Asr.InitializationFlags.EnableHighDynamicRange;
            if (exposureSource == ExposureSource.Auto)
                flags |= Asr.InitializationFlags.EnableAutoExposure;
            if (RuntimeUtilities.IsDynamicResolutionEnabled(context.camera))
                flags |= Asr.InitializationFlags.EnableDynamicResolution;
            if (context.camera.stereoEnabled)
                flags |= Asr.InitializationFlags.EnableDisplayResolutionMotionVectors;

            if (_asrAssets == null)
            {
                _asrAssets = Resources.Load<AsrAssets>("ASR Assets");
            }
            _asrContext = Asr.CreateContext((Asr.Variant)variant, _displaySize, _maxRenderSize, _asrAssets.shaderBundle, flags);
        }

        private void DestroyAASRContext()
        {
            if (_asrContext != null)
            {
                _asrContext.Destroy();
                _asrContext = null;
            }

            MipMapUtils.OnResetAllMipMaps(ref _prevMipMapBias);
        }

        private void ApplyJitter(Camera camera, PostProcessRenderContext context)
        {
            var scaledRenderSize = GetScaledRenderSize(camera);

            // Perform custom jittering of the camera's projection matrix according to AASR's recipe
            int jitterPhaseCount = Asr.GetJitterPhaseCount(scaledRenderSize.x, _displaySize.x);
            Asr.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);

            _dispatchDescription.JitterOffset = new Vector2(jitterX, jitterY);

            jitterX = 2.0f * jitterX / scaledRenderSize.x;
            jitterY = 2.0f * jitterY / scaledRenderSize.y;

            jitterX += UnityEngine.Random.Range(-0.001f * antiGhosting, 0.001f * antiGhosting);
            jitterY += UnityEngine.Random.Range(-0.001f * antiGhosting, 0.001f * antiGhosting);
            jitter = new Vector2(jitterX, jitterY);

            if (camera.stereoEnabled)
            {
                // We only need to configure all of this once for stereo, during OnPreCull
                if (camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right)
                    ConfigureStereoJitteredProjectionMatrices(context, camera);
            }
            else
            {
                ConfigureJitteredProjectionMatrix(camera, jitterX, jitterY);
            }

        }

        /// <summary>
        /// Prepares the jittered and non jittered projection matrices.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        public void ConfigureJitteredProjectionMatrix(Camera camera, float jitterX, float jitterY)
        {
            var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
            var m_projectionMatrix = camera.projectionMatrix;
            camera.nonJitteredProjectionMatrix = m_projectionMatrix;
            camera.projectionMatrix = jitterTranslationMatrix * camera.nonJitteredProjectionMatrix;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        /// <summary>
        /// Prepares the jittered and non jittered projection matrices for stereo rendering.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        // TODO: We'll probably need to isolate most of this for SRPs
        public void ConfigureStereoJitteredProjectionMatrices(PostProcessRenderContext context, Camera camera)
        {
            for (var eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
            {
                // This saves off the device generated projection matrices as non-jittered
                camera.CopyStereoDeviceProjectionMatrixToNonJittered(eye);
                var originalProj = camera.GetStereoNonJitteredProjectionMatrix(eye);
                // Currently no support for custom jitter func, as VR devices would need to provide
                // original projection matrix as input along with jitter
                var jitteredMatrix = RuntimeUtilities.GenerateJitteredProjectionMatrixFromOriginal(context, originalProj, jitter);
                camera.SetStereoProjectionMatrix(eye, jitteredMatrix);
            }

            // jitter has to be scaled for the actual eye texture size, not just the intermediate texture size
            // which could be double-wide in certain stereo rendering scenarios
            jitter = new Vector2(jitter.x / context.screenWidth, jitter.y / context.screenHeight);
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        private void SetupDispatchDescription(PostProcessRenderContext context)
        {
            var camera = context.camera;

            // Set up the main AASR dispatch parameters
            // The input textures are left blank here, as they get bound directly through SetGlobalTexture elsewhere in this source file
            _dispatchDescription.Color = new ResourceView(context.source, RenderTextureSubElement.Color);
            _dispatchDescription.Depth = new ResourceView(GetDepthTexture(context.camera), RenderTextureSubElement.Depth);
            _dispatchDescription.MotionVectors = new ResourceView(BuiltinRenderTextureType.MotionVectors);

            _dispatchDescription.Exposure = ResourceView.Unassigned;
            _dispatchDescription.Reactive = ResourceView.Unassigned;
            _dispatchDescription.TransparencyAndComposition = ResourceView.Unassigned;

            var scaledRenderSize = GetScaledRenderSize(context.camera);

            if (camera.stereoEnabled)
            {
                _dispatchDescription.MotionVectorScale.x = -displaySize.x;
                _dispatchDescription.MotionVectorScale.y = -displaySize.y;
            }
            else
            {
                _dispatchDescription.MotionVectorScale.x = -scaledRenderSize.x;
                _dispatchDescription.MotionVectorScale.y = -scaledRenderSize.y;
            }

            if (!isStereoRendering)
            {
                if (exposureSource == ExposureSource.Manual && exposure != null)
                    _dispatchDescription.Exposure = new ResourceView(exposure);
                if (exposureSource == ExposureSource.Unity)
                    _dispatchDescription.Exposure = new ResourceView(context.autoExposureTexture);
                if (reactiveMask != null)
                    _dispatchDescription.Reactive = new ResourceView(reactiveMask);
                if (transparencyAndCompositionMask != null)
                    _dispatchDescription.TransparencyAndComposition = new ResourceView(transparencyAndCompositionMask);

                _dispatchDescription.Output = new ResourceView(context.destination, RenderTextureSubElement.Color);
                _dispatchDescription.PreExposure = preExposure;

                _dispatchDescription.EnableSharpening = Sharpening;
                _dispatchDescription.Sharpness = sharpness;

                _dispatchDescription.RenderSize = scaledRenderSize;
                _dispatchDescription.InputResourceSize = scaledRenderSize;

                _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
                _dispatchDescription.CameraNear = camera.nearClipPlane;
                _dispatchDescription.CameraFar = camera.farClipPlane;
                _dispatchDescription.CameraFovAngleVertical = camera.fieldOfView * Mathf.Deg2Rad;
                _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
                _dispatchDescription.Reset = _resetHistory;

                // Set up the parameters for the optional experimental auto-TCR feature
                //_dispatchDescription.EnableAutoReactive = autoGenerateTransparencyAndComposition;//TODO
            }
            else
            {
                if (exposureSource == ExposureSource.Manual && context.aasr.exposure != null)
                    _dispatchDescription.Exposure = new ResourceView(context.aasr.exposure);
                if (exposureSource == ExposureSource.Unity)
                    _dispatchDescription.Exposure = new ResourceView(context.autoExposureTexture);
                if (reactiveMask != null)
                    _dispatchDescription.Reactive = new ResourceView(context.aasr.reactiveMask);
                if (transparencyAndCompositionMask != null)
                    _dispatchDescription.TransparencyAndComposition = new ResourceView(context.aasr.transparencyAndCompositionMask);

                _dispatchDescription.Output = new ResourceView(context.destination);
                _dispatchDescription.PreExposure = context.aasr.preExposure;
                _dispatchDescription.EnableSharpening = context.aasr.Sharpening;

                _dispatchDescription.Sharpness = context.aasr.sharpness;

                _dispatchDescription.RenderSize = scaledRenderSize;
                _dispatchDescription.InputResourceSize = scaledRenderSize;

                _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
                _dispatchDescription.CameraNear = camera.nearClipPlane;
                _dispatchDescription.CameraFar = camera.farClipPlane;
                _dispatchDescription.CameraFovAngleVertical = camera.fieldOfView * Mathf.Deg2Rad;
                _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
                _dispatchDescription.Reset = context.aasr._resetHistory;

                autoGenerateReactiveMask = context.aasr.autoGenerateReactiveMask;
            }

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as AASR expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }
        }

        private void SetupAutoReactiveDescription(PostProcessRenderContext context)
        {
            // Set up the parameters to auto-generate a reactive mask
            _genReactiveDescription.ColorOpaqueOnly = new ResourceView(colorOpaqueOnly);
            _genReactiveDescription.ColorPreUpscale = new ResourceView(context.source);
            _genReactiveDescription.OutReactive = new ResourceView(AsrShaderIDs.UavAutoReactive);
            _genReactiveDescription.RenderSize = GetScaledRenderSize(context.camera);

            if (!isStereoRendering)
            {
                _genReactiveDescription.Scale = ReactiveScale;
                _genReactiveDescription.CutoffThreshold = ReactiveThreshold;
                _genReactiveDescription.BinaryValue = ReactiveBinaryValue;
                _genReactiveDescription.Flags = flags;
            }
            else
            {
                _genReactiveDescription.Scale = context.aasr.ReactiveScale;
                _genReactiveDescription.CutoffThreshold = context.aasr.ReactiveThreshold;
                _genReactiveDescription.BinaryValue = context.aasr.ReactiveBinaryValue;
                _genReactiveDescription.Flags = context.aasr.flags;
            }
        }

        private static BuiltinRenderTextureType GetDepthTexture(Camera cam)
        {
            RenderingPath renderingPath = cam.actualRenderingPath;
            return renderingPath == RenderingPath.Forward || renderingPath == RenderingPath.VertexLit ? BuiltinRenderTextureType.Depth : BuiltinRenderTextureType.CameraTarget;
        }

        internal Vector2Int GetScaledRenderSize(Camera camera)
        {
            if (!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
                return _maxRenderSize;

            return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
        }
#endif
    }
}
