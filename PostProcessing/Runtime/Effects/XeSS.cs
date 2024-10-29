using System;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if TND_XeSS
using TND.XeSS;
#else
public enum XeSS_Quality
{
    Off,
    NativeAntiAliasing,
    Quality,
    Balanced,
    Performance,
    UltraPerformance
}
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [Scripting.Preserve]
    [Serializable]
    public class XeSS
    {

        public Antialiasing fallBackAA = Antialiasing.None;
#if TND_XeSS
        [Header("XeSS Settings")]
        public XeSS_Quality qualityMode = XeSS_Quality.Quality;
        [Range(0, 1)]
        public float antiGhosting;
        public bool sharpening = true;
        [Range(0f, 1f)]
        public float sharpness = 0.5f;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2.0f;
        [Range(0.0f, 1.0f)]
        public float mipMapBiasOverride = 1.0f;

        public Vector2Int displaySize;
        public Vector2Int renderSize;

        public float jitterX, jitterY;

        private XeSS_Quality _prevQuality = XeSS_Quality.Off;
        private IntelQuality _intelQuality;
        private Vector2Int _prevDisplayResolution;

        private float _scaleFactor;
        private int _frameIndex = 0;

        private Rect _originalRect;
        private bool _initializeContext;

        private RenderTexture _xessInput;
        private Texture _motionVectorBuffer;
        private Texture _depthBuffer;
        private RenderTexture _xessOutput;
        private RenderTexture _sharpeningOutput;

        private readonly int _idMotionVectorTexture = Shader.PropertyToID("_CameraMotionVectorsTexture");
        private readonly int _idDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        private readonly int _idSharpness = Shader.PropertyToID("Sharpness");

        private ulong _previousLength;
        private float _prevMipMapBias;
        private float _mipMapTimer = float.MaxValue;

        private bool _supportedChecked = false;
        private bool _supported = false;


        private Material _sharpeningMaterial;

        private GraphicsDevice _graphicsDevice;
        private GraphicsDevice GraphicsDevice
        {
            get
            {
                if (_graphicsDevice == null)
                {
                    _graphicsDevice = new GraphicsDevice();
                }

                return _graphicsDevice;
            }
        }

        public bool IsSupported()
        {
#if TND_XeSS
            if (!_supportedChecked)
            {
                _supportedChecked = true;
                _supported = GraphicsDevice.CreateXeSSContext() >= 0;
            }
#endif
            return _supported;
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

        internal void ConfigureCameraViewport(PostProcessRenderContext context)
        {
            var camera = context.camera; 
            _originalRect = camera.rect;

            displaySize = new Vector2Int(context.width, context.height);
            if (displaySize != _prevDisplayResolution || qualityMode != _prevQuality)
            {
                GraphicsDevice.CreateXeSSContext();

                _prevDisplayResolution = displaySize;
                _prevQuality = qualityMode;
                _initializeContext = true;

                (_scaleFactor, _intelQuality) = XeSS_Base.GetScalingFromQualityMode(qualityMode);

                renderSize = new Vector2Int((int)(displaySize.x / _scaleFactor), (int)(displaySize.y / _scaleFactor));

                if (qualityMode == XeSS_Quality.Off)
                {
                    ReleaseResources();
                    return;
                }

                if (_xessInput != null)
                {
                    _xessInput.Release();
                    _xessOutput.Release();
                    _sharpeningOutput.Release();
                }

                _xessInput = new RenderTexture(renderSize.x, renderSize.y, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                _xessInput.enableRandomWrite = false;
                _xessInput.Create();

                _xessOutput = new RenderTexture(displaySize.x, displaySize.y, 0, context.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
                _xessOutput.enableRandomWrite = true;
                _xessOutput.Create();

                _sharpeningOutput = new RenderTexture(_xessOutput);
                _sharpeningOutput.Create();
            }

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (displaySize.x * _originalRect.width) / (displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * renderSize.x / displaySize.x, _originalRect.height * renderSize.y / displaySize.y);

        }

        internal void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            if (qualityMode == XeSS_Quality.Off)
            {
                return;
            }

            Camera camera = context.camera;

            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
            camera.projectionMatrix = XeSS_Base.GetJitteredProjectionMatrix(camera.projectionMatrix, renderSize.x, renderSize.y, antiGhosting, _scaleFactor, ref jitterX, ref jitterY, ref _frameIndex);
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        internal void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;

            if (qualityMode == XeSS_Quality.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }

            if (autoTextureUpdate)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }

            cmd.BeginSample("XeSS");

            if (_initializeContext)
            {
                _initializeContext = false;

                var initParam = new XeSSInitParam()
                {
                    resolution = displaySize,
                    qualitySetting = GraphicsDevice.QualityModeToInitSetting(_intelQuality),
                    flags = XeSSInitFlags.XESS_INIT_FLAG_INVERTED_DEPTH | XeSSInitFlags.XESS_INIT_FLAG_USE_NDC_VELOCITY,
                    jitterScaleX = 1f,
                    jitterScaleY = 1f,
                    motionVectorScaleX = -2f,
                    motionVectorScaleY = 2f,
                };

                GraphicsDevice.InitXeSS(cmd, initParam);
            }

            if (_depthBuffer == null)
            {
                _depthBuffer = Shader.GetGlobalTexture(_idDepthTexture);
            }
            if (_motionVectorBuffer == null)
            {
                _motionVectorBuffer = Shader.GetGlobalTexture(_idMotionVectorTexture);
            }
          
            if (_xessInput != null && _depthBuffer != null && _motionVectorBuffer != null)
            {
                cmd.Blit(context.source, _xessInput);
                var executeParam = new XeSSExecuteParam()
                {
                    colorInput = _xessInput.GetNativeTexturePtr(),
                    depth = _depthBuffer ? _depthBuffer.GetNativeTexturePtr() : IntPtr.Zero,
                    motionVectors = _motionVectorBuffer ? _motionVectorBuffer.GetNativeTexturePtr() : IntPtr.Zero,
                    output = _xessOutput.GetNativeTexturePtr(),
                    inputWidth = (uint)renderSize.x,
                    inputHeight = (uint)renderSize.y,
                    jitterOffsetX = -jitterX,
                    jitterOffsetY = jitterY,
                    exposureScale = 0.001f,
                    resetHistory = false,
                };
                GraphicsDevice.ExecuteXeSS(cmd, executeParam);

                if (sharpening)
                {
                    if (_sharpeningMaterial == null)
                    {
                        _sharpeningMaterial = new Material(Shader.Find("Hidden/Rcas_BIRP"));
                    }

                    _sharpeningMaterial.SetFloat(_idSharpness, Mathf.Clamp01(1 - sharpness));
                    cmd.Blit(_xessOutput, context.destination, _sharpeningMaterial, 0);
                }
                else
                {
                    cmd.Blit(_xessOutput, context.destination);
                }
            }
            else
            {
                cmd.Blit(context.source, context.destination);
            }

            cmd.EndSample("XeSS");
        }

        internal void ResetCameraViewport(PostProcessRenderContext context)
        {
            context.camera.rect = _originalRect;
        }

        internal DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        private void ReleaseResources()
        {
            GraphicsDevice.ReleaseResources();

            if (_xessInput != null)
            {
                _xessInput.Release();
                _xessOutput.Release();
                _sharpeningOutput.Release();
                _xessInput = null;
                _xessOutput = null;
                _sharpeningOutput = null;
            }

            OnResetAllMipMaps();
        }

        internal void Release()
        {
            ReleaseResources();

            //Reset previous display resolution to trigger reinitialization on reactivation
            _prevDisplayResolution = Vector2Int.zero;
        }
#endif
    }
}
