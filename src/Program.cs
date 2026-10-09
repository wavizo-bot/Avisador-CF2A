using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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
        public const string AppTitle = "Avisador CF2A \u2013 Cl\u00ednica da Fam\u00edlia II";
        public const string BrandColorHex = "#2a5298";

        private const string VirtualHostName = "avisador.local";
        private const string HtmlFileName = "FullAutoMensagensWhatsapp_v7.html";
        private const string WebView2RuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        private static readonly Color BrandColor = Color.FromArgb(0x2A, 0x52, 0x98);

        private readonly WebView2 webView;
        private CoreWebView2Environment webViewEnvironment;
        private bool shuttingDown;

        public MainForm()
        {
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
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;

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
            if (!string.Equals(message, "quit", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(Close));
            }
            catch (Exception)
            {
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            shuttingDown = true;
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
