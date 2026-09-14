using System.IO.Pipes;
using Ralven.Broker;
using Xunit;

namespace Ralven.Tests.Broker;

public sealed class NamedPipeEventWriterTests
{
    [Fact]
    public void ClientOptions_DoNotRepeatTheCrossElevationOwnerCheck()
    {
        Assert.Equal(PipeOptions.Asynchronous, NamedPipeEventWriter.ClientPipeOptions);
        Assert.Equal(PipeOptions.None, NamedPipeEventWriter.ClientPipeOptions & PipeOptions.CurrentUserOnly);
    }
}
