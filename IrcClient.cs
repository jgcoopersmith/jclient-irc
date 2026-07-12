using System.Net.Sockets;
using System.Text;

namespace IRCClient;

// RFC 1459 / RFC 2812 IRC client protocol implementation
public class IrcMessage
{
    public string? Prefix { get; init; }
    public string Command { get; init; } = "";
    public string[] Params { get; init; } = [];

    public string? PrefixNick => Prefix?.Split('!')[0];

    // RFC 1459 §2.3 message format: [:prefix] command [params] [:trailing]
    public static IrcMessage Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return new IrcMessage();

        string? prefix = null;
        int pos = 0;

        if (raw[0] == ':')
        {
            int space = raw.IndexOf(' ');
            prefix = raw[1..space];
            pos = space + 1;
        }

        var parts = new List<string>();
        while (pos < raw.Length)
        {
            if (raw[pos] == ':')
            {
                parts.Add(raw[(pos + 1)..]);
                break;
            }
            int next = raw.IndexOf(' ', pos);
            if (next < 0)
            {
                parts.Add(raw[pos..]);
                break;
            }
            parts.Add(raw[pos..next]);
            pos = next + 1;
        }

        return new IrcMessage
        {
            Prefix = prefix,
            Command = parts.Count > 0 ? parts[0] : "",
            Params = parts.Count > 1 ? parts[1..].ToArray() : []
        };
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Prefix != null) sb.Append($":{Prefix} ");
        sb.Append(Command);
        if (Params.Length > 0)
        {
            for (int i = 0; i < Params.Length - 1; i++)
                sb.Append($" {Params[i]}");
            // last param may need trailing colon if it contains spaces
            var last = Params[^1];
            sb.Append(last.Contains(' ') ? $" :{last}" : $" {last}");
        }
        return sb.ToString();
    }
}

public class IrcConnection : IDisposable
{
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private readonly SynchronizationContext _ctx;

    public event Action<IrcMessage>? MessageReceived;
    public event Action<string>? RawLineReceived;
    public event Action? Disconnected;

    public bool IsConnected => _tcp?.Connected ?? false;
    public string? CurrentNick { get; private set; }

    public IrcConnection()
    {
        _ctx = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public async Task ConnectAsync(string host, int port, string nick, string? password = null)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true, NewLine = "\r\n" };

        CurrentNick = nick;
        _cts = new CancellationTokenSource();

        // RFC 2812 §3.1 connection registration
        if (!string.IsNullOrEmpty(password))
            await SendRawAsync($"PASS {password}");
        await SendRawAsync($"NICK {nick}");
        await SendRawAsync($"USER {nick} 0 * :{nick}");

        _ = ReadLoopAsync(_cts.Token);
    }

    public async Task SendRawAsync(string line)
    {
        if (_writer == null) return;
        await _writer.WriteLineAsync(line);
    }

    // RFC 2812 §3.2.1
    public Task JoinAsync(string channel) => SendRawAsync($"JOIN {channel}");

    // RFC 2812 §3.2.2
    public Task PartAsync(string channel, string? reason = null) =>
        SendRawAsync(reason != null ? $"PART {channel} :{reason}" : $"PART {channel}");

    // RFC 1459 §4.4.1
    public Task PrivMsgAsync(string target, string text) => SendRawAsync($"PRIVMSG {target} :{text}");

    // RFC 2812 §3.7.2
    public Task PongAsync(string server) => SendRawAsync($"PONG :{server}");

    // RFC 2812 §3.1.7
    public Task QuitAsync(string reason = "Goodbye") => SendRawAsync($"QUIT :{reason}");

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null) break;

                var raw = line;
                var msg = IrcMessage.Parse(raw);

                // RFC 1459 §4.6.2 — must respond to PING immediately
                if (msg.Command == "PING")
                {
                    await PongAsync(msg.Params.Length > 0 ? msg.Params[0] : "");
                }

                // Track nick changes from server (001 = welcome)
                if (msg.Command == "001" && msg.Params.Length > 0)
                    CurrentNick = msg.Params[0];

                _ctx.Post(_ =>
                {
                    RawLineReceived?.Invoke(raw);
                    MessageReceived?.Invoke(msg);
                }, null);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            _ctx.Post(_ => Disconnected?.Invoke(), null);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _writer?.Dispose();
        _reader?.Dispose();
        _tcp?.Dispose();
    }
}
