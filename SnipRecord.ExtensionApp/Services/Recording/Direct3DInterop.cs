using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// D3D11 ⇄ WinRT bridging without [ComImport] RCWs: raw vtable calls through delegates so nothing depends
/// on built-in COM interop for these hot paths.
/// </summary>
internal static class Direct3DInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowFn(IntPtr self, IntPtr window, ref Guid riid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(IntPtr self, ref Guid riid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutBoolFn(IntPtr self, byte value);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private static readonly object s_cacheLock = new();
    private static IntPtr s_getInterfacePtr;
    private static GetInterfaceFn? s_getInterface;

    private static IntPtr VtableSlot(IntPtr comObject, int slot)
        => Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size);

    /// <summary>Hardware D3D11 device (BGRA support, multithread protected); WARP fallback.</summary>
    public static ID3D11Device CreateDevice()
    {
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? device);
        if (hr.Failure || device == null)
        {
            hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device);
        }
        hr.CheckError();
        using (var mt = device!.QueryInterface<ID3D11Multithread>())
        {
            mt.SetMultithreadProtected(true);
        }
        return device;
    }

    public static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>Wraps a texture as a WinRT surface (the surface keeps its own reference).</summary>
    public static IDirect3DSurface CreateWinRTSurface(ID3D11Texture2D texture)
    {
        using var surface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(surface.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DSurface>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>The ID3D11Texture2D behind a WinRT surface. Caller disposes the result.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iidAccess, out var access));
            try
            {
                IntPtr fn = VtableSlot(access, 3); // IUnknown(3) → GetInterface
                GetInterfaceFn del;
                lock (s_cacheLock)
                {
                    if (fn != s_getInterfacePtr || s_getInterface == null)
                    {
                        s_getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(fn);
                        s_getInterfacePtr = fn;
                    }
                    del = s_getInterface;
                }
                var iidTex = IID_ID3D11Texture2D;
                Marshal.ThrowExceptionForHR(del(access, ref iidTex, out var tex));
                return new ID3D11Texture2D(tex); // takes ownership of the reference
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        IntPtr factory;
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factory));
        }
        finally { WindowsDeleteString(hstring); }

        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowFn>(VtableSlot(factory, 3));
            var iidItem = IID_IGraphicsCaptureItem;
            Marshal.ThrowExceptionForHR(create(factory, hwnd, ref iidItem, out var itemPtr));
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>
    /// GraphicsCaptureSession.IsBorderRequired (Win11 / 20348+, not in the 19041 projection) through
    /// IGraphicsCaptureSession3. Returns false when unsupported or refused.
    /// </summary>
    public static bool TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        try
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                return false;

            IntPtr insp = MarshalInspectable<object>.FromManaged(session);
            try
            {
                var iid = IID_IGraphicsCaptureSession3;
                if (Marshal.QueryInterface(insp, in iid, out var s3) < 0) return false;
                try
                {
                    // IInspectable has 6 slots; get_IsBorderRequired = 6, put_IsBorderRequired = 7.
                    var put = Marshal.GetDelegateForFunctionPointer<PutBoolFn>(VtableSlot(s3, 7));
                    return put(s3, required ? (byte)1 : (byte)0) >= 0;
                }
                finally { Marshal.Release(s3); }
            }
            finally { Marshal.Release(insp); }
        }
        catch
        {
            return false;
        }
    }
}
