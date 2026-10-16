using System;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using Range = CefSharp.Structs.Range;

namespace OmniTube;

/// <summary>
/// Chromium off-screen render handler: tells Chromium how big the view is and receives
/// finished frames, which go straight to <see cref="D3DRenderer"/>.
/// </summary>
internal sealed class OsrHandler : IRenderHandler
{
    private readonly D3DRenderer _renderer;
    private readonly int _uiHeight;
    private volatile int _pxW, _pxH;
    private volatile float _scale = 1f;

    public OsrHandler(D3DRenderer renderer, int uiHeight, int pxW, int pxH)
    {
        _renderer = renderer;
        _uiHeight = uiHeight;
        SetPixelSize(pxW, pxH);
    }

    public float Scale => _scale;
    public int DipWidth => Math.Max(1, (int)Math.Round(_pxW / _scale));
    public int DipHeight => Math.Max(1, (int)Math.Round(_pxH / _scale));

    /// <summary>Window size in physical pixels. The page is laid out at UiHeight logical px and scaled up.</summary>
    public void SetPixelSize(int pxW, int pxH)
    {
        _pxW = Math.Max(1, pxW);
        _pxH = Math.Max(1, pxH);
        _scale = Math.Max(0.25f, (float)_pxH / _uiHeight);
    }

    public ScreenInfo? GetScreenInfo() => new ScreenInfo
    {
        DeviceScaleFactor = _scale,
        Depth = 24,
        DepthPerComponent = 8,
        Rect = new Rect(0, 0, DipWidth, DipHeight),
        AvailableRect = new Rect(0, 0, DipWidth, DipHeight),
    };

    public Rect GetViewRect() => new Rect(0, 0, DipWidth, DipHeight);

    public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
    {
        screenX = (int)(viewX * _scale);
        screenY = (int)(viewY * _scale);
        return true;
    }

    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
    {
        if (type == PaintElementType.View && acceleratedPaintInfo != null)
            _renderer.SubmitSharedTexture(acceleratedPaintInfo.SharedTextureHandle);
    }

    public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
    {
        if (type == PaintElementType.View)
            _renderer.SubmitCpuFrame(buffer, width, height);
    }

    public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) { }

    public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;

    public void UpdateDragCursor(DragOperationsMask operation) { }

    public void OnPopupShow(bool show) { }

    public void OnPopupSize(Rect rect) { }

    public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds) { }

    public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }

    public void Dispose() { }
}
