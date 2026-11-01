namespace CloverPitCoop.Net;

// Line-based message link. On the host one link carries several clients: every client gets a
// player number (1, 2, ...) when it connects; Send goes to all of them, SendTo to one.
// On a client the only peer is the host (player 0).
internal interface ILink : System.IDisposable
{
    bool Connected { get; }

    // Set only when the link has given up for good; short interruptions do not set it.
    string Error { get; }

    // Host only: players that connected since the last call (to greet them) and that left.
    bool TryJoined(out int player);
    bool TryLeft(out int player);

    // Host only: a player's connection broke (the slot is kept for a while) or came back.
    bool TryInterrupted(out int player);
    bool TryRejoined(out int player);

    // Client only: the connection came back after an interruption.
    bool TryReconnected();

    void Send(string line);
    void SendTo(int player, string line);

    // May get lost, never delays anything else. For data that is replaced soon anyway.
    void SendVolatile(string line);

    bool TryReceive(out int fromPlayer, out string line);

    // Pumps the transport; called every frame.
    void Poll();
}
