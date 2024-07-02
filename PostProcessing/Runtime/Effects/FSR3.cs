// Copyright (c) 2023 Nico de Poel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;
#if TND_FSR3 || AEG_FSR3
using FidelityFX;
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class FSR3
    {
        [Tooltip("Fallback AA for when FSR 3 is not supported")]
        public Antialiasing fallBackAA = Antialiasing.None;
        [Range(0, 1)]
        public float antiGhosting = 0.0f;
#if TND_FSR3 || AEG_FSR3
        public Func<PostProcessRenderContext, IFsr3Callbacks> callbacksFactory { get; set; } = (context) => new Callbacks(context.resources);

        [Tooltip("Standard scaling ratio presets.")]

        [Header("FSR 3 Settings")]
        public Fsr3.QualityMode qualityMode = Fsr3.QualityMode.Quality;


        [Tooltip("Apply RCAS sharpening to the image after upscaling.")]
        public bool Sharpening = true;
        [Tooltip("Strength of the sharpening effect.")]
        [Range(0, 1)] public float sharpness = 0.5f;

        [HideInInspector, Tooltip("Allow the use of half precision compute operations, potentially improving performance if the platform supports it.")]
        public bool enableFP16 = false;

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
        public Fsr3.GenerateReactiveFlags flags = Fsr3.GenerateReactiveFlags.ApplyTonemap | Fsr3.GenerateReactiveFlags.ApplyThreshold | Fsr3.GenerateReactiveFlags.UseComponentsMax;

        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2;
        [Range(0, 1)]
        public float mipMapBiasOverride = 1f;

        [HideInInspector, Tooltip("Optional texture to control the influence of the current frame on the reconstructed output. If unset, either an auto-generated or a default cleared reactive mask will be used.")]
        public Texture reactiveMask = null;
        [HideInInspector, Tooltip("Optional texture for marking areas of specialist rendering which should be accounted for during the upscaling process. If unset, a default cleared mask will be used.")]
        public Texture transparencyAndCompositionMask = null;

        [HideInInspector, Tooltip("Choose where to get the exposure value from. Use auto-exposure from either FSR or Unity, provide a manual exposure texture, or use a default value.")]
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

        private Fsr3Context _fsrContext;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;
        private bool _resetHistory;

        private IFsr3Callbacks _callbacks;

        private readonly Fsr3.DispatchDescription _dispatchDescription = new Fsr3.DispatchDescription();
        private readonly Fsr3.GenerateReactiveDescription _genReactiveDescription = new Fsr3.GenerateReactiveDescription();

        private Fsr3.QualityMode _prevQualityMode;
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
            MipMapUtils.OnResetAllMipMaps();
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
            DestroyFsrContext();
        }

        internal void ConfigureJitteredProjectionMatrix(PostProcessRenderContext context)
        {
            if (qualityMode == Fsr3.QualityMode.Off)
            {
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
            Fsr3.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, qualityMode);
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

            _originalRect = context.superResolution3._originalRect;
            _displaySize = new Vector2Int(context.superResolution3._displaySize.x, context.superResolution3._displaySize.y);

            qualityMode = context.superResolution3.qualityMode;
            Fsr3.GetRenderResolutionFromQualityMode(out int maxRenderWidth, out int maxRenderHeight, _displaySize.x, _displaySize.y, qualityMode);
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
            if (qualityMode == Fsr3.QualityMode.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }

            if (autoTextureUpdate)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }

            if (_stereoRendering)
            {
                isStereoRendering = _stereoRendering;
                cmd.BeginSample("FSR3 Right Eye");
            }
            else
            {
                cmd.BeginSample("FSR3");
            }

            // Monitor for any resolution changes and recreate the FSR3 context if necessary
            // We can't create an FSR3 context without info from the post-processing context, so delay the initial setup until here
            if (_fsrContext == null || _displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode || exposureSource != _prevExposureSource)
            {
                DestroyFsrContext();
                CreateFsrContext(context);
                _mipMapTimer = Mathf.Infinity;
            }

            cmd.SetGlobalTexture(Fsr3ShaderIDs.SrvInputColor, context.source);
            cmd.SetGlobalTexture(Fsr3ShaderIDs.SrvInputDepth, BuiltinRenderTextureType.CameraTarget, RenderTextureSubElement.Depth);
            cmd.SetGlobalTexture(Fsr3ShaderIDs.SrvInputMotionVectors, BuiltinRenderTextureType.MotionVectors);

            SetupDispatchDescription(context);

            if (autoGenerateReactiveMask)
            {
                SetupAutoReactiveDescription(context);

                var scaledRenderSize = _genReactiveDescription.RenderSize;
                cmd.GetTemporaryRT(Fsr3ShaderIDs.UavAutoReactive, scaledRenderSize.x, scaledRenderSize.y, 0, default, GraphicsFormat.R8_UNorm, 1, true);
                _fsrContext.GenerateReactiveMask(_genReactiveDescription, cmd);
                _dispatchDescription.Reactive = Fsr3ShaderIDs.UavAutoReactive;
            }

            _fsrContext.Dispatch(_dispatchDescription, cmd);

            if (_stereoRendering)
            {
                cmd.EndSample("FSR3 Right Eye");
            }
            else
            {
                cmd.EndSample("FSR3");
            }

            _resetHistory = false;
        }

        private void CreateFsrContext(PostProcessRenderContext context)
        {
            _prevQualityMode = qualityMode;
            _prevExposureSource = exposureSource;
            _prevDisplaySize = _displaySize;

            enableFP16 = SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat, UnityEngine.Experimental.Rendering.FormatUsage.Render);

            // Initialize FSR3 context
            Fsr3.InitializationFlags flags = 0;
            if (context.camera.allowHDR)
                flags |= Fsr3.InitializationFlags.EnableHighDynamicRange;
            if (enableFP16)
                flags |= Fsr3.InitializationFlags.EnableFP16Usage;
            if (exposureSource == ExposureSource.Auto)
                flags |= Fsr3.InitializationFlags.EnableAutoExposure;
            if (RuntimeUtilities.IsDynamicResolutionEnabled(context.camera))
                flags |= Fsr3.InitializationFlags.EnableDynamicResolution;
            if (context.camera.stereoEnabled)
                flags |= Fsr3.InitializationFlags.EnableDisplayResolutionMotionVectors;

            _callbacks = callbacksFactory(context);
            _fsrContext = Fsr3.CreateContext(_displaySize, _maxRenderSize, _callbacks, flags);
        }

        private void DestroyFsrContext()
        {
            if (_fsrContext != null)
            {
                _fsrContext.Destroy();
                _fsrContext = null;
            }

            MipMapUtils.OnResetAllMipMaps();
        }

        private void ApplyJitter(Camera camera, PostProcessRenderContext context)
        {
            var scaledRenderSize = GetScaledRenderSize(camera);

            // Perform custom jittering of the camera's projection matrix according to FSR3's recipe
            int jitterPhaseCount = Fsr3.GetJitterPhaseCount(scaledRenderSize.x, _displaySize.x);
            Fsr3.GetJitterOffset(out float jitterX, out float jitterY, Time.frameCount, jitterPhaseCount);

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
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
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
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
        }

        private void SetupDispatchDescription(PostProcessRenderContext context)
        {
            var camera = context.camera;

            // Set up the main FSR3 dispatch parameters
            // The input textures are left blank here, as they get bound directly through SetGlobalTexture elsewhere in this source file
            _dispatchDescription.Color = null;
            _dispatchDescription.Depth = null;
            _dispatchDescription.MotionVectors = null;
            _dispatchDescription.Exposure = null;
            _dispatchDescription.Reactive = null;
            _dispatchDescription.TransparencyAndComposition = null;

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

            if (SystemInfo.usesReversedZBuffer)
            {
                // Swap the near and far clip plane distances as FSR3 expects this when using inverted depth
                (_dispatchDescription.CameraNear, _dispatchDescription.CameraFar) = (_dispatchDescription.CameraFar, _dispatchDescription.CameraNear);
            }

#if UNITY_EDITOR
            //TND sharpening shader breaks the output in editor, but it works fine in a build!
            if (context.camera.stereoEnabled)
            {
                Sharpening = false;
            }
#endif

            if (!isStereoRendering)
            {
                if (exposureSource == ExposureSource.Manual && exposure != null)
                    _dispatchDescription.Exposure = exposure;
                if (exposureSource == ExposureSource.Unity)
                    _dispatchDescription.Exposure = context.autoExposureTexture;
                if (reactiveMask != null)
                    _dispatchDescription.Reactive = reactiveMask;
                if (transparencyAndCompositionMask != null)
                    _dispatchDescription.TransparencyAndComposition = transparencyAndCompositionMask;

                _dispatchDescription.Output = context.destination;
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
                _dispatchDescription.EnableAutoReactive = autoGenerateTransparencyAndComposition;
                if (autoGenerateTransparencyAndComposition)
                {
                    _dispatchDescription.ColorOpaqueOnly = colorOpaqueOnly;
                    _dispatchDescription.AutoTcThreshold = generateTransparencyAndCompositionParameters.autoTcThreshold;
                    _dispatchDescription.AutoTcScale = generateTransparencyAndCompositionParameters.autoTcScale;
                    _dispatchDescription.AutoReactiveScale = generateTransparencyAndCompositionParameters.autoReactiveScale;
                    _dispatchDescription.AutoReactiveMax = generateTransparencyAndCompositionParameters.autoReactiveMax;
                }
            }
            else
            {
                if (exposureSource == ExposureSource.Manual && context.superResolution3.exposure != null)
                    _dispatchDescription.Exposure = context.superResolution3.exposure;
                if (exposureSource == ExposureSource.Unity)
                    _dispatchDescription.Exposure = context.autoExposureTexture;
                if (reactiveMask != null)
                    _dispatchDescription.Reactive = context.superResolution3.reactiveMask;
                if (transparencyAndCompositionMask != null)
                    _dispatchDescription.TransparencyAndComposition = context.superResolution3.transparencyAndCompositionMask;

                _dispatchDescription.Output = context.destination;
                _dispatchDescription.PreExposure = context.superResolution3.preExposure;
                _dispatchDescription.EnableSharpening = context.superResolution3.Sharpening;

                _dispatchDescription.Sharpness = context.superResolution3.sharpness;

                _dispatchDescription.RenderSize = scaledRenderSize;
                _dispatchDescription.InputResourceSize = scaledRenderSize;
                _dispatchDescription.FrameTimeDelta = Time.unscaledDeltaTime;
                _dispatchDescription.CameraNear = camera.nearClipPlane;
                _dispatchDescription.CameraFar = camera.farClipPlane;
                _dispatchDescription.CameraFovAngleVertical = camera.fieldOfView * Mathf.Deg2Rad;
                _dispatchDescription.ViewSpaceToMetersFactor = 1.0f; // 1 unit is 1 meter in Unity
                _dispatchDescription.Reset = context.superResolution3._resetHistory;

                autoGenerateReactiveMask = context.superResolution3.autoGenerateReactiveMask;

                // Set up the parameters for the optional experimental auto-TCR feature
                _dispatchDescription.EnableAutoReactive = context.superResolution3.autoGenerateTransparencyAndComposition;
                if (context.superResolution3.autoGenerateTransparencyAndComposition)
                {
                    _dispatchDescription.ColorOpaqueOnly = colorOpaqueOnly;
                    _dispatchDescription.AutoTcThreshold = context.superResolution3.generateTransparencyAndCompositionParameters.autoTcThreshold;
                    _dispatchDescription.AutoTcScale = context.superResolution3.generateTransparencyAndCompositionParameters.autoTcScale;
                    _dispatchDescription.AutoReactiveScale = context.superResolution3.generateTransparencyAndCompositionParameters.autoReactiveScale;
                    _dispatchDescription.AutoReactiveMax = context.superResolution3.generateTransparencyAndCompositionParameters.autoReactiveMax;
                }
            }
        }

        private void SetupAutoReactiveDescription(PostProcessRenderContext context)
        {
            // Set up the parameters to auto-generate a reactive mask
            _genReactiveDescription.ColorOpaqueOnly = colorOpaqueOnly;
            _genReactiveDescription.ColorPreUpscale = null;
            _genReactiveDescription.OutReactive = null;
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
                _genReactiveDescription.Scale = context.superResolution3.ReactiveScale;
                _genReactiveDescription.CutoffThreshold = context.superResolution3.ReactiveThreshold;
                _genReactiveDescription.BinaryValue = context.superResolution3.ReactiveBinaryValue;
                _genReactiveDescription.Flags = context.superResolution3.flags;
            }
        }

        internal Vector2Int GetScaledRenderSize(Camera camera)
        {
            if (!RuntimeUtilities.IsDynamicResolutionEnabled(camera))
                return _maxRenderSize;

            return new Vector2Int(Mathf.CeilToInt(_maxRenderSize.x * ScalableBufferManager.widthScaleFactor), Mathf.CeilToInt(_maxRenderSize.y * ScalableBufferManager.heightScaleFactor));
        }

        private class Callbacks : Fsr3CallbacksBase
        {
            private readonly PostProcessResources _resources;

            public Callbacks(PostProcessResources resources)
            {
                _resources = resources;
            }

            public override ComputeShader LoadComputeShader(string name)
            {
                return Resources.Load<ComputeShader>(name);
            }

            public override void UnloadComputeShader(ComputeShader shader)
            {
            }
        }
#endif
    }
}
