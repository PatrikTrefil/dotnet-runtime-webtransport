namespace System.Net.WebTransport;

public readonly record struct Priority(byte urgency, bool incremental)
{
    public static Priority Default() => new Priority(3, false);
    private byte _urgency = urgency;
    /// <summary>
    /// The value is an unsigned integer in the range [0-7].
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the provided value is out of range.</exception>
    public byte Urgency {
        get => _urgency;
        init {
            if (value > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(Urgency), value, "Value msut be in the range [0-7]")
            }
            _urgency = value;
        }
    }
    /// <summary>
    /// If true, indicates that the data can be processed incrementally.
    /// </summary>
    public bool Incremental { get; init; } = incremental;
}
