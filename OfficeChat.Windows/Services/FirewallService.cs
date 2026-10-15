using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace OfficeChat.Services;

public enum FirewallState
{
    /// <summary>Проверить не удалось (нет доступа, сторонний брандмауэр) — молчим.</summary>
    Unknown,
    /// <summary>Входящие соединения к OfficeChat разрешены (или брандмауэр выключен).</summary>
    Allowed,
    /// <summary>Есть запрещающее правило или нет разрешающего — к нам не смогут подключиться.</summary>
    Blocked,
}

/// <summary>
/// Проверка и настройка брандмауэра Windows для OfficeChat.
/// Типичная беда: при первом запуске разрешение дали только для «Частных» сетей, а сеть помечена
/// как «Общественная», или в окне брандмауэра нажали «Отмена» — тогда Windows создаёт
/// запрещающие правила. Обнаружение при этом работает (мы сами рассылаем), а сообщения к нам не доходят.
/// </summary>
public static class FirewallService
{
    private const int DirectionIn = 1;   // NET_FW_RULE_DIR_IN
    private const int ActionBlock = 0;   // NET_FW_ACTION_BLOCK
    private const int ActionAllow = 1;   // NET_FW_ACTION_ALLOW
    private const int ProtocolTcp = 6;
    private const int ProtocolAny = 256;
    private static readonly int[] ProfileBits = { 1, 2, 4 }; // домен, частная, общественная

    public static FirewallState Check(int messagingPort)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return FirewallState.Unknown;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return FirewallState.Unknown;
            var policy = Activator.CreateInstance(type)!;

            var currentProfiles = (int)Get(policy, "CurrentProfileTypes")!;
            var enabledProfiles = ProfileBits
                .Where(bit => (currentProfiles & bit) != 0 && (bool)Get(policy, "FirewallEnabled", bit)!)
                .Aggregate(0, (acc, bit) => acc | bit);
            if (enabledProfiles == 0)
            {
                Log.Info("Брандмауэр Windows выключен для текущей сети");
                return FirewallState.Allowed;
            }

            bool allowed = false, blocked = false;
            var rules = (System.Collections.IEnumerable)Get(policy, "Rules")!;
            foreach (var rule in rules)
            {
                if (!(bool)Get(rule, "Enabled")! || (int)Get(rule, "Direction")! != DirectionIn) continue;
                if (((int)Get(rule, "Profiles")! & enabledProfiles) == 0) continue;

                var app = Get(rule, "ApplicationName") as string;
                var action = (int)Get(rule, "Action")!;
                var forUs = !string.IsNullOrEmpty(app) &&
                            string.Equals(Environment.ExpandEnvironmentVariables(app), exe, StringComparison.OrdinalIgnoreCase);
                // Правило «для всех программ» на наш TCP-порт тоже считается (его мог сделать администратор).
                var forOurPort = string.IsNullOrEmpty(app) &&
                                 (int)Get(rule, "Protocol")! is ProtocolTcp or ProtocolAny &&
                                 (Get(rule, "LocalPorts") as string)?.Split(',').Contains(messagingPort.ToString()) == true;

                if (forUs && action == ActionBlock) blocked = true;
                else if ((forUs || forOurPort) && action == ActionAllow) allowed = true;
            }

            var state = blocked || !allowed ? FirewallState.Blocked : FirewallState.Allowed;
            Log.Info($"Брандмауэр Windows: {(blocked ? "есть запрещающее правило" : allowed ? "входящие разрешены" : "разрешающего правила нет")} " +
                     $"(профили сети: {currentProfiles}, программа: {exe})");
            return state;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось проверить брандмауэр Windows", ex);
            return FirewallState.Unknown;
        }
    }

    /// <summary>
    /// Разрешает входящие для OfficeChat во всех типах сети: убирает старые правила этой программы
    /// (в том числе запрещающие) и добавляет разрешающее. Нужны права администратора — Windows спросит (UAC).
    /// Возвращает false, если пользователь отказался или команда не удалась.
    /// </summary>
    public static bool AllowIncoming()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;

        var commands =
            $"netsh advfirewall firewall delete rule name=all program=\"{exe}\" >nul & " +
            $"netsh advfirewall firewall add rule name=\"OfficeChat\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commands}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null) return false;
            process.WaitForExit(30_000);
            Log.Info($"Правило брандмауэра для {exe}: код выхода {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Пользователь отказался от запроса прав администратора (UAC)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось добавить правило брандмауэра", ex);
            return false;
        }
    }

    private static object? Get(object target, string property, params object[] args) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, args);
}
