using System;
using UnityEngine;

namespace ClaudeTerminal.Editor
{
    [Serializable]
    public sealed class ClaudeTerminalMessage
    {
        [SerializeField] private string type;
        [SerializeField] private string data;

        public ClaudeTerminalMessage(string type, string data = "")
        {
            this.type = type ?? string.Empty;
            this.data = data ?? string.Empty;
        }

        public string Type => type ?? string.Empty;

        public string Data => data ?? string.Empty;

        public string ToJsonLine()
        {
            return JsonUtility.ToJson(this) + "\n";
        }

        public static ClaudeTerminalMessage FromJsonLine(string jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                return new ClaudeTerminalMessage(string.Empty);
            }

            var message = JsonUtility.FromJson<ClaudeTerminalMessage>(jsonLine.TrimEnd('\r', '\n'));
            if (message == null)
            {
                return new ClaudeTerminalMessage(string.Empty);
            }

            message.type ??= string.Empty;
            message.data ??= string.Empty;
            return message;
        }
    }
}
