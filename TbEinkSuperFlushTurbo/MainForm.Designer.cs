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
            lblPollInterval = new Label();
            trackPollInterval = new TrackBar();
            lblPollIntervalValue = new Label();
            lblPollIntervalUnit = new Label();
            lblToggleHotkey = new Label();
            txtToggleHotkey = new TextBox();
            btnToggleRecord = new Button();
            lblInfo = new Label();
            listBox = new ListBox();
            _trayIcon = new NotifyIcon(components);
            _displayChangeTimer = new System.Windows.Forms.Timer(components);
            contextMenuStrip1 = new ContextMenuStrip(components);
            exitToolStripMenuItem = new ToolStripMenuItem();
            ((System.ComponentModel.ISupportInitialize)trackPixelDelta).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackPollInterval).BeginInit();
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
            // lblPollInterval
            // 
            lblPollInterval.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPollInterval.Location = new Point(35, 123);
            lblPollInterval.Name = "lblPollInterval";
            lblPollInterval.Size = new Size(186, 50);
            lblPollInterval.TabIndex = 6;
            lblPollInterval.Text = "Detection Interval";
            lblPollInterval.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // trackPollInterval
            // 
            trackPollInterval.LargeChange = 500;
            trackPollInterval.Location = new Point(227, 128);
            trackPollInterval.Maximum = 5000;
            trackPollInterval.Minimum = 200;
            trackPollInterval.Name = "trackPollInterval";
            trackPollInterval.Size = new Size(300, 45);
            trackPollInterval.SmallChange = 50;
            trackPollInterval.TabIndex = 7;
            trackPollInterval.TickFrequency = 500;
            trackPollInterval.TickStyle = TickStyle.TopLeft;
            trackPollInterval.Value = 500;
            trackPollInterval.Scroll += trackPollInterval_Scroll;
            trackPollInterval.ValueChanged += trackPollInterval_ValueChanged;
            // 
            // lblPollIntervalValue
            // 
            lblPollIntervalValue.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPollIntervalValue.Location = new Point(532, 123);
            lblPollIntervalValue.Name = "lblPollIntervalValue";
            lblPollIntervalValue.Size = new Size(73, 50);
            lblPollIntervalValue.TabIndex = 8;
            lblPollIntervalValue.Text = "500";
            lblPollIntervalValue.TextAlign = ContentAlignment.MiddleCenter;
            lblPollIntervalValue.Click += lblPollIntervalValue_Click;
            // 
            // lblPollIntervalUnit
            // 
            lblPollIntervalUnit.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblPollIntervalUnit.Location = new Point(613, 123);
            lblPollIntervalUnit.Name = "lblPollIntervalUnit";
            lblPollIntervalUnit.Size = new Size(60, 50);
            lblPollIntervalUnit.TabIndex = 9;
            lblPollIntervalUnit.Text = "ms";
            lblPollIntervalUnit.TextAlign = ContentAlignment.MiddleCenter;
            lblPollIntervalUnit.Click += lblPollIntervalUnit_Click;
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
            btnToggleRecord.Location = new Point(558, 197);
            btnToggleRecord.Name = "btnToggleRecord";
            btnToggleRecord.Size = new Size(47, 45);
            btnToggleRecord.TabIndex = 12;
            btnToggleRecord.Text = "●";
            btnToggleRecord.TextAlign = ContentAlignment.MiddleRight;
            btnToggleRecord.UseVisualStyleBackColor = false;
            btnToggleRecord.Click += btnToggleRecord_Click;
            btnToggleRecord.Paint += btnToggleRecord_Paint;
            btnToggleRecord.MouseEnter += btnToggleRecord_MouseEnter;
            btnToggleRecord.MouseLeave += btnToggleRecord_MouseLeave;
            // 
            // lblInfo
            // 
            lblInfo.Font = new Font("Microsoft Sans Serif", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblInfo.Location = new Point(35, 252);
            lblInfo.Name = "lblInfo";
            lblInfo.Size = new Size(550, 32);
            lblInfo.TabIndex = 13;
            lblInfo.Text = "Status: Stopped";
            lblInfo.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // listBox
            // 
            listBox.Font = new Font("Microsoft Sans Serif", 12F);
            listBox.FormattingEnabled = true;
            listBox.ItemHeight = 20;
            listBox.Location = new Point(35, 301);
            listBox.Name = "listBox";
            listBox.Size = new Size(615, 184);
            listBox.TabIndex = 14;
            listBox.SelectedIndexChanged += listBox_SelectedIndexChanged;
            // 
            // _trayIcon
            // 
            _trayIcon.Text = "EInk Ghost Reducer";
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
            Controls.Add(listBox);
            Controls.Add(lblInfo);
            Controls.Add(btnToggleRecord);
            Controls.Add(txtToggleHotkey);
            Controls.Add(lblToggleHotkey);
            Controls.Add(lblPollIntervalUnit);
            Controls.Add(lblPollIntervalValue);
            Controls.Add(trackPollInterval);
            Controls.Add(lblPollInterval);
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
            ((System.ComponentModel.ISupportInitialize)trackPollInterval).EndInit();
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
        private System.Windows.Forms.Label lblPollInterval;
        private System.Windows.Forms.TrackBar trackPollInterval;
        private System.Windows.Forms.Label lblPollIntervalValue;
        private System.Windows.Forms.Label lblPollIntervalUnit;
        private System.Windows.Forms.Label lblToggleHotkey;
        private System.Windows.Forms.TextBox txtToggleHotkey;
        private System.Windows.Forms.Button btnToggleRecord;
        private System.Windows.Forms.Label lblInfo;
        private System.Windows.Forms.ListBox listBox;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.Timer _displayChangeTimer;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
    }
}