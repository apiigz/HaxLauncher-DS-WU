using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Launcher_Haxball_DS_WU
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string HomeUrl = "https://www.haxball.com/play";

        private static readonly string DataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaxLauncher");

        private static readonly string SettingsFile = System.IO.Path.Combine(DataDir, "settings.json");

        // Dominios publicitarios a bloquear (el dominio y todos sus subdominios).
        private static readonly string[] BlockedDomains = { "cpmstar.com", "rubiconproject.com" };

        private static bool IsBlockedUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
            var host = u.Host.ToLowerInvariant();
            return BlockedDomains.Any(d => host == d || host.EndsWith("." + d));
        }

        // ------------------------------------------------------------------
        // Configuración del usuario (se guarda en settings.json)
        // ------------------------------------------------------------------
        private sealed class AppSettings
        {
            /// <summary>Tope de FPS objetivo. Es el valor que hay que ajustar según la PC.</summary>
            public int Cap { get; set; } = 1000;

            /// <summary>Tope inicial del arranque suave.</summary>
            public int StartCap { get; set; } = 240;

            /// <summary>Duración (ms) de la subida desde StartCap hasta Cap. 0 = sin arranque suave.</summary>
            public int RampMs { get; set; } = 3000;

            /// <summary>Zoom guardado (1.0 = 100%).</summary>
            public double Zoom { get; set; } = 1.0;

            /// <summary>Bloquea los dominios publicitarios y oculta la barra derecha de la página.</summary>
            public bool BlockAds { get; set; } = true;
        }

        private AppSettings _settings = new();
        private WebView2 Web = null!;
        private readonly DispatcherTimer _saveTimer;
        private long _navStamp;

        private WindowStyle _prevStyle;
        private WindowState _prevState;
        private bool _fullscreen;

        public MainWindow()
        {
            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
            Closing += (_, _) => { if (_saveTimer.IsEnabled) SaveSettings(); };
        }

        // ------------------------------------------------------------------
        // Flags de Chromium
        // ------------------------------------------------------------------
        private static string LoadFlags()
        {
            // Si existe flags.txt junto al .exe, manda su contenido (útil para probar).
            var file = System.IO.Path.Combine(AppContext.BaseDirectory, "flags.txt");
            if (System.IO.File.Exists(file))
            {
                return string.Join(" ", System.IO.File.ReadAllLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#')));
            }

            return string.Join(" ",
                "--disable-frame-rate-limit",
                "--disable-gpu-vsync",
                "--ignore-gpu-blocklist",
                "--enable-gpu-rasterization",
                "--use-angle=d3d11",
                "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding",
                "--disable-backgrounding-occluded-windows",
                "--disable-features=CalculateNativeWinOcclusion");
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------
        private void LoadSettings()
        {
            try
            {
                if (System.IO.File.Exists(SettingsFile))
                {
                    _settings = JsonSerializer.Deserialize<AppSettings>(
                        System.IO.File.ReadAllText(SettingsFile)) ?? new AppSettings();
                }
            }
            catch
            {
                _settings = new AppSettings();
            }

            _settings.Cap = Math.Clamp(_settings.Cap, 60, 20000);
            _settings.StartCap = Math.Clamp(_settings.StartCap, 30, _settings.Cap);
            _settings.RampMs = Math.Clamp(_settings.RampMs, 0, 30000);
            _settings.Zoom = Math.Clamp(_settings.Zoom, 0.25, 5.0);

            // Se crea en el primer arranque para que sea fácil de editar.
            if (!System.IO.File.Exists(SettingsFile)) SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                System.IO.Directory.CreateDirectory(DataDir);
                System.IO.File.WriteAllText(SettingsFile,
                    JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* si no se puede guardar, seguimos igual */ }
        }

        // ------------------------------------------------------------------
        // Arranque
        // ------------------------------------------------------------------
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch { /* sin permisos: prioridad normal */ }

            LoadSettings();

            Web = new WebView2();
            Grid.SetRow(Web, 1);
            Root.Children.Add(Web);

            try
            {
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = LoadFlags() };
                var env = await CoreWebView2Environment.CreateAsync(null, DataDir, options);
                await Web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(
                    "Falta el WebView2 Runtime de Microsoft.\n\n" +
                    "Instalá el \"Evergreen Runtime\" desde:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                    "Después volvé a abrir el launcher.",
                    "HaxLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = true;   // Ctrl + rueda, Ctrl +/-, Ctrl+0
            core.Settings.AreDevToolsEnabled = true;     // F12

            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1A, 0x21, 0x25);
            Web.ZoomFactor = _settings.Zoom;

            // --- Zoom persistente ---
            Web.ZoomFactorChanged += (_, _) => OnZoomChanged();
            core.NavigationStarting += (_, _) => _navStamp = Environment.TickCount64;
            core.NavigationCompleted += (_, _) =>
            {
                // Si la navegación reinició el zoom, lo restauramos.
                if (Math.Abs(Web.ZoomFactor - _settings.Zoom) > 0.001)
                    Web.ZoomFactor = _settings.Zoom;
            };

            // --- La barra muestra la URL actual (para copiar el link de la sala) ---
            core.SourceChanged += (_, _) =>
            {
                if (!LinkBox.IsKeyboardFocusWithin) LinkBox.Text = core.Source;
            };

            // --- Los popups (Discord, etc.) se abren en el navegador del sistema ---
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };

            // --- Mensajes desde el script inyectado ---
            core.WebMessageReceived += (_, args) =>
            {
                string msg;
                try { msg = args.TryGetWebMessageAsString(); }
                catch { return; }

                switch (msg)
                {
                    case "toggle-fullscreen": ToggleFullscreen(); break;
                    case "focus-link": FocusLinkBox(); break;
                }
            };

            // --- Bloqueo de publicidad ---
            if (_settings.BlockAds)
            {
                foreach (var d in BlockedDomains)
                {
                    core.AddWebResourceRequestedFilter(
                        $"*{d}/*",
                        CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                }

                core.WebResourceRequested += (_, args) =>
                {
                    if (IsBlockedUri(args.Request.Uri))
                        args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
            }

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildScript());

            core.Navigate(HomeUrl);
        }

        // ------------------------------------------------------------------
        // Zoom
        // ------------------------------------------------------------------
        private void OnZoomChanged()
        {
            // Ignoramos los cambios que ocurren justo al navegar: pueden ser un reinicio, no una acción del usuario.
            if (Environment.TickCount64 - _navStamp < 1500) return;

            var z = Web.ZoomFactor;
            if (Math.Abs(z - _settings.Zoom) < 0.001) return;

            _settings.Zoom = z;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ------------------------------------------------------------------
        // Barra de links
        // ------------------------------------------------------------------
        private static bool TryParseHaxballUrl(string input, out Uri uri)
        {
            uri = null!;
            input = input.Trim();
            if (input.Length == 0) return false;
            if (!input.Contains("://")) input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;

            var host = u.Host.ToLowerInvariant();
            if (host != "haxball.com" && !host.EndsWith(".haxball.com")) return false;

            uri = u;
            return true;
        }

        private void GoToLink()
        {
            if (Web?.CoreWebView2 == null) return;

            if (!TryParseHaxballUrl(LinkBox.Text, out var uri))
            {
                MessageBox.Show("Pegá un link de Haxball válido, por ejemplo:\nhttps://www.haxball.com/play?c=...",
                    "HaxLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Web.CoreWebView2.Navigate(uri.AbsoluteUri);
            Web.Focus(); // devolvemos el teclado al juego
        }

        private void FocusLinkBox()
        {
            Keyboard.Focus(LinkBox);
            LinkBox.SelectAll();
        }

        private void Go_Click(object sender, RoutedEventArgs e) => GoToLink();

        private void LinkBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                GoToLink();
                e.Handled = true;
            }
        }

        private static void OpenExternal(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            {
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // Pantalla completa y atajos (cuando el foco está en la ventana WPF)
        // ------------------------------------------------------------------
        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
            {
                FocusLinkBox();
                e.Handled = true;
            }
        }

        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _prevStyle = WindowStyle;
                _prevState = WindowState;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _prevStyle;
                WindowState = _prevState;
            }
            _fullscreen = !_fullscreen;
        }

        // ------------------------------------------------------------------
        // Script inyectado en todos los frames (limitador + contador)
        // ------------------------------------------------------------------
        private string BuildScript() => FpsScript
            .Replace("__CAP__", _settings.Cap.ToString())
            .Replace("__START__", _settings.StartCap.ToString())
            .Replace("__RAMP__", _settings.RampMs.ToString())
            .Replace("__HIDEADS__", _settings.BlockAds ? "true" : "false");

        private const string FpsScript = """
(() => {
  if (window.__hxFps) return;
  window.__hxFps = true;
 
  const CAP = __CAP__;           // tope objetivo
  const START_CAP = __START__;   // tope inicial del arranque suave
  const RAMP_MS = __RAMP__;      // duración de la subida
  const HIDE_ADS = __HIDEADS__;  // ocultar la barra derecha (publicidad)
 
  if (HIDE_ADS && window === window.top) {
    document.addEventListener('DOMContentLoaded', () => {
      const st = document.createElement('style');
      st.textContent = '.rightbar{display:none!important}';
      document.head.appendChild(st);
    });
  }
 
  const nativeRaf = window.requestAnimationFrame.bind(window);
  let box = null, visible = true;
  let frames = 0, sum = 0, worst = 0, total = 0, cbSum = 0, raw = 0;
  let ramps = 0, inputMax = 0, inputHeld = 0, inputHeldAt = 0;
  let prev = performance.now(), windowStart = prev, nextRun = 0;
  let rampStart = prev;
 
  // Arranque suave: el tope sube de forma geométrica de START_CAP a CAP.
  function currentCap(now) {
    if (RAMP_MS <= 0 || START_CAP >= CAP) return CAP;
    const k = (now - rampStart) / RAMP_MS;
    return k >= 1 ? CAP : START_CAP * Math.pow(CAP / START_CAP, k);
  }
  function restartRamp() { ramps++; rampStart = performance.now(); nextRun = 0; }
 
  // Al volver a la ventana, se repite la rampa.
  document.addEventListener('visibilitychange', () => { if (!document.hidden) restartRamp(); });
  window.addEventListener('focus', restartRamp);
 
  // Latencia de input: tiempo entre la tecla y el siguiente frame presentado.
  // Solo se reportan valores >= 16 ms (límite de la API); redondea a múltiplos de 8 ms.
  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        if ((e.name === 'keydown' || e.name === 'keyup') && e.duration > inputMax) inputMax = e.duration;
      }
    }).observe({ type: 'event', durationThreshold: 16, buffered: true });
  } catch (err) { /* API no disponible */ }
 
  function ensureBox() {
    if (box || !document.body) return;
    if (!document.querySelector('canvas')) return;   // solo en el frame del juego
    box = document.createElement('div');
    box.style.cssText =
      'position:fixed;top:4px;left:4px;z-index:2147483647;pointer-events:none;' +
      'font:12px/1.3 Consolas,monospace;color:#7CFC00;background:rgba(0,0,0,.6);' +
      'padding:3px 6px;border-radius:3px;white-space:pre';
    document.body.appendChild(box);
  }
 
  window.requestAnimationFrame = function (cb) {
    const wrapped = (t) => {
      raw++;
      const now = performance.now();
      if (now < nextRun) { nativeRaf(wrapped); return; }
 
      const cap = currentCap(now);
      const minDt = 1000 / cap;
      nextRun = Math.max(nextRun + minDt, now - minDt);
 
      const dt = now - prev; prev = now;
      frames++; total++; sum += dt;
      if (dt > worst) worst = dt;
 
      const elapsed = now - windowStart;
      if (elapsed >= 500) {
        if (inputMax > 0) { inputHeld = inputMax; inputHeldAt = now; inputMax = 0; }
        const heldNow = (now - inputHeldAt < 2000) ? inputHeld : 0;
        if (total > 120) {
          ensureBox();
          if (box) {
            box.style.display = visible ? 'block' : 'none';
            box.textContent =
              'FPS   ' + (frames * 1000 / elapsed).toFixed(0) + '\n' +
              'tope  ' + cap.toFixed(0) + '\n' +
              'avg   ' + (sum / frames).toFixed(3) + ' ms\n' +
              'peor  ' + worst.toFixed(2) + ' ms\n' +
              'juego ' + (cbSum / frames).toFixed(3) + ' ms/frame\n' +
              'raw   ' + (raw * 1000 / elapsed).toFixed(0) + '\n' +
              'input ' + (heldNow ? heldNow.toFixed(0) + ' ms' : '<16 ms') + '\n' +
              'rampas ' + ramps;
          }
        }
        frames = 0; sum = 0; worst = 0; cbSum = 0; raw = 0; windowStart = now;
      }
 
      const c0 = performance.now();
      cb(t);
      cbSum += performance.now() - c0;
    };
    return nativeRaf(wrapped);
  };
 
  // Diagnóstico (F4): reemplaza los rellenos con textura (patrones) por un color liso.
  // Los wrappers se instalan recién al apretar F4, para no sumar costo en el uso normal.
  let flatInstalled = false, flatFloor = false;
  function installFlat() {
    if (flatInstalled || typeof CanvasPattern === 'undefined') return;
    flatInstalled = true;
    const proto = CanvasRenderingContext2D.prototype;
    for (const fn of ['fillRect', 'fill']) {
      const orig = proto[fn];
      proto[fn] = function (...args) {
        if (flatFloor && this.fillStyle instanceof CanvasPattern) {
          const saved = this.fillStyle;
          this.fillStyle = '#4b5a66';
          try { return orig.apply(this, args); } finally { this.fillStyle = saved; }
        }
        return orig.apply(this, args);
      };
    }
  }
 
  window.addEventListener('keydown', (e) => {
    if (e.key === 'F3') { visible = !visible; e.preventDefault(); }
    if (e.key === 'F4' && !e.altKey) { installFlat(); flatFloor = !flatFloor; e.preventDefault(); }
    if (e.key === 'F11') {
      e.preventDefault();
      window.chrome?.webview?.postMessage('toggle-fullscreen');
    }
    if (e.ctrlKey && (e.key === 'l' || e.key === 'L')) {
      e.preventDefault();
      window.chrome?.webview?.postMessage('focus-link');
    }
  }, true);
})();
""";
    }
}