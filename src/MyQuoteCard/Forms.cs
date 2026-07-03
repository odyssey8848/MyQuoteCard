using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MyQuoteCard
{
    internal sealed class QuoteCardForm : Form
    {
        internal const int LocateMessage = 0x8514;
        internal const int LocateResponse = 0x5143;
        private const int LocateHotKeyId = 0x5143;
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
        private readonly NotifyIcon _trayIcon;
        private readonly Icon _trayImage;
        private readonly Timer _dayTimer;
        private readonly Timer _locateTimer;
        private Font _quoteFont;
        private Image _backgroundImage;
        private float _quoteFontSize = -1.0f;
        private Point _dragOrigin;
        private bool _dragging;
        private bool _initialized;
        private bool _resourcesDisposed;
        private bool _locatingCard;
        private bool _hotKeyRegistered;
        private TransparentHitBoxForm _transparentHitBox;

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
            Size = new Size(state.CardWidth, state.CardHeight);
            MinimumSize = new Size(340, 140);
            MaximumSize = new Size(700, 320);
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
            _trayIcon.DoubleClick += delegate { LocateCard(); };

            ApplyUiLanguage();

            _dayTimer = new Timer();
            _dayTimer.Interval = 60000;
            _dayTimer.Tick += delegate { RefreshForToday(); };

            _locateTimer = new Timer();
            _locateTimer.Interval = 6000;
            _locateTimer.Tick += delegate { EndLocateCard(); };

            ApplyAppearance(true);
            LayoutCard();
            RestoreLocation();
            RefreshForToday();
            _initialized = true;
            _dayTimer.Start();
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

        internal bool LocateModeActive
        {
            get
            {
                return _locatingCard && TopMost &&
                    (_transparentHitBox == null || !_transparentHitBox.Visible);
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
            if (_state.TransparentBackground && !_locatingCard) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color borderColor = _locatingCard ? Color.FromArgb(255, 112, 0) : Color.FromArgb(42, 42, 42);
            float borderWidth = _locatingCard ? 5.0f : 1.0f;
            using (Pen pen = new Pen(borderColor, borderWidth))
            {
                Rectangle rect = ClientRectangle;
                int inset = _locatingCard ? 3 : 0;
                rect.Inflate(-inset, -inset);
                rect.Width -= 1;
                rect.Height -= 1;
                using (GraphicsPath path = RoundedRectangle(rect, 18))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_locatingCard)
            {
                e.Graphics.Clear(Color.FromArgb(255, 246, 150));
                return;
            }
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
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveCardBounds();
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_HOTKEY = 0x0312;
            const int HTCLIENT = 1;
            if (message.Msg == LocateMessage)
            {
                LocateCard();
                message.Result = (IntPtr)LocateResponse;
                return;
            }
            if (message.Msg == WM_HOTKEY && message.WParam.ToInt32() == LocateHotKeyId)
            {
                LocateCard();
                message.Result = IntPtr.Zero;
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
            if (_trayIcon != null && _trayIcon.Visible && !_state.HintShown)
            {
                _state.HintShown = true;
                _state.Save();
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && !Disposing) ShowFindCardHint();
                });
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const uint MOD_ALT = 0x0001;
            const uint MOD_CONTROL = 0x0002;
            _hotKeyRegistered = NativeMethods.RegisterHotKey(Handle, LocateHotKeyId,
                MOD_ALT | MOD_CONTROL, (uint)Keys.Q);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_hotKeyRegistered && IsHandleCreated)
                NativeMethods.UnregisterHotKey(Handle, LocateHotKeyId);
            _hotKeyRegistered = false;
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
            if (_locateTimer != null)
            {
                _locateTimer.Stop();
                _locateTimer.Dispose();
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
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            menu.Padding = new Padding(5);

            ToolStripMenuItem editItem = CreateMenuItem("EditLibrary");
            editItem.Click += delegate { EditQuotes(); };
            menu.Items.Add(editItem);

            ToolStripMenuItem anotherItem = CreateMenuItem("SwitchQuote");
            anotherItem.Click += delegate
            {
                _quoteLabel.Text = _picker.PickAnother();
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
            transparentItem.Click += delegate
            {
                _state.TransparentBackground = transparentItem.Checked;
                _state.Save();
                ApplyAppearance(false);
                RecreateHandle();
                UpdateTransparentHitBox();
            };
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
            smallest.Click += delegate { _state.FontAdjustment = -24; _state.Save(); FitQuoteFont(); };
            ToolStripMenuItem autoFitItem = CreateMenuItem("AutoFitText");
            autoFitItem.CheckOnClick = true;
            autoFitItem.Checked = _state.AutoFitText;
            autoFitItem.Click += delegate
            {
                _state.AutoFitText = autoFitItem.Checked;
                _state.Save();
                _quoteFontSize = -1.0f;
                FitQuoteFont();
            };
            ToolStripMenuItem reset = CreateMenuItem("ResetTextSize");
            reset.Click += delegate { _state.FontAdjustment = 0; _state.Save(); FitQuoteFont(); };
            ToolStripMenuItem textColorItem = CreateMenuItem("TextColor");
            textColorItem.Click += delegate { ChooseTextColor(); };
            ToolStripMenuItem fontItem = CreateMenuItem("ChooseFont");
            fontItem.Click += delegate { ChooseQuoteFont(); };
            textSettingsMenu.DropDownItems.Add(larger);
            textSettingsMenu.DropDownItems.Add(smaller);
            textSettingsMenu.DropDownItems.Add(smallest);
            textSettingsMenu.DropDownItems.Add(reset);
            textSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            textSettingsMenu.DropDownItems.Add(autoFitItem);
            textSettingsMenu.DropDownItems.Add(textColorItem);
            textSettingsMenu.DropDownItems.Add(fontItem);
            menu.Items.Add(textSettingsMenu);

            ToolStripMenuItem localStartupItem = CreateMenuItem("Startup");
            localStartupItem.CheckOnClick = true;
            localStartupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(localStartupItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem showItem = CreateMenuItem("BringCardBack");
            showItem.Click += delegate { LocateCard(); };
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
                    "• 拖动卡片，可以改变位置\n• 拖动卡片的边缘或四角，可以自由调整大小\n• 双击文字，可以编辑内容库\n• “切换到另一条内容”会立即更换当前显示\n• “卡片设置”管理大小、背景和卡片层级\n• “置顶”让卡片盖在其他窗口上；“置底”允许其他窗口盖住卡片\n• “文字设置”管理字号、颜色和字体\n• 找不到卡片时，按 Ctrl+Alt+Q、双击右下角小图标，或再次打开软件\n• 寻找时，卡片会移到屏幕中央并以醒目颜色显示几秒\n• 勾选“开机自启动”表示已成功写入 Windows 启动项\n• 透明背景模式下，整张卡片仍可右键、拖动和调整大小\n• 所有内容都只保存在你的电脑上",
                    "• Drag the card to move it\n• Drag any edge or corner to resize the card freely\n• Double-click the text to edit your library\n• “Switch to another entry” changes the current text immediately\n• “Card settings” controls size, background, and card layer\n• “Top” keeps the card above other windows; “Bottom” lets other windows cover it\n• “Text settings” controls size, color, and font\n• If the card is lost, press Ctrl+Alt+Q, double-click the tray icon, or open the app again\n• The card moves to the center and uses a bright highlight for a few seconds\n• A check beside “Start with Windows” means the startup entry is active\n• In transparent mode, you can still right-click, drag, and resize the whole card\n• All content stays on your computer"),
                    T("自言卡 · 使用方法", "MyQuoteCard · Help"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(helpItem);

            ToolStripMenuItem exitItem = CreateMenuItem("Exit");
            exitItem.Click += delegate { Close(); };
            menu.Items.Add(exitItem);

            menu.Opening += delegate
            {
                try { localStartupItem.Checked = StartupManager.IsEnabled(); }
                catch { localStartupItem.Checked = false; }
                localTopMostItem.Checked = TopMost;
                bottomItem.Checked = !TopMost;
                autoFitItem.Checked = _state.AutoFitText;
                transparentItem.Checked = _state.TransparentBackground;
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
                    case "BackgroundImage": menuItem.Text = T("选择背景图片…", "Choose background image…"); break;
                    case "ClearBackgroundImage": menuItem.Text = T("清除背景图片", "Remove background image"); break;
                    case "CardLayer": menuItem.Text = T("卡片层级", "Card layer"); break;
                    case "LayerTop": menuItem.Text = T("置顶（显示在其他窗口前面）", "Top (above other windows)"); break;
                    case "LayerBottom": menuItem.Text = T("置底（允许其他窗口盖住卡片）", "Bottom (other windows can cover the card)"); break;
                    case "ResetAppearance": menuItem.Text = T("恢复默认样式", "Reset appearance"); break;
                    case "TextSettings": menuItem.Text = T("文字设置", "Text settings"); break;
                    case "TextLarger": menuItem.Text = T("大一点", "Larger"); break;
                    case "TextSmaller": menuItem.Text = T("小一点", "Smaller"); break;
                    case "TextSmallest": menuItem.Text = T("设为最小", "Smallest"); break;
                    case "AutoFitText": menuItem.Text = T("文字自动适应卡片", "Fit text to card automatically"); break;
                    case "ResetTextSize": menuItem.Text = T("恢复默认字号", "Reset text size"); break;
                    case "TextColor": menuItem.Text = T("文字颜色…", "Text color…"); break;
                    case "ChooseFont": menuItem.Text = T("选择字体…", "Choose font…"); break;
                    case "Startup": menuItem.Text = menuItem.Checked ?
                        T("开机自启动（已开启）", "Start with Windows (on)") :
                        T("开机自启动（未开启）", "Start with Windows (off)"); break;
                    case "BringCardBack": menuItem.Text = _hotKeyRegistered ?
                        T("🔍 寻找卡片（Ctrl+Alt+Q）", "🔍 Find card (Ctrl+Alt+Q)") :
                        T("🔍 寻找卡片", "🔍 Find card"); break;
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
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.DropDownItems.Count > 0)
                {
                    menuItem.DropDown.BackColor = Color.FromArgb(28, 28, 28);
                    menuItem.DropDown.ForeColor = Color.White;
                    menuItem.DropDown.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
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
            _quoteLabel.Text = _picker.RefreshAfterEdit();
            _state.Save();
            ApplyUiLanguage();
            FitQuoteFont();
        }

        private void ApplyUiLanguage()
        {
            Text = T("自言卡", "MyQuoteCard");
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
                StartupManager.SetEnabled(requested);
                bool actual = StartupManager.IsEnabled();
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
                try { _startupItem.Checked = StartupManager.IsEnabled(); }
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
                    _quoteLabel.Text = _picker.RefreshAfterEdit();
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
            Invalidate(true);
        }

        private void UpdateTransparentHitBox()
        {
            if (_locatingCard || !_state.TransparentBackground || !Visible || IsDisposed)
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
                _transparentHitBox.UserResizeFinished += delegate { SaveCardBounds(); };
            }
            PositionTransparentHitBox();
        }

        private void PositionTransparentHitBox()
        {
            if (_transparentHitBox == null || _transparentHitBox.IsDisposed) return;
            if (_locatingCard || !_state.TransparentBackground || !Visible)
            {
                _transparentHitBox.Hide();
                return;
            }

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

        private void LocateCard()
        {
            if (IsDisposed || Disposing) return;
            _locateTimer.Stop();
            _locatingCard = true;
            WindowState = FormWindowState.Normal;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2,
                area.Top + (area.Height - Height) / 2);
            Show();
            TopMost = true;
            TransparencyKey = Color.Empty;
            Opacity = 1.0;
            _quoteLabel.ForeColor = Color.Black;
            _menuLabel.ForeColor = Color.FromArgb(180, 0, 0, 0);
            if (_transparentHitBox != null) _transparentHitBox.Hide();
            Invalidate(true);
            Activate();
            BringToFront();
            SaveCardBounds();
            _locateTimer.Start();
        }

        private void EndLocateCard()
        {
            _locateTimer.Stop();
            if (!_locatingCard || IsDisposed || Disposing) return;
            _locatingCard = false;
            TopMost = _state.TopMost;
            ApplyAppearance(false);
            UpdateTransparentHitBox();
            Invalidate(true);
        }

        private void ShowFindCardHint()
        {
            MessageBox.Show(this, T(
                "如果以后突然找不到卡片：\n\n按下  Ctrl + Alt + Q\n\n也可以双击屏幕右下角的自言卡小图标，\n或者再次打开自言卡。\n\n卡片会移动到屏幕中央，并醒目显示 6 秒。",
                "If the card ever goes missing:\n\nPress  Ctrl + Alt + Q\n\nYou can also double-click the MyQuoteCard tray icon,\nor open MyQuoteCard again.\n\nThe card will move to the center and highlight itself for 6 seconds."),
                T("请记住：怎样寻找卡片", "Please remember: how to find your card"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RefreshForToday()
        {
            _quoteLabel.Text = _picker.EnsureToday();
            FitQuoteFont();
        }

        private void ChangeFontAdjustment(int difference)
        {
            _state.FontAdjustment = Math.Max(-24, Math.Min(8, _state.FontAdjustment + difference));
            _state.Save();
            FitQuoteFont();
        }

        private void LayoutCard()
        {
            if (_quoteLabel == null) return;
            _quoteLabel.SetBounds(32, 20, Math.Max(10, ClientSize.Width - 64), Math.Max(10, ClientSize.Height - 40));
            _dateLabel.SetBounds(25, ClientSize.Height - 40, Math.Max(80, ClientSize.Width - 100), 24);
            _menuLabel.SetBounds(ClientSize.Width - 56, ClientSize.Height - 43, 34, 28);
        }

        private void FitQuoteFont()
        {
            if (_quoteLabel == null || _quoteLabel.Width <= 0 || _quoteLabel.Height <= 0) return;
            float start = Math.Min(29.0f, Math.Max(20.0f, Width / 17.0f)) + _state.FontAdjustment;
            float chosen = Math.Max(7.0f, start);
            if (_state.AutoFitText)
            {
                using (Graphics graphics = _quoteLabel.CreateGraphics())
                {
                    for (float size = start; size >= 7.0f; size -= 1.0f)
                    {
                        using (Font candidate = QuoteFont(size))
                        {
                            SizeF measured = graphics.MeasureString(_quoteLabel.Text, candidate,
                                new SizeF(_quoteLabel.Width - 4, 2000));
                            if (measured.Height <= _quoteLabel.Height - 4)
                            {
                                chosen = size;
                                break;
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
                Location = new Point(area.Right - Width - 48, area.Top + 72);
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
            MinimumSize = new Size(340, 140);
            MaximumSize = new Size(700, 320);
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

        public QuoteEditorForm(QuoteRepository repository)
        {
            _repository = repository;
            Text = T("编辑内容库", "Edit content library");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(660, 540);
            MinimumSize = new Size(520, 400);
            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;

            Label heading = new Label();
            heading.Text = T("我的内容库", "My content library");
            heading.Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold, GraphicsUnit.Point);
            heading.ForeColor = Color.White;
            heading.AutoSize = true;
            heading.Location = new Point(24, 20);
            Controls.Add(heading);

            Label help = new Label();
            help.Text = T("每行放一条内容，长段文字也可以。空行和重复内容会忽略；每天随机选一条。",
                "Put one entry on each line; long text is fine.\nBlank and duplicate lines are ignored; one is chosen each day.");
            help.ForeColor = Color.FromArgb(165, 165, 165);
            help.AutoSize = false;
            help.Location = new Point(27, 62);
            help.Size = new Size(ClientSize.Width - 54, 38);
            help.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(help);

            _editor = new TextBox();
            _editor.Multiline = true;
            _editor.AcceptsReturn = true;
            _editor.AcceptsTab = false;
            _editor.ScrollBars = ScrollBars.Vertical;
            _editor.BorderStyle = BorderStyle.FixedSingle;
            _editor.BackColor = Color.FromArgb(8, 8, 8);
            _editor.ForeColor = Color.White;
            _editor.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Point);
            _editor.Location = new Point(28, 92);
            _editor.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _editor.Size = new Size(ClientSize.Width - 56, ClientSize.Height - 164);
            string entrySeparator = repository.IsParagraphMode ?
                Environment.NewLine + Environment.NewLine : Environment.NewLine;
            _editor.Text = string.Join(entrySeparator, repository.Load().ToArray());
            _originalText = _editor.Text;
            _editor.TextChanged += delegate { UpdateCount(); };
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

        private static Button CreateButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(90, 36);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Color.White : Color.FromArgb(90, 90, 90);
            button.BackColor = primary ? Color.White : Color.FromArgb(28, 28, 28);
            button.ForeColor = primary ? Color.Black : Color.White;
            button.Cursor = Cursors.Hand;
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
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
