using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DeskBoxes
{
    /// <summary>自动整理的确认对话框：先把分类结果摊开给人看，再决定动不移动。</summary>
    public class OrganizeForm : Form
    {
        public bool IncludeNonShortcuts;
        public bool Confirmed;

        private readonly List<AutoOrganizer.PlanItem> _plan;
        private CheckBox _chkAll;

        public OrganizeForm(List<AutoOrganizer.PlanItem> plan)
        {
            _plan = plan;

            Text = "自动整理桌面";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 430);
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 251, 253);
            TopMost = true;

            Dictionary<string, List<AutoOrganizer.PlanItem>> groups = AutoOrganizer.Group(plan);

            // ---- 标题说明 ----
            Label head = new Label();
            head.AutoSize = false;
            head.Location = new Point(16, 12);
            head.Size = new Size(528, 38);
            head.Text = "将把 " + plan.Count + " 个项目分成 " + groups.Count + " 个分区，并把桌面上的它们移走。";
            head.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            head.ForeColor = Color.FromArgb(45, 52, 60);
            Controls.Add(head);

            // ---- 预览列表 ----
            ListView lv = new ListView();
            lv.Location = new Point(16, 52);
            lv.Size = new Size(528, 258);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = Color.White;
            lv.Columns.Add("分区", 110);
            lv.Columns.Add("数量", 50, HorizontalAlignment.Right);
            lv.Columns.Add("内容（前几个）", 350);
            Controls.Add(lv);

            foreach (KeyValuePair<string, List<AutoOrganizer.PlanItem>> kv in groups)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < kv.Value.Count && i < 5; i++)
                    names.Add(Path.GetFileNameWithoutExtension(kv.Value[i].Path));
                string preview = string.Join("、", names.ToArray());
                if (kv.Value.Count > 5) preview += " 等";

                ListViewItem it = new ListViewItem(kv.Key);
                it.SubItems.Add(kv.Value.Count.ToString());
                it.SubItems.Add(preview);
                lv.Items.Add(it);
            }

            // ---- 选项 ----
            _chkAll = new CheckBox();
            _chkAll.Text = "连 .exe 或其他文件一起整理（默认只动 .lnk / .url 快捷方式）";
            _chkAll.Location = new Point(16, 322);
            _chkAll.AutoSize = true;
            _chkAll.Checked = false;
            Controls.Add(_chkAll);

            // ---- 说明 ----
            Label note = new Label();
            note.AutoSize = false;
            note.Location = new Point(16, 348);
            note.Size = new Size(528, 34);
            note.ForeColor = Color.FromArgb(120, 126, 134);
            note.Text = "文件会移动到 " + AutoOrganizer.VaultDir + " 下，桌面不再保留。\r\n随时可以在托盘菜单「还原本地快捷方式」全部移回桌面。";
            Controls.Add(note);

            // ---- 按钮 ----
            Button ok = new Button();
            ok.Text = "开始整理";
            ok.Location = new Point(352, 392);
            ok.Size = new Size(94, 28);
            ok.Click += delegate
            {
                Confirmed = true;
                IncludeNonShortcuts = _chkAll.Checked;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(454, 392);
            cancel.Size = new Size(90, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
