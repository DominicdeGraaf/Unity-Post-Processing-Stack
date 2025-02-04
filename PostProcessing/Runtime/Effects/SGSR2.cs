using System;
using UnityEngine.Experimental.Rendering;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if TND_SGSR2
using TND.SGSR2;
using static TND.SGSR2.SGSR2_UTILS;
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class SGSR2
    {
        [Tooltip("Fallback AA for when SGSR 2 is not supported")]
        public Antialiasing fallBackAA = Antialiasing.None;
        [Range(0, 1)]
        public float antiGhosting = 0.0f;

#if TND_SGSR2

        [Header("SGSR 2 Settings")]
        [Tooltip("Which variant to use, trading quality against speed.")]
        public SGSR2_Variant variant = SGSR2_Variant.TwoPassFragment;
        [Tooltip("Standard scaling ratio presets.")]
        public SGSR2_Quality qualityMode = SGSR2_Quality.Quality;

        [HideInInspector, Tooltip("Value by which the input signal will be divided, to get back to the original signal produced by the game.")]
        public float preExposure = 1.0f;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2;
        [Range(0, 1)]
        public float mipMapBiasOverride = 1f;

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

        private readonly SGSR2_Context _sgsr2Context = new();
        private SGSR2_Assets _assets;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private bool _resetHistory;

        private SGSR2_Variant _prevVariant;
        private SGSR2_Quality _prevQualityMode;
        private Vector2Int _prevDisplaySize;

        private Rect _originalRect;
        private Vector2 _jitterOffset;

        private PropertySheet _propertySheet;
        private readonly RenderTargetIdentifier[] _mrt = new RenderTargetIdentifier[2];

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
            DestroyContext();
        }

        internal void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            if (qualityMode == SGSR2_Quality.Off)
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

            float scaleRatio = GetScaling(qualityMode);
            _maxRenderSize = new Vector2Int(Mathf.RoundToInt(_displaySize.x / scaleRatio), Mathf.RoundToInt(_displaySize.y / scaleRatio));

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

            _originalRect = context.sgsr2._originalRect;
            _displaySize = new Vector2Int(context.sgsr2._displaySize.x, context.sgsr2._displaySize.y);

            qualityMode = context.sgsr2.qualityMode;

            float scaleRatio = GetScaling(qualityMode);
            _maxRenderSize = new Vector2Int(Mathf.RoundToInt(_displaySize.x / scaleRatio), Mathf.RoundToInt(_displaySize.y / scaleRatio));

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);
        }

        internal void ResetCameraViewport(PostProcessRenderContext context)
        {
            context.camera.rect = _originalRect;
        }

        internal void Render(PostProcessRenderContext context, bool stereoRendering = false)
        {
            var cmd = context.command;
            if (qualityMode == SGSR2_Quality.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }

            if (stereoRendering)
            {
                isStereoRendering = true;
                cmd.BeginSample("SGSR2 Right Eye");
            }
            else
            {
                cmd.BeginSample("SGSR2");
            }

            if (autoTextureUpdate && !isStereoRendering)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }

            // Monitor for any resolution changes and recreate the SGSR2 context if necessary
            // We can't create an SGSR2 context without info from the post-processing context, so delay the initial setup until here
            if (!_sgsr2Context.Initialized || _displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || variant != _prevVariant || qualityMode != _prevQualityMode)
            {
                DestroyContext();
                CreateContext(context);
                _mipMapTimer = Mathf.Infinity;
            }

            Vector2Int scaledRenderSize = GetScaledRenderSize(context.camera);
            _sgsr2Context.UpdateFrameData(cmd, context.camera, scaledRenderSize, _jitterOffset, preExposure, _resetHistory);

            switch (variant)
            {
                case SGSR2_Variant.TwoPassFragment:
                    RenderTwoPassFragment(context);
                    break;
                case SGSR2_Variant.TwoPassCompute:
                    RenderTwoPassCompute(context, scaledRenderSize);
                    break;
                case SGSR2_Variant.ThreePassCompute:
                    RenderThreePassCompute(context, scaledRenderSize);
                    break;
            }

            if (stereoRendering)
            {
                cmd.EndSample("SGSR2 Right Eye");
            }
            else
            {
                cmd.EndSample("SGSR2");
            }

            _resetHistory = false;
        }

        private void RenderTwoPassFragment(PostProcessRenderContext context)
        {
            var cmd = context.command;
            var constantBuffer = _sgsr2Context.ConstantBuffer;

            cmd.SetGlobalTexture(idInputColor, context.source);
            _propertySheet.properties.SetTexture(idMotionDepthClipAlphaBuffer, _sgsr2Context.MotionDepthClipAlpha);
            _propertySheet.properties.SetTexture(idPrevHistoryOutput, _sgsr2Context.PrevUpscaleHistory);
            _propertySheet.properties.SetConstantBuffer(idParams, constantBuffer, 0, constantBuffer.stride);

            // Convert pass
            cmd.BlitFullscreenTriangle(BuiltinRenderTextureType.None, _sgsr2Context.MotionDepthClipAlpha, _propertySheet, 0);

            // Upscale pass
            // We render to both the output destination and the history buffer at the same time, to save on an extra texture copy.
            _mrt[0] = context.destination;
            _mrt[1] = _sgsr2Context.NextUpscaleHistory;
            cmd.BlitFullscreenTriangle(BuiltinRenderTextureType.None, _mrt, BuiltinRenderTextureType.None, _propertySheet, 1);
        }

        private void RenderTwoPassCompute(PostProcessRenderContext context, in Vector2Int scaledRenderSize)
        {
            var cmd = context.command;
            var shader = _assets.shaders.twoPassCompute;
            var constantBuffer = _sgsr2Context.ConstantBuffer;

            const int threadGroupWorkRegionDim = 8;
            int dispatchSrcX = (scaledRenderSize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchSrcY = (scaledRenderSize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstX = (displaySize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstY = (displaySize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            // Convert pass
            {
                int kernelIndex = shader.FindKernel("CS_Convert");

                cmd.SetComputeConstantBufferParam(shader, idParams, constantBuffer, 0, constantBuffer.stride);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputColor, context.source);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputDepth, GetDepthTexture(context.camera), 0, RenderTextureSubElement.Depth);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputVelocity, BuiltinRenderTextureType.MotionVectors);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthClipAlphaBuffer, _sgsr2Context.MotionDepthClipAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idYCoCgColor, _sgsr2Context.ColorLuma);

                cmd.DispatchCompute(shader, kernelIndex, dispatchSrcX, dispatchSrcY, 1);
            }

            // Upscale pass
            {
                int kernelIndex = shader.FindKernel("CS_Upscale");

                cmd.SetComputeConstantBufferParam(shader, idParams, constantBuffer, 0, constantBuffer.stride);
                cmd.SetComputeTextureParam(shader, kernelIndex, idPrevHistoryOutput, _sgsr2Context.PrevUpscaleHistory);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthClipAlphaBuffer, _sgsr2Context.MotionDepthClipAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idYCoCgColor, _sgsr2Context.ColorLuma);
                cmd.SetComputeTextureParam(shader, kernelIndex, idSceneColorOutput, context.destination);
                cmd.SetComputeTextureParam(shader, kernelIndex, idHistoryOutput, _sgsr2Context.NextUpscaleHistory);

                cmd.DispatchCompute(shader, kernelIndex, dispatchDstX, dispatchDstY, 1);
            }
        }

        private void RenderThreePassCompute(PostProcessRenderContext context, in Vector2Int scaledRenderSize)
        {
            var cmd = context.command;
            var shader = _assets.shaders.threePassCompute;
            var constantBuffer = _sgsr2Context.ConstantBuffer;

            const int threadGroupWorkRegionDim = 8;
            int dispatchSrcX = (scaledRenderSize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchSrcY = (scaledRenderSize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstX = (displaySize.x + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;
            int dispatchDstY = (displaySize.y + (threadGroupWorkRegionDim - 1)) / threadGroupWorkRegionDim;

            // Convert pass
            {
                int kernelIndex = shader.FindKernel("CS_Convert");

                cmd.SetComputeConstantBufferParam(shader, idParams, constantBuffer, 0, constantBuffer.stride);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputOpaqueColor, colorOpaqueOnly);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputColor, context.source);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputDepth, GetDepthTexture(context.camera), 0, RenderTextureSubElement.Depth);
                cmd.SetComputeTextureParam(shader, kernelIndex, idInputVelocity, BuiltinRenderTextureType.MotionVectors);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthAlphaBuffer, _sgsr2Context.MotionDepthAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idYCoCgColor, _sgsr2Context.ColorLuma);

                cmd.DispatchCompute(shader, kernelIndex, dispatchSrcX, dispatchSrcY, 1);
            }

            // Activate pass
            {
                int kernelIndex = shader.FindKernel("CS_Activate");

                cmd.SetComputeConstantBufferParam(shader, idParams, constantBuffer, 0, constantBuffer.stride);
                cmd.SetComputeTextureParam(shader, kernelIndex, idPrevLumaHistory, _sgsr2Context.PrevLumaHistory);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthAlphaBuffer, _sgsr2Context.MotionDepthAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idYCoCgColor, _sgsr2Context.ColorLuma);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthClipAlphaBuffer, _sgsr2Context.MotionDepthClipAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idLumaHistory, _sgsr2Context.NextLumaHistory);

                cmd.DispatchCompute(shader, kernelIndex, dispatchSrcX, dispatchSrcY, 1);
            }

            // Upscale pass
            {
                int kernelIndex = shader.FindKernel("CS_Upscale");

                cmd.SetComputeConstantBufferParam(shader, idParams, constantBuffer, 0, constantBuffer.stride);
                cmd.SetComputeTextureParam(shader, kernelIndex, idPrevHistoryOutput, _sgsr2Context.PrevUpscaleHistory);
                cmd.SetComputeTextureParam(shader, kernelIndex, idMotionDepthClipAlphaBuffer, _sgsr2Context.MotionDepthClipAlpha);
                cmd.SetComputeTextureParam(shader, kernelIndex, idYCoCgColor, _sgsr2Context.ColorLuma);
                cmd.SetComputeTextureParam(shader, kernelIndex, idSceneColorOutput, context.destination);
                cmd.SetComputeTextureParam(shader, kernelIndex, idHistoryOutput, _sgsr2Context.NextUpscaleHistory);

                cmd.DispatchCompute(shader, kernelIndex, dispatchDstX, dispatchDstY, 1);
            }
        }

        private void CreateContext(PostProcessRenderContext context)
        {
            var sgsr2Component = context.camera.GetComponent<SGSR2_Base>();
            if (sgsr2Component != null && sgsr2Component.enabled)
            {
                sgsr2Component.enabled = false;
                Debug.LogWarning("[SGSR 2] Don't use the SGSR2_BIRP and Custom Post Processing stack at the same time!");
            }

            _prevVariant = variant;
            _prevQualityMode = qualityMode;
            _prevDisplaySize = _displaySize;

            // Initialize SGSR2 context
            if (_assets == null)
            {
                _assets = Resources.Load<SGSR2_Assets>("SGSR2/SGSR2_PPV2");
                _propertySheet = new PropertySheet(new Material(_assets.shaders.twoPassFragment));
            }

            switch (variant)
            {
                case SGSR2_Variant.TwoPassFragment:
                    _sgsr2Context.InitTwoPassFragment(_maxRenderSize, _displaySize, context.sourceFormat);
                    break;
                case SGSR2_Variant.TwoPassCompute:
                    _sgsr2Context.InitTwoPassCompute(_maxRenderSize, _displaySize, context.sourceFormat);
                    break;
                case SGSR2_Variant.ThreePassCompute:
                    _sgsr2Context.InitThreePassCompute(_maxRenderSize, _displaySize, context.sourceFormat);
                    break;
            }
        }

        private void DestroyContext()
        {
            _sgsr2Context.Destroy();

            if (_propertySheet != null)
            {
                _propertySheet.Release();
                _propertySheet = null;
            }

            if (_assets != null)
            {
                Resources.UnloadAsset(_assets);
                _assets = null;
            }

            MipMapUtils.OnResetAllMipMaps(ref _prevMipMapBias);
        }

        private void ApplyJitter(Camera camera, PostProcessRenderContext context)
        {
            var scaledRenderSize = GetScaledRenderSize(camera);

            // Perform custom jittering of the camera's projection matrix according to SGSR2's recipe
            int jitterPhaseCount = GetJitterPhaseCount(scaledRenderSize.x, _displaySize.x);
            GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);

            _jitterOffset = new Vector2(jitterX, jitterY);

            jitterX = 2.0f * jitterX / scaledRenderSize.x;
            jitterY = 2.0f * jitterY / scaledRenderSize.y;

            jitterX += Random.Range(-0.001f * antiGhosting, 0.001f * antiGhosting);
            jitterY += Random.Range(-0.001f * antiGhosting, 0.001f * antiGhosting);
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
        public void ConfigureJitteredProjectionMatrix(Camera camera, float jitterX, float jitterY)
        {
            var jitterTranslationMatrix = Matrix4x4.Translate(new Vector3(jitterX, jitterY, 0));
            var projectionMatrix = camera.projectionMatrix;
            camera.nonJitteredProjectionMatrix = projectionMatrix;
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

        private static float GetScaling(SGSR2_Quality quality)
        {
            switch (quality)
            {
                case SGSR2_Quality.Off:
                case SGSR2_Quality.NativeAA:
                    return 1.0f;
                case SGSR2_Quality.UltraQuality:
                    return 1.2f;
                case SGSR2_Quality.Quality:
                    return 1.5f;
                case SGSR2_Quality.Balanced:
                    return 1.7f;
                case SGSR2_Quality.Performance:
                    return 2.0f;
                case SGSR2_Quality.UltraPerformance:
                    return 3.0f;
                default:
                    Debug.LogError($"[SGSR 2 Upscaler]: Quality Level {quality} is not implemented, defaulting to Performance");
                    return 2.0f;
            }
        }

        private static BuiltinRenderTextureType GetDepthTexture(Camera cam)
        {
            RenderingPath renderingPath = cam.renderingPath;
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
