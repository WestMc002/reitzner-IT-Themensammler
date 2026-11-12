// reitzner IT-Themensammler – Verwaltungsfenster: Dienst, Webserver, Admin-Konten und Daten
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>
    /// Verwaltungsfenster auf dem Server: Dienst einrichten und steuern, Port, Admin-Konten anlegen und Daten übernehmen.
    /// Alles Übrige (Themen, Stammdaten, Kundenlogo) erledigt der Admin in der Weboberfläche.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int WM_NCHITTEST = 0x0084, HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
            HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        private readonly float k;
        private readonly ToolTip _tip = new ModernToolTip();
        private readonly Panel _body;
        private readonly Label _message;
        private readonly Timer _timer = new Timer { Interval = 2000 };

        // Dienst
        private readonly CardPanel _cardService;
        private readonly PictureBox _serviceGlyph;
        private readonly Label _serviceState;
        private readonly LinkLabel _serviceLink;
        private readonly Label _serviceDetail;
        private readonly ModernButton _btnInstall, _btnStart, _btnStop, _btnUpdate, _btnRemove, _btnTest, _btnBrowser;
        private DienstZustand _zustand = DienstZustand.Unbekannt;
        /// <summary>
        /// Gewünschte Sichtbarkeit der Knöpfe und des Links. Control.Visible liefert false, solange das Fenster noch nicht
        /// angezeigt ist – zum Anordnen deshalb diese Liste verwenden.
        /// </summary>
        private readonly HashSet<Control> _sichtbar = new HashSet<Control>();
        private WebServer _test;

        // Webserver (Einstellungsseite: erst mit "Speichern" übernehmen)
        private readonly CardPanel _cardServer;
        private readonly ModernTextBox _port;
        // Zugriff von außen über den Entra-Anwendungsproxy (Microsoft 365)
        private readonly CardPanel _cardProxy;
        private readonly ModernCheck _proxyAn;
        private readonly ModernTextBox _proxyPort, _proxyIps, _kopfUpn, _kopfName;
        private readonly Label[] _proxyLabels;
        private readonly Label _proxyHint;
        private readonly ModernButton _save, _cancel;
        private readonly Label _error;

        // Admin-Konten
        private readonly CardPanel _cardAccounts;
        private readonly ModernButton _btnAddAccount;
        private readonly List<Control> _accountRows = new List<Control>();

        // Daten
        private readonly CardPanel _cardData;
        private readonly Label _dataInfo;

        // Update-Prüfung (globale Vorgabe "Updates", Vorlage: Wake on LAN)
        private readonly Timer _updateTimer = new Timer { Interval = 15000 };
        private readonly Panel _updateHost;   // Hinweisleiste oben, nur bei neuer Version sichtbar
        private readonly UpdateBar _updateBar;
        private UpdateInfo _update;           // neuere Version (null = keine)
        private bool _updateRunning, _installing;
        private string _updateError;          // Fehler der letzten Prüfung von Hand (Info-Fenster)
        /// <summary>Neustart durch das Update-Skript (--nach-update): Ergebnis melden.</summary>
        public static bool AfterUpdate;

        public MainForm()
        {
            Theme.Refresh();
            Text = Ui.AppName;
            Icon = Ui.WindowIcon();
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            var header = new HeaderPanel(Ui.AppName, "Themen der Mitarbeiter für den nächsten Vor-Ort-Termin sammeln", k);
            var footer = new SignatureFooter(k);

            // Meldungszeile über dem Footer
            _message = new Label
            {
                Dock = DockStyle.Bottom,
                Height = S(28),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ui.Muted,
                BackColor = Ui.Back,
                Padding = new Padding(S(28), 0, S(28), 0),
                UseMnemonic = false
            };

            _body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, AutoScroll = true };

            // ---------- Karte Dienst ----------
            _cardService = new CardPanel(k) { Title = "Dienst" };
            _serviceGlyph = new PictureBox { Size = new Size(S(20), S(20)), Location = new Point(S(20), S(50)), BackColor = Ui.Card };
            _serviceState = new Label { AutoSize = true, Location = new Point(S(48), S(50)), Font = new Font("Segoe UI Semibold", 10f), ForeColor = Ui.Text, BackColor = Ui.Card, UseMnemonic = false };
            _serviceLink = new LinkLabel { AutoSize = true, BackColor = Ui.Card, LinkColor = Ui.AccentText, ActiveLinkColor = Ui.AccentText, VisitedLinkColor = Ui.AccentText, LinkBehavior = LinkBehavior.HoverUnderline, Font = new Font("Segoe UI", 10f) };
            _serviceLink.LinkClicked += (s, e) => Ui.Open(_serviceLink.Text);
            _serviceDetail = new Label { Location = new Point(S(48), S(76)), ForeColor = Ui.Muted, BackColor = Ui.Card, AutoEllipsis = true, UseMnemonic = false };
            _btnInstall = new ModernButton("Dienst einrichten", MenuHelper.Download, true, k);
            _btnStart = new ModernButton("Starten", MenuHelper.Power, true, k);
            _btnStop = new ModernButton("Beenden", MenuHelper.Power, false, k);
            _btnUpdate = new ModernButton("Dienst aktualisieren", MenuHelper.Sync, true, k);
            _btnRemove = new ModernButton("Entfernen", MenuHelper.Delete, false, k);
            _btnTest = new ModernButton("Testweise starten", MenuHelper.Power, false, k);
            _btnBrowser = new ModernButton("Im Browser öffnen", MenuHelper.Link, false, k);
            _btnInstall.Click += (s, e) => Ausfuehren("Dienst wird eingerichtet …", () =>
            {
                TestBeenden();
                DienstSteuerung.Einrichten(Speicher.EinstellungenLesen().port);
            }, "Dienst eingerichtet und gestartet.");
            _btnStart.Click += (s, e) => Ausfuehren("Dienst wird gestartet …", DienstSteuerung.Starten, "Dienst gestartet.");
            _btnStop.Click += (s, e) => Ausfuehren("Dienst wird beendet …", DienstSteuerung.Beenden, "Dienst beendet.");
            _btnUpdate.Click += (s, e) => Ausfuehren("Dienst wird aktualisiert …", DienstSteuerung.Aktualisieren, "Dienst auf Version " + AppInfo.Version + " aktualisiert.");
            _btnRemove.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Der Dienst und die Firewall-Regel werden entfernt. Die Themen, Einstellungen und Admin-Konten im Datenordner bleiben erhalten.\r\n\r\nDienst entfernen?",
                    Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                Ausfuehren("Dienst wird entfernt …", DienstSteuerung.Entfernen, "Dienst entfernt. Die Daten bleiben erhalten.");
            };
            _btnTest.Click += (s, e) => TestUmschalten();
            _btnBrowser.Click += (s, e) => Ui.Open(Adresse());
            _tip.SetToolTip(_btnTest, "Webserver nur solange dieses Fenster offen ist (ohne Dienst), z. B. zum Ausprobieren");
            _tip.SetToolTip(_btnUpdate, "Diese Programmversion als Dienst übernehmen");
            foreach (Control c in new Control[] { _serviceGlyph, _serviceState, _serviceLink, _serviceDetail, _btnInstall, _btnStart, _btnStop, _btnUpdate, _btnRemove, _btnTest, _btnBrowser })
                _cardService.Controls.Add(c);

            // ---------- Karte Webserver ----------
            _cardServer = new CardPanel(k) { Title = "Webserver" };
            var portLabel = new Label { Text = "Port", AutoSize = true, Location = new Point(S(20), S(54)), ForeColor = Ui.Text, BackColor = Ui.Card };
            _port = new ModernTextBox(k) { Location = new Point(S(120), S(47)), Width = S(110) };
            _port.Box.MaxLength = 5;
            _port.Box.TextChanged += (s, e) => { _error.Text = ""; GeaendertAnzeigen(); };
            var portHint = new Label
            {
                Text = "Unter dieser Adresse erreichen die Mitarbeiter die Weboberfläche. Die Firewall-Regel für Domäne und privates Netz wird beim Speichern angepasst.",
                Location = new Point(S(20), S(92)),
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                UseMnemonic = false
            };
            _cardServer.Controls.Add(portLabel);
            _cardServer.Controls.Add(_port);
            _cardServer.Controls.Add(portHint);
            _cardServer.Tag = portHint;

            // ---------- Karte Zugriff von außen (Microsoft 365) ----------
            _cardProxy = new CardPanel(k) { Title = "Zugriff von außen (Microsoft 365)" };
            _proxyAn = new ModernCheck("Über den Entra-Anwendungsproxy erreichbar (Anmeldung mit Microsoft 365)", k) { BackColor = Ui.Card, ForeColor = Ui.Text };
            _proxyAn.CheckedChanged += (s, e) => { ProxyFelderSchalten(); _error.Text = ""; GeaendertAnzeigen(); };
            _proxyPort = new ModernTextBox(k) { Width = S(110) };
            _proxyPort.Box.MaxLength = 5;
            _proxyIps = new ModernTextBox(k);
            _proxyIps.Box.MaxLength = 200;
            _kopfUpn = new ModernTextBox(k) { Width = S(260) };
            _kopfUpn.Box.MaxLength = 60;
            _kopfName = new ModernTextBox(k) { Width = S(260) };
            _kopfName.Box.MaxLength = 60;
            foreach (var t in new[] { _proxyPort, _proxyIps, _kopfUpn, _kopfName })
                t.Box.TextChanged += (s, e) => { _error.Text = ""; GeaendertAnzeigen(); };
            _proxyLabels = new[] { "Port", "Connector (IP-Adressen)", "Kopfzeile UPN", "Kopfzeile Name" }
                .Select(t => new Label { Text = t, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, UseMnemonic = false }).ToArray();
            _proxyHint = new Label
            {
                Text = "Eigener Port nur für den Connector des Anwendungsproxys: Anfragen von anderen Adressen werden abgewiesen, die Firewall-Regel " +
                       "lässt nur die Connector-IPs zu (mehrere mit Komma trennen). Im Anwendungsproxy „Headerbasiert“ als einmaliges Anmelden wählen und die " +
                       "beiden Kopfzeilen mit user.userprincipalname und user.displayname belegen. Einzelheiten in der Liesmich.",
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                UseMnemonic = false
            };
            _cardProxy.Controls.Add(_proxyAn);
            foreach (Control c in _proxyLabels) _cardProxy.Controls.Add(c);
            foreach (Control c in new Control[] { _proxyPort, _proxyIps, _kopfUpn, _kopfName, _proxyHint }) _cardProxy.Controls.Add(c);
            _tip.SetToolTip(_proxyIps, "Interne IP-Adresse(n) des Servers bzw. der Server mit dem Microsoft Entra Private Network Connector");

            _save = new ModernButton("Speichern", '\0', true, k);
            _cancel = new ModernButton("Abbrechen", '\0', false, k);
            _save.Click += (s, e) => Speichern();
            _cancel.Click += (s, e) => EinstellungenLaden();
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };

            // ---------- Karte Admin-Konten ----------
            _cardAccounts = new CardPanel(k) { Title = "Admin-Konten" };
            _btnAddAccount = new ModernButton("Konto hinzufügen", MenuHelper.Add, false, k) { HeightUnits = 32 };
            _btnAddAccount.UpdateSize();
            _btnAddAccount.Click += (s, e) => KontoBearbeiten(null);
            _cardAccounts.Controls.Add(_btnAddAccount);

            // ---------- Karte Daten ----------
            _cardData = new CardPanel(k) { Title = "Daten" };
            _dataInfo = new Label { Location = new Point(S(20), S(48)), ForeColor = Ui.Text, BackColor = Ui.Card, AutoEllipsis = true, UseMnemonic = false };
            var openFolder = new ModernButton("Datenordner öffnen", MenuHelper.Folder, false, k);
            openFolder.Click += (s, e) => Ui.Open(Speicher.Ordner);
            var import = new ModernButton("Daten übernehmen …", MenuHelper.Open, false, k);
            import.Click += (s, e) => Importieren();
            _tip.SetToolTip(import, "Themen und Stammdaten aus der bisherigen Excel-Liste (.xlsm) oder aus einer daten.json übernehmen");
            openFolder.Location = new Point(S(20), S(108));
            import.Location = new Point(openFolder.Right + S(10), S(108));
            _cardData.Controls.Add(_dataInfo);
            _cardData.Controls.Add(openFolder);
            _cardData.Controls.Add(import);
            _cardData.Height = S(108) + openFolder.Height + S(20);

            _body.Controls.Add(_cardService);
            _body.Controls.Add(_cardServer);
            _body.Controls.Add(_cardProxy);
            _body.Controls.Add(_save);
            _body.Controls.Add(_cancel);
            _body.Controls.Add(_error);
            _body.Controls.Add(_cardAccounts);
            _body.Controls.Add(_cardData);

            // Hinweisleiste "Neue Version verfügbar" unter dem Kopf, bündig mit den Karten
            _updateBar = new UpdateBar(k) { Dock = DockStyle.Fill };
            _updateBar.Install.Click += async (s, e) => await InstallUpdate();
            _updateBar.Page.Click += (s, e) => OpenReleasePage();
            _updateBar.Later.Click += (s, e) => ShowUpdateBar(false);
            _updateHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(64),
                BackColor = Ui.Back,
                Padding = new Padding(S(28), S(10), S(28) + SystemInformation.VerticalScrollBarWidth, S(2)),
                Visible = false
            };
            _updateHost.Controls.Add(_updateBar);

            Controls.Add(_body);
            Controls.Add(_message);
            Controls.Add(footer);
            Controls.Add(_updateHost);
            Controls.Add(header);

            ClientSize = new Size(S(780), Math.Min(S(900), Screen.FromPoint(Cursor.Position).WorkingArea.Height - S(80)));
            AcceptButton = _save;
            CancelButton = _cancel;

            EinstellungenLaden();
            KontenAnzeigen();
            DatenAnzeigen();
            ZustandAnzeigen();
            Anordnen();

            _timer.Tick += (s, e) => { ZustandAnzeigen(); };
            _timer.Start();
            if (!Demo.Active)
            {
                AboutDialog.Updates = MakeUpdateHooks(); // Info-Fenster: Stand, "Jetzt prüfen", automatisch prüfen
                StartUpdateTimer();
            }
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Breite fest, Höhe frei veränderbar (Inhalt rollt)
            // (Höhe 0 bei MaximumSize heißt bei Fenstern nicht "unbegrenzt", sondern klappt das Fenster zusammen)
            MaximumSize = new Size(Width, Math.Max(SystemInformation.VirtualScreen.Height, Height) * 2);
            MinimumSize = new Size(Width, Math.Min(S(520), Height));
            Ui.CenterOnScreen(this);
            if (Speicher.EinstellungenLesen().konten.Count == 0)
                Meldung("Noch kein Admin-Konto angelegt – bitte zuerst unter „Admin-Konten“ ein Konto hinzufügen.", false);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCHITTEST) return;
            // feste Breite: links/rechts keine Größenänderung, an den Ecken nur die Höhe
            switch (m.Result.ToInt32())
            {
                case HTLEFT:
                case HTRIGHT: m.Result = (IntPtr)HTCLIENT; break;
                case HTTOPLEFT:
                case HTTOPRIGHT: m.Result = (IntPtr)HTTOP; break;
                case HTBOTTOMLEFT:
                case HTBOTTOMRIGHT: m.Result = (IntPtr)HTBOTTOM; break;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_installing && _test != null && _test.Laeuft &&
                MessageBox.Show(this, "Der Webserver läuft testweise und wird mit dem Fenster beendet.\r\n\r\nTrotzdem schließen?", Ui.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _timer.Stop();
            _updateTimer.Stop();
            TestBeenden();
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ Anordnung

        /// <summary>Karten untereinander anordnen (Breite fest; Höhe der Konten-Karte hängt von der Zahl der Konten ab).</summary>
        private void Anordnen()
        {
            int x = S(28), y = S(4);
            int w = ClientSize.Width - 2 * S(28) - SystemInformation.VerticalScrollBarWidth;
            var scroll = _body.AutoScrollPosition;

            // Dienst
            _cardService.SetBounds(x + scroll.X, y + scroll.Y, w, 0);
            _serviceDetail.Width = w - S(48) - S(20);
            int bx = S(20), by = S(108);
            foreach (var b in new[] { _btnInstall, _btnStart, _btnStop, _btnUpdate, _btnTest, _btnBrowser, _btnRemove })
            {
                if (!_sichtbar.Contains(b)) continue;
                b.Location = new Point(bx, by);
                bx = b.Right + S(10);
            }
            // "Entfernen" rechts außen
            if (_sichtbar.Contains(_btnRemove)) _btnRemove.Location = new Point(w - S(20) - _btnRemove.Width, by);
            _cardService.Height = by + _btnInstall.Height + S(20);
            y += _cardService.Height + S(16);

            // Webserver mit Speichern/Abbrechen darunter
            _cardServer.SetBounds(x + scroll.X, y + scroll.Y, w, 0);
            var hint = (Label)_cardServer.Tag;
            hint.Width = w - S(40);
            hint.Height = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(hint.Width, 0), TextFormatFlags.WordBreak).Height + S(4);
            _cardServer.Height = hint.Bottom + S(18);
            y += _cardServer.Height + S(16);

            // Zugriff von außen (Microsoft 365)
            _cardProxy.SetBounds(x + scroll.X, y + scroll.Y, w, 0);
            _proxyAn.Location = new Point(S(20), S(44));
            int py = S(44) + _proxyAn.Height + S(10);
            var felder = new[] { _proxyPort, _proxyIps, _kopfUpn, _kopfName };
            for (int i = 0; i < felder.Length; i++)
            {
                _proxyLabels[i].Location = new Point(S(20), py + S(7));
                felder[i].Location = new Point(S(200), py);
                if (felder[i] == _proxyIps) felder[i].Width = Math.Max(S(200), w - S(220));
                py += felder[i].Height + S(10);
            }
            _proxyHint.SetBounds(S(20), py + S(2), w - S(40), 0);
            _proxyHint.Height = TextRenderer.MeasureText(_proxyHint.Text, _proxyHint.Font, new Size(_proxyHint.Width, 0), TextFormatFlags.WordBreak).Height + S(4);
            _cardProxy.Height = _proxyHint.Bottom + S(18);
            y += _cardProxy.Height + S(12);
            // "Abbrechen" links, "Speichern" (Primärknopf) ganz rechts, davor die Fehlerzeile
            _save.Location = new Point(x + w - _save.Width + scroll.X, y + scroll.Y);
            _cancel.Location = new Point(_save.Left - S(10) - _cancel.Width, y + scroll.Y);
            _error.SetBounds(x + scroll.X, y + scroll.Y, _cancel.Left - S(12) - x - scroll.X, _save.Height);
            y += _save.Height + S(20);

            // Admin-Konten
            _cardAccounts.SetBounds(x + scroll.X, y + scroll.Y, w, 0);
            _btnAddAccount.Location = new Point(w - S(20) - _btnAddAccount.Width, S(12));
            int ry = S(52);
            foreach (var row in _accountRows)
            {
                row.SetBounds(S(12), ry, w - S(24), S(44));
                ry += S(44);
            }
            _cardAccounts.Height = ry + S(14);
            y += _cardAccounts.Height + S(16);

            // Daten
            _cardData.SetBounds(x + scroll.X, y + scroll.Y, w, _cardData.Height);
            _dataInfo.SetBounds(S(20), S(46), w - S(40), S(52));
            y += _cardData.Height + S(28);

            // Platz unten zum Rollen
            _body.AutoScrollMinSize = new Size(0, y);
        }

        // ------------------------------------------------------------------ Dienst

        private string Adresse()
        {
            int port = _test != null && _test.Laeuft ? _test.Port : Speicher.EinstellungenLesen().port;
            string host;
            try { host = Demo.Active ? "server" : Dns.GetHostName(); } // Screenshots: kein echter Rechnername
            catch { host = "localhost"; }
            return "http://" + host.ToLowerInvariant() + (port == 80 ? "" : ":" + port.ToString(CultureInfo.InvariantCulture)) + "/";
        }

        private void ZustandAnzeigen()
        {
            var z = DienstSteuerung.Zustand();
            bool test = _test != null && _test.Laeuft;
            bool neuer = z != DienstZustand.NichtEingerichtet && DienstSteuerung.NeuereVersion();
            string zustandText;
            char glyph;
            Color farbe;
            switch (z)
            {
                case DienstZustand.Laeuft: zustandText = "Dienst läuft"; glyph = MenuHelper.Completed; farbe = Ui.Green; break;
                case DienstZustand.Gestoppt: zustandText = "Dienst ist beendet"; glyph = MenuHelper.Warning; farbe = Ui.Red; break;
                case DienstZustand.Startet: zustandText = "Dienst startet …"; glyph = MenuHelper.Sync; farbe = Ui.Muted; break;
                case DienstZustand.Stoppt: zustandText = "Dienst wird beendet …"; glyph = MenuHelper.Sync; farbe = Ui.Muted; break;
                case DienstZustand.NichtEingerichtet:
                    zustandText = test ? "Läuft testweise (ohne Dienst)" : "Dienst ist nicht eingerichtet";
                    glyph = test ? MenuHelper.Completed : MenuHelper.Info;
                    farbe = test ? Ui.Yellow : Ui.Muted;
                    break;
                default: zustandText = "Zustand unbekannt"; glyph = MenuHelper.Warning; farbe = Ui.Muted; break;
            }
            bool erreichbar = z == DienstZustand.Laeuft || test;

            if (_serviceState.Text != zustandText || _zustand != z || _sichtbar.Contains(_serviceLink) != erreichbar || _sichtbar.Contains(_btnUpdate) != neuer)
            {
                _zustand = z;
                _serviceState.Text = zustandText;
                var old = _serviceGlyph.Image;
                _serviceGlyph.Image = MenuHelper.Glyph(glyph, farbe, S(18));
                if (old != null) old.Dispose();
                _serviceLink.Text = Adresse();
                Zeigen(_serviceLink, erreichbar);
                _serviceLink.Location = new Point(_serviceState.Right + S(12), _serviceState.Top);

                string detail;
                if (z == DienstZustand.NichtEingerichtet)
                    detail = test
                        ? "Der Webserver läuft nur, solange dieses Fenster offen ist."
                        : "Zum dauerhaften Betrieb den Dienst einrichten: Er startet mit Windows, auch ohne angemeldeten Benutzer.";
                else if (neuer)
                    detail = "Eingerichtet ist Version " + DienstSteuerung.InstallierteVersion() + " – dieses Fenster hat Version " + AppInfo.Version + ".";
                else
                    detail = "Startet automatisch mit Windows · Version " + DienstSteuerung.InstallierteVersion() + " · " + DienstSteuerung.ProgrammOrdner;
                _serviceDetail.Text = detail;
                _tip.SetToolTip(_serviceDetail, detail);
                _serviceDetail.Height = S(22);

                bool eingerichtet = z != DienstZustand.NichtEingerichtet && z != DienstZustand.Unbekannt;
                Zeigen(_btnInstall, z == DienstZustand.NichtEingerichtet);
                Zeigen(_btnTest, z == DienstZustand.NichtEingerichtet);
                _btnTest.Text = test ? "Test beenden" : "Testweise starten";
                Zeigen(_btnStart, z == DienstZustand.Gestoppt);
                Zeigen(_btnStop, z == DienstZustand.Laeuft);
                Zeigen(_btnUpdate, neuer);
                Zeigen(_btnRemove, eingerichtet);
                Zeigen(_btnBrowser, erreichbar);
                Anordnen();
            }
        }

        private void Zeigen(Control c, bool an)
        {
            c.Visible = an;
            if (an) _sichtbar.Add(c); else _sichtbar.Remove(c);
        }

        /// <summary>Aktion mit Wartecursor ausführen; Fehler erscheinen als Meldung.</summary>
        private void Ausfuehren(string waehrend, Action aktion, string danach)
        {
            Meldung(waehrend, false);
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                aktion();
                Meldung(danach, false);
            }
            catch (Exception ex)
            {
                Speicher.Fehler(waehrend, ex);
                Meldung(ex.Message.Split('\n')[0].Trim(), true);
                MessageBox.Show(this, ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
                _zustand = DienstZustand.Unbekannt;
                _serviceState.Text = "";
                ZustandAnzeigen();
            }
        }

        private void TestUmschalten()
        {
            if (_test != null && _test.Laeuft)
            {
                TestBeenden();
                Meldung("Testbetrieb beendet.", false);
            }
            else
            {
                try
                {
                    _test = new WebServer();
                    _test.Starten();
                    Meldung("Webserver läuft testweise: " + Adresse(), false);
                }
                catch (Exception ex)
                {
                    _test = null;
                    Meldung(ex.Message, true);
                }
            }
            _serviceState.Text = "";
            ZustandAnzeigen();
        }

        private void TestBeenden()
        {
            if (_test == null) return;
            _test.Stoppen();
            _test = null;
        }

        // ------------------------------------------------------------------ Webserver (Speichern / Abbrechen)

        private int _gespeicherterPort;

        private Einstellungen _gespeichert;

        private void EinstellungenLaden()
        {
            _gespeichert = Speicher.EinstellungenLesen();
            _gespeicherterPort = _gespeichert.port;
            _port.Text = _gespeicherterPort.ToString(CultureInfo.InvariantCulture);
            _proxyAn.Checked = _gespeichert.proxyAn == true;
            _proxyPort.Text = _gespeichert.proxyPort.ToString(CultureInfo.InvariantCulture);
            _proxyIps.Text = string.Join(", ", _gespeichert.proxyIps.ToArray());
            _kopfUpn.Text = _gespeichert.proxyKopfUpn;
            _kopfName.Text = _gespeichert.proxyKopfName;
            ProxyFelderSchalten();
            _error.Text = "";
            GeaendertAnzeigen();
        }

        /// <summary>Felder des Anwendungsproxys nur mit eingeschaltetem Schalter bearbeitbar (sofort sichtbar, übernommen erst beim Speichern).</summary>
        private void ProxyFelderSchalten()
        {
            foreach (Control c in new Control[] { _proxyPort, _proxyIps, _kopfUpn, _kopfName }) c.Enabled = _proxyAn.Checked;
            foreach (var l in _proxyLabels) l.ForeColor = _proxyAn.Checked ? Ui.Text : Ui.Muted;
        }

        private static List<string> IpListe(string text)
        {
            return text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        }

        private bool ProxyGeaendert()
        {
            if (_gespeichert == null) return false;
            return _proxyAn.Checked != (_gespeichert.proxyAn == true)
                || _proxyPort.Text.Trim() != _gespeichert.proxyPort.ToString(CultureInfo.InvariantCulture)
                || string.Join(",", IpListe(_proxyIps.Text).ToArray()) != string.Join(",", _gespeichert.proxyIps.ToArray())
                || _kopfUpn.Text.Trim() != _gespeichert.proxyKopfUpn
                || _kopfName.Text.Trim() != _gespeichert.proxyKopfName;
        }

        private void GeaendertAnzeigen()
        {
            bool geaendert = _port.Text.Trim() != _gespeicherterPort.ToString(CultureInfo.InvariantCulture) || ProxyGeaendert();
            _save.Enabled = geaendert;
            _cancel.Enabled = geaendert;
        }

        private void Speichern()
        {
            int port;
            if (!int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                _error.Text = "Bitte einen Port zwischen 1 und 65535 eingeben.";
                return;
            }
            bool proxyAn = _proxyAn.Checked;
            int proxyPort;
            var ips = IpListe(_proxyIps.Text);
            string kopfUpn = _kopfUpn.Text.Trim(), kopfName = _kopfName.Text.Trim();
            if (!int.TryParse(_proxyPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out proxyPort) || proxyPort < 1 || proxyPort > 65535)
            {
                if (proxyAn) { _error.Text = "Bitte für den Anwendungsproxy einen Port zwischen 1 und 65535 eingeben."; return; }
                proxyPort = _gespeichert.proxyPort;
            }
            if (proxyAn)
            {
                if (proxyPort == port) { _error.Text = "Der Anwendungsproxy braucht einen eigenen Port (nicht " + port + ")."; return; }
                if (ips.Count == 0) { _error.Text = "Bitte die IP-Adresse des Connectors eintragen."; return; }
                foreach (string ip in ips)
                {
                    System.Net.IPAddress a;
                    if (!System.Net.IPAddress.TryParse(ip, out a)) { _error.Text = "„" + ip + "“ ist keine gültige IP-Adresse."; return; }
                }
                foreach (string kz in new[] { kopfUpn, kopfName })
                    if (!System.Text.RegularExpressions.Regex.IsMatch(kz, "^[A-Za-z][A-Za-z0-9-]{1,59}$"))
                    {
                        _error.Text = "Ungültiger Name einer Kopfzeile: „" + kz + "“ (nur Buchstaben, Ziffern und Bindestrich).";
                        return;
                    }
            }
            bool proxyGeaendert = ProxyGeaendert();
            if (port == _gespeicherterPort && !proxyGeaendert) return;
            var z = DienstSteuerung.Zustand();
            if (port != _gespeicherterPort && PortBelegt(port))
            {
                _error.Text = "Port " + port + " ist belegt.";
                return;
            }
            bool proxyPortNeu = proxyAn && (_gespeichert.proxyAn != true || proxyPort != _gespeichert.proxyPort);
            if (proxyPortNeu && PortBelegt(proxyPort))
            {
                _error.Text = "Port " + proxyPort + " ist belegt.";
                return;
            }
            try
            {
                Cursor = Cursors.WaitCursor;
                Speicher.EinstellungenAendern(e =>
                {
                    e.port = port;
                    e.proxyAn = proxyAn;
                    e.proxyPort = proxyPort;
                    e.proxyIps = ips;
                    if (kopfUpn.Length > 0) e.proxyKopfUpn = kopfUpn;
                    if (kopfName.Length > 0) e.proxyKopfName = kopfName;
                });
                if (z != DienstZustand.NichtEingerichtet && z != DienstZustand.Unbekannt)
                {
                    DienstSteuerung.FirewallSetzen(port);
                    if (z == DienstZustand.Laeuft) DienstSteuerung.Neustarten();
                }
                if (_test != null && _test.Laeuft) _test.Starten();
                if (port != _gespeicherterPort) Speicher.Protokoll("Port geändert auf " + port);
                if (proxyGeaendert)
                    Speicher.Protokoll("Zugriff von außen (Anwendungsproxy) " + (proxyAn ? "an: Port " + proxyPort + ", Connector " + string.Join(", ", ips.ToArray()) : "aus"));
                EinstellungenLaden();
                Meldung("Gespeichert. Weboberfläche: " + Adresse(), false);
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Port speichern", ex);
                _error.Text = ex.Message.Split('\n')[0].Trim();
            }
            finally
            {
                Cursor = Cursors.Default;
                _serviceState.Text = "";
                ZustandAnzeigen();
            }
        }

        /// <summary>Lauscht schon ein anderes Programm auf dem Port?</summary>
        private static bool PortBelegt(int port)
        {
            try
            {
                foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    if (ep.Port == port) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ Admin-Konten

        private void KontenAnzeigen()
        {
            foreach (var c in _accountRows)
            {
                _cardAccounts.Controls.Remove(c);
                c.Dispose();
            }
            _accountRows.Clear();
            var konten = Speicher.EinstellungenLesen().konten.OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (konten.Count == 0)
            {
                var leer = new Label
                {
                    Text = "Noch kein Konto. Admins melden sich in der Weboberfläche oben rechts mit „Anmelden“ an und können dann Themen bearbeiten, erledigen und löschen, Stammdaten pflegen und das Kundenlogo austauschen.",
                    ForeColor = Ui.Muted,
                    BackColor = Ui.Card,
                    Padding = new Padding(S(8), S(4), S(8), 0),
                    UseMnemonic = false
                };
                _accountRows.Add(leer);
                _cardAccounts.Controls.Add(leer);
            }
            foreach (var konto in konten)
            {
                var name = konto.name;
                var row = new Panel { BackColor = Ui.Card };
                var glyph = new PictureBox { Image = MenuHelper.Glyph(MenuHelper.Lock, Ui.Muted, S(16)), Size = new Size(S(16), S(16)), Location = new Point(S(8), S(14)), BackColor = Ui.Card };
                var text = new Label { Text = name, AutoSize = true, Location = new Point(S(36), S(12)), Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = Ui.Text, BackColor = Ui.Card, UseMnemonic = false };
                string seit = Datum(konto.erstellt);
                var info = new Label { Text = seit.Length > 0 ? "angelegt am " + seit : "", AutoSize = true, ForeColor = Ui.Muted, BackColor = Ui.Card, UseMnemonic = false };
                var pw = new ModernButton("Kennwort ändern", MenuHelper.Edit, false, k) { HeightUnits = 32 };
                pw.UpdateSize();
                var del = new ModernButton("", MenuHelper.Delete, false, k) { HeightUnits = 32 };
                del.UpdateSize();
                _tip.SetToolTip(del, "Konto löschen");
                pw.Click += (s, e) => KontoBearbeiten(name);
                del.Click += (s, e) =>
                {
                    if (MessageBox.Show(this, "Angemeldete Sitzungen dieses Kontos enden sofort.\r\n\r\nKonto „" + name + "“ löschen?", Ui.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    Speicher.KontoLoeschen(name);
                    Speicher.Protokoll("Konto gelöscht: " + name);
                    KontenAnzeigen();
                    Anordnen();
                    Meldung("Konto „" + name + "“ gelöscht.", false);
                };
                row.Controls.Add(glyph);
                row.Controls.Add(text);
                row.Controls.Add(info);
                row.Controls.Add(pw);
                row.Controls.Add(del);
                row.Layout += (s, e) =>
                {
                    del.Location = new Point(row.Width - del.Width - S(8), (row.Height - del.Height) / 2);
                    pw.Location = new Point(del.Left - S(8) - pw.Width, (row.Height - pw.Height) / 2);
                    info.Location = new Point(text.Right + S(12), text.Top + (text.Height - info.Height) / 2);
                };
                row.Paint += (s, e) =>
                {
                    using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, S(8), 0, row.Width - S(8), 0);
                };
                _accountRows.Add(row);
                _cardAccounts.Controls.Add(row);
            }
        }

        private static string Datum(string iso)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(iso) && DateTime.TryParseExact(iso, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            return "";
        }

        /// <summary>Konto anlegen (name = null) bzw. Kennwort eines Kontos ändern.</summary>
        private void KontoBearbeiten(string name)
        {
            using (var d = new KontoDialog(name, k))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Speicher.Protokoll((name == null ? "Konto angelegt: " : "Kennwort geändert: ") + d.KontoName);
                KontenAnzeigen();
                Anordnen();
                Meldung(name == null ? "Konto „" + d.KontoName + "“ angelegt." : "Kennwort für „" + d.KontoName + "“ geändert. Angemeldete Sitzungen dieses Kontos enden.", false);
            }
        }

        // ------------------------------------------------------------------ Daten

        private void DatenAnzeigen()
        {
            string text;
            try
            {
                var zahlen = Speicher.Lesen(d => new[]
                {
                    d.anfragen.Count(a => string.IsNullOrEmpty(a.erledigtAm)),
                    d.anfragen.Count(a => !string.IsNullOrEmpty(a.erledigtAm)),
                    d.stammdaten.standorte.Count,
                    d.stammdaten.termine.Count
                });
                text = zahlen[0] + " offene und " + zahlen[1] + " erledigte Themen · " + zahlen[2] + " Standorte · " + zahlen[3] + " Termine\r\n" + Speicher.AnzeigeOrdner;
            }
            catch (Exception ex)
            {
                text = "daten.json konnte nicht gelesen werden: " + ex.Message;
            }
            _dataInfo.Text = text;
            _tip.SetToolTip(_dataInfo, Speicher.AnzeigeOrdner);
        }

        private void Importieren()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Daten übernehmen",
                Filter = "Excel-Liste oder Daten (*.xlsx;*.xlsm;*.json)|*.xlsx;*.xlsm;*.json|Excel-Liste (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|Daten aus dem Entwurf (*.json)|*.json|Alle Dateien (*.*)|*.*",
                CheckFileExists = true
            })
            {
                // Startordner: der zuletzt benutzte (merkt sich Windows), sonst "Dokumente"
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                bool excel = ext == ".xlsx" || ext == ".xlsm";
                string frage = excel
                    ? "Themen und Stammdaten aus der Excel-Liste „" + Path.GetFileName(dlg.FileName) + "“ werden hinzugefügt. Themen, die es schon gibt, werden übersprungen. Der bisherige Stand wird vorher im Ordner Backup gesichert.\r\n\r\nJetzt übernehmen?"
                    : "Die Themen und Stammdaten aus „" + Path.GetFileName(dlg.FileName) + "“ ersetzen den bisherigen Stand. Er wird vorher im Ordner Backup gesichert.\r\n\r\nJetzt übernehmen?";
                if (MessageBox.Show(this, frage, Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    excel ? MessageBoxDefaultButton.Button1 : MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    if (excel)
                    {
                        var erg = ExcelImport.Importieren(dlg.FileName);
                        DatenAnzeigen();
                        string text = erg.Neu + " Themen übernommen" + (erg.Vorhanden > 0 ? ", " + erg.Vorhanden + " waren schon vorhanden" : "") +
                            " · neu: " + erg.Standorte + " Standorte, " + erg.Benutzer + " Benutzer, " + erg.Termine + " Termine.";
                        Meldung(text, false);
                        if (erg.DatumGeschaetzt > 0)
                            MessageBox.Show(this, text + "\r\n\r\nHinweis: Bei " + erg.DatumGeschaetzt + " von " + erg.Erledigt + " erledigten Themen stand in der Excel-Liste " +
                                "kein festes Erledigt-Datum, sondern die Formel HEUTE(). Dort wurde ersatzweise der Wunschtermin als Erledigt-Datum eingetragen.",
                                Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        int n = Speicher.Importieren(dlg.FileName);
                        DatenAnzeigen();
                        Meldung(n + " Themen übernommen.", false);
                    }
                }
                catch (Exception ex)
                {
                    Speicher.Fehler("Daten übernehmen", ex);
                    Meldung("Übernehmen fehlgeschlagen: " + ex.Message, true);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        // ------------------------------------------------------------------ Meldungen

        // ------------------------------------------------------------------ Update-Prüfung (Vorlage: Wake on LAN)

        private void StartUpdateTimer()
        {
            if (AfterUpdate)
            {
                // Neustart durch das Update-Skript: Ergebnis melden
                string result = UpdateCheck.LastResult();
                if (result != null && result.StartsWith("OK"))
                {
                    string v = result.Substring(2).Trim();
                    _message.Text = "Update auf Version " + v + " installiert.";
                    _message.ForeColor = Ui.Green;
                    Speicher.Protokoll("Update auf Version " + v + " installiert");
                }
                else if (result != null)
                {
                    string why = result.Replace("FEHLER", "").Trim();
                    Meldung("Update fehlgeschlagen: " + why + " – die bisherige Version läuft weiter.", true);
                    Speicher.Protokoll("Update fehlgeschlagen: " + why);
                }
            }
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = 6 * 3600000; // danach alle 6 Stunden nachsehen, ob die Prüfung fällig ist
                // hat der Dienst schon eine neue Version gefunden, gleich prüfen (für den Download-Link)
                bool gemeldet = UpdateCheck.IsNewer(Speicher.EinstellungenLesen().updateVersion);
                await CheckForUpdates(false, gemeldet);
            };
            _updateTimer.Start();
        }

        private UpdateHooks MakeUpdateHooks()
        {
            return new UpdateHooks
            {
                Status = UpdateState,
                Auto = () => UpdatePruefung.Auto,
                SetAuto = on =>
                {
                    try { UpdatePruefung.Auto = on; } catch { }
                },
                CheckNow = () => CheckForUpdates(true, true),
                CanInstall = () => _update != null && !string.IsNullOrEmpty(_update.ZipUrl) && UpdateCheck.InstallBlocker() == null,
                Install = () => BeginInvoke(new Action(() => { var t = InstallUpdate(); })), // erst nach dem Schließen des Info-Fensters
                OpenPage = OpenReleasePage
            };
        }

        /// <summary>Stand für das Info-Fenster: Symbol, Überschrift und zweite Zeile.</summary>
        private UpdateStatus UpdateState()
        {
            var letzte = UpdatePruefung.Letzte;
            string last = letzte == DateTime.MinValue ? "" : "Zuletzt geprüft " + letzte.ToLocalTime().ToString("dd.MM.yyyy");
            if (_update != null) return new UpdateStatus(UpdateKind.Available, "Version " + _update.Version + " verfügbar", last);
            if (_updateError != null) return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", _updateError);
            if (letzte == DateTime.MinValue) return new UpdateStatus(UpdateKind.Unknown, "Noch nicht geprüft", "");
            return new UpdateStatus(UpdateKind.Current, "Aktuell", last);
        }

        /// <summary>
        /// Neueste Version auf GitHub abfragen. Automatisch höchstens alle 20 Stunden und still (Fehler ohne Meldung);
        /// von Hand (bzw. wenn der Dienst schon eine neue Version gemeldet hat) sofort.
        /// </summary>
        private async Task<UpdateStatus> CheckForUpdates(bool manual, bool force)
        {
            if (Demo.Active || _updateRunning) return UpdateState();
            if (!force && !UpdatePruefung.Faellig()) return UpdateState();
            if (!manual && !UpdatePruefung.Auto) return UpdateState();
            _updateRunning = true;
            try
            {
                var release = await Task.Run(() => UpdatePruefung.Pruefen());
                _updateError = null;
                if (UpdateCheck.IsNewer(release.Version))
                {
                    _update = release;
                    ShowUpdateBar(true);
                }
                else
                {
                    _update = null;
                    ShowUpdateBar(false);
                }
            }
            catch (Exception ex)
            {
                if (manual) _updateError = ex.Message; // erscheint im Info-Fenster
            }
            finally { _updateRunning = false; }
            return UpdateState();
        }

        private void ShowUpdateBar(bool show)
        {
            show = show && _update != null;
            if (show)
            {
                _updateBar.Text = "Neue Version " + _update.Version + " verfügbar – installiert ist " + Ui.Version + ".";
                string blocker = UpdateCheck.InstallBlocker();
                _updateBar.Install.Enabled = blocker == null && !string.IsNullOrEmpty(_update.ZipUrl);
                _tip.SetToolTip(_updateBar.Install, blocker ?? (string.IsNullOrEmpty(_update.ZipUrl) ? "Im Release ist keine ZIP-Datei angehängt." :
                    "ZIP herunterladen, Verwaltungsfenster und Dienst kurz beenden, Programm und Liesmich ersetzen, alles neu starten.\r\nThemen, Einstellungen und Konten bleiben erhalten."));
                _tip.SetToolTip(_updateBar, string.IsNullOrEmpty(_update.Notes) ? "Die Änderungen stehen auf der Release-Seite." : "Änderungen:\r\n" + _update.Notes);
            }
            if (show == _updateHost.Visible) return;
            _updateHost.Visible = show;
            // Fenster um die Leiste vergrößern, soweit der Bildschirm reicht (globale Vorgabe)
            if (show && WindowState == FormWindowState.Normal)
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int grow = Math.Min(_updateHost.Height, wa.Bottom - Bottom);
                if (grow > 0) Height += grow;
            }
        }

        private void OpenReleasePage()
        {
            Ui.Open(_update != null && !string.IsNullOrEmpty(_update.Url) ? _update.Url : UpdateCheck.ReleasesPage);
        }

        private async Task InstallUpdate()
        {
            if (_update == null || _installing) return;
            var info = _update;
            bool dienst = DienstSteuerung.Zustand() != DienstZustand.NichtEingerichtet;
            string text = Ui.AppName + " wird auf Version " + info.Version + " aktualisiert. " +
                (dienst ? "Dazu werden das Verwaltungsfenster und der Dienst kurz beendet – die Weboberfläche ist für einige Sekunden nicht erreichbar. "
                        : "Dazu wird das Verwaltungsfenster kurz beendet. ") +
                "Themen, Einstellungen und Konten bleiben erhalten.";
            if (MessageBox.Show(this, text + "\r\n\r\nJetzt aktualisieren?", Ui.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _installing = true;
            foreach (var b in new[] { _updateBar.Install, _updateBar.Page, _updateBar.Later }) b.Enabled = false;
            string barText = _updateBar.Text;
            try
            {
                _updateBar.Text = "Lade Version " + info.Version + " …";
                await UpdateCheck.Install(info, percent => BeginInvoke(new Action(() => _updateBar.Text = "Lade Version " + info.Version + " … " + percent + " %")));
                Speicher.Protokoll("Update auf Version " + info.Version + " wird installiert");
                Close(); // das Skript wartet darauf, ersetzt die Dateien und startet neu
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _updateBar.Text = barText;
                _updateBar.Page.Enabled = true;
                _updateBar.Later.Enabled = true;
                _updateBar.Install.Enabled = UpdateCheck.InstallBlocker() == null;
                _installing = false;
            }
        }

        private void Meldung(string text, bool fehler)
        {
            _message.Text = text;
            _message.ForeColor = fehler ? Ui.Red : Ui.Muted;
        }
    }

    /// <summary>Konto anlegen bzw. Kennwort ändern (Speichern/Abbrechen, Fehler in der Zeile links neben den Knöpfen).</summary>
    internal sealed class KontoDialog : FixedForm
    {
        private readonly float k;
        private readonly string _name;
        private readonly ModernTextBox _boxName, _pw1, _pw2;
        private readonly Label _error;

        public string KontoName { get; private set; }

        public KontoDialog(string name, float scale)
        {
            k = scale;
            _name = name;
            Text = name == null ? "Admin-Konto hinzufügen" : "Kennwort ändern";
            Icon = Ui.WindowIcon();
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            int W = S(460);

            var card = new CardPanel(k) { Location = new Point(S(20), S(20)), Size = new Size(W - S(40), S(200)) };
            int y = S(18), lx = S(20), fx = S(160), fw = W - S(40) - fx - S(20);
            _boxName = Feld(card, "Name", lx, fx, fw, ref y, false);
            _pw1 = Feld(card, "Kennwort", lx, fx, fw, ref y, true);
            _pw2 = Feld(card, "Wiederholen", lx, fx, fw, ref y, true);
            var hint = new Label
            {
                Text = "Mindestens 8 Zeichen. Gespeichert wird nur ein Prüfwert, nicht das Kennwort.",
                Location = new Point(lx, y + S(2)),
                Size = new Size(W - S(40) - 2 * lx, S(22)),
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                UseMnemonic = false
            };
            card.Controls.Add(hint);
            card.Height = hint.Bottom + S(16);
            Controls.Add(card);
            if (name != null)
            {
                _boxName.Text = name;
                _boxName.Enabled = false;
            }

            int by = card.Bottom + S(16);
            var save = new ModernButton("Speichern", '\0', true, k);
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            cancel.Location = new Point(W - S(20) - cancel.Width, by);
            save.Location = new Point(cancel.Left - S(10) - save.Width, by);
            _error = new Label { ForeColor = Ui.Red, BackColor = Ui.Back, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
            _error.SetBounds(S(20), by, save.Left - S(32), save.Height);
            save.Click += (s, e) => Speichern();
            Controls.Add(_error);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
            foreach (var b in new[] { _boxName, _pw1, _pw2 }) b.Box.TextChanged += (s, e) => _error.Text = "";

            ClientSize = new Size(W, save.Bottom + S(20));
            Shown += (s, e) => { if (_name == null) _boxName.Box.Focus(); else _pw1.Box.Focus(); };
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private ModernTextBox Feld(Control card, string label, int lx, int fx, int fw, ref int y, bool kennwort)
        {
            var box = new ModernTextBox(k) { Location = new Point(fx, y), Width = fw };
            box.Password = kennwort;
            box.Box.MaxLength = kennwort ? 200 : 50;
            card.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(lx, y + S(7)), ForeColor = Ui.Text, BackColor = Ui.Card });
            card.Controls.Add(box);
            y += box.Height + S(12);
            return box;
        }

        private void Speichern()
        {
            string name = _boxName.Text.Trim();
            string fehler = Speicher.KontoFehler(name, _pw1.Text);
            if (fehler == null && _pw1.Text != _pw2.Text) fehler = "Die Kennwörter stimmen nicht überein.";
            if (fehler == null && _name == null && Speicher.EinstellungenLesen().konten.Any(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase)))
                fehler = "Ein Konto mit diesem Namen gibt es schon.";
            if (fehler != null)
            {
                _error.Text = fehler;
                return;
            }
            try
            {
                Speicher.KennwortSetzen(name, _pw1.Text);
                KontoName = name;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _error.Text = ex.Message;
            }
        }
    }
}