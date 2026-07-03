using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MyQuoteCard
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                return SelfTests.Run();
            }

            string dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyQuoteCard");
            Directory.CreateDirectory(dataDirectory);
            AppState state = AppState.Load(Path.Combine(dataDirectory, "state.ini"));

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\MyQuoteCard.SingleInstance.1A7C4B92", out createdNew))
            {
                if (!createdNew)
                {
                    if (TryLocateExistingCard()) return 0;
                    MessageBox.Show(UiText.Get(state,
                        "自言卡已经在运行了。\n\n请双击右下角通知区域中的小图标，或按 Ctrl+Alt+Q 寻找卡片。",
                        "MyQuoteCard is already running.\n\nDouble-click its tray icon or press Ctrl+Alt+Q to find the card."),
                        UiText.Get(state, "自言卡", "MyQuoteCard"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool suppressStartupRegistration = HasArgument(args, "--preview");
                QuoteRepository repository = new QuoteRepository(Path.Combine(dataDirectory, "quotes.txt"), state);

                if (!state.StartupInitialized && !suppressStartupRegistration)
                {
                    state.StartupInitialized = true;
                    try
                    {
                        DialogResult answer = MessageBox.Show(
                            UiText.Get(state,
                                "要让自言卡随 Windows 自动启动吗？\n\n以后可以随时在卡片的右键菜单中修改。",
                                "Start MyQuoteCard automatically with Windows?\n\nYou can change this later from the card's right-click menu."),
                            UiText.Get(state, "欢迎使用自言卡", "Welcome to MyQuoteCard"),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1);
                        StartupManager.SetEnabled(answer == DialogResult.Yes);
                    }
                    catch
                    {
                        state.StartupEnableFailed = true;
                    }
                    state.Save();
                }

                using (QuoteCardForm form = new QuoteCardForm(state, repository))
                {
                    Application.Run(form);
                }
                return 0;
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool TryLocateExistingCard()
        {
            string[] titles = new string[] { "自言卡", "MyQuoteCard" };
            foreach (string title in titles)
            {
                IntPtr window = NativeMethods.FindWindow(null, title);
                if (window == IntPtr.Zero) continue;
                IntPtr response;
                IntPtr sent = NativeMethods.SendMessageTimeout(window, (uint)QuoteCardForm.LocateMessage,
                    IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out response);
                if (sent != IntPtr.Zero && response.ToInt32() == QuoteCardForm.LocateResponse) return true;
            }
            return false;
        }
    }
}
