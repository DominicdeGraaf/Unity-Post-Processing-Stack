using System;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if TND_XeSS
using TND.XeSS;
#else
public enum XeSSQuality
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
        public XeSSQuality qualityMode;
        [Range(0, 1)]
        public float antiGhosting;
        public bool sharpening = true;
        [Range(0f, 1f)]
        public float sharpness = 0.5f;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2.0f;
        [Range(0.0f, 1.0f)]
        public float mipmapBiasOverride = 1.0f;

        public Vector2Int displaySize;
        public Vector2Int renderSize;

        public float jitterX, jitterY;

        private XeSSQuality _prevQuality;
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

        private int _idMotionVectorTexture = Shader.PropertyToID("_CameraMotionVectorsTexture");
        private int _idDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

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
            return GraphicsDevice.CreateXeSSContext() >= 0;
#else
            return false;
#endif
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context)
        {
            var camera = context.camera;
            _originalRect = camera.pixelRect;

            displaySize = new Vector2Int(context.width, context.height);
            if (displaySize != _prevDisplayResolution || qualityMode != _prevQuality)
            {
                _prevDisplayResolution = displaySize;
                _prevQuality = qualityMode;
                _initializeContext = true;

                (_scaleFactor, _intelQuality) = XeSS_Base.GetScalingFromQualityMode(qualityMode);

                renderSize = new Vector2Int((int)(displaySize.x / _scaleFactor), (int)(displaySize.y / _scaleFactor));

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
            camera.pixelRect = new Rect(0, 0, renderSize.x, renderSize.y);
        }

        public void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            if (qualityMode == XeSSQuality.Off)
            {
                return;
            }

            Camera camera = context.camera;
            
            camera.nonJitteredProjectionMatrix = camera.projectionMatrix;
            camera.projectionMatrix = XeSS_Base.GetJitteredProjectionMatrix(camera.projectionMatrix, renderSize.x, renderSize.y, antiGhosting, _scaleFactor, ref jitterX, ref jitterY, ref _frameIndex);
            camera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        public void Render(PostProcessRenderContext context)
        {
            var cmd = context.command;
            if (qualityMode == XeSSQuality.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }

            cmd.BeginSample("XeSS");

            if (_initializeContext)
            {
                _initializeContext = false;

                var initParam = new XeSSInitParam()
                {
                    resolution = displaySize,
                    qualitySetting = GraphicsDevice.QualityModeToInitSetting(_intelQuality),
                    flags = XeSSInitFlags.XESS_INIT_FLAG_INVERTED_DEPTH | XeSSInitFlags.XESS_INIT_FLAG_ENABLE_AUTOEXPOSURE | XeSSInitFlags.XESS_INIT_FLAG_USE_NDC_VELOCITY,
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

            if (_xessInput != null)
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
                    exposureScale = 1.0f,
                    resetHistory = false,
                };
                GraphicsDevice.ExecuteXeSS(cmd, executeParam);

                if (sharpening)
                {
                    //cmd.Blit(_xessOutput, _sharpeningOutput, _sharpeningMaterial, 0);
                    //cmd.Blit(_sharpeningOutput, context.destination);
                    cmd.Blit(_xessOutput, context.destination);
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

        public void ResetCameraViewport(PostProcessRenderContext context)
        {
            context.camera.pixelRect = _originalRect;
        }

        public DepthTextureMode GetCameraFlags()
        {
            return DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        }

        public void ReleaseResources()
        {
            if (_xessInput != null)
            {
                _xessInput.Release();
                _xessOutput.Release();
                _sharpeningOutput.Release();
                _xessInput = null;
                _xessOutput = null;
                _sharpeningOutput = null;
            }

            //Reset previous display resolution to trigger reinitialization on reactivation
            _prevDisplayResolution = Vector2Int.zero;
        }
#endif
    }
}
