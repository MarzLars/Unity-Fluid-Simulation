using UnityEngine;

namespace FluidSimulation.FluidSimulation.Util
{
    public static class FluidUtil
    {
        public static void ClearRenderTexture(RenderTexture renderTexture, Color color)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = renderTexture;
            GL.Clear(true, true, color);
            RenderTexture.active = active;
        }
    }
}