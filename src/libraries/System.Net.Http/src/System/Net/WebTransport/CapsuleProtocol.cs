using System.Threading.Tasks;
using System.IO;
using System.Text;

namespace System.Net.WebTransport;

/// <seealso href="https://datatracker.ietf.org/doc/html/rfc9297"/>
internal abstract class Capsule
{
    public abstract async void Serialize(Stream stream);
}

internal sealed class CloseSessionCapsule : Capsule
{
    public readonly int ApplicationErrorCode { get; }
    /// <summary>
    /// Utf-8 encoded string containing the error message.
    /// </summary>
    public readonly ReadOnlySpan<byte> ApplicationErrorMessage { get; }
    public readonly WebTransportSession Session { get; }
    public CloseWebTransportSession(WebTransportSession session, int applicationErrorCode, ReadOnlySpan<byte> applicationErrorMessage)
    {
        Session = session;
        ApplicationErrorCode = applicationErrorCode;
        ApplicationErrorMessage = applicationErrorMessage;
    }
    public static long CapsuleCode => 0x2843;
    public async void ProcessAsync()
    {
        await Session.CloseAsync(ApplicationErrorCode, ApplicationErrorMessage);
    }
    public static async Task<CloseSessionCapsule> DeserializeAsync(Stream stream, WebTransportSession session, CancellationToken cancellationToken = default)
    {
        long length = VariableLengthIntegerStreamHelper.ReadAsync(stream, cancellationToken);
        byte[] buffer = new byte[length];
        await stream.ReadAsync(buffer, 0, length, cancellationToken);
        bool isSuccess = BinaryPrimitives.TryReadInt32BigEndian(buffer, 0, out int applicationErrorCode);
        Debug.Assert(isSuccess, "Buffer should be large enough");
        ReadOnlySpan<byte> applicationErrorMessage = new ArraySegment<byte>(buffer, 4, buffer.Length - 4);
        return new CloseSessionCapsule(session, applicationErrorCode, applicationErrorMessage);
    }
    public override async Task Serialize(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] errorCodeBuffer = new byte[4];

        VariableLengthIntegerStreamHelper.WriteAsync(stream, CapsuleCode, cancellationToken);
        VariableLengthIntegerStreamHelper.WriteAsync(stream, errorCodeBuffer.Length + ApplicationErrorMessage.Length, cancellationToken);

        bool isSuccess = BinaryPrimitives.TryWriteInt32BigEndian(errorCodeBuffer, ApplicationErrorCode);
        Debug.Assert(isSuccess, "Buffer should be large enough");
        await stream.WriteAsync(errorCodeBuffer, 0, errorCodeBuffer.Length, cancellationToken);

        await stream.WriteAsync(ApplicationErrorMessage, 0, ApplicationErrorMessage.Length, cancellationToken);
        // TODO: is flush needed?
    }
}

internal sealed class DrainSessionCapsule : Capsule
{
    public DrainSessionCapsule() { }
    public static long CapsuleCode => 0x78ae;
    public async void ProcessAsync(WebTransportSession session)
    {
        await session.CloseAsync();
    }
    public static DrainSessionCapsule Deserialize(Stream stream) { }
    public override void Serialize(Stream stream) { }
}

internal sealed class MaxBidirectionalStreamsCapsule : Capsule
{
    public long MaxBidirectionalStreams { get; }
    public MaxBidirectionalStreamsCapsule(long maxBidirectionalStreams)
    {
        MaxBidirectionalStreams = maxBidirectionalStreams;
    }
    public static long CapsuleCode => 0x190B4D3F;
    public void Process(WebTransportSession session)
    {
        session.MaxBidirectionalStreams = MaxBidirectionalStreams;
    }
    public static MaxBidirectionalStreamsCapsule Deserialize(Stream stream) { }
    public override void Serialize(Stream stream) { }
}

internal sealed class MaxUnidirectionalStreamsCapsule : Capsule
{
    public long MaxUnidirectionalStreams { get; }
    public MaxUnidirectionalStreamsCapsule(long maxUnidirectionalStreams)
    {
        MaxUnidirectionalStreams = maxUnidirectionalStreams;
    }
    public static long CapsuleCode => 0x190B4D40;
    public void Process(WebTransportSession session)
    {
        session.MaxUnidirectionalStreams = MaxUnidirectionalStreams;
    }
    public static MaxUnidirectionalStreamsCapsule Deserialize(Stream stream) { }
    public override void Serialize(Stream stream) { }
}

internal sealed class MaxDataCapsule : Capsule
{
    public long MaxData { get; }
    public MaxDataCapsule(long maxData)
    {
        MaxData = maxData;
    }
    public static long CapsuleCode => 0x190B4D3D;
    public void Process(WebTransportSession session)
    {
        session.MaxData = MaxData;
    }
    public static MaxDataCapsule Deserialize(Stream stream) { }
    public override void Serialize(Stream stream) { }
}

internal sealed class CapsuleConsumer
{
    private readonly Stream _capsuleStream;
    private readonly WebTransportSession _session;
    public CapsuleProcessor(Stream capsuleStream, WebTransportSession session)
    {
        _capsuleStream = capsuleStream;
        _session = session;
    }
    /// <exception cref="ObjectDisposedException">When the capsule stream has been disposed.</exception>
    public async Task<Capsule> ProcessNextCapsule(CancellationToken cancellationToken = default)
    {
        long incomingType;
        switch incomingType {
            case CloseSessionCapsule.CapsuleCode:
                CloseSessionCapsule closeSessionCapsule = await CloseSessionCapsule.DeserializeAsync(_capsuleStream, _session, cancellationToken);
                await closeSessionCapsule.ProcessAsync(_session);
                break;
            case DrainSessionCapsule.CapsuleCode:
                DrainSessionCapsule drainSessionCapsule = await DrainSessionCapsule.DeserializeAsync(_capsuleStream, _session, cancellationToken);
                await drainSessionCapsule.ProcessAsync(_session);
                break;
            case MaxBidirectionalStreamsCapsule.CapsuleCode:
                MaxBidirectionalStreamsCapsule maxBidirectionalStreamsCapsule = await MaxBidirectionalStreamsCapsule.DeserializeAsync(_capsuleStream, cancellationToken);
                maxBidirectionalStreamsCapsule.Process(_session);
                break;
            case MaxUnidirectionalStreamsCapsule.CapsuleCode:
                MaxUnidirectionalStreamsCapsule maxUnidirectionalStreamsCapsule = await MaxUnidirectionalStreamsCapsule.DeserializeAsync(_capsuleStream, cancellationToken);
                maxUnidirectionalStreamsCapsule.Process(_session);
                break;
            case MaxDataCapsule.CapsuleCode:
                MaxDataCapsule maxDataCapsule = await MaxDataCapsule.DeserializeAsync(_capsuleStream, cancellationToken);
                maxDataCapsule.Process(_session);
                break;
        }
    }
}
