using System.Reflection;
using FluidSimulation.FluidSimulation.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidSimulation.FluidSimulation.Test
{
    public class FluidSimTestSplat : MonoBehaviour
    {
        public FluidSim FluidSim;
        public int SplatCount = 5;
        public float SplatInterval = 2f;
        float _timer;

        void Start()
        {
            if (!FluidSim)
                FluidSim = FindFirstObjectByType<FluidSim>();

            // Initial test splats
            DoTestSplats();
        }

        void Update()
        {
            // Use the new Input System for key press
            if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
                DoTestSplats();

            // Optionally, repeat splats every few seconds
            _timer += Time.deltaTime;
            if (_timer > SplatInterval)
            {
                _timer = 0f;
                DoTestSplats();
            }
        }

        void DoTestSplats()
        {
            if (FluidSim == null) return;

            for (var i = 0; i < SplatCount; i++)
            {
                Vector2 uv = new(Random.value, Random.value);
                Vector2 force = Random.insideUnitCircle * 1000f;
                Vector3 color = new Vector3(Random.value, Random.value, Random.value) * 0.5f;
                // Use reflection to call private EnqueueSplat
                MethodInfo method =
                    typeof(FluidSim).GetMethod("EnqueueSplat", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null)
                    method.Invoke(FluidSim, new object[] { uv, force, color });
            }

            Debug.Log("Test splats triggered.");
        }
    }
}