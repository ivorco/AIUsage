using AIUsage.Core;

namespace AIUsage.UI;

/// <summary>
/// Edits provider settings. Secret fields are write-only: the dialog shows whether a value is saved,
/// lets you replace or remove it, and never reads it back.
/// </summary>
sealed class SettingsForm : Form
{
    sealed class Editor
    {
        public required IUsageProvider Provider;
        public required SettingField Field;
        public required TextBox Box;
        public bool RemoveRequested;
    }

    readonly List<Editor> editors = [];

    public SettingsForm(IReadOnlyList<IUsageProvider> providers)
    {
        Text = "AI Usage settings";
        Icon = TrayIconRenderer.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Font = new Font(Theme.UiFont, 9f);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        int row = 0;
        void AddSpanning(Control control)
        {
            layout.Controls.Add(control, 0, row++);
            layout.SetColumnSpan(control, 3);
        }

        AddSpanning(new Label
        {
            Text = "Keys are stored in Windows Credential Manager, encrypted for your Windows account. " +
                   "A saved key can be replaced or removed here, but never viewed. " +
                   "Leave fields empty to use the logins of Claude Code, Cursor and Codex on this PC.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 0, 0, 6),
        });

        foreach (var provider in providers)
        {
            AddSpanning(new Label
            {
                Text = provider.DisplayName,
                AutoSize = true,
                Font = new Font(Theme.UiFontSemibold, 11f),
                ForeColor = Theme.Orange,
                Margin = new Padding(0, 14, 0, 4),
            });

            foreach (var field in provider.Settings)
            {
                bool saved = CredentialStore.Exists(provider.Id, field.Key);
                var box = new TextBox
                {
                    Width = 330,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Theme.Surface,
                    ForeColor = Theme.Text,
                    UseSystemPasswordChar = field.IsSecret,
                    PlaceholderText = field.IsSecret ? (saved ? "Saved · paste a new value to replace" : "Paste to save") : "",
                    Margin = new Padding(0, 3, 8, 0),
                };
                if (!field.IsSecret)
                    box.Text = CredentialStore.Read(provider.Id, field.Key) ?? "";

                var editor = new Editor { Provider = provider, Field = field, Box = box };
                editors.Add(editor);

                var status = new Label
                {
                    Text = field.IsSecret && saved ? "Saved. " + field.Help : field.Help,
                    AutoSize = true,
                    MaximumSize = new Size(460, 0),
                    ForeColor = field.IsSecret && saved ? Theme.PaleOrange : Theme.Faint,
                    Margin = new Padding(0, 2, 0, 8),
                };

                layout.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 14, 0) }, 0, row);
                layout.Controls.Add(box, 1, row);
                if (field.IsSecret)
                {
                    var remove = CreateButton("Remove", primary: false);
                    remove.Enabled = saved;
                    remove.Click += (_, _) =>
                    {
                        editor.RemoveRequested = true;
                        box.Clear();
                        remove.Enabled = false;
                        status.Text = "Will be removed when you save.";
                        status.ForeColor = Theme.Error;
                    };
                    box.TextChanged += (_, _) =>
                    {
                        if (box.TextLength > 0)
                            editor.RemoveRequested = false;
                    };
                    layout.Controls.Add(remove, 2, row);
                }
                row++;
                layout.Controls.Add(status, 1, row);
                layout.SetColumnSpan(status, 2);
                row++;
            }
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        var save = CreateButton("Save", primary: true);
        save.Click += (_, _) => Save();
        var cancel = CreateButton("Cancel", primary: false);
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AddSpanning(buttons);

        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkChrome(Handle);
    }

    void Save()
    {
        foreach (var editor in editors)
        {
            var value = editor.Box.Text.Trim();
            if (value.Length > 0 && editor.Field.Validate?.Invoke(value) is { } error)
            {
                MessageBox.Show(this, $"{editor.Provider.DisplayName} · {editor.Field.Label}: {error}", "AI Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                editor.Box.Focus();
                return;
            }
        }

        foreach (var editor in editors)
        {
            var value = editor.Box.Text.Trim();
            if (value.Length > 0)
                CredentialStore.Write(editor.Provider.Id, editor.Field.Key, value);
            else if (editor.RemoveRequested || !editor.Field.IsSecret)
                CredentialStore.Delete(editor.Provider.Id, editor.Field.Key);
            editor.Box.Clear();
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Theme.Orange : Theme.Surface,
            ForeColor = primary ? Theme.Background : Theme.Text,
            Padding = new Padding(12, 3, 12, 3),
            Margin = new Padding(8, 2, 0, 0),
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Orange : Theme.Divider;
        return button;
    }
}
