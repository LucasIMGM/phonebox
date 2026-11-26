// PhoneBox Janelas: miniaturas das janelas abertas do PC no tablet, com toque para trazer a janela para frente.
//
// Roda em segundo plano no Windows (sem janela). A cada poucos segundos lista as janelas abertas (como o Alt+Tab),
// tira uma miniatura de cada uma e manda tudo para o PhoneBox com POST /api/push/janelas. Um widget "HTML
// personalizado" no tablet mostra a grade; o toque chama o atalho "janela-N" do PhoneBox, que roda
// "janelas.exe focar N" e traz a janela do quadro N para frente.
//
// Compilado na instalação pelo csc.exe do .NET Framework 4 que vem no Windows, por isso é C# 5:
// nada de $"...", ?. ou nameof.
//
//   janelas.exe                 laço principal (uma instância só)
//   janelas.exe focar N         traz para frente a janela do quadro N
//   janelas.exe parar           encerra o laço principal
//   janelas.exe --porta 8765    porta do PhoneBox (padrão 8765)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PhoneBoxJanelas
{
    internal static class Program
    {
        // ------------------------------------------------------------------ ajustes
        private const int Slots = 9;               // quadros na grade do tablet
        private const int IntervalMs = 3000;       // de quanto em quanto tempo olha as janelas
        private const int CapturesPerCycle = 4;    // miniaturas novas por ciclo (a janela ativa sempre entra)
        private const int ThumbW = 320, ThumbH = 200;
        private const int IconSize = 24;
        private const int Budget = 60000;          // o PhoneBox aceita até 64 KB por valor
        private const int KeepAliveMs = 10000;     // reenvia mesmo sem mudança, para o ttl não vencer
        private const int TtlSec = 30;             // se este programa parar, a grade some do tablet em 30 s
        private const string PushKey = "janelas";

        private const string MutexName = @"Local\PhoneBoxJanelas.Loop";
        private const string StopName = @"Local\PhoneBoxJanelas.Stop";
        private const string RefreshName = @"Local\PhoneBoxJanelas.Refresh";

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBox-Janelas");
        private static readonly string SlotsFile = Path.Combine(DataDir, "quadros.txt");
        private static readonly string IgnoreFile = Path.Combine(DataDir, "ignorar.txt");
        private static readonly string LogFile = Path.Combine(DataDir, "log.txt");

        private static int _port = 8765;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Directory.CreateDirectory(DataDir); } catch { }

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--porta" && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536) _port = p;
                }
            }

            if (args.Length >= 2 && args[0] == "focar")
            {
                int slot;
                if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)) return 1;
                return Focus(slot);
            }
            if (args.Length >= 1 && args[0] == "parar")
            {
                SignalNamed(StopName);
                return 0;
            }

            MakeDpiAware();
            bool created;
            using (Mutex single = new Mutex(true, MutexName, out created))
            {
                if (!created) return 0; // já está rodando
                using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName))
                using (EventWaitHandle refresh = new EventWaitHandle(false, EventResetMode.AutoReset, RefreshName))
                {
                    stop.Reset();
                    Log("iniciado (porta " + _port + ")");
                    Loop(stop, refresh);
                    Log("encerrado");
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ laço principal
        private sealed class Win
        {
            public IntPtr H;
            public string Title;
            public uint Pid;
            public int Z;          // 0 = no topo (usada por último)
            public bool Minimized;
        }

        private sealed class Thumb
        {
            public Bitmap Img;
            public DateTime At;
        }

        private sealed class AppInfo
        {
            public string Key;
            public string Name;
            public string IconB64;
        }

        private static readonly IntPtr[] _slots = new IntPtr[Slots];
        private static readonly uint[] _slotPids = new uint[Slots];
        private static readonly Dictionary<IntPtr, Thumb> _thumbs = new Dictionary<IntPtr, Thumb>();
        private static readonly Dictionary<string, AppInfo> _apps = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        private static string _lastPayloadKey = "";
        private static DateTime _lastPush = DateTime.MinValue;
        private static string _lastSlotsText = "";

        private static void Loop(EventWaitHandle stop, EventWaitHandle refresh)
        {
            WaitHandle[] waits = new WaitHandle[] { stop, refresh };
            while (true)
            {
                try { Cycle(); }
                catch (Exception ex) { Log("erro no ciclo: " + ex.Message); }

                int which = WaitHandle.WaitAny(waits, IntervalMs);
                if (which == 0) return;
                if (which == 1) Thread.Sleep(250); // dá tempo da janela focada terminar de vir para frente
            }
        }

        private static void Cycle()
        {
            HashSet<string> ignore = ReadIgnoreList();
            List<Win> wins = ListWindows(ignore);
            Dictionary<IntPtr, Win> byHandle = new Dictionary<IntPtr, Win>();
            foreach (Win w in wins) byHandle[w.H] = w;

            AssignSlots(wins, byHandle);
            WriteSlotsFile();

            // descarta miniaturas de janelas que fecharam ou saíram da grade
            HashSet<IntPtr> inSlots = new HashSet<IntPtr>();
            for (int i = 0; i < Slots; i++) if (_slots[i] != IntPtr.Zero) inSlots.Add(_slots[i]);
            List<IntPtr> gone = new List<IntPtr>();
            foreach (IntPtr h in _thumbs.Keys) if (!inSlots.Contains(h)) gone.Add(h);
            foreach (IntPtr h in gone) { _thumbs[h].Img.Dispose(); _thumbs.Remove(h); }

            IntPtr active = ActiveRoot();
            RefreshThumbs(byHandle, active);

            string payload = BuildPayload(byHandle, active);
            if (payload == null) return;

            bool changed = payload != _lastPayloadKey;
            if (changed || (DateTime.UtcNow - _lastPush).TotalMilliseconds >= KeepAliveMs)
            {
                if (Post(payload))
                {
                    _lastPayloadKey = payload;
                    _lastPush = DateTime.UtcNow;
                }
            }
        }

        // Quadros estáveis: uma janela fica no mesmo quadro enquanto existir, para a grade não pular e para o
        // atalho "janela-N" continuar apontando para a janela que o usuário viu no quadro N.
        private static void AssignSlots(List<Win> wins, Dictionary<IntPtr, Win> byHandle)
        {
            for (int i = 0; i < Slots; i++)
            {
                if (_slots[i] == IntPtr.Zero) continue;
                Win w;
                if (!byHandle.TryGetValue(_slots[i], out w) || w.Pid != _slotPids[i]) { _slots[i] = IntPtr.Zero; _slotPids[i] = 0; }
            }

            foreach (Win w in wins) // em ordem de uso, a mais recente primeiro
            {
                if (Array.IndexOf(_slots, w.H) >= 0) continue;
                int free = Array.IndexOf(_slots, IntPtr.Zero);
                if (free >= 0)
                {
                    _slots[free] = w.H;
                    _slotPids[free] = w.Pid;
                    continue;
                }
                // grade cheia: troca a janela usada há mais tempo, se esta foi usada depois dela
                int worst = -1, worstZ = -1;
                for (int i = 0; i < Slots; i++)
                {
                    Win o = byHandle[_slots[i]];
                    if (o.Z > worstZ) { worstZ = o.Z; worst = i; }
                }
                if (worst >= 0 && worstZ > w.Z)
                {
                    _slots[worst] = w.H;
                    _slotPids[worst] = w.Pid;
                }
            }
        }

        private static void WriteSlotsFile()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Slots; i++)
            {
                if (_slots[i] == IntPtr.Zero) continue;
                sb.Append(i + 1).Append('\t')
                  .Append(_slots[i].ToInt64().ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(_slotPids[i].ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            string text = sb.ToString();
            if (text == _lastSlotsText) return;
            try
            {
                string tmp = SlotsFile + ".tmp";
                File.WriteAllText(tmp, text, Encoding.ASCII);
                // troca de uma vez só: o "focar" nunca encontra o arquivo pela metade nem ausente
                if (File.Exists(SlotsFile)) File.Replace(tmp, SlotsFile, null);
                else File.Move(tmp, SlotsFile);
                _lastSlotsText = text;
            }
            catch (Exception ex) { Log("não consegui gravar os quadros: " + ex.Message); }
        }

        private static void RefreshThumbs(Dictionary<IntPtr, Win> byHandle, IntPtr active)
        {
            List<IntPtr> order = new List<IntPtr>();
            for (int i = 0; i < Slots; i++)
            {
                IntPtr h = _slots[i];
                if (h == IntPtr.Zero || byHandle[h].Minimized) continue; // minimizada não é desenhada pelo Windows
                order.Add(h);
            }
            // a ativa primeiro, depois as que não têm miniatura, depois as mais antigas
            order.Sort(delegate (IntPtr a, IntPtr b)
            {
                if (a == active) return -1;
                if (b == active) return 1;
                DateTime ta = _thumbs.ContainsKey(a) ? _thumbs[a].At : DateTime.MinValue;
                DateTime tb = _thumbs.ContainsKey(b) ? _thumbs[b].At : DateTime.MinValue;
                return ta.CompareTo(tb);
            });

            int done = 0;
            foreach (IntPtr h in order)
            {
                if (done >= CapturesPerCycle) break;
                done++;
                Bitmap img = null;
                try { img = Capture(h); }
                catch (Exception ex) { Log("captura falhou: " + ex.Message); }
                if (img == null) continue; // fica a miniatura anterior, ou o ícone
                Thumb old;
                if (_thumbs.TryGetValue(h, out old)) old.Img.Dispose();
                Thumb t = new Thumb();
                t.Img = img;
                t.At = DateTime.UtcNow;
                _thumbs[h] = t;
            }
        }

        // ------------------------------------------------------------------ conteúdo enviado ao tablet
        private static readonly long[] Qualities = new long[] { 62, 48, 36, 26 };
        private static readonly double[] Scales = new double[] { 1.0, 0.8, 0.62, 0.48 };

        private static string BuildPayload(Dictionary<IntPtr, Win> byHandle, IntPtr active)
        {
            foreach (double scale in Scales)
            {
                foreach (long q in Qualities)
                {
                    string json = Serialize(byHandle, active, scale, q, true);
                    if (Encoding.UTF8.GetByteCount(json) <= Budget) return json;
                }
            }
            string bare = Serialize(byHandle, active, 1, 0, false); // só títulos e ícones
            if (Encoding.UTF8.GetByteCount(bare) <= Budget) return bare;
            Log("conteúdo grande demais mesmo sem miniaturas");
            return null;
        }

        private static string Serialize(Dictionary<IntPtr, Win> byHandle, IntPtr active, double scale, long quality, bool withThumbs)
        {
            StringBuilder sb = new StringBuilder(Budget);
            Dictionary<string, string> icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sb.Append("{\"v\":1,\"quadros\":[");
            bool first = true;
            for (int i = 0; i < Slots; i++)
            {
                IntPtr h = _slots[i];
                if (h == IntPtr.Zero) continue;
                Win w = byHandle[h];
                AppInfo app = AppFor(w.Pid);

                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"n\":").Append(i + 1);
                sb.Append(",\"titulo\":").Append(Js(w.Title));
                sb.Append(",\"app\":").Append(Js(app.Name));
                if (app.IconB64 != null)
                {
                    sb.Append(",\"icone\":").Append(Js(app.Key));
                    icons[app.Key] = app.IconB64;
                }
                if (h == active) sb.Append(",\"ativa\":true");
                if (w.Minimized) sb.Append(",\"minimizada\":true");
                Thumb t;
                if (withThumbs && _thumbs.TryGetValue(h, out t))
                {
                    sb.Append(",\"img\":\"").Append(Jpeg(t.Img, scale, quality)).Append('"');
                    sb.Append(",\"iw\":").Append(t.Img.Width).Append(",\"ih\":").Append(t.Img.Height);
                }
                sb.Append('}');
            }
            sb.Append("],\"icones\":{");
            first = true;
            foreach (KeyValuePair<string, string> kv in icons)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Js(kv.Key)).Append(":\"").Append(kv.Value).Append('"');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static AppInfo AppFor(uint pid)
        {
            string path = ProcessPath(pid);
            string key = path ?? ("pid:" + pid.ToString(CultureInfo.InvariantCulture));
            AppInfo info;
            if (_apps.TryGetValue(key, out info)) return info;

            info = new AppInfo();
            info.Key = path != null ? Path.GetFileNameWithoutExtension(path).ToLowerInvariant() : "app";
            info.Name = info.Key;
            if (path != null)
            {
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 0) info.Name = desc.Trim();
                    else info.Name = Path.GetFileNameWithoutExtension(path);
                }
                catch { info.Name = Path.GetFileNameWithoutExtension(path); }
                info.IconB64 = IconPng(path);
            }
            if (_apps.Count > 200) _apps.Clear();
            _apps[key] = info;
            return info;
        }

        private static string IconPng(string path)
        {
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                {
                    if (ic == null) return null;
                    using (Bitmap src = ic.ToBitmap())
                    using (Bitmap b = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, IconSize, IconSize);
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            b.Save(ms, ImageFormat.Png);
                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch { return null; }
        }

        private static ImageCodecInfo _jpeg;

        private static string Jpeg(Bitmap img, double scale, long quality)
        {
            if (_jpeg == null)
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                    if (c.MimeType == "image/jpeg") { _jpeg = c; break; }

            Bitmap src = img;
            Bitmap scaled = null;
            try
            {
                if (scale < 0.999)
                {
                    int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
                    scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    src = scaled;
                }
                using (MemoryStream ms = new MemoryStream())
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    src.Save(ms, _jpeg, ep);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            finally
            {
                if (scaled != null) scaled.Dispose();
            }
        }

        private static string Js(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == ' ' || c == ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static bool Post(string json)
        {
            string url = "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/api/push/" + PushKey + "?ttl=" + TtlSec.ToString(CultureInfo.InvariantCulture);
            byte[] body = Encoding.UTF8.GetBytes(json);
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/json; charset=utf-8";
                r.Proxy = null;
                r.Timeout = 4000;
                r.ReadWriteTimeout = 4000;
                r.KeepAlive = false;
                r.ContentLength = body.Length;
                using (Stream s = r.GetRequestStream()) s.Write(body, 0, body.Length);
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    return (int)resp.StatusCode < 300;
            }
            catch (WebException ex)
            {
                string detail = ex.Message;
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    try { using (StreamReader rd = new StreamReader(resp.GetResponseStream())) detail = ((int)resp.StatusCode) + " " + rd.ReadToEnd(); }
                    catch { }
                    resp.Close();
                }
                Log("PhoneBox não aceitou o envio: " + detail);
                return false;
            }
        }

        // ------------------------------------------------------------------ janelas
        private static readonly HashSet<string> SkipClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        };

        private static List<Win> ListWindows(HashSet<string> ignore)
        {
            List<Win> list = new List<Win>();
            uint self = (uint)Process.GetCurrentProcess().Id;
            int z = 0;
            EnumWindowsProc cb = delegate (IntPtr h, IntPtr lp)
            {
                int myZ = z++;
                if (!IsWindowVisible(h)) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
                if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return true;
                if ((ex & WS_EX_NOACTIVATE) != 0 && !appWindow) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero && !appWindow) return true;
                if (IsCloaked(h)) return true;

                string cls = ClassOf(h);
                if (SkipClasses.Contains(cls)) return true;
                int len = GetWindowTextLength(h);
                if (len <= 0) return true;
                StringBuilder sb = new StringBuilder(Math.Min(len, 512) + 1);
                GetWindowText(h, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (cls == "ApplicationFrameWindow") pid = UwpPid(h, pid); // apps da Store: o processo real é o filho
                if (pid == self) return true;

                bool min = IsIconic(h);
                if (!min)
                {
                    RECT r;
                    if (GetWindowRect(h, out r) && (r.Right - r.Left < 40 || r.Bottom - r.Top < 40)) return true;
                }

                if (ignore.Count > 0)
                {
                    string path = ProcessPath(pid);
                    if (path != null && ignore.Contains(Path.GetFileNameWithoutExtension(path))) return true;
                }

                Win w = new Win();
                w.H = h;
                w.Title = title.Length > 120 ? title.Substring(0, 120) : title;
                w.Pid = pid;
                w.Z = myZ;
                w.Minimized = min;
                list.Add(w);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        private static uint UwpPid(IntPtr frame, uint framePid)
        {
            uint found = framePid;
            EnumWindowsProc cb = delegate (IntPtr child, IntPtr lp)
            {
                uint p;
                GetWindowThreadProcessId(child, out p);
                if (p != framePid) { found = p; return false; }
                return true;
            };
            EnumChildWindows(frame, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return found;
        }

        private static IntPtr ActiveRoot()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(fg, GA_ROOTOWNER);
            if (root != IntPtr.Zero && Array.IndexOf(_slots, root) >= 0) return root;
            return fg;
        }

        private static bool IsCloaked(IntPtr h)
        {
            int cloaked;
            try { return DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0; }
            catch { return false; }
        }

        private static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ProcessPath(uint pid)
        {
            IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(p); }
        }

        private static Bitmap Capture(IntPtr h)
        {
            if (IsHungAppWindow(h)) return null; // PrintWindow esperaria a janela travada responder
            RECT wr;
            if (!GetWindowRect(h, out wr)) return null;
            int w = wr.Right - wr.Left, hh = wr.Bottom - wr.Top;
            if (w < 40 || hh < 40 || w > 16384 || hh > 16384) return null;

            // sem a borda invisível que o Windows 10/11 põe em volta das janelas
            int cl = 0, ct = 0, cw = w, ch = hh;
            RECT fr;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(RECT))) == 0)
            {
                int l = Math.Max(0, fr.Left - wr.Left), t = Math.Max(0, fr.Top - wr.Top);
                int fw = Math.Min(w - l, fr.Right - fr.Left), fh = Math.Min(hh - t, fr.Bottom - fr.Top);
                if (fw >= 40 && fh >= 40) { cl = l; ct = t; cw = fw; ch = fh; }
            }

            using (Bitmap full = new Bitmap(w, hh, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(full))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try { ok = PrintWindow(h, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                    if (!ok) return null;
                }
                if (IsBlank(full, cl, ct, cw, ch)) return null;

                double s = Math.Min((double)ThumbW / cw, (double)ThumbH / ch);
                if (s > 1) s = 1;
                int tw = Math.Max(1, (int)Math.Round(cw * s)), th = Math.Max(1, (int)Math.Round(ch * s));
                Bitmap thumb = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
                using (Graphics g2 = Graphics.FromImage(thumb))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g2.CompositingQuality = CompositingQuality.HighQuality;
                    g2.DrawImage(full, new Rectangle(0, 0, tw, th), new Rectangle(cl, ct, cw, ch), GraphicsUnit.Pixel);
                }
                return thumb;
            }
        }

        // PrintWindow devolve tudo preto para janelas protegidas (DRM) ou que não se desenham fora da tela
        private static bool IsBlank(Bitmap b, int x0, int y0, int w, int h)
        {
            for (int iy = 1; iy <= 6; iy++)
                for (int ix = 1; ix <= 6; ix++)
                {
                    Color c = b.GetPixel(x0 + w * ix / 7, y0 + h * iy / 7);
                    if (c.R + c.G + c.B > 24) return false;
                }
            return true;
        }

        private static HashSet<string> ReadIgnoreList()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(IgnoreFile)) return set;
                foreach (string raw in File.ReadAllLines(IgnoreFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) line = line.Substring(0, line.Length - 4);
                    set.Add(line);
                }
            }
            catch { }
            return set;
        }

        // ------------------------------------------------------------------ focar (atalho janela-N)
        private static int Focus(int slot)
        {
            IntPtr h = IntPtr.Zero;
            uint pid = 0;
            try
            {
                foreach (string line in File.ReadAllLines(SlotsFile))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    int n;
                    long hv;
                    uint pv;
                    if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n == slot
                        && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hv)
                        && uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pv))
                    {
                        h = new IntPtr(hv);
                        pid = pv;
                        break;
                    }
                }
            }
            catch { }

            if (h == IntPtr.Zero || !IsWindow(h)) { SignalNamed(RefreshName); return 2; }
            uint cur;
            GetWindowThreadProcessId(h, out cur);
            if (cur != pid)
            {
                // apps da Store: o quadro guarda o processo do app, a janela pertence ao ApplicationFrameHost
                if (ClassOf(h) != "ApplicationFrameWindow" || UwpPid(h, cur) != pid) { SignalNamed(RefreshName); return 2; }
            }

            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            // O PhoneBox libera a troca de foco (AllowSetForegroundWindow) antes de iniciar o atalho, então isto
            // costuma bastar; os passos seguintes são para quando o Windows ainda assim recusa.
            SetForegroundWindow(h);
            if (GetForegroundWindow() != h)
            {
                IntPtr fg = GetForegroundWindow();
                uint fgPid;
                uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
                uint me = GetCurrentThreadId();
                if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
                {
                    BringWindowToTop(h);
                    SetForegroundWindow(h);
                    AttachThreadInput(me, fgThread, false);
                }
            }
            if (GetForegroundWindow() != h)
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                SetForegroundWindow(h);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            SignalNamed(RefreshName); // o laço reenvia já, com a nova janela ativa
            return GetForegroundWindow() == h ? 0 : 3;
        }

        private static void SignalNamed(string name)
        {
            try
            {
                EventWaitHandle e;
                if (EventWaitHandle.TryOpenExisting(name, out e)) using (e) e.Set();
            }
            catch { }
        }

        // ------------------------------------------------------------------ diversos
        private static void MakeDpiAware()
        {
            // sem isto, numa tela com escala 125%/150% o Windows mente o tamanho das janelas e a miniatura sai cortada
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        private static string _lastLog = "";
        private static DateTime _lastLogAt = DateTime.MinValue;

        private static void Log(string msg)
        {
            try
            {
                // a mesma mensagem no máximo uma vez por minuto (o PhoneBox fechado não pode encher o disco)
                if (msg == _lastLog && (DateTime.Now - _lastLogAt).TotalSeconds < 60) return;
                _lastLog = msg;
                _lastLogAt = DateTime.Now;
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 256 * 1024) File.Delete(LogFile);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Win32
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint GW_OWNER = 4;
        private const uint GA_ROOTOWNER = 3;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const uint PW_RENDERFULLCONTENT = 2;
        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 2;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
