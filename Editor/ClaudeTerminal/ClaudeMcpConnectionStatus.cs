using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

namespace ClaudeTerminal.Editor
{
    public static class ClaudeMcpConnectionStatus
    {
        private const string StdioBridgeHostTypeName =
            "MCPForUnity.Editor.Services.Transport.Transports.StdioBridgeHost, MCPForUnity.Editor";
        private const string McpRunStateRelativePath = @"Library\MCPForUnity\RunState";
        private const string HttpPidFilePattern = "mcp_http_*.pid";

        public static bool IsExternalConnectionLikely(int activeClientCount, bool terminalIsRunning)
        {
            return IsExternalConnectionLikely(activeClientCount, httpClaudeCodeSessionRunning: false, terminalIsRunning);
        }

        public static bool IsExternalConnectionLikely(
            int activeClientCount,
            bool httpClaudeCodeSessionRunning,
            bool terminalIsRunning)
        {
            return !terminalIsRunning && (activeClientCount > 0 || httpClaudeCodeSessionRunning);
        }

        public static string BuildExternalConnectionWarning(int activeClientCount)
        {
            return BuildExternalConnectionWarning(activeClientCount, httpClaudeCodeSessionRunning: false);
        }

        public static string BuildExternalConnectionWarning(int activeClientCount, bool httpClaudeCodeSessionRunning)
        {
            var signalText = activeClientCount > 0
                ? $"stdio 活跃客户端数: {activeClientCount}"
                : string.Empty;

            if (httpClaudeCodeSessionRunning)
            {
                signalText = string.IsNullOrEmpty(signalText)
                    ? "HTTP MCP 服务和 Claude Code 进程正在运行"
                    : signalText + "；HTTP MCP 服务和 Claude Code 进程正在运行";
            }

            if (string.IsNullOrEmpty(signalText))
            {
                signalText = "检测到 Unity MCP 外部连接信号";
            }

            return $"检测到外部 Claude Code / Unity MCP 会话正在运行（{signalText}）。如果不考虑多会话/多线程，请先关闭外部 Claude Code 终端，再从 Unity 内启动。";
        }

        public static int GetActiveClientCount()
        {
            try
            {
                var hostType = Type.GetType(StdioBridgeHostTypeName);
                if (hostType == null)
                {
                    return 0;
                }

                var isRunningProperty = hostType.GetProperty("IsRunning", BindingFlags.Public | BindingFlags.Static);
                if (isRunningProperty?.GetValue(null) is bool isRunning && !isRunning)
                {
                    return 0;
                }

                var lockField = hostType.GetField("clientsLock", BindingFlags.NonPublic | BindingFlags.Static);
                var clientsField = hostType.GetField("activeClients", BindingFlags.NonPublic | BindingFlags.Static);
                var clients = clientsField?.GetValue(null);
                var countProperty = clients?.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (countProperty == null)
                {
                    return 0;
                }

                var lockObject = lockField?.GetValue(null);
                if (lockObject == null)
                {
                    return ReadClientCount(countProperty, clients);
                }

                lock (lockObject)
                {
                    return ReadClientCount(countProperty, clients);
                }
            }
            catch
            {
                return 0;
            }
        }

        private static int ReadClientCount(PropertyInfo countProperty, object clients)
        {
            return countProperty.GetValue(clients) is int count ? count : 0;
        }

        public static bool ConfirmStartWhenExternalConnectionLikely(bool terminalIsRunning)
        {
            return ConfirmStartWhenExternalConnectionLikely(terminalIsRunning, projectRoot: null);
        }

        public static bool ConfirmStartWhenExternalConnectionLikely(bool terminalIsRunning, string projectRoot)
        {
            var activeClientCount = GetActiveClientCount();
            var httpClaudeCodeSessionRunning = IsHttpClaudeCodeSessionLikely(projectRoot);
            if (!IsExternalConnectionLikely(activeClientCount, httpClaudeCodeSessionRunning, terminalIsRunning))
            {
                return true;
            }

            return !EditorUtility.DisplayDialog(
                "Claude Code Terminal",
                BuildExternalConnectionWarning(activeClientCount, httpClaudeCodeSessionRunning),
                "取消启动",
                "仍然继续");
        }

        public static bool IsHttpClaudeCodeSessionLikely(string projectRoot)
        {
            return IsHttpMcpServerRunning(projectRoot) && IsExternalClaudeCodeProcessRunning();
        }

        public static bool IsHttpMcpServerRunning(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return false;
            }

            return IsHttpMcpServerRunningFromRunState(Path.Combine(projectRoot, McpRunStateRelativePath));
        }

        public static bool IsHttpMcpServerRunningFromRunState(string runStateDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(runStateDirectory) || !Directory.Exists(runStateDirectory))
                {
                    return false;
                }

                foreach (var pidFile in Directory.GetFiles(runStateDirectory, HttpPidFilePattern))
                {
                    if (TryReadProcessId(pidFile, out var processId) && IsProcessAlive(processId))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public static bool IsExternalClaudeCodeProcessRunning()
        {
            return TryIsClaudeCodeProcessRunningWithManagement(out var isRunning) && isRunning;
        }

        public static bool IsClaudeCodeCommandLine(string processName, string commandLine)
        {
            var name = (processName ?? string.Empty).ToLowerInvariant();
            var command = (commandLine ?? string.Empty).ToLowerInvariant();

            if (name == "claude.exe" || name == "claude")
            {
                return true;
            }

            return command.Contains("@anthropic-ai\\claude-code") ||
                command.Contains("@anthropic-ai/claude-code") ||
                command.Contains("node_modules\\@anthropic-ai\\claude-code") ||
                command.Contains("node_modules/@anthropic-ai/claude-code") ||
                Regex.IsMatch(command, @"(^|[\s""'\\/])claude(\.cmd|\.exe|\.ps1)?([\s""']|$)");
        }

        private static bool TryReadProcessId(string pidFile, out int processId)
        {
            processId = 0;

            try
            {
                var text = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(text, out processId))
                {
                    return processId > 0;
                }

                var pidMatch = Regex.Match(text, @"""pid""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                if (pidMatch.Success)
                {
                    return int.TryParse(pidMatch.Groups[1].Value, out processId) && processId > 0;
                }

                pidMatch = Regex.Match(text, @"\b\d+\b");
                if (!pidMatch.Success)
                {
                    return false;
                }

                return int.TryParse(pidMatch.Value, out processId) && processId > 0;
            }
            catch
            {
                processId = 0;
                return false;
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryIsClaudeCodeProcessRunningWithManagement(out bool isRunning)
        {
            isRunning = false;

            try
            {
                var managementAssembly = Assembly.Load("System.Management");
                var searcherType = managementAssembly.GetType("System.Management.ManagementObjectSearcher");
                if (searcherType == null)
                {
                    return false;
                }

                using (var searcher = Activator.CreateInstance(
                    searcherType,
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='node.exe' OR Name='claude.exe' OR Name='cmd.exe'") as IDisposable)
                {
                    if (searcher == null)
                    {
                        return false;
                    }

                    var getMethod = searcherType.GetMethod("Get", Type.EmptyTypes);
                    var collection = getMethod?.Invoke(searcher, null);
                    if (collection == null)
                    {
                        return false;
                    }

                    using (collection as IDisposable)
                    {
                        foreach (var processObject in (IEnumerable)collection)
                        {
                            using (processObject as IDisposable)
                            {
                                var processId = GetManagementInt32(processObject, "ProcessId");
                                if (processId == Process.GetCurrentProcess().Id)
                                {
                                    continue;
                                }

                                var processName = GetManagementString(processObject, "Name");
                                var commandLine = GetManagementString(processObject, "CommandLine");
                                if (IsClaudeCodeCommandLine(processName, commandLine))
                                {
                                    isRunning = true;
                                    return true;
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch
            {
                isRunning = false;
                return false;
            }
        }

        private static string GetManagementString(object managementObject, string propertyName)
        {
            return GetManagementValue(managementObject, propertyName) as string ?? string.Empty;
        }

        private static int GetManagementInt32(object managementObject, string propertyName)
        {
            var value = GetManagementValue(managementObject, propertyName);
            return value is int intValue ? intValue : 0;
        }

        private static object GetManagementValue(object managementObject, string propertyName)
        {
            return managementObject
                .GetType()
                .GetProperty("Item")
                ?.GetValue(managementObject, new object[] { propertyName });
        }
    }
}
