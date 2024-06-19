using UnityEngine;

public static class MipMapUtils
{
    public static float CalculateMipMapBias(float renderWidth, float displayWidth, float mipmapBiasOverride)
    {
        return (Mathf.Log(renderWidth / displayWidth, 2f) - 1) * mipmapBiasOverride;
    }

    /// <summary>
    /// Updates a single texture, where the MipMap bias is calculated by the provided render width, display width and override.
    /// Should be called when an object is instantiated, or when the ScaleFactor is changed.
    /// </summary>
    public static void OnMipMapSingleTexture(Texture texture, float renderWidth, float displayWidth, float mipmapBiasOverride)
    {
        OnMipMapSingleTexture(texture, CalculateMipMapBias(renderWidth, displayWidth, mipmapBiasOverride));
    }

    /// <summary>
    /// Updates a single texture to the set MipMap Bias.
    /// Should be called when an object is instantiated, or when the ScaleFactor is changed.
    /// </summary>
    public static void OnMipMapSingleTexture(Texture texture, float mapmapBias)
    {
        texture.mipMapBias = mapmapBias;
    }

    /// <summary>
    /// Updates all textures currently loaded, where the MipMap bias is calculated by the provided render width, display width and override.
    /// Should be called when a lot of new textures are loaded, or when the ScaleFactor is changed.
    /// </summary>
    public static void OnMipMapAllTextures(float renderWidth, float displayWidth, float mipmapBiasOverride)
    {
        OnMipMapAllTextures(CalculateMipMapBias(renderWidth, displayWidth, mipmapBiasOverride));
    }

    /// <summary>
    /// Updates all textures currently loaded to the set MipMap Bias.
    /// Should be called when a lot of new textures are loaded, or when the ScaleFactor is changed.
    /// </summary>
    public static void OnMipMapAllTextures(float mapmapBias)
    {
        Texture[] m_allTextures = Resources.FindObjectsOfTypeAll(typeof(Texture)) as Texture[];
        for (int i = 0; i < m_allTextures.Length; i++)
        {
            m_allTextures[i].mipMapBias = mapmapBias;
        }
    }

    /// <summary>
    /// Resets all currently loaded textures to the default mipmap bias. 
    /// </summary>
    public static void OnResetAllMipMaps()
    {
        Texture[] m_allTextures = Resources.FindObjectsOfTypeAll(typeof(Texture)) as Texture[];
        for (int i = 0; i < m_allTextures.Length; i++)
        {
            m_allTextures[i].mipMapBias = 0;
        }
    }

    public static void AutoUpdateMipMaps(float renderWidth, float displayWidth, float mipMapBiasOverride, float updateFrequency, ref float prevMipMapBias, ref float mipMapTimer, ref ulong previousLength)
    {
        mipMapTimer += Time.deltaTime;

        if(mipMapTimer > updateFrequency)
        {
            mipMapTimer = 0;

            float mipMapBias = CalculateMipMapBias(renderWidth, displayWidth, mipMapBiasOverride);
            if(previousLength != Texture.currentTextureMemory || prevMipMapBias != mipMapBias)
            {
                prevMipMapBias = mipMapBias;
                previousLength = Texture.currentTextureMemory;
                OnMipMapAllTextures(mipMapBias);
            }
        }
    }
}
