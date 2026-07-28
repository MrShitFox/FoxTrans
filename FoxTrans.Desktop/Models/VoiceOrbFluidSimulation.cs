namespace FoxTrans.Desktop.Models;

internal sealed class VoiceOrbFluidSimulation
{
    internal const int Resolution = 96;
    private const int Stride = Resolution + 2;
    private const int CellCount = Stride * Stride;
    private const int PressureIterations = 14;

    private readonly float[] _horizontal = new float[CellCount];
    private readonly float[] _vertical = new float[CellCount];
    private readonly float[] _horizontalPrevious = new float[CellCount];
    private readonly float[] _verticalPrevious = new float[CellCount];
    private readonly float[] _water = new float[CellCount];
    private readonly float[] _waterPrevious = new float[CellCount];
    private readonly float[] _pressure = new float[CellCount];
    private readonly float[] _divergence = new float[CellCount];
    private readonly byte[] _texture =
        new byte[Resolution * Resolution * 4];

    private double? _lastSeconds;
    private float _simulationSeconds;

    public VoiceOrbFluidSimulation()
    {
        Initialize();
        WriteTexture();
    }

    internal ReadOnlyMemory<byte> Update(
        VoiceOrbRenderState state,
        double animationSeconds,
        bool reducedMotion)
    {
        double elapsed = _lastSeconds is { } previous &&
            animationSeconds >= previous
            ? Math.Clamp(animationSeconds - previous, 0, 0.05)
            : 1.0 / 60;
        _lastSeconds = animationSeconds;

        if (!reducedMotion)
        {
            int steps = Math.Clamp(
                (int)Math.Ceiling(elapsed / 0.018),
                1,
                3);
            float stepSeconds = (float)(elapsed / steps);
            for (int index = 0; index < steps; index++)
                Step(state, stepSeconds);
        }

        WriteTexture();
        return _texture;
    }

    internal float MeanWater
    {
        get
        {
            float total = 0;
            for (int y = 1; y <= Resolution; y++)
            {
                for (int x = 1; x <= Resolution; x++)
                    total += _water[At(x, y)];
            }
            return total / (Resolution * Resolution);
        }
    }

    internal float MeanSpeed
    {
        get
        {
            float total = 0;
            for (int y = 1; y <= Resolution; y++)
            {
                for (int x = 1; x <= Resolution; x++)
                {
                    int cell = At(x, y);
                    total += MathF.Sqrt(
                        _horizontal[cell] * _horizontal[cell] +
                        _vertical[cell] * _vertical[cell]);
                }
            }
            return total / (Resolution * Resolution);
        }
    }

    internal float MeanMixing
    {
        get
        {
            float total = 0;
            for (int y = 1; y <= Resolution; y++)
            {
                for (int x = 1; x <= Resolution; x++)
                {
                    float water = _water[At(x, y)];
                    total += 4 * water * (1 - water);
                }
            }
            return total / (Resolution * Resolution);
        }
    }

    private void Initialize()
    {
        for (int y = 1; y <= Resolution; y++)
        {
            float ny = Coordinate(y);
            for (int x = 1; x <= Resolution; x++)
            {
                float nx = Coordinate(x);
                int cell = At(x, y);
                float boundary =
                    nx * 0.72f -
                    ny * 0.24f +
                    0.22f * MathF.Sin(ny * 4.1f) +
                    0.10f * MathF.Sin((nx + ny) * 6.2f);
                _water[cell] =
                    1 - SmoothStep(-0.10f, 0.10f, boundary);
                _horizontal[cell] =
                    0.025f *
                    MathF.Sin(MathF.PI * nx) *
                    MathF.Cos(MathF.PI * ny);
                _vertical[cell] =
                    -0.025f *
                    MathF.Cos(MathF.PI * nx) *
                    MathF.Sin(MathF.PI * ny);
            }
        }
        SetBoundary(0, _water);
        SetBoundary(1, _horizontal);
        SetBoundary(2, _vertical);
    }

    private void Step(VoiceOrbRenderState state, float elapsed)
    {
        if (elapsed <= 0)
            return;

        _simulationSeconds += elapsed;
        AddForces(state, elapsed);

        float damping = MathF.Exp(
            -elapsed * (0.72f - state.Energy * 0.34f));
        for (int index = 0; index < CellCount; index++)
        {
            _horizontal[index] *= damping;
            _vertical[index] *= damping;
        }

        Array.Copy(_horizontal, _horizontalPrevious, CellCount);
        Array.Copy(_vertical, _verticalPrevious, CellCount);
        Advect(
            1,
            _horizontal,
            _horizontalPrevious,
            _horizontalPrevious,
            _verticalPrevious,
            elapsed);
        Advect(
            2,
            _vertical,
            _verticalPrevious,
            _horizontalPrevious,
            _verticalPrevious,
            elapsed);
        Project(_horizontal, _vertical);

        Array.Copy(_water, _waterPrevious, CellCount);
        float meanBefore = Mean(_waterPrevious);
        Advect(
            0,
            _water,
            _waterPrevious,
            _horizontal,
            _vertical,
            elapsed);
        MixGases(elapsed, state.Energy);
        PreserveWaterMass(meanBefore);
    }

    private void AddForces(VoiceOrbRenderState state, float elapsed)
    {
        float activity = Math.Clamp(
            state.Energy * 0.88f +
            state.SpeechActivity * 0.32f +
            state.ProcessingIntensity * 0.22f,
            0,
            1.35f);
        float force = 0.32f + activity * 7.4f;
        float leftJetY =
            0.34f * MathF.Sin(_simulationSeconds * 0.71f + 0.4f);
        float rightJetY =
            0.34f * MathF.Sin(_simulationSeconds * 0.59f + 2.2f);
        float verticalJetX =
            0.28f * MathF.Sin(_simulationSeconds * 0.43f + 1.1f);

        for (int y = 1; y <= Resolution; y++)
        {
            float ny = Coordinate(y);
            for (int x = 1; x <= Resolution; x++)
            {
                float nx = Coordinate(x);
                int cell = At(x, y);
                float leftJet = Gaussian(
                    nx + 0.72f,
                    ny - leftJetY,
                    0.12f,
                    0.22f);
                float rightJet = Gaussian(
                    nx - 0.72f,
                    ny - rightJetY,
                    0.12f,
                    0.22f);
                float verticalJet = Gaussian(
                    nx - verticalJetX,
                    ny + 0.70f,
                    0.24f,
                    0.13f);
                float cellularX =
                    MathF.Sin(MathF.PI * nx * 1.5f + 0.7f) *
                    MathF.Cos(MathF.PI * ny * 1.2f - 0.4f);
                float cellularY =
                    -MathF.Cos(MathF.PI * nx * 1.5f + 0.7f) *
                    MathF.Sin(MathF.PI * ny * 1.2f - 0.4f);

                _horizontal[cell] += elapsed * force *
                    (leftJet - rightJet + cellularX * 0.10f);
                _vertical[cell] += elapsed * force *
                    (verticalJet * 0.72f + cellularY * 0.10f);
                float exchange =
                    elapsed * (0.035f + activity * 0.16f);
                _water[cell] +=
                    leftJet * exchange * (1 - _water[cell]);
                _water[cell] -=
                    rightJet * exchange * _water[cell];
            }
        }
        SetBoundary(1, _horizontal);
        SetBoundary(2, _vertical);
        LimitVelocity(0.20f + state.Energy * 0.62f);
    }

    private void LimitVelocity(float maximum)
    {
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                float speed = MathF.Sqrt(
                    _horizontal[cell] * _horizontal[cell] +
                    _vertical[cell] * _vertical[cell]);
                if (speed <= maximum || speed <= 0.0001f)
                    continue;
                float scale = maximum / speed;
                _horizontal[cell] *= scale;
                _vertical[cell] *= scale;
            }
        }
    }

    private void Project(float[] horizontal, float[] vertical)
    {
        float inverseResolution = 1f / Resolution;
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                _divergence[cell] = -0.5f * inverseResolution *
                    (horizontal[At(x + 1, y)] -
                     horizontal[At(x - 1, y)] +
                     vertical[At(x, y + 1)] -
                     vertical[At(x, y - 1)]);
                _pressure[cell] = 0;
            }
        }
        SetBoundary(0, _divergence);
        SetBoundary(0, _pressure);

        for (int iteration = 0; iteration < PressureIterations; iteration++)
        {
            for (int y = 1; y <= Resolution; y++)
            {
                for (int x = 1; x <= Resolution; x++)
                {
                    int cell = At(x, y);
                    _pressure[cell] =
                        (_divergence[cell] +
                         _pressure[At(x - 1, y)] +
                         _pressure[At(x + 1, y)] +
                         _pressure[At(x, y - 1)] +
                         _pressure[At(x, y + 1)]) * 0.25f;
                }
            }
            SetBoundary(0, _pressure);
        }

        float scale = 0.5f * Resolution;
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                horizontal[cell] -= scale *
                    (_pressure[At(x + 1, y)] -
                     _pressure[At(x - 1, y)]);
                vertical[cell] -= scale *
                    (_pressure[At(x, y + 1)] -
                     _pressure[At(x, y - 1)]);
            }
        }
        SetBoundary(1, horizontal);
        SetBoundary(2, vertical);
    }

    private static void Advect(
        int boundary,
        float[] target,
        float[] source,
        float[] horizontal,
        float[] vertical,
        float elapsed)
    {
        float scale = elapsed * Resolution;
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                float sourceX = Math.Clamp(
                    x - scale * horizontal[cell],
                    0.5f,
                    Resolution + 0.5f);
                float sourceY = Math.Clamp(
                    y - scale * vertical[cell],
                    0.5f,
                    Resolution + 0.5f);
                int x0 = (int)MathF.Floor(sourceX);
                int x1 = x0 + 1;
                int y0 = (int)MathF.Floor(sourceY);
                int y1 = y0 + 1;
                float sx = sourceX - x0;
                float sy = sourceY - y0;
                target[cell] =
                    (1 - sx) *
                        ((1 - sy) * source[At(x0, y0)] +
                         sy * source[At(x0, y1)]) +
                    sx *
                        ((1 - sy) * source[At(x1, y0)] +
                         sy * source[At(x1, y1)]);
            }
        }
        SetBoundary(boundary, target);
    }

    private void PreserveWaterMass(float targetMean)
    {
        float correction = targetMean - Mean(_water);
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                _water[cell] = Math.Clamp(
                    _water[cell] + correction,
                    0.015f,
                    0.985f);
            }
        }
        SetBoundary(0, _water);
    }

    private void MixGases(float elapsed, float energy)
    {
        Array.Copy(_water, _waterPrevious, CellCount);
        float mix = Math.Clamp(
            elapsed * (0.35f + energy * 2.4f),
            0,
            0.055f);
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                float neighbors =
                    (_waterPrevious[At(x - 1, y)] +
                     _waterPrevious[At(x + 1, y)] +
                     _waterPrevious[At(x, y - 1)] +
                     _waterPrevious[At(x, y + 1)] +
                     _waterPrevious[At(
                         Math.Max(0, x - 2),
                         y)] +
                     _waterPrevious[At(
                         Math.Min(Resolution + 1, x + 2),
                         y)] +
                     _waterPrevious[At(
                         x,
                         Math.Max(0, y - 2))] +
                     _waterPrevious[At(
                         x,
                         Math.Min(Resolution + 1, y + 2))]) * 0.125f;
                _water[cell] += (neighbors - _water[cell]) * mix;
            }
        }
        SetBoundary(0, _water);
    }

    private void WriteTexture()
    {
        int target = 0;
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
            {
                int cell = At(x, y);
                float speed = MathF.Sqrt(
                    _horizontal[cell] * _horizontal[cell] +
                    _vertical[cell] * _vertical[cell]);
                float pressure = Math.Clamp(
                    0.5f + _pressure[cell] * 26f,
                    0,
                    1);
                _texture[target++] = Byte(_water[cell]);
                _texture[target++] = Byte(Math.Clamp(speed * 2.2f, 0, 1));
                _texture[target++] = Byte(pressure);
                _texture[target++] = 255;
            }
        }
    }

    private static float Mean(float[] values)
    {
        float total = 0;
        for (int y = 1; y <= Resolution; y++)
        {
            for (int x = 1; x <= Resolution; x++)
                total += values[At(x, y)];
        }
        return total / (Resolution * Resolution);
    }

    private static void SetBoundary(int boundary, float[] values)
    {
        for (int index = 1; index <= Resolution; index++)
        {
            values[At(0, index)] = boundary == 1
                ? -values[At(1, index)]
                : values[At(1, index)];
            values[At(Resolution + 1, index)] = boundary == 1
                ? -values[At(Resolution, index)]
                : values[At(Resolution, index)];
            values[At(index, 0)] = boundary == 2
                ? -values[At(index, 1)]
                : values[At(index, 1)];
            values[At(index, Resolution + 1)] = boundary == 2
                ? -values[At(index, Resolution)]
                : values[At(index, Resolution)];
        }
        values[At(0, 0)] =
            0.5f * (values[At(1, 0)] + values[At(0, 1)]);
        values[At(0, Resolution + 1)] =
            0.5f *
            (values[At(1, Resolution + 1)] +
             values[At(0, Resolution)]);
        values[At(Resolution + 1, 0)] =
            0.5f *
            (values[At(Resolution, 0)] +
             values[At(Resolution + 1, 1)]);
        values[At(Resolution + 1, Resolution + 1)] =
            0.5f *
            (values[At(Resolution, Resolution + 1)] +
             values[At(Resolution + 1, Resolution)]);
    }

    private static float Gaussian(
        float x,
        float y,
        float width,
        float height) =>
        MathF.Exp(
            -(x * x / (width * width) +
              y * y / (height * height)) * 1.8f);

    private static float Coordinate(int index) =>
        ((index - 0.5f) / Resolution) * 2 - 1;

    private static int At(int x, int y) => x + Stride * y;

    private static byte Byte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float normalized = Math.Clamp(
            (value - minimum) / (maximum - minimum),
            0,
            1);
        return normalized * normalized * (3 - 2 * normalized);
    }
}
