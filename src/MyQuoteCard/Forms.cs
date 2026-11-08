using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MyQuoteCard.Shared;

namespace MyQuoteCard
{
    internal sealed class QuoteCardForm : Form
    {
        // 文字内边距：左右各 CardPadX，上下各 CardPadY。绘制和最小窗口下限共用这一组，避免算的和画的对不上
        private const int CardPadX = 10;
        private const int CardPadY = 8;
        // 只兜 Windows 自己的最小窗口，不再为别的控件预留
        private const int MinWindowWidth = 40;
        private const int MinWindowHeight = 30;
        internal const int LocateMessage = 0x8514;
        internal const int LocateResponse = 0x5143;
        // 启动器发来的字号调整：wParam = 档位差（+2/-2/+8/-8）
        internal const int FontAdjustMessage = 0x8515;
        internal const int FontAdjustResponse = 0x5144;
        // 启动器发来的样式指令：wParam = 指令号，见 ExecuteRemoteCommand
        internal const int CommandMessage = 0x8516;
        internal const int CommandResponse = 0x5145;
        private static readonly Color TransparentKeyColor = Color.FromArgb(1, 2, 3);
        private readonly AppState _state;
        private readonly QuoteRepository _repository;
        private readonly DailyQuotePicker _picker;
        private readonly Label _quoteLabel;
        private readonly Label _dateLabel;
        private readonly Label _menuLabel;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _topMostItem;
        private ToolStripMenuItem _autoFitItem;
        private ToolStripMenuItem _transparentItem;
        private ToolStripMenuItem _clickThroughItem;
        private readonly List<ToolStripMenuItem> _fontSizeItems = new List<ToolStripMenuItem>();
        private readonly NotifyIcon _trayIcon;
        private readonly Icon _trayImage;
        private readonly Timer _dayTimer;
        private Font _quoteFont;
        private Image _backgroundImage;
        private float _quoteFontSize = -1.0f;
        private Point _dragOrigin;
        private bool _dragging;
        private bool _initialized;
        private bool _resourcesDisposed;
        private TransparentHitBoxForm _transparentHitBox;
        private Timer _styleGuardTimer;

        public QuoteCardForm(AppState state, QuoteRepository repository)
            : this(state, repository, true)
        {
        }

        internal QuoteCardForm(AppState state, QuoteRepository repository, bool showTrayIcon)
        {
            _state = state;
            _repository = repository;
            _picker = new DailyQuotePicker(state, repository);

            Text = "自言卡";
            BackColor = SafeColor(state.BackgroundColorArgb, Color.White);
            ForeColor = SafeColor(state.TextColorArgb, Color.Black);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            TopMost = state.TopMost;
            Opacity = 0.97;
            // 先给系统安全地板，再套 ini 里存下来的尺寸：
            // 顺序反了会把窗体内存里的尺寸顶大，退出保存就把 ini 覆盖回大尺寸
            MinimumSize = new Size(MinWindowWidth, MinWindowHeight);
            MaximumSize = new Size(1600, 1200);
            Size = new Size(Math.Max(MinWindowWidth, state.CardWidth),
                Math.Max(MinWindowHeight, state.CardHeight));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            _quoteLabel = new Label();
            _quoteLabel.AutoEllipsis = true;
            _quoteLabel.BackColor = Color.Transparent;
            _quoteLabel.ForeColor = ForeColor;
            _quoteLabel.TextAlign = ContentAlignment.MiddleCenter;
            _quoteLabel.UseMnemonic = false;
            _quoteLabel.Cursor = Cursors.SizeAll;
            _quoteLabel.AccessibleName = "当前内容";
            Controls.Add(_quoteLabel);

            _dateLabel = new Label();
            _dateLabel.AutoSize = false;
            _dateLabel.BackColor = Color.Transparent;
            _dateLabel.ForeColor = Color.FromArgb(145, 145, 145);
            _dateLabel.Font = UiFont(9.0f, FontStyle.Regular);
            _dateLabel.TextAlign = ContentAlignment.MiddleLeft;
            _dateLabel.Cursor = Cursors.SizeAll;
            _dateLabel.Visible = false;
            Controls.Add(_dateLabel);

            _menuLabel = new Label();
            _menuLabel.AutoSize = false;
            _menuLabel.BackColor = Color.Transparent;
            _menuLabel.ForeColor = Color.FromArgb(165, 165, 165);
            _menuLabel.Font = UiFont(14.0f, FontStyle.Bold);
            _menuLabel.Text = "⋯";
            _menuLabel.TextAlign = ContentAlignment.MiddleCenter;
            _menuLabel.Cursor = Cursors.Hand;
            _menuLabel.AccessibleName = "设置";
            Controls.Add(_menuLabel);

            _menu = BuildMenu(out _startupItem, out _topMostItem);
            ContextMenuStrip = _menu;
            _quoteLabel.ContextMenuStrip = _menu;
            _dateLabel.ContextMenuStrip = _menu;
            _menuLabel.Click += delegate { _menu.Show(_menuLabel, new Point(_menuLabel.Width, _menuLabel.Height), ToolStripDropDownDirection.BelowLeft); };

            AttachDragHandlers(this);
            AttachDragHandlers(_quoteLabel);
            AttachDragHandlers(_dateLabel);
            _quoteLabel.DoubleClick += delegate { EditQuotes(); };

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "自言卡";
            _trayImage = CreateTrayIcon();
            _trayIcon.Icon = _trayImage;
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Visible = showTrayIcon;
            _trayIcon.DoubleClick += delegate { MoveCardToCenter(); };

            // 句柄重建（透明背景开关、DPI 变化）之后，穿透样式要重新挂一次
            HandleCreated += delegate { ApplyClickThroughStyle(); };

            ApplyUiLanguage();

            _dayTimer = new Timer();
            _dayTimer.Interval = 60000;
            _dayTimer.Tick += delegate { RefreshForToday(); };

            ApplyAppearance(true);
            LayoutCard();
            RestoreLocation();
            RefreshForToday();
            _initialized = true;
            _dayTimer.Start();
            StartStyleGuard();
        }

        internal bool TransparentHitBoxActive
        {
            get
            {
                return _transparentHitBox != null && !_transparentHitBox.IsDisposed &&
                    _transparentHitBox.Visible;
            }
        }

        internal bool ClearMenuLayoutActive
        {
            get
            {
                bool cardSettings = false;
                bool textSettings = false;
                foreach (ToolStripItem item in _menu.Items)
                {
                    if (item.Name == "CardSettings") cardSettings = true;
                    if (item.Name == "TextSettings") textSettings = true;
                    if (item.Name == "CardSize" || item.Name == "CustomAppearance") return false;
                }
                return _menu.ShowCheckMargin && cardSettings && textSettings;
            }
        }

        internal bool EnglishUiActive
        {
            get
            {
                ToolStripMenuItem edit = FindMenuItem(_menu.Items, "EditLibrary");
                ToolStripMenuItem bringBack = FindMenuItem(_menu.Items, "BringCardBack");
                ToolStripMenuItem english = FindMenuItem(_menu.Items, "LanguageEnglish");
                ToolStripMenuItem chinese = FindMenuItem(_menu.Items, "LanguageChinese");
                return Text == "MyQuoteCard" && edit != null &&
                    edit.Text == "Edit content library…" && bringBack != null &&
                    bringBack.Text.IndexOf("Find card", StringComparison.Ordinal) >= 0 &&
                    english != null && english.Checked && chinese != null && !chinese.Checked;
            }
        }

        internal bool LayerChoiceActive
        {
            get
            {
                ToolStripMenuItem layer = FindMenuItem(_menu.Items, "CardLayer");
                ToolStripMenuItem top = FindMenuItem(_menu.Items, "LayerTop");
                ToolStripMenuItem bottom = FindMenuItem(_menu.Items, "LayerBottom");
                return layer != null && top != null && bottom != null &&
                    top.Checked != bottom.Checked && top.Checked == TopMost;
            }
        }

        private static ToolStripMenuItem FindMenuItem(ToolStripItemCollection items, string name)
        {
            foreach (ToolStripItem item in items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                if (menuItem.Name == name) return menuItem;
                ToolStripMenuItem nested = FindMenuItem(menuItem.DropDownItems, name);
                if (nested != null) return nested;
            }
            return null;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams parameters = base.CreateParams;
                if (_state == null || !_state.TransparentBackground) parameters.ClassStyle |= CS_DROPSHADOW;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_state.TransparentBackground) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(42, 42, 42), 1.0f))
            {
                Rectangle rect = ClientRectangle;
                rect.Width -= 1;
                rect.Height -= 1;
                using (GraphicsPath path = RoundedRectangle(rect, 18))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_state.TransparentBackground)
            {
                e.Graphics.Clear(TransparentKeyColor);
                return;
            }
            e.Graphics.Clear(BackColor);
            if (_backgroundImage == null) return;

            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle destination = CoverRectangle(ClientRectangle, _backgroundImage.Size);
            e.Graphics.DrawImage(_backgroundImage, destination);
            using (Brush overlay = new SolidBrush(Color.FromArgb(62, 0, 0, 0)))
                e.Graphics.FillRectangle(overlay, ClientRectangle);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
            LayoutCard();
            FitQuoteFont();
            PositionTransparentHitBox();
            ApplyClickThroughStyle();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveCardBounds();
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            if (message.Msg == LocateMessage)
            {
                MoveCardToCenter();
                message.Result = (IntPtr)LocateResponse;
                return;
            }
            if (message.Msg == FontAdjustMessage)
            {
                int difference = message.WParam.ToInt32();
                if (difference != 0) ChangeFontAdjustment(difference);
                message.Result = (IntPtr)FontAdjustResponse;
                return;
            }
            if (message.Msg == CommandMessage)
            {
                int command = message.WParam.ToInt32();
                ExecuteRemoteCommand(command);
                message.Result = (IntPtr)CommandResponse;
                return;
            }
            base.WndProc(ref message);
            if (message.Msg != WM_NCHITTEST || (int)message.Result != HTCLIENT || WindowState != FormWindowState.Normal)
                return;
            Point screenPoint = ResizeSupport.ScreenPoint(message.LParam);
            int hit = ResizeSupport.HitTest(ClientSize, PointToClient(screenPoint));
            if (hit != HTCLIENT) message.Result = (IntPtr)hit;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (_initialized && WindowState == FormWindowState.Normal)
            {
                _state.X = Left;
                _state.Y = Top;
            }
            PositionTransparentHitBox();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateTransparentHitBox();
            // 首次打开的"怎样找到卡片"提示弹窗一并去掉，只把标记记下来，不再打扰
            if (_trayIcon != null && _trayIcon.Visible && !_state.HintShown)
            {
                _state.HintShown = true;
                _state.Save();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (WindowState == FormWindowState.Normal)
            {
                SaveCardBounds();
            }
            DisposeResources();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeResources();
            base.Dispose(disposing);
        }

        private void DisposeResources()
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            if (_dayTimer != null)
            {
                _dayTimer.Stop();
                _dayTimer.Dispose();
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_trayImage != null) _trayImage.Dispose();
            if (_quoteFont != null) _quoteFont.Dispose();
            if (_backgroundImage != null) _backgroundImage.Dispose();
            if (_transparentHitBox != null)
            {
                _transparentHitBox.ContextMenuStrip = null;
                _transparentHitBox.Dispose();
                _transparentHitBox = null;
            }
            if (_menu != null) _menu.Dispose();
        }

        private void SaveCardBounds()
        {
            if (_state == null || WindowState != FormWindowState.Normal) return;
            _state.X = Left;
            _state.Y = Top;
            _state.CardWidth = Width;
            _state.CardHeight = Height;
            _state.Save();
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem startupItem, out ToolStripMenuItem topMostItem)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.BackColor = Color.FromArgb(28, 28, 28);
            menu.ForeColor = Color.White;
            menu.Font = UiFont(10.0f, FontStyle.Regular);
            menu.Renderer = new RoundedMenuRenderer();
            menu.Opened += delegate { RoundMenuWindow(menu); };
            menu.Padding = new Padding(5);

            ToolStripMenuItem editItem = CreateMenuItem("EditLibrary");
            editItem.Click += delegate { EditQuotes(); };
            menu.Items.Add(editItem);

            ToolStripMenuItem anotherItem = CreateMenuItem("SwitchQuote");
            anotherItem.Click += delegate
            {
                SetQuoteDisplay(_picker.PickAnother());
                FitQuoteFont();
            };
            menu.Items.Add(anotherItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem cardSettingsMenu = CreateMenuItem("CardSettings");
            ToolStripMenuItem sizeMenu = CreateMenuItem("CardSize");
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeSmall", 360, 150));
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeMedium", 460, 190));
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeLarge", 580, 240));
            ToolStripMenuItem backgroundColorItem = CreateMenuItem("BackgroundColor");
            backgroundColorItem.Click += delegate { ChooseBackgroundColor(); };
            ToolStripMenuItem transparentItem = CreateMenuItem("TransparentBackground");
            transparentItem.CheckOnClick = true;
            transparentItem.Checked = _state.TransparentBackground;
            _transparentItem = transparentItem;
            transparentItem.Click += delegate { ApplyTransparentBackground(transparentItem.Checked); };
            ToolStripMenuItem clickThroughItem = CreateMenuItem("ClickThrough");
            clickThroughItem.CheckOnClick = true;
            clickThroughItem.Checked = _state.ClickThrough;
            _clickThroughItem = clickThroughItem;
            clickThroughItem.Click += delegate { ApplyClickThrough(clickThroughItem.Checked); };
            ToolStripMenuItem imageItem = CreateMenuItem("BackgroundImage");
            imageItem.Click += delegate { ChooseBackgroundImage(); };
            ToolStripMenuItem clearImageItem = CreateMenuItem("ClearBackgroundImage");
            clearImageItem.Click += delegate { ClearBackgroundImage(true); };
            ToolStripMenuItem layerMenu = CreateMenuItem("CardLayer");
            ToolStripMenuItem localTopMostItem = CreateMenuItem("LayerTop");
            ToolStripMenuItem bottomItem = CreateMenuItem("LayerBottom");
            localTopMostItem.Checked = _state.TopMost;
            bottomItem.Checked = !_state.TopMost;
            localTopMostItem.Click += delegate
            {
                SetCardLayer(true);
                localTopMostItem.Checked = true;
                bottomItem.Checked = false;
            };
            bottomItem.Click += delegate
            {
                SetCardLayer(false);
                localTopMostItem.Checked = false;
                bottomItem.Checked = true;
            };
            layerMenu.DropDownItems.Add(localTopMostItem);
            layerMenu.DropDownItems.Add(bottomItem);
            ToolStripMenuItem resetAppearanceItem = CreateMenuItem("ResetAppearance");
            resetAppearanceItem.Click += delegate { ResetAppearance(); };
            cardSettingsMenu.DropDownItems.Add(sizeMenu);
            cardSettingsMenu.DropDownItems.Add(backgroundColorItem);
            cardSettingsMenu.DropDownItems.Add(transparentItem);
            cardSettingsMenu.DropDownItems.Add(clickThroughItem);
            cardSettingsMenu.DropDownItems.Add(imageItem);
            cardSettingsMenu.DropDownItems.Add(clearImageItem);
            cardSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            cardSettingsMenu.DropDownItems.Add(layerMenu);
            cardSettingsMenu.DropDownItems.Add(resetAppearanceItem);
            menu.Items.Add(cardSettingsMenu);

            ToolStripMenuItem textSettingsMenu = CreateMenuItem("TextSettings");
            ToolStripMenuItem larger = CreateMenuItem("TextLarger");
            larger.Click += delegate { ChangeFontAdjustment(2); };
            ToolStripMenuItem smaller = CreateMenuItem("TextSmaller");
            smaller.Click += delegate { ChangeFontAdjustment(-2); };
            ToolStripMenuItem smallest = CreateMenuItem("TextSmallest");
            smallest.Click += delegate
            {
                _state.ManualFontSize = 0;
                _state.FontAdjustment = -24;
                _state.Save();
                SyncFontSizeChecks();
                FitQuoteFont();
            };
            ToolStripMenuItem muchLarger = CreateMenuItem("TextMuchLarger");
            muchLarger.Click += delegate { ChangeFontAdjustment(8); };
            ToolStripMenuItem muchSmaller = CreateMenuItem("TextMuchSmaller");
            muchSmaller.Click += delegate { ChangeFontAdjustment(-8); };
            _autoFitItem = CreateMenuItem("AutoFitText");
            _autoFitItem.CheckOnClick = true;
            _autoFitItem.Checked = _state.AutoFitText;
            _autoFitItem.Click += delegate
            {
                _state.AutoFitText = _autoFitItem.Checked;
                if (_autoFitItem.Checked) _state.ManualFontSize = 0;
                _state.Save();
                _quoteFontSize = -1.0f;
                SyncFontSizeChecks();
                FitQuoteFont();
            };
            ToolStripMenuItem reset = CreateMenuItem("ResetTextSize");
            reset.Click += delegate
            {
                _state.FontAdjustment = 0;
                _state.ManualFontSize = 0;
                _state.AutoFitText = true;
                _state.Save();
                if (_autoFitItem != null) _autoFitItem.Checked = true;
                _quoteFontSize = -1.0f;
                SyncFontSizeChecks();
                FitQuoteFont();
            };

            // 固定字号（磅）：选定后字号不再随卡片大小变化，重启也会记住
            ToolStripMenuItem fixedSizeMenu = CreateMenuItem("TextFixedSize");
            int[] presetSizes = new int[] { 20, 24, 30, 36, 42, 48, 60, 72, 90, 110 };
            foreach (int preset in presetSizes)
            {
                ToolStripMenuItem sizeItem = CreateMenuItem("TextSize" + preset.ToString(CultureInfo.InvariantCulture));
                int value = preset;
                sizeItem.Click += delegate { ApplyFixedFontSize(value); };
                fixedSizeMenu.DropDownItems.Add(sizeItem);
                _fontSizeItems.Add(sizeItem);
            }
            ToolStripMenuItem textColorItem = CreateMenuItem("TextColor");
            textColorItem.Click += delegate { ChooseTextColor(); };
            ToolStripMenuItem fontItem = CreateMenuItem("ChooseFont");
            fontItem.Click += delegate { ChooseQuoteFont(); };
            textSettingsMenu.DropDownItems.Add(larger);
            textSettingsMenu.DropDownItems.Add(smaller);
            textSettingsMenu.DropDownItems.Add(muchLarger);
            textSettingsMenu.DropDownItems.Add(muchSmaller);
            textSettingsMenu.DropDownItems.Add(smallest);
            textSettingsMenu.DropDownItems.Add(reset);
            textSettingsMenu.DropDownItems.Add(fixedSizeMenu);
            textSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            textSettingsMenu.DropDownItems.Add(_autoFitItem);
            textSettingsMenu.DropDownItems.Add(textColorItem);
            textSettingsMenu.DropDownItems.Add(fontItem);
            menu.Items.Add(textSettingsMenu);
            SyncFontSizeChecks();

            ToolStripMenuItem localStartupItem = CreateMenuItem("Startup");
            localStartupItem.CheckOnClick = true;
            localStartupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(localStartupItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem showItem = CreateMenuItem("BringCardBack");
            showItem.Click += delegate { MoveCardToCenter(); };
            ToolStripSeparator findSeparator = new ToolStripSeparator();
            menu.Items.Insert(0, findSeparator);
            menu.Items.Insert(0, showItem);

            ToolStripMenuItem folderItem = CreateMenuItem("OpenLibraryFolder");
            folderItem.Click += delegate { OpenQuoteFolder(); };
            menu.Items.Add(folderItem);

            ToolStripMenuItem languageMenu = CreateMenuItem("Language");
            ToolStripMenuItem chineseItem = CreateMenuItem("LanguageChinese");
            ToolStripMenuItem englishItem = CreateMenuItem("LanguageEnglish");
            chineseItem.Click += delegate { SetLanguage("zh-CN"); };
            englishItem.Click += delegate { SetLanguage("en"); };
            languageMenu.DropDownItems.Add(chineseItem);
            languageMenu.DropDownItems.Add(englishItem);
            menu.Items.Add(languageMenu);

            ToolStripMenuItem helpItem = CreateMenuItem("Help");
            helpItem.Click += delegate
            {
                MessageBox.Show(T(
                    "• 拖动卡片，可以改变位置\n• 拖动卡片的边缘或四角，可以自由调整大小\n• 双击文字，可以编辑内容库\n• “切换到另一条内容”会立即更换当前显示\n• “卡片设置”管理大小、背景和卡片层级\n• “置顶”让卡片盖在其他窗口上；“置底”允许其他窗口盖住卡片\n• “文字设置”管理字号、颜色和字体；“文字边距”调整文字与卡片边缘的距离\n• 想在同一条内容里强制换行，写 \\n（反斜杠加 n），显示时会在那里断行\n• 找不到卡片时，双击右下角小图标，或用右键菜单「把卡片移到屏幕中央」\n\n• 勾选“开机自启动”表示已成功写入 Windows 启动项\n• 透明背景模式下，整张卡片仍可右键、拖动和调整大小\n• 所有内容都只保存在你的电脑上",
                    "• Drag the card to move it\n• Drag any edge or corner to resize the card freely\n• Double-click the text to edit your library\n• “Switch to another entry” changes the current text immediately\n• “Card settings” controls size, background, and card layer\n• “Top” keeps the card above other windows; “Bottom” lets other windows cover it\n• “Text settings” controls size, color, and font; “Text margins” sets the gap between text and the card edge\n• Write \\n inside an entry to force a line break there\n• If the card is lost, double-click the tray icon or use “Move card to screen center”\n\n• A check beside “Start with Windows” means the startup entry is active\n• In transparent mode, you can still right-click, drag, and resize the whole card\n• All content stays on your computer"),
                    T("自言卡 · 使用方法", "MyQuoteCard · Help"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(helpItem);

            // 关于：一眼确认当前跑的是哪一个版本，省得靠记忆和印象对号
            ToolStripMenuItem aboutItem = CreateMenuItem("About");
            aboutItem.Click += delegate
            {
                MessageBox.Show(T(
                    "版本：" + AppVersion.Tag + "（" + AppVersion.Number + "）\n\n" +
                    "基于 oocreator/MyQuoteCard 的二次定制版本，遵循原项目 MIT License。\n" +
                    "仅个人自用，非官方版本。\n\n" +
                    "数据保存在 %LOCALAPPDATA%\\MyQuoteCard\\",
                    "Version: " + AppVersion.Tag + " (" + AppVersion.Number + ")\n\n" +
                    "A personal customized build based on oocreator/MyQuoteCard, " +
                    "following the original MIT License.\nFor personal use only; not an official release.\n\n" +
                    "Data is stored in %LOCALAPPDATA%\\MyQuoteCard\\"),
                    T("自言卡 · 关于", "MyQuoteCard · About"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(aboutItem);

            ToolStripMenuItem exitItem = CreateMenuItem("Exit");
            exitItem.Click += delegate { Close(); };
            menu.Items.Add(exitItem);

            menu.Opening += delegate
            {
                try { localStartupItem.Checked = StartupManager.IsEnabled(Program.InstanceIndex); }
                catch { localStartupItem.Checked = false; }
                localTopMostItem.Checked = TopMost;
                bottomItem.Checked = !TopMost;
                if (_autoFitItem != null) _autoFitItem.Checked = _state.AutoFitText;
                SyncFontSizeChecks();
                transparentItem.Checked = _state.TransparentBackground;
                if (_clickThroughItem != null) _clickThroughItem.Checked = _state.ClickThrough;
                showItem.Visible = menu.SourceControl == null;
                findSeparator.Visible = showItem.Visible;
                ApplyMenuLanguage(menu.Items);
                ApplyMenuColors(menu.Items);
            };
            ApplyMenuLanguage(menu.Items);
            ApplyMenuColors(menu.Items);
            startupItem = localStartupItem;
            topMostItem = localTopMostItem;
            return menu;
        }

        /// <summary>Win11 的菜单是圆角矩形，ToolStrip 默认是方的，这里按窗口区域裁一下。</summary>
        private static void RoundMenuWindow(ToolStripDropDown dropDown)
        {
            if (dropDown == null || !dropDown.IsHandleCreated) return;
            IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0,
                dropDown.Width + 1, dropDown.Height + 1, 8, 8);
            if (region == IntPtr.Zero) return;
            if (NativeMethods.SetWindowRgn(dropDown.Handle, region, true) == 0)
                NativeMethods.DeleteObject(region);
        }

        private ToolStripMenuItem CreateMenuItem(string name)
        {
            ToolStripMenuItem item = new ToolStripMenuItem();
            item.Name = name;
            return item;
        }

        private void ApplyMenuLanguage(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                if (menuItem.Name != null && menuItem.Name.StartsWith("TextSize", StringComparison.Ordinal))
                {
                    menuItem.Text = menuItem.Name.Substring("TextSize".Length) +
                        (UiText.IsEnglish(_state) ? " pt" : " 磅");
                    continue;
                }
                switch (menuItem.Name)
                {
                    case "EditLibrary": menuItem.Text = T("编辑内容库…", "Edit content library…"); break;
                    case "SwitchQuote": menuItem.Text = T("切换到另一条内容", "Switch to another entry"); break;
                    case "CardSettings": menuItem.Text = T("卡片设置", "Card settings"); break;
                    case "CardSize": menuItem.Text = T("卡片大小", "Card size"); break;
                    case "SizeSmall": menuItem.Text = T("小", "Small"); break;
                    case "SizeMedium": menuItem.Text = T("中", "Medium"); break;
                    case "SizeLarge": menuItem.Text = T("大", "Large"); break;
                    case "BackgroundColor": menuItem.Text = T("背景颜色…", "Background color…"); break;
                    case "TransparentBackground": menuItem.Text = T("透明背景（仅显示文字）", "Transparent background (text only)"); break;
                    case "ClickThrough": menuItem.Text = T("鼠标穿透（整张卡片）", "Click-through (whole card)"); break;
                    case "BackgroundImage": menuItem.Text = T("选择背景图片…", "Choose background image…"); break;
                    case "ClearBackgroundImage": menuItem.Text = T("清除背景图片", "Remove background image"); break;
                    case "CardLayer": menuItem.Text = T("卡片层级", "Card layer"); break;
                    case "LayerTop": menuItem.Text = T("置顶（显示在其他窗口前面）", "Top (above other windows)"); break;
                    case "LayerBottom": menuItem.Text = T("置底（允许其他窗口盖住卡片）", "Bottom (other windows can cover the card)"); break;
                    case "ResetAppearance": menuItem.Text = T("恢复默认样式", "Reset appearance"); break;
                    case "TextSettings": menuItem.Text = T("文字设置", "Text settings"); break;
                    case "TextFixedSize": menuItem.Text = T("固定字号", "Fixed text size"); break;
                    case "TextLarger": menuItem.Text = T("大一点", "Larger"); break;
                    case "TextSmaller": menuItem.Text = T("小一点", "Smaller"); break;
                    case "TextMuchLarger": menuItem.Text = T("明显大一点", "Much larger"); break;
                    case "TextMuchSmaller": menuItem.Text = T("明显小一点", "Much smaller"); break;
                    case "TextSmallest": menuItem.Text = T("设为最小", "Smallest"); break;
                    case "AutoFitText": menuItem.Text = T("文字自动适应卡片", "Fit text to card automatically"); break;
                    case "ResetTextSize": menuItem.Text = T("恢复默认字号", "Reset text size"); break;
                    case "TextColor": menuItem.Text = T("文字颜色…", "Text color…"); break;
                    case "ChooseFont": menuItem.Text = T("选择字体…", "Choose font…"); break;
                    case "Startup": menuItem.Text = menuItem.Checked ?
                        T("开机自启动（已开启）", "Start with Windows (on)") :
                        T("开机自启动（未开启）", "Start with Windows (off)"); break;
                    case "BringCardBack": menuItem.Text = T("把卡片移到屏幕中央", "Move card to screen center"); break;
                    case "OpenLibraryFolder": menuItem.Text = T("打开内容库文件位置", "Open content library location"); break;
                    case "Language": menuItem.Text = "语言 / Language"; break;
                    case "LanguageChinese":
                        menuItem.Text = "简体中文";
                        menuItem.Checked = !UiText.IsEnglish(_state);
                        break;
                    case "LanguageEnglish":
                        menuItem.Text = "English";
                        menuItem.Checked = UiText.IsEnglish(_state);
                        break;
                    case "Help": menuItem.Text = T("使用方法", "Help"); break;
                    case "About": menuItem.Text = T("关于（版本 " + AppVersion.Tag + "）", "About (" + AppVersion.Tag + ")"); break;
                    case "Exit": menuItem.Text = T("退出", "Exit"); break;
                }
                if (menuItem.DropDownItems.Count > 0) ApplyMenuLanguage(menuItem.DropDownItems);
            }
        }

        private static void ApplyMenuColors(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = Color.FromArgb(28, 28, 28);
                item.ForeColor = Color.White;
                item.Padding = new Padding(6, 6, 6, 6);
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.DropDownItems.Count > 0)
                {
                    menuItem.DropDown.BackColor = Color.FromArgb(28, 28, 28);
                    menuItem.DropDown.ForeColor = Color.White;
                    menuItem.DropDown.Renderer = new RoundedMenuRenderer();
                    ToolStripDropDown currentDropDown = menuItem.DropDown;
                    currentDropDown.Opened += delegate { RoundMenuWindow(currentDropDown); };
                    ToolStripDropDownMenu dropDownMenu = menuItem.DropDown as ToolStripDropDownMenu;
                    if (dropDownMenu != null)
                    {
                        dropDownMenu.ShowImageMargin = false;
                        dropDownMenu.ShowCheckMargin = true;
                    }
                    ApplyMenuColors(menuItem.DropDownItems);
                }
            }
        }

        private ToolStripMenuItem CreateSizeItem(string name, int width, int height)
        {
            ToolStripMenuItem item = CreateMenuItem(name);
            item.Click += delegate
            {
                Size = new Size(width, height);
                _state.CardWidth = width;
                _state.CardHeight = height;
                KeepOnScreen();
                _state.Save();
            };
            return item;
        }

        private void SetCardLayer(bool topMost)
        {
            TopMost = topMost;
            _state.TopMost = topMost;
            if (_transparentHitBox != null) _transparentHitBox.TopMost = topMost;
            _state.Save();
            PositionTransparentHitBox();
        }

        private string T(string chinese, string english)
        {
            return UiText.Get(_state, chinese, english);
        }

        private void SetLanguage(string language)
        {
            _state.UiLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ?
                "en" : "zh-CN";
            _repository.RefreshDefaultTextLanguage();
            SetQuoteDisplay(_picker.RefreshAfterEdit());
            _state.Save();
            ApplyUiLanguage();
            FitQuoteFont();
        }

        private void ApplyUiLanguage()
        {
            Text = T("自言卡", "MyQuoteCard");
            if (Program.InstanceIndex > 1) Text = Text + " #" + Program.InstanceIndex;
            _quoteLabel.AccessibleName = T("当前内容", "Current entry");
            _menuLabel.AccessibleName = T("设置", "Settings");
            if (_trayIcon != null) _trayIcon.Text = Text;
            if (_transparentHitBox != null && !_transparentHitBox.IsDisposed)
                _transparentHitBox.Text = T("自言卡透明点击层", "MyQuoteCard transparent interaction layer");
            if (_menu != null)
            {
                ApplyMenuLanguage(_menu.Items);
                ApplyMenuColors(_menu.Items);
            }
        }

        private void ToggleStartup()
        {
            bool requested = _startupItem.Checked;
            try
            {
                StartupManager.SetEnabled(Program.InstanceIndex, requested);
                bool actual = StartupManager.IsEnabled(Program.InstanceIndex);
                if (actual != requested) throw new InvalidOperationException(
                    T("Windows 没有保存这项设置。", "Windows did not save this setting."));
                _startupItem.Checked = actual;
                ApplyMenuLanguage(_menu.Items);
                _state.StartupInitialized = true;
                _state.StartupEnableFailed = false;
                _state.Save();
            }
            catch (Exception ex)
            {
                try { _startupItem.Checked = StartupManager.IsEnabled(Program.InstanceIndex); }
                catch { _startupItem.Checked = false; }
                ApplyMenuLanguage(_menu.Items);
                MessageBox.Show(T("没能修改开机启动设置。", "Could not change the startup setting.") +
                    "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void EditQuotes()
        {
            using (QuoteEditorForm editor = new QuoteEditorForm(_repository))
            {
                if (editor.ShowDialog(this) == DialogResult.OK)
                {
                    SetQuoteDisplay(_picker.RefreshAfterEdit());
                    FitQuoteFont();
                }
            }
        }

        private void OpenQuoteFolder()
        {
            try
            {
                Process.Start("explorer.exe", "/select,\"" + _repository.FilePath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(T("没能打开内容库文件位置。", "Could not open the content library location.") +
                    "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChooseBackgroundColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = BackColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _state.BackgroundColorArgb = dialog.Color.ToArgb();
                _state.TransparentBackground = false;
                _state.Save();
                ApplyAppearance(false);
                RecreateHandle();
                UpdateTransparentHitBox();
            }
        }

        private void ChooseTextColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = _quoteLabel.ForeColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _state.TextColorArgb = dialog.Color.ToArgb();
                _state.Save();
                ApplyAppearance(false);
            }
        }

        private void ChooseQuoteFont()
        {
            using (FontDialog dialog = new FontDialog())
            using (Font current = QuoteFont(12.0f))
            {
                dialog.Font = current;
                dialog.FontMustExist = true;
                dialog.ShowColor = false;
                dialog.ShowEffects = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _state.FontName = dialog.Font.FontFamily.Name;
                _state.FontStyleValue = (int)dialog.Font.Style;
                _state.Save();
                _quoteFontSize = -1.0f;
                FitQuoteFont();
            }
        }

        private void ChooseBackgroundImage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = T("选择卡片背景图片", "Choose a card background image");
                dialog.Filter = T("图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                    "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                Image preview = null;
                try
                {
                    preview = LoadImageUnlocked(dialog.FileName);
                    string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    if (extension.Length == 0 || extension.Length > 5) extension = ".img";
                    string dataDirectory = Path.GetDirectoryName(_repository.FilePath);
                    string destination = Path.Combine(dataDirectory, "background" + extension);
                    if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                        File.Copy(dialog.FileName, destination, true);

                    string previous = _state.BackgroundImagePath;
                    if (_backgroundImage != null) _backgroundImage.Dispose();
                    _backgroundImage = preview;
                    preview = null;
                    _state.BackgroundImagePath = destination;
                    _state.TransparentBackground = false;
                    _state.Save();
                    if (!string.Equals(previous, destination, StringComparison.OrdinalIgnoreCase))
                        TryDeleteOwnedBackground(previous);
                    ApplyAppearance(false);
                    RecreateHandle();
                    UpdateTransparentHitBox();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(T("没能使用这张背景图片。", "Could not use this background image.") +
                        "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    if (preview != null) preview.Dispose();
                }
            }
        }

        private void ClearBackgroundImage(bool save)
        {
            string previous = _state.BackgroundImagePath;
            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            _state.BackgroundImagePath = "";
            if (save) _state.Save();
            TryDeleteOwnedBackground(previous);
            ApplyAppearance(false);
        }

        private void ResetAppearance()
        {
            ClearBackgroundImage(false);
            _state.BackgroundColorArgb = Color.White.ToArgb();
            _state.TextColorArgb = Color.Black.ToArgb();
            _state.FontName = "Microsoft YaHei UI";
            _state.FontStyleValue = (int)FontStyle.Regular;
            _state.TransparentBackground = false;
            _state.Save();
            _quoteFontSize = -1.0f;
            ApplyAppearance(false);
            FitQuoteFont();
            RecreateHandle();
            UpdateTransparentHitBox();
        }

        private void ApplyAppearance(bool reloadImage)
        {
            if (_state.EnsureReadableTextColor()) _state.Save();
            BackColor = SafeColor(_state.BackgroundColorArgb, Color.White);
            ForeColor = SafeColor(_state.TextColorArgb, Color.Black);
            _quoteLabel.ForeColor = ForeColor;
            _menuLabel.ForeColor = Color.FromArgb(175, ForeColor.R, ForeColor.G, ForeColor.B);
            if (_state.TransparentBackground)
            {
                TransparencyKey = TransparentKeyColor;
                Opacity = 1.0;
            }
            else
            {
                TransparencyKey = Color.Empty;
                Opacity = 0.97;
            }
            if (reloadImage) LoadBackgroundImage();
            UpdateMinimumWindowSize();
            Invalidate(true);
        }

        /// <summary>透明背景开关：右键菜单和启动器指令共用这一条路径。</summary>
        /// <summary>启动器发来的样式指令。会弹窗的重活一律 BeginInvoke，免得在窗口过程里重入。</summary>
        private void ExecuteRemoteCommand(int command)
        {
            // 低 8 位是动作码，高位附带期望值（命令 14 用：0x100 = 期望穿透，0x200 = 期望置顶），
            // 让启动器能把自己的配置直接下发过来，而不是靠 toggle 猜
            int code = command & 0xFF;
            switch (code)
            {
                case 1: ChangeFontAdjustment(2); break;
                case 2: ChangeFontAdjustment(-2); break;
                case 3: ChangeFontAdjustment(8); break;
                case 4: ChangeFontAdjustment(-8); break;
                case 5: BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) ChooseTextColor(); }); break;
                case 6: BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) ChooseQuoteFont(); }); break;
                case 7: BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) ChooseBackgroundColor(); }); break;
                case 8: BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    ApplyTransparentBackground(!_state.TransparentBackground);
                }); break;
                case 9: SetCardLayer(true); break;
                case 10: SetCardLayer(false); break;
                case 11: SetCardLayer(!_state.TopMost); break;
                case 12: BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    ApplyClickThrough(!_state.ClickThrough);
                }); break;
                case 13: BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    EditQuotes();
                }); break;
                // 14 = 按启动器下发的期望值把穿透/置顶拉回一致（幂等，不做 toggle）
                case 14: BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    bool wantClickThrough = (command & 0x100) != 0;
                    bool wantTopMost = (command & 0x200) != 0;
                    if (_state.TopMost != wantTopMost)
                    {
                        TopMost = wantTopMost;
                        _state.TopMost = wantTopMost;
                        if (_transparentHitBox != null && !_transparentHitBox.IsDisposed)
                            _transparentHitBox.TopMost = wantTopMost;
                    }
                    if (_state.ClickThrough != wantClickThrough) ApplyClickThrough(wantClickThrough);
                    // 值本来就一致时也要强补一遍：防的是状态没错、窗口上的样式位被系统悄悄清掉了
                    GuardWindowStyles();
                    try { _state.Save(); } catch { }
                }); break;
            }
        }

        private void ApplyTransparentBackground(bool enabled)
        {
            _state.TransparentBackground = enabled;
            _state.Save();
            if (_transparentItem != null) _transparentItem.Checked = enabled;
            ApplyAppearance(false);
            RecreateHandle();
            UpdateTransparentHitBox();
        }

        /// <summary>整张卡片穿透：直接挂 WS_EX_TRANSPARENT，左键右键滚轮全部交给下层窗口。</summary>
        private void ApplyClickThrough(bool enabled)
        {
            _state.ClickThrough = enabled;
            if (_clickThroughItem != null) _clickThroughItem.Checked = enabled;
            // 先把样式挂上去，配置写不进去也不能让穿透失效
            ApplyClickThroughStyle();
            try { _state.Save(); } catch { }
        }

        private void ApplyClickThroughStyle()
        {
            ApplyClickThroughToHandle(Handle);
            if (_transparentHitBox != null && !_transparentHitBox.IsDisposed && _transparentHitBox.IsHandleCreated)
                ApplyClickThroughToHandle(_transparentHitBox.Handle);
        }

        /// <summary>
        /// 巡检：读窗口真身上的扩展样式位，跟记忆里的状态对不上就补回去。
        /// 以卡片自己的 state（也就是启动器那份配置）为准，而不是把界面上的勾回写成关闭。
        /// </summary>
        private void GuardWindowStyles()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;

            int style = NativeMethods.GetWindowLong(Handle, GwlExStyle);
            bool isTopMost = (style & WsExTopMost) != 0;
            if (isTopMost != _state.TopMost)
            {
                IntPtr order = _state.TopMost ? HwndTopMost : HwndNoTopMost;
                NativeMethods.SetWindowPos(Handle, order, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
                if (_transparentHitBox != null && !_transparentHitBox.IsDisposed && _transparentHitBox.IsHandleCreated)
                    NativeMethods.SetWindowPos(_transparentHitBox.Handle, order, 0, 0, 0, 0,
                        SwpNoSize | SwpNoMove | SwpNoActivate);
            }

            // 穿透位两个窗口都要核：开着穿透时真正接鼠标的是透明命中层
            ApplyClickThroughToHandle(Handle);
            if (_transparentHitBox != null && !_transparentHitBox.IsDisposed && _transparentHitBox.IsHandleCreated)
                ApplyClickThroughToHandle(_transparentHitBox.Handle);
        }

        /// <summary>样式巡检，10 秒一轮；单轮两次 GetWindowLong，开销在微秒级，实测无感。</summary>
        private void StartStyleGuard()
        {
            if (_styleGuardTimer != null) return;
            _styleGuardTimer = new System.Windows.Forms.Timer();
            _styleGuardTimer.Interval = 10000;
            _styleGuardTimer.Tick += delegate { GuardWindowStyles(); };
            _styleGuardTimer.Start();
        }

        private void ApplyClickThroughToHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            int style = NativeMethods.GetWindowLong(handle, GwlExStyle);
            int wanted = _state.ClickThrough ? (style | WsExTransparent) : (style & ~WsExTransparent);
            if (wanted == style) return;
            NativeMethods.SetWindowLong(handle, GwlExStyle, wanted);
            // 改完扩展样式要让系统重算一次命中测试，否则某些情况下位改对了、鼠标还是打进卡片。
            // 必须带 SWP_NOZORDER：不带的 SetWindowPos 会顺手改动窗口层级，把置顶状态一起搅乱。
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExTopMost = 0x8;
        private static readonly IntPtr HwndTopMost = (IntPtr)(-1);
        private static readonly IntPtr HwndNoTopMost = (IntPtr)(-2);
        private static readonly IntPtr HwndTop = IntPtr.Zero;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;

        private void UpdateTransparentHitBox()
        {
            if (!_state.TransparentBackground || !Visible || IsDisposed)
            {
                if (_transparentHitBox != null) _transparentHitBox.Hide();
                return;
            }

            if (_transparentHitBox == null || _transparentHitBox.IsDisposed)
            {
                _transparentHitBox = new TransparentHitBoxForm();
                _transparentHitBox.ContextMenuStrip = _menu;
                AttachDragHandlers(_transparentHitBox);
                _transparentHitBox.DoubleClick += delegate { EditQuotes(); };
                _transparentHitBox.UserBoundsChanged += delegate
                {
                    if (_transparentHitBox == null || _transparentHitBox.IsDisposed) return;
                    Bounds = _transparentHitBox.Bounds;
                    _state.X = Left;
                    _state.Y = Top;
                    _state.CardWidth = Width;
                    _state.CardHeight = Height;
                };
                // 这一层是启动后期才建的：建好、以及它自己重建句柄时都要把穿透样式补上，
                // 否则开着穿透启动，命中层仍然接鼠标，看着就像"重启后穿透失效"。
                _transparentHitBox.HandleCreated += delegate { ApplyClickThroughStyle(); };
                _transparentHitBox.UserResizeFinished += delegate { SaveCardBounds(); };
            }
            PositionTransparentHitBox();
        }

        private void PositionTransparentHitBox()
        {
            if (_transparentHitBox == null || _transparentHitBox.IsDisposed) return;
            if (!_state.TransparentBackground || !Visible)
            {
                _transparentHitBox.Hide();
                return;
            }

            // 先对齐下限再套边界：下限比卡片大时 SetBounds 会把透明层撑大，反过来把卡片顶回去
            if (_transparentHitBox.MinimumSize != MinimumSize) _transparentHitBox.MinimumSize = MinimumSize;
            _transparentHitBox.SetCardBounds(Bounds, Region);
            _transparentHitBox.TopMost = TopMost;
            if (!_transparentHitBox.Visible) _transparentHitBox.Show();
            if (IsHandleCreated && _transparentHitBox.IsHandleCreated)
            {
                const uint SWP_NOSIZE = 0x0001;
                const uint SWP_NOMOVE = 0x0002;
                const uint SWP_NOACTIVATE = 0x0010;
                NativeMethods.SetWindowPos(_transparentHitBox.Handle, Handle, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
            // 显示/重排之后同步一次穿透样式，保证启动时就生效
            ApplyClickThroughStyle();
        }

        private void LoadBackgroundImage()
        {
            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            if (string.IsNullOrEmpty(_state.BackgroundImagePath) ||
                !File.Exists(_state.BackgroundImagePath)) return;
            try
            {
                _backgroundImage = LoadImageUnlocked(_state.BackgroundImagePath);
            }
            catch
            {
                _state.BackgroundImagePath = "";
                _state.Save();
            }
        }

        private static Image LoadImageUnlocked(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image source = Image.FromStream(stream))
                return new Bitmap(source);
        }

        private void TryDeleteOwnedBackground(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                string dataDirectory = Path.GetFullPath(Path.GetDirectoryName(_repository.FilePath));
                string candidate = Path.GetFullPath(path);
                if (string.Equals(Path.GetDirectoryName(candidate), dataDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(candidate).StartsWith("background", StringComparison.OrdinalIgnoreCase))
                    File.Delete(candidate);
            }
            catch
            {
                // A locked old image can be left in place without affecting the card.
            }
        }

        private void BringCardToFront()
        {
            KeepOnScreen();
            Show();
            WindowState = FormWindowState.Normal;
            UpdateTransparentHitBox();
            Activate();
            BringToFront();
            PositionTransparentHitBox();
        }

        private void MoveCardToCenter()
        {
            // 不再有寻找模式和高亮闪烁，只把卡片挪到当前屏幕中央
            if (IsDisposed || Disposing) return;
            WindowState = FormWindowState.Normal;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2,
                area.Top + (area.Height - Height) / 2);
            Show();
            Activate();
            BringToFront();
            SaveCardBounds();
        }

        private void ShowFindCardHint()
        {
            MessageBox.Show(this, T(
                "如果以后突然找不到卡片：\n\n双击屏幕右下角的自言卡小图标，\n或右键托盘图标选择「把卡片移到屏幕中央」。\n\n卡片会直接挪到屏幕中间，不会再闪烁变色。",
                "If the card ever goes missing:\n\nDouble-click the MyQuoteCard tray icon,\nor right-click the tray icon and choose “Move card to screen center”.\n\nThe card simply moves to the middle of the screen; no color flashing."),
                T("请记住：怎样找到卡片", "Please remember: how to find your card"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RefreshForToday()
        {
            SetQuoteDisplay(_picker.EnsureToday());
            FitQuoteFont();
        }

        private void ChangeFontAdjustment(int difference)
        {
            if (_state.ManualFontSize > 0)
            {
                // 已经选了固定字号时，直接在这个磅值上加减
                _state.ManualFontSize = Math.Max(6, Math.Min(400, _state.ManualFontSize + difference));
            }
            else
            {
                // 手动微调范围 -24..80，可以一路放大
                _state.FontAdjustment = Math.Max(-24, Math.Min(80, _state.FontAdjustment + difference));
            }
            _state.Save();
            SyncFontSizeChecks();
            FitQuoteFont();
        }

        /// <summary>选定一个固定磅值：不再自动适应卡片，重启后仍然保持这个字号。</summary>
        private void ApplyFixedFontSize(int size)
        {
            _state.ManualFontSize = size;
            _state.AutoFitText = false;
            _state.FontAdjustment = 0;
            _state.Save();
            if (_autoFitItem != null) _autoFitItem.Checked = false;
            _quoteFontSize = -1.0f;
            SyncFontSizeChecks();
            FitQuoteFont();
        }

        private void SyncFontSizeChecks()
        {
            foreach (ToolStripMenuItem item in _fontSizeItems)
            {
                int size = 0;
                if (item.Name != null && item.Name.StartsWith("TextSize", StringComparison.Ordinal))
                    int.TryParse(item.Name.Substring("TextSize".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out size);
                item.Checked = _state.ManualFontSize > 0 && size == _state.ManualFontSize;
            }
        }

        /// <summary>
        /// 语录里的字面 \n 当作本条内部硬换行，其余照原样交给 Label。
        /// 只影响显示，不写回内容库。
        /// </summary>
        private void SetQuoteDisplay(string text)
        {
            if (text == null) text = "";
            _quoteLabel.Text = text.Replace("\\n", Environment.NewLine);
            UpdateMinimumWindowSize();
        }

        /// <summary>
        /// 窗口下限跟着“当前文本 + 最小字号 + 原版边距”走：
        /// 文字压到最小字号时，窗口正好包住它，不再留一圈缩不掉的空白。
        /// 换成长文本，下限自动抬高，保证不裁字。
        /// </summary>
        private void UpdateMinimumWindowSize()
        {
            if (_quoteLabel == null) return;
            string text = _quoteLabel.Text;
            if (string.IsNullOrEmpty(text)) text = "字";

            float minFont = _state.ManualFontSize > 0 ? (float)_state.ManualFontSize : 7.0f;
            int maxLineWidth = 0;
            int lineHeight = 0;
            using (Font probe = QuoteFont(minFont))
            {
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (string line in lines)
                {
                    string sample = line.Trim().Length == 0 ? " " : line;
                    Size measured = TextRenderer.MeasureText(sample, probe,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    if (measured.Width > maxLineWidth) maxLineWidth = measured.Width;
                    if (measured.Height > lineHeight) lineHeight = measured.Height;
                }
                int lineCount = Math.Max(1, lines.Length);
                // 文字宽高 + 内边距 + 极小防裁余量（自适应字号量高时会留 4px 边，这里补回去）
                int width = maxLineWidth + CardPadX * 2 + 6;
                int height = lineHeight * lineCount + CardPadY * 2 + 8;
                width = Math.Max(MinWindowWidth, Math.Min(1600, width));
                height = Math.Max(MinWindowHeight, Math.Min(1200, height));
                Size target = new Size(width, height);
                if (MinimumSize != target)
                {
                    MinimumSize = target;
                    if (_transparentHitBox != null && !_transparentHitBox.IsDisposed)
                        _transparentHitBox.MinimumSize = target;
                }
            }
        }

        private void LayoutCard()
        {
            if (_quoteLabel == null) return;
            _quoteLabel.SetBounds(CardPadX, CardPadY,
                Math.Max(1, ClientSize.Width - CardPadX * 2),
                Math.Max(1, ClientSize.Height - CardPadY * 2));
            _dateLabel.SetBounds(25, ClientSize.Height - 40, Math.Max(80, ClientSize.Width - 100), 24);
            _menuLabel.SetBounds(ClientSize.Width - 56, ClientSize.Height - 43, 34, 28);
        }

        private void FitQuoteFont()
        {
            if (_quoteLabel == null || _quoteLabel.Width <= 0 || _quoteLabel.Height <= 0) return;
            // 字号上限从 29 放开到 120：窗口拖大后文字可以继续放大
            float start = Math.Min(120.0f, Math.Max(20.0f, Width / 17.0f)) + _state.FontAdjustment;
            float chosen;
            if (_state.ManualFontSize > 0)
            {
                // 用户明确选定了固定字号：原样使用，不被卡片大小和自动适应改回去
                chosen = (float)_state.ManualFontSize;
            }
            else
            {
                chosen = Math.Max(7.0f, start);
                if (_state.AutoFitText && start > 7.0f)
                {
                    using (Graphics graphics = _quoteLabel.CreateGraphics())
                    {
                        // 二分查找能放进卡片的最大字号：字可以放得很大，同时拖动窗口时不会卡
                        float low = 7.0f;
                        float high = start;
                        chosen = low;
                        for (int step = 0; step < 14 && high - low > 0.4f; step++)
                        {
                            float probe = (low + high) / 2.0f;
                            using (Font candidate = QuoteFont(probe))
                            {
                                SizeF measured = graphics.MeasureString(_quoteLabel.Text, candidate,
                                    new SizeF(_quoteLabel.Width - 4, 4000));
                                if (measured.Height <= _quoteLabel.Height - 4)
                                {
                                    chosen = probe;
                                    low = probe;
                                }
                                else
                                {
                                    high = probe;
                                }
                            }
                        }
                    }
                }
            }
            if (Math.Abs(chosen - _quoteFontSize) < 0.1f) return;
            Font replacement = QuoteFont(chosen);
            _quoteLabel.Font = replacement;
            if (_quoteFont != null) _quoteFont.Dispose();
            _quoteFont = replacement;
            _quoteFontSize = chosen;
        }

        private void RestoreLocation()
        {
            if (_state.X != int.MinValue && _state.Y != int.MinValue)
                Location = new Point(_state.X, _state.Y);
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                // 多开时依次错开，避免几张卡片叠在同一处
                int offset = Math.Max(0, Program.InstanceIndex - 1);
                Location = new Point(area.Right - Width - 48 - 32 * offset, area.Top + 72 + 56 * offset);
            }
            KeepOnScreen();
        }

        private void KeepOnScreen()
        {
            Rectangle card = new Rectangle(Location, Size);
            Screen best = Screen.FromRectangle(card);
            Rectangle area = best.WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        private void AttachDragHandlers(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragOrigin = Cursor.Position;
                }
            };
            control.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging || e.Button != MouseButtons.Left) return;
                Point current = Cursor.Position;
                Left += current.X - _dragOrigin.X;
                Top += current.Y - _dragOrigin.Y;
                _dragOrigin = current;
            };
            control.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging) return;
                _dragging = false;
                KeepOnScreen();
                _state.X = Left;
                _state.Y = Top;
                _state.Save();
            };
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            IntPtr regionHandle = NativeMethods.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 19, 19);
            Region old = Region;
            Region = Region.FromHrgn(regionHandle);
            NativeMethods.DeleteObject(regionHandle);
            if (old != null) old.Dispose();
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Font QuoteFont(float size)
        {
            try
            {
                return new Font(_state.FontName, size, (FontStyle)_state.FontStyleValue, GraphicsUnit.Point);
            }
            catch
            {
                return UiFont(size, FontStyle.Regular);
            }
        }

        private static Color SafeColor(int argb, Color fallback)
        {
            try
            {
                Color color = Color.FromArgb(argb);
                return color.A == 0 ? fallback : color;
            }
            catch
            {
                return fallback;
            }
        }

        private static Rectangle CoverRectangle(Rectangle bounds, Size imageSize)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
                return bounds;
            double scale = Math.Max((double)bounds.Width / imageSize.Width,
                (double)bounds.Height / imageSize.Height);
            int width = Math.Max(1, (int)Math.Ceiling(imageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(imageSize.Height * scale));
            return new Rectangle(bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
        }

        private static Font UiFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }

        private static Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (Brush background = new SolidBrush(Color.FromArgb(8, 8, 8)))
                    graphics.FillEllipse(background, 1, 1, 30, 30);
                using (Font font = new Font("Georgia", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush foreground = new SolidBrush(Color.White))
                    graphics.DrawString("“", font, foreground, new PointF(8, 6));
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }
    }

    internal sealed class TransparentHitBoxForm : Form
    {
        private bool _settingCardBounds;
        public event EventHandler UserBoundsChanged;
        public event EventHandler UserResizeFinished;

        public TransparentHitBoxForm()
        {
            Text = "自言卡透明点击层";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.01;
            MinimizeBox = false;
            MaximizeBox = false;
            // 下限由卡片本体动态下发，别在这里写死，否则切透明时会把卡片顶回去
            MinimumSize = new Size(40, 30);
            MaximumSize = new Size(1600, 1200);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            base.WndProc(ref message);
            if (message.Msg != WM_NCHITTEST || (int)message.Result != HTCLIENT || WindowState != FormWindowState.Normal)
                return;
            Point screenPoint = ResizeSupport.ScreenPoint(message.LParam);
            int hit = ResizeSupport.HitTest(ClientSize, PointToClient(screenPoint));
            if (hit != HTCLIENT) message.Result = (IntPtr)hit;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            RaiseUserBoundsChanged();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RaiseUserBoundsChanged();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!_settingCardBounds && UserResizeFinished != null) UserResizeFinished(this, EventArgs.Empty);
        }

        private void RaiseUserBoundsChanged()
        {
            if (!_settingCardBounds && UserBoundsChanged != null) UserBoundsChanged(this, EventArgs.Empty);
        }

        public void SetCardBounds(Rectangle bounds, Region sourceRegion)
        {
            _settingCardBounds = true;
            try
            {
                Bounds = bounds;
                Region old = Region;
                Region = sourceRegion == null ? null : (Region)sourceRegion.Clone();
                if (old != null) old.Dispose();
            }
            finally
            {
                _settingCardBounds = false;
            }
        }
    }

    internal sealed class QuoteEditorForm : Form
    {
        private readonly QuoteRepository _repository;
        private readonly TextBox _editor;
        private readonly Label _countLabel;
        private readonly string _originalText;
        private readonly Panel _titleBar;

        public QuoteEditorForm(QuoteRepository repository)
        {
            _repository = repository;
            Text = T("编辑内容库", "Edit content library");
            StartPosition = FormStartPosition.CenterParent;
            // 系统标题栏在这台机器上不吃 DWM 深色属性（写进去读回来是 1，画面仍是白的直角条），
            // 干脆去掉边框，自己画一条和右键菜单同色系的标题条
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(660, 540);
            MinimumSize = new Size(520, 400);
            BackColor = Color.FromArgb(28, 28, 28);
            DoubleBuffered = true;
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;

            // 标题条单独用一个 Panel 画：窗体自己的 OnPaint 在这台机器上只拿到一小块脏区，画不满整条
            _titleBar = new Panel();
            _titleBar.Dock = DockStyle.Top;
            _titleBar.Height = TitleBarHeight;
            _titleBar.BackColor = Color.FromArgb(28, 28, 28);
            _titleBar.Paint += TitleBarPaint;
            _titleBar.MouseDown += TitleBarMouseDown;
            _titleBar.MouseMove += TitleBarMouseMove;
            _titleBar.MouseLeave += TitleBarMouseLeave;
            Controls.Add(_titleBar);

            Label heading = new Label();
            heading.Text = T("我的内容库", "My content library");
            heading.Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold, GraphicsUnit.Point);
            heading.ForeColor = Color.White;
            heading.AutoSize = true;
            heading.Location = new Point(24, 60);
            Controls.Add(heading);

            Label help = new Label();
            help.Text = T("每行放一条内容；同一条里想换行，写 \\n（反斜杠加 n）。空行和重复内容会忽略。",
                "One entry per line; write \\n inside an entry for a line break. Blank and duplicate lines are ignored.");
            help.ForeColor = Color.FromArgb(165, 165, 165);
            help.AutoSize = false;
            help.Location = new Point(27, 102);
            help.Size = new Size(ClientSize.Width - 54, 38);
            help.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(help);

            _editor = new TextBox();
            _editor.Multiline = true;
            _editor.AcceptsReturn = true;
            _editor.AcceptsTab = false;
            _editor.ScrollBars = ScrollBars.Vertical;
            _editor.BorderStyle = BorderStyle.FixedSingle;
            _editor.BackColor = Color.FromArgb(18, 18, 18);
            _editor.ForeColor = Color.White;
            _editor.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Point);
            _editor.Location = new Point(28, 132);
            _editor.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _editor.Size = new Size(ClientSize.Width - 56, ClientSize.Height - 204);
            string entrySeparator = repository.IsParagraphMode ?
                Environment.NewLine + Environment.NewLine : Environment.NewLine;
            _editor.Text = string.Join(entrySeparator, repository.Load().ToArray());
            _originalText = _editor.Text;
            _editor.TextChanged += delegate { UpdateCount(); };
            // 多行框右侧那条滚动条是系统画的，不套 DarkMode_Explorer 就是一条白杠
            _editor.HandleCreated += delegate
            {
                try { NativeMethods.SetWindowTheme(_editor.Handle, "DarkMode_Explorer", null); } catch { }
            };
            Controls.Add(_editor);

            _countLabel = new Label();
            _countLabel.ForeColor = Color.FromArgb(145, 145, 145);
            _countLabel.AutoSize = false;
            _countLabel.TextAlign = ContentAlignment.MiddleLeft;
            _countLabel.Location = new Point(28, ClientSize.Height - 57);
            _countLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _countLabel.Size = new Size(180, 30);
            Controls.Add(_countLabel);

            Button cancel = CreateButton(T("取消", "Cancel"), false);
            cancel.Location = new Point(ClientSize.Width - 220, ClientSize.Height - 60);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            Button save = CreateButton(T("保存", "Save"), true);
            save.Location = new Point(ClientSize.Width - 118, ClientSize.Height - 60);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { SaveQuotes(); };
            Controls.Add(save);
            AcceptButton = save;

            FormClosing += ConfirmDiscardIfNeeded;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S)
                {
                    e.SuppressKeyPress = true;
                    SaveQuotes();
                }
            };
            Shown += delegate { _editor.Focus(); _editor.SelectionStart = _editor.TextLength; };
            UpdateCount();
        }

        private const int TitleBarHeight = 40;
        private bool _closeHot;

        private Rectangle CloseButtonBounds
        {
            get { return new Rectangle(_titleBar.Width - 46, 0, 46, _titleBar.Height); }
        }

        private void TitleBarPaint(object sender, PaintEventArgs e)
        {
            // 底边一道分隔线，和右键菜单的分隔线同色
            using (SolidBrush lineBrush = new SolidBrush(Color.FromArgb(56, 56, 56)))
                e.Graphics.FillRectangle(lineBrush, 0, _titleBar.Height - 1, _titleBar.Width, 1);

            using (Font titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point))
                TextRenderer.DrawText(e.Graphics, Text, titleFont,
                    new Rectangle(16, 0, _titleBar.Width - 70, _titleBar.Height),
                    Color.FromArgb(235, 235, 235),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Rectangle close = CloseButtonBounds;
            if (_closeHot)
            {
                // Win11 关闭按钮悬停是这块红，跟系统一个色
                using (SolidBrush hot = new SolidBrush(Color.FromArgb(196, 43, 28)))
                    e.Graphics.FillRectangle(hot, close);
            }
            using (Font closeFont = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Point))
                TextRenderer.DrawText(e.Graphics, "✕", closeFont, close,
                    _closeHot ? Color.White : Color.FromArgb(220, 220, 220),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void TitleBarMouseMove(object sender, MouseEventArgs e)
        {
            bool hot = CloseButtonBounds.Contains(e.Location);
            if (hot == _closeHot) return;
            _closeHot = hot;
            _titleBar.Invalidate(CloseButtonBounds);
            _titleBar.Cursor = hot ? Cursors.Hand : Cursors.Default;
        }

        private void TitleBarMouseLeave(object sender, EventArgs e)
        {
            if (!_closeHot) return;
            _closeHot = false;
            _titleBar.Invalidate(CloseButtonBounds);
        }

        private void TitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (CloseButtonBounds.Contains(e.Location))
            {
                Close();
                return;
            }
            // 无边框窗口拖动：交回系统按 HTCAPTION 处理，自带跟手和边缘吸附
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            base.WndProc(ref message);
            // 去掉系统边框后要自己补边缘缩放热区，复用卡片那套 ResizeSupport
            if (message.Msg != WM_NCHITTEST || (int)message.Result != ResizeSupport.Client) return;
            if (WindowState != FormWindowState.Normal) return;
            Point screenPoint = ResizeSupport.ScreenPoint(message.LParam);
            int hit = ResizeSupport.HitTest(ClientSize, PointToClient(screenPoint));
            if (hit != ResizeSupport.Client) message.Result = (IntPtr)hit;
        }

        private static Button CreateButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(90, 36);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            // 两个按钮都走深色，只靠底色和描边亮度区分主次（主按钮亮一档），跟右键菜单同一套灰度
            Color normalBack = primary ? Color.FromArgb(45, 45, 45) : Color.FromArgb(28, 28, 28);
            Color hoverBack = primary ? Color.FromArgb(70, 70, 70) : Color.FromArgb(58, 58, 58);
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(130, 130, 130) : Color.FromArgb(62, 62, 62);
            button.BackColor = normalBack;
            button.ForeColor = Color.FromArgb(235, 235, 235);
            button.Cursor = Cursors.Hand;
            // FlatStyle.Flat 的悬停/按下仍走系统色，这里自己接管，保持和菜单 hover 一致
            button.MouseEnter += delegate { button.BackColor = hoverBack; };
            button.MouseLeave += delegate { button.BackColor = normalBack; };
            return button;
        }

        private void SaveQuotes()
        {
            try
            {
                _repository.SaveEditorLines(_editor.Lines);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, T("内容库还不能保存", "The content library could not be saved"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateCount()
        {
            if (_countLabel == null) return;
            List<string> values = new List<string>();
            foreach (string line in _editor.Lines)
            {
                string value = (line ?? "").Trim();
                if (value.Length > 0 && !values.Contains(value)) values.Add(value);
            }
            _countLabel.Text = T("共 " + values.Count.ToString(CultureInfo.InvariantCulture) + " 条",
                values.Count.ToString(CultureInfo.InvariantCulture) + (values.Count == 1 ? " entry" : " entries"));
        }

        private void ConfirmDiscardIfNeeded(object sender, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK || _editor.Text == _originalText) return;
            DialogResult answer = MessageBox.Show(
                T("内容库有尚未保存的修改，要直接放弃吗？",
                    "The content library has unsaved changes. Discard them?"),
                T("编辑内容库", "Edit content library"), MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) e.Cancel = true;
        }

        private string T(string chinese, string english)
        {
            return _repository.IsEnglish ? english : chinese;
        }
    }

    /// <summary>深色圆角菜单渲染：文字垂直居中、选中项圆角高亮、分隔线收进两侧。</summary>
    internal sealed class RoundedMenuRenderer : ToolStripRenderer
    {
        private static readonly Color MenuBack = Color.FromArgb(28, 28, 28);
        private static readonly Color TextColor = Color.FromArgb(235, 235, 235);
        private static readonly Color DisabledText = Color.FromArgb(120, 120, 120);
        private static readonly Color HighlightColor = Color.FromArgb(58, 58, 58);
        private static readonly Color PressedColor = Color.FromArgb(70, 70, 70);
        private static readonly Color LineColor = Color.FromArgb(56, 56, 56);
        private static readonly Color BorderColor = Color.FromArgb(62, 62, 62);
        private static readonly Color ArrowColor = Color.FromArgb(200, 200, 200);
        private static readonly Color CheckColor = Color.FromArgb(230, 230, 230);

        private readonly Font _checkFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        private ToolStrip _marginStrip;
        private int _marginWidth;

        private static GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            if (diameter > bounds.Width) diameter = bounds.Width;
            if (diameter > bounds.Height) diameter = bounds.Height;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 勾选区宽度。ShowCheckMargin 打开后系统会把这个宽度并进菜单的左内边距，
        /// 所以直接读 Padding.Left 就是真实值（自带 DPI 缩放），菜单项 Bounds 也从 0 起算。
        /// </summary>
        private int CheckMarginWidth(ToolStrip strip)
        {
            if (strip == null) return 0;
            if (_marginStrip == strip) return _marginWidth;

            int width = 0;
            ToolStripDropDownMenu menu = strip as ToolStripDropDownMenu;
            if (menu != null && menu.ShowCheckMargin)
            {
                width = menu.Padding.Left;
                if (width < 18) width = 18;
            }

            _marginStrip = strip;
            _marginWidth = width;
            return width;
        }

        private static void FillRow(Graphics graphics, ToolStripItem item, int width)
        {
            Rectangle row = new Rectangle(0, item.Bounds.Top, width, item.Height);
            using (SolidBrush brush = new SolidBrush(MenuBack))
                graphics.FillRectangle(brush, row);
        }

        private static void DrawHighlight(Graphics graphics, ToolStripItem item, int width, bool pressed)
        {
            Rectangle bounds = new Rectangle(3, item.Bounds.Top + 1, width - 8, item.Height - 2);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using (GraphicsPath path = RoundRect(bounds, 5))
            using (SolidBrush brush = new SolidBrush(pressed ? PressedColor : HighlightColor))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillPath(brush, path);
            }
        }

        /// <summary>整项内容：勾选标记 + 文字 + 子菜单箭头。坐标一律按 ToolStrip 客户区计算。</summary>
        private void DrawItem(Graphics graphics, ToolStripItem item, int width, int checkMargin)
        {
            if (item is ToolStripSeparator) return;

            ToolStripMenuItem menuItem = item as ToolStripMenuItem;

            // 勾选标记：只在开了 ShowCheckMargin 的菜单里画，位置落在左侧勾选区正中
            if (menuItem != null && menuItem.Checked && checkMargin > 0)
            {
                Rectangle box = new Rectangle(item.Bounds.Left + (checkMargin - 16) / 2,
                    item.Bounds.Top + (item.Height - 16) / 2, 16, 16);
                TextRenderer.DrawText(graphics, "\u2713", _checkFont, box, CheckColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix);
            }

            // 文字：从勾选区右侧起，按整行高度垂直居中，右侧留出箭头的位置
            if (!string.IsNullOrEmpty(item.Text))
            {
                int textLeft = item.Bounds.Left + (checkMargin > 0 ? checkMargin : 12) + 4;
                int textWidth = width - textLeft - 20;
                if (textWidth < 8) textWidth = 8;
                Rectangle rect = new Rectangle(textLeft, item.Bounds.Top, textWidth, item.Height);
                TextRenderer.DrawText(graphics, item.Text, item.Font, rect,
                    item.Enabled ? TextColor : DisabledText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            // 子菜单箭头：贴着菜单可视区右边缘。item.Bounds 比菜单本身宽，不能拿它算右边界
            if (menuItem != null && menuItem.HasDropDownItems)
            {
                int centerX = Math.Max(item.Bounds.Left + 30, width - 12);
                int centerY = item.Bounds.Top + item.Height / 2;
                Point[] points = new Point[]
                {
                    new Point(centerX - 4, centerY - 4),
                    new Point(centerX + 4, centerY - 4),
                    new Point(centerX, centerY + 3)
                };
                using (SolidBrush brush = new SolidBrush(item.Enabled ? ArrowColor : DisabledText))
                    graphics.FillPolygon(brush, points);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics graphics = e.Graphics;
            int width = e.ToolStrip.Width;
            int checkMargin = CheckMarginWidth(e.ToolStrip);

            using (SolidBrush brush = new SolidBrush(MenuBack))
                graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));

            // 关键：ToolStrip 弹出时只把"脏"的那一项的笔迹提交到窗口表面，逐项 OnRenderItemText
            // 除第一项之外都会被丢掉。所以整条菜单的底色、分隔线、选中块、勾选、箭头、文字
            // 全部在这一遍里一次画完（v31 在启动器菜单上验证过的同一个修法）。
            foreach (ToolStripItem item in e.ToolStrip.Items)
            {
                if (item is ToolStripSeparator)
                {
                    int y = item.Bounds.Top + item.Height / 2;
                    using (Pen pen = new Pen(LineColor))
                        graphics.DrawLine(pen, 12, y, width - 12, y);
                    continue;
                }
                if (item.Selected && item.Enabled) DrawHighlight(graphics, item, width, item.Pressed);
                DrawItem(graphics, item, width, checkMargin);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (GraphicsPath path = RoundRect(bounds, 8))
            using (Pen pen = new Pen(BorderColor))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 默认会在左侧画一条竖线，深色下很难看，直接不画
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // hover / 按下只重绘这一项，所以底色、高亮和内容要在这里再画一次
            Graphics graphics = e.Graphics;
            int width = e.ToolStrip == null ? e.Item.Width : e.ToolStrip.Width;
            int checkMargin = CheckMarginWidth(e.ToolStrip);

            FillRow(graphics, e.Item, width);
            if (e.Item.Selected && e.Item.Enabled) DrawHighlight(graphics, e.Item, width, e.Item.Pressed);
            DrawItem(graphics, e.Item, width, checkMargin);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // 文字已在背景遍和 hover 遍画过，这里不再重复画第二遍
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // 勾选标记由 DrawItem 统一画
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // 分隔线由背景遍统一画（分隔线本身不会被 hover 单独重绘）
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            // 箭头由 DrawItem 统一画
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(28, 28, 28); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(58, 58, 58); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(80, 80, 80); } }
        public override Color MenuBorder { get { return Color.FromArgb(65, 65, 65); } }
        public override Color SeparatorDark { get { return Color.FromArgb(50, 50, 50); } }
        public override Color SeparatorLight { get { return Color.FromArgb(50, 50, 50); } }
        public override Color CheckBackground { get { return Color.FromArgb(70, 70, 70); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(85, 85, 85); } }
    }

    internal static class ResizeSupport
    {
        private const int Grip = 12;
        public const int Client = 1;
        public const int Left = 10;
        public const int Right = 11;
        public const int Top = 12;
        public const int TopLeft = 13;
        public const int TopRight = 14;
        public const int Bottom = 15;
        public const int BottomLeft = 16;
        public const int BottomRight = 17;

        public static Point ScreenPoint(IntPtr packedPoint)
        {
            long value = packedPoint.ToInt64();
            int x = unchecked((short)(value & 0xFFFF));
            int y = unchecked((short)((value >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        public static int HitTest(Size size, Point point)
        {
            bool left = point.X < Grip;
            bool right = point.X >= size.Width - Grip;
            bool top = point.Y < Grip;
            bool bottom = point.Y >= size.Height - Grip;
            if (left && top) return TopLeft;
            if (right && top) return TopRight;
            if (left && bottom) return BottomLeft;
            if (right && bottom) return BottomRight;
            if (left) return Left;
            if (right) return Right;
            if (top) return Top;
            if (bottom) return Bottom;
            return Client;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        public static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
