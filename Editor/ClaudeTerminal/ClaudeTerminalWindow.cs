using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ClaudeTerminal.Editor
{
    public sealed class ClaudeTerminalWindow : EditorWindow
    {
        private const string CommandKey = "ClaudeTerminal.Command";
        private const string WorkingDirectoryKey = "ClaudeTerminal.WorkingDirectory";
        private const string BridgeProjectPathKey = "ClaudeTerminal.BridgeProjectPath";
        private const string WebViewHostProjectPathKey = "ClaudeTerminal.WebViewHostProjectPath";
        private const string PortKey = "ClaudeTerminal.Port";
        private const string WebPortKey = "ClaudeTerminal.WebPort";
        private const string ControlPortKey = "ClaudeTerminal.ControlPort";
        private const string ScrollbackLimitKey = "ClaudeTerminal.ScrollbackLimit";
        private const string SessionModeKey = "ClaudeTerminal.SessionMode";
        private const string BridgeProcessIdKey = "ClaudeTerminal.BridgeProcessId";
        private const string HostProcessIdKey = "ClaudeTerminal.HostProcessId";
        private const string PackageName = "com.mortalgame.claude-code-terminal";
        private const string SessionModeText = "text";
        private const string SessionModeWeb = "web";
        private const string SessionModeWebView = "webview";
        private const string SessionModeEmbedded = "embedded";
        private const string SessionModeNativeTerminal = "native-terminal";

        private readonly StringBuilder output = new StringBuilder();
        private readonly ClaudeTerminalAnsiFilter ansiFilter = new ClaudeTerminalAnsiFilter();
        private ClaudeTerminalClient client;
        private Process bridgeProcess;
        private Process hostProcess;
        private TcpClient embeddedControlClient;
        private StreamWriter embeddedControlWriter;
        private Vector2 outputScroll;
        private string input = string.Empty;
        private string status = "idle";
        private string command;
        private string workingDirectory;
        private string bridgeProjectPath;
        private string webViewHostProjectPath;
        private int port;
        private int webPort;
        private int controlPort;
        private int scrollbackLimit;
        private Rect embeddedTerminalRect;
        private RectInt lastSentEmbeddedBounds;
        private double lastEmbeddedBoundsSentAt;
        private bool hasSentEmbeddedBounds;
        private GUIStyle terminalStyle;
        private GUIStyle terminalBoxStyle;
        private bool embeddedMode;
        private bool nativeTerminalMode;
        private IntPtr nativeTerminalWindowHandle;
        private int activeMcpClientCount;
        private bool httpClaudeCodeSessionRunning;
        private double nextMcpClientCheckAt;

        [MenuItem("Window/Claude Code Terminal")]
        public static void Open()
        {
            GetWindow<ClaudeTerminalWindow>("Claude Terminal");
        }

        private void OnEnable()
        {
            command = EditorPrefs.GetString(ProjectScopedKey(CommandKey), "claude");
            workingDirectory = EditorPrefs.GetString(ProjectScopedKey(WorkingDirectoryKey), ProjectRoot);
            bridgeProjectPath = GetProjectPathPreference(ProjectScopedKey(BridgeProjectPathKey), DefaultBridgeProjectPath);
            webViewHostProjectPath = GetProjectPathPreference(ProjectScopedKey(WebViewHostProjectPathKey), DefaultWebViewHostProjectPath);
            port = EditorPrefs.GetInt(ProjectScopedKey(PortKey), 50557);
            webPort = EditorPrefs.GetInt(ProjectScopedKey(WebPortKey), 50558);
            controlPort = EditorPrefs.GetInt(ProjectScopedKey(ControlPortKey), 50559);
            scrollbackLimit = EditorPrefs.GetInt(ProjectScopedKey(ScrollbackLimitKey), 200000);
            TryRestoreRunningSession();
            RefreshMcpClientCount();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            SaveRunningSessionState();
        }

        private void OnDestroy()
        {
            if (!IsTransientUnityLifecycle)
            {
                StopSession();
            }
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawSettings();
            DrawExternalMcpConnectionWarning();
            DrawToolbar();

            if ((embeddedMode || nativeTerminalMode) && IsRunning)
            {
                DrawEmbeddedTerminalArea();
            }
            else
            {
                DrawOutput();
            }
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Claude Code Terminal", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(IsRunning))
            {
                command = EditorGUILayout.TextField("Command", command);
                workingDirectory = EditorGUILayout.TextField("Working Directory", workingDirectory);
                bridgeProjectPath = EditorGUILayout.TextField("Bridge Project", bridgeProjectPath);
                webViewHostProjectPath = EditorGUILayout.TextField("WebView2 Host Project", webViewHostProjectPath);
                webPort = EditorGUILayout.IntField("Web Terminal Port", webPort);
                controlPort = EditorGUILayout.IntField("Embed Control Port", controlPort);
            }

            if (GUI.changed)
            {
                SaveSettings();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"Status: {status}", GUILayout.MinWidth(160));

            using (new EditorGUI.DisabledScope(!IsRunning))
            {
                if (GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(64)))
                {
                    StopSession();
                }
            }

            if (GUILayout.Button("Open WebView2", EditorStyles.toolbarButton, GUILayout.Width(112)))
            {
                OpenWebViewTerminal();
            }

            if (GUILayout.Button("Embed WebView2", EditorStyles.toolbarButton, GUILayout.Width(116)))
            {
                EmbedWebViewTerminal();
            }

            if (GUILayout.Button("Embed Native Terminal", EditorStyles.toolbarButton, GUILayout.Width(154)))
            {
                EmbedNativeTerminal();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawExternalMcpConnectionWarning()
        {
            if (!ClaudeMcpConnectionStatus.IsExternalConnectionLikely(
                activeMcpClientCount,
                httpClaudeCodeSessionRunning,
                IsRunning))
            {
                return;
            }

            EditorGUILayout.HelpBox(
                ClaudeMcpConnectionStatus.BuildExternalConnectionWarning(activeMcpClientCount, httpClaudeCodeSessionRunning),
                MessageType.Warning);
        }

        private void DrawOutput()
        {
            outputScroll = EditorGUILayout.BeginScrollView(outputScroll, terminalBoxStyle, GUILayout.ExpandHeight(true));
            var text = output.ToString();
            var height = terminalStyle.CalcHeight(new GUIContent(text), Mathf.Max(100, position.width - 28));
            EditorGUILayout.SelectableLabel(text, terminalStyle, GUILayout.MinHeight(Mathf.Max(height, position.height - 180)));
            EditorGUILayout.EndScrollView();
        }

        private void DrawEmbeddedTerminalArea()
        {
            embeddedTerminalRect = GUILayoutUtility.GetRect(
                100,
                10000,
                120,
                10000,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            EditorGUI.DrawRect(embeddedTerminalRect, new Color(0.047f, 0.047f, 0.047f));

            if (Event.current.type == EventType.Repaint)
            {
                if (nativeTerminalMode)
                {
                    ApplyNativeTerminalBoundsIfNeeded(force: false);
                }
                else
                {
                    SendEmbeddedBoundsIfNeeded(force: false);
                }
            }
        }

        private void DrawInput()
        {
            GUI.SetNextControlName("ClaudeTerminalInput");
            input = EditorGUILayout.TextField(input);

            var current = Event.current;
            if (current.type == EventType.KeyDown &&
                current.keyCode == KeyCode.Return &&
                GUI.GetNameOfFocusedControl() == "ClaudeTerminalInput")
            {
                SendInput();
                current.Use();
            }
        }

        private async void StartSession()
        {
            if (IsRunning)
            {
                return;
            }

            RefreshMcpClientCount();
            if (!ClaudeMcpConnectionStatus.ConfirmStartWhenExternalConnectionLikely(IsRunning, ProjectRoot))
            {
                status = "idle";
                return;
            }

            SaveSettings();
            embeddedMode = false;
            hasSentEmbeddedBounds = false;
            lastSentEmbeddedBounds = default;
            lastEmbeddedBoundsSentAt = 0;
            DisposeEmbeddedControlClient();
            output.AppendLine("[terminal] starting bridge...");
            status = "starting";

            try
            {
                bridgeProcess = StartBridgeProcess();
                client = new ClaudeTerminalClient();
                client.OutputReceived += AppendOutput;
                client.StatusChanged += value => status = value;
                client.ErrorReceived += AppendError;

                await client.ConnectAsync(port);
                SaveRunningSessionState(SessionModeText);
            }
            catch (Exception ex)
            {
                AppendError(ex.Message);
                status = "error";
            }
        }

        private void StopSession()
        {
            var currentClient = client;
            var currentHostProcess = hostProcess;
            var currentBridgeProcess = bridgeProcess;

            client = null;
            hostProcess = null;
            bridgeProcess = null;
            embeddedMode = false;
            nativeTerminalMode = false;
            nativeTerminalWindowHandle = IntPtr.Zero;
            hasSentEmbeddedBounds = false;
            ClearRunningSessionState();
            DisposeEmbeddedControlClient();

            if (currentClient != null)
            {
                _ = StopClientWithoutBlockingUnityAsync(currentClient);
            }

            if (currentHostProcess != null)
            {
                try
                {
                    if (!currentHostProcess.HasExited)
                    {
                        currentHostProcess.Kill();
                    }
                }
                catch
                {
                    // Process may already be gone.
                }

                currentHostProcess.Dispose();
            }

            if (currentBridgeProcess != null)
            {
                try
                {
                    if (!currentBridgeProcess.HasExited)
                    {
                        currentBridgeProcess.Kill();
                    }
                }
                catch
                {
                    // Process may already be gone.
                }

                currentBridgeProcess.Dispose();
            }

            status = "idle";
        }

        private static async Task StopClientWithoutBlockingUnityAsync(ClaudeTerminalClient currentClient)
        {
            try
            {
                await currentClient.StopRemoteProcessAsync();
            }
            catch
            {
                // The bridge may already be gone.
            }
            finally
            {
                currentClient.Dispose();
            }
        }

        private void OpenWebTerminal()
        {
            SaveSettings();
            RefreshMcpClientCount();
            if (!ClaudeMcpConnectionStatus.ConfirmStartWhenExternalConnectionLikely(IsRunning, ProjectRoot))
            {
                return;
            }

            StopSession();

            try
            {
                bridgeProcess = StartWebBridgeProcess();
                status = "web";
                embeddedMode = false;
                SaveRunningSessionState(SessionModeWeb);
                Application.OpenURL($"http://127.0.0.1:{webPort}/");
            }
            catch (Exception ex)
            {
                AppendError(ex.Message);
                status = "error";
            }
        }

        private void OpenWebViewTerminal()
        {
            SaveSettings();
            RefreshMcpClientCount();
            if (!ClaudeMcpConnectionStatus.ConfirmStartWhenExternalConnectionLikely(IsRunning, ProjectRoot))
            {
                return;
            }

            StopSession();

            try
            {
                bridgeProcess = StartWebBridgeProcess();
                hostProcess = StartWebViewHostProcess();
                status = "webview";
                embeddedMode = false;
                SaveRunningSessionState(SessionModeWebView);
            }
            catch (Exception ex)
            {
                AppendError(ex.Message);
                status = "error";
            }
        }

        private void EmbedWebViewTerminal()
        {
            SaveSettings();
            RefreshMcpClientCount();
            if (!ClaudeMcpConnectionStatus.ConfirmStartWhenExternalConnectionLikely(IsRunning, ProjectRoot))
            {
                return;
            }

            StopSession();
            embeddedMode = true;
            hasSentEmbeddedBounds = false;
            lastSentEmbeddedBounds = default;
            lastEmbeddedBoundsSentAt = 0;

            try
            {
                bridgeProcess = StartWebBridgeProcess();
                hostProcess = StartEmbeddedWebViewHostProcess();
                status = "embedded";
                SaveRunningSessionState(SessionModeEmbedded);
                Repaint();
            }
            catch (Exception ex)
            {
                embeddedMode = false;
                AppendError(ex.Message);
                status = "error";
            }
        }

        private void EmbedNativeTerminal()
        {
            SaveSettings();
            RefreshMcpClientCount();
            if (!ClaudeMcpConnectionStatus.ConfirmStartWhenExternalConnectionLikely(IsRunning, ProjectRoot))
            {
                return;
            }

            StopSession();
            nativeTerminalMode = true;
            embeddedMode = false;
            hasSentEmbeddedBounds = false;
            lastSentEmbeddedBounds = default;
            lastEmbeddedBoundsSentAt = 0;
            nativeTerminalWindowHandle = IntPtr.Zero;

            try
            {
                var nativeTerminalTitle = "ClaudeNativeTerminal-" + Guid.NewGuid().ToString("N");
                var existingWindows = NativeTerminalWindowController.GetVisibleTopLevelWindows();
                hostProcess = StartNativeTerminalProcess(nativeTerminalTitle);
                nativeTerminalWindowHandle = WaitForMainWindowHandle(
                    hostProcess,
                    nativeTerminalTitle,
                    existingWindows,
                    TimeSpan.FromSeconds(5));
                if (nativeTerminalWindowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Native terminal window handle was not found.");
                }

                NativeTerminalWindowController.FollowPanel(
                    nativeTerminalWindowHandle,
                    GetEmbeddedScreenBounds());

                status = "native-terminal";
                SaveRunningSessionState(SessionModeNativeTerminal);
                Repaint();
            }
            catch (Exception ex)
            {
                nativeTerminalMode = false;
                nativeTerminalWindowHandle = IntPtr.Zero;
                AppendError(ex.Message);
                status = "error";
            }
        }

        private async void SendInput()
        {
            if (client == null || string.IsNullOrEmpty(input))
            {
                return;
            }

            var text = input;
            input = string.Empty;
            await client.SendInputAsync(text + "\n");
        }

        private Process StartBridgeProcess()
        {
            return StartBridgeProcessWithArguments($"--port {port} --command \"{command}\" --working-directory \"{workingDirectory}\"");
        }

        private Process StartWebBridgeProcess()
        {
            return StartBridgeProcessWithArguments($"--web-port {webPort} --command \"{command}\" --working-directory \"{workingDirectory}\"");
        }

        private Process StartWebViewHostProcess()
        {
            var hostExecutablePath = ResolveWebViewHostExecutablePath();

            var url = $"http://127.0.0.1:{webPort}/";
            var startInfo = new ProcessStartInfo
            {
                FileName = hostExecutablePath,
                Arguments = $"--url \"{url}\" --title \"Claude Code Terminal\" --width 1100 --height 760 " +
                    $"--user-data-folder \"{WebView2UserDataPath}\"",
                WorkingDirectory = ProjectRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("WebView2 host failed to start.");
            }

            return process;
        }

        private Process StartEmbeddedWebViewHostProcess()
        {
            var parentHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (parentHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unity main window handle was not found.");
            }

            var hostExecutablePath = ResolveWebViewHostExecutablePath();

            var bounds = GetEmbeddedScreenBounds();
            var url = $"http://127.0.0.1:{webPort}/";
            var arguments =
                $"--embedded --parent-hwnd {parentHandle.ToInt64()} --control-port {controlPort} " +
                $"--url \"{url}\" --title \"Claude Code Terminal\" " +
                $"--left {bounds.x} --top {bounds.y} --width {bounds.width} --height {bounds.height} " +
                $"--user-data-folder \"{WebView2UserDataPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = hostExecutablePath,
                Arguments = arguments,
                WorkingDirectory = ProjectRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Embedded WebView2 host failed to start.");
            }

            return process;
        }

        private Process StartBridgeProcessWithArguments(string arguments)
        {
            var bridgeExecutablePath = ResolveBridgeExecutablePath();

            var startInfo = new ProcessStartInfo
            {
                FileName = bridgeExecutablePath,
                Arguments = arguments,
                WorkingDirectory = ProjectRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Bridge failed to start.");
            }

            return process;
        }

        private Process StartNativeTerminalProcess(string nativeTerminalTitle)
        {
            var cmdPath = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(cmdPath))
            {
                cmdPath = "cmd.exe";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = $"/k \"title {nativeTerminalTitle} & chcp 65001 > nul & {command}\"",
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : ProjectRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                CreateNoWindow = false
            };

            var process = StartWithTemporaryConsoleHostDelegation(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Native terminal failed to start.");
            }

            return process;
        }

        private static Process StartWithTemporaryConsoleHostDelegation(ProcessStartInfo startInfo)
        {
            var previousDelegationConsole = TerminalDelegationRegistry.Read("DelegationConsole");
            var previousDelegationTerminal = TerminalDelegationRegistry.Read("DelegationTerminal");

            try
            {
                TerminalDelegationRegistry.SetConsoleHost();
                return Process.Start(startInfo);
            }
            finally
            {
                TerminalDelegationRegistry.Restore("DelegationConsole", previousDelegationConsole);
                TerminalDelegationRegistry.Restore("DelegationTerminal", previousDelegationTerminal);
            }
        }

        private static IntPtr WaitForMainWindowHandle(
            Process process,
            string windowTitle,
            System.Collections.Generic.HashSet<IntPtr> existingWindows,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        return IntPtr.Zero;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }

                    var titledWindowHandle = NativeTerminalWindowController.FindTopLevelWindowByTitle(windowTitle);
                    if (titledWindowHandle != IntPtr.Zero)
                    {
                        return titledWindowHandle;
                    }

                    var newVisibleWindowHandle = NativeTerminalWindowController.FindNewVisibleTopLevelWindow(existingWindows);
                    if (newVisibleWindowHandle != IntPtr.Zero)
                    {
                        return newVisibleWindowHandle;
                    }

                    process.WaitForInputIdle(100);
                }
                catch
                {
                    // Console processes may not always report input-idle state.
                }

                System.Threading.Thread.Sleep(50);
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            var titledWindow = NativeTerminalWindowController.FindTopLevelWindowByTitle(windowTitle);
            return titledWindow != IntPtr.Zero
                ? titledWindow
                : NativeTerminalWindowController.FindNewVisibleTopLevelWindow(existingWindows);
        }

        private string ResolveBridgeExecutablePath()
        {
            return ResolveToolExecutablePath(
                DefaultBridgeExecutablePath,
                bridgeProjectPath,
                GetBridgeBuildExecutablePath(),
                "Bridge");
        }

        private string ResolveWebViewHostExecutablePath()
        {
            return ResolveToolExecutablePath(
                DefaultWebViewHostExecutablePath,
                webViewHostProjectPath,
                GetWebViewHostBuildExecutablePath(),
                "WebView2 host");
        }

        private string ResolveToolExecutablePath(string prebuiltExecutablePath, string projectPath, string buildExecutablePath, string label)
        {
            if (File.Exists(prebuiltExecutablePath))
            {
                return prebuiltExecutablePath;
            }

            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException(
                    label + " executable was not found. Reinstall the package, or install .NET 8 SDK and build the tool from source.",
                    prebuiltExecutablePath);
            }

            EnsureDotnetExecutable(projectPath, buildExecutablePath, label);
            return buildExecutablePath;
        }

        private string GetBridgeBuildExecutablePath()
        {
            var bridgeDirectory = Path.GetDirectoryName(bridgeProjectPath);
            if (string.IsNullOrEmpty(bridgeDirectory))
            {
                return string.Empty;
            }

            return Path.Combine(bridgeDirectory, "bin", "Debug", "net8.0", "ClaudeTerminalBridge.exe");
        }

        private string GetWebViewHostBuildExecutablePath()
        {
            var hostDirectory = Path.GetDirectoryName(webViewHostProjectPath);
            if (string.IsNullOrEmpty(hostDirectory))
            {
                return string.Empty;
            }

            return Path.Combine(hostDirectory, "bin", "Debug", "net8.0-windows10.0.17763.0", "ClaudeTerminalWebViewHost.exe");
        }

        private void EnsureDotnetExecutable(string projectPath, string executablePath, string label)
        {
            if (!NeedsDotnetBuild(projectPath, executablePath))
            {
                return;
            }

            var buildInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\"",
                WorkingDirectory = ProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var buildProcess = Process.Start(buildInfo);
            if (buildProcess == null)
            {
                throw new InvalidOperationException(label + " build failed to start.");
            }

            var outputText = buildProcess.StandardOutput.ReadToEnd();
            var errorText = buildProcess.StandardError.ReadToEnd();
            buildProcess.WaitForExit();

            if (buildProcess.ExitCode != 0 || !File.Exists(executablePath))
            {
                throw new InvalidOperationException(label + " build failed.\n" + outputText + "\n" + errorText);
            }
        }

        private static bool NeedsDotnetBuild(string projectPath, string executablePath)
        {
            if (!File.Exists(executablePath))
            {
                return true;
            }

            var executableLastWrite = File.GetLastWriteTimeUtc(executablePath);
            if (File.GetLastWriteTimeUtc(projectPath) > executableLastWrite)
            {
                return true;
            }

            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                return false;
            }

            foreach (var sourcePath in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(projectDirectory, sourcePath);
                if (relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(sourcePath) > executableLastWrite)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnEditorUpdate()
        {
            client?.Pump();
            if (EditorApplication.timeSinceStartup >= nextMcpClientCheckAt)
            {
                RefreshMcpClientCount();
                nextMcpClientCheckAt = EditorApplication.timeSinceStartup + 1.0f;
            }

            Repaint();
        }

        private void RefreshMcpClientCount()
        {
            activeMcpClientCount = ClaudeMcpConnectionStatus.GetActiveClientCount();
            httpClaudeCodeSessionRunning = ClaudeMcpConnectionStatus.IsHttpClaudeCodeSessionLikely(ProjectRoot);
        }

        private RectInt GetEmbeddedScreenBounds()
        {
            var rect = embeddedTerminalRect.width > 1 && embeddedTerminalRect.height > 1
                ? embeddedTerminalRect
                : new Rect(0, 130, Mathf.Max(640, position.width), Mathf.Max(420, position.height - 130));

            var screenMin = GUIUtility.GUIToScreenPoint(new Vector2(rect.xMin, rect.yMin));
            var width = Mathf.Max(160, Mathf.RoundToInt(rect.width));
            var height = Mathf.Max(120, Mathf.RoundToInt(rect.height));
            return new RectInt(
                Mathf.RoundToInt(screenMin.x),
                Mathf.RoundToInt(screenMin.y),
                width,
                height);
        }

        private void SendEmbeddedBoundsIfNeeded(bool force)
        {
            if (!embeddedMode || hostProcess == null || hostProcess.HasExited)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            var bounds = GetEmbeddedScreenBounds();
            if (!force && hasSentEmbeddedBounds && bounds.Equals(lastSentEmbeddedBounds))
            {
                return;
            }

            if (!force && hasSentEmbeddedBounds && now - lastEmbeddedBoundsSentAt < 0.05)
            {
                return;
            }

            try
            {
                EnsureEmbeddedControlClient();
                embeddedControlWriter.WriteLine($"bounds {bounds.x} {bounds.y} {bounds.width} {bounds.height}");
                lastSentEmbeddedBounds = bounds;
                hasSentEmbeddedBounds = true;
                lastEmbeddedBoundsSentAt = now;
            }
            catch
            {
                DisposeEmbeddedControlClient();
            }
        }

        private void ApplyNativeTerminalBoundsIfNeeded(bool force)
        {
            if (!nativeTerminalMode || hostProcess == null || hostProcess.HasExited)
            {
                return;
            }

            if (nativeTerminalWindowHandle == IntPtr.Zero)
            {
                nativeTerminalWindowHandle = hostProcess.MainWindowHandle;
                if (nativeTerminalWindowHandle == IntPtr.Zero)
                {
                    return;
                }
            }

            var now = EditorApplication.timeSinceStartup;
            var bounds = GetEmbeddedScreenBounds();
            if (!force && hasSentEmbeddedBounds && bounds.Equals(lastSentEmbeddedBounds))
            {
                return;
            }

            if (!force && hasSentEmbeddedBounds && now - lastEmbeddedBoundsSentAt < 0.05)
            {
                return;
            }

            NativeTerminalWindowController.FollowPanel(nativeTerminalWindowHandle, bounds);
            lastSentEmbeddedBounds = bounds;
            hasSentEmbeddedBounds = true;
            lastEmbeddedBoundsSentAt = now;
        }

        private void EnsureEmbeddedControlClient()
        {
            if (embeddedControlClient != null && embeddedControlClient.Connected && embeddedControlWriter != null)
            {
                return;
            }

            DisposeEmbeddedControlClient();
            embeddedControlClient = new TcpClient();
            embeddedControlClient.Connect("127.0.0.1", controlPort);
            embeddedControlWriter = new StreamWriter(embeddedControlClient.GetStream(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        private void DisposeEmbeddedControlClient()
        {
            embeddedControlWriter?.Dispose();
            embeddedControlClient?.Dispose();
            embeddedControlWriter = null;
            embeddedControlClient = null;
        }

        private void AppendOutput(string text)
        {
            var filtered = ansiFilter.Filter(text);
            if (string.IsNullOrEmpty(filtered))
            {
                return;
            }

            output.Append(filtered);
            TrimOutput();
            outputScroll.y = float.MaxValue;
        }

        private void AppendError(string text)
        {
            output.AppendLine("[error] " + text);
            TrimOutput();
            outputScroll.y = float.MaxValue;
        }

        private void TrimOutput()
        {
            if (output.Length <= scrollbackLimit)
            {
                return;
            }

            output.Remove(0, output.Length - scrollbackLimit);
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString(ProjectScopedKey(CommandKey), command);
            EditorPrefs.SetString(ProjectScopedKey(WorkingDirectoryKey), workingDirectory);
            EditorPrefs.SetString(ProjectScopedKey(BridgeProjectPathKey), bridgeProjectPath);
            EditorPrefs.SetString(ProjectScopedKey(WebViewHostProjectPathKey), webViewHostProjectPath);
            EditorPrefs.SetInt(ProjectScopedKey(PortKey), port);
            EditorPrefs.SetInt(ProjectScopedKey(WebPortKey), webPort);
            EditorPrefs.SetInt(ProjectScopedKey(ControlPortKey), controlPort);
            EditorPrefs.SetInt(ProjectScopedKey(ScrollbackLimitKey), scrollbackLimit);
        }

        private void SaveRunningSessionState(string mode = null)
        {
            if (!IsRunning)
            {
                return;
            }

            if (!string.IsNullOrEmpty(mode))
            {
                EditorPrefs.SetString(ProjectScopedKey(SessionModeKey), mode);
            }

            SaveProcessId(ProjectScopedKey(BridgeProcessIdKey), bridgeProcess);
            SaveProcessId(ProjectScopedKey(HostProcessIdKey), hostProcess);
        }

        private static void SaveProcessId(string key, Process process)
        {
            if (process == null)
            {
                EditorPrefs.DeleteKey(key);
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    EditorPrefs.SetInt(key, process.Id);
                    return;
                }
            }
            catch
            {
                // Process may have exited between checks.
            }

            EditorPrefs.DeleteKey(key);
        }

        private void TryRestoreRunningSession()
        {
            var mode = EditorPrefs.GetString(ProjectScopedKey(SessionModeKey), string.Empty);
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }

            bridgeProcess = TryGetRunningProcess(EditorPrefs.GetInt(ProjectScopedKey(BridgeProcessIdKey), -1));
            hostProcess = TryGetRunningProcess(EditorPrefs.GetInt(ProjectScopedKey(HostProcessIdKey), -1));
            if (!IsRunning)
            {
                ClearRunningSessionState();
                return;
            }

            embeddedMode = mode == SessionModeEmbedded;
            nativeTerminalMode = mode == SessionModeNativeTerminal;
            status = mode;
            if (embeddedMode || nativeTerminalMode)
            {
                hasSentEmbeddedBounds = false;
                lastSentEmbeddedBounds = default;
                lastEmbeddedBoundsSentAt = 0;
                nativeTerminalWindowHandle = nativeTerminalMode && hostProcess != null
                    ? hostProcess.MainWindowHandle
                    : IntPtr.Zero;
            }
        }

        private static Process TryGetRunningProcess(int processId)
        {
            if (processId <= 0)
            {
                return null;
            }

            try
            {
                var process = Process.GetProcessById(processId);
                return process.HasExited ? null : process;
            }
            catch
            {
                return null;
            }
        }

        private static void ClearRunningSessionState()
        {
            EditorPrefs.DeleteKey(ProjectScopedKey(SessionModeKey));
            EditorPrefs.DeleteKey(ProjectScopedKey(BridgeProcessIdKey));
            EditorPrefs.DeleteKey(ProjectScopedKey(HostProcessIdKey));
        }

        private void EnsureStyles()
        {
            terminalStyle ??= new GUIStyle(EditorStyles.label)
            {
                font = EditorStyles.miniFont,
                wordWrap = true,
                richText = false,
                normal = { textColor = new Color(0.82f, 0.94f, 0.84f) }
            };

            terminalBoxStyle ??= new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                normal = { background = Texture2D.grayTexture }
            };
        }

        private bool IsRunning =>
            bridgeProcess != null && !bridgeProcess.HasExited ||
            hostProcess != null && !hostProcess.HasExited;

        private static bool IsTransientUnityLifecycle =>
            EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling;

        private static class NativeTerminalWindowController
        {
            private const uint SwpShowWindow = 0x0040;
            private const int SwShow = 5;
            private const uint RdwInvalidate = 0x0001;
            private const uint RdwInternalPaint = 0x0002;
            private const uint RdwErase = 0x0004;
            private const uint RdwAllChildren = 0x0080;
            private const uint RdwUpdateNow = 0x0100;
            private const uint RdwFrame = 0x0400;
            private static readonly IntPtr HwndTop = IntPtr.Zero;
            private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

            public static void FollowPanel(IntPtr terminalWindowHandle, RectInt screenBounds)
            {
                if (terminalWindowHandle == IntPtr.Zero)
                {
                    return;
                }

                SetWindowPos(
                    terminalWindowHandle,
                    HwndTop,
                    screenBounds.x,
                    screenBounds.y,
                    screenBounds.width,
                    screenBounds.height,
                    SwpShowWindow);

                ShowWindow(terminalWindowHandle, SwShow);
                InvalidateRect(terminalWindowHandle, IntPtr.Zero, true);
                RedrawWindow(
                    terminalWindowHandle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    RdwInvalidate | RdwInternalPaint | RdwErase | RdwAllChildren | RdwUpdateNow | RdwFrame);
                UpdateWindow(terminalWindowHandle);
            }

            public static IntPtr FindTopLevelWindowByTitle(string title)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return IntPtr.Zero;
                }

                var foundWindowHandle = IntPtr.Zero;
                EnumWindows((windowHandle, _) =>
                {
                    if (!IsWindowVisible(windowHandle))
                    {
                        return true;
                    }

                    var length = GetWindowTextLength(windowHandle);
                    if (length <= 0)
                    {
                        return true;
                    }

                    var builder = new StringBuilder(length + 1);
                    GetWindowText(windowHandle, builder, builder.Capacity);
                    if (builder.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return true;
                    }

                    foundWindowHandle = windowHandle;
                    return false;
                }, IntPtr.Zero);

                return foundWindowHandle;
            }

            public static System.Collections.Generic.HashSet<IntPtr> GetVisibleTopLevelWindows()
            {
                var windows = new System.Collections.Generic.HashSet<IntPtr>();
                EnumWindows((windowHandle, _) =>
                {
                    if (IsWindowVisible(windowHandle))
                    {
                        windows.Add(windowHandle);
                    }

                    return true;
                }, IntPtr.Zero);
                return windows;
            }

            public static IntPtr FindNewVisibleTopLevelWindow(System.Collections.Generic.HashSet<IntPtr> existingWindows)
            {
                var foundWindowHandle = IntPtr.Zero;
                EnumWindows((windowHandle, _) =>
                {
                    if (!IsWindowVisible(windowHandle) || existingWindows.Contains(windowHandle))
                    {
                        return true;
                    }

                    foundWindowHandle = windowHandle;
                    return false;
                }, IntPtr.Zero);

                return foundWindowHandle;
            }

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(
                IntPtr windowHandle,
                IntPtr insertAfterWindowHandle,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

            [DllImport("user32.dll")]
            private static extern bool InvalidateRect(IntPtr windowHandle, IntPtr rect, bool erase);

            [DllImport("user32.dll")]
            private static extern bool RedrawWindow(IntPtr windowHandle, IntPtr updateRect, IntPtr updateRegion, uint flags);

            [DllImport("user32.dll")]
            private static extern bool UpdateWindow(IntPtr windowHandle);

            [DllImport("user32.dll")]
            private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern bool IsWindowVisible(IntPtr windowHandle);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowTextLength(IntPtr windowHandle);
        }

        private static class TerminalDelegationRegistry
        {
            private const string StartupKeyPath = @"Console\%%Startup";
            private const string ConsoleHostGuid = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";

            public static RegistryValueSnapshot Read(string valueName)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
                if (key == null)
                {
                    return RegistryValueSnapshot.Missing;
                }

                var value = key.GetValue(valueName);
                if (value == null)
                {
                    return RegistryValueSnapshot.Missing;
                }

                return new RegistryValueSnapshot(value, key.GetValueKind(valueName));
            }

            public static void SetConsoleHost()
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                key.SetValue("DelegationConsole", ConsoleHostGuid, Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("DelegationTerminal", ConsoleHostGuid, Microsoft.Win32.RegistryValueKind.String);
            }

            public static void Restore(string valueName, RegistryValueSnapshot snapshot)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupKeyPath);
                if (!snapshot.Exists)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    return;
                }

                key.SetValue(valueName, snapshot.Value, snapshot.ValueKind);
            }
        }

        private readonly struct RegistryValueSnapshot
        {
            public static readonly RegistryValueSnapshot Missing = new RegistryValueSnapshot(false, null, Microsoft.Win32.RegistryValueKind.Unknown);

            public RegistryValueSnapshot(object value, Microsoft.Win32.RegistryValueKind valueKind)
                : this(true, value, valueKind)
            {
            }

            private RegistryValueSnapshot(bool exists, object value, Microsoft.Win32.RegistryValueKind valueKind)
            {
                Exists = exists;
                Value = value;
                ValueKind = valueKind;
            }

            public bool Exists { get; }
            public object Value { get; }
            public Microsoft.Win32.RegistryValueKind ValueKind { get; }
        }

        private static string DefaultBridgeProjectPath =>
            Path.Combine(PackageRoot, "Tools~", "ClaudeTerminalBridge", "ClaudeTerminalBridge.csproj");

        private static string DefaultWebViewHostProjectPath =>
            Path.Combine(PackageRoot, "Tools~", "ClaudeTerminalWebViewHost", "ClaudeTerminalWebViewHost.csproj");

        private static string DefaultBridgeExecutablePath =>
            Path.Combine(PackageRoot, "Tools", "Prebuilt", "win-x64", "ClaudeTerminalBridge", "ClaudeTerminalBridge.exe");

        private static string DefaultWebViewHostExecutablePath =>
            Path.Combine(PackageRoot, "Tools", "Prebuilt", "win-x64", "ClaudeTerminalWebViewHost", "ClaudeTerminalWebViewHost.exe");

        private static string WebView2UserDataPath =>
            Path.Combine(ProjectRoot, "Library", "ClaudeCodeTerminal", "WebView2UserData");

        private static string PackageRoot
        {
            get
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ClaudeTerminalWindow).Assembly);
                if (packageInfo != null && Directory.Exists(packageInfo.resolvedPath))
                {
                    return packageInfo.resolvedPath;
                }

                var embeddedPackagePath = Path.Combine(ProjectRoot, "Packages", PackageName);
                if (Directory.Exists(embeddedPackagePath))
                {
                    return embeddedPackagePath;
                }

                var packageCachePath = Path.Combine(ProjectRoot, "Library", "PackageCache");
                if (Directory.Exists(packageCachePath))
                {
                    var newestPath = string.Empty;
                    var newestWriteTime = DateTime.MinValue;
                    foreach (var packagePath in Directory.GetDirectories(packageCachePath, PackageName + "*"))
                    {
                        var writeTime = Directory.GetLastWriteTimeUtc(packagePath);
                        if (writeTime >= newestWriteTime)
                        {
                            newestPath = packagePath;
                            newestWriteTime = writeTime;
                        }
                    }

                    if (!string.IsNullOrEmpty(newestPath))
                    {
                        return newestPath;
                    }
                }

                return embeddedPackagePath;
            }
        }

        private static string GetProjectPathPreference(string key, string defaultPath)
        {
            var savedPath = EditorPrefs.GetString(key, defaultPath);
            return File.Exists(savedPath) ? savedPath : defaultPath;
        }

        private static string ProjectScopedKey(string key)
        {
            return $"{key}.{ProjectRootHash}";
        }

        private static string ProjectRootHash
        {
            get
            {
                unchecked
                {
                    const ulong offsetBasis = 14695981039346656037UL;
                    const ulong prime = 1099511628211UL;
                    var normalizedPath = ProjectRoot.Replace('\\', '/').ToUpperInvariant();
                    var hash = offsetBasis;

                    foreach (var character in normalizedPath)
                    {
                        hash ^= character;
                        hash *= prime;
                    }

                    return hash.ToString("x16");
                }
            }
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
    }
}
