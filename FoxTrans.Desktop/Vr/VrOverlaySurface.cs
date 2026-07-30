using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FoxTrans.Desktop.Vr;

/// <summary>
/// Reusable offscreen surface for SteamVR's straight-alpha RGBA raw texture.
/// Avalonia renders premultiplied BGRA, so the conversion happens after every
/// render without allocating a per-frame buffer.
/// </summary>
public sealed class VrOverlaySurface : IDisposable
{
    public const int LogicalWidth = 1024;
    public const int LogicalHeight = 448;
    public const int Width = 1536;
    public const int Height = 672;
    private const int BytesPerPixel = 4;
    private static readonly Vector RenderDpi = new(144, 144);

    private readonly RenderTargetBitmap _bitmap = new(
        new PixelSize(Width, Height), RenderDpi);
    private readonly WriteableBitmap _copyBuffer = new(
        new PixelSize(Width, Height),
        RenderDpi,
        PixelFormats.Bgra8888,
        AlphaFormat.Premul);
    private readonly byte[] _bgra = new byte[Width * Height * BytesPerPixel];
    private readonly byte[] _rgba = new byte[Width * Height * BytesPerPixel];
    private bool _disposed;

    public ReadOnlyMemory<byte> Render(Control root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        var size = new Size(LogicalWidth, LogicalHeight);
        root.Measure(size);
        root.Arrange(new Rect(size));
        _bitmap.Render(root);
        CopyBgraPixels();
        ConvertBgraPremultipliedToRgbaStraight(_bgra, _rgba);
        return _rgba;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _copyBuffer.Dispose();
        _bitmap.Dispose();
    }

    public static void ConvertBgraPremultipliedToRgbaStraight(
        ReadOnlySpan<byte> bgra,
        Span<byte> rgba,
        bool forceScalar = false)
    {
        if (bgra.Length != rgba.Length || (bgra.Length & 3) != 0)
            throw new ArgumentException("Pixels must be equal-length BGRA/RGBA quads.");

        int offset = 0;
        if (!forceScalar && Vector128.IsHardwareAccelerated)
        {
            for (; offset <= bgra.Length - 16; offset += 16)
            {
                Vector128<byte> block = Vector128.Create(
                    bgra[offset], bgra[offset + 1], bgra[offset + 2], bgra[offset + 3],
                    bgra[offset + 4], bgra[offset + 5], bgra[offset + 6], bgra[offset + 7],
                    bgra[offset + 8], bgra[offset + 9], bgra[offset + 10], bgra[offset + 11],
                    bgra[offset + 12], bgra[offset + 13], bgra[offset + 14], bgra[offset + 15]);
                ConvertPixel(block.GetElement(0), block.GetElement(1), block.GetElement(2), block.GetElement(3), rgba, offset);
                ConvertPixel(block.GetElement(4), block.GetElement(5), block.GetElement(6), block.GetElement(7), rgba, offset + 4);
                ConvertPixel(block.GetElement(8), block.GetElement(9), block.GetElement(10), block.GetElement(11), rgba, offset + 8);
                ConvertPixel(block.GetElement(12), block.GetElement(13), block.GetElement(14), block.GetElement(15), rgba, offset + 12);
            }
        }
        for (; offset < bgra.Length; offset += BytesPerPixel)
        {
            ConvertPixel(
                bgra[offset],
                bgra[offset + 1],
                bgra[offset + 2],
                bgra[offset + 3],
                rgba,
                offset);
        }
    }

    private void CopyBgraPixels()
    {
        using ILockedFramebuffer framebuffer = _copyBuffer.Lock();
        _bitmap.CopyPixels(framebuffer);
        int destinationOffset = 0;
        for (int row = 0; row < Height; row++)
        {
            nint source = framebuffer.Address + row * framebuffer.RowBytes;
            Marshal.Copy(source, _bgra, destinationOffset, Width * BytesPerPixel);
            destinationOffset += Width * BytesPerPixel;
        }
    }

    private static void ConvertPixel(
        byte blue,
        byte green,
        byte red,
        byte alpha,
        Span<byte> rgba,
        int offset)
    {
        if (alpha == 0)
        {
            rgba[offset] = 0;
            rgba[offset + 1] = 0;
            rgba[offset + 2] = 0;
            rgba[offset + 3] = 0;
            return;
        }

        rgba[offset] = Unpremultiply(red, alpha);
        rgba[offset + 1] = Unpremultiply(green, alpha);
        rgba[offset + 2] = Unpremultiply(blue, alpha);
        rgba[offset + 3] = alpha;
    }

    private static byte Unpremultiply(byte component, byte alpha) =>
        (byte)Math.Min(255, (component * 255 + alpha / 2) / alpha);
}
