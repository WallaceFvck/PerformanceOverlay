<div align="center">

# PerformanceOverlay

### Monitore o desempenho do seu PC enquanto joga sem perder FPS.

**[🚀 CLIQUE AQUI PARA BAIXAR O INSTALADOR](https://github.com/WallaceFvck/PerformanceOverlay/releases/latest/download/PerformanceOverlay_Setup.exe)**
*(Link direto para a versão mais recente)*

---

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Anticheat Safe](https://img.shields.io/badge/Anticheat-Safe-4CAF50?style=flat-square)](#segurança-e-anticheat)

**Monitor de FPS, CPU, GPU e RAM direto no Kernel do Windows.**
**Seguro para jogos online • Super leve • Sem propagandas**

[Screenshots](#screenshots) • [Como funciona](#detalhes-técnicos) • [Segurança](#segurança-e-anticheat)

</div>

---

## 📥 Como baixar e usar (Passo a Passo)

1. **Baixe o Instalador:** [Clique aqui](https://github.com/WallaceFvck/PerformanceOverlay/releases/latest/download/PerformanceOverlay_Setup.exe) para baixar o arquivo `PerformanceOverlay_Setup.exe`.
2. **Instale:** Abra o arquivo baixado e siga as instruções na tela (é um instalador comum do Windows).
3. **Execute como Administrador:** Após instalar, clique com o botão direito no ícone do **PerformanceOverlay** na sua área de trabalho e escolha **"Executar como Administrador"** (Isso é obrigatório para que ele consiga ler a temperatura das peças).
4. **No Jogo:** O overlay aparecerá automaticamente no canto da tela.

### ⌨️ Comandos Rápidos
* **F8**: Esconde ou mostra o monitor.
* **Ctrl + F8**: Muda o monitor de lugar (canto superior esquerdo, direito, etc).
* **Shift + F8**: Alterna entre o modo detalhado e o modo compacto (só FPS).

---

## 📸 Screenshots

<div align="center">

| Visual no Jogo | Menu de Configurações |
|:---:|:---:|
| ![Full Mode](docs/screenshot-full.png) | ![Settings General](docs/screenshot-settings-general.png) |

</div>

---

## 🛡️ Segurança e Anticheat

Se você joga jogos como *Valorant, CS2, Warzone ou League of Legends*, pode usar o PerformanceOverlay sem medo. 

Diferente de outros programas (como o antigo Fraps), nós **não injetamos nada** dentro do seu jogo. O app apenas "escuta" o Windows dizer quando um frame foi desenhado. É uma leitura passiva, o que torna o app invisível para sistemas anticheat (Vanguard, Ricochet, EAC).

---

## ✨ O que ele monitora?

* **FPS Real:** Taxa de quadros precisa.
* **1% Low:** Mostra se o jogo está tendo aquelas "travadinhas" chatas.
* **CPU:** Uso total e temperatura.
* **GPU:** Uso da placa de vídeo, temperatura e memória VRAM.
* **RAM:** Quanto do seu sistema está sendo usado.
* **API:** Detecta se o jogo é DX11, DX12, Vulkan, etc.

---

## 🛠️ Detalhes para Técnicos

O PerformanceOverlay utiliza **ETW (Event Tracing for Windows)**. Usamos o provedor `Microsoft-Windows-D3D9` e `DXGI` para capturar eventos de *Present* sem a necessidade de hooks de renderização. O monitoramento de hardware é feito através da biblioteca `LibreHardwareMonitor`.

---

## 📄 Licença e Suporte

Este é um projeto de código aberto sob a licença **MIT**. 

Encontrou um erro? 
Verifique o arquivo de log em: `%APPDATA%\PerformanceOverlay\overlay.log` e me envie o erro aqui no GitHub.

---
<div align="center">
Gostou do projeto? Deixa uma ⭐ aqui no topo da página para ajudar!
</div>
