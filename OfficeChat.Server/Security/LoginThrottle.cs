using System.Collections.Concurrent;

namespace OfficeChat.Server.Security;

/// <summary>
/// Защита от подбора пароля: после 5 неудачных попыток подряд для логина (или с одного адреса)
/// следующие попытки отклоняются минуту.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntil)> _state = new();

    public bool IsLocked(params string[] keys)
    {
        var now = DateTime.UtcNow;
        return keys.Any(k => _state.TryGetValue(k, out var s) && s.LockedUntil > now);
    }

    public void Failed(params string[] keys)
    {
        foreach (var key in keys)
            _state.AddOrUpdate(key, _ => (1, DateTime.MinValue), (_, s) =>
            {
                var failures = s.Failures + 1;
                return failures >= MaxFailures ? (0, DateTime.UtcNow + Lockout) : (failures, s.LockedUntil);
            });
    }

    public void Succeeded(params string[] keys)
    {
        foreach (var key in keys) _state.TryRemove(key, out _);
    }
}
