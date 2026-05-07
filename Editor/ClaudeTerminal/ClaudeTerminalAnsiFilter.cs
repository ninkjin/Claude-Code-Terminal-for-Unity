using System.Text;

namespace ClaudeTerminal.Editor
{
    public sealed class ClaudeTerminalAnsiFilter
    {
        private enum State
        {
            Text,
            Escape,
            Csi,
            Osc,
            OscEscape
        }

        private State state;

        public string Filter(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var output = new StringBuilder(input.Length);

            foreach (var ch in input)
            {
                switch (state)
                {
                    case State.Text:
                        HandleText(ch, output);
                        break;
                    case State.Escape:
                        HandleEscape(ch);
                        break;
                    case State.Csi:
                        HandleCsi(ch);
                        break;
                    case State.Osc:
                        HandleOsc(ch);
                        break;
                    case State.OscEscape:
                        state = ch == '\\' ? State.Text : State.Osc;
                        break;
                }
            }

            return output.ToString();
        }

        private void HandleText(char ch, StringBuilder output)
        {
            if (ch == '\u001b')
            {
                state = State.Escape;
                return;
            }

            if (ch == '\r')
            {
                return;
            }

            if (ch == '\b')
            {
                if (output.Length > 0)
                {
                    output.Length--;
                }

                return;
            }

            if (!char.IsControl(ch) || ch == '\n' || ch == '\t')
            {
                output.Append(ch);
            }
        }

        private void HandleEscape(char ch)
        {
            if (ch == '[')
            {
                state = State.Csi;
            }
            else if (ch == ']')
            {
                state = State.Osc;
            }
            else
            {
                state = State.Text;
            }
        }

        private void HandleCsi(char ch)
        {
            if (ch >= '@' && ch <= '~')
            {
                state = State.Text;
            }
        }

        private void HandleOsc(char ch)
        {
            if (ch == '\a')
            {
                state = State.Text;
            }
            else if (ch == '\u001b')
            {
                state = State.OscEscape;
            }
        }
    }
}
