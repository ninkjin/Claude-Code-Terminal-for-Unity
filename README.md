# Claude Code Terminal for Unity

Languages: English | [简体中文](README.zh-CN.md)

An `EditorWindow` package that brings a Claude Code terminal into the Unity Editor.

The package adds `Window / Claude Code Terminal` and provides:

- `Embed WebView2`: embed a near-native Claude Code terminal into a Unity editor panel area.
- `Open WebView2`: open the same terminal experience in a standalone WebView2 window.
- `Stop`: stop the Claude Code terminal session started by this tool.
- External Claude Code / Unity MCP session detection, so you do not accidentally run multiple MCP-connected sessions.
- Chinese IME support, paste support, multi-line paste support, and common terminal shortcuts.
- Session preservation when Unity enters Play Mode, so starting the game does not immediately kill the terminal process.

## Screenshot

![Claude Code Terminal embedded in Unity](Documentation~/images/claude-code-terminal-unity.png)

## Installation

In Unity, open:

```text
Window / Package Manager / + / Add package from git URL...
```

Paste this repository URL:

```text
https://github.com/ninkjin/Claude-Code-Terminal-for-Unity.git
```

To install the `main` branch explicitly:

```text
https://github.com/ninkjin/Claude-Code-Terminal-for-Unity.git#main
```

## Requirements

- Windows + Unity 2022.3 or newer.
- Claude Code CLI installed and available from the command line as `claude`.
- Microsoft Edge WebView2 Runtime. Most Windows 10/11 machines already have it; install it manually if the WebView2 window cannot open.
- If you want Claude Code to control Unity, start and connect your Unity MCP integration as usual.

The package includes prebuilt Windows x64 executables for:

- `Tools/ClaudeTerminalBridge`
- `Tools/ClaudeTerminalWebViewHost`

Normal users do not need the .NET SDK. The source projects are included under `Tools~` for development; install the .NET 8 SDK only if you want to modify and rebuild the bridge or WebView2 host yourself.

## Usage

After installation, open:

```text
Window / Claude Code Terminal
```

Typical workflow:

1. Confirm `Command` is `claude`.
2. Confirm `Working Directory` points to the current Unity project root.
3. Click `Embed WebView2` to use Claude Code inside the Unity editor panel.
4. Click `Open WebView2` if you want a standalone window.
5. Click `Stop` when you want to close the current session.

## Settings

- `Command`: command used to start Claude Code. Default: `claude`.
- `Working Directory`: working directory for Claude Code. Default: the current Unity project root.
- `Bridge Project`: `.csproj` path for the terminal bridge. By default, it points to `Tools/ClaudeTerminalBridge` inside the package.
- `WebView2 Host Project`: `.csproj` path for the WebView2 host. By default, it points to `Tools/ClaudeTerminalWebViewHost` inside the package.
- `Web Terminal Port`: port for the xterm.js web terminal server. Default: `50558`.
- `Embed Control Port`: control port used between Unity and the WebView2 host in embedded mode. Default: `50559`.

## Known Limitations

- `Embed WebView2` overlays an external WebView2 window onto the Unity editor panel area. It is not a native Unity IMGUI control.
- The package is currently focused on Windows. macOS and Linux are not supported yet.
- If an external cmd window already has a Claude Code session connected to Unity MCP, the tool will warn you before starting another session.
