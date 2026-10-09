using System.Net.Sockets;
using OfficeChat.Services;

namespace OfficeChat.Platform;

/// <summary>
/// Одна копия программы на пользователя. Первая копия слушает Unix-сокет в $XDG_RUNTIME_DIR
/// (у каждого пользователя свой); вторая подключается к нему — это просьба показать окно — и выходит.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _path;
    private Socket? _listener;

    /// <summary>Другая копия попросила показать окно (вызывается не в UI-потоке).</summary>
    public event Action? ShowRequested;

    public SingleInstance()
    {
        var name = $"officechat-{Environment.UserName}.sock";
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        _path = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? Path.Combine(dir, name) : "";
        // Путь Unix-сокета ограничен ~108 байтами — если не помещается, берём короткий в /tmp.
        if (_path.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(_path) > 100)
            _path = Path.Combine("/tmp", name);
    }

    /// <summary>true — мы первая копия; false — уже запущена, ей отправлена просьба показать окно.</summary>
    public bool TryBecomePrimary()
    {
        if (File.Exists(_path))
        {
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(_path));
                client.Send("show"u8.ToArray());
                return false;
            }
            catch (SocketException)
            {
                // Сокет остался от упавшей копии — убираем и занимаем сами.
                File.Delete(_path);
            }
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(4);
        _ = AcceptLoopAsync(_listener);
        return true;
    }

    private async Task AcceptLoopAsync(Socket listener)
    {
        while (true)
        {
            try
            {
                using var client = await listener.AcceptAsync();
                ShowRequested?.Invoke();
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Log.Warn("Ошибка сокета единственной копии", ex);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_listener == null) return;
        _listener.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
