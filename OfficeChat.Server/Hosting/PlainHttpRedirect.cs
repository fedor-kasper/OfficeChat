using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Сервер работает только по HTTPS. Если адрес открыли как http://… (браузер так делает, когда схему не
/// написали), без этого соединение молча закрылось бы и браузер показал бы «пустой ответ».
/// Здесь такой запрос узнаётся по первому байту и получает перенаправление на https://.
/// </summary>
public static class PlainHttpRedirect
{
    private const byte TlsHandshake = 0x16;
    private const int MaxHeaderBytes = 16 * 1024;

    public static ConnectionDelegate Middleware(ConnectionDelegate next) => async connection =>
    {
        var input = connection.Transport.Input;
        var first = await input.ReadAtLeastAsync(1);
        if (first.Buffer.IsEmpty)
        {
            input.AdvanceTo(first.Buffer.Start);
            return;
        }
        var isTls = first.Buffer.FirstSpan[0] == TlsHandshake;
        input.AdvanceTo(first.Buffer.Start); // ничего не съели — HTTPS читает с начала
        if (isTls)
        {
            await next(connection);
            return;
        }

        // Обычный HTTP: дочитываем заголовки (до пустой строки) и отвечаем перенаправлением.
        string head;
        while (true)
        {
            var result = await input.ReadAsync();
            var text = Encoding.ASCII.GetString(result.Buffer.Slice(0, Math.Min(result.Buffer.Length, MaxHeaderBytes)));
            if (text.Contains("\r\n\r\n") || result.IsCompleted || result.Buffer.Length >= MaxHeaderBytes)
            {
                head = text;
                input.AdvanceTo(result.Buffer.End);
                break;
            }
            input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }

        var lines = head.Split("\r\n");
        var path = lines[0].Split(' ') is [_, var target, ..] && target.StartsWith('/') ? target : "/";
        var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
        if (string.IsNullOrEmpty(host) || host.Any(c => c is '\r' or '\n' or ' ')) host = null;

        var body = host == null
            ? "Сервер OfficeChat работает по HTTPS: откройте адрес, начиная с https://"
            : $"Сервер OfficeChat работает по HTTPS: https://{host}{path}";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var response = new StringBuilder()
            .Append(host == null ? "HTTP/1.1 400 Bad Request\r\n" : "HTTP/1.1 301 Moved Permanently\r\n")
            .Append(host == null ? "" : $"Location: https://{host}{path}\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await connection.Transport.Output.WriteAsync(Encoding.ASCII.GetBytes(response));
        await connection.Transport.Output.WriteAsync(bodyBytes);
        await connection.Transport.Output.FlushAsync();
    };
}
