using System.Runtime.InteropServices;
using OfficeChat.Services;

namespace OfficeChat.Platform;

/// <summary>
/// Чтение буфера обмена X11 напрямую. Avalonia не дочитывает большие данные: X11 передаёт их по частям (INCR),
/// и скриншот в пару мегабайт из буфера не вставлялся. Здесь — своё соединение с X-сервером и окно-получатель,
/// данные собираются по частям до конца.
/// </summary>
public static class X11Clipboard
{
    private const int SelectionNotify = 31;
    private const int PropertyNotify = 28;
    private const long PropertyChangeMask = 1L << 22;
    private const int PropertyNewValue = 0;
    private const int XEventSize = 192;

    /// <summary>Данные буфера в указанном формате (например, image/png). null — нет такого или не удалось.</summary>
    public static Task<byte[]?> ReadAsync(string target, TimeSpan timeout) => Task.Run(() =>
    {
        try
        {
            return Read(target, timeout);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать буфер обмена ({target}) напрямую", ex);
            return null;
        }
    });

    /// <summary>Какие форматы предлагает владелец буфера (image/png, text/plain, text/uri-list…). null — не удалось.</summary>
    public static Task<List<string>?> TargetsAsync(TimeSpan timeout) => Task.Run(() =>
    {
        try
        {
            var data = Read("TARGETS", timeout);
            if (data == null) return null;
            // Список атомов (по 64 бита на 64-битной системе) — переводим в имена.
            var names = new List<string>();
            var connection = XOpenDisplay(IntPtr.Zero);
            if (connection == IntPtr.Zero) return null;
            try
            {
                for (var i = 0; i + IntPtr.Size <= data.Length; i += IntPtr.Size)
                {
                    var atom = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(data, i) : BitConverter.ToInt32(data, i);
                    var name = XGetAtomName(connection, atom);
                    if (name == IntPtr.Zero) continue;
                    names.Add(Marshal.PtrToStringAnsi(name) ?? "");
                    XFree(name);
                }
            }
            finally
            {
                XCloseDisplay(connection);
            }
            return names;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    });

    private static byte[]? Read(string target, TimeSpan timeout)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return null;
        var window = IntPtr.Zero;
        var ev = Marshal.AllocHGlobal(XEventSize);
        try
        {
            var root = XDefaultRootWindow(display);
            window = XCreateSimpleWindow(display, root, 0, 0, 1, 1, 0, UIntPtr.Zero, UIntPtr.Zero);
            XSelectInput(display, window, (IntPtr)PropertyChangeMask);
            var clipboard = XInternAtom(display, "CLIPBOARD", false);
            var targetAtom = XInternAtom(display, target, false);
            var property = XInternAtom(display, "OFFICECHAT_CLIPBOARD", false);
            var incr = XInternAtom(display, "INCR", false);

            XConvertSelection(display, clipboard, targetAtom, property, window, IntPtr.Zero);
            XFlush(display);

            var deadline = DateTime.UtcNow + timeout;
            if (!WaitFor(display, ev, deadline, e => Marshal.ReadInt32(e) == SelectionNotify)) return null;
            if (Marshal.ReadIntPtr(ev, 56) == IntPtr.Zero) return null; // владелец отказал: такого формата нет

            var (type, data) = GetProperty(display, window, property, delete: true);
            if (type != incr) return data;

            // Большие данные: владелец кладёт их частями, после каждой нашей «прочитал» (удаление свойства).
            using var result = new MemoryStream();
            while (true)
            {
                if (!WaitFor(display, ev, deadline + timeout, e =>
                        Marshal.ReadInt32(e) == PropertyNotify && Marshal.ReadIntPtr(e, 40) == property &&
                        Marshal.ReadInt32(e, 56) == PropertyNewValue))
                    return null;
                var (_, chunk) = GetProperty(display, window, property, delete: true);
                if (chunk == null || chunk.Length == 0) break;
                result.Write(chunk);
            }
            return result.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
            if (window != IntPtr.Zero) XDestroyWindow(display, window);
            XCloseDisplay(display);
        }
    }

    private static bool WaitFor(IntPtr display, IntPtr ev, DateTime deadline, Func<IntPtr, bool> match)
    {
        while (DateTime.UtcNow < deadline)
        {
            while (XPending(display) > 0)
            {
                XNextEvent(display, ev);
                if (match(ev)) return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }

    private static (IntPtr Type, byte[]? Data) GetProperty(IntPtr display, IntPtr window, IntPtr property, bool delete)
    {
        // long_length — в 32-битных единицах; берём с запасом (до 256 МБ), лишнего X-сервер не вернёт.
        if (XGetWindowProperty(display, window, property, IntPtr.Zero, (IntPtr)(64 * 1024 * 1024), delete, IntPtr.Zero,
                out var type, out var format, out var items, out _, out var data) != 0)
            return (IntPtr.Zero, null);
        try
        {
            if (data == IntPtr.Zero) return (type, Array.Empty<byte>());
            // Формат 32 (атомы, числа) Xlib отдаёт как long — по IntPtr.Size байт на элемент.
            var size = format switch { 8 => 1, 16 => 2, _ => IntPtr.Size };
            var bytes = new byte[(long)items * size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return (type, bytes);
        }
        finally
        {
            if (data != IntPtr.Zero) XFree(data);
        }
    }

    private const string X11 = "libX11.so.6";

    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width,
        uint height, uint borderWidth, UIntPtr border, UIntPtr background);
    [DllImport(X11)] private static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport(X11)] private static extern int XSelectInput(IntPtr display, IntPtr window, IntPtr mask);
    [DllImport(X11)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(X11)] private static extern int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target,
        IntPtr property, IntPtr requestor, IntPtr time);
    [DllImport(X11)] private static extern int XFlush(IntPtr display);
    [DllImport(X11)] private static extern int XPending(IntPtr display);
    [DllImport(X11)] private static extern int XNextEvent(IntPtr display, IntPtr ev);
    [DllImport(X11)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        IntPtr offset, IntPtr length, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr items, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(X11)] private static extern int XFree(IntPtr data);
    [DllImport(X11)] private static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);
}
