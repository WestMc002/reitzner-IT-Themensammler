// reitzner IT-Themensammler – eingebauter Webserver: Weboberfläche, Schnittstelle und Admin-Anmeldung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections;
using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITThemensammler
{
    /// <summary>Fehler, der als Meldung im Browser erscheint (mit HTTP-Status).</summary>
    internal sealed class WebFehler : Exception
    {
        public readonly int Status;

        public WebFehler(int status, string meldung) : base(meldung)
        {
            Status = status;
        }
    }

    /// <summary>
    /// Kleiner Webserver (HttpListener). Liefert die Weboberfläche aus den eingebetteten Dateien und die Schnittstelle /api/….
    /// Themen eintragen und ansehen darf jeder im Netz; Bearbeiten, Erledigen, Löschen, Stammdaten und Aussehen nur ein
    /// angemeldeter Admin (lokales Konto, Sitzung per Cookie). Mitarbeiter melden sich auf Wunsch still mit dem
    /// Domänenkonto an (Windows-Anmeldung nur unter /api/domaene, eigene Sitzung per Cookie).
    /// </summary>
    internal sealed class WebServer
    {
        public const string Ticket = "ticket";
        private const string CookieName = "ITS-Sitzung";
        /// <summary>Pflicht-Kopfzeile bei ändernden Anfragen: fremde Webseiten können sie ohne Freigabe nicht setzen (Schutz vor CSRF).</summary>
        private const string SchutzHeader = "X-Themensammler";
        private const int SitzungMinuten = 30;
        private const string MitarbeiterCookie = "ITS-Mitarbeiter";
        /// <summary>Pfad mit Windows-Anmeldung (Kerberos/NTLM). Alles andere bleibt ohne Windows-Anmeldung.</summary>
        private const string DomaenePfad = "/api/domaene";
        private const int MitarbeiterStunden = 10;
        private const int MaxJson = 64 * 1024;
        private const int MaxBild = 3 * 1024 * 1024;
        /// <summary>Bild zu einem Thema: höchstens 10 MB beim Hochladen, gespeichert verkleinert als JPEG.</summary>
        private const int MaxAnhang = 10 * 1024 * 1024;
        private const int AnhangKante = 2000;
        private const int MaxFehlversuche = 5;

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _laeuft;
        /// <summary>Port für den Entra-Anwendungsproxy (0 = aus).</summary>
        public int ProxyPort { get; private set; }

        public int Port { get; private set; }

        private sealed class Sitzung
        {
            public string Name;
            public string Hash;
            public DateTime Ablauf;
            /// <summary>Admin über die AD-Gruppe (Anmeldung mit dem Windows-Konto): Name der Gruppe, sonst null.</summary>
            public string AdGruppe;
        }

        /// <summary>Mitarbeiter, angemeldet mit dem Domänenkonto.</summary>
        private sealed class MitarbeiterSitzung
        {
            /// <summary>Kennung für neue Themen, z. B. "ad:gemeinde\mmuster" oder "m365:m.muster@example.com" (klein geschrieben)</summary>
            public string Konto;
            /// <summary>Alle Kennungen derselben Person (Domänen- und Microsoft-365-Konto), für "eigene Themen"</summary>
            public List<string> Kennungen = new List<string>();
            public string Name;
            public DateTime Ablauf;
            /// <summary>"ad" (Windows-Anmeldung im Haus) oder "m365" (über den Anwendungsproxy)</summary>
            public string Quelle = "ad";
            /// <summary>zugeordnetes AD-Konto (für Admins über die AD-Gruppe), sonst null</summary>
            public AdKonto Ad;

            public bool Besitzt(string ersteller)
            {
                return !string.IsNullOrEmpty(ersteller) && Kennungen.Contains(ersteller.ToLowerInvariant());
            }
        }

        private sealed class Sperrzeit
        {
            public int Anzahl;
            public DateTime Bis;
        }

        private readonly Dictionary<string, Sitzung> _sitzungen = new Dictionary<string, Sitzung>();
        private readonly Dictionary<string, MitarbeiterSitzung> _mitarbeiter = new Dictionary<string, MitarbeiterSitzung>();
        private readonly Dictionary<string, Sperrzeit> _fehlversuche = new Dictionary<string, Sperrzeit>();
        private readonly Dictionary<string, byte[]> _dateien = new Dictionary<string, byte[]>();

        public bool Laeuft { get { return _laeuft; } }
        private DateTime _gestartet;

        /// <summary>Webserver auf dem Port aus den Einstellungen starten. Wirft eine verständliche Ausnahme, wenn das nicht geht.</summary>
        public void Starten(int port = 0)
        {
            Stoppen();
            // port > 0 nur bei den Screenshots (freier Port statt des eingestellten)
            Port = port > 0 ? port : Speicher.EinstellungenLesen().port;
            var l = new HttpListener();
            l.Prefixes.Add("http://+:" + Port.ToString(CultureInfo.InvariantCulture) + "/");
            // Zugriff von außen: eigener Port für den Entra-Anwendungsproxy (nur bei Dienst/Test, nicht bei den Screenshots)
            var einst = Speicher.EinstellungenLesen();
            ProxyPort = port == 0 && einst.proxyAn == true && einst.proxyPort != Port ? einst.proxyPort : 0;
            if (ProxyPort > 0) l.Prefixes.Add("http://+:" + ProxyPort.ToString(CultureInfo.InvariantCulture) + "/");
            // Windows-Anmeldung nur für die Domänen-Anmeldung, sonst bliebe die Seite auf Rechnern ohne Domäne nicht frei erreichbar
            l.AuthenticationSchemeSelectorDelegate = req =>
                string.Equals(req.Url.AbsolutePath, DomaenePfad, StringComparison.OrdinalIgnoreCase) && req.HttpMethod == "POST" &&
                (ProxyPort == 0 || req.LocalEndPoint.Port != ProxyPort)
                    ? AuthenticationSchemes.IntegratedWindowsAuthentication
                    : AuthenticationSchemes.Anonymous;
            try
            {
                l.Start();
            }
            catch (HttpListenerException ex)
            {
                l.Close();
                if (ex.ErrorCode == 5) throw new InvalidOperationException("Keine Berechtigung für Port " + Port + ". Bitte als Administrator bzw. als Dienst starten.", ex);
                if (ex.ErrorCode == 32 || ex.ErrorCode == 183) throw new InvalidOperationException("Port " + Port + (ProxyPort > 0 ? " bzw. " + ProxyPort : "") + " ist schon belegt (anderes Programm oder der Dienst läuft bereits).", ex);
                throw new InvalidOperationException("Der Webserver konnte auf Port " + Port + " nicht starten: " + ex.Message, ex);
            }
            _listener = l;
            _laeuft = true;
            _gestartet = DateTime.Now;
            _thread = new Thread(Schleife) { IsBackground = true, Name = "Webserver" };
            _thread.Start();
            if (!Demo.Active)
            {
                AdAbgleich.Starten();
                Aufraeumen.Starten();
            }
            Speicher.Protokoll("Webserver gestartet auf Port " + Port + (ProxyPort > 0 ? ", Anwendungsproxy auf Port " + ProxyPort : ""));
        }

        public void Stoppen()
        {
            if (_listener == null) return;
            AdAbgleich.Beenden();
            Aufraeumen.Beenden();
            _laeuft = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
            Speicher.Protokoll("Webserver beendet");
        }

        private void Schleife()
        {
            var l = _listener;
            while (_laeuft && l != null && l.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = l.GetContext();
                }
                catch (HttpListenerException)
                {
                    break; // beim Beenden
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                ThreadPool.QueueUserWorkItem(o => Bearbeiten((HttpListenerContext)o), ctx);
            }
        }

        // ------------------------------------------------------------------ Verteilung

        private void Bearbeiten(HttpListenerContext ctx)
        {
            string pfad = ctx.Request.Url.AbsolutePath;
            try
            {
                // Proxy-Port: nur Anfragen vom Connector (dessen Kopfzeilen tragen die Microsoft-365-Anmeldung)
                if (ViaProxy(ctx) && !VomConnector(ctx))
                {
                    ProxyAbgewiesen(ctx);
                    throw new WebFehler(403, "Dieser Port ist nur für den Anwendungsproxy.");
                }
                if (pfad.StartsWith("/api/", StringComparison.Ordinal))
                    Api(ctx, pfad.TrimEnd('/'));
                else
                    Datei(ctx, pfad);
            }
            catch (WebFehler wf)
            {
                // Browser kann die Verbindung schon getrennt haben: darf den Dienst nicht beenden
                try { Antworten(ctx, wf.Status, new { fehler = wf.Message }); } catch { }
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Fehler bei " + ctx.Request.HttpMethod + " " + pfad, ex);
                Ereignis.Schreiben(Ereignis.Anfragefehler, System.Diagnostics.EventLogEntryType.Warning,
                    "Fehler bei " + ctx.Request.HttpMethod + " " + pfad + ": " + ex.Message + " (Einzelheiten in Fehler.log)");
                try { Antworten(ctx, 500, new { fehler = "Interner Fehler im Server. Einzelheiten stehen in Fehler.log." }); } catch { }
            }
        }

        private void Datei(HttpListenerContext ctx, string pfad)
        {
            string m = ctx.Request.HttpMethod;
            if (m != "GET" && m != "HEAD") throw new WebFehler(405, "Nicht erlaubt.");
            switch (pfad)
            {
                case "/":
                case "/index.html":
                    DemoAnmeldung(ctx);
                    Senden(ctx, 200, Eingebettet("Web.index.html"), "text/html; charset=utf-8", false);
                    return;
                case "/app.css":
                    Senden(ctx, 200, Eingebettet("Web.app.css"), "text/css; charset=utf-8", true);
                    return;
                case "/app.js":
                    Senden(ctx, 200, Eingebettet("Web.app.js"), "text/javascript; charset=utf-8", true);
                    return;
                case "/favicon.ico":
                    Senden(ctx, 200, Eingebettet("ITThemensammler.ico"), "image/x-icon", true);
                    return;
                case "/logo/reitzner-hell.png":
                    Senden(ctx, 200, Eingebettet("Logo-hell.png"), "image/png", true);
                    return;
                case "/logo/reitzner-dunkel.png":
                    Senden(ctx, 200, Eingebettet("Logo-dunkel.png"), "image/png", true);
                    return;
                case "/logo/kunde-hell.png":
                case "/logo/kunde-dunkel.png":
                    bool dunkel = pfad.EndsWith("dunkel.png", StringComparison.Ordinal);
                    string datei = Speicher.LogoPfad(dunkel);
                    if (!File.Exists(datei)) datei = Speicher.LogoPfad(!dunkel); // nur ein Logo hochgeladen: für beide Modi
                    if (!File.Exists(datei)) throw new WebFehler(404, "Kein Kundenlogo hinterlegt.");
                    Senden(ctx, 200, File.ReadAllBytes(datei), "image/png", true);
                    return;
            }
            throw new WebFehler(404, "Nicht gefunden.");
        }

        private byte[] Eingebettet(string name)
        {
            lock (_dateien)
            {
                byte[] b;
                if (_dateien.TryGetValue(name, out b)) return b;
                using (var rs = typeof(WebServer).Assembly.GetManifestResourceStream(name))
                {
                    if (rs == null) throw new WebFehler(404, "Nicht gefunden.");
                    using (var ms = new MemoryStream())
                    {
                        rs.CopyTo(ms);
                        b = ms.ToArray();
                    }
                }
                _dateien[name] = b;
                return b;
            }
        }

        // ------------------------------------------------------------------ Schnittstelle

        private static readonly Regex AnfragePfad = new Regex("^/api/anfragen/([0-9a-f]{32})(/erledigt)?$", RegexOptions.CultureInvariant);
        private static readonly Regex BildPfad = new Regex("^/api/anfragen/([0-9a-f]{32})/bild$", RegexOptions.CultureInvariant);

        private void Api(HttpListenerContext ctx, string pfad)
        {
            string m = ctx.Request.HttpMethod;
            bool aendernd = m != "GET" && m != "HEAD";
            if (aendernd && ctx.Request.Headers[SchutzHeader] != "1") throw new WebFehler(403, "Anfrage abgelehnt.");

            if (pfad == "/api/daten" && !aendernd)
            {
                Antworten(ctx, 200, Gesamtstand(ctx, Angemeldet(ctx)));
                return;
            }

            if (pfad == "/api/status" && !aendernd)
            {
                Status(ctx);
                return;
            }

            if (pfad == "/api/anmelden" && m == "POST")
            {
                Anmelden(ctx);
                return;
            }

            if (pfad == "/api/abmelden" && m == "POST")
            {
                Abmelden(ctx);
                Antworten(ctx, 200, Gesamtstand(ctx, null));
                return;
            }

            if (pfad == DomaenePfad && m == "POST")
            {
                DomaeneAnmelden(ctx);
                return;
            }

            // Neues Thema: für alle (Mitarbeiter ohne oder mit Domänen-Anmeldung, je nach Einstellung)
            if (pfad == "/api/anfragen" && m == "POST")
            {
                var ma = Mitarbeiter(ctx);
                if (ma == null && Speicher.EinstellungenLesen().mitarbeiterAnmeldung == "pflicht" && Angemeldet(ctx) == null)
                    throw new WebFehler(403, "Bitte mit dem Windows-Konto anmelden.");
                var b = Body(ctx);
                var a = new Anfrage
                {
                    id = Guid.NewGuid().ToString("N"),
                    standort = Text(b, "standort", 100, true),
                    benutzer = Text(b, "benutzer", 100, true),
                    wunschtermin = Termin(Text(b, "wunschtermin", 10, true)),
                    problem = Text(b, "problem", 4000, true),
                    erfasst = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    erledigtAm = "",
                    erledigtVon = "",
                    notiz = "",
                    status = "",
                    hinweis = "",
                    bild = "",
                    ersteller = ma == null ? "" : ma.Konto
                };
                if (a.wunschtermin == Ticket) throw new WebFehler(400, "Dringende Probleme bitte per E-Mail oder über die Hotline melden.");
                // Besitzer-Schlüssel: nur der Browser des Erstellers erhält ihn, gespeichert wird nur die Prüfsumme
                string schluessel = BitConverter.ToString(Speicher.Zufall(24)).Replace("-", "").ToLowerInvariant();
                a.schluessel = Speicher.Pruefsumme(schluessel);
                Speicher.Aendern(d => { d.anfragen.Add(a); return 0; });
                Speicher.Protokoll("Neues Thema von " + a.benutzer + " (" + a.standort + "), " + IpAdresse(ctx));
                Antworten(ctx, 201, Gesamtstand(ctx, Angemeldet(ctx), new { id = a.id, schluessel = schluessel }));
                return;
            }

            string admin = Angemeldet(ctx);
            var treffer = AnfragePfad.Match(pfad);

            // Bild zu einem Thema: ansehen (wer das Thema sieht), hochladen und entfernen (Admin bzw. Ersteller, solange offen)
            var bildTreffer = BildPfad.Match(pfad);
            if (bildTreffer.Success)
            {
                Bild(ctx, bildTreffer.Groups[1].Value, m, admin);
                return;
            }

            // Mitarbeiter: eigenes, noch offenes Thema bearbeiten oder löschen (Besitzer-Schlüssel im Header)
            if (admin == null && treffer.Success && !treffer.Groups[2].Success && (m == "PUT" || m == "DELETE"))
            {
                EigenesThema(ctx, treffer.Groups[1].Value, m);
                return;
            }

            // ---------- ab hier nur für angemeldete Admins ----------
            if (admin == null) throw new WebFehler(401, "Bitte als Admin anmelden.");

            if (treffer.Success)
            {
                string id = treffer.Groups[1].Value;
                bool erledigen = treffer.Groups[2].Success;
                if (erledigen && m == "POST")
                {
                    var b = Body(ctx);
                    bool erledigt = b.ContainsKey("erledigt") && b["erledigt"] is bool && (bool)b["erledigt"];
                    string notiz = Text(b, "notiz", 2000, false);
                    Speicher.Aendern(d =>
                    {
                        var a = Finden(d, id);
                        a.erledigtAm = erledigt ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
                        a.erledigtVon = erledigt ? admin : "";
                        a.status = ""; // erledigt bzw. wieder offen: Stand zurücksetzen
                        if (b.ContainsKey("notiz")) a.notiz = notiz;
                        return 0;
                    });
                    Speicher.Protokoll(admin + ": Thema " + id + (erledigt ? " erledigt" : " wieder offen"));
                    Antworten(ctx, 200, Gesamtstand(ctx, admin));
                    return;
                }
                if (!erledigen && m == "PUT")
                {
                    var b = Body(ctx);
                    string standort = Text(b, "standort", 100, true);
                    string benutzer = Text(b, "benutzer", 100, true);
                    string termin = Termin(Text(b, "wunschtermin", 10, true));
                    string problem = Text(b, "problem", 4000, true);
                    string notiz = Text(b, "notiz", 2000, false);
                    string status = Text(b, "status", 12, false);
                    string hinweis = Text(b, "hinweis", 1000, false);
                    if (status == "offen") status = "";
                    if (status != "" && status != "arbeit" && status != "rueckfrage" && status != "beantwortet") throw new WebFehler(400, "Der Stand ist ungültig.");
                    if (status == "rueckfrage" && hinweis.Length == 0 && b.ContainsKey("hinweis")) throw new WebFehler(400, "Bitte die Rückfrage als Hinweis eintragen.");
                    Speicher.Aendern(d =>
                    {
                        var a = Finden(d, id);
                        a.standort = standort;
                        a.benutzer = benutzer;
                        a.wunschtermin = termin;
                        a.problem = problem;
                        if (b.ContainsKey("notiz")) a.notiz = notiz;
                        if (b.ContainsKey("status") && a.erledigtAm.Length == 0) a.status = status;
                        if (b.ContainsKey("hinweis")) a.hinweis = hinweis;
                        return 0;
                    });
                    Speicher.Protokoll(admin + ": Thema " + id + " bearbeitet");
                    Antworten(ctx, 200, Gesamtstand(ctx, admin));
                    return;
                }
                if (!erledigen && m == "DELETE")
                {
                    Speicher.Aendern(d =>
                    {
                        Finden(d, id);
                        d.anfragen.RemoveAll(x => x.id == id);
                        return 0;
                    });
                    Speicher.AnhangLoeschen(id);
                    Speicher.Protokoll(admin + ": Thema " + id + " gelöscht");
                    Antworten(ctx, 200, Gesamtstand(ctx, admin));
                    return;
                }
            }

            if (pfad == "/api/stammdaten" && m == "PUT")
            {
                var b = Body(ctx);
                var standorte = Liste(b, "standorte", 100);
                var benutzer = Liste(b, "benutzer", 100);
                var termine = Liste(b, "termine", 10);
                foreach (string t in termine)
                    if (!IstDatum(t)) throw new WebFehler(400, "Termin ungültig: " + t);
                termine.Sort(StringComparer.Ordinal);
                var orte = Zuordnung(b, "orte", standorte);
                var arten = Zuordnung(b, "arten", standorte);
                var ortGruppen = b.ContainsKey("ortGruppen") ? Liste(b, "ortGruppen", 50) : null;
                var artGruppen = b.ContainsKey("artGruppen") ? Liste(b, "artGruppen", 50) : null;
                string sammelOrt = Text(b, "sammelOrt", 50, false);
                string sammelArt = Text(b, "sammelArt", 50, false);
                bool? eigeneReihenfolge = b.ContainsKey("eigeneReihenfolge") && b["eigeneReihenfolge"] is bool ? (bool?)(bool)b["eigeneReihenfolge"] : null;
                Speicher.Aendern(d =>
                {
                    // AD-Benutzer, die ein Admin entfernt, nicht wieder eintragen; von Hand wieder hinzugefügte freigeben
                    var sdAlt = d.stammdaten;
                    foreach (string n in sdAlt.adBenutzer)
                    {
                        bool vorher = sdAlt.benutzer.Contains(n), jetzt = benutzer.Contains(n);
                        if (vorher && !jetzt && !sdAlt.adAusgeschlossen.Contains(n)) sdAlt.adAusgeschlossen.Add(n);
                        if (jetzt) sdAlt.adAusgeschlossen.Remove(n);
                    }
                    d.stammdaten.standorte = standorte;
                    d.stammdaten.benutzer = benutzer;
                    d.stammdaten.termine = termine;
                    if (orte != null) d.stammdaten.orte = orte;
                    if (arten != null) d.stammdaten.arten = arten;
                    if (ortGruppen != null) d.stammdaten.ortGruppen = ortGruppen;
                    if (artGruppen != null) d.stammdaten.artGruppen = artGruppen;
                    if (sammelOrt.Length > 0) d.stammdaten.sammelOrt = sammelOrt;
                    if (sammelArt.Length > 0) d.stammdaten.sammelArt = sammelArt;
                    if (eigeneReihenfolge.HasValue) d.stammdaten.eigeneReihenfolge = eigeneReihenfolge;
                    return 0;
                });
                Speicher.Protokoll(admin + ": Stammdaten geändert");
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            // ---------- Active Directory: Benutzer abgleichen ----------
            if (pfad == "/api/ad/ous" && m == "GET")
            {
                List<object> ous;
                try
                {
                    ous = AdAbgleich.OuListe();
                }
                catch (Exception ex)
                {
                    throw new WebFehler(502, "Das Active Directory ist nicht erreichbar: " + ex.Message);
                }
                Antworten(ctx, 200, new { ous = ous });
                return;
            }

            if (pfad == "/api/ad" && m == "PUT")
            {
                var b = Body(ctx);
                var ous = Liste(b, "ous", 500);
                if (ous.Count > 30) throw new WebFehler(400, "Bitte höchstens 30 OUs auswählen.");
                foreach (string dn in ous)
                    if (!AdAbgleich.IstDn(dn)) throw new WebFehler(400, "Ungültige OU: " + dn);
                bool abgleich = b.ContainsKey("abgleich") && b["abgleich"] is bool && (bool)b["abgleich"];
                var ausschluss = b.ContainsKey("ausschluss") ? Liste(b, "ausschluss", 50) : null;
                if (ausschluss != null && ausschluss.Count > 50) throw new WebFehler(400, "Bitte höchstens 50 Ausschlüsse eintragen.");
                Speicher.EinstellungenAendern(e =>
                {
                    e.adOus = ous;
                    e.adAbgleich = abgleich;
                    if (ausschluss != null) e.adAusschluss = ausschluss;
                    if (b.ContainsKey("ohneNamen") && b["ohneNamen"] is bool) e.adOhneNamen = (bool)b["ohneNamen"];
                });
                Speicher.Protokoll(admin + ": AD-Abgleich " + (abgleich ? "an" : "aus") + ", OUs: " + string.Join("; ", ous.ToArray()));
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            if (pfad == "/api/ad/abgleichen" && m == "POST")
            {
                string ergebnis;
                try
                {
                    ergebnis = AdAbgleich.Abgleichen("von " + admin);
                }
                catch (Exception ex)
                {
                    throw new WebFehler(502, ex.Message);
                }
                Antworten(ctx, 200, Gesamtstand(ctx, admin, new { ad = ergebnis }));
                return;
            }

            // Einen Benutzer umbenennen (auch in allen Themen); auf Wunsch mit einem vorhandenen zusammenführen
            if (pfad == "/api/benutzer" && m == "POST")
            {
                var b = Body(ctx);
                string alt = Text(b, "alt", 100, true);
                string neu = Text(b, "neu", 100, true);
                bool zusammen = b.ContainsKey("zusammenfuehren") && b["zusammenfuehren"] is bool && (bool)b["zusammenfuehren"];
                int themen = Speicher.Aendern(d =>
                {
                    var sd = d.stammdaten;
                    int pos = sd.benutzer.FindIndex(x => x == alt);
                    if (pos < 0) throw new WebFehler(404, "Diesen Benutzer gibt es nicht mehr. Bitte die Seite neu laden.");
                    int vorhanden = sd.benutzer.FindIndex(x => x != alt && string.Equals(x, neu, StringComparison.OrdinalIgnoreCase));
                    if (vorhanden >= 0 && !zusammen)
                        throw new WebFehler(409, "Den Benutzer „" + sd.benutzer[vorhanden] + "“ gibt es schon. Nochmal speichern führt beide zusammen.");
                    if (vorhanden >= 0)
                    {
                        neu = sd.benutzer[vorhanden]; // Schreibweise des vorhandenen Benutzers
                        sd.benutzer.RemoveAt(pos);
                    }
                    else
                    {
                        sd.benutzer[pos] = neu;
                    }
                    // Name aus dem AD: der Abgleich soll den alten Namen nicht wieder eintragen
                    if (neu != alt && sd.adBenutzer.Contains(alt) && !sd.adAusgeschlossen.Contains(alt)) sd.adAusgeschlossen.Add(alt);
                    int n = 0;
                    if (neu != alt)
                        foreach (var a in d.anfragen.Where(x => x.benutzer == alt))
                        {
                            a.benutzer = neu;
                            n++;
                        }
                    return n;
                });
                Speicher.Protokoll(admin + ": Benutzer " + alt + " → " + neu + " (" + themen + " Themen)");
                Antworten(ctx, 200, Gesamtstand(ctx, admin, new { benutzer = neu, themen = themen }));
                return;
            }

            // Einen Termin verschieben: Datum in den Stammdaten und bei allen offenen Themen mit diesem Wunschtermin
            if (pfad == "/api/termin" && m == "POST")
            {
                var b = Body(ctx);
                string alt = Text(b, "alt", 10, true);
                string neu = Text(b, "neu", 10, true);
                if (!IstDatum(alt) || !IstDatum(neu)) throw new WebFehler(400, "Das Datum ist ungültig.");
                int themen = Speicher.Aendern(d =>
                {
                    var t = d.stammdaten.termine;
                    if (!t.Contains(alt)) throw new WebFehler(404, "Diesen Termin gibt es nicht mehr. Bitte die Seite neu laden.");
                    t.Remove(alt);
                    if (!t.Contains(neu)) t.Add(neu); // gibt es den neuen Termin schon, werden beide zusammengelegt
                    t.Sort(StringComparer.Ordinal);
                    int n = 0;
                    if (neu != alt)
                        foreach (var a in d.anfragen.Where(x => x.wunschtermin == alt && string.IsNullOrEmpty(x.erledigtAm)))
                        {
                            a.wunschtermin = neu;
                            n++;
                        }
                    return n;
                });
                Speicher.Protokoll(admin + ": Termin " + alt + " → " + neu + " (" + themen + " offene Themen)");
                Antworten(ctx, 200, Gesamtstand(ctx, admin, new { termin = neu, themen = themen }));
                return;
            }

            // Einen Standort bearbeiten: Name (auch in allen Themen), Ort und Art; auf Wunsch mit einem vorhandenen zusammenführen
            if (pfad == "/api/standort" && m == "POST")
            {
                var b = Body(ctx);
                string alt = Text(b, "alt", 100, true);
                string neu = Text(b, "neu", 100, true);
                string ort = Text(b, "ort", 50, false);
                string art = Text(b, "art", 50, false);
                bool zusammen = b.ContainsKey("zusammenfuehren") && b["zusammenfuehren"] is bool && (bool)b["zusammenfuehren"];
                int themen = Speicher.Aendern(d =>
                {
                    var sd = d.stammdaten;
                    int pos = sd.standorte.FindIndex(x => x == alt);
                    if (pos < 0) throw new WebFehler(404, "Diesen Standort gibt es nicht mehr. Bitte die Seite neu laden.");
                    int vorhanden = sd.standorte.FindIndex(x => x != alt && string.Equals(x, neu, StringComparison.OrdinalIgnoreCase));
                    if (vorhanden >= 0 && !zusammen)
                        throw new WebFehler(409, "Den Standort „" + sd.standorte[vorhanden] + "“ gibt es schon. Nochmal speichern führt beide zusammen.");
                    if (vorhanden >= 0)
                    {
                        neu = sd.standorte[vorhanden]; // Schreibweise des vorhandenen Standorts
                        sd.standorte.RemoveAt(pos);
                    }
                    else
                    {
                        sd.standorte[pos] = neu;
                    }
                    sd.orte.Remove(alt);
                    sd.arten.Remove(alt);
                    if (ort.Length > 0) sd.orte[neu] = ort; else sd.orte.Remove(neu);
                    if (art.Length > 0) sd.arten[neu] = art; else sd.arten.Remove(neu);
                    int n = 0;
                    if (neu != alt)
                        foreach (var a in d.anfragen.Where(x => x.standort == alt))
                        {
                            a.standort = neu;
                            n++;
                        }
                    return n;
                });
                Speicher.Protokoll(admin + ": Standort " + alt + (neu != alt ? " → " + neu + " (" + themen + " Themen)" : "") + " bearbeitet");
                Antworten(ctx, 200, Gesamtstand(ctx, admin, new { standort = neu, themen = themen }));
                return;
            }

            // Admins aus einer AD-Gruppe (leer = aus); die Gruppe muss es im AD geben
            if (pfad == "/api/admingruppe" && m == "PUT")
            {
                var b = Body(ctx);
                string gruppe = Text(b, "gruppe", 100, false);
                if (gruppe.Length > 0 && AdAbgleich.GruppeSid(gruppe) == null)
                    throw new WebFehler(400, "Die Gruppe „" + gruppe + "“ wurde im Active Directory nicht gefunden.");
                Speicher.EinstellungenAendern(e => e.adAdminGruppe = gruppe);
                Speicher.Protokoll(admin + ": Admins aus dem AD " + (gruppe.Length > 0 ? "über die Gruppe " + gruppe : "ausgeschaltet"));
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            if (pfad == "/api/aussehen" && m == "PUT")
            {
                var b = Body(ctx);
                string titel = Text(b, "titel", 60, true);
                string untertitel = Text(b, "untertitel", 120, false);
                string kunde = Text(b, "kunde", 100, false);
                string mail = Text(b, "ticketMail", 100, false);
                string hotline = Text(b, "hotline", 40, false);
                string gruppierung = Text(b, "gruppierung", 10, false);
                if (gruppierung.Length > 0 && gruppierung != "ort" && gruppierung != "art" && gruppierung != "keine")
                    throw new WebFehler(400, "Gruppierung ungültig.");
                if (mail.Length > 0 && !Regex.IsMatch(mail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new WebFehler(400, "Die E-Mail-Adresse ist ungültig.");
                string anmeldung = Text(b, "mitarbeiterAnmeldung", 10, false);
                int loeschMonate = -1;
                if (b.ContainsKey("loeschMonate") && (!int.TryParse(Text(b, "loeschMonate", 4, false), NumberStyles.None, CultureInfo.InvariantCulture, out loeschMonate) || loeschMonate > 120))
                    throw new WebFehler(400, "Die Löschfrist ist ungültig.");
                string standortFeld = b.ContainsKey("adStandortFeld") ? Text(b, "adStandortFeld", 10, false) : null;
                if (standortFeld != null && standortFeld != "" && standortFeld != "buero" && standortFeld != "abteilung") throw new WebFehler(400, "Das AD-Feld ist ungültig.");
                Speicher.EinstellungenAendern(e =>
                {
                    e.titel = titel;
                    e.untertitel = untertitel;
                    e.kunde = kunde;
                    e.ticketMail = mail;
                    e.hotline = hotline;
                    if (gruppierung.Length > 0) e.gruppierung = gruppierung;
                    if (b.ContainsKey("erledigteSichtbar") && b["erledigteSichtbar"] is bool) e.erledigteSichtbar = (bool)b["erledigteSichtbar"];
                    if (anmeldung == "aus" || anmeldung == "domaene" || anmeldung == "pflicht") e.mitarbeiterAnmeldung = anmeldung;
                    if (loeschMonate >= 0) e.loeschMonate = loeschMonate;
                    if (b.ContainsKey("loeschArchiv") && b["loeschArchiv"] is bool) e.loeschArchiv = (bool)b["loeschArchiv"];
                    if (standortFeld != null) e.adStandortFeld = standortFeld;
                });
                Speicher.Protokoll(admin + ": Aussehen geändert");
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            if ((pfad == "/api/logo/hell" || pfad == "/api/logo/dunkel") && (m == "PUT" || m == "DELETE"))
            {
                bool dunkel = pfad.EndsWith("dunkel", StringComparison.Ordinal);
                if (m == "PUT") LogoSpeichern(ctx, dunkel);
                else if (File.Exists(Speicher.LogoPfad(dunkel))) File.Delete(Speicher.LogoPfad(dunkel));
                Speicher.EinstellungenAendern(e => e.logoStand = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                Speicher.Protokoll(admin + ": Kundenlogo (" + (dunkel ? "dunkel" : "hell") + ") " + (m == "PUT" ? "hochgeladen" : "entfernt"));
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            if (pfad == "/api/kennwort" && m == "POST")
            {
                if (AdAdmin(ctx)) throw new WebFehler(400, "Angemeldet mit dem Windows-Konto – das Kennwort bitte in Windows ändern.");
                var b = Body(ctx);
                string alt = b.ContainsKey("alt") ? b["alt"] as string : null;
                string neu = b.ContainsKey("neu") ? b["neu"] as string : null;
                if (Speicher.Pruefen(admin, alt) == null) throw new WebFehler(400, "Das bisherige Kennwort stimmt nicht.");
                string fehler = Speicher.KontoFehler(admin, neu);
                if (fehler != null) throw new WebFehler(400, fehler);
                Speicher.KennwortSetzen(admin, neu);
                // die eigene Sitzung bleibt gültig, alle anderen Sitzungen dieses Kontos enden
                var konto = Speicher.EinstellungenLesen().konten.First(k => string.Equals(k.name, admin, StringComparison.OrdinalIgnoreCase));
                lock (_sitzungen)
                {
                    Sitzung s;
                    string token = Token(ctx);
                    if (token != null && _sitzungen.TryGetValue(token, out s)) s.Hash = konto.hash;
                }
                Speicher.Protokoll(admin + ": Kennwort geändert");
                Antworten(ctx, 200, Gesamtstand(ctx, admin));
                return;
            }

            throw new WebFehler(404, "Nicht gefunden.");
        }

        /// <summary>Alles, was die Weboberfläche zum Anzeigen braucht.</summary>
        /// <param name="neueSitzung">nur direkt nach der Domänen-Anmeldung: die eben angelegte Mitarbeiter-Sitzung (der Browser schickt das Cookie erst ab der nächsten Anfrage)</param>
        /// <param name="adAdmin">nur direkt nach der Domänen-Anmeldung: Admin über die AD-Gruppe</param>
        private object Gesamtstand(HttpListenerContext ctx, string admin, object neu = null, MitarbeiterSitzung neueSitzung = null, bool adAdmin = false)
        {
            var e = Speicher.EinstellungenLesen();
            var ma = neueSitzung ?? Mitarbeiter(ctx);
            adAdmin = admin != null && (adAdmin || AdAdmin(ctx));
            string ich = ma == null ? "" : ma.Konto;
            // "Nur mit Domänenkonto": ohne Anmeldung keine Themen
            bool gesperrt = admin == null && ma == null && e.mitarbeiterAnmeldung == "pflicht";
            // Erledigte Themen nur an Admins, wenn sie für Mitarbeiter ausgeblendet sind (Verwaltung › Aussehen)
            bool alle = admin != null || e.erledigteSichtbar != false;
            object daten = Speicher.Lesen(d => new
            {
                stammdaten = d.stammdaten,
                // ohne Besitzer-Schlüssel; den Ersteller sehen nur Admins
                anfragen = d.anfragen.Where(a => !gesperrt && (alle || string.IsNullOrEmpty(a.erledigtAm))).Select(a => new
                {
                    id = a.id,
                    standort = a.standort,
                    benutzer = a.benutzer,
                    wunschtermin = a.wunschtermin,
                    problem = a.problem,
                    erfasst = a.erfasst,
                    erledigtAm = a.erledigtAm,
                    erledigtVon = a.erledigtVon,
                    notiz = a.notiz,
                    status = a.erledigtAm.Length == 0 ? a.status : "",
                    hinweis = a.erledigtAm.Length == 0 ? a.hinweis : "",
                    bild = a.bild,
                    ersteller = admin != null ? a.ersteller : "",
                    eigen = ma != null && ma.Besitzt(a.ersteller)
                }).ToList()
            });
            return new
            {
                daten = daten,
                aussehen = new
                {
                    titel = e.titel,
                    untertitel = e.untertitel,
                    kunde = e.kunde,
                    ticketMail = e.ticketMail,
                    hotline = e.hotline,
                    gruppierung = e.gruppierung,
                    erledigteSichtbar = e.erledigteSichtbar != false,
                    mitarbeiterAnmeldung = e.mitarbeiterAnmeldung,
                    adAdmins = e.adAdminGruppe.Length > 0, // Anmeldedialog: "Mit Windows-Konto anmelden"
                    adAdminGruppe = admin != null ? e.adAdminGruppe : "",
                    loeschMonate = admin != null ? e.loeschMonate : 0,
                    loeschArchiv = admin != null && e.loeschArchiv != false,
                    adStandortFeld = admin != null ? e.adStandortFeld : "",
                    logoHell = File.Exists(Speicher.LogoPfad(false)),
                    logoDunkel = File.Exists(Speicher.LogoPfad(true)),
                    logoStand = e.logoStand
                },
                admin = admin,
                adminAd = adAdmin,
                mitarbeiter = ma == null ? null : new { name = ma.Name, konto = ma.Konto.Substring(ma.Konto.IndexOf(':') + 1), quelle = ma.Quelle, standort = AdStandort(ma, e) },
                adminAutomatisch = admin != null && AdminAutomatisch(ctx),
                anmeldungNoetig = gesperrt,
                // neue Version auf GitHub (prüft der Dienst), nur für Admins
                update = admin != null && !Demo.Active && UpdateCheck.IsNewer(e.updateVersion) ? new { version = e.updateVersion, seite = e.updateSeite } : null,
                ad = admin == null ? null : new
                {
                    ous = e.adOus.Select(dn => new { dn = dn, pfad = AdAbgleich.Pfad(dn) }).ToList(),
                    abgleich = e.adAbgleich == true,
                    letzter = e.adLetzter,
                    ergebnis = e.adErgebnis,
                    ausschluss = e.adAusschluss,
                    ohneNamen = e.adOhneNamen == true
                },
                neu = neu,
                programm = new { name = AppInfo.Name, version = AppInfo.Version, firma = AppInfo.Company }
            };
        }

        /// <summary>
        /// Für Überwachungsprogramme (PRTG, Checkmk, Uptime Kuma, RMM …): HTTP 200 und "status":"ok", wenn der Webserver läuft
        /// und daten.json lesbar ist, sonst HTTP 503 und "status":"fehler". Ohne Anmeldung, ohne Inhalte der Themen.
        /// </summary>
        private void Status(HttpListenerContext ctx)
        {
            string fehler = "";
            int offen = 0;
            try
            {
                offen = Speicher.Lesen(d => d.anfragen.Count(a => string.IsNullOrEmpty(a.erledigtAm)));
                Speicher.EinstellungenLesen();
            }
            catch (Exception ex)
            {
                fehler = "Daten nicht lesbar: " + ex.Message;
                Speicher.Fehler("Status: Daten nicht lesbar", ex);
                Ereignis.Schreiben(Ereignis.Datenfehler, System.Diagnostics.EventLogEntryType.Error, "daten.json bzw. einstellungen.json ist nicht lesbar: " + ex.Message);
            }
            bool ok = fehler.Length == 0;
            Antworten(ctx, ok ? 200 : 503, new
            {
                status = ok ? "ok" : "fehler",
                fehler = fehler,
                programm = AppInfo.Name,
                version = AppInfo.Version,
                rechner = Environment.MachineName,
                port = Port,
                gestartet = _gestartet.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                laufzeitMinuten = (int)(DateTime.Now - _gestartet).TotalMinutes,
                offeneThemen = offen
            });
        }

        /// <summary>Header mit dem Besitzer-Schlüssel eines Themas (nur der Browser des Erstellers kennt ihn).</summary>
        private const string SchluesselHeader = "X-Themen-Schluessel";

        /// <summary>
        /// Mitarbeiter ohne Admin-Anmeldung: das eigene Thema bearbeiten (PUT) oder löschen (DELETE),
        /// nur mit passendem Besitzer-Schlüssel und nur, solange es offen ist. Die Notiz bleibt den Admins vorbehalten.
        /// </summary>
        private void EigenesThema(HttpListenerContext ctx, string id, string methode)
        {
            // Besitz: über das angemeldete Domänenkonto oder über den Besitzer-Schlüssel dieses Browsers
            string schluessel = ctx.Request.Headers[SchluesselHeader] ?? "";
            string pruef = Regex.IsMatch(schluessel, "^[0-9a-f]{48}$") ? Speicher.Pruefsumme(schluessel) : "";
            var ma = Mitarbeiter(ctx);
            string ich = ma == null ? "" : ma.Konto;
            if (pruef.Length == 0 && ich.Length == 0) throw new WebFehler(403, "Dieses Thema kann nur der Ersteller oder ein Admin ändern.");

            string standort = "", benutzer = "", termin = "", problem = "";
            if (methode == "PUT")
            {
                var b = Body(ctx);
                standort = Text(b, "standort", 100, true);
                benutzer = Text(b, "benutzer", 100, true);
                termin = Termin(Text(b, "wunschtermin", 10, true));
                problem = Text(b, "problem", 4000, true);
                if (termin == Ticket) throw new WebFehler(400, "Dringende Probleme bitte per E-Mail oder über die Hotline melden.");
            }

            string wer = Speicher.Aendern(d =>
            {
                var a = Finden(d, id);
                bool perKonto = ma != null && ma.Besitzt(a.ersteller);
                bool perSchluessel = pruef.Length > 0 && a.schluessel == pruef;
                if (!perKonto && !perSchluessel) throw new WebFehler(403, "Dieses Thema kann nur der Ersteller oder ein Admin ändern.");
                if (a.erledigtAm.Length > 0) throw new WebFehler(403, "Erledigte Themen kann nur ein Admin ändern.");
                if (methode == "DELETE")
                {
                    d.anfragen.RemoveAll(x => x.id == id);
                }
                else
                {
                    a.standort = standort;
                    a.benutzer = benutzer;
                    a.wunschtermin = termin;
                    a.problem = problem;
                    if (a.status == "rueckfrage") a.status = "beantwortet"; // der Techniker sieht, dass geantwortet wurde
                }
                return a.benutzer;
            });
            if (methode == "DELETE") Speicher.AnhangLoeschen(id);
            Speicher.Protokoll(wer + ": eigenes Thema " + id + (methode == "DELETE" ? " gelöscht" : " bearbeitet") + ", " + IpAdresse(ctx));
            Antworten(ctx, 200, Gesamtstand(ctx, null));
        }

        /// <summary>Standort aus dem AD-Feld des angemeldeten Mitarbeiters (Büro bzw. Abteilung), wenn es einen Standort dieses Namens gibt; sonst "".</summary>
        private static string AdStandort(MitarbeiterSitzung ma, Einstellungen e)
        {
            if (ma == null || ma.Ad == null || e.adStandortFeld.Length == 0) return "";
            string wert = e.adStandortFeld == "abteilung" ? ma.Ad.Abteilung : ma.Ad.Buero;
            if (string.IsNullOrEmpty(wert)) return "";
            return Speicher.Lesen(d => d.stammdaten.standorte.FirstOrDefault(x => string.Equals(x, wert, StringComparison.OrdinalIgnoreCase))) ?? "";
        }

        // ------------------------------------------------------------------ Bild zu einem Thema

        /// <summary>
        /// GET: Bild ansehen – wer das Thema sehen darf. PUT/DELETE: Admin immer; der Ersteller (Domänen- bzw. Microsoft-365-Konto
        /// oder Besitzer-Schlüssel dieses Browsers), solange das Thema offen ist.
        /// </summary>
        private void Bild(HttpListenerContext ctx, string id, string m, string admin)
        {
            var e = Speicher.EinstellungenLesen();
            var ma = Mitarbeiter(ctx);
            if (m == "GET" || m == "HEAD")
            {
                bool gesperrt = admin == null && ma == null && e.mitarbeiterAnmeldung == "pflicht";
                bool alle = admin != null || e.erledigteSichtbar != false;
                bool sichtbar = Speicher.Lesen(d =>
                {
                    var a = d.anfragen.FirstOrDefault(x => x.id == id);
                    return a != null && a.bild.Length > 0 && !gesperrt && (alle || a.erledigtAm.Length == 0);
                });
                string datei = Speicher.AnhangPfad(id);
                if (!sichtbar || !File.Exists(datei)) throw new WebFehler(404, "Zu diesem Thema gibt es kein Bild.");
                Senden(ctx, 200, File.ReadAllBytes(datei), "image/jpeg", true);
                return;
            }
            if (m != "PUT" && m != "DELETE") throw new WebFehler(405, "Nicht erlaubt.");

            string schluessel = ctx.Request.Headers[SchluesselHeader] ?? "";
            string pruef = Regex.IsMatch(schluessel, "^[0-9a-f]{48}$") ? Speicher.Pruefsumme(schluessel) : "";
            Func<Anfrage, bool> darf = a => admin != null ||
                (a.erledigtAm.Length == 0 && ((ma != null && ma.Besitzt(a.ersteller)) || (pruef.Length > 0 && a.schluessel == pruef)));
            // erst die Berechtigung prüfen, dann das Bild annehmen
            Speicher.Lesen(d =>
            {
                if (!darf(Finden(d, id))) throw new WebFehler(403, "Das Bild kann nur der Ersteller (solange das Thema offen ist) oder ein Admin ändern.");
                return 0;
            });

            if (m == "PUT") BildSpeichern(ctx, id);
            string stempel = m == "PUT" ? DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "";
            try
            {
                Speicher.Aendern(d =>
                {
                    var a = Finden(d, id);
                    if (!darf(a)) throw new WebFehler(403, "Das Bild kann nur der Ersteller (solange das Thema offen ist) oder ein Admin ändern.");
                    a.bild = stempel;
                    return 0;
                });
            }
            catch
            {
                if (m == "PUT") Speicher.AnhangLoeschen(id); // Thema inzwischen gelöscht bzw. erledigt
                throw;
            }
            if (m == "DELETE") Speicher.AnhangLoeschen(id);
            Speicher.Protokoll((admin ?? (ma != null ? ma.Name : "Ersteller")) + ": Bild zu Thema " + id + (m == "PUT" ? " angehängt" : " entfernt") + ", " + IpAdresse(ctx));
            Antworten(ctx, 200, Gesamtstand(ctx, admin));
        }

        /// <summary>
        /// Hochgeladenes Bild prüfen und neu als JPEG speichern (längste Kante höchstens 2000 Pixel, nach EXIF gedreht).
        /// Was nicht als Bild lesbar ist, wird abgewiesen; durch das Neuspeichern bleiben keine Metadaten (z. B. Standort) erhalten.
        /// </summary>
        private static void BildSpeichern(HttpListenerContext ctx, string id)
        {
            byte[] daten = Lesen(ctx, MaxAnhang, "Das Bild ist zu groß (höchstens 10 MB).");
            if (daten.Length == 0) throw new WebFehler(400, "Kein Bild empfangen.");
            Image bild;
            try
            {
                bild = Image.FromStream(new MemoryStream(daten), false, true);
            }
            catch
            {
                throw new WebFehler(400, "Die Datei ist kein lesbares Bild. Bitte PNG oder JPG verwenden.");
            }
            using (bild)
            {
                if (bild.Width < 8 || bild.Height < 8) throw new WebFehler(400, "Das Bild ist zu klein.");
                try
                {
                    // Handyfotos: Drehung aus den EXIF-Daten übernehmen
                    if (Array.IndexOf(bild.PropertyIdList, 0x0112) >= 0)
                    {
                        int lage = bild.GetPropertyItem(0x0112).Value[0];
                        if (lage == 3) bild.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        else if (lage == 6) bild.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        else if (lage == 8) bild.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    }
                }
                catch { /* ohne Drehung */ }
                double f = Math.Min(1.0, (double)AnhangKante / Math.Max(bild.Width, bild.Height));
                int w = Math.Max(1, (int)Math.Round(bild.Width * f)), h = Math.Max(1, (int)Math.Round(bild.Height * f));
                using (var neu = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(neu))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(bild, 0, 0, w, h);
                    }
                    Speicher.OrdnerAnlegen();
                    Directory.CreateDirectory(Speicher.AnhangOrdner);
                    string ziel = Speicher.AnhangPfad(id);
                    string tmp = ziel + ".tmp";
                    var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                        neu.Save(tmp, jpeg, p);
                    }
                    if (File.Exists(ziel)) File.Delete(ziel);
                    File.Move(tmp, ziel);
                }
            }
        }

        private static Anfrage Finden(DatenDatei d, string id)
        {
            var a = d.anfragen.FirstOrDefault(x => x.id == id);
            if (a == null) throw new WebFehler(404, "Dieses Thema wurde inzwischen gelöscht.");
            return a;
        }

        // ------------------------------------------------------------------ Anmeldung

        private void Anmelden(HttpListenerContext ctx)
        {
            string ip = IpAdresse(ctx);
            lock (_fehlversuche)
            {
                Sperrzeit sp;
                if (_fehlversuche.TryGetValue(ip, out sp) && sp.Bis > DateTime.UtcNow)
                    throw new WebFehler(429, "Zu viele Fehlversuche. Bitte in " + Math.Max(1, (int)Math.Ceiling((sp.Bis - DateTime.UtcNow).TotalMinutes)) + " Minuten erneut versuchen.");
            }
            var b = Body(ctx);
            string name = b.ContainsKey("name") ? b["name"] as string : null;
            string kennwort = b.ContainsKey("kennwort") ? b["kennwort"] as string : null;
            var konto = Speicher.Pruefen(name, kennwort);
            if (konto == null)
            {
                lock (_fehlversuche)
                {
                    Sperrzeit sp;
                    if (!_fehlversuche.TryGetValue(ip, out sp)) _fehlversuche[ip] = sp = new Sperrzeit();
                    sp.Anzahl++;
                    if (sp.Anzahl >= MaxFehlversuche)
                    {
                        sp.Bis = DateTime.UtcNow.AddMinutes(2);
                        sp.Anzahl = 0;
                    }
                }
                Speicher.Protokoll("Anmeldung fehlgeschlagen (" + (name ?? "") + "), " + ip);
                Thread.Sleep(600); // bremst das Durchprobieren
                throw new WebFehler(403, "Name oder Kennwort falsch.");
            }
            lock (_fehlversuche) _fehlversuche.Remove(ip);

            string token = BitConverter.ToString(Speicher.Zufall(32)).Replace("-", "").ToLowerInvariant();
            lock (_sitzungen)
            {
                foreach (var alt in _sitzungen.Where(p => p.Value.Ablauf < DateTime.UtcNow).Select(p => p.Key).ToList()) _sitzungen.Remove(alt);
                _sitzungen[token] = new Sitzung { Name = konto.name, Hash = konto.hash, Ablauf = DateTime.UtcNow.AddMinutes(SitzungMinuten) };
            }
            ctx.Response.AppendHeader("Set-Cookie", CookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Strict" + (ctx.Request.IsSecureConnection ? "; Secure" : ""));
            Speicher.Protokoll("Anmeldung " + konto.name + ", " + ip);
            Antworten(ctx, 200, Gesamtstand(ctx, konto.name));
        }

        /// <summary>
        /// Nur bei den Screenshots (Demo.Active, Webserver mit Beispieldaten): "/?demo=&lt;Schlüssel&gt;" meldet den Browser
        /// als Admin an, damit auch die Verwaltung aufgenommen werden kann. Im normalen Betrieb ohne Wirkung.
        /// </summary>
        private void DemoAnmeldung(HttpListenerContext ctx)
        {
            if (!Demo.Active || string.IsNullOrEmpty(Demo.WebSchluessel)) return;
            if (ctx.Request.QueryString["demo"] != Demo.WebSchluessel) return;
            var konto = Speicher.EinstellungenLesen().konten.FirstOrDefault();
            if (konto == null) return;
            string token = BitConverter.ToString(Speicher.Zufall(32)).Replace("-", "").ToLowerInvariant();
            lock (_sitzungen) _sitzungen[token] = new Sitzung { Name = konto.name, Hash = konto.hash, Ablauf = DateTime.UtcNow.AddMinutes(SitzungMinuten) };
            ctx.Response.AppendHeader("Set-Cookie", CookieName + "=" + token + "; Path=/; HttpOnly; SameSite=Strict");
        }

        private void Abmelden(HttpListenerContext ctx)
        {
            string token = Token(ctx);
            if (token != null) lock (_sitzungen) _sitzungen.Remove(token);
            ctx.Response.AppendHeader("Set-Cookie", CookieName + "=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");
        }

        private static string Token(HttpListenerContext ctx)
        {
            var c = ctx.Request.Cookies[CookieName];
            return c == null || string.IsNullOrEmpty(c.Value) ? null : c.Value;
        }

        /// <summary>Name des angemeldeten Admins oder null. Die Sitzung verlängert sich bei jeder Anfrage.</summary>
        /// <summary>Ist die Admin-Sitzung dieser Anfrage eine Anmeldung über die AD-Gruppe (kein lokales Konto)?</summary>
        private bool AdAdmin(HttpListenerContext ctx)
        {
            string token = Token(ctx);
            if (token != null)
                lock (_sitzungen)
                {
                    Sitzung s;
                    if (_sitzungen.TryGetValue(token, out s)) return s.AdGruppe != null;
                }
            return ProxyAdmin(ctx) != null;
        }

        /// <summary>Admin ohne eigene Sitzung (über den Anwendungsproxy und die AD-Gruppe): "Abmelden" ergibt dort keinen Sinn.</summary>
        private bool AdminAutomatisch(HttpListenerContext ctx)
        {
            return AngemeldetCookie(ctx) == null && ProxyAdmin(ctx) != null;
        }

        /// <summary>Angemeldeter Admin: lokales Konto bzw. AD-Gruppe (Sitzung per Cookie) oder – über den Anwendungsproxy – AD-Gruppe.</summary>
        private string Angemeldet(HttpListenerContext ctx)
        {
            return AngemeldetCookie(ctx) ?? ProxyAdmin(ctx);
        }

        private string AngemeldetCookie(HttpListenerContext ctx)
        {
            string token = Token(ctx);
            if (token == null) return null;
            Sitzung s;
            lock (_sitzungen)
            {
                if (!_sitzungen.TryGetValue(token, out s)) return null;
                if (s.Ablauf < DateTime.UtcNow)
                {
                    _sitzungen.Remove(token);
                    return null;
                }
            }
            // Admin über die AD-Gruppe: gültig, solange dieselbe Gruppe eingestellt ist
            if (s.AdGruppe != null)
            {
                bool gilt = string.Equals(Speicher.EinstellungenLesen().adAdminGruppe, s.AdGruppe, StringComparison.OrdinalIgnoreCase);
                lock (_sitzungen)
                {
                    if (!gilt)
                    {
                        _sitzungen.Remove(token);
                        return null;
                    }
                    s.Ablauf = DateTime.UtcNow.AddMinutes(SitzungMinuten);
                }
                return s.Name;
            }
            // Konto gelöscht oder Kennwort zurückgesetzt (z. B. im Verwaltungsfenster): Sitzung beenden
            var konto = Speicher.EinstellungenLesen().konten.FirstOrDefault(k => string.Equals(k.name, s.Name, StringComparison.OrdinalIgnoreCase));
            lock (_sitzungen)
            {
                if (konto == null || konto.hash != s.Hash)
                {
                    _sitzungen.Remove(token);
                    return null;
                }
                s.Ablauf = DateTime.UtcNow.AddMinutes(SitzungMinuten);
            }
            return s.Name;
        }

        /// <summary>
        /// Stille Anmeldung mit dem Domänenkonto: Der Browser meldet sich unter /api/domaene per Kerberos (bzw. NTLM) an,
        /// danach gilt eine eigene Mitarbeiter-Sitzung per Cookie. Auf Rechnern ohne Domäne scheitert das – dann bleibt
        /// die Seite je nach Einstellung ohne Anmeldung nutzbar.
        /// </summary>
        // ------------------------------------------------------------------ Anwendungsproxy (Microsoft 365)

        /// <summary>Kam die Anfrage auf dem Port für den Anwendungsproxy an?</summary>
        private bool ViaProxy(HttpListenerContext ctx)
        {
            return ProxyPort > 0 && ctx.Request.LocalEndPoint != null && ctx.Request.LocalEndPoint.Port == ProxyPort;
        }

        /// <summary>Stammt die Anfrage von einem eingetragenen Connector-Server?</summary>
        private static bool VomConnector(HttpListenerContext ctx)
        {
            var ep = ctx.Request.RemoteEndPoint;
            if (ep == null) return false;
            var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            foreach (string s in Speicher.EinstellungenLesen().proxyIps)
            {
                IPAddress erlaubt;
                if (IPAddress.TryParse(s, out erlaubt) && erlaubt.Equals(ip)) return true;
            }
            return false;
        }

        private readonly Dictionary<string, DateTime> _abgewiesen = new Dictionary<string, DateTime>();

        private void ProxyAbgewiesen(HttpListenerContext ctx)
        {
            string ip = IpAdresse(ctx);
            lock (_abgewiesen)
            {
                DateTime t;
                if (_abgewiesen.TryGetValue(ip, out t) && t > DateTime.UtcNow) return; // höchstens einmal je Stunde protokollieren
                _abgewiesen[ip] = DateTime.UtcNow.AddHours(1);
            }
            Speicher.Protokoll("Anfrage auf dem Proxy-Port von " + ip + " abgewiesen (kein eingetragener Connector)");
        }

        private readonly Dictionary<string, MitarbeiterSitzung> _proxyKonten = new Dictionary<string, MitarbeiterSitzung>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mitarbeiter über den Anwendungsproxy: Der Proxy hat ihn mit Microsoft 365 angemeldet und schickt UPN und Namen
        /// in Kopfzeilen (headerbasiertes einmaliges Anmelden). Ohne UPN-Kopfzeile: null.
        /// </summary>
        private MitarbeiterSitzung ProxyMitarbeiter(HttpListenerContext ctx)
        {
            if (!ViaProxy(ctx) || !VomConnector(ctx)) return null;
            var e = Speicher.EinstellungenLesen();
            string upn = Konten.Kopf(ctx.Request, e.proxyKopfUpn).ToLowerInvariant();
            if (upn.Length == 0 || upn.Length > 120 || upn.IndexOf('@') < 1) return null;
            lock (_proxyKonten)
            {
                MitarbeiterSitzung s;
                if (_proxyKonten.TryGetValue(upn, out s) && s.Ablauf > DateTime.UtcNow) return s;
            }
            var ad = Konten.AusM365(upn);
            string name = Konten.Kopf(ctx.Request, e.proxyKopfName);
            if (name.Length == 0) name = ad != null ? ad.Name : upn;
            var neu = new MitarbeiterSitzung
            {
                Konto = "m365:" + upn,
                Kennungen = Konten.Kennungen(ad, upn),
                Name = name,
                Ablauf = DateTime.UtcNow.AddMinutes(10), // AD-Zuordnung und Gruppen alle 10 Minuten neu
                Quelle = "m365",
                Ad = ad
            };
            lock (_proxyKonten)
            {
                bool neuAngemeldet = !_proxyKonten.ContainsKey(upn);
                _proxyKonten[upn] = neu;
                if (neuAngemeldet)
                    Speicher.Protokoll("Microsoft-365-Anmeldung " + upn + " (" + name + (ad != null ? ", AD-Konto " + ad.Anmeldung : ", kein AD-Konto zugeordnet") + ") über den Anwendungsproxy");
            }
            return neu;
        }

        /// <summary>Admin über die AD-Gruppe für einen Mitarbeiter, der über den Anwendungsproxy kommt; sonst null.</summary>
        private string ProxyAdmin(HttpListenerContext ctx)
        {
            if (!ViaProxy(ctx)) return null;
            string gruppe = Speicher.EinstellungenLesen().adAdminGruppe;
            if (gruppe.Length == 0) return null;
            var ma = ProxyMitarbeiter(ctx);
            return ma != null && ma.Ad != null && Konten.IstInGruppe(ma.Ad, gruppe) ? ma.Name : null;
        }

        private void DomaeneAnmelden(HttpListenerContext ctx)
        {
            if (ViaProxy(ctx)) throw new WebFehler(404, "Über den Anwendungsproxy meldet Microsoft 365 an.");
            var einst = Speicher.EinstellungenLesen();
            bool mitarbeiterAn = einst.mitarbeiterAnmeldung != "aus";
            if (!mitarbeiterAn && einst.adAdminGruppe.Length == 0) throw new WebFehler(404, "Die Anmeldung mit dem Domänenkonto ist ausgeschaltet.");
            var wi = ctx.User == null ? null : ctx.User.Identity as WindowsIdentity;
            if (wi == null || !wi.IsAuthenticated || wi.IsAnonymous || wi.IsGuest || string.IsNullOrEmpty(wi.Name))
                throw new WebFehler(403, "Die Anmeldung mit dem Windows-Konto ist nicht möglich.");

            string name = AnzeigeName(wi.Name);
            string sicher = ctx.Request.IsSecureConnection ? "; Secure" : "";

            // Admin über die AD-Gruppe (Gruppenmitgliedschaft aus dem Windows-Token, verschachtelte Gruppen eingeschlossen)
            string admin = Angemeldet(ctx);
            bool adAdmin = false;
            if (admin == null && einst.adAdminGruppe.Length > 0)
            {
                var sid = AdAbgleich.GruppeSid(einst.adAdminGruppe);
                if (sid != null && new WindowsPrincipal(wi).IsInRole(sid))
                {
                    string at = BitConverter.ToString(Speicher.Zufall(32)).Replace("-", "").ToLowerInvariant();
                    lock (_sitzungen)
                        _sitzungen[at] = new Sitzung { Name = name, Hash = "", AdGruppe = einst.adAdminGruppe, Ablauf = DateTime.UtcNow.AddMinutes(SitzungMinuten) };
                    ctx.Response.AppendHeader("Set-Cookie", CookieName + "=" + at + "; Path=/; HttpOnly; SameSite=Strict" + sicher);
                    admin = name;
                    adAdmin = true;
                    Speicher.Protokoll("Admin-Anmeldung mit Windows-Konto " + wi.Name + " (" + name + ", Gruppe " + einst.adAdminGruppe + "), " + IpAdresse(ctx));
                }
            }
            if (!mitarbeiterAn && !adAdmin && admin == null)
                throw new WebFehler(403, "Ihr Windows-Konto gehört nicht zur Gruppe „" + einst.adAdminGruppe + "“.");

            // je Antwort nur ein Cookie (HttpListener fasst mehrere Set-Cookie-Kopfzeilen sonst zusammen):
            // Admins bekommen die Admin-Sitzung, alle anderen die Mitarbeiter-Sitzung
            MitarbeiterSitzung ma = null;
            if (mitarbeiterAn && !adAdmin)
            {
                string token = BitConverter.ToString(Speicher.Zufall(32)).Replace("-", "").ToLowerInvariant();
                var adKonto = Konten.AusAnmeldung(wi.Name);
                ma = new MitarbeiterSitzung
                {
                    Konto = "ad:" + wi.Name.ToLowerInvariant(),
                    Name = name,
                    Ablauf = DateTime.UtcNow.AddHours(MitarbeiterStunden),
                    Ad = adKonto
                };
                ma.Kennungen = Konten.Kennungen(adKonto, null);
                if (!ma.Kennungen.Contains(ma.Konto)) ma.Kennungen.Insert(0, ma.Konto);
                lock (_mitarbeiter)
                {
                    foreach (var alt in _mitarbeiter.Where(p => p.Value.Ablauf < DateTime.UtcNow).Select(p => p.Key).ToList()) _mitarbeiter.Remove(alt);
                    _mitarbeiter[token] = ma;
                }
                ctx.Response.AppendHeader("Set-Cookie", MitarbeiterCookie + "=" + token + "; Path=/; HttpOnly; SameSite=Strict" + sicher);
                Speicher.Protokoll("Domänen-Anmeldung " + wi.Name + " (" + name + "), " + IpAdresse(ctx));
            }
            Antworten(ctx, 200, Gesamtstand(ctx, admin, null, ma, adAdmin));
        }

        /// <summary>Mit dem Domänenkonto angemeldeter Mitarbeiter oder null. Die Sitzung verlängert sich bei jeder Anfrage.</summary>
        private MitarbeiterSitzung Mitarbeiter(HttpListenerContext ctx)
        {
            if (ViaProxy(ctx)) return ProxyMitarbeiter(ctx); // Microsoft 365 (unabhängig von der Einstellung im Haus)
            var c = ctx.Request.Cookies[MitarbeiterCookie];
            if (c == null || string.IsNullOrEmpty(c.Value)) return null;
            if (Speicher.EinstellungenLesen().mitarbeiterAnmeldung == "aus") return null;
            lock (_mitarbeiter)
            {
                MitarbeiterSitzung s;
                if (!_mitarbeiter.TryGetValue(c.Value, out s)) return null;
                if (s.Ablauf < DateTime.UtcNow)
                {
                    _mitarbeiter.Remove(c.Value);
                    return null;
                }
                s.Ablauf = DateTime.UtcNow.AddHours(MitarbeiterStunden);
                return s;
            }
        }

        private static readonly Dictionary<string, string> NamenCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Anzeigename aus dem AD ("Max Muster"); geht das nicht, der Anmeldename ohne Domäne.</summary>
        private static string AnzeigeName(string domaeneBenutzer)
        {
            lock (NamenCache)
            {
                string n;
                if (NamenCache.TryGetValue(domaeneBenutzer, out n)) return n;
            }
            int p = domaeneBenutzer.IndexOf('\\');
            string domaene = p > 0 ? domaeneBenutzer.Substring(0, p) : null;
            string name = p >= 0 ? domaeneBenutzer.Substring(p + 1) : domaeneBenutzer;
            try
            {
                using (var pc = domaene == null ? new PrincipalContext(ContextType.Domain) : new PrincipalContext(ContextType.Domain, domaene))
                using (var u = UserPrincipal.FindByIdentity(pc, IdentityType.SamAccountName, name))
                {
                    if (u != null && !string.IsNullOrEmpty(u.DisplayName)) name = u.DisplayName.Trim();
                    else if (u != null && (!string.IsNullOrEmpty(u.GivenName) || !string.IsNullOrEmpty(u.Surname)))
                        name = ((u.GivenName ?? "") + " " + (u.Surname ?? "")).Trim();
                }
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Anzeigename für " + domaeneBenutzer + " nicht lesbar", ex);
            }
            lock (NamenCache) NamenCache[domaeneBenutzer] = name;
            return name;
        }

        private static string IpAdresse(HttpListenerContext ctx)
        {
            var ep = ctx.Request.RemoteEndPoint;
            return ep == null ? "?" : ep.Address.ToString();
        }

        // ------------------------------------------------------------------ Kundenlogo

        /// <summary>
        /// Hochgeladenes Bild prüfen und als PNG neu speichern (höchstens 800 × 300 Pixel). Nur echte Bilder
        /// (PNG, JPEG, GIF, BMP) werden angenommen; was nicht als Bild lesbar ist, wird abgewiesen. SVG ist nicht erlaubt.
        /// </summary>
        private static void LogoSpeichern(HttpListenerContext ctx, bool dunkel)
        {
            byte[] daten = Lesen(ctx, MaxBild, "Das Bild ist zu groß (höchstens 3 MB).");
            if (daten.Length == 0) throw new WebFehler(400, "Kein Bild empfangen.");
            Image bild;
            try
            {
                bild = Image.FromStream(new MemoryStream(daten), false, true);
            }
            catch
            {
                throw new WebFehler(400, "Die Datei ist kein lesbares Bild. Bitte PNG oder JPG verwenden.");
            }
            using (bild)
            {
                if (bild.Width < 8 || bild.Height < 8) throw new WebFehler(400, "Das Bild ist zu klein.");
                double f = Math.Min(1.0, Math.Min(800.0 / bild.Width, 300.0 / bild.Height));
                int w = Math.Max(1, (int)Math.Round(bild.Width * f)), h = Math.Max(1, (int)Math.Round(bild.Height * f));
                using (var neu = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(neu))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(bild, 0, 0, w, h);
                    }
                    Speicher.OrdnerAnlegen();
                    string ziel = Speicher.LogoPfad(dunkel);
                    string tmp = ziel + ".tmp";
                    neu.Save(tmp, ImageFormat.Png);
                    if (File.Exists(ziel)) File.Delete(ziel);
                    File.Move(tmp, ziel);
                }
            }
        }

        // ------------------------------------------------------------------ Eingaben

        private static Dictionary<string, object> Body(HttpListenerContext ctx)
        {
            string typ = ctx.Request.ContentType ?? "";
            if (!typ.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) throw new WebFehler(415, "Ungültige Anfrage.");
            string text = Encoding.UTF8.GetString(Lesen(ctx, MaxJson, "Die Eingabe ist zu lang."));
            Dictionary<string, object> d;
            try
            {
                d = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            }
            catch
            {
                d = null;
            }
            if (d == null) throw new WebFehler(400, "Ungültige Anfrage.");
            return d;
        }

        private static byte[] Lesen(HttpListenerContext ctx, int max, string zuGross)
        {
            long laenge = ctx.Request.ContentLength64;
            if (laenge > max) throw new WebFehler(413, zuGross);
            using (var ms = new MemoryStream())
            {
                var puffer = new byte[16384];
                int n;
                while ((n = ctx.Request.InputStream.Read(puffer, 0, puffer.Length)) > 0)
                {
                    ms.Write(puffer, 0, n);
                    if (ms.Length > max) throw new WebFehler(413, zuGross);
                }
                return ms.ToArray();
            }
        }

        private static string Text(Dictionary<string, object> b, string feld, int max, bool pflicht)
        {
            object o;
            string t = b.TryGetValue(feld, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture).Trim() : "";
            if (t.Length > max) throw new WebFehler(400, "Eingabe zu lang (" + feld + ").");
            if (pflicht && t.Length == 0) throw new WebFehler(400, "Bitte alle Pflichtfelder ausfüllen.");
            return t;
        }

        private static List<string> Liste(Dictionary<string, object> b, string feld, int max)
        {
            var liste = new List<string>();
            object o;
            if (!b.TryGetValue(feld, out o) || o == null) return liste;
            var werte = o as IEnumerable;
            if (werte == null || o is string) throw new WebFehler(400, "Ungültige Liste (" + feld + ").");
            foreach (object w in werte)
            {
                string t = w == null ? "" : Convert.ToString(w, CultureInfo.InvariantCulture).Trim();
                if (t.Length == 0) continue;
                if (t.Length > max) throw new WebFehler(400, "Eintrag zu lang: " + t);
                if (!liste.Contains(t)) liste.Add(t);
            }
            if (liste.Count > 1000) throw new WebFehler(400, "Liste zu lang (" + feld + ").");
            return liste;
        }

        /// <summary>
        /// Zuordnung Standort → Ort bzw. Art aus der Eingabe (nur für vorhandene Standorte, leere Werte = automatisch).
        /// null, wenn das Feld fehlt (dann bleibt die bisherige Zuordnung).
        /// </summary>
        private static Dictionary<string, string> Zuordnung(Dictionary<string, object> b, string feld, List<string> standorte)
        {
            object o;
            if (!b.TryGetValue(feld, out o) || o == null) return null;
            var quelle = o as Dictionary<string, object>;
            if (quelle == null) throw new WebFehler(400, "Ungültige Zuordnung (" + feld + ").");
            var ziel = new Dictionary<string, string>();
            foreach (var p in quelle)
            {
                string wert = p.Value == null ? "" : Convert.ToString(p.Value, CultureInfo.InvariantCulture).Trim();
                if (wert.Length == 0 || !standorte.Contains(p.Key)) continue;
                if (wert.Length > 50) throw new WebFehler(400, "Eintrag zu lang: " + wert);
                ziel[p.Key] = wert;
            }
            return ziel;
        }

        private static string Termin(string t)
        {
            if (t != Ticket && !IstDatum(t)) throw new WebFehler(400, "Der Wunschtermin ist ungültig.");
            return t;
        }

        private static bool IstDatum(string t)
        {
            DateTime d;
            return t != null && Regex.IsMatch(t, @"^\d{4}-\d{2}-\d{2}$") &&
                DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        // ------------------------------------------------------------------ Antworten

        private static void Antworten(HttpListenerContext ctx, int status, object inhalt)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(inhalt);
            Senden(ctx, status, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", false);
        }

        private static void Senden(HttpListenerContext ctx, int status, byte[] daten, string typ, bool zwischenspeichern)
        {
            var res = ctx.Response;
            try
            {
                res.StatusCode = status;
                res.ContentType = typ;
                // Browser fragen bei Dateien jedes Mal nach (no-cache), Antworten der Schnittstelle werden nie gespeichert
                res.AddHeader("Cache-Control", zwischenspeichern ? "no-cache" : "no-store");
                res.AddHeader("X-Content-Type-Options", "nosniff");
                res.AddHeader("X-Frame-Options", "DENY");
                res.AddHeader("Referrer-Policy", "no-referrer");
                res.AddHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
                res.ContentLength64 = daten.Length;
                // HEAD fragt nur die Kopfdaten ab: Größe melden, aber keinen Inhalt senden
                if (ctx.Request.HttpMethod != "HEAD") res.OutputStream.Write(daten, 0, daten.Length);
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }
    }
}