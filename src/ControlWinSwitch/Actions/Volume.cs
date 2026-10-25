using static ControlWinSwitch.Native.Win32;

namespace ControlWinSwitch.Actions;

/// <summary>
/// Master volume and mute of the default output device (what the keyboard volume keys change), through
/// IAudioEndpointVolume. Raw COM through vtables (AOT friendly). Open on the UI thread while the slider is
/// shown, then Dispose; <see cref="Reacquire"/> follows a change of the default device.
/// </summary>
internal sealed unsafe class Volume : IDisposable
{
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const uint ClsctxAll = 0x17, CoinitApartmentThreaded = 2;
    private const int ERender = 0, EConsole = 0;

    private void* _enumerator;
    private void* _endpoint; // IAudioEndpointVolume of the current default device; null when there is none
    private bool _uninit;

    /// <summary>Null when the audio service can't be reached.</summary>
    public static Volume? Open()
    {
        int hr = CoInitializeEx(0, CoinitApartmentThreaded);
        var volume = new Volume { _uninit = hr >= 0 }; // S_OK / S_FALSE need a CoUninitialize; RPC_E_CHANGED_MODE: COM already usable

        var clsid = ClsidEnumerator;
        var iid = IidEnumerator;
        void* enumerator;
        hr = CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &enumerator);
        if (hr < 0)
        {
            Log.Error($"Volume: MMDeviceEnumerator failed (0x{hr:X8})");
            volume.Dispose();
            return null;
        }
        volume._enumerator = enumerator;
        volume.Reacquire();
        return volume;
    }

    /// <summary>Takes the current default output device again (it may have changed). False when there is none.</summary>
    public bool Reacquire()
    {
        Release(_endpoint);
        _endpoint = null;
        if (_enumerator == null) return false;

        void* device = null;
        try
        {
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole, out device)
            if (((delegate* unmanaged<void*, int, int, void**, int>)Slot(_enumerator, 4))(_enumerator, ERender, EConsole, &device) < 0) return false;
            var iid = IidEndpointVolume;
            void* endpoint;
            // IMMDevice::Activate(iid, clsctx, null, out endpoint)
            if (((delegate* unmanaged<void*, Guid*, uint, void*, void**, int>)Slot(device, 3))(device, &iid, ClsctxAll, null, &endpoint) < 0) return false;
            _endpoint = endpoint;
            return true;
        }
        finally { Release(device); }
    }

    /// <summary>Volume in percent (0..100), or -1 when there is no output device.</summary>
    public int Get(out bool muted)
    {
        muted = false;
        if (_endpoint == null) return -1;
        float level;
        // IAudioEndpointVolume::GetMasterVolumeLevelScalar
        if (((delegate* unmanaged<void*, float*, int>)Slot(_endpoint, 9))(_endpoint, &level) < 0) return -1;
        int mute;
        // IAudioEndpointVolume::GetMute
        if (((delegate* unmanaged<void*, int*, int>)Slot(_endpoint, 15))(_endpoint, &mute) >= 0) muted = mute != 0;
        return (int)MathF.Round(level * 100f);
    }

    public bool Set(int percent)
    {
        if (_endpoint == null) return false;
        // IAudioEndpointVolume::SetMasterVolumeLevelScalar(level, no event context)
        return ((delegate* unmanaged<void*, float, Guid*, int>)Slot(_endpoint, 7))(_endpoint, Math.Clamp(percent, 0, 100) / 100f, null) >= 0;
    }

    public bool SetMute(bool muted)
    {
        if (_endpoint == null) return false;
        // IAudioEndpointVolume::SetMute
        return ((delegate* unmanaged<void*, int, Guid*, int>)Slot(_endpoint, 14))(_endpoint, muted ? 1 : 0, null) >= 0;
    }

    public void Dispose()
    {
        Release(_endpoint);
        Release(_enumerator);
        _endpoint = _enumerator = null;
        if (_uninit) CoUninitialize();
        _uninit = false;
    }

    private static void* Slot(void* obj, int index) => (*(void***)obj)[index];

    private static void Release(void* obj)
    {
        if (obj != null) ((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);
    }
}
