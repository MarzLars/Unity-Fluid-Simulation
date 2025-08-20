/*
MIT License

Copyright (c) 2017 Pavel Dobryakov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Collections.Generic;
using System.Text;
using FluidSimulation.FluidSimulation.Util;
using UnityEngine;
using UnityEngine.InputSystem;
using Random = UnityEngine.Random;

namespace FluidSimulation.FluidSimulation.Core
{
    [ExecuteAlways]
    public class FluidSim : MonoBehaviour
    {
        const int ThreadSize = 8;
        static readonly int IsVelocity = Shader.PropertyToID("_IsVelocity");

        [Header("Resolutions")]
        public int SimResolution = 1024; // velocity / pressure resolution
        public int DyeResolution = 1024; // color/density resolution

        [Header("Dissipation / Dynamics")]
        [Range(0.0f, 4f)] public float DensityDissipation = 1.0f;
        [Range(0.0f, 4f)] public float VelocityDissipation = 0.2f;
        [Range(0, 50f)] public float CurlStrength = 30f;
        [Range(0f, 1f)]
        public float PressureDamping = 0.8f; // like PRESSURE in original (used to clear existing pressure)
        [Range(1, 80)] public int PressureIterations = 20;

        [Header("Splats")]
        [Range(0.001f, 1f)] public float SplatRadius = 0.25f;
        public float SplatForce = 6000f;
        public bool Colorful = true;
        public float ColorUpdateSpeed = 10f;

        [Header("Visual")]
        public bool Shading = true; // approximate shading like original
        public Color BackgroundColor = Color.black;

        [Header("Time Control")]
        public bool Paused;
        public float FixedDeltaClamp = 1f / 60f;

        [Header("Shaders / Compute")]
        public ComputeShader AdvectCs;
        public ComputeShader CurlCs;
        public ComputeShader VorticityCs;
        public ComputeShader DivergenceCs;
        public ComputeShader PressureCs;
        public ComputeShader GradientSubtractCs;
        public ComputeShader SplatCs;
        public ComputeShader CopyCs;
        public Material DisplayMaterial;

        [Header("Debug")]
        public bool VerboseDebug;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // StringBuilder for debug logging to avoid string allocations (debug builds only)
        static readonly StringBuilder DebugStringBuilder = new(256);

        // Helper methods to avoid boxing when appending value types to StringBuilder
        static void AppendInt(StringBuilder sb, int value)
        {
            // Avoid boxing by using ToString() directly on the value type
            sb.Append(value.ToString());
        }

        static void AppendBool(StringBuilder stringBuilder, bool value)
        {
            // Use string literals to avoid boxing
            stringBuilder.Append(value ? "True" : "False");
        }
#endif

        // Cached vectors for compute shader parameters to avoid allocations
        Vector2 _cachedResolutionVector = Vector2.zero;
        Vector2 _cachedTexelSizeVector = Vector2.zero;

        [Header("URP Output")]
        [Tooltip(
            "Optional: assign a RenderTexture in the inspector to use for URP output. If left empty, the sim may create one at runtime.")]
        public RenderTexture OutputTexture;

        Camera _cam;

        float _colorTimer;
        RenderTexture _curl;
        Vector3 _currentPointerColor = new(0.15f, 0.15f, 0.15f);
        RenderTexture _divergence;
        DoubleRenderTexture _dye;
        readonly int _idAspectRatio = Shader.PropertyToID("_AspectRatio");
        readonly int _idCurl = Shader.PropertyToID("_Curl");
        readonly int _idCurlTex = Shader.PropertyToID("_CurlTex");
        readonly int _idDissipation = Shader.PropertyToID("_Dissipation");
        readonly int _idDivergenceTex = Shader.PropertyToID("_DivergenceTex");
        readonly int _idDt = Shader.PropertyToID("_Dt");
        readonly int _idForce = Shader.PropertyToID("_Force");
        readonly int _idPressureTex = Shader.PropertyToID("_PressureTex");

        readonly int _idResolution = Shader.PropertyToID("_Resolution");
        readonly int _idSourceTex = Shader.PropertyToID("_SourceTex");
        readonly int _idSplatColor = Shader.PropertyToID("_SplatColor");
        readonly int _idSplatPoint = Shader.PropertyToID("_SplatPoint");
        readonly int _idSplatRadius = Shader.PropertyToID("_SplatRadius");
        readonly int _idTarget = Shader.PropertyToID("_Target");
        readonly int _idValue = Shader.PropertyToID("_Value");
        readonly int _idVelocityTex = Shader.PropertyToID("_VelocityTex");
        readonly int _idDyeTex = Shader.PropertyToID("_DyeTex");
        readonly int _idApplyShading = Shader.PropertyToID("_ApplyShading");
        readonly int _idTexelSize = Shader.PropertyToID("_TexelSize");
        readonly int _idBackground = Shader.PropertyToID("_Background");

        // Struct to avoid boxing when enqueueing splats
        readonly struct SplatData
        {
            public readonly Vector2 Uv;
            public readonly Vector2 Force;
            public readonly Vector3 Color;

            public SplatData(Vector2 uv, Vector2 force, Vector3 color)
            {
                Uv = uv;
                Force = force;
                Color = color;
            }
        }

        // Tracks whether this component created the OutputTexture at runtime (so we can safely Release it)
        bool _outputTextureCreatedBySim;
        readonly List<SplatData> _pendingSplats = new();
        DoubleRenderTexture _pressureRenderTexture;
        DoubleRenderTexture _velocity;

        #region Unity Methods

        void OnEnable()
        {
            _cam = Camera.main;
            AllocateAll();
            var amount = 5 + Random.Range(0, 20);
            RandomSplats(amount);
        }

        void OnDisable()
        {
            ReleaseAll();
        }

        void Update()
        {
            if (Application.isPlaying && Paused) return;

            var dt = Mathf.Min(Time.deltaTime, FixedDeltaClamp);
            if (Application.isEditor && !Application.isPlaying)
                dt = FixedDeltaClamp;

            // Handle resize (in case values changed)
            if (!_velocity.Read || _velocity.Width != SimResolution || _dye.Width != DyeResolution)
            {
                ReleaseAll();
                AllocateAll();
            }

            UpdateColors(dt);
            ApplyPendingSplats();

            if (!Paused)
                Step(dt);
        }

        void LateUpdate()
        {
            HandleMouseInput();
            if (!_dye.Read || !OutputTexture) return;
            if (DisplayMaterial)
            {
                // Ensure the material has the dye texture and visual params set when used in URP
                DisplayMaterial.SetTexture(_idDyeTex, _dye.Read);
                DisplayMaterial.SetInt(_idApplyShading, Shading ? 1 : 0);

                // Use cached vector to avoid allocation
                _cachedTexelSizeVector.x = 1f / _dye.Read.width;
                _cachedTexelSizeVector.y = 1f / _dye.Read.height;
                DisplayMaterial.SetVector(_idTexelSize, _cachedTexelSizeVector);

                DisplayMaterial.SetColor(_idBackground, BackgroundColor);

                Graphics.Blit(_dye.Read, OutputTexture, DisplayMaterial);
            }
            else
            {
                // Fallback: blit the raw dye texture if no display material is assigned
                Graphics.Blit(_dye.Read, OutputTexture);
            }
        }

        #endregion

        #region Render Logic

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (!DisplayMaterial || !_dye.Read)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (VerboseDebug)
                {
                    Debug.Log("OnRenderImage fallback: " +
                              "DisplayMaterial=" + !DisplayMaterial + ", _dye.Read=" + !_dye.Read);
                    if (_dye.Read)
                        Debug.Log($"_dye.Read size: {_dye.Read.width}x{_dye.Read.height}");
                }
#endif
                Graphics.Blit(source, destination);
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug)
                Debug.Log($"OnRenderImage: Blitting dye texture {_dye.Read.width}x{_dye.Read.height} to screen.");
#endif

            DisplayMaterial.SetTexture(_idDyeTex, _dye.Read);
            DisplayMaterial.SetInt(_idApplyShading, Shading ? 1 : 0);

            // Use cached vector to avoid allocation
            _cachedTexelSizeVector.x = 1f / _dye.Read.width;
            _cachedTexelSizeVector.y = 1f / _dye.Read.height;
            DisplayMaterial.SetVector(_idTexelSize, _cachedTexelSizeVector);

            DisplayMaterial.SetColor(_idBackground, BackgroundColor);

            Graphics.Blit(null, destination, DisplayMaterial);
        }

        void Step(float deltaTime)
        {
            // Vorticity (curl)
            ComputeCurl();
            ApplyVorticity(deltaTime);

            // Divergence
            ComputeDivergence();

            // Clear pressure / apply damping
            ClearPressure();

            // Jacobi pressure iterations
            SolvePressure();

            // Subtract gradient
            SubtractGradient();

            // Advect velocity
            Advect(ref _velocity, ref _velocity, VelocityDissipation, deltaTime, true);

            // Advect dye
            Advect(ref _velocity, ref _dye, DensityDissipation, deltaTime, false);
        }

        #endregion

        #region Pass Implementations

        void Advect(ref DoubleRenderTexture velocityField, ref DoubleRenderTexture quantity, float dissipation,
            float deltaTime, bool isVelocity)
        {
            ComputeShader advectComputeShader = AdvectCs;
            var kernel = advectComputeShader.FindKernel("Advect");

            // Use cached vector to avoid allocation
            _cachedResolutionVector.x = quantity.Read.width;
            _cachedResolutionVector.y = quantity.Read.height;
            advectComputeShader.SetVector(_idResolution, _cachedResolutionVector);

            advectComputeShader.SetFloat(_idDt, deltaTime);
            advectComputeShader.SetFloat(_idDissipation, dissipation);
            advectComputeShader.SetTexture(kernel, _idVelocityTex, velocityField.Read);
            advectComputeShader.SetTexture(kernel, _idSourceTex, quantity.Read);
            advectComputeShader.SetTexture(kernel, _idTarget, quantity.Write);
            advectComputeShader.SetInt(IsVelocity, isVelocity ? 1 : 0);
            Dispatch(advectComputeShader, kernel, quantity.Read.width, quantity.Read.height);
            quantity.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Optimized debug logging to avoid boxing
            if (!VerboseDebug) return;
            DebugStringBuilder.Clear();
            DebugStringBuilder.Append("Advect completed (isVelocity=");
            AppendBool(DebugStringBuilder, isVelocity);
            DebugStringBuilder.Append("). New quantity.Read = ");
            if (quantity.Read)
            {
                AppendInt(DebugStringBuilder, quantity.Read.width);
                DebugStringBuilder.Append("x");
                AppendInt(DebugStringBuilder, quantity.Read.height);
            }
            else
            {
                DebugStringBuilder.Append("null");
            }

            Debug.Log(DebugStringBuilder.ToString());
#endif
        }

        void ComputeCurl()
        {
            var kernel = CurlCs.FindKernel("Curl");

            // Use cached vector to avoid allocation
            _cachedResolutionVector.x = _velocity.Read.width;
            _cachedResolutionVector.y = _velocity.Read.height;
            CurlCs.SetVector(_idResolution, _cachedResolutionVector);

            CurlCs.SetTexture(kernel, _idVelocityTex, _velocity.Read);
            CurlCs.SetTexture(kernel, _idTarget, _curl);
            Dispatch(CurlCs, kernel, _velocity.Read.width, _velocity.Read.height);
        }

        void ApplyVorticity(float dt)
        {
            var kernel = VorticityCs.FindKernel("Vorticity");

            // Use cached vector to avoid allocation
            _cachedResolutionVector.x = _velocity.Read.width;
            _cachedResolutionVector.y = _velocity.Read.height;
            VorticityCs.SetVector(_idResolution, _cachedResolutionVector);

            VorticityCs.SetTexture(kernel, _idVelocityTex, _velocity.Read);
            VorticityCs.SetTexture(kernel, _idCurlTex, _curl);
            VorticityCs.SetFloat(_idCurl, CurlStrength);
            VorticityCs.SetFloat(_idDt, dt);
            VorticityCs.SetTexture(kernel, _idTarget, _velocity.Write);
            Dispatch(VorticityCs, kernel, _velocity.Read.width, _velocity.Read.height);
            _velocity.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Optimized debug logging
            if (!VerboseDebug) return;
            DebugStringBuilder.Clear();
            DebugStringBuilder.Append("ApplyVorticity completed. _velocity.Read = ");
            AppendInt(DebugStringBuilder, _velocity.Read.width);
            DebugStringBuilder.Append("x");
            AppendInt(DebugStringBuilder, _velocity.Read.height);
            Debug.Log(DebugStringBuilder.ToString());
#endif
        }

        void ComputeDivergence()
        {
            var kernel = DivergenceCs.FindKernel("Divergence");

            // Use cached vector to avoid allocation
            _cachedResolutionVector.x = _velocity.Read.width;
            _cachedResolutionVector.y = _velocity.Read.height;
            DivergenceCs.SetVector(_idResolution, _cachedResolutionVector);

            DivergenceCs.SetTexture(kernel, _idVelocityTex, _velocity.Read);
            DivergenceCs.SetTexture(kernel, _idTarget, _divergence);
            Dispatch(DivergenceCs, kernel, _velocity.Read.width, _velocity.Read.height);
        }

        void ClearPressure()
        {
            var kernel = CopyCs.FindKernel("Multiply");
            CopyCs.SetTexture(kernel, _idSourceTex, _pressureRenderTexture.Read);
            CopyCs.SetFloat(_idValue, PressureDamping);
            CopyCs.SetTexture(kernel, _idTarget, _pressureRenderTexture.Write);
            Dispatch(CopyCs, kernel, _pressureRenderTexture.Read.width, _pressureRenderTexture.Read.height);
            _pressureRenderTexture.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!VerboseDebug) return;
            DebugStringBuilder.Clear();
            DebugStringBuilder.Append("ClearPressure completed. _pressureRT.Read = ");
            DebugStringBuilder.Append(_pressureRenderTexture.Read.width);
            DebugStringBuilder.Append("x");
            DebugStringBuilder.Append(_pressureRenderTexture.Read.height);
            Debug.Log(DebugStringBuilder.ToString());
#endif
        }

        void SolvePressure()
        {
            var kernel = PressureCs.FindKernel("PressureJacobi");
            for (var i = 0; i < PressureIterations; i++)
            {
                // Use cached vector to avoid allocation
                _cachedResolutionVector.x = _pressureRenderTexture.Read.width;
                _cachedResolutionVector.y = _pressureRenderTexture.Read.height;
                PressureCs.SetVector(_idResolution, _cachedResolutionVector);

                PressureCs.SetTexture(kernel, _idPressureTex, _pressureRenderTexture.Read);
                PressureCs.SetTexture(kernel, _idDivergenceTex, _divergence);
                PressureCs.SetTexture(kernel, _idTarget, _pressureRenderTexture.Write);
                Dispatch(PressureCs, kernel, _pressureRenderTexture.Read.width, _pressureRenderTexture.Read.height);
                _pressureRenderTexture.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Optimized debug logging
                if (!VerboseDebug) continue;
                DebugStringBuilder.Clear();
                DebugStringBuilder.Append("Pressure iteration ");
                DebugStringBuilder.Append(i);
                DebugStringBuilder.Append(" completed. _pressureRT.Read = ");
                DebugStringBuilder.Append(_pressureRenderTexture.Read.width);
                DebugStringBuilder.Append("x");
                DebugStringBuilder.Append(_pressureRenderTexture.Read.height);
                Debug.Log(DebugStringBuilder.ToString());
#endif
            }
        }

        void SubtractGradient()
        {
            var kernel = GradientSubtractCs.FindKernel("GradientSubtract");

            // Use cached vector to avoid allocation
            _cachedResolutionVector.x = _velocity.Read.width;
            _cachedResolutionVector.y = _velocity.Read.height;
            GradientSubtractCs.SetVector(_idResolution, _cachedResolutionVector);

            GradientSubtractCs.SetTexture(kernel, _idVelocityTex, _velocity.Read);
            GradientSubtractCs.SetTexture(kernel, _idPressureTex, _pressureRenderTexture.Read);
            GradientSubtractCs.SetTexture(kernel, _idTarget, _velocity.Write);
            Dispatch(GradientSubtractCs, kernel, _velocity.Read.width, _velocity.Read.height);
            _velocity.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Optimized debug logging
            if (!VerboseDebug) return;
            DebugStringBuilder.Clear();
            DebugStringBuilder.Append("SubtractGradient completed. _velocity.Read = ");
            DebugStringBuilder.Append(_velocity.Read.width);
            DebugStringBuilder.Append("x");
            DebugStringBuilder.Append(_velocity.Read.height);
            Debug.Log(DebugStringBuilder.ToString());
#endif
        }

        void DoSplat(Vector2 uv, Vector2 force, Vector3 color)
        {
            // Velocity splat
            {
                var kernel = SplatCs.FindKernel("SplatVelocity");

                // Use cached vector to avoid allocation
                _cachedResolutionVector.x = _velocity.Read.width;
                _cachedResolutionVector.y = _velocity.Read.height;
                SplatCs.SetVector(_idResolution, _cachedResolutionVector);

                SplatCs.SetTexture(kernel, _idVelocityTex, _velocity.Read);
                SplatCs.SetTexture(kernel, _idTarget, _velocity.Write);
                SplatCs.SetVector(_idSplatPoint, uv);
                SplatCs.SetVector(_idForce, force);
                SplatCs.SetFloat(_idSplatRadius, CorrectRadius(SplatRadius));
                SplatCs.SetFloat(_idAspectRatio, AspectRatio());
                Dispatch(SplatCs, kernel, _velocity.Read.width, _velocity.Read.height);
                _velocity.Swap();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Optimized debug logging
                if (VerboseDebug)
                {
                    DebugStringBuilder.Clear();
                    DebugStringBuilder.Append("SplatVelocity completed. _velocity.Read = ");
                    DebugStringBuilder.Append(_velocity.Read.width);
                    DebugStringBuilder.Append("x");
                    DebugStringBuilder.Append(_velocity.Read.height);
                    Debug.Log(DebugStringBuilder.ToString());
                }
#endif
            }

            // Dye splat
            {
                var kernel = SplatCs.FindKernel("SplatDye");

                // Use cached vector to avoid allocation
                _cachedResolutionVector.x = _dye.Read.width;
                _cachedResolutionVector.y = _dye.Read.height;
                SplatCs.SetVector(_idResolution, _cachedResolutionVector);

                SplatCs.SetTexture(kernel, _idSourceTex, _dye.Read);
                SplatCs.SetTexture(kernel, _idTarget, _dye.Write);
                SplatCs.SetVector(_idSplatPoint, uv);
                SplatCs.SetVector(_idSplatColor, color);
                SplatCs.SetFloat(_idSplatRadius, CorrectRadius(SplatRadius));
                SplatCs.SetFloat(_idAspectRatio, AspectRatio());
                Dispatch(SplatCs, kernel, _dye.Read.width, _dye.Read.height);
                _dye.Swap();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Optimized debug logging
                if (!VerboseDebug) return;
                DebugStringBuilder.Clear();
                DebugStringBuilder.Append("SplatDye completed. _dye.Read = ");
                DebugStringBuilder.Append(_dye.Read.width);
                DebugStringBuilder.Append("x");
                DebugStringBuilder.Append(_dye.Read.height);
                Debug.Log(DebugStringBuilder.ToString());
#endif
            }
        }

        #endregion

        #region Allocation / Release

        void AllocateAll()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug) Debug.Log("AllocateAll: allocating render textures...");
#endif
            AllocDouble(ref _velocity, SimResolution, SimResolution, RenderTextureFormat.RGHalf);
            AllocDouble(ref _dye, DyeResolution, DyeResolution, RenderTextureFormat.ARGBHalf);
            AllocDouble(ref _pressureRenderTexture, SimResolution, SimResolution, RenderTextureFormat.RHalf);
            _divergence = AllocSingle(SimResolution, SimResolution, RenderTextureFormat.RHalf);
            _curl = AllocSingle(SimResolution, SimResolution, RenderTextureFormat.RHalf);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug)
                Debug.Log(
                    $"AllocateAll: _velocity.Read = {_velocity.Read.width}x{_velocity.Read.height}, _dye.Read = {_dye.Read.width}x{_dye.Read.height}, _pressureRT.Read = {_pressureRenderTexture.Read.width}x{_pressureRenderTexture.Read.height}");
#endif
        }

        void ReleaseAll()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug) Debug.Log("ReleaseAll: releasing render textures...");
#endif
            ReleaseDouble(_velocity);
            ReleaseDouble(_dye);
            ReleaseDouble(_pressureRenderTexture);
            SafeRelease(_divergence);
            SafeRelease(_curl);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug) Debug.Log("ReleaseAll: release completed");
#endif
        }

        void AllocDouble(ref DoubleRenderTexture doubleRenderTexture, int width, int height, RenderTextureFormat format)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug) Debug.Log($"AllocDouble: creating double RT {width}x{height} format={format}");
#endif
            doubleRenderTexture.Read = CreateRenderTexture(width, height, format);
            doubleRenderTexture.Write = CreateRenderTexture(width, height, format);
            doubleRenderTexture.Width = width;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug)
                Debug.Log(
                    $"AllocDouble: created Read={doubleRenderTexture.Read.width}x{doubleRenderTexture.Read.height} Write={doubleRenderTexture.Write.width}x{doubleRenderTexture.Write.height}");
#endif
        }

        void ReleaseDouble(DoubleRenderTexture doubleRenderTexture)
        {
            SafeRelease(doubleRenderTexture.Read);
            SafeRelease(doubleRenderTexture.Write);
        }

        static RenderTexture AllocSingle(int width, int height, RenderTextureFormat format)
        {
            return CreateRenderTexture(width, height, format);
        }

        static RenderTexture CreateRenderTexture(int width, int height, RenderTextureFormat format)
        {
            RenderTexture renderTexture = new(width, height, 0, format, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                anisoLevel = 0
            };
            renderTexture.Create();
            // Clear to zero
            FluidUtil.ClearRenderTexture(renderTexture, Color.clear);

            return renderTexture;
        }

        static void SafeRelease(RenderTexture renderTexture)
        {
            renderTexture?.Release();
        }

        #endregion

        #region Input / Splats / Colors

        void HandleMouseInput()
        {
            if (!_cam) return;

            // Use the new Input System's Mouse class
            Mouse mouse = Mouse.current;
            if (mouse is null) return;

            if (!mouse.leftButton.isPressed) return;
            Vector2 mousePos = mouse.position.ReadValue();
            // Unity's screen Y origin is bottom-left, while many texture/UV conventions used
            // by the fluid simulation expect (0,0) at the top-left or the opposite mapping.
            // The symptom (touch top -> effect at bottom) means we need to flip Y when
            // converting screen coordinates to simulation UVs.
            Vector2 uv = new(mousePos.x / Screen.width, 1f - mousePos.y / Screen.height);

            // Calculate mouse delta (scale it properly like in JS reference)
            Vector2 mouseDelta = mouse.delta.ReadValue();

            // Invert the Y component of the force so dragging/swiping produces
            // movement in the expected screen direction after the UV flip.
            Vector2 force = new Vector2(mouseDelta.x, -mouseDelta.y) * (SplatForce * 0.001f);

            Vector3 color = _currentPointerColor;
            EnqueueSplat(uv, force, color);
        }

        void EnqueueSplat(Vector2 uv, Vector2 force, Vector3 color)
        {
            _pendingSplats.Add(new SplatData(uv, force, color));
        }

        void ApplyPendingSplats()
        {
            if (_pendingSplats.Count == 0) return;
            foreach (SplatData s in _pendingSplats) DoSplat(s.Uv, s.Force, s.Color);
            _pendingSplats.Clear();
        }

        void RandomSplats(int amount)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (VerboseDebug) Debug.Log($"RandomSplats: enqueueing {amount} splats");
#endif
            for (var i = 0; i < amount; i++)
            {
                Vector2 uv = new(Random.value, Random.value);
                Vector2 force = 1000f * new Vector2(Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f));
                Vector3 c = RandomColor() * 10f;
                EnqueueSplat(uv, force, c);
            }
        }

        void UpdateColors(float dt)
        {
            if (!Colorful) return;
            _colorTimer += dt * ColorUpdateSpeed;
            if (!(_colorTimer >= 1f)) return;
            _colorTimer -= Mathf.Floor(_colorTimer);
            _currentPointerColor = RandomColor() * 0.15f;
        }

        static Vector3 RandomColor()
        {
            Color hsvToRGB = Color.HSVToRGB(Random.value, 1f, 1f);
            return new Vector3(hsvToRGB.r, hsvToRGB.g, hsvToRGB.b);
        }

        static float AspectRatio()
        {
            return (float)Screen.width / Screen.height;
        }

        static float CorrectRadius(float radius)
        {
            var aspect = AspectRatio();
            if (aspect > 1f) radius *= aspect;
            return radius;
        }

        #endregion
        
        #region Utility

        static void Dispatch(ComputeShader computeShader, int kernel, int width, int height)
        {
            var threadGroupsX = (width + ThreadSize - 1) / ThreadSize;
            var threadGroupsY = (height + ThreadSize - 1) / ThreadSize;
            computeShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
        }

        struct DoubleRenderTexture
        {
            public RenderTexture Read;
            public RenderTexture Write;
            public int Width;

            public void Swap()
            {
                (Read, Write) = (Write, Read);
            }
        }

        #endregion
    }
}