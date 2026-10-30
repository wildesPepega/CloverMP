namespace CloverPitCoop.Net;

// Line-based message link. On the host one link carries several clients: every client gets a
// player number (1, 2, ...) when it connects; Send goes to all of them, SendTo to one.
// On a client the only peer is the host (player 0).
internal interface ILink : System.IDisposable
{
    bool Connected { get; }
    string Error { get; }

    // Host only: players that connected since the last call (to greet them) and that left.
    bool TryJoined(out int player);
    bool TryLeft(out int player);

    void Send(string line);
    void SendTo(int player, string line);
    bool TryReceive(out int fromPlayer, out string line);

    // Pumps the transport; called every frame.
    void Poll();
}
