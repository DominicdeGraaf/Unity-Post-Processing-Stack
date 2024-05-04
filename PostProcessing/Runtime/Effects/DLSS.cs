using System;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if (TND_DLSS || AEG_DLSS) && UNITY_STANDALONE_WIN && UNITY_64
#if (TND_DLSS) 
using TND.DLSS;
#else
using AEG.DLSS;
#endif

using UnityEngine.NVIDIA;

#if (TND_DLSS) 
using static TND.DLSS.DLSS_UTILS;
#else
using static AEG.DLSS.DLSS_UTILS;
#endif

using NVIDIA = UnityEngine.NVIDIA;
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
        [Range(0, 1)] public float antiGhosting = 0.1f;

        [Header("MipMap Settings")]
        public bool AutoTextureUpdate = true;
        public float UpdateFrequency = 2;
        [Range(0, 1)]
        public float MipmapBiasOverride = 1f;

#if TND_DLSS || AEG_DLSS
#if UNITY_STANDALONE_WIN && UNITY_64


        public Vector2Int renderSize => _maxRenderSize;
        public Vector2Int displaySize => _displaySize;

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

        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private DLSS_Quality _prevQualityMode;
        private Vector2Int _prevDisplaySize;

        private Rect _originalRect;

        //Mipmap variables
        protected Texture[] m_allTextures;
        protected ulong m_previousLength;
        protected float m_mipMapBias;
        protected float m_prevMipMapBias;
        protected float m_mipMapTimer = float.MaxValue;

        /// <summary>
        /// Updates a single texture to the set MipMap Bias.
        /// Should be called when an object is instantiated, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipmapSingleTexture(Texture texture) {
            texture.mipMapBias = m_mipMapBias;
        }

        /// <summary>
        /// Updates all textures currently loaded to the set MipMap Bias.
        /// Should be called when a lot of new textures are loaded, or when the ScaleFactor is changed.
        /// </summary>
        public void OnMipMapAllTextures() {
#if(TND_DLSS)
            TND.DLSS.DLSS_UTILS.OnMipMapAllTextures(m_mipMapBias);
#else
            AEG.DLSS.DLSS_UTILS.OnMipMapAllTextures(m_mipMapBias);
#endif
        }

        /// <summary>
        /// Resets all currently loaded textures to the default mipmap bias. 
        /// </summary>
        public void OnResetAllMipMaps() {
#if(TND_DLSS)
            TND.DLSS.DLSS_UTILS.OnResetAllMipMaps();
#else
            AEG.DLSS.DLSS_UTILS.OnResetAllMipMaps();
#endif

        }

        public DepthTextureMode GetCameraFlags() {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        public void Release() {
            if(state != null) {
                if(cmd != null) {
                    state.Cleanup(cmd);
                }

                state = null;
                OnResetAllMipMaps();
            }
        }

        public void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context) {
            if(qualityMode == DLSS_Quality.Off) {
                return;
            }
            ApplyJitter(context.camera);
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context) {
            var camera = context.camera;
            _originalRect = camera.rect;

            // Determine the desired rendering and display resolutions
            _displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, qualityMode);
            _maxRenderSize = new Vector2Int(maxRenderWidth, maxRenderHeight);

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);
        }

        public void ResetCameraViewport(PostProcessRenderContext context) {
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
        public void Render(PostProcessRenderContext context) {

            cmd = context.command;
            if(qualityMode == DLSS_Quality.Off) {
                cmd.Blit(context.source, context.destination);
                return;
            }

            cmd.BeginSample("DLSS");

            if(state == null) {
                state = new ViewState(device);
            }

            // Monitor for any resolution changes and recreate the DLSS context if necessary
            // We can't create an DLSS context without info from the post-processing context, so delay the initial setup until here
            if(_displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode) {
                m_mipMapTimer = Mathf.Infinity;

                if(m_dlssOutput != null) {
                    m_dlssOutput.Release();
                    m_dlssInput.Release();
                }

                //var scaledRenderSize = GetScaledRenderSize(camera);

                float _upscaleRatio = GetUpscaleRatioFromQualityMode(qualityMode);
                m_renderWidth = Mathf.RoundToInt(_displaySize.x / _upscaleRatio);
                m_renderHeight = Mathf.RoundToInt(_displaySize.y / _upscaleRatio);

#if(TND_DLSS)
                dlssData.inputRes = new TND.DLSS.DLSS_UTILS.Resolution() { width = m_renderWidth, height = m_renderHeight };
                dlssData.outputRes = new TND.DLSS.DLSS_UTILS.Resolution() { width = _displaySize.x, height = _displaySize.y };
#else
                dlssData.inputRes = new AEG.DLSS.DLSS_UTILS.Resolution() { width = m_renderWidth, height = m_renderHeight };
                dlssData.outputRes = new AEG.DLSS.DLSS_UTILS.Resolution() { width = _displaySize.x, height = _displaySize.y };
#endif

                m_dlssInput = new RenderTexture(m_renderWidth, m_renderHeight, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                m_dlssInput.enableRandomWrite = false;
                m_dlssInput.Create();

                m_dlssOutput = new RenderTexture(_displaySize.x, _displaySize.y, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                m_dlssOutput.enableRandomWrite = true;
                m_dlssOutput.Create();

                _prevQualityMode = qualityMode;
                _prevDisplaySize = _displaySize;
            }

            if(AutoTextureUpdate) {
                UpdateMipMaps(context.camera.scaledPixelWidth, _displaySize.x);
            }

            if(m_depthBuffer == null) {
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
            cmd.EndSample("DLSS");
        }

        /// <summary>
        /// Automatically updates the mipmap of all loaded textures
        /// </summary>
        protected void UpdateMipMaps(float _renderWidth, float _displayWidth) {
            m_mipMapTimer += Time.deltaTime;

            if(m_mipMapTimer > UpdateFrequency) {
                m_mipMapTimer = 0;

                m_mipMapBias = (Mathf.Log(_renderWidth / _displayWidth, 2f) - 1) * MipmapBiasOverride;
                if(m_previousLength != Texture.currentTextureMemory || m_prevMipMapBias != m_mipMapBias) {
                    m_prevMipMapBias = m_mipMapBias;
                    m_previousLength = Texture.currentTextureMemory;
                    DLSS_UTILS.OnMipMapAllTextures(m_mipMapBias);
                }
            }
        }

        private void ApplyJitter(Camera camera) {
            var scaledRenderSize = GetScaledRenderSize(camera);

            var m_jitterMatrix = GetJitteredProjectionMatrix(camera.projectionMatrix, scaledRenderSize.x, scaledRenderSize.y, antiGhosting, camera);
            var m_projectionMatrix = camera.projectionMatrix;
            camera.nonJitteredProjectionMatrix = m_projectionMatrix;
            camera.projectionMatrix = m_jitterMatrix;
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        private Vector2Int GetScaledRenderSize(Camera camera) {
            if(!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
                return _maxRenderSize;

            return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
        }
#endif
#endif
    }
}
