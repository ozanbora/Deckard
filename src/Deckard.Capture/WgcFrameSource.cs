using System.Threading.Channels;
using Deckard.Core.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;

namespace Deckard.Capture;

public sealed record WgcOptions
{
    /// <summary>Draw the cursor into frames. Off by default so it never pollutes detection or OCR.</summary>
    public bool CaptureCursor { get; init; }

    /// <summary>Show the yellow capture border. Windows 11 lets unpackaged apps turn it off.</summary>
    public bool ShowBorder { get; init; }
}

/// <summary>
/// Captures a single window through Windows.Graphics.Capture and reads frames back to CPU memory.
/// This is the OS path the Snipping Tool and OBS use: it reads composed window contents and never touches the game process.
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 2;

    private readonly GraphicsCaptureItem _item;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Channel<Frame> _frames;
    private readonly Lock _gate = new();
    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    private WgcFrameSource(GraphicsCaptureItem item, WgcOptions options)
    {
        _item = item;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!, out _device!).CheckError();
        _context = _device.ImmediateContext;
        _winrtDevice = Direct3D11Interop.CreateWinRTDevice(_device);

        _frames = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true },
            dropped => dropped.Dispose());

        _poolSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, PixelFormat, BufferCount, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;

        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = options.CaptureCursor;
        _session.IsBorderRequired = options.ShowBorder;
    }

    public ChannelReader<Frame> Frames => _frames.Reader;

    public string DisplayName => _item.DisplayName;

    public static async Task<WgcFrameSource> CreateForWindowAsync(nint hwnd, WgcOptions? options = null)
    {
        options ??= new WgcOptions();
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");

        var item = GraphicsCaptureItem.TryCreateFromWindowId(new WindowId { Value = (ulong)hwnd })
            ?? throw new ArgumentException($"Window 0x{hwnd:X} cannot be captured.", nameof(hwnd));

        if (!options.ShowBorder)
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);

        return new WgcFrameSource(item, options);
    }

    public void Start() => _session.StartCapture();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            using var captured = sender.TryGetNextFrame();
            if (captured is null)
                return;

            var contentSize = captured.ContentSize;
            using (var texture = Direct3D11Interop.GetTexture(captured.Surface))
                _frames.Writer.TryWrite(ReadBack(texture, contentSize, captured.SystemRelativeTime));

            // The window was resized: rebuild the pool so the next frames match the new size.
            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                _poolSize = contentSize;
                sender.Recreate(_winrtDevice, PixelFormat, BufferCount, contentSize);
            }
        }
    }

    private unsafe Frame ReadBack(ID3D11Texture2D texture, SizeInt32 contentSize, TimeSpan timestamp)
    {
        var description = texture.Description;
        EnsureStaging(description);
        _context.CopyResource(_staging!, texture);

        // Content can be smaller than the pool texture while a resize is in flight.
        var width = (int)Math.Min((uint)contentSize.Width, description.Width);
        var height = (int)Math.Min((uint)contentSize.Height, description.Height);
        var frame = Frame.Create(width, height, timestamp);

        var mapped = _context.Map(_staging!, 0, MapMode.Read);
        try
        {
            var pitch = (int)mapped.RowPitch;
            var source = new ReadOnlySpan<byte>((void*)mapped.DataPointer, pitch * height);
            PixelCopy.CopyRows(source, pitch, frame.Pixels, frame.Stride, width * Frame.BytesPerPixel, height);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return frame;
    }

    private void EnsureStaging(Texture2DDescription source)
    {
        if (_staging is not null)
        {
            var current = _staging.Description;
            if (current.Width == source.Width && current.Height == source.Height && current.Format == source.Format)
                return;
            _staging.Dispose();
        }

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => _frames.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
        }

        _framePool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        _session.Dispose();
        _framePool.Dispose();

        _frames.Writer.TryComplete();
        while (_frames.Reader.TryRead(out var pending))
            pending.Dispose();

        _staging?.Dispose();
        _winrtDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
        return ValueTask.CompletedTask;
    }
}
