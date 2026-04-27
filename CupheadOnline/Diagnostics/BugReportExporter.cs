using System;
using System.IO;
using System.Text;
using BepInEx;

namespace CupheadOnline.Diagnostics
{
    internal static class BugReportExporter
    {
        static string _lastAutoExportKey = string.Empty;
        static DateTime _lastAutoExportUtc = DateTime.MinValue;

        public static string Export()
        {
            return Export("Manual export", false);
        }

        public static bool TryAutoExport(string reason, out string reportDir)
        {
            reportDir = string.Empty;
            if (!Plugin.AutoExportBugReports)
                return false;

            string key = GetPairingKey() + "|" + (reason ?? string.Empty);
            DateTime now = DateTime.UtcNow;
            if (key == _lastAutoExportKey && (now - _lastAutoExportUtc).TotalSeconds < 10.0)
                return false;

            try
            {
                reportDir = Export(reason, true);
                _lastAutoExportKey = key;
                _lastAutoExportUtc = now;
                Plugin.Log.LogInfo("[BugReport] Auto-exported paired report: " + reportDir);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[BugReport] Auto-export failed: " + ex.Message);
                return false;
            }
        }

        public static string Export(string reason, bool automatic)
        {
            string root = Path.Combine(
                Path.Combine(Paths.BepInExRootPath, "CupHeads"),
                "Reports");
            Directory.CreateDirectory(root);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string prefix = automatic ? "auto-report-" : "report-";
            string reportDir = Path.Combine(root, prefix + stamp + "-" + GetRoleSlug() + "-" + GetPairingSlug());
            Directory.CreateDirectory(reportDir);

            File.WriteAllText(
                Path.Combine(reportDir, "pairing.txt"),
                BuildPairingText(reason, automatic),
                Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(reportDir, "diagnostics.txt"),
                BuildReportText(reason, automatic),
                Encoding.UTF8);

            CopyIfExists(
                Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
                Path.Combine(reportDir, "LogOutput.log"));

            CopyIfExists(
                Path.Combine(Paths.ConfigPath, PluginInfo.GUID + ".cfg"),
                Path.Combine(reportDir, PluginInfo.GUID + ".cfg"));

            return reportDir;
        }

        static string BuildReportText(string reason, bool automatic)
        {
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            sb.AppendLine("CupHeads Bug Report");
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Export Mode: " + (automatic ? "Automatic" : "Manual"));
            sb.AppendLine("Export Reason: " + (string.IsNullOrEmpty(reason) ? "(none)" : reason));
            sb.AppendLine("Game Root: " + Paths.GameRootPath);
            sb.AppendLine("BepInEx Root: " + Paths.BepInExRootPath);
            sb.AppendLine("Unity Version: " + UnityEngine.Application.unityVersion);
            sb.AppendLine("Platform: " + UnityEngine.Application.platform);
            sb.AppendLine("Data Path: " + UnityEngine.Application.dataPath);
            sb.AppendLine();
            sb.AppendLine(Plugin.BuildDiagnosticsReport());
            sb.AppendLine();
            sb.AppendLine("System Info:");
            sb.AppendLine("Device: " + UnityEngine.SystemInfo.deviceModel);
            sb.AppendLine("OS: " + UnityEngine.SystemInfo.operatingSystem);
            sb.AppendLine("CPU: " + UnityEngine.SystemInfo.processorType);
            sb.AppendLine("GPU: " + UnityEngine.SystemInfo.graphicsDeviceName);
            sb.AppendLine("Memory: " + UnityEngine.SystemInfo.systemMemorySize + " MB");
            return sb.ToString().Replace("\n", nl);
        }

        static string BuildPairingText(string reason, bool automatic)
        {
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            sb.AppendLine("CupHeads Paired Log Info");
            sb.AppendLine("Pairing Key: " + GetPairingKey());
            sb.AppendLine("Role: " + GetRoleLabel());
            sb.AppendLine("Export Mode: " + (automatic ? "Automatic" : "Manual"));
            sb.AppendLine("Export Reason: " + (string.IsNullOrEmpty(reason) ? "(none)" : reason));
            sb.AppendLine("Generated Local: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Generated UTC: " + DateTime.UtcNow.ToString("o"));

            if (Plugin.Net != null)
            {
                sb.AppendLine("Network State: " + Plugin.Net.CurrentStateName);
                sb.AppendLine("Lobby ID: " + (string.IsNullOrEmpty(Plugin.Net.CurrentLobbyId) ? "(none)" : Plugin.Net.CurrentLobbyId));
                sb.AppendLine("Peer: " + (string.IsNullOrEmpty(Plugin.Net.CurrentPeerName) ? "(none)" : Plugin.Net.CurrentPeerName));
                sb.AppendLine("Last Status: " + (Plugin.Net.LastStatusMessage ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
                sb.AppendLine("Last Failure: " + (Plugin.Net.LastFailureReason ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
            }

            sb.AppendLine();
            sb.AppendLine("To pair reports, collect this folder from both players after the same run.");
            sb.AppendLine("The two folders should have the same Pairing Key when they came from the same lobby/session.");
            return sb.ToString().Replace("\n", nl);
        }

        static string GetPairingKey()
        {
            if (Plugin.Net == null)
                return "offline-no-session";

            if (Plugin.Net.IsLanEmulationActive)
                return "lan-session-port-" + Plugin.LanPort;

            string lobbyId = Plugin.Net.CurrentLobbyId;
            if (string.IsNullOrEmpty(lobbyId))
                return "no-lobby-" + Plugin.Net.CurrentStateName;

            return "steam-lobby-" + lobbyId;
        }

        static string GetPairingSlug()
        {
            return SanitizeFilePart(Shorten(GetPairingKey(), 36));
        }

        static string GetRoleLabel()
        {
            if (Plugin.Net == null)
                return "Offline";
            if (Plugin.Net.IsConnected || Plugin.Net.IsInLobby)
                return Plugin.Net.IsHost ? "Host" : "Guest";
            return "Offline";
        }

        static string GetRoleSlug()
        {
            return GetRoleLabel().ToLowerInvariant();
        }

        static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength);
        }

        static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "none";

            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                    chars[i] = '-';
            }
            return new string(chars).Trim('-');
        }

        static void CopyIfExists(string source, string destination)
        {
            if (!File.Exists(source))
                return;

            File.Copy(source, destination, true);
        }
    }
}
