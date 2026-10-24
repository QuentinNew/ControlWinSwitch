namespace ControlWinSwitch.Input;

/// <summary>
/// One Joy-Con a <see cref="JoyConSlot"/> can hold: a Joy-Con 2 (<see cref="JoyCon2Device"/>, Bluetooth LE) or an
/// original Joy-Con (<see cref="JoyCon1Device"/>, Bluetooth HID). Both report in the Joy-Con 2 shape
/// (<see cref="JoyCon2Input"/>), so a left and a right one of either generation pair up. UI thread only.
/// </summary>
internal interface IJoyCon
{
    JoyConSide Side { get; }

    /// <summary>"AA:BB:CC:DD:EE:FF": the id its profile and picture are stored under.</summary>
    string Mac { get; }

    string Name { get; }

    /// <summary>The stick's ranges from the Joy-Con's memory (null when it holds none).</summary>
    AxisRange[]? Calibration { get; }

    /// <summary>A side button (SL / SR) was held when it connected: the user wants it alone, sideways.</summary>
    bool SideButtonsHeld { get; }

    /// <summary>The HID interface games could read it through (null for a Joy-Con 2, which is no HID device).</summary>
    string? HidPath { get; }

    /// <summary>Raised for every input report.</summary>
    event Action<IJoyCon, JoyCon2Input>? Input;

    /// <summary>Raised once when it is gone (or <see cref="Disconnect"/> was called).</summary>
    event Action<IJoyCon>? Removed;

    /// <summary>Battery level 0 (empty) to 4 (full), or -1 when unknown, as of <paramref name="input"/>.</summary>
    (int Level, bool Charging) Battery(in JoyCon2Input input);

    /// <summary>Lights the player LEDs (one bit per LED).</summary>
    void SetLeds(byte mask);

    /// <summary>Rumbles from Xbox motor strengths (0..255) scaled by <paramref name="percent"/>; (0, 0) stops it.</summary>
    bool SetRumbleMotors(byte large, byte small, int percent);

    void Disconnect();
}
