namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Applies relevant settings before rendering transparent objects
    /// </summary>

    internal class TransparentSettingsPass : ScriptableRenderPass
    {
        private bool m_ShouldReceiveShadows;
        private bool m_MainLightShadows;
        private bool m_MainLightShadowCascades;
        private bool m_AdditionalLightShadows;
        private Vector4 m_drawObjectPassData;

        private const string m_ProfilerTag = "Transparent Settings Pass";
        private static readonly int s_DrawObjectPassDataPropID = Shader.PropertyToID("_DrawObjectPassData");

        public TransparentSettingsPass(RenderPassEvent evt, bool shadowReceiveSupported)
        {
            renderPassEvent = evt;
            m_ShouldReceiveShadows = shadowReceiveSupported;
        }

        public bool Setup(ref RenderingData renderingData, bool mainLightShadows, bool additionalLightShadows, bool hasDepthTexture)
        {
            m_MainLightShadows          = m_ShouldReceiveShadows && mainLightShadows;
            m_AdditionalLightShadows    = m_ShouldReceiveShadows && additionalLightShadows;
            m_MainLightShadowCascades   = m_ShouldReceiveShadows && renderingData.shadowData.mainLightShadowCascadesCount > 1;

            // Global render pass data containing various settings.
            m_drawObjectPassData = new Vector4(
                0.0f,                           // Unused
                0.0f,                           // Unused
                hasDepthTexture ? 1.0f : 0.0f,  // Do we have access to a depth texture or not
                0.0f                            // Are these objects opaque(1) or alpha blended(0)
            );

            return true;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Get a command buffer...
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);

            // Toggle light shadows enabled based on the renderer setting set in the constructor
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadows, m_MainLightShadows);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadowCascades, m_MainLightShadowCascades);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightShadows, m_AdditionalLightShadows);

            // Send the correct draw data for the objects in drawn in this pass
            cmd.SetGlobalVector(s_DrawObjectPassDataPropID, m_drawObjectPassData);

            // Execute and release the command buffer...
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
