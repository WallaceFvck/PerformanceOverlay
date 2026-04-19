// ═══════════════════════════════════════════════════════════════════════════════
// HotkeyService.cs — Serviço de hotkeys globais com re-registro dinâmico
// ═══════════════════════════════════════════════════════════════════════════════
// Permite que a SettingsWindow altere hotkeys em runtime: o serviço faz
// Unregister da tecla antiga e Register da nova sem precisar reiniciar a app.
//
// Formato aceito: "F8", "Ctrl+F8", "Shift+F1", "Ctrl+Shift+F12", "Alt+F10".
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using PerformanceOverlay.Helpers;

namespace PerformanceOverlay.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<int, (uint mod, uint vk)> _registered = new();
    private int _nextId = 1;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>
    /// Registra uma hotkey a partir de string ("F8", "Ctrl+F12", etc.).
    /// Retorna o ID da hotkey (usado internamente) ou -1 se falhou.
    /// </summary>
    public int Register(string hotkeyString, Action handler)
    {
        if (!TryParse(hotkeyString, out uint mod, out uint vk))
        {
            Trace.WriteLine($"[Hotkey] Parse failed for '{hotkeyString}'");
            return -1;
        }

        int id = _nextId++;
        bool ok = NativeInterop.RegisterHotKey(_hwnd, id, mod | NativeInterop.MOD_NOREPEAT, vk);

        if (!ok)
        {
            // Retry sem NOREPEAT (alguns cenários bloqueiam isso)
            ok = NativeInterop.RegisterHotKey(_hwnd, id, mod, vk);
        }

        if (!ok)
        {
            Trace.WriteLine($"[Hotkey] RegisterHotKey FAILED for '{hotkeyString}' (mod={mod:X}, vk={vk:X})");
            return -1;
        }

        _handlers[id] = handler;
        _registered[id] = (mod, vk);
        Trace.WriteLine($"[Hotkey] Registered '{hotkeyString}' as ID {id} (mod={mod:X}, vk={vk:X})");
        return id;
    }

    /// <summary>Remove e re-registra uma hotkey no mesmo ID com nova combinação.</summary>
    public bool Reassign(int id, string newHotkeyString, Action newHandler)
    {
        if (_registered.ContainsKey(id))
        {
            NativeInterop.UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
            _handlers.Remove(id);
        }

        if (!TryParse(newHotkeyString, out uint mod, out uint vk))
        {
            Trace.WriteLine($"[Hotkey] Reassign parse failed for '{newHotkeyString}'");
            return false;
        }

        bool ok = NativeInterop.RegisterHotKey(_hwnd, id, mod | NativeInterop.MOD_NOREPEAT, vk);
        if (!ok) ok = NativeInterop.RegisterHotKey(_hwnd, id, mod, vk);

        if (!ok)
        {
            Trace.WriteLine($"[Hotkey] Reassign RegisterHotKey FAILED for '{newHotkeyString}'");
            return false;
        }

        _handlers[id] = newHandler;
        _registered[id] = (mod, vk);
        Trace.WriteLine($"[Hotkey] Reassigned ID {id} → '{newHotkeyString}'");
        return true;
    }

    /// <summary>Chamado pelo WndProc quando recebe WM_HOTKEY.</summary>
    public bool Invoke(int id)
    {
        if (_handlers.TryGetValue(id, out var handler))
        {
            handler();
            return true;
        }
        return false;
    }

    /// <summary>Parse de "Ctrl+Shift+F8" → (MOD_CONTROL|MOD_SHIFT, VK_F8)</summary>
    public static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        // Últimas partes devem ser o modificador, a última é a tecla
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeInterop.MOD_CONTROL; break;
                case "SHIFT":
                    modifiers |= NativeInterop.MOD_SHIFT; break;
                case "ALT":
                    modifiers |= NativeInterop.MOD_ALT; break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeInterop.MOD_WIN; break;
                default:
                    return false;
            }
        }

        // Última parte: a tecla (F1-F24, A-Z, 0-9)
        string keyName = parts[^1].ToUpperInvariant();
        vk = KeyNameToVk(keyName);
        return vk != 0;
    }

    private static uint KeyNameToVk(string name)
    {
        // F-keys: F1=0x70 ... F24=0x87
        if (name.StartsWith("F") && int.TryParse(name[1..], out int fNum) && fNum >= 1 && fNum <= 24)
            return (uint)(0x70 + fNum - 1);

        // A-Z: VK = ASCII
        if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
            return (uint)name[0];

        // 0-9: VK = ASCII
        if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
            return (uint)name[0];

        // Teclas especiais comuns
        return name switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            _ => 0
        };
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys.ToList())
        {
            NativeInterop.UnregisterHotKey(_hwnd, id);
        }
        _registered.Clear();
        _handlers.Clear();
    }
}
