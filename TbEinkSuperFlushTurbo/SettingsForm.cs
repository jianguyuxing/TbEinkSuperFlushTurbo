using System;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;

namespace TbEinkSuperFlushTurbo
{
    public partial class SettingsForm : Form
    {
        private CheckBox checkBoxStopOver59Hz;
        private Button btnOK;
        private Button btnCancel;
        
        public bool StopOver59Hz { get; private set; }

        public SettingsForm(bool currentStopOver59Hz)
        {
            StopOver59Hz = currentStopOver59Hz;
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            checkBoxStopOver59Hz.Checked = StopOver59Hz;
        }

        private void btnOK_Click(object? sender, EventArgs e)
        {
            StopOver59Hz = checkBoxStopOver59Hz.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object? sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}