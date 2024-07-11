using System;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if (TND_DLSS || AEG_DLSS) && UNITY_STANDALONE_WIN && UNITY_64
#if (TND_DLSS) 
using TND.DLSS;
#else
using AEG.DLSS;
#endif


#if (TND_DLSS) 
using static TND.DLSS.DLSS_UTILS;
#else
using static AEG.DLSS.DLSS_UTILS;
#endif

#else
public enum DLSS_Quality
{
    Off,
    DLAA,
    MaximumQuality,
    Balanced,
    MaximumPerformance,
    UltraPerformance,
}
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class DLSS
    {
        public Antialiasing fallBackAA = Antialiasing.None;
        public Vector2 jitter
        {
            get; private set;
        }

        public bool IsSupported() {
#if (TND_DLSS || AEG_DLSS)  && UNITY_STANDALONE_WIN && UNITY_64
            if(device == null) {
                return false;
            }
            return device.IsFeatureAvailable(NVIDIA.GraphicsDeviceFeature.DLSS);
#else
            return false;
#endif
        }

        [Header("DLSS Settings")]
        public DLSS_Quality qualityMode = DLSS_Quality.MaximumQuality;

        [Tooltip("Apply sharpening to the image during upscaling.")]
        public bool Sharpening = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.5f;


        [Range(0, 1)] public float antiGhosting = 0.1f;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2;
        [Range(0, 1)]
        public float mipMapBiasOverride = 1f;

#if TND_DLSS || AEG_DLSS
#if UNITY_STANDALONE_WIN && UNITY_64


        DlssViewData dlssData;
        ViewState state;

        private int m_cameraMotionVectorsTextureID = Shader.PropertyToID("_CameraMotionVectorsTexture");
        private int m_cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        public Texture m_motionVectorBuffer;
        public Texture m_depthBuffer;
        public RenderTexture m_colorBuffer;

        protected int m_renderWidth, m_renderHeight;


        private RenderTexture m_dlssInput;
        public RenderTexture m_dlssOutput;

        public Vector2Int renderSize;
        public Vector2Int displaySize;
        private DLSS_Quality _prevQualityMode;
        private Vector2Int _prevDisplaySize;

        private Rect _originalRect;

        //Mipmap variables
        protected ulong _previousLength;
        protected float _prevMipMapBias;
        protected float _mipMapTimer = float.MaxValue;

        private bool isStereoRendering = false;
        /// <summary>
        /// Updates a single texture to the set MipMap Bias.
        /// Should be called when an object is instantiated, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipmapSingleTexture(Texture texture) {
            MipMapUtils.OnMipMapSingleTexture(texture, renderSize.x, displaySize.x, mipMapBiasOverride);
        }

        /// <summary>
        /// Updates all textures currently loaded to the set MipMap Bias.
        /// Should be called when a lot of new textures are loaded, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipMapAllTextures() {
            MipMapUtils.OnMipMapAllTextures(renderSize.x, displaySize.x, mipMapBiasOverride);
        }

        /// <summary>
        /// Resets all currently loaded textures to the default mipmap bias. 
        /// </summary>
        public void OnResetAllMipMaps() {
            MipMapUtils.OnResetAllMipMaps();
        }

        internal DepthTextureMode GetCameraFlags() {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        internal void Release() {
            if(state != null) {
                if(cmd != null) {
                    state.Cleanup(cmd);
                }

                state = null;
                OnResetAllMipMaps();
            }
        }

        internal void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context) {
            if(qualityMode == DLSS_Quality.Off) {
                return;
            }
            ApplyJitter(context.camera, context);
        }

        internal void ConfigureCameraViewport(PostProcessRenderContext context) {
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
            displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, displaySize.x, displaySize.y, qualityMode);
            renderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (displaySize.x * _originalRect.width) / (displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * renderSize.x / displaySize.x, _originalRect.height * renderSize.y / displaySize.y);
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

            _originalRect = context.deepLearningSuperSampling._originalRect;
            displaySize = new Vector2Int(context.deepLearningSuperSampling.displaySize.x, context.deepLearningSuperSampling.displaySize.y);

            qualityMode = context.deepLearningSuperSampling.qualityMode;
            GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, displaySize.x, displaySize.y, qualityMode);
            renderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (displaySize.x * _originalRect.width) / (displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * renderSize.x / displaySize.x, _originalRect.height * renderSize.y / displaySize.y);
        }

        internal void ResetCameraViewport(PostProcessRenderContext context) {
            context.camera.rect = _originalRect;
        }

        static protected NVIDIA.GraphicsDevice _device;
        public NVIDIA.GraphicsDevice device
        {
            get
            {
                if(_device == null) {
                    SetupDevice();
                }

                return _device;
            }
        }

        protected void SetupDevice() {
            if(!NVIDIA.NVUnityPlugin.IsLoaded())
                return;

            if(!SystemInfo.graphicsDeviceVendor.ToLower().Contains("nvidia"))
                return;


            _device = NVIDIA.GraphicsDevice.CreateGraphicsDevice();
        }

        CommandBuffer cmd;
        internal void Render(PostProcessRenderContext context, bool _stereoRendering = false) {

            cmd = context.command;
            if(qualityMode == DLSS_Quality.Off) {
                Release();
                cmd.Blit(context.source, context.destination);
                return;
            }

            if (_stereoRendering)
            {
                isStereoRendering = _stereoRendering;
                cmd.BeginSample("DLSS Right Eye");
            }
            else
            {
                cmd.BeginSample("DLSS");
            }

            if (state == null) {
                state = new ViewState(device);
            }

            if (_stereoRendering)
            {
                dlssData.sharpening = context.deepLearningSuperSampling.Sharpening;
                dlssData.sharpness = context.deepLearningSuperSampling.sharpness;
            }
            else
            {
                dlssData.sharpening = Sharpening;
                dlssData.sharpness = sharpness;
            }

            // Monitor for any resolution changes and recreate the DLSS context if necessary
            // We can't create an DLSS context without info from the post-processing context, so delay the initial setup until here
            if (displaySize.x != _prevDisplaySize.x || displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode) {
                _mipMapTimer = Mathf.Infinity;

                if(m_dlssOutput != null) {
                    m_dlssOutput.Release();
                    m_dlssInput.Release();
                }

                //var scaledRenderSize = GetScaledRenderSize(camera);

                float _upscaleRatio = GetUpscaleRatioFromQualityMode(qualityMode);
                m_renderWidth = (int)(displaySize.x / _upscaleRatio);
                m_renderHeight = (int)(displaySize.y / _upscaleRatio);

#if(TND_DLSS)
                dlssData.inputRes = new TND.DLSS.DLSS_UTILS.Resolution() { width = m_renderWidth, height = m_renderHeight };
                dlssData.outputRes = new TND.DLSS.DLSS_UTILS.Resolution() { width = displaySize.x, height = displaySize.y };
#else
                dlssData.inputRes = new AEG.DLSS.DLSS_UTILS.Resolution() { width = m_renderWidth, height = m_renderHeight };
                dlssData.outputRes = new AEG.DLSS.DLSS_UTILS.Resolution() { width = _displaySize.x, height = _displaySize.y };
#endif

                m_dlssInput = new RenderTexture(m_renderWidth, m_renderHeight, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                m_dlssInput.enableRandomWrite = false;
                m_dlssInput.Create();

                m_dlssOutput = new RenderTexture(displaySize.x, displaySize.y, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                m_dlssOutput.enableRandomWrite = true;
                m_dlssOutput.Create();

                _prevQualityMode = qualityMode;
                _prevDisplaySize = displaySize;
            }

            if (autoTextureUpdate && !_stereoRendering)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }

            if (m_depthBuffer == null) {
                m_depthBuffer = Shader.GetGlobalTexture(m_cameraDepthTextureID);
            }
            if(m_motionVectorBuffer == null) {
                m_motionVectorBuffer = Shader.GetGlobalTexture(m_cameraMotionVectorsTextureID);
            }

            if(m_dlssInput != null && m_depthBuffer != null && m_motionVectorBuffer != null) {
                cmd.Blit(context.source, m_dlssInput);
                UpdateDlssSettings(ref dlssData, state, qualityMode, device);
                state.CreateContext(dlssData, cmd);
                state.UpdateDispatch(m_dlssInput, m_depthBuffer, m_motionVectorBuffer, null, m_dlssOutput, cmd);
                cmd.Blit(m_dlssOutput, context.destination);
            } else {
                cmd.Blit(context.source, context.destination);
            }

            if (_stereoRendering)
            {
                cmd.EndSample("DLSS Right Eye");
            }
            else
            {
                cmd.EndSample("DLSS");
            }
        }

        private void ApplyJitter(Camera camera, PostProcessRenderContext context) {

            if(camera.stereoEnabled)
            {
                // We only need to configure all of this once for stereo, during OnPreCull
                if(camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right)
                    ConfigureStereoJitteredProjectionMatrices(context, camera);
            }
            else
            {
                ConfigureJitteredProjectionMatrixNoStereo(camera);
            }
        }

        /// <summary>
        /// Prepares the jittered and non jittered projection matrices.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        public void ConfigureJitteredProjectionMatrixNoStereo(Camera camera)
        {
            var m_projectionMatrix = camera.projectionMatrix;
            var scaledRenderSize = GetScaledRenderSize(camera);
            var m_jitterMatrix = GetJitteredProjectionMatrix(m_projectionMatrix, scaledRenderSize.x, scaledRenderSize.y, antiGhosting, camera);
   
            camera.nonJitteredProjectionMatrix = m_projectionMatrix;
            camera.projectionMatrix = m_jitterMatrix;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        /// <summary>
        /// Prepares the jittered and non jittered projection matrices for stereo rendering.
        /// </summary>
        /// <param name="context">The current post-processing context.</param>
        // TODO: We'll probably need to isolate most of this for SRPs
        public void ConfigureStereoJitteredProjectionMatrices(PostProcessRenderContext context, Camera camera)
        {
#if UNITY_2017_3_OR_NEWER
            for(var eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
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
#endif
        }

        private Vector2Int GetScaledRenderSize(Camera camera) {
            if(!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
                return renderSize;

            return new Vector2Int(Mathf.CeilToInt(renderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(renderSize.y * ScalableBufferManager.heightScaleFactor));
        }
#endif
#endif
    }
}
