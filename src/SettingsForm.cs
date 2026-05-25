namespace UniversalSpellCheck;

internal sealed class SettingsForm : Form
{
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticsLogger _logger;
    private readonly TextBox _apiKeyTextBox = new();
    private readonly Label _statusLabel = new();

    public SettingsForm(SettingsStore settingsStore, DiagnosticsLogger logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;

        Text = "Universal Spell Check Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 170);

        var apiKeyLabel = new Label
        {
            AutoSize = true,
            Text = "OpenAI API key",
            Location = new Point(16, 18)
        };

        _apiKeyTextBox.Location = new Point(16, 44);
        _apiKeyTextBox.Width = 488;
        _apiKeyTextBox.UseSystemPasswordChar = true;
        _apiKeyTextBox.PlaceholderText = _settingsStore.HasApiKey()
            ? "API key saved. Enter a new key to replace it."
            : "sk-...";

        var saveButton = new Button
        {
            Text = "Save",
            Location = new Point(348, 92),
            Size = new Size(75, 30)
        };
        saveButton.Click += (_, _) => SaveApiKey();

        var closeButton = new Button
        {
            Text = "Close",
            Location = new Point(429, 92),
            Size = new Size(75, 30)
        };
        closeButton.Click += (_, _) => Close();

        _statusLabel.AutoSize = false;
        _statusLabel.Location = new Point(16, 130);
        _statusLabel.Size = new Size(488, 24);

        Controls.Add(apiKeyLabel);
        Controls.Add(_apiKeyTextBox);
        Controls.Add(saveButton);
        Controls.Add(closeButton);
        Controls.Add(_statusLabel);
    }

    private void SaveApiKey()
    {
        var apiKey = _apiKeyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _statusLabel.Text = "Enter an API key before saving.";
            return;
        }

        try
        {
            _settingsStore.SaveApiKey(apiKey);
            _apiKeyTextBox.Clear();
            _apiKeyTextBox.PlaceholderText = "API key saved. Enter a new key to replace it.";
            _statusLabel.Text = "Saved.";
            _logger.Log("apikey_saved");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Save failed.";
            _logger.Log($"apikey_save_failed error=\"{ex.Message}\"");
        }
    }
}
