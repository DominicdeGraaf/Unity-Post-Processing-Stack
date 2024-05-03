using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;
using System;

#if TND_FSR1 && TND_FSR3 || AEG_FSR1 && AEG_FSR3
using FidelityFX;
#endif



namespace UnityEngine.Rendering.PostProcessing
{
#if TND_FSR1 || AEG_FSR1
    namespace FSR
    {
        public enum QualityMode
        {
            Quality,
            Balanced,
            Performance,
            UltraPerformance
        }
    }
#endif

    [UnityEngine.Scripting.Preserve]
    [Serializable]
    public class FSR1
    {
        [Tooltip("Fallback AA for when FSR 3 is not supported")]
        public Antialiasing fallBackAA = Antialiasing.None;

#if TND_FSR1 || AEG_FSR1

        [Header("FSR Compute Shaders")]
        public ComputeShader computeShaderEASU;
        public ComputeShader computeShaderRCAS;

        [Header("FSR 1 Settings")]
        public FSR.QualityMode qualityMode = FSR.QualityMode.Quality;
        public float scaleFactor = new FloatParameter { value = 1.3f };
        public bool Sharpening = new BoolParameter { value = true };

        [Range(0f, 1f), Tooltip("0 = sharpest, 2 = less sharp")]
        public float sharpness = new FloatParameter { value = 0.2f };

        // Robust Contrast Adaptive Sharpening
        private static readonly int _RCASScale = Shader.PropertyToID("_RCASScale");
        private static readonly int _RCASParameters = Shader.PropertyToID("_RCASParameters");

        // Edge Adaptive Spatial Upsampling
        private static readonly int _EASUViewportSize = Shader.PropertyToID("_EASUViewportSize");
        private static readonly int _EASUInputImageSize = Shader.PropertyToID("_EASUInputImageSize");
        private static readonly int _EASUOutputSize = Shader.PropertyToID("_EASUOutputSize");
        private static readonly int _EASUParameters = Shader.PropertyToID("_EASUParameters");

        private static readonly int InputTexture = Shader.PropertyToID("InputTexture");
        private static readonly int OutputTexture = Shader.PropertyToID("OutputTexture");

        public RenderTexture outputImage, outputImage2;

        private ComputeBuffer EASUParametersCB, RCASParametersCB;

        public Vector2Int renderSize => _maxRenderSize;
        public Vector2Int displaySize => _displaySize;
        private Vector2Int _maxRenderSize;
        private Vector2Int _displaySize;

        //MAYBE
        public Func<PostProcessRenderContext, IFsr3Callbacks> callbacksFactory { get; set; } = (context) => new Callbacks();
        private IFsr3Callbacks _callbacks;

        private FSR.QualityMode _prevQualityMode;
        private Vector2Int _prevDisplaySize;
        private bool _prevSharpening;

        private Rect _originalRect;


        [Header("MipMap Settings")]
        public bool AutoTextureUpdate = true;
        public float UpdateFrequency = 2;
        [Range(0, 1)]
        public float MipmapBiasOverride = 1f;

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
            _callbacks.OnMipMapAllTextures(m_mipMapBias);
        }
        /// <summary>
        /// Resets all currently loaded textures to the default mipmap bias. 
        /// </summary>
        public void OnResetAllMipMaps() {
            _callbacks.OnResetAllMipMaps();
        }

        public bool IsSupported() {
            return SystemInfo.supportsComputeShaders;
        }

        public void Release() {
            computeShaderEASU = null;
            computeShaderRCAS = null;
            if(outputImage) {
                outputImage.Release();
                outputImage = null;
            }

            if(EASUParametersCB != null) {
                EASUParametersCB.Dispose();
                EASUParametersCB = null;
            }

            if(outputImage2) {
                outputImage2.Release();
                outputImage2 = null;
            }

            if(RCASParametersCB != null) {
                RCASParametersCB.Dispose();
                RCASParametersCB = null;
            }

            if(_callbacks != null) {
                // Undo the current mipmap bias offset
                _callbacks.OnResetAllMipMaps();
                _callbacks = null;
            }
        }

        public void Init() {
            _callbacks = callbacksFactory(null);

            computeShaderEASU = (ComputeShader)Resources.Load("FSR1/EdgeAdaptiveScaleUpsampling");
            computeShaderRCAS = (ComputeShader)Resources.Load("FSR1/RobustContrastAdaptiveSharpen");

            EASUParametersCB = new ComputeBuffer(4, sizeof(uint) * 4);
            EASUParametersCB.name = "EASU Parameters";

            RCASParametersCB = new ComputeBuffer(1, sizeof(uint) * 4);
            RCASParametersCB.name = "RCAS Parameters";
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context) {
            var camera = context.camera;
            _originalRect = camera.rect;

            // Determine the desired rendering and display resolutions
            _displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            _maxRenderSize = new Vector2Int((int)(_displaySize.x / scaleFactor), (int)(_displaySize.y / scaleFactor));

            // Render to a smaller portion of the screen by manipulating the camera's viewport rect
            camera.aspect = (_displaySize.x * _originalRect.width) / (_displaySize.y * _originalRect.height);
            camera.rect = new Rect(0, 0, _originalRect.width * _maxRenderSize.x / _displaySize.x, _originalRect.height * _maxRenderSize.y / _displaySize.y);

        }

        public void ResetCameraViewport(PostProcessRenderContext context) {
            context.camera.rect = _originalRect;
        }

        public void Render(PostProcessRenderContext context) {
            //FSR1
            ResolveFSR1(context);
        }

        private void ResolveFSR1(PostProcessRenderContext context) {

            if(computeShaderEASU == null || computeShaderRCAS == null) {
                Init();
            }
            if(AutoTextureUpdate) {
                UpdateMipMaps(renderSize.x, _displaySize.x);
            }
            //FSR1
            var cmd = context.command;
            cmd.BeginSample("FSR1");

            if(outputImage == null || outputImage2 == null || _displaySize.x != _prevDisplaySize.x || _displaySize.y != _prevDisplaySize.y || qualityMode != _prevQualityMode || Sharpening != _prevSharpening) {
                _prevQualityMode = qualityMode;
                _prevDisplaySize = _displaySize;
                _prevSharpening = Sharpening;

                scaleFactor = GetScaling();

                m_mipMapTimer = Mathf.Infinity;

                float normalizedScale = (scaleFactor - 1.3f) / (2f - 1.3f);
                float mipBias = -Mathf.Lerp(0.38f, 1f, normalizedScale); //Ultra Quality -0.38f, Quality -0.58f, Balanced -0.79f, Performance -1f

                //EASU
                if(outputImage)
                    outputImage.Release();
                outputImage = new RenderTexture(displaySize.x, displaySize.y, 0, context.sourceFormat, RenderTextureReadWrite.sRGB);
                outputImage.enableRandomWrite = true;
                outputImage.mipMapBias = mipBias; //Ultra Quality -0.38f, Quality -0.58f, Balanced -0.79f, Performance -1f
                outputImage.Create();

                //RCAS
                if(Sharpening) {
                    if(outputImage2)
                        outputImage2.Release();
                    outputImage2 = new RenderTexture(displaySize.x, displaySize.y, 0, context.sourceFormat, RenderTextureReadWrite.sRGB);
                    outputImage2.enableRandomWrite = true;
                    outputImage2.Create();
                }
            }

            //EASU
            cmd.SetComputeVectorParam(computeShaderEASU, _EASUViewportSize, new Vector4(renderSize.x, renderSize.y));
            cmd.SetComputeVectorParam(computeShaderEASU, _EASUInputImageSize, new Vector4(renderSize.x, renderSize.y));
            cmd.SetComputeVectorParam(computeShaderEASU, _EASUOutputSize, new Vector4(displaySize.x, displaySize.y, 1f / displaySize.x, 1f / displaySize.y));
            cmd.SetComputeBufferParam(computeShaderEASU, 1, _EASUParameters, EASUParametersCB);

            cmd.DispatchCompute(computeShaderEASU, 1, 1, 1, 1); //init

            cmd.SetComputeTextureParam(computeShaderEASU, 0, InputTexture, context.source);
            cmd.SetComputeTextureParam(computeShaderEASU, 0, OutputTexture, outputImage);

            const int ThreadGroupWorkRegionRim = 8;
            int dispatchX = (outputImage.width + ThreadGroupWorkRegionRim - 1) / ThreadGroupWorkRegionRim;
            int dispatchY = (outputImage.height + ThreadGroupWorkRegionRim - 1) / ThreadGroupWorkRegionRim;

            cmd.SetComputeBufferParam(computeShaderEASU, 0, _EASUParameters, EASUParametersCB);
            cmd.DispatchCompute(computeShaderEASU, 0, dispatchX, dispatchY, 1); //main

            //RCAS
            if(Sharpening) {
                cmd.SetComputeBufferParam(computeShaderRCAS, 1, _RCASParameters, RCASParametersCB);
                cmd.SetComputeFloatParam(computeShaderRCAS, _RCASScale, 1 - sharpness);
                cmd.DispatchCompute(computeShaderRCAS, 1, 1, 1, 1); //init

                cmd.SetComputeBufferParam(computeShaderRCAS, 0, _RCASParameters, RCASParametersCB);
                cmd.SetComputeTextureParam(computeShaderRCAS, 0, InputTexture, outputImage);
                cmd.SetComputeTextureParam(computeShaderRCAS, 0, OutputTexture, outputImage2);

                cmd.DispatchCompute(computeShaderRCAS, 0, dispatchX, dispatchY, 1); //main
            }

            //if(Sharpening) {
            cmd.BlitFullscreenTriangle(Sharpening ? outputImage2 : outputImage, context.destination, false, new Rect(0f, 0f, displaySize.x, displaySize.y));
            //} else {
            //    cmd.Blit(context.source, context.destination);
            //}
            cmd.EndSample("FSR1");
        }
        private float GetScaling() {
            if(qualityMode == FSR.QualityMode.Quality) {
                return 1.5f;
            } else if(qualityMode == FSR.QualityMode.Balanced) {
                return 1.7f;
            } else if(qualityMode == FSR.QualityMode.Performance) {
                return 2.0f;
            } else {
                return 3;
            }
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

                    _callbacks.OnMipMapAllTextures(m_mipMapBias);
                }
            }
        }

        private class Callbacks : Fsr3CallbacksBase
        {
            private readonly PostProcessResources _resources;

            public Callbacks() {
            }
        }
#endif
    }
}
