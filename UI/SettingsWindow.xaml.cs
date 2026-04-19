using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PerformanceOverlay.Localization;
using PerformanceOverlay.Models;
using PerformanceOverlay.Services;

namespace PerformanceOverlay.UI;

public partial class SettingsWindow : Window
{
    private readonly OverlayConfig _config;
    private readonly OverlayWindow _overlayWindow;
    private readonly TelemetryPipeline _pipeline;

    private readonly string _origAccent;
    private readonly double _origScale, _origOpacity;
    private readonly OverlayMode _origMode;
    private readonly string _origLang;
    private readonly string _origManualProcess;

    private Button? _hotkeyCapturing;
    private string _hotkeyOrigText = "";

    // Guard: impede SelectionChanged de disparar durante LoadProcessList()
    private bool _isLoadingProcesses;

    public bool Saved { get; private set; }

    public SettingsWindow(OverlayConfig config, OverlayWindow overlayWindow, TelemetryPipeline pipeline)
    {
        InitializeComponent();

        _config = config;
        _overlayWindow = overlayWindow;
        _pipeline = pipeline;

        _origAccent = config.AccentColorHex;
        _origScale = config.Scale;
        _origOpacity = config.Opacity;
        _origMode = config.Mode;
        _origLang = config.Language;
        _origManualProcess = config.ManualProcessName;

        BtnHotkeyToggle.Content = config.ToggleVisibilityHotkey;
        BtnHotkeyPosition.Content = config.CyclePositionHotkey;
        BtnHotkeyMode.Content = config.ToggleModeHotkey;
        TxtAccentHex.Text = config.AccentColorHex;
        SliderScale.Value = config.Scale;
        SliderOpacity.Value = config.Opacity;

        foreach (ComboBoxItem item in CbxInitialMode.Items)
            if (item.Tag is string tag && Enum.TryParse<OverlayMode>(tag, out var m) && m == config.Mode)
            { item.IsSelected = true; break; }

        PopulateLanguageSelector();

        // Carregar lista de processos e selecionar o salvo
        LoadProcessList();
        SelectProcessByName(config.ManualProcessName);

        // Wiring APÓS a inicialização (evita disparos prematuros)
        CbxProcesses.SelectionChanged += OnProcessSelectionChanged;
        CbxProcesses.DropDownOpened += OnProcessDropDownOpened;

        ApplyLocalization();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Localização
    // ═══════════════════════════════════════════════════════════════════════

    private void PopulateLanguageSelector()
    {
        CbxLanguage.Items.Clear();
        int sel = 0;
        for (int i = 0; i < LocalizationManager.SupportedLanguages.Length; i++)
        {
            var (code, display) = LocalizationManager.SupportedLanguages[i];
            CbxLanguage.Items.Add(new ComboBoxItem { Content = display, Tag = code });
            if (code == _config.Language) sel = i;
        }
        CbxLanguage.SelectedIndex = sel;
        CbxLanguage.SelectionChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CbxLanguage.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            LocalizationManager.SetLanguage(code);
            ApplyLocalization();
        }
    }

    private void ApplyLocalization()
    {
        Title = Loc.S("settings_title");
        TabGeneral.Header = Loc.S("tab_general");
        TabAppearance.Header = Loc.S("tab_appearance");

        TxtManualTitle.Text = Loc.S("manual_monitoring");
        TxtManualDesc.Text = Loc.S("manual_desc");
        TxtManualHint.Text = Loc.S("manual_hint");

        TxtHotkeysTitle.Text = Loc.S("hotkeys_title");
        TxtHotkeysDesc.Text = Loc.S("hotkeys_desc");
        TxtHkToggleLabel.Text = Loc.S("hotkey_toggle");
        TxtHkPositionLabel.Text = Loc.S("hotkey_position");
        TxtHkModeLabel.Text = Loc.S("hotkey_mode");

        TxtLanguageTitle.Text = Loc.S("language_title");

        TxtAccentTitle.Text = Loc.S("accent_color");
        TxtHexLabel.Text = Loc.S("hex_label");
        TxtScaleTitle.Text = Loc.S("scale_title");
        TxtOpacityTitle.Text = Loc.S("opacity_title");

        TxtInitialMode.Text = Loc.S("initial_mode");
        CbxModeFullItem.Content = Loc.S("mode_full");
        CbxModeCompactItem.Content = Loc.S("mode_compact");

        BtnSave.Content = Loc.S("btn_save");
        BtnCancel.Content = Loc.S("btn_cancel");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Process Picker
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seleciona o ComboBoxItem cujo Tag == processName.
    /// Se não encontrar, põe o texto no campo editável.
    /// </summary>
    private void SelectProcessByName(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            if (CbxProcesses.Items.Count > 0)
                CbxProcesses.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < CbxProcesses.Items.Count; i++)
        {
            if (CbxProcesses.Items[i] is ComboBoxItem item &&
                item.Tag is string tag &&
                tag.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                CbxProcesses.SelectedIndex = i;
                Trace.WriteLine($"[Settings] Process selected: '{processName}' (index {i})");
                return;
            }
        }

        // Processo salvo não está em execução — selecionar auto
        if (CbxProcesses.Items.Count > 0)
            CbxProcesses.SelectedIndex = 0;
        Trace.WriteLine($"[Settings] Process '{processName}' not running — defaulting to auto");
    }

    /// <summary>Auto-refresh quando o dropdown abre.</summary>
    private void OnProcessDropDownOpened(object? sender, EventArgs e)
    {
        string current = GetSelectedProcessName();
        LoadProcessList();
        SelectProcessByName(current);
    }

    /// <summary>
    /// Feedback visual imediato + ativa monitoramento ao selecionar.
    /// Guard: ignorado durante LoadProcessList() que dispara Clear → SelectionChanged(null).
    /// </summary>
    private void OnProcessSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProcesses) return;

        string name = GetSelectedProcessName();

        if (!string.IsNullOrEmpty(name))
        {
            // Feedback: mostrar título da janela do processo selecionado
            string title = "";
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                    try { title = procs[0].MainWindowTitle; } catch { }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Settings] Process lookup failed for '{name}': {ex.Message}");
            }
            finally
            {
                if (procs != null) foreach (var p in procs) p.Dispose();
            }

            TxtProcessFeedback.Text = !string.IsNullOrWhiteSpace(title)
                ? $"✓ {title}"
                : $"✓ {name}";
            TxtProcessFeedback.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));

            // Ativar monitoramento imediatamente (sem esperar Save)
            _pipeline.SetManualProcess(name);
            Trace.WriteLine($"[Settings] Manual process activated: '{name}' (title: '{title}')");
        }
        else
        {
            // "— Auto —" selecionado → limpar processo manual
            TxtProcessFeedback.Text = "";
            _pipeline.SetManualProcess("");
            Trace.WriteLine("[Settings] Process selection cleared → auto mode");
        }
    }

    private void LoadProcessList()
    {
        _isLoadingProcesses = true;
        Process[]? allProcs = null;
        try
        {
            allProcs = Process.GetProcesses();
            var items = new List<(string Name, string Title)>();

            foreach (var p in allProcs)
            {
                try
                {
                    // Filtro: apenas processos com título de janela não-vazio
                    // MainWindowTitle é mais confiável que MainWindowHandle
                    // porque não lança Win32Exception em processos protegidos
                    string title = p.MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;

                    string name = p.ProcessName;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Dedup por nome
                    if (!items.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        items.Add((name, title));
                }
                catch { /* processo protegido — ignorar */ }
            }

            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            CbxProcesses.Items.Clear();
            // Primeira opção vazia = auto-detecção
            CbxProcesses.Items.Add(new ComboBoxItem { Content = "", Tag = "" });

            foreach (var (name, title) in items)
            {
                CbxProcesses.Items.Add(new ComboBoxItem
                {
                    Content = $"{name} — {Truncate(title, 35)}",
                    Tag = name
                });
            }

            Trace.WriteLine($"[Settings] Process list: {items.Count} windowed processes found");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Settings] LoadProcessList FAILED: {ex.Message}");
        }
        finally
        {
            if (allProcs != null)
                foreach (var p in allProcs) try { p.Dispose(); } catch { }
            _isLoadingProcesses = false;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private void OnRefreshProcesses(object? sender, RoutedEventArgs e)
    {
        string current = GetSelectedProcessName();
        LoadProcessList();
        SelectProcessByName(current);
    }

    private string GetSelectedProcessName()
    {
        if (CbxProcesses.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return "";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hotkey Picker
    // ═══════════════════════════════════════════════════════════════════════

    private void OnHotkeyButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (_hotkeyCapturing != null && _hotkeyCapturing != btn)
            _hotkeyCapturing.Content = _hotkeyOrigText;
        _hotkeyCapturing = btn;
        _hotkeyOrigText = btn.Content?.ToString() ?? "";
        btn.Content = "...";
        btn.Focus();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (_hotkeyCapturing == null || sender != _hotkeyCapturing) return;
        if (e.Key == Key.Escape) { _hotkeyCapturing.Content = _hotkeyOrigText; _hotkeyCapturing = null; e.Handled = true; return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System) { e.Handled = true; return; }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        string kn = KeyToString(e.Key);
        if (string.IsNullOrEmpty(kn)) { e.Handled = true; return; }
        parts.Add(kn);
        string hk = string.Join("+", parts);
        if (!HotkeyService.TryParse(hk, out _, out _)) { TxtHotkeyStatus.Text = $"❌ '{hk}'"; e.Handled = true; return; }

        _hotkeyCapturing.Content = hk;
        _hotkeyCapturing = null;
        TxtHotkeyStatus.Text = "";
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnHotkeyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_hotkeyCapturing != null && _hotkeyCapturing == sender)
        {
            _hotkeyCapturing.Content = _hotkeyOrigText;
            _hotkeyCapturing = null;
        }
    }

    private static string KeyToString(Key key)
    {
        if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (key - Key.NumPad0);
        return key switch { Key.Space => "Space", Key.Enter or Key.Return => "Enter", Key.Tab => "Tab",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Insert => "Insert", Key.Delete => "Delete", _ => "" };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Color / Sliders
    // ═══════════════════════════════════════════════════════════════════════

    private void OnColorPick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Background is SolidColorBrush brush)
        {
            string hex = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
            TxtAccentHex.Text = hex;
            _overlayWindow.ApplyTheme(hex);
        }
    }

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        string hex = TxtAccentHex.Text.Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        try { ColorConverter.ConvertFromString(hex); _overlayWindow.ApplyTheme(hex); } catch { }
    }

    private void OnScaleChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtScaleValue == null) return;
        TxtScaleValue.Text = $"{e.NewValue:F1}x";
        _overlayWindow?.ApplyScaleAndOpacity(e.NewValue, SliderOpacity?.Value ?? 0.92);
    }

    private void OnOpacityChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOpacityValue == null) return;
        TxtOpacityValue.Text = $"{(int)(e.NewValue * 100)}%";
        _overlayWindow?.ApplyScaleAndOpacity(SliderScale?.Value ?? 1.0, e.NewValue);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Save / Cancel
    // ═══════════════════════════════════════════════════════════════════════

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        string toggleHk = BtnHotkeyToggle.Content?.ToString()?.Trim() ?? "F8";
        string posHk = BtnHotkeyPosition.Content?.ToString()?.Trim() ?? "Ctrl+F8";
        string modeHk = BtnHotkeyMode.Content?.ToString()?.Trim() ?? "Shift+F8";

        if (!HotkeyService.TryParse(toggleHk, out _, out _)) { TxtHotkeyStatus.Text = $"❌ '{toggleHk}'"; return; }
        if (!HotkeyService.TryParse(posHk, out _, out _)) { TxtHotkeyStatus.Text = $"❌ '{posHk}'"; return; }
        if (!HotkeyService.TryParse(modeHk, out _, out _)) { TxtHotkeyStatus.Text = $"❌ '{modeHk}'"; return; }

        _config.ManualProcessName = GetSelectedProcessName();
        _config.ToggleVisibilityHotkey = toggleHk;
        _config.CyclePositionHotkey = posHk;
        _config.ToggleModeHotkey = modeHk;

        string hex = TxtAccentHex.Text.Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        try { ColorConverter.ConvertFromString(hex); _config.AccentColorHex = hex; }
        catch { _config.AccentColorHex = "#E53935"; }

        _config.Scale = SliderScale.Value;
        _config.Opacity = SliderOpacity.Value;

        if (CbxInitialMode.SelectedItem is ComboBoxItem mi && mi.Tag is string mt && Enum.TryParse<OverlayMode>(mt, out var nm))
        { _config.Mode = nm; _overlayWindow.ApplyMode(nm); }

        if (CbxLanguage.SelectedItem is ComboBoxItem li && li.Tag is string lc)
            _config.Language = lc;

        _overlayWindow.ApplyTheme(_config.AccentColorHex);
        _overlayWindow.ApplyScaleAndOpacity(_config.Scale, _config.Opacity);
        _overlayWindow.ReapplyHotkeys(_config.ToggleVisibilityHotkey, _config.CyclePositionHotkey, _config.ToggleModeHotkey);
        _pipeline.SetManualProcess(_config.ManualProcessName);

        Trace.WriteLine($"[Settings] SAVED — Process: '{_config.ManualProcessName}', " +
            $"Hotkeys: {_config.ToggleVisibilityHotkey}/{_config.CyclePositionHotkey}/{_config.ToggleModeHotkey}, " +
            $"Lang: {_config.Language}, Scale: {_config.Scale:F1}, Opacity: {_config.Opacity:F2}");

        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        // Reverter TUDO ao estado original
        _overlayWindow.ApplyTheme(_origAccent);
        _overlayWindow.ApplyScaleAndOpacity(_origScale, _origOpacity);
        _overlayWindow.ApplyMode(_origMode);

        if (_config.Language != _origLang)
            LocalizationManager.SetLanguage(_origLang);

        // Reverter processo manual ao valor ORIGINAL (não ao config que pode ter sido alterado)
        _pipeline.SetManualProcess(_origManualProcess);

        Trace.WriteLine($"[Settings] CANCELLED — reverted to process: '{_origManualProcess}'");

        Saved = false;
        Close();
    }
}
