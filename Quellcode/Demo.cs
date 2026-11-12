// reitzner IT-Themensammler – Screenshots mit Beispieldaten (hell und dunkel) für GitHub
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>
    /// Erzeugt die Screenshots für die README aus Beispieldaten (Resources\Demo.json, in die EXE eingebettet).
    /// Aufruf: "reitzner IT-Themensammler.exe" --screenshots [Zielordner].
    /// Die echten Daten unter %ProgramData% und der eingerichtete Dienst werden dabei nicht angefasst: Die Beispieldaten
    /// liegen in einem eigenen temporären Ordner, der Webserver läuft nur für die Aufnahmen auf einem freien Port.
    ///
    /// Bilder:
    /// - hell.png / dunkel.png – Weboberfläche, Themen (Titelbild)
    /// - bericht-*.png – Tagesbericht, verwaltung-*.png – Verwaltung (Standorte und Gruppen)
    /// - verwaltungsfenster-*.png – Verwaltungsfenster (Dienst), info-*.png – Info-Fenster
    /// Die Weboberfläche nimmt Microsoft Edge (sonst Google Chrome) ohne sichtbares Fenster auf. Die Programmfenster werden
    /// einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben sich durchsichtige runde Ecken und
    /// der Windows-Schatten (Vorlage: Wake on LAN, Demo.cs).
    /// </summary>
    internal static class Demo
    {
        /// <summary>true, solange die Screenshots erstellt werden (Dienst gilt als laufend, kein echter Rechnername).</summary>
        public static bool Active;

        /// <summary>Nur bei den Screenshots: Schlüssel, mit dem der Browser als Admin angemeldet wird ("/?demo=…").</summary>
        public static string WebSchluessel;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static bool RunScreenshots(string outDir)
        {
            string temp = Path.Combine(Path.GetTempPath(), AppInfo.Name + " Screenshots");
            WebServer server = null;
            string fehlerWeb = null;
            try
            {
                Active = true;
                Directory.CreateDirectory(outDir);
                try { File.Delete(Path.Combine(outDir, "Fehler.txt")); } catch { }
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                string daten = Path.Combine(temp, "Daten");
                Directory.CreateDirectory(daten);

                // Beispieldaten statt der echten Daten
                Speicher.DemoOrdner = daten;
                BeispieldatenAnlegen();

                // Webserver nur für die Aufnahmen (angezeigt wird trotzdem Port 8080)
                int port = FreierPort();
                WebSchluessel = BitConverter.ToString(Speicher.Zufall(16)).Replace("-", "").ToLowerInvariant();
                server = new WebServer();
                server.Starten(port);

                // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm)
                var area = Screen.PrimaryScreen.WorkingArea;
                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);

                string browser = BrowserSuchen();
                if (browser == null) fehlerWeb = "Microsoft Edge bzw. Google Chrome wurde nicht gefunden – die Bilder der Weboberfläche fehlen.";

                using (var backdrop = new Backdrop())
                {
                    // Hintergrundfläche einmal vorab anzeigen (Windows blendet neue Fenster langsam ein)
                    backdrop.Cover(new Rectangle(area.Right - 2, area.Bottom - 2, 1, 1), Color.Black, IntPtr.Zero);
                    Pump(300);
                    backdrop.Hide();

                    foreach (bool dark in new[] { false, true })
                    {
                        string suffix = dark ? "dunkel" : "hell";

                        // Weboberfläche: Themen (Titelbild), Tagesbericht und Verwaltung
                        if (browser != null)
                        {
                            try
                            {
                                string basis = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                                Web(browser, temp, basis + "?farbe=" + suffix, Path.Combine(outDir, suffix + ".png"), 1280, 1000);
                                Web(browser, temp, basis + "?demo=" + WebSchluessel + "&farbe=" + suffix + "#bericht", Path.Combine(outDir, "bericht-" + suffix + ".png"), 1280, 1000);
                                Web(browser, temp, basis + "?demo=" + WebSchluessel + "&farbe=" + suffix + "#verwaltung", Path.Combine(outDir, "verwaltung-" + suffix + ".png"), 1280, 1100);
                            }
                            catch (Exception ex)
                            {
                                fehlerWeb = "Weboberfläche: " + ex.Message;
                            }
                        }

                        // Programmfenster
                        Theme.Override = dark;
                        Theme.Refresh();
                        Shot(backdrop, () => new MainForm(), Path.Combine(outDir, "verwaltungsfenster-" + suffix + ".png"));
                        Shot(backdrop, () => new AboutDialog(null) { StartPosition = FormStartPosition.CenterScreen }, Path.Combine(outDir, "info-" + suffix + ".png"));
                    }
                }
                if (fehlerWeb != null) throw new InvalidOperationException(fehlerWeb);
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(outDir, "Fehler.txt"), ex.ToString()); } catch { }
                return false;
            }
            finally
            {
                if (server != null) try { server.Stoppen(); } catch { }
                Theme.Override = null;
                Active = false;
                WebSchluessel = null;
                Speicher.DemoOrdner = null;
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        /// <summary>
        /// Beispieldaten aus Resources\Demo.json (erfundene Gemeinde "Musterstadt"). Datumsangaben stehen dort relativ zu heute
        /// ("T+7" = in 7 Tagen, "T-2" = vor 2 Tagen), damit Termine und Tagesbericht immer passen.
        /// </summary>
        private static void BeispieldatenAnlegen()
        {
            string json;
            using (var rs = typeof(Demo).Assembly.GetManifestResourceStream("Demo.json"))
            {
                if (rs == null) throw new FileNotFoundException("Demo.json ist nicht in die EXE eingebettet.");
                using (var r = new StreamReader(rs, Encoding.UTF8)) json = r.ReadToEnd();
            }
            json = Regex.Replace(json, @"""T([+-]\d+)", m =>
                "\"" + DateTime.Today.AddDays(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            File.WriteAllText(Speicher.DatenPfad, json, new UTF8Encoding(false));

            Speicher.EinstellungenAendern(e =>
            {
                e.port = 8080;
                e.titel = "IT-Themensammlung";
                e.untertitel = "Sammlung bis zum nächsten Vor-Ort-Termin";
                e.kunde = "Gemeinde Musterstadt";
                e.ticketMail = "it-service@example.com";
                e.hotline = "01234 5678-0";
                e.gruppierung = "ort";
                e.erledigteSichtbar = true;
                e.mitarbeiterAnmeldung = "aus";
                e.loeschMonate = 24;
                e.loeschArchiv = true;
            });
            BeispielbildAnlegen("1066726e7160bd9c987c9968e0cc275a");
            Speicher.KennwortSetzen("admin", "Demo-Kennwort-2026");
            Speicher.KennwortSetzen("service", "Demo-Kennwort-2026");
        }

        /// <summary>
        /// Bild zum Beispielthema "Tablets im WLAN": eine gezeichnete Meldung im Windows-Stil (kein echtes Bildschirmfoto).
        /// </summary>
        private static void BeispielbildAnlegen(string id)
        {
            Directory.CreateDirectory(Speicher.AnhangOrdner);
            using (var bmp = new Bitmap(900, 520))
            using (var g = Graphics.FromImage(bmp))
            using (var titel = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel))
            using (var text = new Font("Segoe UI", 17f, GraphicsUnit.Pixel))
            using (var klein = new Font("Segoe UI", 15f, GraphicsUnit.Pixel))
            using (var grau = new SolidBrush(Color.FromArgb(95, 97, 104)))
            using (var dunkel = new SolidBrush(Color.FromArgb(27, 27, 27)))
            using (var blau = new SolidBrush(Color.FromArgb(0, 103, 192)))
            using (var rand = new Pen(Color.FromArgb(209, 209, 209)))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.FromArgb(32, 80, 129));
                var fenster = new Rectangle(150, 90, 600, 320);
                g.FillRectangle(Brushes.White, fenster);
                g.DrawRectangle(rand, fenster);
                g.FillRectangle(new SolidBrush(Color.FromArgb(243, 243, 243)), fenster.X + 1, fenster.Bottom - 70, fenster.Width - 1, 70);
                // Warnsymbol
                g.FillEllipse(new SolidBrush(Color.FromArgb(196, 43, 28)), fenster.X + 30, fenster.Y + 34, 40, 40);
                g.DrawString("!", titel, Brushes.White, fenster.X + 44, fenster.Y + 38);
                g.DrawString("Verbindung nicht möglich", titel, dunkel, fenster.X + 90, fenster.Y + 36);
                g.DrawString("Mit „Schule-WLAN“ kann keine Verbindung hergestellt werden.", text, dunkel, new RectangleF(fenster.X + 90, fenster.Y + 82, 480, 60));
                g.DrawString("Fehlercode: 0x80070057 (Beispiel)", klein, grau, fenster.X + 90, fenster.Y + 150);
                var ok = new Rectangle(fenster.Right - 140, fenster.Bottom - 52, 110, 34);
                g.FillRectangle(blau, ok);
                var mitte = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("Schließen", text, Brushes.White, ok, mitte);
                var enc = ImageCodecInfo.GetImageEncoders();
                ImageCodecInfo jpeg = null;
                foreach (var c in enc) if (c.MimeType == "image/jpeg") jpeg = c;
                using (var p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                    bmp.Save(Speicher.AnhangPfad(id), jpeg, p);
                }
            }
        }

        private static int FreierPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>Microsoft Edge, sonst Google Chrome (für die Aufnahme der Weboberfläche ohne sichtbares Fenster).</summary>
        private static string BrowserSuchen()
        {
            var basen = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            foreach (string rel in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
                foreach (string b in basen)
                {
                    if (string.IsNullOrEmpty(b)) continue;
                    string p = Path.Combine(b, rel);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        /// <summary>Eine Seite der Weboberfläche aufnehmen (Browser ohne Fenster, eigenes Profil im temporären Ordner).</summary>
        private static void Web(string browser, string temp, string url, string datei, int breite, int hoehe)
        {
            try { File.Delete(datei); } catch { }
            // je Aufnahme ein eigenes Profil: Bei einem gemeinsamen Profil übergibt Edge die Aufnahme sonst an den noch
            // laufenden Vorgänger und beendet sich sofort, das Bild entsteht dann erst später (oder gar nicht)
            string profil = Path.Combine(temp, "Browser-" + Path.GetFileNameWithoutExtension(datei));
            var args = string.Join(" ", new[]
            {
                "--headless", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-sync", "--lang=de", "--force-device-scale-factor=1",
                "--virtual-time-budget=8000",
                "--window-size=" + breite.ToString(CultureInfo.InvariantCulture) + "," + hoehe.ToString(CultureInfo.InvariantCulture),
                "\"--user-data-dir=" + profil + "\"",
                "\"--screenshot=" + datei + "\"",
                "\"" + url + "\""
            });
            var psi = new ProcessStartInfo(browser, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(90000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("Der Browser hat nicht rechtzeitig geantwortet (" + Path.GetFileName(datei) + ").");
                }
            }
            // Edge schreibt das Bild teils erst kurz nach dem Ende des Startprozesses
            for (int i = 0; i < 60 && !FertigGeschrieben(datei); i++) Thread.Sleep(250);
            if (!File.Exists(datei)) throw new IOException("Der Browser hat kein Bild erzeugt (" + Path.GetFileName(datei) + ").");
        }

        private static bool FertigGeschrieben(string datei)
        {
            try
            {
                if (!File.Exists(datei)) return false;
                using (var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.None)) return fs.Length > 0;
            }
            catch (IOException) { return false; }
        }

        private static void ShowForShot(Form f)
        {
            f.ShowInTaskbar = false;
            f.Show();
            f.TopMost = true;
            f.Activate();
            Pump(1500); // Aufbau und Symbole
        }

        private static void Shot(Backdrop backdrop, Func<Form> create, string file)
        {
            using (var f = create())
            {
                ShowForShot(f);
                Save(Capture(backdrop, f.Handle, FrameBounds(f), Margin(f)), file);
                f.Close();
            }
            Pump(300);
        }

        private static int S(Control c, float v)
        {
            try { using (var g = c.CreateGraphics()) return (int)Math.Round(v * g.DpiX / 96f); }
            catch { return (int)v; }
        }

        /// <summary>Sichtbarer Fensterrahmen ohne den unsichtbaren Größenänderungsrand.</summary>
        private static Rectangle FrameBounds(Form f)
        {
            RECT r;
            return DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : f.Bounds;
        }

        /// <summary>Rand um das Fenster für den Schatten (24 Einheiten).</summary>
        private static int Margin(Control c)
        {
            return S(c, 24);
        }

        /// <summary>Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit.</summary>
        private static Bitmap Capture(Backdrop backdrop, IntPtr target, Rectangle bounds, int margin)
        {
            // nur der sichtbare Bildschirm: liegt das Fenster am Rand, wäre der Bereich daneben sonst schwarz
            var r = Rectangle.Intersect(Rectangle.Inflate(bounds, margin, margin), Screen.FromHandle(target).Bounds);
            backdrop.Cover(r, Color.Black, target);
            Pump(300);
            using (var onBlack = Grab(r))
            {
                backdrop.Cover(r, Color.White, target);
                Pump(300);
                using (var onWhite = Grab(r))
                {
                    backdrop.Hide();
                    return Combine(onBlack, onWhite);
                }
            }
        }

        private static Bitmap Grab(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        private static Bitmap Combine(Bitmap onBlack, Bitmap onWhite)
        {
            int w = onBlack.Width, h = onBlack.Height;
            var rect = new Rectangle(0, 0, w, h);
            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var wd = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var od = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = bd.Stride * h;
                var b = new byte[n];
                var wh = new byte[n];
                var o = new byte[n];
                Marshal.Copy(bd.Scan0, b, 0, n);
                Marshal.Copy(wd.Scan0, wh, 0, n);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    int diff = ((wh[i] - b[i]) + (wh[i + 1] - b[i + 1]) + (wh[i + 2] - b[i + 2])) / 3;
                    int a = Math.Max(0, Math.Min(255, 255 - diff));
                    if (a == 0) continue; // ganz durchsichtig
                    for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, b[i + c] * 255 / a);
                    o[i + 3] = (byte)a;
                }
                Marshal.Copy(o, 0, od.Scan0, n);
            }
            finally
            {
                onBlack.UnlockBits(bd);
                onWhite.UnlockBits(wd);
                result.UnlockBits(od);
            }
            return result;
        }

        private static void Save(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }

        /// <summary>Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden).</summary>
        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Einfarbige Fläche hinter dem aufgenommenen Bereich.</summary>
        private sealed class Backdrop : Form
        {
            public Backdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void Cover(Rectangle r, Color color, IntPtr target)
            {
                BackColor = color;
                Bounds = r;
                if (!Visible) Show();
                // vor alle anderen Fenster legen, danach das aufgenommene Fenster wieder davor
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                Refresh();
            }

            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}