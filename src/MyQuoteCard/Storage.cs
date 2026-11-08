using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MyQuoteCard
{
    internal static class UiText
    {
        public static bool IsEnglish(AppState state)
        {
            return state != null && string.Equals(state.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        }

        public static string Get(AppState state, string chinese, string english)
        {
            return IsEnglish(state) ? english : chinese;
        }
    }

    internal sealed class AppState
    {
        private readonly string _path;

        public string TodayDate = "";
        public string TodayQuote = "";
        public string LastQuote = "";
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public int CardWidth = 460;
        public int CardHeight = 190;
        public bool TopMost = true;   // 新建卡片默认置顶
        public int FontAdjustment = 0;
        /// <summary>固定字号（磅）。0 表示跟随卡片大小自动适应。</summary>
        public int ManualFontSize = 0;
        public int BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
        public int TextColorArgb = unchecked((int)0xFF000000);
        public string FontName = "Microsoft YaHei UI";
        public int FontStyleValue = 0;
        public string BackgroundImagePath = "";
        public bool TransparentBackground = false;
        public bool ClickThrough = false;
        public int TransparencyDefaultVersion = 0;
        public bool AutoFitText = true;
        public bool ParagraphMode = false;
        public string UiLanguage = "zh-CN";
        public int ThemeDefaultVersion = 0;
        public bool StartupInitialized = false;
        public bool StartupEnableFailed = false;
        public bool HintShown = false;

        private AppState(string path)
        {
            _path = path;
        }

        public static AppState Load(string path)
        {
            AppState state = new AppState(path);
            if (!File.Exists(path))
            {
                state.ThemeDefaultVersion = 2;
                state.TransparencyDefaultVersion = 2;
                return state;
            }

            try
            {
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = rawLine.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = rawLine.Substring(0, separator);
                    string value = rawLine.Substring(separator + 1);
                    int number;
                    bool flag;
                    switch (key)
                    {
                        case "TodayDate": state.TodayDate = Decode(value); break;
                        case "TodayQuote": state.TodayQuote = Decode(value); break;
                        case "LastQuote": state.LastQuote = Decode(value); break;
                        case "X": if (int.TryParse(value, out number)) state.X = number; break;
                        case "Y": if (int.TryParse(value, out number)) state.Y = number; break;
                        case "CardWidth": if (int.TryParse(value, out number)) state.CardWidth = number; break;
                        case "CardHeight": if (int.TryParse(value, out number)) state.CardHeight = number; break;
                        case "TopMost": if (bool.TryParse(value, out flag)) state.TopMost = flag; break;
                        case "FontAdjustment": if (int.TryParse(value, out number)) state.FontAdjustment = number; break;
                        case "ManualFontSize": if (int.TryParse(value, out number)) state.ManualFontSize = number; break;
                        case "BackgroundColorArgb": if (int.TryParse(value, out number)) state.BackgroundColorArgb = number; break;
                        case "TextColorArgb": if (int.TryParse(value, out number)) state.TextColorArgb = number; break;
                        case "FontName": state.FontName = Decode(value); break;
                        case "FontStyleValue": if (int.TryParse(value, out number)) state.FontStyleValue = number; break;
                        case "BackgroundImagePath": state.BackgroundImagePath = Decode(value); break;
                        case "TransparentBackground": if (bool.TryParse(value, out flag)) state.TransparentBackground = flag; break;
                        case "ClickThrough": if (bool.TryParse(value, out flag)) state.ClickThrough = flag; break;
                        case "TransparencyDefaultVersion": if (int.TryParse(value, out number)) state.TransparencyDefaultVersion = number; break;
                        case "AutoFitText": if (bool.TryParse(value, out flag)) state.AutoFitText = flag; break;
                        case "ParagraphMode": if (bool.TryParse(value, out flag)) state.ParagraphMode = flag; break;
                        case "UiLanguage": state.UiLanguage = Decode(value); break;
                        case "ThemeDefaultVersion": if (int.TryParse(value, out number)) state.ThemeDefaultVersion = number; break;
                        case "StartupInitialized": if (bool.TryParse(value, out flag)) state.StartupInitialized = flag; break;
                        case "StartupEnableFailed": if (bool.TryParse(value, out flag)) state.StartupEnableFailed = flag; break;
                        case "HintShown": if (bool.TryParse(value, out flag)) state.HintShown = flag; break;
                    }
                }
            }
            catch
            {
                // A damaged settings file should never prevent the card from opening.
            }

            // 下限跟着“最小字号 + 边距”走，这里只留系统安全地板，不再把用户拖小的尺寸顶回去
            state.CardWidth = Math.Max(40, Math.Min(1600, state.CardWidth));
            state.CardHeight = Math.Max(30, Math.Min(1200, state.CardHeight));
            state.FontAdjustment = Math.Max(-24, Math.Min(80, state.FontAdjustment));
            state.ManualFontSize = Math.Max(0, Math.Min(400, state.ManualFontSize));
            if (string.IsNullOrWhiteSpace(state.FontName)) state.FontName = "Microsoft YaHei UI";
            if (!string.Equals(state.UiLanguage, "en", StringComparison.OrdinalIgnoreCase))
                state.UiLanguage = "zh-CN";
            state.FontStyleValue = Math.Max(0, Math.Min(15, state.FontStyleValue));
            if (state.ThemeDefaultVersion < 1)
            {
                bool stillUsingOldDefault = state.BackgroundColorArgb == unchecked((int)0xFF080808) &&
                    state.TextColorArgb == unchecked((int)0xFFFFFFFF) &&
                    string.IsNullOrEmpty(state.BackgroundImagePath) && !state.TransparentBackground;
                if (stillUsingOldDefault)
                {
                    state.BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
                    state.TextColorArgb = unchecked((int)0xFF000000);
                }
                state.ThemeDefaultVersion = 1;
            }
            if (state.ThemeDefaultVersion < 2)
            {
                state.EnsureReadableTextColor();
                state.ThemeDefaultVersion = 2;
            }
            if (state.TransparencyDefaultVersion < 2)
            {
                // Pre-release builds shared one settings file. Reset this choice once so the
                // updated public build visibly starts with its intended non-transparent card.
                state.TransparentBackground = false;
                state.TransparencyDefaultVersion = 2;
            }
            return state;
        }

        public bool EnsureReadableTextColor()
        {
            if (!string.IsNullOrEmpty(BackgroundImagePath) || TransparentBackground) return false;
            int backgroundBrightness = Brightness(BackgroundColorArgb);
            int textBrightness = Brightness(TextColorArgb);
            if (Math.Abs(backgroundBrightness - textBrightness) >= 70) return false;
            int readableColor = backgroundBrightness >= 140 ?
                unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
            if (TextColorArgb == readableColor) return false;
            TextColorArgb = readableColor;
            return true;
        }

        private static int Brightness(int argb)
        {
            int red = (argb >> 16) & 255;
            int green = (argb >> 8) & 255;
            int blue = argb & 255;
            return (red * 299 + green * 587 + blue * 114) / 1000;
        }

        public void Save()
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string[] lines = new string[]
            {
                "TodayDate=" + Encode(TodayDate),
                "TodayQuote=" + Encode(TodayQuote),
                "LastQuote=" + Encode(LastQuote),
                "X=" + X.ToString(CultureInfo.InvariantCulture),
                "Y=" + Y.ToString(CultureInfo.InvariantCulture),
                "CardWidth=" + CardWidth.ToString(CultureInfo.InvariantCulture),
                "CardHeight=" + CardHeight.ToString(CultureInfo.InvariantCulture),
                "TopMost=" + TopMost.ToString(),
                "FontAdjustment=" + FontAdjustment.ToString(CultureInfo.InvariantCulture),
                "ManualFontSize=" + ManualFontSize.ToString(CultureInfo.InvariantCulture),
                "BackgroundColorArgb=" + BackgroundColorArgb.ToString(CultureInfo.InvariantCulture),
                "TextColorArgb=" + TextColorArgb.ToString(CultureInfo.InvariantCulture),
                "FontName=" + Encode(FontName),
                "FontStyleValue=" + FontStyleValue.ToString(CultureInfo.InvariantCulture),
                "BackgroundImagePath=" + Encode(BackgroundImagePath),
                "TransparentBackground=" + TransparentBackground.ToString(),
                "ClickThrough=" + ClickThrough.ToString(),
                "TransparencyDefaultVersion=" + TransparencyDefaultVersion.ToString(CultureInfo.InvariantCulture),
                "AutoFitText=" + AutoFitText.ToString(),
                "ParagraphMode=" + ParagraphMode.ToString(),
                "UiLanguage=" + Encode(UiLanguage),
                "ThemeDefaultVersion=" + ThemeDefaultVersion.ToString(CultureInfo.InvariantCulture),
                "StartupInitialized=" + StartupInitialized.ToString(),
                "StartupEnableFailed=" + StartupEnableFailed.ToString(),
                "HintShown=" + HintShown.ToString()
            };
            File.WriteAllLines(_path, lines, new UTF8Encoding(false));
        }

        private static string Encode(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string Decode(string text)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch { return ""; }
        }
    }

    internal sealed class QuoteRepository
    {
        private readonly string _path;
        private readonly AppState _state;
        private const string FirstRunText = "双击文字编辑内容库；右键卡片打开设置。";
        private const string EnglishFirstRunText = "Double-click the text to edit your library; right-click the card for settings.";
        private const string PreviousFirstRunText = "右键卡片，编辑你的内容，也把卡片变成你的样子。";
        private const string EarlierFirstRunText = "右键卡片，写下你的句子，也把卡片变成你的样子。";

        public QuoteRepository(string path, AppState state)
        {
            _path = path;
            _state = state;
            EnsureExists();
            MigrateFirstRunText();
        }

        public string FilePath { get { return _path; } }
        public bool IsParagraphMode { get { return _state.ParagraphMode; } }
        public bool IsEnglish { get { return UiText.IsEnglish(_state); } }

        public List<string> Load()
        {
            EnsureExists();
            try
            {
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                return _state.ParagraphMode ? ParseParagraphLines(lines) : NormalizeSingleLines(lines);
            }
            catch
            {
                return new List<string> { DefaultFirstRunText };
            }
        }

        public void SaveEditorLines(IEnumerable<string> lines)
        {
            List<string> entries = _state.ParagraphMode ? ParseParagraphLines(lines) : NormalizeSingleLines(lines);
            SaveEntries(entries);
        }

        public void SaveEntries(IEnumerable<string> entries)
        {
            List<string> normalized = NormalizeEntries(entries, _state.ParagraphMode);
            if (normalized.Count == 0) throw new InvalidOperationException(
                UiText.Get(_state, "请至少保留一条内容。", "Please keep at least one entry."));
            if (_state.ParagraphMode)
                File.WriteAllText(_path, string.Join(Environment.NewLine + Environment.NewLine,
                    normalized.ToArray()), new UTF8Encoding(false));
            else
                File.WriteAllLines(_path, normalized.ToArray(), new UTF8Encoding(false));
        }

        private void EnsureExists()
        {
            if (File.Exists(_path)) return;
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(_path, new string[] { DefaultFirstRunText }, new UTF8Encoding(false));
        }

        public void RefreshDefaultTextLanguage()
        {
            MigrateFirstRunText();
        }

        private string DefaultFirstRunText
        {
            get { return UiText.Get(_state, FirstRunText, EnglishFirstRunText); }
        }

        private void MigrateFirstRunText()
        {
            try
            {
                string current = File.ReadAllText(_path, Encoding.UTF8).Trim();
                if (current == FirstRunText || current == EnglishFirstRunText ||
                    current == PreviousFirstRunText || current == EarlierFirstRunText)
                    File.WriteAllLines(_path, new string[] { DefaultFirstRunText }, new UTF8Encoding(false));
            }
            catch
            {
                // Existing user content must never be disturbed when migration cannot be verified.
            }
        }

        private static List<string> NormalizeSingleLines(IEnumerable<string> lines)
        {
            List<string> entries = new List<string>();
            if (lines == null) return entries;
            foreach (string line in lines)
            {
                string value = (line ?? "").Trim();
                if (value.Length > 0) entries.Add(value);
            }
            return NormalizeEntries(entries, false);
        }

        private static List<string> ParseParagraphLines(IEnumerable<string> lines)
        {
            List<string> entries = new List<string>();
            StringBuilder current = new StringBuilder();
            if (lines != null)
            {
                foreach (string rawLine in lines)
                {
                    string line = (rawLine ?? "").Trim();
                    if (line.Length == 0)
                    {
                        if (current.Length > 0)
                        {
                            entries.Add(current.ToString());
                            current.Length = 0;
                        }
                    }
                    else
                    {
                        if (current.Length > 0) current.Append(Environment.NewLine);
                        current.Append(line);
                    }
                }
            }
            if (current.Length > 0) entries.Add(current.ToString());
            return NormalizeEntries(entries, true);
        }

        private static List<string> NormalizeEntries(IEnumerable<string> entries, bool keepLineBreaks)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (entries == null) return result;
            foreach (string entry in entries)
            {
                string value = (entry ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                string[] parts = value.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> cleaned = new List<string>();
                foreach (string part in parts)
                {
                    string text = part.Trim();
                    if (text.Length > 0) cleaned.Add(text);
                }
                value = string.Join(keepLineBreaks ? Environment.NewLine : " ", cleaned.ToArray()).Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                result.Add(value);
            }
            return result;
        }
    }

    internal sealed class DailyQuotePicker
    {
        private readonly AppState _state;
        private readonly QuoteRepository _repository;

        public DailyQuotePicker(AppState state, QuoteRepository repository)
        {
            _state = state;
            _repository = repository;
        }

        public string EnsureToday()
        {
            List<string> quotes = _repository.Load();

            // 不再按天自动轮换：只要当前这句话还在内容库里，就一直显示它。
            // 想换内容时，右键菜单里的“切换到另一条内容”可以随时手动切换。
            if (_state.TodayQuote.Length > 0 && quotes.Contains(_state.TodayQuote))
                return _state.TodayQuote;

            string previous = _state.TodayQuote;
            _state.LastQuote = previous;
            _state.TodayQuote = Pick(quotes, previous);
            _state.TodayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _state.Save();
            return _state.TodayQuote;
        }

        public string PickAnother()
        {
            List<string> quotes = _repository.Load();
            string previous = _state.TodayQuote;
            _state.LastQuote = previous;
            _state.TodayQuote = Pick(quotes, previous);
            _state.TodayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _state.Save();
            return _state.TodayQuote;
        }

        public string RefreshAfterEdit()
        {
            List<string> quotes = _repository.Load();
            if (!quotes.Contains(_state.TodayQuote))
            {
                _state.TodayQuote = Pick(quotes, _state.TodayQuote);
                _state.TodayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _state.Save();
            }
            return _state.TodayQuote;
        }

        private string Pick(List<string> quotes, string avoid)
        {
            if (quotes == null || quotes.Count == 0) return UiText.Get(_state,
                "请在内容库中添加一条内容。", "Please add an entry to the content library.");
            List<string> candidates = quotes;
            if (quotes.Count > 1 && !string.IsNullOrEmpty(avoid))
            {
                candidates = quotes.Where(delegate(string q) { return q != avoid; }).ToList();
                if (candidates.Count == 0) candidates = quotes;
            }

            byte[] bytes = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            uint value = BitConverter.ToUInt32(bytes, 0);
            return candidates[(int)(value % (uint)candidates.Count)];
        }
    }

    internal static class StartupManager
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>第 1 张沿用原来的值名，其余每张一个独立值名，多开时互不覆盖。</summary>
        internal static string ValueNameFor(int instanceIndex)
        {
            return instanceIndex <= 1 ? "MyQuoteCard" : "MyQuoteCard" + instanceIndex.ToString();
        }

        internal static string CommandLineFor(int instanceIndex)
        {
            string exe = "\"" + System.Windows.Forms.Application.ExecutablePath + "\"";
            return instanceIndex <= 1 ? exe + " --startup" : exe + " --instance=" + instanceIndex.ToString();
        }

        public static bool IsEnabled(int instanceIndex)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (key == null) return false;
                string value = key.GetValue(ValueNameFor(instanceIndex)) as string;
                if (string.IsNullOrEmpty(value)) return false;
                return value.IndexOf(System.Windows.Forms.Application.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static void SetEnabled(int instanceIndex, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) throw new InvalidOperationException("无法打开开机启动设置。");
                if (enabled)
                    key.SetValue(ValueNameFor(instanceIndex), CommandLineFor(instanceIndex),
                        RegistryValueKind.String);
                else
                    key.DeleteValue(ValueNameFor(instanceIndex), false);
            }
        }
    }

    internal static class SelfTests
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "MyQuoteCardTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string statePath = Path.Combine(root, "state.ini");
                string quotePath = Path.Combine(root, "quotes.txt");
                AppState state = AppState.Load(statePath);
                if (ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(0, 0)) != ResizeSupport.TopLeft ||
                    ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(459, 189)) != ResizeSupport.BottomRight ||
                    ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(230, 95)) != ResizeSupport.Client) return 25;
                if (state.BackgroundColorArgb != unchecked((int)0xFFFFFFFF) ||
                    state.TextColorArgb != unchecked((int)0xFF000000) ||
                    state.ThemeDefaultVersion != 2 || state.TransparentBackground ||
                    state.TransparencyDefaultVersion != 2) return 10;
                QuoteRepository repository = new QuoteRepository(quotePath, state);
                repository.SaveEntries(new string[] { "第一句", "第二句", "第二句", "  " });
                if (repository.Load().Count != 2) return 11;

                DailyQuotePicker picker = new DailyQuotePicker(state, repository);
                string first = picker.EnsureToday();
                string again = picker.EnsureToday();
                if (first != again) return 12;
                string other = picker.PickAnother();
                if (other == first) return 13;

                state.BackgroundColorArgb = unchecked((int)0xFF123456);
                state.TextColorArgb = unchecked((int)0xFFF0E0D0);
                state.FontName = "Arial";
                state.FontStyleValue = 1;
                state.BackgroundImagePath = Path.Combine(root, "background.png");
                state.TransparentBackground = true;
                state.AutoFitText = false;
                state.Save();

                AppState reloaded = AppState.Load(statePath);
                if (reloaded.TodayQuote != other || reloaded.TodayDate.Length != 10) return 14;
                if (reloaded.BackgroundColorArgb != unchecked((int)0xFF123456) ||
                    reloaded.TextColorArgb != unchecked((int)0xFFF0E0D0) ||
                    reloaded.FontName != "Arial" || reloaded.FontStyleValue != 1 ||
                    !reloaded.TransparentBackground || reloaded.AutoFitText ||
                    reloaded.ThemeDefaultVersion != 2 ||
                    reloaded.TransparencyDefaultVersion != 2) return 15;

                string transparencyPath = Path.Combine(root, "transparency-state.ini");
                AppState oldTransparencyState = AppState.Load(transparencyPath);
                oldTransparencyState.TransparentBackground = true;
                oldTransparencyState.TransparencyDefaultVersion = 1;
                oldTransparencyState.Save();
                AppState resetTransparencyState = AppState.Load(transparencyPath);
                if (resetTransparencyState.TransparentBackground ||
                    resetTransparencyState.TransparencyDefaultVersion != 2) return 27;

                string contrastPath = Path.Combine(root, "contrast-state.ini");
                AppState contrastState = AppState.Load(contrastPath);
                contrastState.BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
                contrastState.TextColorArgb = unchecked((int)0xFFFFFFFF);
                contrastState.ThemeDefaultVersion = 1;
                contrastState.Save();
                AppState repairedContrast = AppState.Load(contrastPath);
                if (repairedContrast.TextColorArgb != unchecked((int)0xFF000000) ||
                    repairedContrast.ThemeDefaultVersion != 2) return 19;
                using (QuoteCardForm form = new QuoteCardForm(reloaded, repository, false))
                {
                    form.CreateControl();
                    if (form.Text != "自言卡") return 16;
                    if (!form.ClearMenuLayoutActive) return 18;
                    if (!form.LayerChoiceActive) return 23;
                    form.Location = new System.Drawing.Point(-10000, -10000);
                    form.Show();
                    System.Windows.Forms.Application.DoEvents();
                    if (!form.TransparentHitBoxActive) return 17;
                    IntPtr locateResult;
                    IntPtr locateSent = NativeMethods.SendMessageTimeout(form.Handle,
                        (uint)QuoteCardForm.LocateMessage, IntPtr.Zero, IntPtr.Zero,
                        0x0002, 1000, out locateResult);
                    System.Windows.Forms.Application.DoEvents();
                    if (locateSent == IntPtr.Zero ||
                        locateResult.ToInt32() != QuoteCardForm.LocateResponse) return 26;
                    form.Hide();
                }
                reloaded.UiLanguage = "en";
                reloaded.Save();
                AppState englishState = AppState.Load(statePath);
                if (!UiText.IsEnglish(englishState)) return 20;
                QuoteRepository englishRepository = new QuoteRepository(quotePath, englishState);
                using (QuoteCardForm englishForm = new QuoteCardForm(englishState, englishRepository, false))
                {
                    englishForm.CreateControl();
                    if (!englishForm.EnglishUiActive) return 21;
                    if (!englishForm.LayerChoiceActive) return 24;
                }
                using (QuoteEditorForm englishEditor = new QuoteEditorForm(englishRepository))
                {
                    if (englishEditor.Text != "Edit content library") return 22;
                }
                return 0;
            }
            catch
            {
                return 99;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
