using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Deckard.Capture;

/// <summary>Bridges between Vortice D3D11 objects and the WinRT Direct3D types used by Windows.Graphics.Capture.</summary>
internal static partial class Direct3D11Interop
{
    private static readonly Guid IDirect3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Gets the D3D11 texture behind a capture surface. The caller owns the returned texture.</summary>
    public static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IDirect3DDxgiInterfaceAccessIid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface is the first method after IUnknown (vtable slot 3).
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
                var iid = ID3D11Texture2DIid;
                nint texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
