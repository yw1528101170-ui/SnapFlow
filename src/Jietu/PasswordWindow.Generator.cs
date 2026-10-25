using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Jietu;

/// <summary>PasswordWindow 的「密码生成器 / 动态验证码（TOTP）」部分（partial）。</summary>
public sealed partial class PasswordWindow
{
    private void OnToggleGenerator(object sender, RoutedEventArgs e)
    {
        var show = GeneratorPanel.Visibility != Visibility.Visible;
        GeneratorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SyncLayout();
        if (show)
        {
            Generate();
            DetailPane.ChangeView(null, 0, null, true);
        }
        else if (!_editorVisible)
        {
            // 没在编辑条目时收起详情区，回到「选择条目」引导。
            RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
        }
    }

    private void OnGeneratorOptionChanged(object sender, RoutedEventArgs e) => Regenerate();

    private void OnGeneratorLengthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Regenerate();

    private void Regenerate()
    {
        if (!_xamlReady)
        {
            return;
        }

        var phrase = GenPassphrase.IsChecked == true;
        GenWordsBox.Visibility = phrase ? Visibility.Visible : Visibility.Collapsed;
        GenLengthBox.IsEnabled = !phrase;
        if (GeneratorPanel.Visibility == Visibility.Visible)
        {
            Generate();
        }
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e) => Generate();

    private void Generate()
    {
        if (GenPassphrase.IsChecked == true)
        {
            var words = Math.Clamp((int)GenWordsBox.Value, PasswordGenerator.MinWords, PasswordGenerator.MaxWords);
            GenOutput.Text = PasswordGenerator.GeneratePassphrase(words);
            // 熵值模板化：bit 数由实际词表规模实算（7776 词 × 6 词 ≈ 77 bit），不写死常量。
            GenEntropyText.Text = PasswordGenerator.PassphraseEntropyLabel(words);
            return;
        }

        var lower = GenLower.IsChecked == true;
        var upper = GenUpper.IsChecked == true;
        var digits = GenDigits.IsChecked == true;
        var symbols = GenSymbols.IsChecked == true;
        if (!lower && !upper && !digits && !symbols)
        {
            GenOutput.Text = "";
            GenEntropyText.Text = "至少选择一个字符类";
            return;
        }

        var length = Math.Clamp((int)GenLengthBox.Value, PasswordGenerator.MinLength, PasswordGenerator.MaxLength);
        GenOutput.Text = PasswordGenerator.Generate(length, lower, upper, digits, symbols);
        var bits = PasswordGenerator.EstimateEntropyBits(length, lower, upper, digits, symbols);
        GenEntropyText.Text = $"约 {bits:F0} bit 熵";
    }

    private void OnCopyGenerated(object sender, RoutedEventArgs e) => CopySecret(GenOutput.Text, "生成的密码");

    private void OnUseGenerated(object sender, RoutedEventArgs e)
    {
        if (GenOutput.Text.Length == 0)
        {
            return;
        }

        // 没有打开任何条目时，「使用」直接把生成结果落进一条新草稿（少一步 Ctrl+N）。
        if (!_editorVisible)
        {
            OnNewEntry(this, new RoutedEventArgs());
        }

        // 密码只属于登录条目：note 模式下填入自动切回登录。
        KindCombo.SelectedIndex = 0;
        HideReveal();
        _editorLoading = true;
        PasswordBox.Password = GenOutput.Text;
        PasswordTextBox.Text = GenOutput.Text;
        _editorLoading = false;
        UnsavedBadge.Visibility = Visibility.Visible;
        GenOutput.Text = "";
        _host.Toast.Show("已填入密码（尚未保存）");
    }

    private void StartTotpTimer()
    {
        if (_totpTimer != null)
        {
            return;
        }

        _totpTimer = Root.DispatcherQueue.CreateTimer();
        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += (_, _) => UpdateTotpDisplay();
        _totpTimer.Start();
    }

    private void StopTotpTimer() => _totpTimer?.Stop();

    private void UpdateTotpDisplay()
    {
        if (_selectedId == null || !_vault.IsUnlocked)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry == null || entry.TotpSecret.Length == 0)
        {
            TotpCode.Text = "------";
            TotpRing.Value = 0;
            return;
        }

        try
        {
            var remaining = Totp.RemainingSeconds();
            TotpCode.Text = Totp.Compute(entry.TotpSecret) + "  (" + remaining + "s)";
            TotpRing.Value = remaining;
        }
        catch
        {
            TotpCode.Text = "密钥无效";
            TotpRing.Value = 0;
        }
    }

    private void OnCopyTotp(object sender, RoutedEventArgs e)
    {
        if (_selectedId == null)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry?.TotpSecret.Length > 0)
        {
            try
            {
                CopySecret(Totp.Compute(entry.TotpSecret), "验证码");
            }
            catch
            {
                _host.Toast.Show("TOTP 密钥无效");
            }
        }
    }
}
