using System.Threading.Channels;

namespace Deckard.Core.Capture;

/// <summary>
/// Produces frames of the game window. Sources must only read what is presented on screen;
/// they never touch the game process (no memory reads, injection or hooks).
/// </summary>
public interface IFrameSource : IAsyncDisposable
{
    /// <summary>
    /// Latest-frame channel: when the consumer falls behind, older frames are dropped (and disposed)
    /// so perception always works on the newest image. Consumers own and must dispose frames they read.
    /// </summary>
    ChannelReader<Frame> Frames { get; }

    void Start();
}
