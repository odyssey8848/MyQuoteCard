using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MyQuoteCardLauncher
{
    internal static class Program
    {
        private const string SingleInstanceName = "Local\\MyQuoteCardLauncher.SingleInstance";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 启动器只跑一个：已经有实例在跑，就把那个窗口叫到前面，自己退出
            bool createdNew = true;
            System.Threading.Mutex mutex = null;
            try
            {
                mutex = new System.Threading.Mutex(true, SingleInstanceName, out createdNew);
            }
            catch
            {
                createdNew = true;
            }
            if (!createdNew)
            {
                ActivateExistingLauncher();
                return;
            }
            try
            {
                Application.Run(new LauncherForm());
            }
            finally
            {
                if (mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                    mutex.Dispose();
                }
            }
        }

        private delegate bool EnumWindowProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(int attachId, int attachToId, bool attach);
        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        private static int _foregroundProcessId;
        private static int _targetProcessId;
        private static IntPtr _launcherWindow;
        private static int _launcherProcessId;
        private static EnumWindowProc _enumCallback;

        private static bool FindLauncherWindow(IntPtr hWnd, IntPtr lParam)
        {
            int processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == _launcherProcessId && IsWindowVisible(hWnd))
            {
                _launcherWindow = hWnd;
                return false;
            }
            return true;
        }

        /// <summary>找到已经在跑的启动器窗口，还原并置前。</summary>
        private static void ActivateExistingLauncher()
        {
            int selfId = Process.GetCurrentProcess().Id;
            string name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (process.Id == selfId) continue;
                _launcherProcessId = process.Id;
                _launcherWindow = IntPtr.Zero;
                if (_enumCallback == null) _enumCallback = new EnumWindowProc(FindLauncherWindow);
                EnumWindows(_enumCallback, IntPtr.Zero);
                if (_launcherWindow == IntPtr.Zero) continue;

                ShowWindow(_launcherWindow, 9); // SW_RESTORE
                // 后台进程直接抢焦会被系统拦，临时挂到前台线程上再置前
                int foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _foregroundProcessId);
                int selfThread = GetCurrentThreadId();
                int targetThread = GetWindowThreadProcessId(_launcherWindow, out _targetProcessId);
                bool attachedSelf = selfThread != foregroundThread && AttachThreadInput(selfThread, foregroundThread, true);
                bool attachedTarget = targetThread != foregroundThread && AttachThreadInput(targetThread, foregroundThread, true);
                try
                {
                    SetForegroundWindow(_launcherWindow);
                }
                finally
                {
                    if (attachedTarget) AttachThreadInput(targetThread, foregroundThread, false);
                    if (attachedSelf) AttachThreadInput(selfThread, foregroundThread, false);
                }
                return;
            }
        }
    }

    internal sealed class CardInstance
    {
        public int Index;
        public string Directory;
        public string Preview;
        public bool Running;
        public bool Transparent;
        public bool TopMost;
        public bool ClickThrough;
        public bool Startup;
        public int ProcessId;
    }

    internal sealed class LauncherForm : Form
    {
        private const int MaxInstances = 9;
        // 与卡片窗体约定的字号调整消息，wParam = 档位差
        private const int FontAdjustMessage = 0x8515;
        private const int FontAdjustResponse = 0x5144;
        private const int CommandMessage = 0x8516;
        private const int CommandResponse = 0x5145;
        // 14 号指令：按启动器下发的期望值把卡片的穿透/置顶拉回一致（高位带期望值）
        private const int CommandAlignStyle = 14;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExTopMost = 0x8;
        // 编号 / 状态 / 当前内容 / 透明 / 顶层 / 穿透 / 自启
        private static readonly int[] DefaultColumnWidths = new int[] { 50, 65, 300, 65, 65, 65, 65 };
        private static readonly string[] ColumnTitles =
            new string[] { "编号", "状态", "当前内容", "透明", "顶层", "穿透", "自启" };
        private const string DataFolderName = "MyQuoteCard";

        private readonly ListView _list;
        private readonly Button _openButton;
        private readonly Button _createButton;
        private readonly Button _deleteButton;
        private readonly Button _closeAllButton;
        private readonly Button _refreshButton;
        private readonly Button _fontLargerButton;
        private readonly Button _fontSmallerButton;
        private readonly Button _fontMuchLargerButton;
        private readonly Button _fontMuchSmallerButton;
        private readonly Button _textColorButton;
        private readonly Button _fontFaceButton;
        private readonly Button _backgroundColorButton;
        private readonly Button _transparentButton;
        private readonly Button _layerButton;
        private Label[] _headerLabels;
        private Panel[] _headerDividers;
        private Panel _headerBar;
        private int _dragColumn = -1;
        private int _dragStartX;
        private int _dragStartWidth;
        private DateTime _dragEndTime = DateTime.MinValue;
        private int _hoverRowIndex = -1;
        private int _hoverColumn = -1;
        private int _sortColumn = 0;
        private bool _sortAscending = true;
        private readonly Dictionary<int, int> _running = new Dictionary<int, int>();
        private readonly Label _statusLabel;
        private readonly Timer _timer;
        private ContextMenuStrip _rowMenu;

        public LauncherForm()
        {
            Text = "自言卡 · 启动器";
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 514);
            MinimumSize = new Size(600, 434);
            ShowIcon = true;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _statusLabel = new Label();
            _statusLabel.AutoSize = false;
            _statusLabel.SetBounds(0, 0, ClientSize.Width, 44);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusLabel.Padding = new Padding(12, 0, 12, 0);
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = Color.FromArgb(238, 238, 238);
            _statusLabel.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            Controls.Add(_statusLabel);

            Panel buttonBar = new Panel();
            buttonBar.SetBounds(0, ClientSize.Height - 52, ClientSize.Width, 52);
            buttonBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            buttonBar.BackColor = Color.FromArgb(36, 36, 36);
            Controls.Add(buttonBar);

            Panel fontBar = new Panel();
            fontBar.SetBounds(0, ClientSize.Height - 96, ClientSize.Width, 44);
            fontBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            fontBar.BackColor = Color.FromArgb(31, 31, 31);
            Controls.Add(fontBar);

            Label fontCaption = new Label();
            fontCaption.AutoSize = false;
            fontCaption.SetBounds(0, 0, 60, 44);
            fontCaption.Text = "字号";
            fontCaption.TextAlign = ContentAlignment.MiddleLeft;
            fontCaption.Padding = new Padding(12, 0, 0, 0);
            fontCaption.ForeColor = Color.FromArgb(190, 190, 190);
            fontBar.Controls.Add(fontCaption);

            Panel styleBar = new Panel();
            styleBar.SetBounds(0, ClientSize.Height - 140, ClientSize.Width, 44);
            styleBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            styleBar.BackColor = Color.FromArgb(31, 31, 31);
            Controls.Add(styleBar);

            Label styleCaption = new Label();
            styleCaption.AutoSize = false;
            styleCaption.SetBounds(0, 0, 60, 44);
            styleCaption.Text = "样式";
            styleCaption.TextAlign = ContentAlignment.MiddleLeft;
            styleCaption.Padding = new Padding(12, 0, 0, 0);
            styleCaption.ForeColor = Color.FromArgb(190, 190, 190);
            styleBar.Controls.Add(styleCaption);

            _textColorButton = MakeButton("字体颜色", 78);
            _fontFaceButton = MakeButton("字体", 56);
            _backgroundColorButton = MakeButton("背景色", 70);
            _transparentButton = MakeButton("背景透明", 84);
            _layerButton = MakeButton("切换顶层", 84);

            // 按钮宽度按文字实测定，保证「字体颜色」四个字完整显示，不再被截成「字体颜...」
            foreach (Button button in new Button[] { _textColorButton, _fontFaceButton, _backgroundColorButton,
                                                      _transparentButton, _layerButton })
            {
                int need = TextRenderer.MeasureText(button.Text, button.Font).Width + 20;
                if (button.Width < need) button.Width = need;
            }

            // 不用二级菜单了：点一下直接在置顶 / 不置顶之间切
            _layerButton.Click += delegate { SendCommand(11); };

            // 放大 / 缩小换成 + / −，一眼能看懂，按钮宽度不动（96），点击区域不缩水
            _fontLargerButton = MakeButton("字体 +", 96);
            _fontSmallerButton = MakeButton("字体 −", 96);
            _fontMuchLargerButton = MakeButton("明显 +", 96);
            _fontMuchSmallerButton = MakeButton("明显 −", 96);

            Button[] styleButtons = new Button[] { _textColorButton, _fontFaceButton, _backgroundColorButton,
                                                   _transparentButton, _layerButton };
            Button[] fontButtons = new Button[] { _fontLargerButton, _fontSmallerButton,
                                                  _fontMuchLargerButton, _fontMuchSmallerButton };

            // 两行右边界严格对齐：按钮宽度一概不动，把多出来的那点空间均分到间隙里，
            // 除不尽的零头逐个补 1px，保证两行最后一个按钮的右边界落在同一个像素上
            int captionWidth = 60;
            int minGap = 8;
            int styleSum = 0, fontSum = 0;
            foreach (Button b in styleButtons) styleSum += b.Width;
            foreach (Button b in fontButtons) fontSum += b.Width;
            int styleRight = captionWidth + styleSum + minGap * (styleButtons.Length - 1);
            int fontRight = captionWidth + fontSum + minGap * (fontButtons.Length - 1);
            int targetRight = Math.Max(styleRight, fontRight);

            LayoutButtons(styleBar, styleButtons, captionWidth, targetRight, minGap);
            LayoutButtons(fontBar, fontButtons, captionWidth, targetRight, minGap);

            _openButton = MakeButton("打开选中", 96);
            _createButton = MakeButton("新建卡片", 96);
            _deleteButton = MakeButton("删除选中", 96);
            _closeAllButton = MakeButton("全部关闭", 96);
            _refreshButton = MakeButton("刷新", 70);

            int left = 10;
            foreach (Button button in new Button[] { _openButton, _createButton, _deleteButton, _closeAllButton, _refreshButton })
            {
                button.Left = left;
                button.Top = 9;
                left += button.Width + 8;
                buttonBar.Controls.Add(button);
            }

            _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshButton.Left = ClientSize.Width - 10 - _refreshButton.Width;
            _closeAllButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _closeAllButton.Left = _refreshButton.Left - 8 - _closeAllButton.Width;
            _deleteButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _deleteButton.Left = _closeAllButton.Left - 8 - _deleteButton.Width;

            _list = new ListView();
            _list.SetBounds(0, 70, ClientSize.Width, ClientSize.Height - 70 - 52 - 44 - 44);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.BackColor = Color.FromArgb(34, 34, 34);
            _list.ForeColor = Color.FromArgb(230, 230, 230);
            _list.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular);
            _list.BorderStyle = BorderStyle.None;
            // 系统图表的列头是固定浅色的，改不动，直接隐藏，换一条自绘的深色表头
            _list.HeaderStyle = ColumnHeaderStyle.None;
            foreach (int width in DefaultColumnWidths)
                _list.Columns.Add("", width, HorizontalAlignment.Left);
            _list.Columns[0].TextAlign = HorizontalAlignment.Center;
            _list.Columns[2].TextAlign = HorizontalAlignment.Left;
            _list.Columns[1].TextAlign = HorizontalAlignment.Center;
            for (int index = 3; index < _list.Columns.Count; index++)
                _list.Columns[index].TextAlign = HorizontalAlignment.Center;
            Controls.Add(_list);

            _headerBar = new Panel();
            _headerBar.SetBounds(0, 44, ClientSize.Width, 26);
            _headerBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _headerBar.BackColor = Color.FromArgb(44, 44, 44);
            Controls.Add(_headerBar);
            Font headerFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _headerLabels = new Label[DefaultColumnWidths.Length];
            for (int index = 0; index < _headerLabels.Length; index++)
            {
                _headerLabels[index] = MakeHeaderLabel("", 0, 40, headerFont,
                    ContentAlignment.MiddleCenter);
                int column = index;
                _headerLabels[index].Click += delegate { SortBy(column); };
                _headerLabels[index].MouseDown += delegate(object sender, MouseEventArgs e)
                {
                    BeginHeaderDrag(column, (Label)sender, e);
                };
                _headerLabels[index].MouseMove += delegate(object sender, MouseEventArgs e)
                {
                    ContinueHeaderDrag(column, (Label)sender, e);
                };
                _headerLabels[index].MouseUp += delegate { EndHeaderDrag(); };
                _headerBar.Controls.Add(_headerLabels[index]);
            }
            // 表头列分隔线：用 1px 的 Panel 顶在标签上面，避免父面板 Paint 被标签盖住
            _headerDividers = new Panel[DefaultColumnWidths.Length - 1];
            for (int index = 0; index < _headerDividers.Length; index++)
            {
                _headerDividers[index] = new Panel();
                _headerDividers[index].BackColor = Color.FromArgb(66, 66, 66);
                _headerBar.Controls.Add(_headerDividers[index]);
            }

            LoadColumnWidths();
            LoadSortState();
            // 构造期 ListView 还没有窗口，ClientSize 取不到，显示出来后再压一次列宽
            Shown += delegate
            {
                FitColumnsToWindow();
                LayoutHeaderLabels();
            };
            LayoutHeaderLabels();
            UpdateHeaderTitles();
            _headerBar.BringToFront();

            _openButton.Click += delegate { OpenSelected(); };
            _createButton.Click += delegate { CreateNew(); };
            _deleteButton.Click += delegate { DeleteSelected(); };
            _closeAllButton.Click += delegate { CloseAll(); };
            _refreshButton.Click += delegate { RefreshList(false); };
            _fontLargerButton.Click += delegate { SendFontAdjust(2); };
            _fontSmallerButton.Click += delegate { SendFontAdjust(-2); };
            _fontMuchLargerButton.Click += delegate { SendFontAdjust(8); };
            _fontMuchSmallerButton.Click += delegate { SendFontAdjust(-8); };
            _textColorButton.Click += delegate { SendCommand(5); };
            _fontFaceButton.Click += delegate { SendCommand(6); };
            _backgroundColorButton.Click += delegate { SendCommand(7); };
            _transparentButton.Click += delegate { SendCommand(8); };
            _list.DoubleClick += delegate { OpenSelected(); };
            _list.ItemSelectionChanged += delegate
            {
                _list.Invalidate();
                UpdateButtons();
            };
            try
            {
                // 自绘时开双缓冲，滚动不闪
                typeof(ListView).GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_list, true, null);
            }
            catch
            {
            }
            _list.OwnerDraw = true;
            _list.DrawItem += DrawListItem;
            _list.MouseMove += delegate(object sender, MouseEventArgs e) { UpdateHover(e.Location); };
            _list.MouseLeave += delegate
            {
                _hoverRowIndex = -1;
                _hoverColumn = -1;
                _list.Cursor = Cursors.Default;
                _list.Invalidate();
            };
            _list.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ListViewHitTestInfo hit = _list.HitTest(e.Location);
                if (hit.Item == null || hit.SubItem == null) return;
                int column = hit.Item.SubItems.IndexOf(hit.SubItem);
                if (!IsCheckColumn(column)) return;

                // 只有落在勾选框本体（外扩 2px 方便点）才算点了勾，列再宽也不会整列都能点
                Rectangle box = CheckBoxBounds(CellBounds(hit.Item, column));
                box.Inflate(2, 2);
                if (!box.Contains(e.Location)) return;

                if (column == 1) ToggleRunning((int)hit.Item.Tag, hit.Item);
                else ToggleCell((int)hit.Item.Tag, column, hit.Item);
            };
            _list.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                ListViewItem item = _list.GetItemAt(e.X, e.Y);
                if (item == null) return;
                item.Selected = true;
                item.Focused = true;
            };
            BuildRowMenu();

            _timer = new Timer();
            _timer.Interval = 1500;
            _timer.Tick += delegate { RefreshRunningState(); };
            _timer.Start();

            RefreshList(false);
            RefreshRunningState();
            // 打开启动器先按 ini 把在跑卡片的窗口样式对一遍，兜住"界面显示已开、实际已失效"
            AlignRunningCardStyles();
        }

        private Label MakeHeaderLabel(string text, int left, int width, Font font, ContentAlignment align)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.SetBounds(left, 0, width, 26);
            label.Text = text;
            label.Cursor = Cursors.Hand;
            label.TextAlign = align;
            label.Padding = new Padding(6, 0, 4, 0);
            label.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                Label source = (Label)sender;
                source.Cursor = _dragColumn >= 0 ? Cursors.SizeWE :
                    (e.X >= source.Width - 8 ? Cursors.SizeWE : Cursors.Hand);
            };
            label.FlatStyle = FlatStyle.Flat;
            label.BackColor = Color.FromArgb(44, 44, 44);
            label.ForeColor = Color.FromArgb(215, 215, 215);
            label.Font = font;
            return label;
        }

        private Button MakeButton(string text, int width)
        {
            FlatButton button = new FlatButton();
            button.Text = text;
            button.Width = width;
            button.Height = 33;
            return button;
        }

        /// <summary>把一行按钮从左到右摆开，右边界落点固定为 targetRight，间隙均分、零头逐个补 1px。</summary>
        private static void LayoutButtons(Panel bar, Button[] buttons, int captionWidth, int targetRight, int minGap)
        {
            int sum = 0;
            foreach (Button button in buttons) sum += button.Width;
            int gapCount = buttons.Length - 1;
            if (gapCount <= 0) return;

            int gap = (targetRight - captionWidth - sum) / gapCount;
            if (gap < minGap) gap = minGap;
            int remainder = targetRight - captionWidth - sum - gap * gapCount;
            if (remainder < 0) remainder = 0;
            if (remainder > gapCount) remainder = gapCount;

            int left = captionWidth;
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Left = left;
                buttons[i].Top = 6;
                left += buttons[i].Width + gap + (i < remainder ? 1 : 0);
                bar.Controls.Add(buttons[i]);
            }
        }

        /// <summary>深色右键菜单的配色表，对齐 Win11 深色菜单。</summary>
        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            // 对齐豆包托盘右键菜单：实底深灰、浅灰 hover、弱分隔线
            internal static readonly Color MenuBack = Color.FromArgb(31, 31, 31);
            internal static readonly Color MenuHighlight = Color.FromArgb(58, 58, 58);
            internal static readonly Color LineColor = Color.FromArgb(51, 51, 51);
            internal static readonly Color TextColor = Color.FromArgb(255, 255, 255);
            internal static readonly Color DisabledText = Color.FromArgb(140, 140, 140);

            public override Color ToolStripDropDownBackground { get { return MenuBack; } }
            public override Color ImageMarginGradientBegin { get { return MenuBack; } }
            public override Color ImageMarginGradientMiddle { get { return MenuBack; } }
            public override Color ImageMarginGradientEnd { get { return MenuBack; } }
            public override Color MenuItemSelected { get { return MenuHighlight; } }
            public override Color MenuItemSelectedGradientBegin { get { return MenuHighlight; } }
            public override Color MenuItemSelectedGradientEnd { get { return MenuHighlight; } }
            public override Color MenuItemBorder { get { return MenuHighlight; } }
            public override Color MenuBorder { get { return LineColor; } }
            public override Color SeparatorDark { get { return LineColor; } }
            public override Color SeparatorLight { get { return LineColor; } }
            public override Color ToolStripBorder { get { return LineColor; } }
        }

        /// <summary>右键菜单渲染：实底深灰、整行铺底、4px 圆角浅灰 hover、纯白文字。</summary>
        private sealed class DarkMenuRenderer : ToolStripRenderer
        {
            public DarkMenuRenderer()
            {
            }

            /// <summary>把每个菜单项的整行铺满底色，缺了这一步没被 hover 的行会透出黑底。</summary>
            private static void FillRow(Graphics graphics, ToolStripItem item, ToolStrip strip)
            {
                int width = strip == null ? item.Width : strip.Width;
                Rectangle row = new Rectangle(0, item.Bounds.Top, width, item.Height);
                using (SolidBrush brush = new SolidBrush(DarkMenuColorTable.MenuBack))
                    graphics.FillRectangle(brush, row);
            }

            /// <summary>项内文字：左对齐、按整行高度垂直居中，留 12px 左边距。</summary>
            private static void DrawItemText(Graphics graphics, ToolStripItem item, int stripWidth)
            {
                Rectangle rect = new Rectangle(12, item.Bounds.Top, stripWidth - 24, item.Height);
                TextRenderer.DrawText(graphics, item.Text, item.Font, rect,
                    item.Enabled ? DarkMenuColorTable.TextColor : DarkMenuColorTable.DisabledText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            /// <summary>选中行：4px 圆角浅灰亮块。</summary>
            private static void DrawHighlight(Graphics graphics, ToolStripItem item, int stripWidth)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle bounds = new Rectangle(3, item.Bounds.Top + 1, stripWidth - 8, item.Height - 2);
                using (GraphicsPath path = RoundedPath(bounds, 5))
                using (SolidBrush brush = new SolidBrush(DarkMenuColorTable.MenuHighlight))
                    graphics.FillPath(brush, path);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                Graphics graphics = e.Graphics;
                int width = e.ToolStrip.Width;
                using (SolidBrush brush = new SolidBrush(DarkMenuColorTable.MenuBack))
                    graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));

                // 关键：ToolStrip 显示时只把"脏"的那一项画到窗口上，逐项画文字会只剩第一行有字。
                // 所以整条菜单的底色、分隔线、选中块、文字都在这一遍统一画完。
                foreach (ToolStripItem item in e.ToolStrip.Items)
                {
                    if (item is ToolStripSeparator)
                    {
                        using (Pen pen = new Pen(DarkMenuColorTable.LineColor))
                            graphics.DrawLine(pen, 10, item.Bounds.Top + item.Height / 2,
                                width - 10, item.Bounds.Top + item.Height / 2);
                        continue;
                    }
                    if (item.Selected && item.Enabled) DrawHighlight(graphics, item, width);
                    DrawItemText(graphics, item, width);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // 边框交给窗口区域裁圆角，这里不画直角边线
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                // 默认会画一条左侧竖线，深色菜单里很难看，直接不画
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Graphics graphics = e.Graphics;
                int width = e.ToolStrip == null ? e.Item.Width : e.ToolStrip.Width;
                FillRow(graphics, e.Item, e.ToolStrip);

                // hover 只会重绘这一项，所以高亮和文字要在这里再画一次
                if (e.Item.Selected && e.Item.Enabled) DrawHighlight(graphics, e.Item, width);
                DrawItemText(graphics, e.Item, width);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // 文字已在背景遍和 hover 遍画过，这里不再重复画第二遍
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                Graphics graphics = e.Graphics;
                FillRow(graphics, e.Item, e.ToolStrip);
                int width = e.ToolStrip == null ? e.Item.Width : e.ToolStrip.Width;
                int y = e.Item.Height / 2;
                using (Pen pen = new Pen(DarkMenuColorTable.LineColor))
                    graphics.DrawLine(pen, 10, y, width - 10, y);
            }
        }

        /// <summary>圆角矩形路径，按钮和菜单高亮共用。</summary>
        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            if (diameter <= 0 || rect.Width <= diameter || rect.Height <= diameter)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 直接使用系统原生 BUTTON 控件渲染：圆角、描边、hover、焦点、禁用态全部由
        /// Windows 主题引擎负责（DarkMode_Explorer），不再自己 GDI 填路径，
        /// 放大看边缘和 Santa Switch 一类原生深色控件一致，没有自绘抗锯齿留下的毛边。
        /// </summary>
        private sealed class FlatButton : Button
        {
            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
            private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

            public FlatButton()
            {
                FlatStyle = FlatStyle.System;   // 交给系统主题画，不要 UserPaint
                Cursor = Cursors.Hand;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                TryEnableDarkTheme(Handle);
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            }

            /// <summary>让 uxtheme 用深色资源画这个按钮；个别系统版本没有该主题时悄悄退回默认。</summary>
            private static void TryEnableDarkTheme(IntPtr handle)
            {
                if (handle == IntPtr.Zero) return;
                try
                {
                    SetWindowTheme(handle, "DarkMode_Explorer", null);
                }
                catch
                {
                    // 主题不可用时保持系统默认外观，不影响功能
                }
            }
        }

        // ---------------- 数据目录 ----------------

        private static string BaseDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    DataFolderName);
            }
        }

        private static string DirectoryFor(int index)
        {
            return index == 1
                ? BaseDirectory
                : Path.Combine(BaseDirectory, "instance" + index);
        }

        private static string CardExePath
        {
            get
            {
                string here = Path.GetDirectoryName(Application.ExecutablePath);
                string candidate = Path.Combine(here, "MyQuoteCard.exe");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(here, "dist", "MyQuoteCard.exe");
                if (File.Exists(candidate)) return candidate;
                return null;
            }
        }

        // ---------------- 列表刷新 ----------------

        /// <summary>按排序设置比较两张卡片。ListBox 自己的 Sort() 在自绘模式下不稳，这里自己排。</summary>
        private static int CompareInstances(CardInstance a, CardInstance b, int column)
        {
            CompareInfo chinese = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
            switch (column)
            {
                case 0:
                    return a.Index.CompareTo(b.Index);
                case 1:
                    if (a.Running == b.Running) return a.Index.CompareTo(b.Index);
                    return a.Running ? -1 : 1;
                case 2:
                    return chinese.Compare(a.Preview ?? "", b.Preview ?? "", CompareOptions.None);
                case 3:
                    if (a.Transparent == b.Transparent) return a.Index.CompareTo(b.Index);
                    return a.Transparent ? -1 : 1;
                case 4:
                    if (a.TopMost == b.TopMost) return a.Index.CompareTo(b.Index);
                    return a.TopMost ? -1 : 1;
                case 5:
                    if (a.ClickThrough == b.ClickThrough) return a.Index.CompareTo(b.Index);
                    return a.ClickThrough ? -1 : 1;
                case 6:
                    if (a.Startup == b.Startup) return a.Index.CompareTo(b.Index);
                    return a.Startup ? -1 : 1;
                default:
                    return a.Index.CompareTo(b.Index);
            }
        }

        private void RefreshList(bool keepSelection)
        {
            int selected = -1;
            if (keepSelection && _list.SelectedItems.Count > 0)
                selected = (int)_list.SelectedItems[0].Tag;

            List<CardInstance> instances = ScanInstances();
            // 先按当前排序列排好，再按顺序建行，顺序不依赖 ListView 内部行为
            instances.Sort(delegate(CardInstance a, CardInstance b)
            {
                int result = CompareInstances(a, b, _sortColumn);
                return _sortAscending ? result : -result;
            });
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (CardInstance instance in instances)
            {
                ListViewItem row = new ListViewItem(instance.Index.ToString());
                row.SubItems.Add(instance.Running ? "1" : "0");
                row.SubItems.Add(instance.Preview);
                row.SubItems.Add(instance.Transparent ? "1" : "0");
                row.SubItems.Add(instance.TopMost ? "1" : "0");
                row.SubItems.Add(instance.ClickThrough ? "1" : "0");
                row.SubItems.Add(instance.Startup ? "1" : "0");
                row.Tag = instance.Index;
                row.ToolTipText = instance.Preview;
                ApplyRowColors(row, instance);
                _list.Items.Add(row);
            }
            _list.EndUpdate();

            if (selected > 0)
            {
                foreach (ListViewItem row in _list.Items)
                {
                    if ((int)row.Tag == selected)
                    {
                        row.Selected = true;
                        row.Focused = true;
                        break;
                    }
                }
            }
            else
            {
                // 刚打开时没有"上次选中"的行，别让 ListView 自动选中第一行造成误认
                _list.SelectedItems.Clear();
            }

            int running = 0;
            foreach (CardInstance instance in instances) if (instance.Running) running++;
            _statusLabel.Text = "共 " + instances.Count + " 张卡片，正在运行 " + running +
                " 张。双击一行打开，列表显示的是每张卡片当前的内容。";
            UpdateButtons();
        }

        private void RefreshRunningState()
        {
            Dictionary<int, int> running = FindRunningInstances();
            _running.Clear();
            foreach (KeyValuePair<int, int> pair in running) _running[pair.Key] = pair.Value;
            foreach (ListViewItem row in _list.Items)
            {
                int index = (int)row.Tag;
                bool isRunning = running.ContainsKey(index);
                row.SubItems[1].Text = isRunning ? "1" : "0";
                // 整行颜色跟着状态走：只有运行中是绿的，退出后编号列也回到浅灰
                row.ForeColor = isRunning ? Color.FromArgb(110, 214, 130) : Color.FromArgb(225, 225, 225);
                row.SubItems[1].ForeColor = isRunning ? Color.FromArgb(110, 214, 130) : Color.FromArgb(150, 150, 150);
                row.SubItems[2].ForeColor = Color.FromArgb(225, 225, 225);
                // 透明 / 顶层 两列跟着 ini 实时更新，卡片那边切过之后这里也能看到
                string directory = DirectoryFor(index);
                row.SubItems[3].Text = DecodeStateFlag(directory, "TransparentBackground") ? "1" : "0";
                row.SubItems[4].Text = DecodeStateFlag(directory, "TopMost") ? "1" : "0";
                row.SubItems[5].Text = DecodeStateFlag(directory, "ClickThrough") ? "1" : "0";
                row.SubItems[6].Text = IsStartupEnabled(index) ? "1" : "0";
            }
            _list.Invalidate();
            UpdateButtons();
        }

        private static void ApplyRowColors(ListViewItem row, CardInstance instance)
        {
            row.ForeColor = instance.Running ? Color.FromArgb(110, 214, 130) : Color.FromArgb(225, 225, 225);
            row.SubItems[2].ForeColor = Color.FromArgb(225, 225, 225);
            row.SubItems[3].ForeColor = Color.FromArgb(225, 225, 225);
            row.SubItems[4].ForeColor = Color.FromArgb(225, 225, 225);
            row.SubItems[5].ForeColor = Color.FromArgb(225, 225, 225);
            row.SubItems[6].ForeColor = Color.FromArgb(225, 225, 225);
        }

        private void UpdateButtons()
        {
            bool hasSelection = _list.SelectedItems.Count > 0;
            _openButton.Enabled = hasSelection;
            _deleteButton.Enabled = hasSelection && (int)_list.SelectedItems[0].Tag > 1;
            int count = _list.Items.Count;
            _createButton.Enabled = count < MaxInstances;

            // 四个字号按钮：选中的那张卡片必须在运行，否则置灰
            bool canAdjustFont = false;
            if (hasSelection)
            {
                int index = (int)_list.SelectedItems[0].Tag;
                canAdjustFont = _running.ContainsKey(index);
            }
            _fontLargerButton.Enabled = canAdjustFont;
            _fontSmallerButton.Enabled = canAdjustFont;
            _fontMuchLargerButton.Enabled = canAdjustFont;
            _fontMuchSmallerButton.Enabled = canAdjustFont;
            _textColorButton.Enabled = canAdjustFont;
            _fontFaceButton.Enabled = canAdjustFont;
            _backgroundColorButton.Enabled = canAdjustFont;
            _transparentButton.Enabled = canAdjustFont;
            _layerButton.Enabled = canAdjustFont;
        }

        private static string LauncherStatePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MyQuoteCard", "launcher.ini");
            }
        }

        /// <summary>列宽合计超过可用宽度时压缩列宽，任何 DPI 下都不出现横向滚动条。</summary>
        private void FitColumnsToWindow()
        {
            if (_list == null) return;
            int room = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            if (room < 260) return;
            int total = 0;
            foreach (ColumnHeader column in _list.Columns) total += column.Width;
            int overflow = total - room;
            if (overflow <= 0) return;
            // 先让"当前内容"列让位，最少保留 120 像素
            ColumnHeader preview = _list.Columns[2];
            int cut = Math.Min(overflow, Math.Max(0, preview.Width - 120));
            preview.Width -= cut;
            overflow -= cut;
            // 还不够就其余列每轮各收 6 像素，最少收到 44
            while (overflow > 0)
            {
                bool changed = false;
                for (int index = 0; index < _list.Columns.Count && overflow > 0; index++)
                {
                    if (index == 2) continue;
                    if (_list.Columns[index].Width <= 44) continue;
                    int take = Math.Min(overflow, 6);
                    _list.Columns[index].Width -= take;
                    overflow -= take;
                    changed = true;
                }
                if (!changed) break;
            }
        }

        /// <summary>按各列当前宽度重新摆放表头文字，宽度变化后也能对齐。</summary>
        private void LayoutHeaderLabels()
        {
            if (_headerLabels == null || _headerBar == null) return;
            // 七列宽度超过列表可用宽度时，从"当前内容"列里扣，保证不出现横向滚动条
            FitColumnsToWindow();
            int left = 0;
            for (int index = 0; index < _headerLabels.Length; index++)
            {
                int width = _list.Columns[index].Width;
                _headerLabels[index].SetBounds(left, 0, width, 26);
                if (_headerDividers != null && index < _headerDividers.Length)
                    _headerDividers[index].SetBounds(left + width - 1, 0, 1, 26);
                left += width;
            }
            if (_headerDividers != null)
                foreach (Panel divider in _headerDividers) divider.BringToFront();
            _headerBar.Invalidate();
        }

        private void LoadColumnWidths()
        {
            try
            {
                string path = LauncherStatePath;
                if (!File.Exists(path)) return;
                // v29 换了列宽分配方案，旧版本的六列宽度不再沿用，第一次先按新默认来
                bool migrated = false;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim() != "WidthsV29") continue;
                    migrated = line.Substring(eq + 1).Trim() == "1";
                }
                if (!migrated) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator).Trim();
                    if (!key.StartsWith("ColWidth", StringComparison.Ordinal)) continue;
                    int column;
                    if (!int.TryParse(key.Substring("ColWidth".Length), out column)) continue;
                    if (column < 0 || column >= DefaultColumnWidths.Length) continue;
                    int width;
                    if (!int.TryParse(line.Substring(separator + 1).Trim(), out width)) continue;
                    if (width >= 30 && width <= 500) _list.Columns[column].Width = width;
                }
            }
            catch
            {
                // 读不出来就用默认宽度
            }
        }

        private void SaveLauncherState()
        {
            try
            {
                string path = LauncherStatePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                for (int index = 0; index < DefaultColumnWidths.Length; index++)
                    lines.Add("ColWidth" + index.ToString() + "=" + _list.Columns[index].Width.ToString());
                lines.Add("WidthsV29=1");
                // 排序不落盘：每次打开固定按编号升序，避免来回翻转
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch
            {
                // 存不下也不影响使用
            }
        }

        /// <summary>启动一律按编号升序；排序只在本次会话内有效，不跨启动保存。</summary>
        private void LoadSortState()
        {
            _sortColumn = 0;
            _sortAscending = true;
        }

        private void BeginHeaderDrag(int column, Label label, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.X < label.Width - 8) return;
            _dragColumn = column;
            _dragStartX = _headerBar.PointToClient(Cursor.Position).X;
            _dragStartWidth = _list.Columns[column].Width;
            label.Capture = true;
        }

        private void ContinueHeaderDrag(int column, Label label, MouseEventArgs e)
        {
            if (_dragColumn != column) return;
            int currentX = _headerBar.PointToClient(Cursor.Position).X;
            int width = _dragStartWidth + (currentX - _dragStartX);
            if (width < 30) width = 30;
            if (width > 500) width = 500;
            if (_list.Columns[column].Width == width) return;
            _list.Columns[column].Width = width;
            LayoutHeaderLabels();
        }

        private void EndHeaderDrag()
        {
            if (_dragColumn < 0) return;
            _dragColumn = -1;
            _dragEndTime = DateTime.Now;
            SaveLauncherState();
            LayoutHeaderLabels();
        }

        /// <summary>点表头排序：同一列再点一次反向。</summary>
        private void SortBy(int column)
        {
            if (column < 0 || column >= _headerLabels.Length) return;
            // 拖列宽结束时系统也会补发一次 Click，用时间窗放过它，不影响下一次真正的点击
            if ((DateTime.Now - _dragEndTime).TotalMilliseconds < 400) return;
            if (_sortColumn == column) _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }
            UpdateHeaderTitles();
            RefreshList(true);
            SaveLauncherState();
        }

        private void UpdateHeaderTitles()
        {
            string[] titles = ColumnTitles;
            for (int index = 0; index < _headerLabels.Length; index++)
            {
                string suffix = index == _sortColumn ? (_sortAscending ? " ▲" : " ▼") : "";
                _headerLabels[index].Text = titles[index] + suffix;
            }
        }

        /// <summary>选中行的底色：低饱和石墨蓝，不用系统那个亮蓝，免得盖掉绿色文字。</summary>
        private static readonly Color RowSelectedColor = Color.FromArgb(36, 50, 68);
        private static readonly Color RowHoverColor = Color.FromArgb(48, 62, 80);
        private static readonly Color RowBackColor = Color.FromArgb(34, 34, 34);
        private static readonly Color RunningTextColor = Color.FromArgb(110, 214, 130);
        private static readonly Color NormalTextColor = Color.FromArgb(225, 225, 225);

        private const int CheckBoxSize = 14;

        /// <summary>第 column 列在某一行的矩形范围。</summary>
        private Rectangle CellBounds(ListViewItem item, int column)
        {
            if (item == null || column < 0 || column >= _list.Columns.Count) return Rectangle.Empty;
            int left = item.Bounds.Left;
            for (int index = 0; index < column; index++) left += _list.Columns[index].Width;
            return new Rectangle(left, item.Bounds.Top, _list.Columns[column].Width, item.Bounds.Height);
        }

        /// <summary>勾选框本体的矩形，绘制和点击命中都用这一份，保证"画在哪就只能点哪"。</summary>
        private static Rectangle CheckBoxBounds(Rectangle cell)
        {
            return new Rectangle(
                cell.Left + (cell.Width - CheckBoxSize) / 2,
                cell.Top + (cell.Height - CheckBoxSize) / 2,
                CheckBoxSize, CheckBoxSize);
        }

        private static bool IsCheckColumn(int column)
        {
            return column == 1 || column == 3 || column == 4 || column == 5 || column == 6;
        }

        private void UpdateHover(Point location)
        {
            ListViewHitTestInfo hit = _list.HitTest(location);
            int row = -1;
            int column = -1;

            // 只有指针真的落在某一行的矩形内才算悬停，落在行间隙 / 表头一律视为没有
            if (hit.Item != null && hit.Item.Index >= 0)
            {
                Rectangle itemBounds = hit.Item.Bounds;
                if (itemBounds.Contains(location))
                {
                    row = hit.Item.Index;
                    if (hit.SubItem != null) column = hit.Item.SubItems.IndexOf(hit.SubItem);
                }
            }

            bool clickable = false;
            if (IsCheckColumn(column) && row >= 0 && row < _list.Items.Count)
            {
                Rectangle box = CheckBoxBounds(CellBounds(_list.Items[row], column));
                box.Inflate(2, 2);
                clickable = box.Contains(location);
            }
            _list.Cursor = clickable ? Cursors.Hand : Cursors.Default;
            if (row == _hoverRowIndex && column == _hoverColumn) return;

            _hoverRowIndex = row;
            _hoverColumn = clickable ? column : -1;

            // RedrawItems 在 OwnerDraw 下经常不真正重绘，旧底色会残留在屏幕上，
            // 表现为"到处都有淡蓝、只有个别位置会变"。这里直接整表失效。
            _list.Invalidate();
        }

        private void DrawListItem(object sender, DrawListViewItemEventArgs e)
        {
            // 优先级：选中 > 悬停 > 普通。选中行不会被 hover 覆盖掉底色
            bool selected = _list.SelectedIndices.Contains(e.ItemIndex);
            bool hoveredRow = !selected && e.ItemIndex == _hoverRowIndex;
            Color rowBack = selected ? RowSelectedColor : (hoveredRow ? RowHoverColor : RowBackColor);
            using (SolidBrush background = new SolidBrush(rowBack))
                e.Graphics.FillRectangle(background, e.Bounds);

            Color textColor = e.Item.SubItems[1].Text == "1" ? RunningTextColor : NormalTextColor;
            int left = e.Bounds.Left;
            for (int column = 0; column < _list.Columns.Count; column++)
            {
                int width = _list.Columns[column].Width;
                Rectangle cell = new Rectangle(left, e.Bounds.Top, width, e.Bounds.Height);
                if (cell.Right > e.Bounds.Right) cell.Width = Math.Max(0, e.Bounds.Right - cell.Left);

                if (IsCheckColumn(column))
                {
                    bool hovered = e.ItemIndex == _hoverRowIndex && column == _hoverColumn;
                    DrawCheckBox(e.Graphics, cell, e.Item.SubItems[column].Text == "1", hovered);
                }
                else
                {
                    bool centre = column != 2;
                    DrawCellText(e.Graphics, e.Item.SubItems[column].Text, cell, centre, textColor);
                }

                left += width;
                if (left >= e.Bounds.Right) break;
            }

            // 列分隔线：贯穿整行，深色底上用低对比灰，选中行稍微亮一档
            int separateAt = e.Bounds.Left;
            Color separatorColor = selected ? Color.FromArgb(64, 80, 100) : Color.FromArgb(50, 50, 50);
            for (int column = 0; column < _list.Columns.Count - 1; column++)
            {
                separateAt += _list.Columns[column].Width;
                if (separateAt >= e.Bounds.Right) break;
                using (Pen separator = new Pen(separatorColor))
                    e.Graphics.DrawLine(separator, separateAt, e.Bounds.Top, separateAt, e.Bounds.Bottom - 1);
            }

            if (selected)
            {
                using (Pen edge = new Pen(Color.FromArgb(78, 96, 118)))
                    e.Graphics.DrawRectangle(edge, e.Bounds.Left, e.Bounds.Top,
                        e.Bounds.Width - 1, e.Bounds.Height - 1);
            }
        }

        private void DrawCellText(Graphics graphics, string text, Rectangle cell,
            bool centre, Color color)
        {
            StringFormat format = new StringFormat();
            format.LineAlignment = StringAlignment.Center;
            format.Alignment = centre ? StringAlignment.Center : StringAlignment.Near;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            Rectangle padded = new Rectangle(cell.Left + 6, cell.Top, Math.Max(0, cell.Width - 10), cell.Height);
            using (SolidBrush brush = new SolidBrush(color))
                graphics.DrawString(text, _list.Font, brush, padded, format);
            format.Dispose();
        }

        /// <summary>统一尺寸的自绘勾选框：外框固定，勾只画在里面，hover 时框体提亮。</summary>
        private void DrawCheckBox(Graphics graphics, Rectangle cell, bool isChecked, bool hovered)
        {
            Rectangle box = CheckBoxBounds(cell);
            int x = box.Left;
            int y = box.Top;

            if (hovered)
            {
                Rectangle glow = new Rectangle(x - 3, y - 3, CheckBoxSize + 6, CheckBoxSize + 6);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(62, 86, 112)))
                    graphics.FillRectangle(brush, glow);
            }

            if (isChecked)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(76, 148, 232)))
                    graphics.FillRectangle(brush, box);
                using (Pen edge = new Pen(Color.FromArgb(120, 176, 240)))
                    graphics.DrawRectangle(edge, box);
                using (Pen tick = new Pen(Color.White, 1.6f))
                {
                    tick.StartCap = LineCap.Round;
                    tick.EndCap = LineCap.Round;
                    graphics.DrawLine(tick, x + 3.5f, y + 7.5f, x + 6.0f, y + 10.0f);
                    graphics.DrawLine(tick, x + 6.0f, y + 10.0f, x + 10.5f, y + 4.5f);
                }
            }
            else
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(44, 44, 44)))
                    graphics.FillRectangle(brush, box);
                using (Pen edge = new Pen(hovered ? Color.FromArgb(150, 150, 150) : Color.FromArgb(104, 104, 104)))
                    graphics.DrawRectangle(edge, box);
            }
        }

        /// <summary>点状态勾：勾着就关掉，空着就启动这一张。不加确认框。</summary>
        private void ToggleRunning(int index, ListViewItem row)
        {
            Dictionary<int, int> running = FindRunningInstances();
            if (running.ContainsKey(index))
            {
                CloseProcesses(new List<int>(new int[] { running[index] }));
            }
            else
            {
                StartInstance(index);
            }
            row.SubItems[1].Text = "0";
            _list.Invalidate();
        }

        /// <summary>启动指定编号的卡片。</summary>
        private void StartInstance(int index)
        {
            string exe = CardExePath;
            if (exe == null)
            {
                MessageBox.Show(this,
                    "找不到 MyQuoteCard.exe。\r\n请把启动器放在和 MyQuoteCard.exe 同一个文件夹里。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (index == 1) Process.Start(exe);
                else Process.Start(exe, "--instance=" + index);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "打开失败：" + error.Message,
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Timer delay = new Timer();
            delay.Interval = 1200;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                if (!IsDisposed) RefreshList(true);
            };
            delay.Start();
        }

        /// <summary>点单元格直接开关：正在运行就发指令，没运行就改它的 state.ini，下次打开生效。</summary>
        private void ToggleCell(int index, int column, ListViewItem row)
        {
            string directory = DirectoryFor(index);
            bool running = FindRunningInstances().ContainsKey(index);

            // 关键：命令必须发给「被点的这一行」对应的卡片，
            // 不能走 SendCommand（那个用的是 _list.SelectedItems[0]），
            // 否则点第 3 行会去改当前选中的第 1 张卡，出现"点了这行关了那行"的串状态。
            if (column == 6)
            {
                ToggleStartup(index);
            }
            else if (column == 5)
            {
                if (running && SendCommandTo(index, 12)) { }
                else FlipStateFlag(directory, "ClickThrough");
            }
            else if (column == 3)
            {
                if (running && SendCommandTo(index, 8)) { }
                else FlipStateFlag(directory, "TransparentBackground");
            }
            else if (column == 4)
            {
                if (running && SendCommandTo(index, 11)) { }
                else FlipStateFlag(directory, "TopMost");
            }

            row.SubItems[3].Text = DecodeStateFlag(directory, "TransparentBackground") ? "1" : "0";
            row.SubItems[4].Text = DecodeStateFlag(directory, "TopMost") ? "1" : "0";
            row.SubItems[5].Text = DecodeStateFlag(directory, "ClickThrough") ? "1" : "0";
            row.SubItems[6].Text = IsStartupEnabled(index) ? "1" : "0";
            _list.Invalidate();

            // 卡片那边是收到消息后异步写盘，立刻读会是旧值；半秒后再对一次，勾选框不会停在老状态
            RefreshFlagsLater();
        }

        /// <summary>半秒后补刷一次状态列，等卡片把 state.ini 写完。</summary>
        private void RefreshFlagsLater()
        {
            Timer once = new Timer();
            once.Interval = 500;
            once.Tick += delegate
            {
                once.Stop();
                once.Dispose();
                RefreshRunningState();
            };
            once.Start();
        }

        /// <summary>卡片没运行时改 ini：只替换那一行，其他内容原样保留。</summary>
        private static void FlipStateFlag(string directory, string key)
        {
            string path = Path.Combine(directory, "state.ini");
            try
            {
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                bool found = false;
                for (int position = 0; position < lines.Length; position++)
                {
                    int separator = lines[position].IndexOf('=');
                    if (separator <= 0) continue;
                    if (!string.Equals(lines[position].Substring(0, separator), key, StringComparison.Ordinal)) continue;
                    bool current = string.Equals(lines[position].Substring(separator + 1).Trim(),
                        "True", StringComparison.OrdinalIgnoreCase);
                    lines[position] = key + "=" + (current ? "False" : "True");
                    found = true;
                    break;
                }
                if (!found) return;
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
            }
            catch
            {
                // 写不进去就算了，界面上会显示原状态
            }
        }

        /// <summary>开机自启不依赖卡片是否运行：直接改注册表对应值名。</summary>
        private void ToggleStartup(int index)
        {
            string exe = CardExePath;
            if (exe == null) return;
            try
            {
                string valueName = index <= 1 ? "MyQuoteCard" : "MyQuoteCard" + index.ToString();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null) return;
                    if (IsStartupEnabled(index)) key.DeleteValue(valueName, false);
                    else
                        key.SetValue(valueName,
                            "\"" + exe + "\"" + (index <= 1 ? " --startup" : " --instance=" + index.ToString()),
                            RegistryValueKind.String);
                }
            }
            catch
            {
                // 权限或注册表不可用时保持原样
            }
        }

        /// <summary>把样式指令发给正在运行的那张卡片（复用卡片自己的菜单逻辑）。</summary>
        private void SendCommand(int command)
        {
            if (_list.SelectedItems.Count == 0) return;
            SendCommandTo((int)_list.SelectedItems[0].Tag, command);
        }

        /// <summary>对已经拿到的窗口句柄直接发指令，省掉一次全量枚举。</summary>
        private bool SendCommandToHandle(IntPtr window, int command)
        {
            if (window == IntPtr.Zero) return false;
            IntPtr result;
            IntPtr sent = SendMessageTimeout(window, CommandMessage,
                (IntPtr)command, IntPtr.Zero, 0x0002, 2000, out result);
            if (sent == IntPtr.Zero)
            {
                RefreshRunningState();
                return false;
            }
            return true;
        }

        /// <summary>
        /// 启动器打开时对一遍：正在跑的卡片，窗口上的穿透/置顶位应该和 ini 里的设置一致。
        /// 不一致就把期望值下发给卡片，让它自己拉回去——跨进程改不了别人的窗口样式，只能让卡片动手。
        /// 开机自启那列本来就直读注册表（IsStartupEnabled），不存在显示假勾的问题，这里不用管。
        /// </summary>
        private void AlignRunningCardStyles()
        {
            foreach (KeyValuePair<int, int> pair in _running)
            {
                int index = pair.Key;
                IntPtr window = FindWindowByInstanceIndex(index);
                if (window == IntPtr.Zero) continue;

                string directory = DirectoryFor(index);
                bool wantClickThrough = DecodeStateFlag(directory, "ClickThrough");
                bool wantTopMost = DecodeStateFlag(directory, "TopMost");

                int style;
                try { style = GetWindowLongW(window, GwlExStyle); }
                catch { continue; }
                bool hasClickThrough = (style & WsExTransparent) != 0;
                bool hasTopMost = (style & WsExTopMost) != 0;
                if (hasClickThrough == wantClickThrough && hasTopMost == wantTopMost) continue;

                SendCommandToHandle(window, CommandAlignStyle
                    | (wantClickThrough ? 0x100 : 0)
                    | (wantTopMost ? 0x200 : 0));
            }
        }

        /// <summary>把命令发给指定编号的卡片；卡片没跑或窗口没找到返回 false，让调用方走 ini 兜底。</summary>
        private bool SendCommandTo(int index, int command)
        {
            if (!_running.ContainsKey(index)) return false;

            IntPtr window = FindWindowByInstanceIndex(index);
            if (window == IntPtr.Zero) return false;

            IntPtr result;
            IntPtr sent = SendMessageTimeout(window, CommandMessage,
                (IntPtr)command, IntPtr.Zero, 0x0002, 2000, out result);
            if (sent == IntPtr.Zero)
            {
                RefreshRunningState();
                return false;
            }
            return true;
        }

        /// <summary>把字号档位差发给正在运行的那张卡片（复用卡片自己的缩放逻辑）。</summary>
        private void SendFontAdjust(int difference)
        {
            if (_list.SelectedItems.Count == 0) return;
            int index = (int)_list.SelectedItems[0].Tag;
            if (!_running.ContainsKey(index)) return;

            IntPtr window = FindWindowByInstanceIndex(index);
            if (window == IntPtr.Zero) return;

            IntPtr result;
            IntPtr sent = SendMessageTimeout(window, FontAdjustMessage,
                (IntPtr)difference, IntPtr.Zero, 0x0002, 2000, out result);
            if (sent == IntPtr.Zero) RefreshRunningState();
        }

        private List<CardInstance> ScanInstances()
        {
            List<CardInstance> result = new List<CardInstance>();
            Dictionary<int, int> running = FindRunningInstances();

            for (int index = 1; index <= MaxInstances; index++)
            {
                string directory = DirectoryFor(index);
                bool usable = index == 1 ? Directory.Exists(BaseDirectory) : Directory.Exists(directory);
                if (!usable) continue;
                CardInstance instance = new CardInstance();
                instance.Index = index;
                instance.Directory = directory;
                instance.Running = running.ContainsKey(index);
                instance.ProcessId = running.ContainsKey(index) ? running[index] : 0;
                instance.Preview = BuildPreview(directory);
                instance.Transparent = DecodeStateFlag(directory, "TransparentBackground");
                instance.TopMost = DecodeStateFlag(directory, "TopMost");
                instance.ClickThrough = DecodeStateFlag(directory, "ClickThrough");
                instance.Startup = IsStartupEnabled(index);
                result.Add(instance);
            }
            return result;
        }

        private static string BuildPreview(string directory)
        {
            string text = DecodeStateValue(directory, "TodayQuote");
            if (string.IsNullOrEmpty(text)) text = FirstLibraryLine(directory);
            if (string.IsNullOrEmpty(text)) return "（还没有内容，双击卡片后编辑）";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.IndexOf("  ") >= 0) text = text.Replace("  ", " ");
            if (text.Length > 46) text = text.Substring(0, 46) + "…";
            return text;
        }

        private static bool DecodeStateFlag(string directory, string key)
        {
            // 布尔项在 ini 里是明文 True/False，只有字符串（TodayQuote 之类）才做 base64
            try
            {
                string path = Path.Combine(directory, "state.ini");
                if (!File.Exists(path)) return false;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    if (!string.Equals(line.Substring(0, separator), key, StringComparison.Ordinal)) continue;
                    return string.Equals(line.Substring(separator + 1).Trim(), "True",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 读不到就当关闭
            }
            return false;
        }

        /// <summary>开机自启按实例读注册表（第 1 张沿用 MyQuoteCard，其余 MyQuoteCard2..9）。</summary>
        private static bool IsStartupEnabled(int index)
        {
            try
            {
                string valueName = index <= 1 ? "MyQuoteCard" : "MyQuoteCard" + index.ToString();
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(valueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string DecodeStateValue(string directory, string key)
        {
            try
            {
                string path = Path.Combine(directory, "state.ini");
                if (!File.Exists(path)) return "";
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    if (!string.Equals(line.Substring(0, separator), key, StringComparison.Ordinal)) continue;
                    string raw = line.Substring(separator + 1).Trim();
                    if (raw.Length == 0) return "";
                    return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                }
            }
            catch
            {
                // 读不出来就退回内容库第一行
            }
            return "";
        }

        private static string FirstLibraryLine(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "quotes.txt");
                if (!File.Exists(path)) return "";
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string value = (line ?? "").Trim();
                    if (value.Length > 0) return value;
                }
            }
            catch
            {
            }
            return "";
        }

        // ---------------- 运行状态检测 ----------------

        private static readonly Regex TitlePattern =
            new Regex(@"^(?:\u81ea\u8a00\u5361|MyQuoteCard)(?: #(\d+))?$", RegexOptions.Compiled);

        private static Dictionary<int, int> FindRunningInstances()
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                StringBuilder buffer = new StringBuilder(512);
                GetWindowTextW(hWnd, buffer, buffer.Capacity);
                string title = buffer.ToString();
                if (title.Length == 0) return true;
                Match match = TitlePattern.Match(title);
                if (!match.Success) return true;
                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);
                int index = 1;
                if (match.Groups[1].Success) int.TryParse(match.Groups[1].Value, out index);
                if (index >= 1 && index <= MaxInstances)
                    result[index] = (int)processId;
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // ---------------- 操作 ----------------

        private void BuildRowMenu()
        {
            _rowMenu = new ContextMenuStrip();
            _rowMenu.BackColor = DarkMenuColorTable.MenuBack;
            _rowMenu.ForeColor = DarkMenuColorTable.TextColor;
            _rowMenu.ShowImageMargin = false;
            _rowMenu.Renderer = new DarkMenuRenderer();

            ToolStripMenuItem openItem = new ToolStripMenuItem("打开这一张");
            openItem.Click += delegate { OpenSelected(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出卡片");
            exitItem.Click += delegate { CloseSelected(); };
            ToolStripMenuItem editItem = new ToolStripMenuItem("编辑文字");
            editItem.Click += delegate { EditSelected(); };
            ToolStripMenuItem deleteItem = new ToolStripMenuItem("删除这一张");
            deleteItem.Click += delegate { DeleteSelected(); };

            // 深底细字会显得发虚，菜单项统一加粗一档；行距也跟着松一点，接近托盘菜单的呼吸感
            Font menuFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            foreach (ToolStripItem item in new ToolStripItem[] { openItem, exitItem, editItem, deleteItem })
            {
                item.Padding = new Padding(12, 6, 12, 6);
                item.Font = menuFont;
            }
            _rowMenu.Items.Add(openItem);
            _rowMenu.Items.Add(exitItem);
            _rowMenu.Items.Add(editItem);
            _rowMenu.Items.Add(new ToolStripSeparator());
            _rowMenu.Items.Add(deleteItem);

            // 菜单整体圆角：Win32 原生菜单是直角，用窗口区域裁一个 4px 圆角
            // 菜单整体圆角 + 4px 半径区域裁剪
            _rowMenu.Opened += delegate { RoundRowMenu(); };

            _rowMenu.Opening += delegate
            {
                bool has = _list.SelectedItems.Count > 0;
                int index = has ? (int)_list.SelectedItems[0].Tag : 0;
                openItem.Enabled = has;
                editItem.Enabled = has;
                // 只有正在运行的那张才能"退出"
                exitItem.Enabled = has && FindRunningInstances().ContainsKey(index);
                deleteItem.Enabled = has && index > 1;
            };

            _list.ContextMenuStrip = _rowMenu;
        }

        /// <summary>编辑选中那张卡片的文字：没在运行就先把它打开，起来后再把编辑指令送过去。</summary>
        private void EditSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            int index = (int)_list.SelectedItems[0].Tag;
            // 先按当前真实运行态判断，避免 _running 过期导致又起一张
            RefreshRunningState();
            if (_running.ContainsKey(index))
            {
                SendCommandTo(index, 13);
                return;
            }

            StartInstance(index);
            // 刚起的卡片要一会儿才建好窗口，每 0.4 秒试一次，最多 8 秒
            int attempts = 0;
            Timer delay = new Timer();
            delay.Interval = 400;
            delay.Tick += delegate
            {
                attempts++;
                if (IsDisposed || attempts > 20)
                {
                    delay.Stop();
                    delay.Dispose();
                    return;
                }
                RefreshRunningState();
                if (!_running.ContainsKey(index)) return;
                if (FindWindowByInstanceIndex(index) == IntPtr.Zero) return;
                delay.Stop();
                delay.Dispose();
                SendCommandTo(index, 13);
            };
            delay.Start();
        }

        /// <summary>把右键菜单窗口裁成 4px 圆角，Win32 原生菜单没有圆角。</summary>
        private void RoundRowMenu()
        {
            if (_rowMenu == null || !_rowMenu.IsHandleCreated) return;
            int width = _rowMenu.Width;
            int height = _rowMenu.Height;
            if (width <= 0 || height <= 0) return;
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, 10, 10);
            if (region == IntPtr.Zero) return;
            // 设进去之后区域归系统所有；设失败才自己销毁
            if (SetWindowRgn(_rowMenu.Handle, region, true) == 0) DeleteObject(region);
        }

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            int index = (int)_list.SelectedItems[0].Tag;
            CardInstance found = null;
            foreach (CardInstance instance in ScanInstances())
                if (instance.Index == index) found = instance;

            if (found != null && found.Running)
            {
                BringToFrontWindow(index);
                RefreshRunningState();
                return;
            }

            StartInstance(index);
        }

        private void BringToFrontWindow(int index)
        {
            Dictionary<int, int> running = FindRunningInstances();
            if (!running.ContainsKey(index)) return;
            IntPtr window = FindWindowByInstanceIndex(index);
            if (window == IntPtr.Zero) return;
            ShowWindow(window, 5);
            SetForegroundWindow(window);
        }

        private static IntPtr FindWindowByInstanceIndex(int index)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                StringBuilder buffer = new StringBuilder(512);
                GetWindowTextW(hWnd, buffer, buffer.Capacity);
                Match match = TitlePattern.Match(buffer.ToString());
                if (!match.Success) return true;
                int titleIndex = 1;
                if (match.Groups[1].Success) int.TryParse(match.Groups[1].Value, out titleIndex);
                if (titleIndex == index)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private void CreateNew()
        {
            string exe = CardExePath;
            if (exe == null)
            {
                MessageBox.Show(this,
                    "找不到 MyQuoteCard.exe。\r\n请把启动器放在和 MyQuoteCard.exe 同一个文件夹里。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int maxIndex = 0;
            foreach (CardInstance instance in ScanInstances())
                if (instance.Index > maxIndex) maxIndex = instance.Index;

            int nextIndex = maxIndex + 1;
            if (nextIndex > MaxInstances)
            {
                MessageBox.Show(this, "最多只能有 " + MaxInstances + " 张卡片。\r\n请先删掉不用的那张。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                if (nextIndex == 1) Process.Start(exe);
                else Process.Start(exe, "--instance=" + nextIndex);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "新建失败：" + error.Message,
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Timer delay = new Timer();
            delay.Interval = 1500;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                if (!IsDisposed) RefreshList(false);
            };
            delay.Start();
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            int index = (int)_list.SelectedItems[0].Tag;
            if (index <= 1)
            {
                MessageBox.Show(this,
                    "第 1 张卡片用的是主目录（所有卡片的内容库都在它下面），不能整体删除。\r\n\r\n" +
                    "如果不想看它，打开后把内容改成别的也行。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string preview = _list.SelectedItems[0].SubItems[2].Text;
            DialogResult answer = MessageBox.Show(this,
                "确定删除第 " + index + " 张卡片吗？\r\n\r\n内容：" + preview +
                "\r\n\r\n会连同它的设置一起删掉，删除后无法恢复。",
                "删除卡片", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            Dictionary<int, int> running = FindRunningInstances();
            if (running.ContainsKey(index))
            {
                try
                {
                    Process runningProcess = Process.GetProcessById(running[index]);
                    runningProcess.CloseMainWindow();
                    if (!runningProcess.WaitForExit(1500)) runningProcess.Kill();
                    runningProcess.WaitForExit(1200);
                }
                catch
                {
                    // 进程可能已经退出
                }
            }

            string directory = DirectoryFor(index);
            string error2 = DeleteDirectoryWithRetry(directory);
            if (error2 != null)
            {
                MessageBox.Show(this,
                    "删除失败：" + error2 + "\r\n\r\n可能卡片还在关闭中，稍后再点一次删除。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RefreshList(false);
        }

        private static string DeleteDirectoryWithRetry(string directory)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(directory)) return null;
                    Directory.Delete(directory, true);
                    return null;
                }
                catch (Exception error)
                {
                    if (attempt == 7) return error.Message;
                    System.Threading.Thread.Sleep(250);
                }
            }
            return null;
        }

        private void CloseSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            int index = (int)_list.SelectedItems[0].Tag;
            Dictionary<int, int> running = FindRunningInstances();
            if (!running.ContainsKey(index)) return;
            CloseProcesses(new List<int>(new int[] { running[index] }));
        }

        private void CloseAll()
        {
            Dictionary<int, int> running = FindRunningInstances();
            if (running.Count == 0)
            {
                MessageBox.Show(this, "现在没有正在运行的卡片。",
                    "自言卡 · 启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CloseProcesses(new List<int>(running.Values));
        }

        private void CloseProcesses(List<int> processIds)
        {
            foreach (int processId in processIds)
            {
                try
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                }
            }

            Timer delay = new Timer();
            delay.Interval = 1200;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                foreach (int processId in processIds)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(processId))
                        {
                            if (!process.HasExited) process.Kill();
                        }
                    }
                    catch
                    {
                    }
                }
                if (!IsDisposed) RefreshRunningState();
            };
            delay.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.OnFormClosed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveLauncherState();
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 让系统标题栏跟着走深色，不然上一条深色、标题条还是白的
            try
            {
                if (Environment.OSVersion.Version.Major < 10) return;
                int enabled = 1;
                int size = sizeof(int);
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, size) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, size);
            }
            catch
            {
                // 老系统不支持就保持系统默认
            }
        }

        // ---------------- Win32 ----------------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, int message,
            IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);

        // 跨进程读扩展样式没问题（写才受限），用来核对卡片窗口的真身状态
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLongW(IntPtr window, int index);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
            int widthEllipse, int heightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }
}
