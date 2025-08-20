using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FluidSimulation.FluidSimulation.Core
{
    public class FluidSimBlitFeature : ScriptableRendererFeature
    {
        // Cached shader property IDs for better performance
        static readonly int DyeTexId = Shader.PropertyToID("_DyeTex");
        static readonly int ApplyShadingId = Shader.PropertyToID("_ApplyShading");
        static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        static readonly int BackgroundId = Shader.PropertyToID("_Background");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Initialize()
        {
            // This method ensures proper initialization for the FluidSimBlitFeature
            // Required to resolve UDR0001 warning about missing RuntimeInitializeOnLoadMethod
            BlitPass.InitializePass();
        }

        class BlitPass : ScriptableRenderPass
        {
            readonly Material _blitMaterial;
            
            // Cached vectors to avoid allocations in render pass
            static Vector2 _cachedTexelSizeVector = Vector2.zero;

            // Static initializer to reset state on domain reload
            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
            internal static void InitializePass()
            {
                _cachedTexelSizeVector = Vector2.zero;
            }

            public BlitPass(Material material)
            {
                _blitMaterial = material;
            }

            [System.Obsolete]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                // Find the FluidSim component and get its OutputTexture
                FluidSim fluidSim = FindFirstObjectByType<FluidSim>();
                if (!fluidSim || !fluidSim.OutputTexture)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get("FluidSimBlitPass");
            
                // Blit FluidSim.OutputTexture to camera target
                RenderTargetIdentifier cameraTarget = BuiltinRenderTextureType.CameraTarget;
            
                if (_blitMaterial)
                {
                    // Set the dye texture on the material before blitting using cached property IDs
                    _blitMaterial.SetTexture(DyeTexId, fluidSim.OutputTexture);
                    _blitMaterial.SetInt(ApplyShadingId, fluidSim.Shading ? 1 : 0);
                    
                    // Use cached vector to avoid allocation
                    _cachedTexelSizeVector.x = 1f / fluidSim.OutputTexture.width;
                    _cachedTexelSizeVector.y = 1f / fluidSim.OutputTexture.height;
                    _blitMaterial.SetVector(TexelSizeId, _cachedTexelSizeVector);
                    
                    _blitMaterial.SetColor(BackgroundId, fluidSim.BackgroundColor);
                
                    cmd.Blit(fluidSim.OutputTexture, cameraTarget, _blitMaterial);
                }
                else
                {
                    // Raw blit without material
                    cmd.Blit(fluidSim.OutputTexture, cameraTarget);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            // Implement RecordRenderGraph for RenderGraph compatibility
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                // Find the FluidSim component and get its OutputTexture
                FluidSim fluidSim = FindFirstObjectByType<FluidSim>();
                if (!fluidSim || !fluidSim.OutputTexture)
                    return;

                using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("FluidSimBlitPass", out PassData passData);
                passData.BlitMaterial = _blitMaterial;
                passData.OutputTexture = fluidSim.OutputTexture;
                passData.Shading = fluidSim.Shading;
                passData.BackgroundColor = fluidSim.BackgroundColor;

                builder.SetRenderFunc((PassData data, UnsafeGraphContext _) =>
                {
                    if (data is null || !data.OutputTexture) return;

                    if (data.BlitMaterial)
                    {
                        // Set material properties before blitting using cached property IDs
                        data.BlitMaterial.SetTexture(DyeTexId, data.OutputTexture);
                        data.BlitMaterial.SetInt(ApplyShadingId, data.Shading ? 1 : 0);
                        
                        // Use cached vector to avoid allocation
                        _cachedTexelSizeVector.x = 1f / data.OutputTexture.width;
                        _cachedTexelSizeVector.y = 1f / data.OutputTexture.height;
                        data.BlitMaterial.SetVector(TexelSizeId, _cachedTexelSizeVector);
                        
                        data.BlitMaterial.SetColor(BackgroundId, data.BackgroundColor);
                        
                        // Use Graphics.Blit with null destination to render to the current render target
                        Graphics.Blit(data.OutputTexture, (RenderTexture)null, data.BlitMaterial);
                    }
                    else
                    {
                        Graphics.Blit(data.OutputTexture, (RenderTexture)null);
                    }
                });
            }

            class PassData
            {
                public Material BlitMaterial;
                public RenderTexture OutputTexture;
                public bool Shading;
                public Color BackgroundColor;
            }
        }

        [System.Serializable]
        public class FluidSimBlitSettings
        {
            public Material BlitMaterial;
        }

        public FluidSimBlitSettings Settings = new();
        BlitPass _blitPass;

        public override void Create()
        {
            _blitPass = new BlitPass(Settings.BlitMaterial)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Always enqueue the pass - it will check for FluidSim.OutputTexture internally
            renderer.EnqueuePass(_blitPass);
        }
    }
}