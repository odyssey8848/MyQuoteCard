using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MyQuoteCard
{
    internal static class Program
    {
        private const string DataFolderName = "MyQuoteCard";
        private const string MutexBaseName = "Local\\MyQuoteCard.SingleInstance.1A7C4B92";
        private const int MaxInstances = 9;

        /// <summary>
        /// 当前进程是第几张卡片（从 1 开始）。多开时用它区分独立的数据目录和标题。
        /// </summary>
        internal static int InstanceIndex = 1;

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
            {
                return SelfTests.Run();
            }

            string baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DataFolderName);
            Directory.CreateDirectory(baseDirectory);

            // 启动器可以指定要开哪一张：MyQuoteCard.exe --instance=3
            int requested = ParseInstanceArgument(args);

            Mutex instanceMutex;
            if (requested > 0)
            {
                InstanceIndex = requested;
                bool createdNew;
                instanceMutex = new Mutex(true, MutexName(requested), out createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("第 " + requested + " 张卡片已经在运行了。",
                        "自言卡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
            }
            else if (!TryAcquireInstance(out instanceMutex))
            {
                MessageBox.Show(
                    "已经打开了 " + MaxInstances + " 张卡片。\r\n请先关掉一张，再打开新的。",
                    "自言卡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            using (instanceMutex)
            {
                string dataDirectory = InstanceIndex == 1
                    ? baseDirectory
                    : Path.Combine(baseDirectory, "instance" + InstanceIndex);
                Directory.CreateDirectory(dataDirectory);
                if (InstanceIndex > 1) SeedFromMainInstance(baseDirectory, dataDirectory);

                AppState state = AppState.Load(Path.Combine(dataDirectory, "state.ini"));

                // 多开的新卡片直接沿用已有设置，不再重复询问开机启动
                if (InstanceIndex > 1 && !state.StartupInitialized)
                {
                    state.StartupInitialized = true;
                    state.Save();
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                QuoteRepository repository = new QuoteRepository(Path.Combine(dataDirectory, "quotes.txt"), state);

                // 不再弹"要不要随 Windows 自动启动"：首次直接按不自动启动处理，右键菜单里的开关随时可改
                if (!state.StartupInitialized)
                {
                    state.StartupInitialized = true;
                    state.Save();
                }

                using (QuoteCardForm form = new QuoteCardForm(state, repository))
                {
                    Application.Run(form);
                }
                return 0;
            }
        }

        /// <summary>
        /// 依次尝试第 1 到第 MaxInstances 个实例锁。抢到哪个就是第几张卡片。
        /// </summary>
        private static bool TryAcquireInstance(out Mutex acquired)
        {
            acquired = null;
            for (int index = 1; index <= MaxInstances; index++)
            {
                string name = MutexName(index);
                bool createdNew;
                Mutex candidate = new Mutex(true, name, out createdNew);
                if (createdNew)
                {
                    InstanceIndex = index;
                    acquired = candidate;
                    return true;
                }
                candidate.Dispose();
            }
            return false;
        }

        internal static string MutexName(int index)
        {
            return index <= 1 ? MutexBaseName : MutexBaseName + "." + index;
        }

        /// <summary>
        /// 解析 --instance=N 参数。没有或解析失败返回 0（表示按老逻辑自动顺号）。
        /// </summary>
        private static int ParseInstanceArgument(string[] args)
        {
            if (args == null) return 0;
            foreach (string arg in args)
            {
                if (arg == null) continue;
                string trimmed = arg.Trim();
                const string prefix = "--instance=";
                if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (!int.TryParse(trimmed.Substring(prefix.Length), out value)) return 0;
                if (value < 1) return 1;
                return Math.Min(MaxInstances, value);
            }
            return 0;
        }

        /// <summary>
        /// 新开一张卡片时，先复制主卡片的内容库作为起点（设置保持各自独立）。
        /// </summary>
        private static void SeedFromMainInstance(string baseDirectory, string dataDirectory)
        {
            try
            {
                string target = Path.Combine(dataDirectory, "quotes.txt");
                if (File.Exists(target)) return;
                string source = Path.Combine(baseDirectory, "quotes.txt");
                if (File.Exists(source)) File.Copy(source, target);
            }
            catch
            {
                // 复制失败时由 QuoteRepository 自己创建默认内容，不影响启动。
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
    }
}
