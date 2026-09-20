using System.IO.Pipes;
using LazyForza.EstatePeer;
using LazyForza.EstatePeer.Host;

if (args.Length != 2 || args[0] != "--control-pipe" || !args[1].StartsWith("LazyForza.Peer.", StringComparison.Ordinal)) return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    await pipe.ConnectAsync(startup.Token);
    var settings = await PeerPipe.ReadAsync<PeerHostStart>(pipe, startup.Token);
    await using var room = new PeerRoom(settings);
    await room.StartAsync(startup.Token);
    await PeerPipe.WriteAsync(pipe, new PeerHostReply(true, Invitation: room.Invitation.Encode(), Port: room.Port), startup.Token);
    while (pipe.IsConnected)
    {
        var read = PeerPipe.ReadAsync<PeerHostCommand>(pipe, CancellationToken.None);
        if (await Task.WhenAny(read, room.Completion) == room.Completion)
        {
            await room.Completion;
            break;
        }
        var command = await read;
        if (command.Action == "stop") break;
        await PeerPipe.WriteAsync(pipe, room.Control(command), CancellationToken.None);
    }
    return 0;
}
catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)
{
    if (pipe.IsConnected)
    {
        try { await PeerPipe.WriteAsync(pipe, new PeerHostReply(false, error.Message), startup.Token); }
        catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
    }
    return 1;
}
