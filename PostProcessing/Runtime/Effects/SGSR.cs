using System;
using System.Collections.Generic;
using static UnityEngine.Rendering.PostProcessing.PostProcessLayer;

#if TND_SGSR
using TND.SGSR;
#else
public enum SGSR_Quality
{
    Off,
    Native,
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}
#endif

namespace UnityEngine.Rendering.PostProcessing
{
    [Scripting.Preserve]
    [Serializable]
    public class SGSR
    {
        public enum SGSR_Quality
        {
            Off,
            Native,
            Quality,
            Balanced,
            Performance,
            UltraPerformance,
        }

        [Tooltip("Fallback AA for when SGSR is not supported")]
        public Antialiasing fallBackAA = Antialiasing.None;

#if TND_SGSR
        [Header("SGSR Settings")]
        public SGSR_Quality qualityMode = SGSR_Quality.Performance;
        [Range(0f, 5.0f)]
        public float edgeSharpness = new FloatParameter { value = 2.0f };


        [Header("MipMap Settings")]
        public bool autoTextureUpdate = true;
        public float updateFrequency = 2.0f;
        [Range(0.0f, 1.0f)]
        public float mipMapBiasOverride = 1.0f;

        private Rect _originalRect;

        private float _scaleFactor;
        internal Vector2Int renderSize, displaySize;

        private ulong _previousLength;
        private float _prevMipMapBias;
        private float _mipMapTimer = float.MaxValue;

        public bool IsSupported()
        {
            return BlitMaterial.shader.isSupported;
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
        public void Release()
        {
            MipMapUtils.OnResetAllMipMaps();
        }

        public void ConfigureCameraViewport(PostProcessRenderContext context)
        {
            Camera camera = context.camera;
            _originalRect = camera.rect;

            _scaleFactor = GetScaling();

            displaySize = new Vector2Int(camera.pixelWidth, camera.pixelHeight);
            renderSize = new Vector2Int((int)(displaySize.x / _scaleFactor), (int)(displaySize.y / _scaleFactor));
            if (qualityMode == SGSR_Quality.Off)
            {
                Release();
            }
            if (context.camera.stereoEnabled)
            {
#if UNITY_STANDALONE
                camera.rect = new Rect(0, 0, renderSize.x, renderSize.y);
#else
                ScalableBufferManager.ResizeBuffers(1 / _scaleFactor, 1 / _scaleFactor);
#endif
            }
            else
            {
                camera.rect = new Rect(0, 0, renderSize.x, renderSize.y);
            }
        }

        public void ResetCameraViewport(PostProcessRenderContext context)
        {
            if (context.camera.stereoEnabled)
            {
#if UNITY_STANDALONE
                context.camera.rect = _originalRect;
#else
                ScalableBufferManager.ResizeBuffers(1, 1);
#endif
            }
            else
            {
                context.camera.rect = _originalRect;
            }
        }

        internal void Render(PostProcessRenderContext context)
        {


            var cmd = context.command;
            if (qualityMode == SGSR_Quality.Off)
            {
                cmd.Blit(context.source, context.destination);
                return;
            }
            if (autoTextureUpdate)
            {
                MipMapUtils.AutoUpdateMipMaps(renderSize.x, displaySize.x, mipMapBiasOverride, updateFrequency, ref _prevMipMapBias, ref _mipMapTimer, ref _previousLength);
            }
            cmd.BeginSample("SGSR");

            cmd.SetGlobalFloat(SGSR_UTILS.idEdgeSharpness, edgeSharpness);
            cmd.SetGlobalVector(SGSR_UTILS.idViewportInfo, new Vector4(1.0f / renderSize.x, 1.0f / renderSize.y, renderSize.x, renderSize.y));
            cmd.SetGlobalTexture(SGSR_UTILS.idBlitTexture, context.source);
            cmd.SetRenderTarget(context.destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetViewport(new Rect(0, 0, displaySize.x, displaySize.y));
            cmd.DrawMesh(FullscreenMesh, Matrix4x4.identity, BlitMaterial, 0, 0);
            cmd.SetViewProjectionMatrices(context.camera.worldToCameraMatrix, context.camera.projectionMatrix);

            cmd.EndSample("SGSR");
        }

        private float GetScaling()
        {
            switch (qualityMode)
            {
                case SGSR_Quality.Off:
                    return 1.0f;
                case SGSR_Quality.Native:
                    return 1.0f;
                case SGSR_Quality.Quality:
                    return 1.5f;
                case SGSR_Quality.Balanced:
                    return 1.7f;
                case SGSR_Quality.Performance:
                    return 2.0f;
                case SGSR_Quality.UltraPerformance:
                    return 3.0f;
                default:
                    Debug.LogError($"[SGSR Upscaler]: Quality Level {qualityMode} is not implemented, defaulting to Performance");
                    break;
            }

            return 2.0f;
        }

        static Material _blitMaterial = null;

        public static Material BlitMaterial
        {
            get
            {
                if (_blitMaterial == null)
                {
                    _blitMaterial = new Material(Shader.Find("Hidden/SGSR_BlitShader_BIRP"));
                }

                return _blitMaterial;
            }
        }

        static Mesh _fullscreenMesh = null;
        /// <summary>
        /// Returns a mesh that you can use with <see cref="CommandBuffer.DrawMesh(Mesh, Matrix4x4, Material)"/> to render full-screen effects.
        /// </summary>
        public static Mesh FullscreenMesh
        {
            get
            {
                if (_fullscreenMesh != null)
                    return _fullscreenMesh;

                float topV = 1.0f;
                float bottomV = 0.0f;

                _fullscreenMesh = new Mesh { name = "Fullscreen Quad" };
                _fullscreenMesh.SetVertices(new List<Vector3>
                {
                    new Vector3(-1.0f, -1.0f, 0.0f),
                    new Vector3(-1.0f,  1.0f, 0.0f),
                    new Vector3(1.0f, -1.0f, 0.0f),
                    new Vector3(1.0f,  1.0f, 0.0f)
                });

                _fullscreenMesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0.0f, bottomV),
                    new Vector2(0.0f, topV),
                    new Vector2(1.0f, bottomV),
                    new Vector2(1.0f, topV)
                });

                _fullscreenMesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0, false);
                _fullscreenMesh.UploadMeshData(true);
                return _fullscreenMesh;
            }
        }
#endif
    }
}
