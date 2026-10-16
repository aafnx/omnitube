using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmniTube;

/// <summary>
/// Owns a D3D11 device and a flip-model DXGI swap chain on our window, and presents
/// the latest browser frame every vsync from a dedicated thread.
///
/// This is the whole point of the app: the Steam overlay hooks IDXGISwapChain::Present
/// of the game's own swap chain. Chromium never touches our window — it renders into a
/// texture that we copy into this swap chain — so to Steam we look like a normal D3D11 game.
///
/// COM is called through raw vtable pointers (indices verified against the Windows SDK
/// headers) to avoid pulling in a binding library.
/// </summary>
internal sealed unsafe class D3DRenderer : IDisposable
{
    // ---- IIDs ----
    private static readonly Guid IID_ID3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    // ---- constants ----
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
    private const uint DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
    private const uint DXGI_SCALING_STRETCH = 0;
    private const uint DXGI_MWA_NO_ALT_ENTER = 0x2;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int DXGI_STATUS_OCCLUDED = 0x087A0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SAMPLE_DESC { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height, Format;
        public int Stereo;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        int* featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    // ---- state ----
    private readonly object _sync = new();
    private IntPtr _device, _device1, _context, _swapChain, _backBuffer, _rtv, _frameTex;
    private uint _frameW, _frameH, _frameFormat;
    private int _bbW, _bbH;
    private volatile int _pendingW, _pendingH;
    private volatile bool _resizePending;
    private bool _hasFrame;
    private Thread _thread;
    private volatile bool _running;

    public long FramesReceived;          // browser frames copied in
    public int SharedTextureFailures;    // failed OpenSharedResource calls

    public D3DRenderer(IntPtr hwnd, int width, int height)
    {
        int* levels = stackalloc int[] { 0xb100, 0xb000 };
        Check(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, out _device, out int featureLevel, out _context), "D3D11CreateDevice");
        Log.Info($"D3D11 device created, feature level 0x{featureLevel:x}");

        _device1 = QueryInterface(_device, IID_ID3D11Device1);

        // Chromium's frames arrive on CEF's thread while we present on ours.
        IntPtr mt = QueryInterface(_context, IID_ID3D10Multithread);
        if (mt != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(mt, 5))(mt, 1); // SetMultithreadProtected(TRUE)
            Release(mt);
        }

        IntPtr dxgiDevice = QueryInterface(_device, IID_IDXGIDevice);
        IntPtr adapter;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(dxgiDevice, 7))(dxgiDevice, &adapter), "IDXGIDevice::GetAdapter");
        LogAdapter(adapter);

        IntPtr factory;
        Guid iidFactory = IID_IDXGIFactory2;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(adapter, 6))(adapter, &iidFactory, &factory), "IDXGIAdapter::GetParent");

        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = 2,
            Scaling = DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
        };
        IntPtr swapChain;
        // IDXGIFactory2::CreateSwapChainForHwnd(pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, ppSwapChain)
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, DXGI_SWAP_CHAIN_DESC1*, void*, IntPtr, IntPtr*, int>)Vtbl(factory, 15))
            (factory, _device, hwnd, &desc, null, IntPtr.Zero, &swapChain), "CreateSwapChainForHwnd");
        _swapChain = swapChain;

        // IDXGIFactory::MakeWindowAssociation — we stay borderless, no exclusive fullscreen on Alt+Enter
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Vtbl(factory, 8))(factory, hwnd, DXGI_MWA_NO_ALT_ENTER);

        Release(factory);
        Release(adapter);
        Release(dxgiDevice);

        _bbW = (int)desc.Width;
        _bbH = (int)desc.Height;
        CreateBackBufferViews();
        Log.Info($"Swap chain {_bbW}x{_bbH} created");
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Present", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void RequestResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _pendingW = width;
        _pendingH = height;
        _resizePending = true;
    }

    /// <summary>Called on CEF's thread with an NT handle that is only valid during the call.</summary>
    public void SubmitSharedTexture(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        lock (_sync)
        {
            IntPtr tex = OpenShared(handle);
            if (tex == IntPtr.Zero) return;

            try
            {
                D3D11_TEXTURE2D_DESC d;
                ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Vtbl(tex, 10))(tex, &d); // ID3D11Texture2D::GetDesc
                if (!EnsureFrameTexture(d.Width, d.Height, d.Format)) return;

                // ID3D11DeviceContext::CopyResource(dst, src)
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(_context, 47))(_context, _frameTex, tex);
                _hasFrame = true;

                if (Interlocked.Increment(ref FramesReceived) == 1)
                    Log.Info($"First GPU frame: {d.Width}x{d.Height}, format {d.Format}");
            }
            finally
            {
                Release(tex);
            }
        }
    }

    /// <summary>Called on CEF's thread with a BGRA buffer (fallback path).</summary>
    public void SubmitCpuFrame(IntPtr buffer, int width, int height)
    {
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0) return;

        lock (_sync)
        {
            if (!EnsureFrameTexture((uint)width, (uint)height, DXGI_FORMAT_B8G8R8A8_UNORM)) return;

            // ID3D11DeviceContext::UpdateSubresource(dst, sub, box, data, rowPitch, depthPitch)
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, D3D11_BOX*, IntPtr, uint, uint, void>)Vtbl(_context, 48))
                (_context, _frameTex, 0, null, buffer, (uint)(width * 4), 0);
            _hasFrame = true;

            if (Interlocked.Increment(ref FramesReceived) == 1)
                Log.Info($"First CPU frame: {width}x{height}");
        }
    }

    private IntPtr OpenShared(IntPtr handle)
    {
        Guid iid = IID_ID3D11Texture2D;
        IntPtr tex = IntPtr.Zero;
        int hr = -1;

        // Modern CEF hands out NT handles -> ID3D11Device1::OpenSharedResource1
        if (_device1 != IntPtr.Zero)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(_device1, 48))(_device1, handle, &iid, &tex);

        // Older CEF used legacy KMT handles -> ID3D11Device::OpenSharedResource
        if (hr < 0)
        {
            int hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(_device, 28))(_device, handle, &iid, &tex);
            if (hr2 >= 0) hr = hr2;
        }

        if (hr < 0 || tex == IntPtr.Zero)
        {
            int n = Interlocked.Increment(ref SharedTextureFailures);
            if (n <= 5 || n % 300 == 0)
                Log.Warn($"OpenSharedResource failed hr=0x{hr:x8} (failure #{n})");
            return IntPtr.Zero;
        }

        return tex;
    }

    private bool EnsureFrameTexture(uint w, uint h, uint format)
    {
        if (_frameTex != IntPtr.Zero && _frameW == w && _frameH == h && _frameFormat == format) return true;

        if (_frameTex != IntPtr.Zero)
        {
            Release(_frameTex);
            _frameTex = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = 0, // DEFAULT
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
        };
        IntPtr tex;
        // ID3D11Device::CreateTexture2D(desc, initialData, out tex)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void*, IntPtr*, int>)Vtbl(_device, 5))(_device, &desc, null, &tex);
        if (hr < 0)
        {
            Log.Error($"CreateTexture2D {w}x{h} fmt {format} failed hr=0x{hr:x8}");
            return false;
        }

        _frameTex = tex;
        _frameW = w;
        _frameH = h;
        _frameFormat = format;
        _hasFrame = false;
        Log.Info($"Frame texture {w}x{h} fmt {format}");
        return true;
    }

    private void RenderLoop()
    {
        float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
        int presentErrors = 0;

        while (_running)
        {
            if (_resizePending)
            {
                _resizePending = false;
                DoResize(_pendingW, _pendingH);
            }

            lock (_sync)
            {
                if (_rtv != IntPtr.Zero)
                {
                    // ID3D11DeviceContext::ClearRenderTargetView
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)Vtbl(_context, 50))(_context, _rtv, black);
                }

                if (_hasFrame && _frameTex != IntPtr.Zero && _backBuffer != IntPtr.Zero)
                {
                    uint copyW = (uint)Math.Min((int)_frameW, _bbW);
                    uint copyH = (uint)Math.Min((int)_frameH, _bbH);
                    uint dstX = (uint)Math.Max(0, (_bbW - (int)_frameW) / 2);
                    uint dstY = (uint)Math.Max(0, (_bbH - (int)_frameH) / 2);
                    uint srcX = (uint)Math.Max(0, ((int)_frameW - _bbW) / 2);
                    uint srcY = (uint)Math.Max(0, ((int)_frameH - _bbH) / 2);
                    var box = new D3D11_BOX { Left = srcX, Top = srcY, Front = 0, Right = srcX + copyW, Bottom = srcY + copyH, Back = 1 };

                    // ID3D11DeviceContext::CopySubresourceRegion(dst, dstSub, x, y, z, src, srcSub, box)
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Vtbl(_context, 46))
                        (_context, _backBuffer, 0, dstX, dstY, 0, _frameTex, 0, &box);
                }
            }

            // IDXGISwapChain::Present(syncInterval, flags) — blocks until vsync. This is what Steam hooks.
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Vtbl(_swapChain, 8))(_swapChain, 1, 0);
            if (hr == DXGI_STATUS_OCCLUDED)
            {
                Thread.Sleep(16);
            }
            else if (hr < 0)
            {
                if (++presentErrors <= 5) Log.Error($"Present failed hr=0x{hr:x8}");
                Thread.Sleep(100);
            }
        }
    }

    private void DoResize(int w, int h)
    {
        if (w == _bbW && h == _bbH) return;

        lock (_sync)
        {
            ReleaseBackBufferViews();
            // IDXGISwapChain::ResizeBuffers(count=0 keep, w, h, format=UNKNOWN keep, flags)
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)Vtbl(_swapChain, 13))
                (_swapChain, 0, (uint)w, (uint)h, 0, 0);
            if (hr < 0) Log.Error($"ResizeBuffers {w}x{h} failed hr=0x{hr:x8}");
            else
            {
                _bbW = w;
                _bbH = h;
                Log.Info($"Swap chain resized to {w}x{h}");
            }
            CreateBackBufferViews();
        }
    }

    private void CreateBackBufferViews()
    {
        Guid iid = IID_ID3D11Texture2D;
        IntPtr buffer;
        // IDXGISwapChain::GetBuffer(0, riid, out)
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Vtbl(_swapChain, 9))(_swapChain, 0, &iid, &buffer), "GetBuffer");
        _backBuffer = buffer;

        IntPtr rtv;
        // ID3D11Device::CreateRenderTargetView(resource, desc, out rtv)
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Vtbl(_device, 9))(_device, _backBuffer, null, &rtv), "CreateRenderTargetView");
        _rtv = rtv;
    }

    private void ReleaseBackBufferViews()
    {
        if (_rtv != IntPtr.Zero) { Release(_rtv); _rtv = IntPtr.Zero; }
        if (_backBuffer != IntPtr.Zero) { Release(_backBuffer); _backBuffer = IntPtr.Zero; }
    }

    private static void LogAdapter(IntPtr adapter)
    {
        try
        {
            IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_ADAPTER_DESC>());
            try
            {
                // IDXGIAdapter::GetDesc
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl(adapter, 8))(adapter, mem);
                if (hr >= 0)
                {
                    var d = Marshal.PtrToStructure<DXGI_ADAPTER_DESC>(mem);
                    Log.Info($"GPU: {d.Description} (vendor 0x{d.VendorId:x4}, luid {d.LuidHigh:x8}:{d.LuidLow:x8})");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read adapter description: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);

        lock (_sync)
        {
            ReleaseBackBufferViews();
            if (_frameTex != IntPtr.Zero) { Release(_frameTex); _frameTex = IntPtr.Zero; }
            if (_swapChain != IntPtr.Zero) { Release(_swapChain); _swapChain = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Release(_context); _context = IntPtr.Zero; }
            if (_device1 != IntPtr.Zero) { Release(_device1); _device1 = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Release(_device); _device = IntPtr.Zero; }
        }
    }

    // ---- COM helpers ----
    private static void* Vtbl(IntPtr obj, int index) => (*(void***)obj)[index];

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        IntPtr result = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(obj, 0))(obj, &iid, &result);
        if (hr < 0)
        {
            Log.Warn($"QueryInterface {iid} failed hr=0x{hr:x8}");
            return IntPtr.Zero;
        }
        return result;
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj, 2))(obj);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (hr=0x{hr:x8})", hr);
    }
}
