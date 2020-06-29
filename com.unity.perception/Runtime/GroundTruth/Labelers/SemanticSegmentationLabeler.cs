﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using Unity.Collections;
using Unity.Simulation;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
#if HDRP_PRESENT
using UnityEngine.Rendering.HighDefinition;
#endif

namespace UnityEngine.Perception.GroundTruth {
    [Serializable]
    public class SemanticSegmentationLabeler : CameraLabeler
    {
        const string k_SemanticSegmentationDirectory = "SemanticSegmentation";
        const string k_SegmentationFilePrefix = "segmentation_";

        public string annotationId = "12F94D8D-5425-4DEB-9B21-5E53AD957D66";
        /// <summary>
        /// The SemanticSegmentationLabelConfig to use for determining segmentation pixel values.
        /// </summary>
        public SemanticSegmentationLabelConfig labelConfig;

        public event Action<int,NativeArray<Color32>,RenderTexture> SemanticSegmentationImageReadback;

        [NonSerialized]
        internal RenderTexture semanticSegmentationTexture;

        AnnotationDefinition m_SemanticSegmentationAnnotationDefinition;
        RenderTextureReader<Color32> m_SemanticSegmentationTextureReader;

#if HDRP_PRESENT
        SemanticSegmentationPass m_SemanticSegmentationPass;
#endif

        Dictionary<int, AsyncAnnotation> m_AsyncAnnotations;

        public SemanticSegmentationLabeler()
        {
        }

        public SemanticSegmentationLabeler(SemanticSegmentationLabelConfig labelConfig)
        {
            this.labelConfig = labelConfig;
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        struct SemanticSegmentationSpec
        {
            [UsedImplicitly]
            public string label_name;
            [UsedImplicitly]
            public Color pixel_value;
        }

        struct AsyncSemanticSegmentationWrite
        {
            public NativeArray<Color32> data;
            public int width;
            public int height;
            public string path;
        }

        public override void Setup()
        {
            var myCamera = perceptionCamera.GetComponent<Camera>();
            var width = myCamera.pixelWidth;
            var height = myCamera.pixelHeight;

            if (labelConfig == null)
            {
                Debug.LogError("LabelingConfiguration must be set if producing ground truth data");
                this.enabled = false;
            }

            m_AsyncAnnotations = new Dictionary<int, AsyncAnnotation>();

            semanticSegmentationTexture = new RenderTexture(new RenderTextureDescriptor(width, height, GraphicsFormat.R8G8B8A8_UNorm, 8));
            semanticSegmentationTexture.name = "Labeling";

#if HDRP_PRESENT
            var gameObject = perceptionCamera.gameObject;
            var customPassVolume = gameObject.GetComponent<CustomPassVolume>() ?? gameObject.AddComponent<CustomPassVolume>();
            customPassVolume.injectionPoint = CustomPassInjectionPoint.BeforeRendering;
            customPassVolume.isGlobal = true;
            m_SemanticSegmentationPass = new SemanticSegmentationPass(myCamera, semanticSegmentationTexture, labelConfig)
            {
                name = "Labeling Pass"
            };
            customPassVolume.customPasses.Add(m_SemanticSegmentationPass);
#endif
#if URP_PRESENT
            perceptionCamera.AddScriptableRenderPass(new SemanticSegmentationUrpPass(myCamera, semanticSegmentationTexture, labelConfig));
#endif

            var specs = labelConfig.LabelEntries.Select((l) => new SemanticSegmentationSpec()
            {
                label_name = l.label,
                pixel_value = l.color
            }).ToArray();

            m_SemanticSegmentationAnnotationDefinition = SimulationManager.RegisterAnnotationDefinition("semantic segmentation", specs, "pixel-wise semantic segmentation label", "PNG");

            m_SemanticSegmentationTextureReader = new RenderTextureReader<Color32>(semanticSegmentationTexture, myCamera,
                (frameCount, data, tex) => OnSemanticSegmentationImageRead(frameCount, data));

            SimulationManager.SimulationEnding += Cleanup;
        }

        void OnSemanticSegmentationImageRead(int frameCount, NativeArray<Color32> data)
        {
            var dxLocalPath = Path.Combine(k_SemanticSegmentationDirectory, k_SegmentationFilePrefix) + frameCount + ".png";
            var path = Path.Combine(Manager.Instance.GetDirectoryFor(k_SemanticSegmentationDirectory), k_SegmentationFilePrefix) + frameCount + ".png";

            if (!m_AsyncAnnotations.TryGetValue(frameCount, out var annotation))
                return;

            annotation.ReportFile(dxLocalPath);

            var asyncRequest = Manager.Instance.CreateRequest<AsyncRequest<AsyncSemanticSegmentationWrite>>();
            SemanticSegmentationImageReadback?.Invoke(frameCount, data, semanticSegmentationTexture);
            asyncRequest.data = new AsyncSemanticSegmentationWrite()
            {
                data = new NativeArray<Color32>(data, Allocator.TempJob),
                width = semanticSegmentationTexture.width,
                height = semanticSegmentationTexture.height,
                path = path
            };
            asyncRequest.Start((r) =>
            {
                Profiler.EndSample();
                Profiler.BeginSample("Encode");
                var pngBytes = ImageConversion.EncodeArrayToPNG(r.data.data.ToArray(), GraphicsFormat.R8G8B8A8_UNorm, (uint)r.data.width, (uint)r.data.height);
                Profiler.EndSample();
                Profiler.BeginSample("WritePng");
                File.WriteAllBytes(r.data.path, pngBytes);
                Manager.Instance.ConsumerFileProduced(r.data.path);
                Profiler.EndSample();
                r.data.data.Dispose();
                return AsyncRequest.Result.Completed;
            });
        }

        public override void OnBeginRendering()
        {
            m_AsyncAnnotations[Time.frameCount] = perceptionCamera.SensorHandle.ReportAnnotationAsync(m_SemanticSegmentationAnnotationDefinition);
        }

        public override void Cleanup()
        {
            m_SemanticSegmentationTextureReader?.WaitForAllImages();
            m_SemanticSegmentationTextureReader?.Dispose();
            m_SemanticSegmentationTextureReader = null;
        }

        void OnDisable()
        {
            SimulationManager.SimulationEnding -= Cleanup;

            m_SemanticSegmentationTextureReader?.Dispose();
            m_SemanticSegmentationTextureReader = null;

            if (semanticSegmentationTexture != null)
                semanticSegmentationTexture.Release();

            semanticSegmentationTexture = null;
        }
    }
}
