namespace TbEinkSuperFlushTurbo
{
    partial class MainForm
    {
    
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            btnStart = new Button();
            btnStop = new Button();
            lblPixelDelta = new Label();
            trackPixelDelta = new TrackBar();
            lblPixelDeltaValue = new Label();
            btnHelpPixelDelta = new Button();
            lblTileSize = new Label();
            trackTileSize = new TrackBar();
            lblTileSizeValue = new Label();
            lblTileSizeUnit = new Label();
            lblToggleHotkey = new Label();
            txtToggleHotkey = new TextBox();
            btnToggleRecord = new Button();
            panelBottom = new Panel();
            listBox = new ListBox();
            lblInfo = new Label();
            lblDisplay = new Label();
            comboDisplay = new ComboBox();
            btnSettings = new Button();
            _trayIcon = new NotifyIcon(components);
            _displayChangeTimer = new System.Windows.Forms.Timer(components);
            contextMenuStrip1 = new ContextMenuStrip(components);
            exitToolStripMenuItem = new ToolStripMenuItem();
            ((System.ComponentModel.ISupportInitialize)trackPixelDelta).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackTileSize).BeginInit();
            panelBottom.SuspendLayout();
            contextMenuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // btnStart
            // 
            btnStart.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Bold, GraphicsUnit.Point, 134);
            btnStart.Location = new Point(120, 12);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(160, 50);
            btnStart.TabIndex = 0;
            btnStart.Text = "Start";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // btnStop
            // 
            btnStop.Enabled = false;
            btnStop.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Bold, GraphicsUnit.Point, 134);
            btnStop.Location = new Point(332, 12);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(160, 50);
            btnStop.TabIndex = 1;
            btnStop.Text = "Stop";
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // lblPixelDelta
            // 
            lblPixelDelta.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPixelDelta.Location = new Point(37, 73);
            lblPixelDelta.Name = "lblPixelDelta";
            lblPixelDelta.Size = new Size(186, 50);
            lblPixelDelta.TabIndex = 2;
            lblPixelDelta.Text = "Pixel Color Diff";
            lblPixelDelta.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // trackPixelDelta
            // 
            trackPixelDelta.Location = new Point(227, 78);
            trackPixelDelta.Maximum = 25;
            trackPixelDelta.Minimum = 2;
            trackPixelDelta.Name = "trackPixelDelta";
            trackPixelDelta.Size = new Size(302, 45);
            trackPixelDelta.TabIndex = 3;
            trackPixelDelta.TickStyle = TickStyle.TopLeft;
            trackPixelDelta.Value = 10;
            trackPixelDelta.ValueChanged += trackPixelDelta_ValueChanged;
            // 
            // lblPixelDeltaValue
            // 
            lblPixelDeltaValue.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPixelDeltaValue.Location = new Point(532, 73);
            lblPixelDeltaValue.Name = "lblPixelDeltaValue";
            lblPixelDeltaValue.Size = new Size(73, 50);
            lblPixelDeltaValue.TabIndex = 4;
            lblPixelDeltaValue.Text = "10";
            lblPixelDeltaValue.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // btnHelpPixelDelta
            // 
            btnHelpPixelDelta.BackColor = Color.LightBlue;
            btnHelpPixelDelta.FlatStyle = FlatStyle.Flat;
            btnHelpPixelDelta.Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point, 0);
            btnHelpPixelDelta.Location = new Point(624, 82);
            btnHelpPixelDelta.Name = "btnHelpPixelDelta";
            btnHelpPixelDelta.Size = new Size(26, 27);
            btnHelpPixelDelta.TabIndex = 5;
            btnHelpPixelDelta.Text = "?";
            btnHelpPixelDelta.TextAlign = ContentAlignment.TopCenter;
            btnHelpPixelDelta.UseVisualStyleBackColor = false;
            btnHelpPixelDelta.Click += btnHelpPixelDelta_Click;
            btnHelpPixelDelta.Paint += btnHelpPixelDelta_Paint;
            btnHelpPixelDelta.MouseEnter += btnHelpPixelDelta_MouseEnter;
            btnHelpPixelDelta.MouseLeave += btnHelpPixelDelta_MouseLeave;
            // 
            // lblTileSize
            // 
            lblTileSize.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblTileSize.Location = new Point(35, 123);
            lblTileSize.Name = "lblTileSize";
            lblTileSize.Size = new Size(186, 50);
            lblTileSize.TabIndex = 6;
            lblTileSize.Text = "Detect Tile Pixel Size";
            lblTileSize.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // trackTileSize
            // 
            trackTileSize.LargeChange = 4;
            trackTileSize.Location = new Point(227, 128);
            trackTileSize.Maximum = 32;
            trackTileSize.Minimum = 8;
            trackTileSize.Name = "trackTileSize";
            trackTileSize.Size = new Size(300, 45);
            trackTileSize.SmallChange = 1;
            trackTileSize.TabIndex = 7;
            trackTileSize.TickFrequency = 4;
            trackTileSize.TickStyle = TickStyle.TopLeft;
            trackTileSize.Value = 16;
            trackTileSize.Scroll += trackTileSize_Scroll;
            trackTileSize.ValueChanged += trackTileSize_ValueChanged;
            // 
            // lblTileSizeValue
            // 
            lblTileSizeValue.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblTileSizeValue.Location = new Point(532, 123);
            lblTileSizeValue.Name = "lblTileSizeValue";
            lblTileSizeValue.Size = new Size(73, 50);
            lblTileSizeValue.TabIndex = 8;
            lblTileSizeValue.Text = "16";
            lblTileSizeValue.TextAlign = ContentAlignment.MiddleCenter;
            lblTileSizeValue.Click += lblTileSizeValue_Click;
            // 
            // lblTileSizeUnit
            // 
            lblTileSizeUnit.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblTileSizeUnit.Location = new Point(613, 123);
            lblTileSizeUnit.Name = "lblTileSizeUnit";
            lblTileSizeUnit.Size = new Size(60, 50);
            lblTileSizeUnit.TabIndex = 9;
            lblTileSizeUnit.Text = "px";
            lblTileSizeUnit.TextAlign = ContentAlignment.MiddleCenter;
            lblTileSizeUnit.Click += lblTileSizeUnit_Click;
            // 
            // lblToggleHotkey
            // 
            lblToggleHotkey.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblToggleHotkey.Location = new Point(35, 192);
            lblToggleHotkey.Name = "lblToggleHotkey";
            lblToggleHotkey.Size = new Size(186, 50);
            lblToggleHotkey.TabIndex = 10;
            lblToggleHotkey.Text = "Toggle Switch Hotkey";
            lblToggleHotkey.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // txtToggleHotkey
            // 
            txtToggleHotkey.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtToggleHotkey.Location = new Point(227, 204);
            txtToggleHotkey.Name = "txtToggleHotkey";
            txtToggleHotkey.ReadOnly = true;
            txtToggleHotkey.Size = new Size(300, 26);
            txtToggleHotkey.TabIndex = 11;
            txtToggleHotkey.Text = "F6";
            // 
            // btnToggleRecord
            // 
            btnToggleRecord.BackColor = Color.White;
            btnToggleRecord.Cursor = Cursors.Hand;
            btnToggleRecord.FlatStyle = FlatStyle.Flat;
            btnToggleRecord.Font = new Font("Microsoft Sans Serif", 22F, FontStyle.Bold);
            btnToggleRecord.ForeColor = Color.Red;
            btnToggleRecord.ImageAlign = ContentAlignment.MiddleRight;
            btnToggleRecord.Location = new Point(573, 204);
            btnToggleRecord.Name = "btnToggleRecord";
            btnToggleRecord.Size = new Size(43, 38);
            btnToggleRecord.TabIndex = 12;
            btnToggleRecord.Text = "●";
            btnToggleRecord.TextAlign = ContentAlignment.MiddleRight;
            btnToggleRecord.UseVisualStyleBackColor = false;
            btnToggleRecord.Click += btnToggleRecord_Click;
            btnToggleRecord.Paint += btnToggleRecord_Paint;
            btnToggleRecord.MouseEnter += btnToggleRecord_MouseEnter;
            btnToggleRecord.MouseLeave += btnToggleRecord_MouseLeave;
            // 
            // panelBottom
            // 
            panelBottom.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            panelBottom.Controls.Add(listBox);
            panelBottom.Controls.Add(lblInfo);
            panelBottom.Location = new Point(35, 308);
            panelBottom.Name = "panelBottom";
            panelBottom.Size = new Size(615, 216);
            panelBottom.TabIndex = 15;
            // 
            // listBox
            // 
            listBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            listBox.Font = new Font("Microsoft Sans Serif", 12F);
            listBox.FormattingEnabled = true;
            listBox.ItemHeight = 20;
            listBox.Location = new Point(0, 30);
            listBox.Name = "listBox";
            listBox.Size = new Size(615, 144);
            listBox.TabIndex = 1;
            listBox.SelectedIndexChanged += listBox_SelectedIndexChanged;
            // 
            // lblInfo
            // 
            lblInfo.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblInfo.Location = new Point(0, 0);
            lblInfo.Name = "lblInfo";
            lblInfo.Size = new Size(615, 20);
            lblInfo.TabIndex = 0;
            lblInfo.Text = "Status: Stopped";
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblInfo.TextChanged += lblInfo_TextChanged;
            // 
            // lblDisplay
            // 
            lblDisplay.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblDisplay.Location = new Point(35, 248);
            lblDisplay.Name = "lblDisplay";
            lblDisplay.Size = new Size(186, 50);
            lblDisplay.TabIndex = 13;
            lblDisplay.Text = "Display Selection:";
            lblDisplay.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // comboDisplay
            // 
            comboDisplay.DropDownStyle = ComboBoxStyle.DropDownList;
            comboDisplay.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            comboDisplay.FormattingEnabled = true;
            comboDisplay.Location = new Point(227, 260);
            comboDisplay.Name = "comboDisplay";
            comboDisplay.Size = new Size(340, 28);
            comboDisplay.TabIndex = 14;
            comboDisplay.SelectedIndexChanged += comboDisplay_SelectedIndexChanged;
            // 
            // btnSettings
            // 
            btnSettings.BackColor = Color.White;
            btnSettings.Cursor = Cursors.Hand;
            btnSettings.FlatStyle = FlatStyle.Flat;
            btnSettings.Font = new Font("Microsoft Sans Serif", 16F);
            btnSettings.ForeColor = SystemColors.ControlText;
            btnSettings.Location = new Point(573, 251);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(43, 40);
            btnSettings.TabIndex = 16;
            btnSettings.Text = "⚙";
            btnSettings.UseVisualStyleBackColor = false;
            btnSettings.Click += btnSettings_Click;
            btnSettings.MouseEnter += btnSettings_MouseEnter;
            btnSettings.MouseLeave += btnSettings_MouseLeave;
            // 
            // _trayIcon
            // 
            _trayIcon.Text = "Eink Ghost Reducer";
            // 
            // _displayChangeTimer
            // 
            _displayChangeTimer.Interval = 2000;
            _displayChangeTimer.Tick += _displayChangeTimer_Tick;
            // 
            // contextMenuStrip1
            // 
            contextMenuStrip1.ImageScalingSize = new Size(20, 20);
            contextMenuStrip1.Items.AddRange(new ToolStripItem[] { exitToolStripMenuItem });
            contextMenuStrip1.Name = "contextMenuStrip1";
            contextMenuStrip1.Size = new Size(97, 26);
            // 
            // exitToolStripMenuItem
            // 
            exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            exitToolStripMenuItem.Size = new Size(96, 22);
            exitToolStripMenuItem.Text = "Exit";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(686, 509);
            Controls.Add(btnSettings);
            Controls.Add(comboDisplay);
            Controls.Add(lblDisplay);
            Controls.Add(panelBottom);
            Controls.Add(btnToggleRecord);
            Controls.Add(txtToggleHotkey);
            Controls.Add(lblToggleHotkey);
            Controls.Add(lblTileSizeUnit);
            Controls.Add(lblTileSizeValue);
            Controls.Add(trackTileSize);
            Controls.Add(lblTileSize);
            Controls.Add(btnHelpPixelDelta);
            Controls.Add(lblPixelDeltaValue);
            Controls.Add(trackPixelDelta);
            Controls.Add(lblPixelDelta);
            Controls.Add(btnStop);
            Controls.Add(btnStart);
            DoubleBuffered = true;
            Font = new Font("Microsoft Sans Serif", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            KeyPreview = true;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "TbEink Super Flush Turbo";
            Load += MainForm_Load;
            KeyDown += MainForm_KeyDown;
            Resize += MainForm_Resize;
            ((System.ComponentModel.ISupportInitialize)trackPixelDelta).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackTileSize).EndInit();
            panelBottom.ResumeLayout(false);
            contextMenuStrip1.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnStart;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Label lblPixelDelta;
        private System.Windows.Forms.TrackBar trackPixelDelta;
        private System.Windows.Forms.Label lblPixelDeltaValue;
        private System.Windows.Forms.Button btnHelpPixelDelta;
        private System.Windows.Forms.Label lblTileSize;
        private System.Windows.Forms.TrackBar trackTileSize;
        private System.Windows.Forms.Label lblTileSizeValue;
        private System.Windows.Forms.Label lblTileSizeUnit;
        private System.Windows.Forms.Label lblToggleHotkey;
        private System.Windows.Forms.TextBox txtToggleHotkey;
        private System.Windows.Forms.Button btnToggleRecord;
        private System.Windows.Forms.Panel panelBottom;
        private System.Windows.Forms.Label lblInfo;
        private System.Windows.Forms.ListBox listBox;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.Timer _displayChangeTimer;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.Windows.Forms.Label lblDisplay;
        private System.Windows.Forms.ComboBox comboDisplay;
        private System.Windows.Forms.Button btnSettings;
    }
}