using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Contains properties and helper functions that you can use when rendering.
    /// </summary>
    [MovedFrom("UnityEngine.Rendering.LWRP")] public static class RenderingUtils
    {
        static List<ShaderTagId> m_LegacyShaderPassNames = new List<ShaderTagId>()
        {
            new ShaderTagId("Always"),
            new ShaderTagId("ForwardBase"),
            new ShaderTagId("PrepassBase"),
            new ShaderTagId("Vertex"),
            new ShaderTagId("VertexLMRGBM"),
            new ShaderTagId("VertexLM"),
        };

        static Mesh s_FullscreenMesh = null;

        /// <summary>
        /// Returns a mesh that you can use with <see cref="CommandBuffer.DrawMesh(Mesh, Matrix4x4, Material)"/> to render full-screen effects.
        /// </summary>
        public static Mesh fullscreenMesh
        {
            get
            {
                if (s_FullscreenMesh != null)
                    return s_FullscreenMesh;

                float topV = 1.0f;
                float bottomV = 0.0f;

                s_FullscreenMesh = new Mesh { name = "Fullscreen Quad" };
                s_FullscreenMesh.SetVertices(new List<Vector3>
                {
                    new Vector3(-1.0f, -1.0f, 0.0f),
                    new Vector3(-1.0f,  1.0f, 0.0f),
                    new Vector3(1.0f, -1.0f, 0.0f),
                    new Vector3(1.0f,  1.0f, 0.0f)
                });

                s_FullscreenMesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0.0f, bottomV),
                    new Vector2(0.0f, topV),
                    new Vector2(1.0f, bottomV),
                    new Vector2(1.0f, topV)
                });

                s_FullscreenMesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0, false);
                s_FullscreenMesh.UploadMeshData(true);
                return s_FullscreenMesh;
            }
        }

        internal static bool useStructuredBuffer
        {
            // There are some performance issues with StructuredBuffers in some platforms.
            // We fallback to UBO in those cases.
            get
            {
                // TODO: For now disabling SSBO until figure out Vulkan binding issues.
                // When enabling this also enable it in shader side in Input.hlsl
                return false;

                // We don't use SSBO in D3D because we can't figure out without adding shader variants if platforms is D3D10.
                //GraphicsDeviceType deviceType = SystemInfo.graphicsDeviceType;
                //return !Application.isMobilePlatform &&
                //    (deviceType == GraphicsDeviceType.Metal || deviceType == GraphicsDeviceType.Vulkan ||
                //     deviceType == GraphicsDeviceType.PlayStation4 || deviceType == GraphicsDeviceType.XboxOne);
            }
            
        }

        static Material s_ErrorMaterial;
        static Material errorMaterial
        {
            get
            {
                if (s_ErrorMaterial == null)
                {
                    // TODO: When importing project, AssetPreviewUpdater::CreatePreviewForAsset will be called multiple times.
                    // This might be in a point that some resources required for the pipeline are not finished importing yet.
                    // Proper fix is to add a fence on asset import.
                    try
                    {
                        s_ErrorMaterial = new Material(Shader.Find("Hidden/Universal Render Pipeline/FallbackError"));
                    }
                    catch { }
                }

                return s_ErrorMaterial;
            }
        }

        /// <summary>
        /// Checks if the pipeline needs to create a intermediate render texture.
        /// </summary>
        /// <param name="cameraData">CameraData contains all relevant render target information for the camera.</param>
        /// <seealso cref="CameraData"/>
        /// <returns>Return true if pipeline needs to render to a intermediate render texture.</returns>
        public static bool RequiresIntermediateRenderTexture(ref CameraData cameraData)
        {
            // When rendering a camera stack we always create an intermediate render texture to composite camera results.
            // We create it upon rendering the Base camera.
            if (cameraData.renderType == CameraRenderType.Base && !cameraData.resolveFinalTarget)
                return true;

            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            int msaaSamples = cameraTargetDescriptor.msaaSamples;
            bool isStereoEnabled = cameraData.isStereoEnabled;
            bool isScaledRender = !Mathf.Approximately(cameraData.renderScale, 1.0f);
            bool isCompatibleBackbufferTextureDimension = cameraTargetDescriptor.dimension == TextureDimension.Tex2D;
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && !SystemInfo.supportsMultisampleAutoResolve;
            bool isOffscreenRender = cameraData.targetTexture != null && !cameraData.isSceneViewCamera;
            bool isCapturing = cameraData.captureActions != null;

#if ENABLE_VR && ENABLE_VR_MODULE
            if (isStereoEnabled)
                isCompatibleBackbufferTextureDimension = UnityEngine.XR.XRSettings.deviceEyeTextureDimension == cameraTargetDescriptor.dimension;
#endif

            bool requiresBlitForOffscreenCamera = cameraData.postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || cameraData.isSceneViewCamera || isScaledRender || cameraData.isHdrEnabled ||
                   !isCompatibleBackbufferTextureDimension || !cameraData.isDefaultViewport || isCapturing || Display.main.requiresBlitToBackbuffer;
        }


        /// <summary>
        /// Set camera matrices. This method will set <c>UNITY_MATRIX_V</c>, <c>UNITY_MATRIX_P</c>, <c>UNITY_MATRIX_VP</c> to camera matrices.
        /// Additionally this will also set <c>unity_CameraProjection</c> and <c>unity_CameraProjection</c>.
        /// If <c>setInverseMatrices</c> is set to true this function will also set <c>UNITY_MATRIX_I_V</c> and <c>UNITY_MATRIX_I_VP</c>.
        /// This function has no effect when rendering in stereo. When in stereo rendering you cannot override camera matrices.
        /// If you need to set general purpose view and projection matrices call <see cref="SetViewAndProjectionMatrices(CommandBuffer, Matrix4x4, Matrix4x4, bool)"/> instead.
        /// </summary>
        /// <param name="cmd">CommandBuffer to submit data to GPU.</param>
        /// <param name="cameraData">CameraData containing camera matrices information.</param>
        /// <param name="setInverseMatrices">Set this to true if you also need to set inverse camera matrices.</param>
        public static void SetCameraMatrices(CommandBuffer cmd, ref CameraData cameraData, bool setInverseMatrices)
        {
            // We cannot override camera matrices in VR. They are set using context.SetupCameraProperties until XR Pure SDK lands.
            if (cameraData.isStereoEnabled)
                return;

            Matrix4x4 viewMatrix = cameraData.viewMatrix;

            // Note: CameraData.projectionMatrix is computed with GL.GetGPUProjecitonMatrix.
            // This account for platform specific y and z direction differences in OpenGL vs other platforms.
            Matrix4x4 projectionMatrix = cameraData.projectionMatrix;

            SetViewAndProjectionMatrices(cmd, viewMatrix, projectionMatrix, setInverseMatrices);
            cmd.SetGlobalMatrix(ShaderPropertyId.worldToCameraMatrix, viewMatrix);

            if (setInverseMatrices)
            {
                Matrix4x4 inverseViewMatrix = Matrix4x4.Inverse(viewMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.cameraToWorldMatrix, inverseViewMatrix);
            }
        }

        /// <summary>
        /// Set view and projection matrices.
        /// This function will set <c>UNITY_MATRIX_V</c>, <c>UNITY_MATRIX_P</c>, <c>UNITY_MATRIX_VP</c> to given view and projection matrices.
        /// If <c>setInverseMatrices</c> is set to true this function will also set <c>UNITY_MATRIX_I_V</c> and <c>UNITY_MATRIX_I_VP</c>.
        /// </summary>
        /// <param name="cmd">CommandBuffer to submit data to GPU.</param>
        /// <param name="viewMatrix">View matrix to be set.</param>
        /// <param name="projectionMatrix">Projection matrix to be set.</param>
        /// <param name="setInverseMatrices">Set this to true if you also need to set inverse camera matrices.</param>
        public static void SetViewAndProjectionMatrices(CommandBuffer cmd, Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix, bool setInverseMatrices)
        {
            Matrix4x4 viewAndProjectionMatrix = projectionMatrix * viewMatrix;
            cmd.SetGlobalMatrix(ShaderPropertyId.viewMatrix, viewMatrix);
            cmd.SetGlobalMatrix(ShaderPropertyId.projectionMatrix, projectionMatrix);
            cmd.SetGlobalMatrix(ShaderPropertyId.viewAndProjectionMatrix, viewAndProjectionMatrix);

            if (setInverseMatrices)
            {
                Matrix4x4 inverseMatrix = Matrix4x4.Inverse(viewMatrix);
                // Note: inverse projection is currently undefined
                Matrix4x4 inverseViewProjection = Matrix4x4.Inverse(viewAndProjectionMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewMatrix, inverseMatrix);
                cmd.SetGlobalMatrix(ShaderPropertyId.inverseViewAndProjectionMatrix, inverseViewProjection);
            }
        }

        /// <summary>
        /// Returns true if the projection matrix is y-flipped.
        /// Unity renders with a flip projection matrix if rendering to a render texture in non OpengGL platforms.
        /// </summary>
        /// <param name="projectionMatrix">The projection matrix.</param>
        /// <returns>True if the projection matrix if y-flipped.</returns>
        public static float GetProjectionFlipSign(Matrix4x4 projectionMatrix)
        {
            return Mathf.Sign(projectionMatrix.GetColumn(1).y);
        }

        // This is used to render materials that contain built-in shader passes not compatible with URP. 
        // It will render those legacy passes with error/pink shader.
        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal static void RenderObjectsWithError(ScriptableRenderContext context, ref CullingResults cullResults, Camera camera, FilteringSettings filterSettings, SortingCriteria sortFlags)
        {
            // TODO: When importing project, AssetPreviewUpdater::CreatePreviewForAsset will be called multiple times.
            // This might be in a point that some resources required for the pipeline are not finished importing yet.
            // Proper fix is to add a fence on asset import.
            if (errorMaterial == null)
                return;
            
            SortingSettings sortingSettings = new SortingSettings(camera) { criteria = sortFlags };
            DrawingSettings errorSettings = new DrawingSettings(m_LegacyShaderPassNames[0], sortingSettings)
            {
                perObjectData = PerObjectData.None,
                overrideMaterial = errorMaterial,
                overrideMaterialPassIndex = 0
            };
            for (int i = 1; i < m_LegacyShaderPassNames.Count; ++i)
                errorSettings.SetShaderPassName(i, m_LegacyShaderPassNames[i]);

            context.DrawRenderers(cullResults, ref errorSettings, ref filterSettings);
        }

        // Caches render texture format support. SystemInfo.SupportsRenderTextureFormat allocates memory due to boxing.
        static Dictionary<RenderTextureFormat, bool> m_RenderTextureFormatSupport = new Dictionary<RenderTextureFormat, bool>();

        internal static void ClearSystemInfoCache()
        {
            m_RenderTextureFormatSupport.Clear();
        }

        /// <summary>
        /// Checks if a render texture format is supported by the run-time system.
        /// Similar to <see cref="SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat)"/>, but doesn't allocate memory.
        /// </summary>
        /// <param name="format">The format to look up.</param>
        /// <returns>Returns true if the graphics card supports the given <c>RenderTextureFormat</c></returns>
        public static bool SupportsRenderTextureFormat(RenderTextureFormat format)
        {
            if (!m_RenderTextureFormatSupport.TryGetValue(format, out var support))
            {
                support = SystemInfo.SupportsRenderTextureFormat(format);
                m_RenderTextureFormatSupport.Add(format, support);
            }

            return support;
        }

        /// <summary>
        /// Return the last colorBuffer index actually referring to an existing RenderTarget
        /// </summary>
        /// <param name="colorBuffers"></param>
        /// <returns></returns>
        internal static int GetLastValidColorBufferIndex(RenderTargetIdentifier[] colorBuffers)
        {
            int i = colorBuffers.Length - 1;
            for(; i>=0; --i)
            {
                if (colorBuffers[i] != 0)
                    break;
            }
            return i;
        }

        /// <summary>
        /// Return the number of items in colorBuffers actually referring to an existing RenderTarget
        /// </summary>
        /// <param name="colorBuffers"></param>
        /// <returns></returns>
        internal static uint GetValidColorBufferCount(RenderTargetIdentifier[] colorBuffers)
        {
            uint nonNullColorBuffers = 0;
            if (colorBuffers != null)
            {
                foreach (var identifier in colorBuffers)
                {
                    if (identifier != 0)
                        ++nonNullColorBuffers;
                }
            }
            return nonNullColorBuffers;
        }

        /// <summary>
        /// Return true if colorBuffers is an actual MRT setup
        /// </summary>
        /// <param name="colorBuffers"></param>
        /// <returns></returns>
        internal static bool IsMRT(RenderTargetIdentifier[] colorBuffers)
        {
            return GetValidColorBufferCount(colorBuffers) > 1;
        }

        /// <summary>
        /// Return true if value can be found in source (without recurring to Linq)
        /// </summary>
        /// <param name="source"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static bool Contains(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            foreach (var identifier in source)
            {
                if (identifier == value)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Return the index where value was found source. Otherwise, return -1. (without recurring to Linq)
        /// </summary>
        /// <param name="source"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static int IndexOf(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i] == value)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Return the number of RenderTargetIdentifiers in "source" that are valid (not 0) and different from "value" (without recurring to Linq)
        /// </summary>
        /// <param name="source"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static uint CountDistinct(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            uint count = 0;
            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i] != value && source[i] != 0)
                    ++count;
            }
            return count;
        }

        /// <summary>
        /// Return the index of last valid (i.e different from 0) RenderTargetIdentifiers in "source" (without recurring to Linq)
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        internal static int LastValid(RenderTargetIdentifier[] source)
        {
            for (int i = source.Length-1; i >= 0; --i)
            {
                if (source[i] != 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Return true if ClearFlag a contains ClearFlag b
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        internal static bool Contains(ClearFlag a, ClearFlag b)
        {
            return (a & b) == b;
        }

        /// <summary>
        /// Return true if "left" and "right" are the same (without recurring to Linq)
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        internal static bool SequenceEqual(RenderTargetIdentifier[] left, RenderTargetIdentifier[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; ++i)
                if (left[i] != right[i])
                    return false;

            return true;
        }
    }
}
