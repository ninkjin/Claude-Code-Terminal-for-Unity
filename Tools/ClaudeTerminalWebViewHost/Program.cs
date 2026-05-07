using System.Windows.Forms;

namespace ClaudeTerminalWebViewHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TerminalHostForm(HostOptions.Parse(args)));
    }
}
