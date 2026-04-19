// ═══════════════════════════════════════════════════════════════════════════════
// App.xaml.cs — Entry point + System Tray (REVIEWED)
// ═══════════════════════════════════════════════════════════════════════════════
// REVIEW FIXES:
//   [CRITICAL] ETW cleanup via pipeline Dispose on exit/crash
//   [LOW]      Cache JsonSerializerOptions (avoids alloc per load/save)
//   [LOW]      Tray menu built once, rebuilt only on settings save
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using PerformanceOverlay.Localization;
using PerformanceOverlay.Models;
using PerformanceOverlay.Services;
using PerformanceOverlay.UI;

namespace PerformanceOverlay;

public partial class App : Application
{
    private TelemetryPipeline? _pipeline;
    private OverlayWindow? _overlayWindow;
    private OverlayConfig _config = new();
    private TaskbarIcon? _trayIcon;

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PerformanceOverlay");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    // [LOW] Cache JsonSerializerOptions — evita aloc por chamada
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Logging para ficheiro (suporte a utilizadores) ──
        InitializeFileLogging();

        if (!IsRunningAsAdmin())
        {
            MessageBox.Show(
                "PerformanceOverlay precisa de privilégios de administrador.\n\n" +
                "• ETW: captura de frames requer acesso ao kernel\n" +
                "• Sensores: leitura de MSR/SMBus requer ring0",
                "Elevação Necessária", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        _config = LoadConfig();

        // Inicializar localização (auto-detecta idioma ou usa o salvo)
        LocalizationManager.Initialize(_config.Language);

        try
        {
            _pipeline = new TelemetryPipeline(_config);
            await _pipeline.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao inicializar:\n\n{ex.Message}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _overlayWindow = new OverlayWindow();
        _overlayWindow.Initialize(_pipeline, _config);
        _overlayWindow.Show();

        CreateTrayIcon();

        Trace.WriteLine("[App] Ready.");

        DispatcherUnhandledException += (_, args) =>
        { Trace.WriteLine($"[App] UI Error: {args.Exception}"); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Trace.WriteLine($"[App] Domain Error: {args.ExceptionObject}");
    }

    // ═══ System Tray ═════════════════════════════════════════════════════

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = LoadIconFromResources(),
            ToolTipText = string.Format(Loc.S("tray_tooltip"), _config.ToggleVisibilityHotkey),
            ContextMenu = BuildDarkContextMenu()
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => OpenSettings();
        _trayIcon.ForceCreate();
    }

    private static System.Windows.Media.ImageSource LoadIconFromResources()
    {
        var uri = new Uri("pack://application:,,,/Resources/overlay.ico", UriKind.Absolute);
        return new System.Windows.Media.Imaging.BitmapImage(uri);
    }

    private ContextMenu BuildDarkContextMenu()
    {
        var bgColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
        var bgHover = Color.FromRgb(0x2A, 0x2A, 0x2A);
        var borderColor = Color.FromRgb(0x3A, 0x3A, 0x3A);
        var textColor = Color.FromRgb(0xE0, 0xE0, 0xE0);

        var bgBrush = new SolidColorBrush(bgColor); bgBrush.Freeze();
        var bgHoverBrush = new SolidColorBrush(bgHover); bgHoverBrush.Freeze();
        var borderBrush = new SolidColorBrush(borderColor); borderBrush.Freeze();
        var textBrush = new SolidColorBrush(textColor); textBrush.Freeze();
        var accentBrush = GetAccentBrush();

        var menu = new ContextMenu
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Foreground = textBrush,
            Padding = new Thickness(4),
            Style = BuildContextMenuStyle(bgBrush, borderBrush)
        };

        var header = BuildMenuItem("🎮  PerformanceOverlay", textBrush, bgBrush, bgHoverBrush, accentBrush);
        header.Click += (_, _) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/WallaceFvck/PerformanceOverlay", UseShellExecute = true }); } catch { } };
        menu.Items.Add(header);
        menu.Items.Add(new Separator { Background = borderBrush, Height = 1, Margin = new Thickness(8, 4, 8, 4), Opacity = 0.6 });

        var settings = BuildMenuItem(Loc.S("tray_settings"), textBrush, bgBrush, bgHoverBrush, accentBrush);
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
        menu.Items.Add(new Separator { Background = borderBrush, Height = 1, Margin = new Thickness(8, 4, 8, 4), Opacity = 0.6 });

        var exit = BuildMenuItem(Loc.S("tray_exit"), textBrush, bgBrush, bgHoverBrush, accentBrush);
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        return menu;
    }

    private SolidColorBrush GetAccentBrush()
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(_config.AccentColorHex);
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }
        catch { var b = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)); b.Freeze(); return b; }
    }

    private static MenuItem BuildMenuItem(string text,
        SolidColorBrush textBrush, SolidColorBrush bg, SolidColorBrush bgHover, SolidColorBrush accent)
    {
        var item = new MenuItem
        {
            Header = text, Foreground = textBrush, Background = bg,
            BorderThickness = new Thickness(0), Padding = new Thickness(14, 8, 14, 8),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var template = new ControlTemplate(typeof(MenuItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ItemBorder";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(MenuItem.BackgroundProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(MenuItem.PaddingProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, bgHover, "ItemBorder"));
        hoverTrigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, accent));
        template.Triggers.Add(hoverTrigger);

        item.Template = template;
        return item;
    }

    private static Style BuildContextMenuStyle(SolidColorBrush bg, SolidColorBrush border)
    {
        var style = new Style(typeof(ContextMenu));
        var template = new ControlTemplate(typeof(ContextMenu));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, bg);
        borderFactory.SetValue(Border.BorderBrushProperty, border);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));
        var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
        stackFactory.SetValue(StackPanel.IsItemsHostProperty, true);
        borderFactory.AppendChild(stackFactory);
        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(ContextMenu.TemplateProperty, template));
        return style;
    }

    // ═══ Actions ═════════════════════════════════════════════════════════

    private void OpenSettings()
    {
        if (_overlayWindow == null || _pipeline == null) return;
        var settingsWindow = new SettingsWindow(_config, _overlayWindow, _pipeline);
        settingsWindow.ShowDialog();
        if (settingsWindow.Saved)
        {
            SaveConfig(_config);
            if (_trayIcon != null)
                _trayIcon.ToolTipText = string.Format(Loc.S("tray_tooltip"), _config.ToggleVisibilityHotkey);
            _trayIcon!.ContextMenu = BuildDarkContextMenu();
        }
    }

    private void ExitApplication()
    {
        SaveConfig(_config);
        _trayIcon?.Dispose();
        _overlayWindow?.Close();
        _pipeline?.Dispose();
        Shutdown(0);
    }

    // ═══ Config ══════════════════════════════════════════════════════════

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _overlayWindow?.Close();
        _pipeline?.Dispose();
        base.OnExit(e);
    }

    private static OverlayConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<OverlayConfig>(json, JsonReadOptions);
                if (config != null) return config;
            }
        }
        catch (Exception ex) { Trace.WriteLine($"[App] Config load: {ex.Message}"); }
        var def = new OverlayConfig();
        SaveConfig(def);
        return def;
    }

    public static void SaveConfig(OverlayConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonWriteOptions));
        }
        catch (Exception ex) { Trace.WriteLine($"[App] Config save: {ex.Message}"); }
    }

    private static readonly string LogPath = Path.Combine(ConfigDir, "overlay.log");

    private static void InitializeFileLogging()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            // Rotação: se > 1MB, apaga e recria
            if (File.Exists(LogPath))
            {
                var info = new FileInfo(LogPath);
                if (info.Length > 1_048_576) // 1MB
                    File.Delete(LogPath);
            }

            var listener = new TextWriterTraceListener(LogPath)
            {
                TraceOutputOptions = TraceOptions.DateTime
            };
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;

            Trace.WriteLine("═══════════════════════════════════════════════════════");
            Trace.WriteLine($"  PerformanceOverlay v1.0 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Trace.WriteLine($"  OS: {Environment.OSVersion} | CLR: {Environment.Version}");
            Trace.WriteLine("═══════════════════════════════════════════════════════");
        }
        catch { /* Se logging falhar, app continua normalmente */ }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
