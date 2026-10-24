using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace GestorArchivos.Sync.Selector.Windows;

internal static class Program
{
    private const string AgentArgument = "--agent";
    private const string AllowedOrigin = "http://localhost:5173";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains(AgentArgument, StringComparer.OrdinalIgnoreCase))
        {
            using var mutex = new Mutex(true, @"Local\GestorArchivosSelectorAgent", out var owner);
            if (!owner) return;
            Application.Run(new SelectorAgentContext());
            return;
        }

        // El selector también inicia el agente en la sesión interactiva del usuario.
        // No requiere permisos de administrador ni instala un protocolo del navegador.
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            run?.SetValue("GestorArchivosSelectorAgent",
                $"\"{Application.ExecutablePath}\" {AgentArgument}");

            Process.Start(new ProcessStartInfo(Application.ExecutablePath, AgentArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo iniciar el agente local: {ex.Message}",
                "Gestor Archivos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Application.Run(new Form1());
    }

    private sealed class SelectorAgentContext : ApplicationContext
    {
        private readonly Form _dispatcher = new()
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            Opacity = 0
        };
        private readonly NotifyIcon _tray;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private Form1? _selector;

        public SelectorAgentContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir selector", null, (_, _) => ShowSelector());
            menu.Items.Add("Salir del agente", null, (_, _) => ExitThread());
            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Gestor Archivos - Selector",
                ContextMenuStrip = menu,
                Visible = true
            };

            _dispatcher.Shown += (_, _) =>
            {
                _dispatcher.Hide();
                _ = ListenAsync();
            };
            _dispatcher.FormClosed += (_, _) => ExitThread();
            _dispatcher.Show();
        }

        private void ShowSelector()
        {
            if (_selector is { IsDisposed: false })
            {
                _selector.WindowState = FormWindowState.Normal;
                _selector.Activate();
                return;
            }

            _selector = new Form1();
            _selector.Show();
            _selector.Activate();
        }

        private async Task ListenAsync()
        {
            try
            {
                _listener.Prefixes.Add("http://127.0.0.1:47832/");
                _listener.Start();

                while (!_stop.IsCancellationRequested)
                {
                    var request = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    Handle(request);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, "Gestor Archivos",
                    $"No se pudo iniciar el agente local: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                    !string.Equals(request.Headers["Origin"], AllowedOrigin,
                        StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 403;
                }
                else
                {
                    response.Headers["Access-Control-Allow-Origin"] = AllowedOrigin;
                    response.Headers["Vary"] = "Origin";
                    response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                    response.Headers["Access-Control-Allow-Headers"] = "Content-Type";

                    if (request.Url?.AbsolutePath != "/selector")
                        response.StatusCode = 404;
                    else if (request.HttpMethod == "OPTIONS")
                        response.StatusCode = 204;
                    else if (request.HttpMethod == "POST")
                    {
                        _dispatcher.BeginInvoke((Action)ShowSelector);
                        response.StatusCode = 204;
                    }
                    else
                        response.StatusCode = 405;
                }
                response.Close();
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { }
            }
        }

        protected override void ExitThreadCore()
        {
            _stop.Cancel();
            _listener.Close();
            _tray.Visible = false;
            _tray.Dispose();
            _dispatcher.Dispose();
            _stop.Dispose();
            base.ExitThreadCore();
        }
    }
}
