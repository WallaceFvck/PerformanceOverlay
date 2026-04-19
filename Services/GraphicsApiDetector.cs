// ═══════════════════════════════════════════════════════════════════════════════
// GraphicsApiDetector.cs — Detecção de API com prioridade estrita
// ═══════════════════════════════════════════════════════════════════════════════
//
// REGRAS DE PRIORIDADE:
//   DX12 > Vulkan > DX11 > DX9 > OpenGL
//
// PROBLEMA ANTERIOR: opengl32.dll é carregada pelo Windows em TODOS os
// processos que usam GDI (até o Notepad a tem). Os drivers GL da GPU
// (atio6axx.dll, nvoglv64.dll) também são carregados pelo sistema.
// Resultado: RE4 (DX12) era detectado como "OpenGL" porque tinha
// opengl32.dll + atio6axx.dll carregados.
//
// SOLUÇÃO: OpenGL só é reportado se:
//   1. LWJGL está carregado (Minecraft garantido), OU
//   2. É processo Java + opengl32.dll + SEM DX12/DX11/Vulkan, OU
//   3. Nenhuma API moderna detectada e opengl32.dll está presente
//
// O Refine() agora dá prioridade absoluta ao ETW para DX12/DX11/D3D9
// (se DXGI provider emitiu eventos, é DX garantido) e só aceita OpenGL
// dos módulos (ETW não sabe distinguir OpenGL de "genérico DxgKrnl").
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PerformanceOverlay.Models;

namespace PerformanceOverlay.Services;

public static class GraphicsApiDetector
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(
        IntPtr hProcess, [Out] IntPtr[] lphModule, int cb,
        out int lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameExW(
        IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint LIST_MODULES_ALL = 0x03;
    private const uint LIST_MODULES_32BIT = 0x01;

    public static GraphicsApi Detect(int processId)
    {
        if (processId <= 0) return GraphicsApi.Unknown;
        IntPtr hProcess = IntPtr.Zero;

        try
        {
            hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
            if (hProcess == IntPtr.Zero) return GraphicsApi.Unknown;

            IsWow64Process(hProcess, out bool isWow64);
            uint filterFlag = isWow64 ? LIST_MODULES_32BIT : LIST_MODULES_ALL;

            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(hProcess, modules, modules.Length * IntPtr.Size,
                    out int bytesNeeded, filterFlag))
            {
                if (!EnumProcessModulesEx(hProcess, modules, modules.Length * IntPtr.Size,
                        out bytesNeeded, LIST_MODULES_ALL))
                    return GraphicsApi.Unknown;
            }

            int moduleCount = bytesNeeded / IntPtr.Size;
            var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(260);
            string? processExeName = null;

            for (int i = 0; i < moduleCount && i < modules.Length; i++)
            {
                if (modules[i] == IntPtr.Zero) continue;
                sb.Clear();
                if (GetModuleFileNameExW(hProcess, modules[i], sb, sb.Capacity) == 0) continue;
                string name = System.IO.Path.GetFileName(sb.ToString());
                moduleNames.Add(name);
                if (i == 0) processExeName = name;
            }

            var api = ClassifyFromModules(moduleNames, processExeName);
            Trace.WriteLine($"[ApiDetect] PID {processId}: {api} ({moduleCount} modules, exe={processExeName})");
            return api;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ApiDetect] Error: {ex.Message}");
            return GraphicsApi.Unknown;
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private static GraphicsApi ClassifyFromModules(HashSet<string> m, string? exeName)
    {
        // ── Detectar presença de cada API ────────────────────────────────
        bool hasDx12 = m.Contains("d3d12.dll") || m.Contains("d3d12core.dll");

        bool hasVulkan = m.Contains("vulkan-1.dll")
            || m.Contains("amdvlk64.dll") || m.Contains("amdvlk32.dll");

        bool hasDx11 = m.Contains("d3d11.dll");

        bool hasDx9 = m.Contains("d3d9.dll")
            || m.Contains("d3dx9_43.dll") || m.Contains("d3dx9_42.dll")
            || m.Contains("d3dx9_41.dll") || m.Contains("d3dx9_40.dll")
            || m.Contains("d3dx9_31.dll");

        // ── OpenGL: detecção CONSERVADORA ────────────────────────────────
        // opengl32.dll está presente em quase todos os processos (GDI loader).
        // Só consideramos OpenGL REAL se houver evidência forte:

        // LWJGL = Minecraft/apps Java com rendering GL (certeza absoluta)
        bool hasLwjgl = m.Any(n => n.StartsWith("lwjgl", StringComparison.OrdinalIgnoreCase))
            || m.Contains("glfw.dll") || m.Contains("glfw3.dll");

        bool isJavaProcess = exeName != null && (
            exeName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains("minecraft", StringComparison.OrdinalIgnoreCase));

        // OpenGL confirmado = LWJGL presente, ou Java sem nenhuma API moderna
        bool confirmedOpenGL = hasLwjgl
            || (isJavaProcess && !hasDx12 && !hasDx11 && !hasVulkan);

        // ── PRIORIDADE ESTRITA: DX12 > Vulkan > DX11 > DX9 > OpenGL ────
        //
        // Se qualquer API moderna está presente, opengl32.dll é IGNORADA.
        // Isto resolve o RE4 (DX12 + opengl32.dll carregada pelo sistema).

        if (hasDx12) return GraphicsApi.DirectX12;
        if (hasVulkan) return GraphicsApi.Vulkan;
        if (hasDx11) return GraphicsApi.DirectX11;
        if (hasDx9) return GraphicsApi.DirectX9;
        if (confirmedOpenGL) return GraphicsApi.OpenGL;

        // Fallback: nenhuma API moderna e opengl32.dll presente = provavelmente GL
        if (m.Contains("opengl32.dll") && !hasDx12 && !hasDx11 && !hasVulkan && !hasDx9)
            return GraphicsApi.OpenGL;

        return GraphicsApi.Unknown;
    }

    /// <summary>
    /// Combina detecção por módulos com detecção por ETW events.
    ///
    /// REGRAS:
    /// - ETW diz "DirectX" (DXGI/D3D9 runtime ativo) → confirma família DX.
    ///   Módulos definem a versão exata (DX12 vs DX11 vs D3D9).
    /// - ETW diz Unknown (só DxgKrnl, sem DXGI) → módulos decidem tudo.
    ///   Se módulos dizem OpenGL → é OpenGL real (Minecraft).
    ///   Se módulos dizem DX12 mas ETW é Unknown → processo pode estar
    ///   loading. Confiar nos módulos.
    /// </summary>
    public static GraphicsApi Refine(GraphicsApi fromModules, GraphicsApi fromEtw)
    {
        // ETW confirmou que DXGI/D3D9 runtime está ativo → é DirectX.
        // Mas ETW não sabe a versão exata (DXGI serve DX11 e DX12).
        // Módulos sabem: d3d12.dll = DX12, d3d11.dll = DX11.
        bool etwConfirmsDx = fromEtw == GraphicsApi.DirectX12
            || fromEtw == GraphicsApi.DirectX11
            || fromEtw == GraphicsApi.DirectX9;

        if (etwConfirmsDx)
        {
            // Módulos têm a versão específica → usar módulos
            if (fromModules == GraphicsApi.DirectX12 ||
                fromModules == GraphicsApi.DirectX11 ||
                fromModules == GraphicsApi.DirectX9 ||
                fromModules == GraphicsApi.Vulkan)
                return fromModules;

            // Módulos não detectaram DX específico → usar ETW como está
            return fromEtw;
        }

        // ETW não confirmou DX (Unknown = só DxgKrnl ou sem eventos)
        // → confiar nos módulos para tudo (incluindo OpenGL)
        if (fromModules != GraphicsApi.Unknown)
            return fromModules;

        return fromEtw;
    }

    public static string GetApiDisplayName(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX9 => "D3D9",
        GraphicsApi.DirectX11 => "D3D11",
        GraphicsApi.DirectX12 => "D3D12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "N/A"
    };
}
