// ═══════════════════════════════════════════════════════════════════════════════
// LocalizationManager.cs — Sistema de localização multi-idioma
// ═══════════════════════════════════════════════════════════════════════════════
// Idiomas: PT-BR, EN, ES, FR, DE, ZH
// Fallback: EN (sempre disponível, hardcoded como base)
// Detecção: CultureInfo.CurrentUICulture → mapeamento automático
// Runtime: troca dinâmica via evento LanguageChanged
// ═══════════════════════════════════════════════════════════════════════════════

using System.Globalization;

namespace PerformanceOverlay.Localization;

public static class LocalizationManager
{
    // ── Idiomas suportados ───────────────────────────────────────────────
    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    {
        ("auto", "🌐 Automático (Sistema)"),
        ("en",   "🇬🇧 English"),
        ("pt",   "🇧🇷 Português"),
        ("es",   "🇪🇸 Español"),
        ("fr",   "🇫🇷 Français"),
        ("de",   "🇩🇪 Deutsch"),
        ("zh",   "🇨🇳 中文"),
    };

    private static string _currentLanguage = "en";
    private static Dictionary<string, string> _currentStrings = new();
    private static readonly Dictionary<string, string> _fallback = BuildEnglish();

    /// <summary>Disparado quando o idioma muda. UI deve re-ler todas as strings.</summary>
    public static event Action? LanguageChanged;

    public static string CurrentLanguage => _currentLanguage;

    /// <summary>
    /// Inicializa com detecção automática ou idioma explícito.
    /// Chamar uma vez no startup (App.xaml.cs).
    /// </summary>
    public static void Initialize(string languageCode = "auto")
    {
        SetLanguage(languageCode);
    }

    /// <summary>
    /// Troca o idioma em runtime. UI é notificada via LanguageChanged.
    /// </summary>
    public static void SetLanguage(string code)
    {
        if (code == "auto")
            code = DetectSystemLanguage();

        _currentLanguage = code;
        _currentStrings = code switch
        {
            "pt" => BuildPortuguese(),
            "es" => BuildSpanish(),
            "fr" => BuildFrench(),
            "de" => BuildGerman(),
            "zh" => BuildChinese(),
            _    => BuildEnglish()
        };

        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Obtém string localizada. Fallback para EN se chave não existir.
    /// Uso: Loc.Get("fps_label") → "FPS" ou "Quadros/s" etc.
    /// </summary>
    public static string Get(string key)
    {
        if (_currentStrings.TryGetValue(key, out var value))
            return value;
        if (_fallback.TryGetValue(key, out var fallback))
            return fallback;
        return $"[{key}]"; // Debug: chave não traduzida aparece visível
    }

    /// <summary>Atalho: Loc.Get(key)</summary>
    public static string S(string key) => Get(key);

    private static string DetectSystemLanguage()
    {
        string culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return culture switch
        {
            "pt" => "pt",
            "es" => "es",
            "fr" => "fr",
            "de" => "de",
            "zh" => "zh",
            _    => "en"
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dicionários de tradução (inline — evita dependência de ficheiros JSON)
    // ═══════════════════════════════════════════════════════════════════════

    private static Dictionary<string, string> BuildEnglish() => new()
    {
        // ── Overlay ──
        ["fps_label"] = "fps",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "Frametime",
        ["frametime_ms"] = "ms",
        ["driver_label"] = "Driver",
        ["process_prefix"] = "▶",

        // ── Sensores ──
        ["cpu_label"] = "CPU",
        ["gpu_label"] = "GPU",
        ["ram_label"] = "RAM",
        ["vram_label"] = "VRAM",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "MB",

        // ── Settings Window ──
        ["settings_title"] = "PerformanceOverlay — Settings",
        ["tab_general"] = "  General  ",
        ["tab_appearance"] = "  Appearance  ",

        // ── Tab Geral ──
        ["manual_monitoring"] = "Manual Monitoring",
        ["manual_desc"] = "Select an active process to force capture:",
        ["manual_hint"] = "Leave empty for automatic detection by window focus.",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "Hotkeys",
        ["hotkeys_desc"] = "Click the button and press the desired key combination.",
        ["hotkey_toggle"] = "Show/Hide:",
        ["hotkey_position"] = "Change Position:",
        ["hotkey_mode"] = "Compact / Full Mode:",

        ["initial_mode"] = "Initial Mode",
        ["mode_full"] = "Full (all sensors)",
        ["mode_compact"] = "Compact (FPS + 1% Low only)",

        ["language_title"] = "Language",

        // ── Tab Aparência ──
        ["accent_color"] = "Accent Color",
        ["hex_label"] = "Hex:",
        ["scale_title"] = "Interface Size",
        ["opacity_title"] = "Background Opacity",

        // ── Botões ──
        ["btn_save"] = "Save",
        ["btn_cancel"] = "Cancel",

        // ── Tray ──
        ["tray_settings"] = "⚙  Settings",
        ["tray_exit"] = "✕  Exit",
        ["tray_tooltip"] = "PerformanceOverlay — {0} to toggle",

        // ── API Display ──
        ["api_na"] = "N/A",

        // ── Estados ──
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };

    private static Dictionary<string, string> BuildPortuguese() => new()
    {
        ["fps_label"] = "fps",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "Frametime",
        ["frametime_ms"] = "ms",
        ["driver_label"] = "Driver",
        ["process_prefix"] = "▶",

        ["cpu_label"] = "CPU",
        ["gpu_label"] = "GPU",
        ["ram_label"] = "RAM",
        ["vram_label"] = "VRAM",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "MB",

        ["settings_title"] = "PerformanceOverlay — Configurações",
        ["tab_general"] = "  Geral  ",
        ["tab_appearance"] = "  Aparência  ",

        ["manual_monitoring"] = "Monitoramento Manual",
        ["manual_desc"] = "Selecione um processo ativo para forçar a captura:",
        ["manual_hint"] = "Deixe vazio para detecção automática pelo foco de janela.",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "Atalhos (Hotkeys)",
        ["hotkeys_desc"] = "Clique no botão e pressione a combinação desejada.",
        ["hotkey_toggle"] = "Mostrar/Esconder:",
        ["hotkey_position"] = "Mudar Posição:",
        ["hotkey_mode"] = "Modo Compacto / Full:",

        ["initial_mode"] = "Modo Inicial",
        ["mode_full"] = "Full (todos os sensores)",
        ["mode_compact"] = "Compacto (só FPS + 1% Low)",

        ["language_title"] = "Idioma",

        ["accent_color"] = "Cor de Destaque",
        ["hex_label"] = "Hex:",
        ["scale_title"] = "Tamanho da Interface",
        ["opacity_title"] = "Opacidade do Fundo",

        ["btn_save"] = "Salvar",
        ["btn_cancel"] = "Cancelar",

        ["tray_settings"] = "⚙  Configurações",
        ["tray_exit"] = "✕  Sair",
        ["tray_tooltip"] = "PerformanceOverlay — {0} para toggle",

        ["api_na"] = "N/A",
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };

    private static Dictionary<string, string> BuildSpanish() => new()
    {
        ["fps_label"] = "fps",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "Frametime",
        ["frametime_ms"] = "ms",
        ["driver_label"] = "Driver",
        ["process_prefix"] = "▶",

        ["cpu_label"] = "CPU",
        ["gpu_label"] = "GPU",
        ["ram_label"] = "RAM",
        ["vram_label"] = "VRAM",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "MB",

        ["settings_title"] = "PerformanceOverlay — Configuración",
        ["tab_general"] = "  General  ",
        ["tab_appearance"] = "  Apariencia  ",

        ["manual_monitoring"] = "Monitoreo Manual",
        ["manual_desc"] = "Seleccione un proceso activo para forzar la captura:",
        ["manual_hint"] = "Deje vacío para detección automática por foco de ventana.",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "Atajos de Teclado",
        ["hotkeys_desc"] = "Haga clic en el botón y presione la combinación deseada.",
        ["hotkey_toggle"] = "Mostrar/Ocultar:",
        ["hotkey_position"] = "Cambiar Posición:",
        ["hotkey_mode"] = "Modo Compacto / Completo:",

        ["initial_mode"] = "Modo Inicial",
        ["mode_full"] = "Completo (todos los sensores)",
        ["mode_compact"] = "Compacto (solo FPS + 1% Low)",

        ["language_title"] = "Idioma",

        ["accent_color"] = "Color de Acento",
        ["hex_label"] = "Hex:",
        ["scale_title"] = "Tamaño de Interfaz",
        ["opacity_title"] = "Opacidad del Fondo",

        ["btn_save"] = "Guardar",
        ["btn_cancel"] = "Cancelar",

        ["tray_settings"] = "⚙  Configuración",
        ["tray_exit"] = "✕  Salir",
        ["tray_tooltip"] = "PerformanceOverlay — {0} para alternar",

        ["api_na"] = "N/A",
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };

    private static Dictionary<string, string> BuildFrench() => new()
    {
        ["fps_label"] = "ips",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "Frametime",
        ["frametime_ms"] = "ms",
        ["driver_label"] = "Pilote",
        ["process_prefix"] = "▶",

        ["cpu_label"] = "CPU",
        ["gpu_label"] = "GPU",
        ["ram_label"] = "RAM",
        ["vram_label"] = "VRAM",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "Mo",

        ["settings_title"] = "PerformanceOverlay — Paramètres",
        ["tab_general"] = "  Général  ",
        ["tab_appearance"] = "  Apparence  ",

        ["manual_monitoring"] = "Surveillance Manuelle",
        ["manual_desc"] = "Sélectionnez un processus actif pour forcer la capture :",
        ["manual_hint"] = "Laissez vide pour la détection automatique par fenêtre active.",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "Raccourcis Clavier",
        ["hotkeys_desc"] = "Cliquez sur le bouton et appuyez sur la combinaison souhaitée.",
        ["hotkey_toggle"] = "Afficher/Masquer :",
        ["hotkey_position"] = "Changer Position :",
        ["hotkey_mode"] = "Mode Compact / Complet :",

        ["initial_mode"] = "Mode Initial",
        ["mode_full"] = "Complet (tous les capteurs)",
        ["mode_compact"] = "Compact (FPS + 1% Low uniquement)",

        ["language_title"] = "Langue",

        ["accent_color"] = "Couleur d'Accentuation",
        ["hex_label"] = "Hex :",
        ["scale_title"] = "Taille de l'Interface",
        ["opacity_title"] = "Opacité du Fond",

        ["btn_save"] = "Enregistrer",
        ["btn_cancel"] = "Annuler",

        ["tray_settings"] = "⚙  Paramètres",
        ["tray_exit"] = "✕  Quitter",
        ["tray_tooltip"] = "PerformanceOverlay — {0} pour basculer",

        ["api_na"] = "N/A",
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };

    private static Dictionary<string, string> BuildGerman() => new()
    {
        ["fps_label"] = "fps",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "Frametime",
        ["frametime_ms"] = "ms",
        ["driver_label"] = "Treiber",
        ["process_prefix"] = "▶",

        ["cpu_label"] = "CPU",
        ["gpu_label"] = "GPU",
        ["ram_label"] = "RAM",
        ["vram_label"] = "VRAM",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "MB",

        ["settings_title"] = "PerformanceOverlay — Einstellungen",
        ["tab_general"] = "  Allgemein  ",
        ["tab_appearance"] = "  Erscheinungsbild  ",

        ["manual_monitoring"] = "Manuelle Überwachung",
        ["manual_desc"] = "Wählen Sie einen aktiven Prozess zur Erfassung:",
        ["manual_hint"] = "Leer lassen für automatische Erkennung durch Fensterfokus.",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "Tastenkürzel",
        ["hotkeys_desc"] = "Klicken Sie auf die Schaltfläche und drücken Sie die gewünschte Taste.",
        ["hotkey_toggle"] = "Ein-/Ausblenden:",
        ["hotkey_position"] = "Position Ändern:",
        ["hotkey_mode"] = "Kompakt / Vollmodus:",

        ["initial_mode"] = "Anfangsmodus",
        ["mode_full"] = "Vollständig (alle Sensoren)",
        ["mode_compact"] = "Kompakt (nur FPS + 1% Low)",

        ["language_title"] = "Sprache",

        ["accent_color"] = "Akzentfarbe",
        ["hex_label"] = "Hex:",
        ["scale_title"] = "Schnittstellengröße",
        ["opacity_title"] = "Hintergrund-Opazität",

        ["btn_save"] = "Speichern",
        ["btn_cancel"] = "Abbrechen",

        ["tray_settings"] = "⚙  Einstellungen",
        ["tray_exit"] = "✕  Beenden",
        ["tray_tooltip"] = "PerformanceOverlay — {0} zum Umschalten",

        ["api_na"] = "N/A",
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };

    private static Dictionary<string, string> BuildChinese() => new()
    {
        ["fps_label"] = "帧",
        ["fps_1low"] = "1%",
        ["frametime_label"] = "帧时间",
        ["frametime_ms"] = "毫秒",
        ["driver_label"] = "驱动",
        ["process_prefix"] = "▶",

        ["cpu_label"] = "处理器",
        ["gpu_label"] = "显卡",
        ["ram_label"] = "内存",
        ["vram_label"] = "显存",
        ["usage_suffix"] = "%",
        ["temp_suffix"] = "°C",
        ["power_suffix"] = "W",
        ["clock_suffix"] = "MHz",
        ["memory_suffix"] = "MB",

        ["settings_title"] = "PerformanceOverlay — 设置",
        ["tab_general"] = "  常规  ",
        ["tab_appearance"] = "  外观  ",

        ["manual_monitoring"] = "手动监控",
        ["manual_desc"] = "选择一个活动进程以强制捕获：",
        ["manual_hint"] = "留空以通过窗口焦点自动检测。",
        ["refresh_btn"] = "↻",

        ["hotkeys_title"] = "快捷键",
        ["hotkeys_desc"] = "点击按钮并按下所需的组合键。",
        ["hotkey_toggle"] = "显示/隐藏：",
        ["hotkey_position"] = "更改位置：",
        ["hotkey_mode"] = "紧凑/完整模式：",

        ["initial_mode"] = "初始模式",
        ["mode_full"] = "完整（所有传感器）",
        ["mode_compact"] = "紧凑（仅 FPS + 1% Low）",

        ["language_title"] = "语言",

        ["accent_color"] = "强调色",
        ["hex_label"] = "十六进制：",
        ["scale_title"] = "界面大小",
        ["opacity_title"] = "背景不透明度",

        ["btn_save"] = "保存",
        ["btn_cancel"] = "取消",

        ["tray_settings"] = "⚙  设置",
        ["tray_exit"] = "✕  退出",
        ["tray_tooltip"] = "PerformanceOverlay — {0} 切换",

        ["api_na"] = "N/A",
        ["no_data"] = "---",
        ["no_temp"] = "--°C",
        ["no_power"] = "--W",
        ["no_frametime"] = "--- ms",
    };
}

/// <summary>Atalho global: Loc.S("key")</summary>
public static class Loc
{
    public static string S(string key) => LocalizationManager.Get(key);
}
