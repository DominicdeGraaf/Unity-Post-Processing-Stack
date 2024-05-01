using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEditorInternal;
using System.IO;

namespace UnityEditor.Rendering.PostProcessing
{
    using SerializedBundleRef = PostProcessLayer.SerializedBundleRef;
    using EXRFlags = Texture2D.EXRFlags;

    [CanEditMultipleObjects, CustomEditor(typeof(PostProcessLayer))]
    sealed class PostProcessLayerEditor : BaseEditor<PostProcessLayer>
    {
        SerializedProperty m_StopNaNPropagation;
#pragma warning disable 414
        SerializedProperty m_DirectToCameraTarget;
#pragma warning restore 414
        SerializedProperty m_VolumeTrigger;
        SerializedProperty m_VolumeLayer;

        SerializedProperty m_AntialiasingMode;
        SerializedProperty m_TaaJitterSpread;
        SerializedProperty m_TaaSharpness;
        SerializedProperty m_TaaStationaryBlending;
        SerializedProperty m_TaaMotionBlending;
        SerializedProperty m_SmaaQuality;
        SerializedProperty m_FxaaFastMode;
        SerializedProperty m_FxaaKeepAlpha;

        //SGSR
        SerializedProperty m_SGSRQualityMode;
        SerializedProperty m_SGSREdgeSharpness;
        SerializedProperty m_SGSRFallBack;

        SerializedProperty m_SGSRAutoTextureUpdate;
        SerializedProperty m_SGSRUpdateFrequency;
        SerializedProperty m_SGSRMipmapBiasOverride;

        //FSR 1
        SerializedProperty m_FSR1QualityMode;
        SerializedProperty m_FSR1PerformSharpen;
        SerializedProperty m_FSR1Sharpness;

        SerializedProperty m_FSR1FallBack;
        SerializedProperty m_FSR1AutoTextureUpdate;
        SerializedProperty m_FSR1UpdateFrequency;
        SerializedProperty m_FSR1MipmapBiasOverride;

        //FSR 3
        SerializedProperty m_FSR3QualityMode;
        SerializedProperty m_FSR3PerformSharpen;
        SerializedProperty m_FSR3Sharpness;
        SerializedProperty m_FSR3EnableFP16;
        SerializedProperty m_FSR3ExposureSource;
        SerializedProperty m_FSR3ExposureTexture;
        SerializedProperty m_FSR3PreExposure;
        SerializedProperty m_FSR3AutoReactive;
        SerializedProperty m_FSR3ReactiveScale;
        SerializedProperty m_FSR3ReactiveThreshold;
        SerializedProperty m_FSR3ReactiveBinaryValue;
        SerializedProperty m_FSR3ReactiveFlags;
        SerializedProperty m_FSR3ReactiveMaskTexture;
        SerializedProperty m_FSR3AutoTextureUpdate;

        SerializedProperty m_FSR3UpdateFrequency;
        SerializedProperty m_FSR3MipmapBiasOverride;

        SerializedProperty m_FSR3AutoTcr;
        SerializedProperty m_FSR3AutoTcrParams;
        SerializedProperty m_FSR3TcrMaskTexture;

        SerializedProperty m_FSR3FallBack;
        SerializedProperty m_FSR3AntiGhosting;

        //DLSS
        SerializedProperty m_DLSSQualityMode;
        SerializedProperty m_DLSSFallBack;

        SerializedProperty m_DLSSAutoTextureUpdate;
        SerializedProperty m_DLSSUpdateFrequency;
        SerializedProperty m_DLSSMipmapBiasOverride;



        SerializedProperty m_DLSSAntiGhosting;

        SerializedProperty m_FogEnabled;
        SerializedProperty m_FogExcludeSkybox;

        SerializedProperty m_ShowToolkit;
        SerializedProperty m_ShowCustomSorter;

        Dictionary<PostProcessEvent, ReorderableList> m_CustomLists;

#if UNITY_2017_3_OR_NEWER
        Camera m_TargetCameraComponent;
#endif

        static GUIContent[] s_AntialiasingMethodNames =
        {
            new GUIContent("No Anti-aliasing"),
            new GUIContent("Fast Approximate Anti-aliasing (FXAA)"),
            new GUIContent("Subpixel Morphological Anti-aliasing (SMAA)"),
            new GUIContent("Temporal Anti-aliasing (TAA)"),
            new GUIContent("Snapdragon Game Super Resolution (SGSR)"),
            new GUIContent("FidelityFX Super Resolution 1 (FSR1)"),
            new GUIContent("FidelityFX Super Resolution 3 (FSR3)"),
            new GUIContent("Deep Learning Super Sampling (DLSS)")
        };

        static GUIContent[] s_AntialiasingDLSSFallBackMethodNames =
       {
            new GUIContent("No Anti-aliasing"),
            new GUIContent("Fast Approximate Anti-aliasing (FXAA)"),
            new GUIContent("Subpixel Morphological Anti-aliasing (SMAA)"),
            new GUIContent("Temporal Anti-aliasing (TAA)"),
            new GUIContent("Snapdragon Game Super Resolution (SGSR)"),
            new GUIContent("FidelityFX Super Resolution 1 (FSR1)"),
            new GUIContent("FidelityFX Super Resolution 3 (FSR3)"),
        };

        static GUIContent[] s_AntialiasingFSR3FallBackMethodNames =
        {
            new GUIContent("No Anti-aliasing"),
            new GUIContent("Fast Approximate Anti-aliasing (FXAA)"),
            new GUIContent("Subpixel Morphological Anti-aliasing (SMAA)"),
            new GUIContent("Temporal Anti-aliasing (TAA)"),
            new GUIContent("Snapdragon Game Super Resolution (SGSR)"),
            new GUIContent("FidelityFX Super Resolution 1 (FSR1)"),
        };

        static GUIContent[] s_AntialiasingFSR1FallBackMethodNames =
        {
            new GUIContent("No Anti-aliasing"),
            new GUIContent("Fast Approximate Anti-aliasing (FXAA)"),
            new GUIContent("Subpixel Morphological Anti-aliasing (SMAA)"),
            new GUIContent("Temporal Anti-aliasing (TAA)"),
            new GUIContent("Snapdragon Game Super Resolution (SGSR)"),
        };

        static GUIContent[] s_AntialiasingSGSRFallBackMethodNames =
        {
            new GUIContent("No Anti-aliasing"),
            new GUIContent("Fast Approximate Anti-aliasing (FXAA)"),
            new GUIContent("Subpixel Morphological Anti-aliasing (SMAA)"),
            new GUIContent("Temporal Anti-aliasing (TAA)"),
            new GUIContent("FidelityFX Super Resolution 1 (FSR1)"),
        };

        enum ExportMode
        {
            FullFrame,
            DisablePost,
            BreakBeforeColorGradingLinear,
            BreakBeforeColorGradingLog
        }

        void OnEnable() {
            m_StopNaNPropagation = FindProperty(x => x.stopNaNPropagation);
            m_DirectToCameraTarget = FindProperty(x => x.finalBlitToCameraTarget);
            m_VolumeTrigger = FindProperty(x => x.volumeTrigger);
            m_VolumeLayer = FindProperty(x => x.volumeLayer);

            m_AntialiasingMode = FindProperty(x => x.antialiasingMode);
            m_TaaJitterSpread = FindProperty(x => x.temporalAntialiasing.jitterSpread);
            m_TaaSharpness = FindProperty(x => x.temporalAntialiasing.sharpness);
            m_TaaStationaryBlending = FindProperty(x => x.temporalAntialiasing.stationaryBlending);
            m_TaaMotionBlending = FindProperty(x => x.temporalAntialiasing.motionBlending);
            m_SmaaQuality = FindProperty(x => x.subpixelMorphologicalAntialiasing.quality);
            m_FxaaFastMode = FindProperty(x => x.fastApproximateAntialiasing.fastMode);
            m_FxaaKeepAlpha = FindProperty(x => x.fastApproximateAntialiasing.keepAlpha);
#if TND_SGSR
            m_SGSRQualityMode = FindProperty(x => x.sgsr.qualityMode);
            m_SGSREdgeSharpness = FindProperty(x => x.sgsr.edgeSharpness);

            m_SGSRAutoTextureUpdate = FindProperty(x => x.sgsr.autoTextureUpdate);
            m_SGSRUpdateFrequency = FindProperty(x => x.sgsr.updateFrequency);
            m_SGSRMipmapBiasOverride = FindProperty(x => x.sgsr.mipmapBiasOverride);
#endif
#if TND_FSR1 || AEG_FSR1
            m_FSR1QualityMode = FindProperty(x => x.fsr1.qualityMode);
            m_FSR1PerformSharpen = FindProperty(x => x.fsr1.Sharpening);
            m_FSR1Sharpness = FindProperty(x => x.fsr1.sharpness);

            m_FSR1AutoTextureUpdate = FindProperty(x => x.fsr1.AutoTextureUpdate);
            m_FSR1UpdateFrequency = FindProperty(x => x.fsr1.UpdateFrequency);
            m_FSR1MipmapBiasOverride = FindProperty(x => x.fsr1.MipmapBiasOverride);
#endif
#if TND_FSR3 || AEG_FSR3
            m_FSR3QualityMode = FindProperty(x => x.fsr3.qualityMode);
            m_FSR3PerformSharpen = FindProperty(x => x.fsr3.Sharpening);
            m_FSR3Sharpness = FindProperty(x => x.fsr3.sharpness);

            m_FSR3EnableFP16 = FindProperty(x => x.fsr3.enableFP16);
            m_FSR3ExposureSource = FindProperty(x => x.fsr3.exposureSource);
            m_FSR3ExposureTexture = FindProperty(x => x.fsr3.exposure);
            m_FSR3PreExposure = FindProperty(x => x.fsr3.preExposure);
            m_FSR3AutoReactive = FindProperty(x => x.fsr3.autoGenerateReactiveMask);

            m_FSR3ReactiveScale = FindProperty(x => x.fsr3.ReactiveScale);
            m_FSR3ReactiveThreshold = FindProperty(x => x.fsr3.ReactiveThreshold);
            m_FSR3ReactiveBinaryValue = FindProperty(x => x.fsr3.ReactiveBinaryValue);
            m_FSR3ReactiveFlags = FindProperty(x => x.fsr3.flags);

            m_FSR3ReactiveMaskTexture = FindProperty(x => x.fsr3.reactiveMask);

            m_FSR3AutoTcr = FindProperty(x => x.fsr3.autoGenerateTransparencyAndComposition);
            m_FSR3AutoTcrParams = FindProperty(x => x.fsr3.generateTransparencyAndCompositionParameters);
            m_FSR3TcrMaskTexture = FindProperty(x => x.fsr3.transparencyAndCompositionMask);

            m_FSR3AutoTextureUpdate = FindProperty(x => x.fsr3.AutoTextureUpdate);
            m_FSR3UpdateFrequency = FindProperty(x => x.fsr3.UpdateFrequency);
            m_FSR3MipmapBiasOverride = FindProperty(x => x.fsr3.MipmapBiasOverride);

            m_FSR3AntiGhosting = FindProperty(x => x.fsr3.antiGhosting);

#endif
#if TND_DLSS || AEG_DLSS
            m_DLSSQualityMode = FindProperty(x => x.dlss.qualityMode);
            m_DLSSAntiGhosting = FindProperty(x => x.dlss.antiGhosting);

            m_DLSSAutoTextureUpdate = FindProperty(x => x.dlss.AutoTextureUpdate);
            m_DLSSUpdateFrequency = FindProperty(x => x.dlss.UpdateFrequency);
            m_DLSSMipmapBiasOverride = FindProperty(x => x.dlss.MipmapBiasOverride);
#endif
            m_SGSRFallBack = FindProperty(x => x.sgsr.fallBackAA);
            m_FSR1FallBack = FindProperty(x => x.fsr1.fallBackAA);
            m_FSR3FallBack = FindProperty(x => x.fsr3.fallBackAA);
            m_DLSSFallBack = FindProperty(x => x.dlss.fallBackAA);

            m_FogEnabled = FindProperty(x => x.fog.enabled);
            m_FogExcludeSkybox = FindProperty(x => x.fog.excludeSkybox);

            m_ShowToolkit = serializedObject.FindProperty("m_ShowToolkit");
            m_ShowCustomSorter = serializedObject.FindProperty("m_ShowCustomSorter");

#if UNITY_2017_3_OR_NEWER
            m_TargetCameraComponent = m_Target.GetComponent<Camera>();
#endif
        }

        void OnDisable() {
            m_CustomLists = null;
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            var camera = m_Target.GetComponent<Camera>();

            DoVolumeBlending();
            DoAntialiasing();
            DoFog(camera);

            EditorGUILayout.PropertyField(m_StopNaNPropagation, EditorUtilities.GetContent("Stop NaN Propagation|Automatically replaces NaN/Inf in shaders by a black pixel to avoid breaking some effects. This will slightly affect performances and should only be used if you experience NaN issues that you can't fix. Has no effect on GLES2 platforms."));

#if UNITY_2019_1_OR_NEWER
            if (!RuntimeUtilities.scriptableRenderPipelineActive)
                EditorGUILayout.PropertyField(m_DirectToCameraTarget, EditorUtilities.GetContent("Directly to Camera Target|Use the final blit to the camera render target for postprocessing. This has less overhead but breaks compatibility with legacy image effect that use OnRenderImage."));
#endif

            EditorGUILayout.Space();

            DoToolkit();
            DoCustomEffectSorter();

            EditorUtilities.DrawSplitter();
            EditorGUILayout.Space();

            serializedObject.ApplyModifiedProperties();
        }

        void DoVolumeBlending() {
            EditorGUILayout.LabelField(EditorUtilities.GetContent("Volume blending"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            {
                // The layout system sort of break alignement when mixing inspector fields with
                // custom layouted fields, do the layout manually instead
                var indentOffset = EditorGUI.indentLevel * 15f;
                var lineRect = GUILayoutUtility.GetRect(1, EditorGUIUtility.singleLineHeight);
                var labelRect = new Rect(lineRect.x, lineRect.y, EditorGUIUtility.labelWidth - indentOffset, lineRect.height);
                var fieldRect = new Rect(labelRect.xMax, lineRect.y, lineRect.width - labelRect.width - 60f, lineRect.height);
                var buttonRect = new Rect(fieldRect.xMax, lineRect.y, 60f, lineRect.height);

                EditorGUI.PrefixLabel(labelRect, EditorUtilities.GetContent("Trigger|A transform that will act as a trigger for volume blending."));
                m_VolumeTrigger.objectReferenceValue = (Transform)EditorGUI.ObjectField(fieldRect, m_VolumeTrigger.objectReferenceValue, typeof(Transform), true);
                if(GUI.Button(buttonRect, EditorUtilities.GetContent("This|Assigns the current GameObject as a trigger."), EditorStyles.miniButton))
                    m_VolumeTrigger.objectReferenceValue = m_Target.transform;

                if(m_VolumeTrigger.objectReferenceValue == null)
                    EditorGUILayout.HelpBox("No trigger has been set, the camera will only be affected by global volumes.", MessageType.Info);

                EditorGUILayout.PropertyField(m_VolumeLayer, EditorUtilities.GetContent("Layer|This camera will only be affected by volumes in the selected scene-layers."));

                int mask = m_VolumeLayer.intValue;
                if(mask == 0)
                    EditorGUILayout.HelpBox("No layer has been set, the trigger will never be affected by volumes.", MessageType.Warning);
                else if(mask == -1 || ((mask & 1) != 0))
                    EditorGUILayout.HelpBox("Do not use \"Everything\" or \"Default\" as a layer mask as it will slow down the volume blending process! Put post-processing volumes in their own dedicated layer for best performances.", MessageType.Warning);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
        }

        void DoAntialiasing() {
            EditorGUILayout.LabelField(EditorUtilities.GetContent("Anti-aliasing"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            {
                m_AntialiasingMode.intValue = EditorGUILayout.Popup(EditorUtilities.GetContent("Mode|The anti-aliasing method to use. FXAA is fast but low quality. SMAA works well for non-HDR scenes. TAA is a bit slower but higher quality and works well with HDR."), m_AntialiasingMode.intValue, s_AntialiasingMethodNames);

                if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.TemporalAntialiasing) {
#if !UNITY_2017_3_OR_NEWER
                    if(RuntimeUtilities.isSinglePassStereoSelected)
                        EditorGUILayout.HelpBox("TAA requires Unity 2017.3+ for Single-pass stereo rendering support.", MessageType.Warning);
#endif
#if UNITY_2017_3_OR_NEWER
                    if (m_TargetCameraComponent != null && RuntimeUtilities.IsDynamicResolutionEnabled(m_TargetCameraComponent))
                        EditorGUILayout.HelpBox("TAA is not supported with Dynamic Resolution.", MessageType.Warning);
#endif

                    EditorGUILayout.PropertyField(m_TaaJitterSpread);
                    EditorGUILayout.PropertyField(m_TaaStationaryBlending);
                    EditorGUILayout.PropertyField(m_TaaMotionBlending);
                    EditorGUILayout.PropertyField(m_TaaSharpness);
                } else if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing) {
                    if(RuntimeUtilities.isSinglePassStereoSelected)
                        EditorGUILayout.HelpBox("SMAA doesn't work with Single-pass stereo rendering.", MessageType.Warning);

                    EditorGUILayout.PropertyField(m_SmaaQuality);

                    if(m_SmaaQuality.intValue != (int)SubpixelMorphologicalAntialiasing.Quality.Low && EditorUtilities.isTargetingConsolesOrMobiles)
                        EditorGUILayout.HelpBox("For performance reasons it is recommended to use Low Quality on mobile and console platforms.", MessageType.Warning);
                } else if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.FastApproximateAntialiasing) {
                    EditorGUILayout.PropertyField(m_FxaaFastMode);
                    EditorGUILayout.PropertyField(m_FxaaKeepAlpha);

                    if(!m_FxaaFastMode.boolValue && EditorUtilities.isTargetingConsolesOrMobiles)
                        EditorGUILayout.HelpBox("For performance reasons it is recommended to use Fast Mode on mobile and console platforms.", MessageType.Warning);
                } else if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.SGSR)
                {
                    EditorGUI.indentLevel++;
                    m_SGSRFallBack.intValue = EditorGUILayout.Popup(EditorUtilities.GetContent("Fall Back|The anti-aliasing method to use with FSR 1, FSR 3 or DLSS are not supported. FXAA is fast but low quality. SMAA works well for non-HDR scenes. TAA is a bit slower but higher quality and works well with HDR."), m_SGSRFallBack.intValue, s_AntialiasingSGSRFallBackMethodNames);
                    EditorGUI.indentLevel--;
#if TND_SGSR
                    EditorGUILayout.PropertyField(m_SGSRQualityMode);
                    EditorGUILayout.PropertyField(m_SGSREdgeSharpness);

                    EditorGUILayout.PropertyField(m_SGSRAutoTextureUpdate);
                    if (m_SGSRAutoTextureUpdate.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_SGSRUpdateFrequency);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(m_SGSRMipmapBiasOverride);

#if !UNITY_2017_3_OR_NEWER
                    if(RuntimeUtilities.isSinglePassStereoSelected)
                        EditorGUILayout.HelpBox("TAA requires Unity 2017.3+ for Single-pass stereo rendering support.", MessageType.Warning);
#endif

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("Unity TAA Settings."), EditorStyles.boldLabel);

                    EditorGUILayout.PropertyField(m_TaaJitterSpread);
                    EditorGUILayout.PropertyField(m_TaaStationaryBlending);
                    EditorGUILayout.PropertyField(m_TaaMotionBlending);
                    EditorGUILayout.PropertyField(m_TaaSharpness);
#else
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("----- SGSR Package not loaded ------"), EditorStyles.boldLabel);
#endif
                }
                else if (m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.FSR1) {
                    EditorGUI.indentLevel++;
                    m_FSR1FallBack.intValue = EditorGUILayout.Popup(EditorUtilities.GetContent("Fall Back|The anti-aliasing method to use with FSR 1, FSR 3 or DLSS are not supported. FXAA is fast but low quality. SMAA works well for non-HDR scenes. TAA is a bit slower but higher quality and works well with HDR."), m_FSR1FallBack.intValue, s_AntialiasingFSR1FallBackMethodNames);
                    EditorGUI.indentLevel--;
#if TND_FSR1 || AEG_FSR1
                    EditorGUILayout.PropertyField(m_FSR1QualityMode);
                    EditorGUILayout.PropertyField(m_FSR1PerformSharpen);
                    if (m_FSR1PerformSharpen.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_FSR1Sharpness);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.PropertyField(m_FSR1AutoTextureUpdate);
                    if (m_FSR1AutoTextureUpdate.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_FSR1UpdateFrequency);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(m_FSR1MipmapBiasOverride);

#if !UNITY_2017_3_OR_NEWER
                    if(RuntimeUtilities.isSinglePassStereoSelected)
                        EditorGUILayout.HelpBox("TAA requires Unity 2017.3+ for Single-pass stereo rendering support.", MessageType.Warning);
#endif

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("Unity TAA Settings."), EditorStyles.boldLabel);

                    EditorGUILayout.PropertyField(m_TaaJitterSpread);
                    EditorGUILayout.PropertyField(m_TaaStationaryBlending);
                    EditorGUILayout.PropertyField(m_TaaMotionBlending);
                    EditorGUILayout.PropertyField(m_TaaSharpness);
#else
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("----- FSR 1 Package not loaded ------"), EditorStyles.boldLabel);
#endif
                } else if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.FSR3) {
                    EditorGUI.indentLevel++;
                    m_FSR3FallBack.intValue = EditorGUILayout.Popup(EditorUtilities.GetContent("Fall Back|The anti-aliasing method to use with FSR 1, FSR 3 or DLSS are not supported. FXAA is fast but low quality. SMAA works well for non-HDR scenes. TAA is a bit slower but higher quality and works well with HDR."), m_FSR3FallBack.intValue, s_AntialiasingFSR3FallBackMethodNames);
                    EditorGUI.indentLevel--;

#if TND_FSR3 || AEG_FSR3
                    EditorGUILayout.PropertyField(m_FSR3QualityMode);
                    EditorGUILayout.PropertyField(m_FSR3AntiGhosting);
                    EditorGUILayout.PropertyField(m_FSR3PerformSharpen);
                    if (m_FSR3PerformSharpen.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_FSR3Sharpness);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(m_FSR3AutoReactive);
                    if (m_FSR3AutoReactive.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_FSR3ReactiveScale);
                        EditorGUILayout.PropertyField(m_FSR3ReactiveThreshold);
                        EditorGUILayout.PropertyField(m_FSR3ReactiveBinaryValue);
                        EditorGUILayout.PropertyField(m_FSR3ReactiveFlags);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.PropertyField(m_FSR3AutoTextureUpdate);
                    if (m_FSR3AutoTextureUpdate.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_FSR3UpdateFrequency);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(m_FSR3MipmapBiasOverride);
#else
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("----- FSR 3 Package not loaded ------"), EditorStyles.boldLabel);
#endif

                } else if(m_AntialiasingMode.intValue == (int)PostProcessLayer.Antialiasing.DLSS) {
#if !UNITY_STANDALONE_WIN || !UNITY_64
                    EditorGUILayout.LabelField("----- DLSS is not supported on this platform ------", EditorStyles.boldLabel);
#endif
                    EditorGUI.indentLevel++;
                    m_DLSSFallBack.intValue = EditorGUILayout.Popup(EditorUtilities.GetContent("Fall Back|The anti-aliasing method to use with FSR 1, FSR 3 or DLSS are not supported. FXAA is fast but low quality. SMAA works well for non-HDR scenes. TAA is a bit slower but higher quality and works well with HDR."), m_DLSSFallBack.intValue, s_AntialiasingDLSSFallBackMethodNames);
                    EditorGUI.indentLevel--;

#if !DLSS_INSTALLED
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("----- Missing NVIDIA DLSS Package ------"), EditorStyles.boldLabel);
                    if(GUILayout.Button("Install Package")) {
                        UnityEditor.PackageManager.Client.Add("com.unity.modules.nvidia");
                        AddDefine("DLSS_INSTALLED");
                        AssetDatabase.Refresh();
                    }
#else
#if TND_DLSS || AEG_DLSS

                    EditorGUILayout.PropertyField(m_DLSSQualityMode);
                    EditorGUILayout.PropertyField(m_DLSSAntiGhosting);

                    EditorGUILayout.PropertyField(m_DLSSAutoTextureUpdate);
                    if(m_DLSSAutoTextureUpdate.boolValue) {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(m_DLSSUpdateFrequency);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.PropertyField(m_DLSSMipmapBiasOverride);
#else
                    EditorGUILayout.LabelField(EditorUtilities.GetContent("----- DLSS Package not loaded ------"), EditorStyles.boldLabel);
#endif
#endif
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
        }

        void AddDefine(string define) {
            var definesList = GetDefines();
            if(!definesList.Contains(define)) {
                definesList.Add(define);
                SetDefines(definesList);
            }
        }

        void RemoveDefine(string define) {
            var definesList = GetDefines();
            if(definesList.Contains(define)) {
                definesList.Remove(define);
                SetDefines(definesList);
            }
        }

        List<string> GetDefines() {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
            return defines.Split(';').ToList();
        }

        void SetDefines(List<string> definesList) {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            var defines = string.Join(";", definesList.ToArray());
            PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, defines);
        }

        void DoFog(Camera camera) {
            if(camera == null || camera.actualRenderingPath != RenderingPath.DeferredShading)
                return;

            EditorGUILayout.LabelField(EditorUtilities.GetContent("Deferred Fog"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            {
                EditorGUILayout.PropertyField(m_FogEnabled);

                if(m_FogEnabled.boolValue) {
                    EditorGUILayout.PropertyField(m_FogExcludeSkybox);
                    EditorGUILayout.HelpBox("This adds fog compatibility to the deferred rendering path; actual fog settings should be set in the Lighting panel.", MessageType.Info);
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
        }

        void DoToolkit() {
            EditorUtilities.DrawSplitter();
            m_ShowToolkit.boolValue = EditorUtilities.DrawHeader("Toolkit", m_ShowToolkit.boolValue);

            if(m_ShowToolkit.boolValue) {
                GUILayout.Space(2);

                if(GUILayout.Button(EditorUtilities.GetContent("Export frame to EXR..."), EditorStyles.miniButton)) {
                    var menu = new GenericMenu();
                    menu.AddItem(EditorUtilities.GetContent("Full Frame (as displayed)"), false, () => ExportFrameToExr(ExportMode.FullFrame));
                    menu.AddItem(EditorUtilities.GetContent("Disable post-processing"), false, () => ExportFrameToExr(ExportMode.DisablePost));
                    menu.AddItem(EditorUtilities.GetContent("Break before Color Grading (Linear)"), false, () => ExportFrameToExr(ExportMode.BreakBeforeColorGradingLinear));
                    menu.AddItem(EditorUtilities.GetContent("Break before Color Grading (Log)"), false, () => ExportFrameToExr(ExportMode.BreakBeforeColorGradingLog));
                    menu.ShowAsContext();
                }

                if(GUILayout.Button(EditorUtilities.GetContent("Select all layer volumes|Selects all the volumes that will influence this layer."), EditorStyles.miniButton)) {
                    var volumes = RuntimeUtilities.GetAllSceneObjects<PostProcessVolume>()
                        .Where(x => (m_VolumeLayer.intValue & (1 << x.gameObject.layer)) != 0)
                        .Select(x => x.gameObject)
                        .Cast<UnityEngine.Object>()
                        .ToArray();

                    if(volumes.Length > 0)
                        Selection.objects = volumes;
                }

                if(GUILayout.Button(EditorUtilities.GetContent("Select all active volumes|Selects all volumes currently affecting the layer."), EditorStyles.miniButton)) {
                    var volumes = new List<PostProcessVolume>();
                    PostProcessManager.instance.GetActiveVolumes(m_Target, volumes);

                    if(volumes.Count > 0) {
                        Selection.objects = volumes
                            .Select(x => x.gameObject)
                            .Cast<UnityEngine.Object>()
                            .ToArray();
                    }
                }

                GUILayout.Space(3);
            }
        }

        void DoCustomEffectSorter() {
            EditorUtilities.DrawSplitter();
            m_ShowCustomSorter.boolValue = EditorUtilities.DrawHeader("Custom Effect Sorting", m_ShowCustomSorter.boolValue);

            if(m_ShowCustomSorter.boolValue) {
                bool isInPrefab = false;

                // Init lists if needed
                if(m_CustomLists == null) {
                    // In some cases the editor will refresh before components which means
                    // components might not have been fully initialized yet. In this case we also
                    // need to make sure that we're not in a prefab as sorteBundles isn't a
                    // serializable object and won't exist until put on a scene.
                    if(m_Target.sortedBundles == null) {
                        isInPrefab = string.IsNullOrEmpty(m_Target.gameObject.scene.name);

                        if(!isInPrefab) {
                            // sortedBundles will be initialized and ready to use on the next frame
                            Repaint();
                        }
                    } else {
                        // Create a reorderable list for each injection event
                        m_CustomLists = new Dictionary<PostProcessEvent, ReorderableList>();
                        foreach(var evt in Enum.GetValues(typeof(PostProcessEvent)).Cast<PostProcessEvent>()) {
                            var bundles = m_Target.sortedBundles[evt];
                            var listName = ObjectNames.NicifyVariableName(evt.ToString());

                            var list = new ReorderableList(bundles, typeof(SerializedBundleRef), true, true, false, false);

                            list.drawHeaderCallback = (rect) => {
                                EditorGUI.LabelField(rect, listName);
                            };

                            list.drawElementCallback = (rect, index, isActive, isFocused) => {
                                var sbr = (SerializedBundleRef)list.list[index];
                                EditorGUI.LabelField(rect, sbr.bundle.attribute.menuItem);
                            };

                            list.onReorderCallback = (l) => {
                                EditorUtility.SetDirty(m_Target);
                            };

                            m_CustomLists.Add(evt, list);
                        }
                    }
                }

                GUILayout.Space(5);

                if(isInPrefab) {
                    EditorGUILayout.HelpBox("Not supported in prefabs.", MessageType.Info);
                    GUILayout.Space(3);
                    return;
                }

                bool anyList = false;
                if(m_CustomLists != null) {
                    foreach(var kvp in m_CustomLists) {
                        var list = kvp.Value;

                        // Skip empty lists to avoid polluting the inspector
                        if(list.count == 0)
                            continue;

                        list.DoLayoutList();
                        anyList = true;
                    }
                }

                if(!anyList) {
                    EditorGUILayout.HelpBox("No custom effect loaded.", MessageType.Info);
                    GUILayout.Space(3);
                }
            }
        }

        void ExportFrameToExr(ExportMode mode) {
            string path = EditorUtility.SaveFilePanel("Export EXR...", "", "Frame", "exr");

            if(string.IsNullOrEmpty(path))
                return;

            EditorUtility.DisplayProgressBar("Export EXR", "Rendering...", 0f);

            var camera = m_Target.GetComponent<Camera>();
            var w = camera.pixelWidth;
            var h = camera.pixelHeight;

            var texOut = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            var target = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

            var lastActive = RenderTexture.active;
            var lastTargetSet = camera.targetTexture;
            var lastPostFXState = m_Target.enabled;
            var lastBreakColorGradingState = m_Target.breakBeforeColorGrading;

            if(mode == ExportMode.DisablePost)
                m_Target.enabled = false;
            else if(mode == ExportMode.BreakBeforeColorGradingLinear || mode == ExportMode.BreakBeforeColorGradingLog)
                m_Target.breakBeforeColorGrading = true;

            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = lastTargetSet;

            EditorUtility.DisplayProgressBar("Export EXR", "Reading...", 0.25f);

            m_Target.enabled = lastPostFXState;
            m_Target.breakBeforeColorGrading = lastBreakColorGradingState;

            if(mode == ExportMode.BreakBeforeColorGradingLog) {
                // Convert to log
                var material = new Material(Shader.Find("Hidden/PostProcessing/Editor/ConvertToLog"));
                var newTarget = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Graphics.Blit(target, newTarget, material, 0);
                RenderTexture.ReleaseTemporary(target);
                DestroyImmediate(material);
                target = newTarget;
            }

            RenderTexture.active = target;
            texOut.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            texOut.Apply();
            RenderTexture.active = lastActive;

            EditorUtility.DisplayProgressBar("Export EXR", "Encoding...", 0.5f);

            var bytes = texOut.EncodeToEXR(EXRFlags.OutputAsFloat | EXRFlags.CompressZIP);

            EditorUtility.DisplayProgressBar("Export EXR", "Saving...", 0.75f);

            File.WriteAllBytes(path, bytes);

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();

            RenderTexture.ReleaseTemporary(target);
            DestroyImmediate(texOut);
        }
    }
}
