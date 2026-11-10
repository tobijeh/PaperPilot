using System.Runtime.InteropServices;
using System.Windows.Interop;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

/// <summary>
/// Precision-touchpad scrolling in Chrome/Edge goes through DirectManipulation and never shows
/// up in the low-level mouse hook. Raw HID input from the touchpad does: when two or more fingers
/// are down while the cursor is over the target window, that's the user scrolling (or zooming).
/// </summary>
public sealed class TouchpadMonitor : IDisposable
{
    private const ushort DigitizerPage = 0x0D;
    private const ushort TouchpadUsage = 0x05;
    private const ushort ContactCountUsage = 0x54;

    private readonly HwndSource _source;
    private readonly InputMonitor _input;
    private readonly Dictionary<IntPtr, IntPtr> _preparsed = new();
    private bool _loggedFirst;

    public TouchpadMonitor(InputMonitor input)
    {
        _input = input;
        // A hidden (never shown) normal window: raw input with INPUTSINK needs a real target window.
        _source = new HwndSource(new HwndSourceParameters("PaperPilotTouchpad") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);

        var device = new NativeMethods.RAWINPUTDEVICE
        {
            usUsagePage = DigitizerPage,
            usUsage = TouchpadUsage,
            dwFlags = NativeMethods.RIDEV_INPUTSINK,
            hwndTarget = _source.Handle,
        };
        if (NativeMethods.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            Log.Info("TouchpadMonitor: listening for precision-touchpad gestures.");
        else
            Log.Warn($"TouchpadMonitor: could not register for touchpad input (error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_INPUT)
        {
            try { OnRawInput(lParam); }
            catch (Exception ex) { Log.Warn($"TouchpadMonitor: {ex.Message}"); }
        }
        return IntPtr.Zero;
    }

    private void OnRawInput(IntPtr hRawInput)
    {
        var target = _input.TargetWindow;
        if (target == IntPtr.Zero) return;

        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        uint size = 0;
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, buffer, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);
            if (header.dwType != NativeMethods.RIM_TYPEHID) return;

            // RAWHID follows the header: DWORD dwSizeHid, DWORD dwCount, BYTE bRawData[dwSizeHid * dwCount]
            var hid = buffer + (int)headerSize;
            uint reportSize = (uint)Marshal.ReadInt32(hid);
            uint count = (uint)Marshal.ReadInt32(hid, 4);
            var reports = hid + 8;

            var preparsed = GetPreparsedData(header.hDevice);
            if (preparsed == IntPtr.Zero) return;

            uint maxContacts = 0;
            for (uint i = 0; i < count; i++)
            {
                var report = reports + (int)(i * reportSize);
                if (NativeMethods.HidP_GetUsageValue(0 /* HidP_Input */, DigitizerPage, 0, ContactCountUsage,
                        out var contacts, preparsed, report, reportSize) == NativeMethods.HIDP_STATUS_SUCCESS)
                    maxContacts = Math.Max(maxContacts, contacts);
            }

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Log.Info($"TouchpadMonitor: receiving touchpad reports (contacts {maxContacts}).");
            }

            if (maxContacts >= 2 && NativeMethods.GetCursorPos(out var pt) && NativeMethods.RootWindowFromPoint(pt) == target)
                _input.Report(ManualInputKind.Touchpad, $"{maxContacts}-finger touchpad gesture");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr GetPreparsedData(IntPtr device)
    {
        if (_preparsed.TryGetValue(device, out var cached)) return cached;
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        IntPtr data = IntPtr.Zero;
        if (size > 0)
        {
            data = Marshal.AllocHGlobal((int)size);
            if (NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RIDI_PREPARSEDDATA, data, ref size) == unchecked((uint)-1))
            {
                Marshal.FreeHGlobal(data);
                data = IntPtr.Zero;
            }
        }
        _preparsed[device] = data; // kept for the app's lifetime; one entry per touchpad
        return data;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
        foreach (var p in _preparsed.Values)
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        _preparsed.Clear();
    }
}
