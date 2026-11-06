using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace wavizo.AvisadorCF2A
{
    internal static class Program
    {
        private static Mutex instanceMutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            instanceMutex = new Mutex(true, "Local\\wavizo.AvisadorCF2A", out createdNew);
            if (!createdNew)
            {
                NativeWindowHelper.ActivateExistingInstance();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Falha ao iniciar o aplicativo.\r\n\r\n" + ex.Message,
                    MainForm.AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (instanceMutex != null)
                {
                    instanceMutex.ReleaseMutex();
                    instanceMutex.Close();
                    instanceMutex = null;
                }
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(
                "Ocorreu um erro inesperado.\r\n\r\n" + e.Exception.Message,
                MainForm.AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    internal static class NativeWindowHelper
    {
        private const string WindowCaption = MainForm.AppTitle;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void ActivateExistingInstance()
        {
            try
            {
                IntPtr handle = FindWindow(null, WindowCaption);
                if (handle != IntPtr.Zero)
                {
                    SetForegroundWindow(handle);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    public class MainForm : Form
    {
        public const string AppTitle = "Avisador CF2A \u2013 Gerador de mensagens do Maico";
        public const string BrandColorHex = "#2a5298";

        private const string VirtualHostName = "avisador.local";
        private const string HtmlFileName = "FullAutoMensagensWhatsapp_v7.html";
        private const string WebView2RuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // Ponte local usada pela extensão PEC (Chrome/Edge) para entregar a
        // agenda sem passar pela área de transferência. Fica só em loopback.
        private const int PecPort = 47831;
        private const int PecLimiteBytes = 4 * 1024 * 1024;

        private static readonly Color BrandColor = Color.FromArgb(0x2A, 0x52, 0x98);

        private readonly WebView2 webView;
        private CoreWebView2Environment webViewEnvironment;
        private bool shuttingDown;
        private readonly string packageVersion;

        private TcpListener pecListener;
        private Thread pecThread;
        private volatile bool pecRunning;
        private bool paginaPronta;
        private string pendentePec;

        public MainForm()
        {
            packageVersion = ReadPackageVersion();
            Text = AppTitle;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1200, 800);
            MinimumSize = new Size(880, 620);
            BackColor = BrandColor;
            KeyPreview = false;

            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.BackColor = BrandColor;
            webView.DefaultBackgroundColor = BrandColor;
            Controls.Add(webView);

            webView.CoreWebView2InitializationCompleted += OnCoreWebView2Initialized;
            Load += OnFormLoad;
            FormClosed += OnFormClosed;
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "wavizo",
                    "AvisadorCF2A");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                webViewEnvironment = environment;
                await webView.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                ShowFatalError(
                    "N\u00e3o foi poss\u00edvel iniciar o componente Microsoft Edge WebView2.\r\n\r\n"
                    + ex.Message
                    + "\r\n\r\nInstale o WebView2 Runtime e abra o aplicativo novamente:\r\n"
                    + WebView2RuntimeUrl);
            }
        }

        private void OnCoreWebView2Initialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (shuttingDown)
            {
                return;
            }

            if (!e.IsSuccess)
            {
                string detail = e.InitializationException != null ? e.InitializationException.Message : "erro desconhecido";
                ShowFatalError("Falha ao inicializar o navegador embutido.\r\n\r\n" + detail);
                return;
            }

            CoreWebView2 core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.IsScriptEnabled = true;

            string htmlFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "html");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                htmlFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;

            IniciarPontePec();
            core.Navigate("https://" + VirtualHostName + "/" + HtmlFileName);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsInternalResource(e.Uri))
            {
                return;
            }

            e.Cancel = true;
            OpenLinkInsideApp(e.Uri);
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (IsInternalResource(e.Uri))
            {
                return;
            }

            e.Handled = true;
            OpenLinkInsideApp(e.Uri);
        }

        private void OpenLinkInsideApp(string uri)
        {
            try
            {
                ExternalLinkForm.Open(this, webViewEnvironment, uri);
            }
            catch (Exception)
            {
            }
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            if (shuttingDown)
            {
                return;
            }

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                DialogResult restart = MessageBox.Show(
                    this,
                    "O componente do navegador foi encerrado inesperadamente.\r\n\r\nDeseja reiniciar o aplicativo?",
                    AppTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (restart == DialogResult.Yes)
                {
                    shuttingDown = true;
                    Application.Restart();
                }
                else
                {
                    shuttingDown = true;
                    Close();
                }
                return;
            }

            try
            {
                webView.Reload();
            }
            catch (Exception)
            {
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.TryGetWebMessageAsString();
            if (message == null)
            {
                return;
            }

            if (string.Equals(message, "quit", StringComparison.Ordinal))
            {
                try
                {
                    BeginInvoke(new Action(Close));
                }
                catch (Exception)
                {
                }
                return;
            }

            // v1.4: ponte de SMS ({"id":1,"cmd":"sms",...}) — resposta via
            // PostWebMessageAsString com o mesmo "id".
            if (message.IndexOf("\"cmd\"", StringComparison.Ordinal) >= 0)
            {
                ProcessarComandoPonte(message);
            }
        }

        // ============================================================
        // v1.4.2 — PONTE LOCAL DA EXTENSÃO PEC
        // A extensão do Chrome/Edge faz POST http://127.0.0.1:47831/pec/import
        // com o texto da agenda. O host injeta o texto no campo #inputData e
        // dispara a geração das mensagens, sem copiar/colar.
        // ============================================================
        private void IniciarPontePec()
        {
            if (pecListener != null)
            {
                return;
            }

            try
            {
                pecListener = new TcpListener(IPAddress.Loopback, PecPort);
                pecListener.Start();
                pecRunning = true;
                pecThread = new Thread(LoopPontePec);
                pecThread.IsBackground = true;
                pecThread.Name = "PontePEC";
                pecThread.Start();
            }
            catch (Exception)
            {
                pecListener = null;
                pecRunning = false;
            }
        }

        private void LoopPontePec()
        {
            while (pecRunning)
            {
                TcpClient cliente = null;
                try
                {
                    cliente = pecListener.AcceptTcpClient();
                    cliente.ReceiveTimeout = 5000;
                    using (NetworkStream fluxo = cliente.GetStream())
                    {
                        AtenderPontePec(fluxo);
                    }
                }
                catch (Exception)
                {
                    // Porta fechada no encerramento ou requisição incompleta:
                    // o próximo laço recomeça.
                }
                finally
                {
                    if (cliente != null)
                    {
                        try { cliente.Close(); } catch (Exception) { }
                    }
                }
            }
        }

        private void AtenderPontePec(NetworkStream fluxo)
        {
            byte[] buffer = new byte[8192];
            MemoryStream recebido = new MemoryStream();
            int fimCabecalho = -1;

            while (fimCabecalho < 0 && recebido.Length < 65536)
            {
                int lido = fluxo.Read(buffer, 0, buffer.Length);
                if (lido <= 0)
                {
                    return;
                }
                recebido.Write(buffer, 0, lido);
                fimCabecalho = EncontrarFimCabecalho(recebido.GetBuffer(), (int)recebido.Length);
            }

            if (fimCabecalho < 0)
            {
                ResponderPonte(fluxo, 400, "{\"ok\":false,\"erro\":\"cabecalho invalido\"}");
                return;
            }

            byte[] dados = recebido.ToArray();
            string cabecalho = Encoding.ASCII.GetString(dados, 0, fimCabecalho);
            string[] linhas = cabecalho.Split(new string[] { "\r\n" }, StringSplitOptions.None);

            string metodo = "GET";
            string caminho = "/";
            if (linhas.Length > 0)
            {
                string[] partes = linhas[0].Split(' ');
                if (partes.Length > 0)
                {
                    metodo = partes[0].ToUpperInvariant();
                }
                if (partes.Length > 1)
                {
                    caminho = partes[1];
                }
            }

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < linhas.Length; i++)
            {
                int separador = linhas[i].IndexOf(':');
                if (separador > 0)
                {
                    string nome = linhas[i].Substring(0, separador).Trim();
                    string valor = linhas[i].Substring(separador + 1).Trim();
                    headers[nome] = valor;
                }
            }

            string origem = headers.ContainsKey("Origin") ? headers["Origin"] : null;
            if (!OrigemPecPermitida(origem))
            {
                ResponderPonte(fluxo, 403, "{\"ok\":false,\"erro\":\"origem nao autorizada\"}");
                return;
            }

            if (string.Equals(metodo, "OPTIONS", StringComparison.Ordinal))
            {
                ResponderPonte(fluxo, 204, string.Empty);
                return;
            }

            if (string.Equals(metodo, "GET", StringComparison.Ordinal) &&
                caminho.StartsWith("/pec/ping", StringComparison.OrdinalIgnoreCase))
            {
                ResponderPonte(fluxo, 200, "{\"ok\":true,\"app\":\"Avisador CF2A\",\"porta\":" + PecPort + "}");
                return;
            }

            if (!string.Equals(metodo, "POST", StringComparison.Ordinal) ||
                !caminho.StartsWith("/pec/import", StringComparison.OrdinalIgnoreCase))
            {
                ResponderPonte(fluxo, 404, "{\"ok\":false,\"erro\":\"rota desconhecida\"}");
                return;
            }

            int tamanhoCorpo = 0;
            if (headers.ContainsKey("Content-Length"))
            {
                int.TryParse(headers["Content-Length"], out tamanhoCorpo);
            }

            if (tamanhoCorpo <= 0 || tamanhoCorpo > PecLimiteBytes)
            {
                ResponderPonte(fluxo, 413, "{\"ok\":false,\"erro\":\"tamanho invalido\"}");
                return;
            }

            int inicioCorpo = fimCabecalho + 4;
            int jaRecebido = dados.Length - inicioCorpo;
            if (jaRecebido > tamanhoCorpo)
            {
                jaRecebido = tamanhoCorpo;
            }
            if (jaRecebido < 0)
            {
                jaRecebido = 0;
            }

            MemoryStream corpo = new MemoryStream(tamanhoCorpo);
            corpo.Write(dados, inicioCorpo, jaRecebido);
            while (corpo.Length < tamanhoCorpo)
            {
                int restante = tamanhoCorpo - (int)corpo.Length;
                int lido = fluxo.Read(buffer, 0, Math.Min(buffer.Length, restante));
                if (lido <= 0)
                {
                    break;
                }
                corpo.Write(buffer, 0, lido);
            }

            string texto = Encoding.UTF8.GetString(corpo.ToArray());
            if (string.IsNullOrWhiteSpace(texto))
            {
                ResponderPonte(fluxo, 400, "{\"ok\":false,\"erro\":\"texto vazio\"}");
                return;
            }

            bool entregue = EnfileirarAgendaPec(texto);
            ResponderPonte(
                fluxo,
                200,
                "{\"ok\":" + (entregue ? "true" : "false") +
                ",\"recebidos\":" + texto.Length + "}");
        }

        private static bool OrigemPecPermitida(string origem)
        {
            // A extensão do Chrome/Edge chega como chrome-extension://<id>.
            // Sites comuns chegam como http(s):// e são recusados, o que
            // impede um site malicioso de postar na porta local.
            if (string.IsNullOrEmpty(origem))
            {
                return true;
            }
            return origem.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
        }

        private bool EnfileirarAgendaPec(string texto)
        {
            if (shuttingDown)
            {
                return false;
            }

            try
            {
                BeginInvoke(new Action<string>(ReceberAgendaPec), texto);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReceberAgendaPec(string texto)
        {
            if (shuttingDown)
            {
                return;
            }

            if (!paginaPronta || webView == null || webView.CoreWebView2 == null)
            {
                pendentePec = texto;
                return;
            }

            AplicarAgendaPec(texto);
        }

        private void AplicarAgendaPec(string texto)
        {
            try
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }
                Show();
                Activate();
                BringToFront();

                // O texto entra no mesmo campo que o usuário usaria para colar
                // e a geração roda como se ele tivesse clicado em "Gerar".
                string script =
                    "(function(){try{" +
                    "var t=\"" + EscapearJson(texto) + "\";" +
                    "var el=document.getElementById('inputData');" +
                    "if(!el)return 'sem-campo';" +
                    "el.value=t;" +
                    "el.dispatchEvent(new Event('input',{bubbles:true}));" +
                    "if(typeof generateMessages==='function')generateMessages();" +
                    "if(typeof switchTab==='function')switchTab('messages');" +
                    "if(typeof showToast==='function')showToast('📥 Agenda recebida da extensão PEC — mensagens geradas.');" +
                    "return 'ok';" +
                    "}catch(e){return 'erro:'+e.message;}})()";
                webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception)
            {
            }
        }

        private void AplicarAgendaPendente()
        {
            if (string.IsNullOrEmpty(pendentePec))
            {
                return;
            }

            string texto = pendentePec;
            pendentePec = null;
            AplicarAgendaPec(texto);
        }

        private static int EncontrarFimCabecalho(byte[] dados, int tamanho)
        {
            for (int i = 0; i <= tamanho - 4; i++)
            {
                if (dados[i] == 13 && dados[i + 1] == 10 &&
                    dados[i + 2] == 13 && dados[i + 3] == 10)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void ResponderPonte(NetworkStream fluxo, int status, string corpo)
        {
            byte[] conteudo = Encoding.UTF8.GetBytes(corpo ?? string.Empty);
            string motivo = "OK";
            if (status == 204) motivo = "No Content";
            else if (status == 400) motivo = "Bad Request";
            else if (status == 403) motivo = "Forbidden";
            else if (status == 404) motivo = "Not Found";
            else if (status == 413) motivo = "Payload Too Large";

            StringBuilder cabecalho = new StringBuilder();
            cabecalho.Append("HTTP/1.1 ").Append(status).Append(' ').Append(motivo).Append("\r\n");
            cabecalho.Append("Content-Type: application/json; charset=utf-8\r\n");
            cabecalho.Append("Access-Control-Allow-Origin: *\r\n");
            cabecalho.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            cabecalho.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            cabecalho.Append("Access-Control-Allow-Private-Network: true\r\n");
            cabecalho.Append("Access-Control-Max-Age: 86400\r\n");
            cabecalho.Append("Cache-Control: no-store\r\n");
            cabecalho.Append("Connection: close\r\n");
            cabecalho.Append("Content-Length: ").Append(conteudo.Length).Append("\r\n\r\n");

            byte[] bytesCabecalho = Encoding.ASCII.GetBytes(cabecalho.ToString());
            fluxo.Write(bytesCabecalho, 0, bytesCabecalho.Length);
            if (conteudo.Length > 0)
            {
                fluxo.Write(conteudo, 0, conteudo.Length);
            }
            fluxo.Flush();
        }

        private void ProcessarComandoPonte(string message)
        {
            int id = ExtrairInteiroJson(message, "id", 0);
            string cmd = ExtrairTextoJson(message, "cmd");

            // v1.4.1: abre o WhatsApp instalado direto, sem a janela WebView2.
            if (string.Equals(cmd, "whatsapp", StringComparison.Ordinal))
            {
                string phone = ExtrairTextoJson(message, "phone");
                string texto = ExtrairTextoJson(message, "text");
                ThreadPool.QueueUserWorkItem(
                    delegate(object state)
                    {
                        bool ok = false;
                        string erro = null;
                        try
                        {
                            ok = AbrirWhatsApp(phone, texto, out erro);
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            erro = ex.Message;
                        }

                        string reply =
                            "{\"id\":" + id +
                            ",\"ok\":" + (ok ? "true" : "false") +
                            ",\"status\":0" +
                            ",\"error\":" + (erro == null ? "null" : "\"" + EscapearJson(erro) + "\"") +
                            "}";
                        PostarMensagemParaPagina(reply);
                    },
                    null);
                return;
            }

            if (!string.Equals(cmd, "sms", StringComparison.Ordinal))
            {
                return;
            }

            string url = ExtrairTextoJson(message, "url");
            string key = ExtrairTextoJson(message, "key");
            string number = ExtrairTextoJson(message, "number");
            string text = ExtrairTextoJson(message, "text");
            bool test = ExtrairBooleanoJson(message, "test", false);

            ThreadPool.QueueUserWorkItem(
                delegate(object state)
                {
                    int status = 0;
                    string erro = null;
                    bool ok = false;
                    try
                    {
                        ok = ExecutarSmsOpenSms(url, key, number, text, test, out status, out erro);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        status = 0;
                        erro = ex.Message;
                    }

                    string reply =
                        "{\"id\":" + id +
                        ",\"ok\":" + (ok ? "true" : "false") +
                        ",\"status\":" + status +
                        ",\"error\":" + (erro == null ? "null" : "\"" + EscapearJson(erro) + "\"") +
                        "}";
                    PostarMensagemParaPagina(reply);
                },
                null);
        }

        /* Abre o WhatsApp instalado pelo protocolo whatsapp:, sem criar a
           janela WebView2 intermediária. O texto chega como Unicode pela
           ponte e é codificado UMA vez aqui — por isso os emojis sobrevivem.
           Se o protocolo não estiver registrado, abre o wa.me no navegador
           padrão (mesmo link do botão "Link"). */
        private static bool AbrirWhatsApp(string phone, string text, out string erro)
        {
            erro = null;
            string numero = (phone ?? string.Empty).Trim();
            if (numero.Length == 0)
            {
                erro = "Telefone ausente.";
                return false;
            }

            string codificado = Uri.EscapeDataString(text ?? string.Empty);
            try
            {
                Process.Start(new ProcessStartInfo(
                    "whatsapp://send?phone=" + numero + "&text=" + codificado)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://wa.me/" + numero + "?text=" + codificado)
                    {
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Exception ex2)
                {
                    erro = ex2.Message;
                    return false;
                }
            }
        }

        private void PostarMensagemParaPagina(string json)
        {
            try
            {
                // Esta checagem roda na thread do ThreadPool — webView.CoreWebView2
                // NÃO pode ser lido daqui ("CoreWebView2 can only be accessed from
                // the UI thread"). Só a referência do controle é segura; a checagem
                // completa fica em PostarMensagemDireto, que roda na UI thread.
                if (shuttingDown || webView == null)
                {
                    return;
                }
                BeginInvoke(new Action<string>(PostarMensagemDireto), json);
            }
            catch (Exception)
            {
            }
        }

        private void PostarMensagemDireto(string json)
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsString(json);
                }
            }
            catch (Exception)
            {
            }
        }

        /* POST para o servidor OpenSMS (http://IP:9292/sms/) com Bearer key.
           "test": envia POST sem corpo — qualquer resposta HTTP prova que o
           servidor está vivo (401/422 inclusive), sem disparar SMS algum. */
        private static bool ExecutarSmsOpenSms(
            string url,
            string key,
            string number,
            string text,
            bool test,
            out int status,
            out string erro)
        {
            status = 0;
            erro = null;

            Uri alvo;
            if (string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out alvo) ||
                (alvo.Scheme != Uri.UriSchemeHttp && alvo.Scheme != Uri.UriSchemeHttps))
            {
                erro = "Endereço do servidor OpenSMS inválido.";
                return false;
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(alvo);
                request.Method = "POST";
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.KeepAlive = false;
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("Authorization", "Bearer " + key);
                }

                if (test)
                {
                    request.ContentLength = 0;
                }
                else
                {
                    string corpo =
                        "{\"number\":\"" + EscapearJson(number ?? string.Empty) +
                        "\",\"text\":\"" + EscapearJson(text ?? string.Empty) + "\"}";
                    byte[] dados = System.Text.Encoding.UTF8.GetBytes(corpo);
                    request.ContentType = "application/json; charset=utf-8";
                    request.ContentLength = dados.Length;
                    using (Stream fluxo = request.GetRequestStream())
                    {
                        fluxo.Write(dados, 0, dados.Length);
                    }
                }

                using (HttpWebResponse resposta = (HttpWebResponse)request.GetResponse())
                {
                    status = (int)resposta.StatusCode;
                    if (test)
                    {
                        return true;
                    }
                    bool sucesso = status >= 200 && status <= 299;
                    if (!sucesso)
                    {
                        erro = "O servidor respondeu HTTP " + status + ".";
                    }
                    return sucesso;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse respostaErro = ex.Response as HttpWebResponse;
                if (respostaErro != null)
                {
                    status = (int)respostaErro.StatusCode;
                    try { respostaErro.Close(); }
                    catch (Exception) { }
                    // Servidor respondeu: no teste isso já é sucesso; no envio, é recusa.
                    if (test)
                    {
                        return true;
                    }
                    erro = "O servidor recusou a solicitação (HTTP " + status + ").";
                    return false;
                }

                status = 0;
                erro = "Sem resposta do servidor OpenSMS: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                status = 0;
                erro = ex.Message;
                return false;
            }
        }

        private static string EscapearJson(string valor)
        {
            if (string.IsNullOrEmpty(valor))
            {
                return string.Empty;
            }
            return valor
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\u2028", "\\u2028")
                .Replace("\u2029", "\\u2029");
        }

        private static string ExtrairTextoJson(string json, string campo)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\"" + campo + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!match.Success)
            {
                return null;
            }
            return match.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\/", "/")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\");
        }

        private static int ExtrairInteiroJson(string json, string campo, int padrao)
        {
            if (string.IsNullOrEmpty(json))
            {
                return padrao;
            }
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\"" + campo + "\"\\s*:\\s*(-?\\d+)");
            if (!match.Success)
            {
                return padrao;
            }
            int valor;
            return int.TryParse(match.Groups[1].Value, out valor) ? valor : padrao;
        }

        private static bool ExtrairBooleanoJson(string json, string campo, bool padrao)
        {
            if (string.IsNullOrEmpty(json))
            {
                return padrao;
            }
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\"" + campo + "\"\\s*:\\s*(true|false)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return padrao;
            }
            return string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadPackageVersion()
        {
            try
            {
                string manifestPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "AppxManifest.xml");
                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                string xml = File.ReadAllText(manifestPath);
                System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                    xml,
                    "Version=\"([0-9]+(?:\\.[0-9]+){3})\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (shuttingDown || !e.IsSuccess)
            {
                return;
            }

            paginaPronta = true;

            if (!string.IsNullOrEmpty(packageVersion))
            {
                try
                {
                    webView.CoreWebView2.ExecuteScriptAsync(
                        "window.aplicarVersao && window.aplicarVersao('" + packageVersion + "');");
                }
                catch (Exception)
                {
                }
            }

            AplicarAgendaPendente();
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            shuttingDown = true;
            pecRunning = false;
            try
            {
                if (pecListener != null)
                {
                    pecListener.Stop();
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (webView != null)
                {
                    webView.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        private void ShowFatalError(string message)
        {
            MessageBox.Show(this, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            shuttingDown = true;
            Close();
        }

        private static bool IsInternalResource(string uri)
        {
            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed))
            {
                return false;
            }

            if (string.Equals(parsed.Scheme, "about", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parsed.Scheme, "blob", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parsed.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(parsed.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class ExternalLinkForm : Form
    {
        private static ExternalLinkForm current;

        private readonly CoreWebView2Environment environment;
        private readonly string initialUrl;
        private readonly WebView2 webView;

        private ExternalLinkForm(CoreWebView2Environment environment, string url)
        {
            this.environment = environment;
            initialUrl = url;

            Text = MainForm.AppTitle;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 720);
            MinimumSize = new Size(560, 460);
            BackColor = Color.White;

            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.DefaultBackgroundColor = Color.White;
            Controls.Add(webView);

            Load += OnFormLoad;
            FormClosed += OnFormClosed;
        }

        public static void Open(Form owner, CoreWebView2Environment environment, string url)
        {
            if (environment == null || string.IsNullOrEmpty(url))
            {
                return;
            }

            if (current != null && !current.IsDisposed)
            {
                current.NavigateTo(url);
                current.Activate();
                return;
            }

            current = new ExternalLinkForm(environment, url);
            if (owner != null && !owner.IsDisposed)
            {
                current.Show(owner);
            }
            else
            {
                current.Show();
            }
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            try
            {
                await webView.EnsureCoreWebView2Async(environment);

                CoreWebView2 core = webView.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsStatusBarEnabled = true;
                core.NewWindowRequested += OnNestedNewWindowRequested;
                core.DocumentTitleChanged += OnDocumentTitleChanged;

                core.Navigate(initialUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "N\u00e3o foi poss\u00edvel abrir o link.\r\n\r\n" + ex.Message,
                    MainForm.AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                Close();
            }
        }

        private void OnNestedNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            NavigateTo(e.Uri);
        }

        private void OnDocumentTitleChanged(object sender, object e)
        {
            try
            {
                string title = webView.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    Text = title + " \u2013 Avisador CF2A";
                }
            }
            catch (Exception)
            {
            }
        }

        private void NavigateTo(string url)
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.Navigate(url);
                }
            }
            catch (Exception)
            {
            }
            Activate();
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            current = null;
            try
            {
                if (webView != null)
                {
                    webView.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
