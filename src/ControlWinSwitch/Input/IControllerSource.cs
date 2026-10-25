namespace ControlWinSwitch.Input;

/// <summary>
/// What drives one controller slot: a Pro Controller (<see cref="HidControllerReader"/>) or one or two Joy-Cons of
/// either generation (<see cref="JoyConSlot"/>). Both deliver Pro Controller shaped reports, through <see cref="ControllerSet"/>'s events.
/// </summary>
internal interface IControllerSource
{
    bool IsOpen { get; }

    /// <summary>Stable identity the slot's name and picture are stored under (a MAC address once known). Null when closed.</summary>
    string? Id { get; }

    /// <summary>The right Joy-Con of a pair, which keeps its own picture (null for anything else).</summary>
    string? PartnerId { get; }

    /// <summary>The temporary id used until the MAC is known (null when there is none).</summary>
    string? FallbackId { get; }

    /// <summary>The identity is the MAC address.</summary>
    bool HasMac { get; }

    /// <summary>The HID interfaces to hide from games while the slot drives a virtual pad.</summary>
    IReadOnlyList<string> HidPaths { get; }

    /// <summary>
    /// Where the deadzone of the slot's left or right stick is stored: the profile id, and whether it is that profile's
    /// right-stick field. Null when the slot has no such stick.
    /// </summary>
    (string Id, bool RightField)? StickProfile(bool right);

    /// <summary>Starts or stops the rumble motors. Returns false when the controller is absent or cannot rumble.</summary>
    bool SetRumble(bool on);

    /// <summary>Rumbles from Xbox motor strengths (0..255) scaled by <paramref name="percent"/>; (0, 0) stops it.</summary>
    bool SetRumbleMotors(byte large, byte small, int percent);
}

/// <summary>The controller slots as the app reads them: <see cref="ControllerSet"/> in the app, a fake in tests.</summary>
internal interface IControllerSlots
{
    /// <summary>What drives the slot (a closed source when the slot is free).</summary>
    IControllerSource Source(int slot);

    /// <summary>The physical controllers in the slot whose MAC is known, with their kind.</summary>
    IEnumerable<(string Mac, string Kind)> Devices(int slot);
}
