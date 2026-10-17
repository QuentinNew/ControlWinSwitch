namespace ControlWin.Input;

/// <summary>Up to four Pro Controllers, one <see cref="HidControllerReader"/> per slot.</summary>
internal sealed class ControllerSet : IDisposable
{
    public const int Slots = 4;

    public HidControllerReader[] Readers { get; } = new HidControllerReader[Slots];

    public ControllerSet()
    {
        for (int i = 0; i < Slots; i++) Readers[i] = new HidControllerReader(i);
    }

    public bool AnyOpen => Readers.Any(r => r.IsOpen);

    public int OpenCount => Readers.Count(r => r.IsOpen);

    /// <summary>Opens every controller that is present and not open yet, each in the first free slot.</summary>
    public void TryOpen()
    {
        var taken = Readers.Where(r => r.Path is not null).Select(r => r.Path!).ToList();
        foreach (var reader in Readers)
        {
            if (reader.IsOpen) continue;
            reader.TryOpen(taken);
            if (reader.Path is not null) taken.Add(reader.Path);
        }
    }

    public void OnInterfaceRemoved(string path)
    {
        foreach (var reader in Readers) reader.OnInterfaceRemoved(path);
    }

    public void Dispose()
    {
        foreach (var reader in Readers) reader.Dispose();
    }
}
