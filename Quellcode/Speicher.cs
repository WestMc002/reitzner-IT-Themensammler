// reitzner IT-Themensammler – Datenhaltung: Themen, Stammdaten, Einstellungen und Admin-Konten (JSON im Datenordner)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ITThemensammler
{
    // Feldnamen klein geschrieben: so bleibt daten.json mit dem bisherigen Entwurf (PowerShell-Webserver) verträglich.

    /// <summary>Ein eingetragenes Thema.</summary>
    public sealed class Anfrage
    {
        public string id { get; set; }
        public string standort { get; set; }
        public string benutzer { get; set; }
        /// <summary>Datum JJJJ-MM-TT oder "ticket".</summary>
        public string wunschtermin { get; set; }
        public string problem { get; set; }
        /// <summary>Erfasst am (JJJJ-MM-TT HH:mm).</summary>
        public string erfasst { get; set; }
        /// <summary>Leer = offen, sonst Datum JJJJ-MM-TT.</summary>
        public string erledigtAm { get; set; }
        /// <summary>Admin-Konto, das das Thema als erledigt markiert hat.</summary>
        public string erledigtVon { get; set; }
        /// <summary>Was wurde gemacht?</summary>
        public string notiz { get; set; }
        /// <summary>
        /// SHA-256 (hex) des Besitzer-Schlüssels, den nur der Browser des Erstellers kennt. Damit darf der Ersteller
        /// sein Thema bearbeiten und löschen, solange es offen ist. Leer = nur Admins (z. B. importierte Themen).
        /// Wird nie an die Weboberfläche ausgegeben.
        /// </summary>
        public string schluessel { get; set; }
        /// <summary>Angemeldetes Konto des Erstellers, z. B. "ad:gemeinde\mmuster" (später auch "m365:…"). Leer = ohne Anmeldung eingetragen.</summary>
        public string ersteller { get; set; }
        /// <summary>Stand eines offenen Themas: "" = offen, "arbeit" = in Arbeit, "rueckfrage" = Rückfrage an den Mitarbeiter, "beantwortet" = Rückfrage beantwortet.</summary>
        public string status { get; set; }
        /// <summary>Hinweis bzw. Rückfrage des Technikers zum offenen Thema (für den Mitarbeiter sichtbar).</summary>
        public string hinweis { get; set; }
        /// <summary>Leer = kein Bild, sonst Zeitstempel des angehängten Bildes (Datei Anhaenge\&lt;id&gt;.jpg).</summary>
        public string bild { get; set; }
    }

    public sealed class Stammdaten
    {
        public List<string> standorte { get; set; }
        public List<string> benutzer { get; set; }
        /// <summary>Termine als JJJJ-MM-TT, aufsteigend.</summary>
        public List<string> termine { get; set; }
        /// <summary>Von Hand festgelegter Ort je Standort (Gruppierung nach Ort); fehlt ein Eintrag, schlägt die Weboberfläche ihn vor.</summary>
        public Dictionary<string, string> orte { get; set; }
        /// <summary>Von Hand festgelegte Art je Standort (Gruppierung nach Art, z. B. Feuerwehren).</summary>
        public Dictionary<string, string> arten { get; set; }
        /// <summary>Festgelegte Ort-Gruppen in der Reihenfolge der Auswahl (leer = Orte automatisch erkennen).</summary>
        public List<string> ortGruppen { get; set; }
        /// <summary>Festgelegte Art-Gruppen in der Reihenfolge der Auswahl (leer = Standardliste).</summary>
        public List<string> artGruppen { get; set; }
        /// <summary>Sammelgruppe für Standorte ohne Ort (steht oben), Standard "Allgemein".</summary>
        public string sammelOrt { get; set; }
        /// <summary>Sammelgruppe für Standorte ohne Art (steht unten), Standard "Sonstige".</summary>
        public string sammelArt { get; set; }
        /// <summary>Benutzer, die der AD-Abgleich eingetragen hat (fallen beim Abgleich wieder weg, wenn sie im AD fehlen).</summary>
        public List<string> adBenutzer { get; set; }
        /// <summary>AD-Benutzer, die ein Admin aus der Liste entfernt hat; der Abgleich trägt sie nicht wieder ein.</summary>
        public List<string> adAusgeschlossen { get; set; }
        /// <summary>true: Standorte in der Reihenfolge der Liste (per Drag and Drop festgelegt), sonst alphabetisch.</summary>
        public bool? eigeneReihenfolge { get; set; }
    }

    public sealed class DatenDatei
    {
        public Stammdaten stammdaten { get; set; }
        public List<Anfrage> anfragen { get; set; }

        /// <summary>Fehlende Listen ergänzen (ältere bzw. von Hand bearbeitete Dateien).</summary>
        public void Vervollstaendigen()
        {
            if (stammdaten == null) stammdaten = new Stammdaten();
            if (stammdaten.standorte == null) stammdaten.standorte = new List<string>();
            if (stammdaten.benutzer == null) stammdaten.benutzer = new List<string>();
            if (stammdaten.termine == null) stammdaten.termine = new List<string>();
            if (stammdaten.orte == null) stammdaten.orte = new Dictionary<string, string>();
            if (stammdaten.arten == null) stammdaten.arten = new Dictionary<string, string>();
            if (stammdaten.ortGruppen == null) stammdaten.ortGruppen = new List<string>();
            if (stammdaten.artGruppen == null) stammdaten.artGruppen = new List<string>();
            if (string.IsNullOrEmpty(stammdaten.sammelOrt)) stammdaten.sammelOrt = "Allgemein";
            if (string.IsNullOrEmpty(stammdaten.sammelArt)) stammdaten.sammelArt = "Sonstige";
            if (stammdaten.adBenutzer == null) stammdaten.adBenutzer = new List<string>();
            if (stammdaten.adAusgeschlossen == null) stammdaten.adAusgeschlossen = new List<string>();
            if (!stammdaten.eigeneReihenfolge.HasValue) stammdaten.eigeneReihenfolge = false;
            if (anfragen == null) anfragen = new List<Anfrage>();
            anfragen.RemoveAll(a => a == null || string.IsNullOrEmpty(a.id));
            foreach (var a in anfragen)
            {
                if (a.standort == null) a.standort = "";
                if (a.benutzer == null) a.benutzer = "";
                if (a.wunschtermin == null) a.wunschtermin = "";
                if (a.problem == null) a.problem = "";
                if (a.erfasst == null) a.erfasst = "";
                if (a.erledigtAm == null) a.erledigtAm = "";
                if (a.erledigtVon == null) a.erledigtVon = "";
                if (a.notiz == null) a.notiz = "";
                if (a.schluessel == null) a.schluessel = "";
                if (a.ersteller == null) a.ersteller = "";
                if (a.status != "arbeit" && a.status != "rueckfrage" && a.status != "beantwortet") a.status = "";
                if (a.hinweis == null) a.hinweis = "";
                if (a.bild == null) a.bild = "";
            }
        }
    }

    /// <summary>Lokales Admin-Konto (Kennwort nur als PBKDF2-Prüfwert).</summary>
    public sealed class Konto
    {
        public string name { get; set; }
        public string salz { get; set; }
        public string hash { get; set; }
        public int runden { get; set; }
        public string erstellt { get; set; }
    }

    public sealed class Einstellungen
    {
        public const int StandardPort = 8080;

        public int port { get; set; }
        /// <summary>Überschrift der Webseite.</summary>
        public string titel { get; set; }
        public string untertitel { get; set; }
        /// <summary>Name des Kunden (z. B. für den Alternativtext des Kundenlogos).</summary>
        public string kunde { get; set; }
        /// <summary>Hinweis bei "Schnellstmöglich/Ticket": E-Mail und Telefon der Hotline.</summary>
        public string ticketMail { get; set; }
        public string hotline { get; set; }
        /// <summary>Auswahl "Standort" gruppieren: "ort", "art" oder "keine" (je Kunde einstellbar).</summary>
        public string gruppierung { get; set; }
        /// <summary>Anmeldung der Mitarbeiter: "aus" (ohne Anmeldung), "domaene" (Domänenkonto, wenn möglich), "pflicht" (nur mit Domänenkonto).</summary>
        public string mitarbeiterAnmeldung { get; set; }
        /// <summary>Dürfen Mitarbeiter (ohne Admin-Anmeldung) erledigte Themen sehen? Fehlt der Wert: ja.</summary>
        public bool? erledigteSichtbar { get; set; }
        /// <summary>Kennung der Logo-Dateien (ändert sich bei jedem Hochladen, damit der Browser das neue Bild lädt).</summary>
        public string logoStand { get; set; }
        public List<Konto> konten { get; set; }
        /// <summary>AD-Abgleich der Benutzerliste: Distinguished Names der OUs (mit Unter-OUs).</summary>
        public List<string> adOus { get; set; }
        /// <summary>Benutzerliste täglich automatisch mit dem AD abgleichen.</summary>
        public bool? adAbgleich { get; set; }
        /// <summary>Zeitpunkt des letzten erfolgreichen Abgleichs (ISO, Ortszeit).</summary>
        public string adLetzter { get; set; }
        /// <summary>Ergebnis bzw. Fehler des letzten Abgleichs.</summary>
        public string adErgebnis { get; set; }
        /// <summary>Erledigte Themen nach so vielen Monaten löschen (0 = nie).</summary>
        public int loeschMonate { get; set; }
        /// <summary>Vor dem Löschen in Archiv\Themen-JJJJ.csv schreiben.</summary>
        public bool? loeschArchiv { get; set; }
        /// <summary>Standort im Formular aus dem AD vorbelegen: "" (aus), "buero" oder "abteilung".</summary>
        public string adStandortFeld { get; set; }
        /// <summary>AD-Abgleich: Konten nicht übernehmen, deren Name oder Anmeldename einen dieser Texte enthält.</summary>
        public List<string> adAusschluss { get; set; }
        /// <summary>AD-Abgleich: Konten ohne Anzeigenamen mit dem Anmeldenamen übernehmen.</summary>
        public bool? adOhneNamen { get; set; }
        /// <summary>Zugriff von außen über den Entra-Anwendungsproxy (Microsoft 365) – eigener Port, nur vom Connector erreichbar.</summary>
        public bool? proxyAn { get; set; }
        public int proxyPort { get; set; }
        /// <summary>IP-Adressen der Connector-Server; nur von dort werden Anfragen auf dem Proxy-Port angenommen.</summary>
        public List<string> proxyIps { get; set; }
        /// <summary>Kopfzeilen, die der Anwendungsproxy schickt (headerbasiertes einmaliges Anmelden).</summary>
        public string proxyKopfUpn { get; set; }
        public string proxyKopfName { get; set; }
        /// <summary>Mitglieder dieser AD-Gruppe sind nach der Anmeldung mit dem Windows-Konto Admins (leer = aus).</summary>
        public string adAdminGruppe { get; set; }
        /// <summary>Automatisch auf GitHub nach Updates suchen (Standard: ja).</summary>
        public bool? updateAuto { get; set; }
        /// <summary>Zeitpunkt der letzten erfolgreichen Update-Prüfung (UTC, ISO 8601).</summary>
        public string updateLetzte { get; set; }
        /// <summary>Neueste Version auf GitHub bei der letzten Prüfung und ihre Release-Seite.</summary>
        public string updateVersion { get; set; }
        public string updateSeite { get; set; }

        public void Vervollstaendigen()
        {
            if (port <= 0 || port > 65535) port = StandardPort;
            if (titel == null) titel = "IT-Themensammlung";
            if (untertitel == null) untertitel = "Sammlung bis zum nächsten Regeltermin";
            if (kunde == null) kunde = "";
            if (ticketMail == null) ticketMail = "";
            if (hotline == null) hotline = "";
            if (gruppierung != "ort" && gruppierung != "art" && gruppierung != "keine") gruppierung = "ort";
            if (!erledigteSichtbar.HasValue) erledigteSichtbar = true;
            if (mitarbeiterAnmeldung != "aus" && mitarbeiterAnmeldung != "domaene" && mitarbeiterAnmeldung != "pflicht") mitarbeiterAnmeldung = "aus";
            if (logoStand == null) logoStand = "";
            if (konten == null) konten = new List<Konto>();
            konten.RemoveAll(k => k == null || string.IsNullOrEmpty(k.name));
            if (adOus == null) adOus = new List<string>();
            if (!adAbgleich.HasValue) adAbgleich = false;
            if (adLetzter == null) adLetzter = "";
            if (adErgebnis == null) adErgebnis = "";
            if (!updateAuto.HasValue) updateAuto = true;
            if (adAdminGruppe == null) adAdminGruppe = "";
            if (loeschMonate < 0 || loeschMonate > 120) loeschMonate = 0;
            if (!loeschArchiv.HasValue) loeschArchiv = true;
            if (adStandortFeld != "buero" && adStandortFeld != "abteilung") adStandortFeld = "";
            if (adAusschluss == null) adAusschluss = new List<string>();
            if (!adOhneNamen.HasValue) adOhneNamen = false;
            if (!proxyAn.HasValue) proxyAn = false;
            if (proxyPort < 1 || proxyPort > 65535) proxyPort = 8081;
            if (proxyIps == null) proxyIps = new List<string>();
            if (string.IsNullOrEmpty(proxyKopfUpn)) proxyKopfUpn = "X-Themensammler-UPN";
            if (string.IsNullOrEmpty(proxyKopfName)) proxyKopfName = "X-Themensammler-Name";
            if (updateLetzte == null) updateLetzte = "";
            if (updateVersion == null) updateVersion = "";
            if (updateSeite == null) updateSeite = "";
        }
    }

    /// <summary>
    /// Zugriff auf die Dateien im Datenordner (%ProgramData%\reitzner IT-Themensammler). Dienst und Verwaltungsfenster
    /// arbeiten mit denselben Dateien; vor jedem Zugriff wird geprüft, ob die Datei inzwischen geändert wurde.
    /// </summary>
    internal static class Speicher
    {
        public const int BackupTage = 30;
        public const int HashRunden = 100000;

        private static readonly object Sperre = new object();
        private static DatenDatei _daten;
        private static DateTime _datenStand;
        private static Einstellungen _einst;
        private static DateTime _einstStand;

        /// <summary>Nur für die Screenshots: eigener Datenordner mit Beispieldaten (sonst null).</summary>
        public static string DemoOrdner;

        /// <summary>Datenordner: %ProgramData%\reitzner IT-Themensammler (bei den Screenshots ein Ordner mit Beispieldaten).</summary>
        public static string Ordner
        {
            get { return DemoOrdner ?? StandardOrdner; }
        }

        public static string StandardOrdner
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.Name); }
        }

        /// <summary>Für die Anzeige: bei den Screenshots der übliche Ordner statt des temporären mit den Beispieldaten.</summary>
        public static string AnzeigeOrdner
        {
            get { return DemoOrdner != null ? @"C:\ProgramData\" + AppInfo.Name : Ordner; }
        }

        public static string DatenPfad { get { return Path.Combine(Ordner, "daten.json"); } }
        public static string EinstellungenPfad { get { return Path.Combine(Ordner, "einstellungen.json"); } }
        public static string BackupOrdner { get { return Path.Combine(Ordner, "Backup"); } }
        /// <summary>Bilder zu den Themen (eine Datei &lt;id&gt;.jpg je Thema).</summary>
        public static string AnhangOrdner { get { return Path.Combine(Ordner, "Anhaenge"); } }
        public static string AnhangPfad(string id) { return Path.Combine(AnhangOrdner, id + ".jpg"); }
        /// <summary>Archiv gelöschter erledigter Themen (Themen-JJJJ.csv), falls eingeschaltet.</summary>
        public static string ArchivOrdner { get { return Path.Combine(Ordner, "Archiv"); } }

        public static void AnhangLoeschen(string id)
        {
            try { if (File.Exists(AnhangPfad(id))) File.Delete(AnhangPfad(id)); }
            catch (Exception ex) { Fehler("Bild zu Thema " + id + " löschen", ex); }
        }

        /// <summary>Kundenlogo für den hellen bzw. dunklen Modus.</summary>
        public static string LogoPfad(bool dunkel)
        {
            return Path.Combine(Ordner, dunkel ? "Kundenlogo-dunkel.png" : "Kundenlogo-hell.png");
        }

        private static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        /// <summary>
        /// Datenordner anlegen. Neu angelegt nur für Administratoren und SYSTEM zugänglich
        /// (enthält Kundendaten und die Prüfwerte der Admin-Kennwörter).
        /// </summary>
        public static void OrdnerAnlegen()
        {
            if (Directory.Exists(Ordner)) return;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var vererbung = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, vererbung, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, vererbung, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(Ordner, sec);
        }

        // ------------------------------------------------------------------ Daten (Themen und Stammdaten)

        /// <summary>Lesend auf die Daten zugreifen (unter Sperre, mit dem neuesten Stand der Datei).</summary>
        public static T Lesen<T>(Func<DatenDatei, T> f)
        {
            lock (Sperre) return f(DatenLaden());
        }

        /// <summary>Daten ändern und speichern (unter Sperre). Wirft die Funktion eine Ausnahme, wird nichts gespeichert.</summary>
        public static T Aendern<T>(Func<DatenDatei, T> f)
        {
            lock (Sperre)
            {
                var d = DatenLaden();
                // Änderungen an einer Kopie, damit eine abgebrochene Änderung den Stand im Speicher nicht verfälscht
                var kopie = Json().Deserialize<DatenDatei>(Json().Serialize(d));
                kopie.Vervollstaendigen();
                T ergebnis = f(kopie);
                kopie.Vervollstaendigen(); // neu angelegte Themen (z. B. aus der Excel-Übernahme): fehlende Felder ergänzen
                DatenSpeichern(kopie);
                return ergebnis;
            }
        }

        private static DatenDatei DatenLaden()
        {
            DateTime stand = Zeitstempel(DatenPfad);
            if (_daten != null && stand == _datenStand) return _daten;
            DatenDatei d;
            if (stand == DateTime.MinValue)
                d = new DatenDatei();
            else
                d = Json().Deserialize<DatenDatei>(TextLesen(DatenPfad)) ?? new DatenDatei();
            d.Vervollstaendigen();
            _daten = d;
            _datenStand = stand;
            return d;
        }

        private static void DatenSpeichern(DatenDatei d)
        {
            OrdnerAnlegen();
            TagesSicherung();
            AtomarSchreiben(DatenPfad, Json().Serialize(d));
            _daten = d;
            _datenStand = Zeitstempel(DatenPfad);
        }

        /// <summary>Einmal pro Tag den alten Stand nach Backup\daten-JJJJ-MM-TT.json sichern, ältere als 30 Tage löschen.</summary>
        private static void TagesSicherung()
        {
            try
            {
                if (!File.Exists(DatenPfad)) return;
                Directory.CreateDirectory(BackupOrdner);
                string ziel = Path.Combine(BackupOrdner, "daten-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
                if (File.Exists(ziel)) return;
                File.Copy(DatenPfad, ziel);
                foreach (var f in new DirectoryInfo(BackupOrdner).GetFiles("daten-*.json"))
                    if (f.LastWriteTime < DateTime.Now.AddDays(-BackupTage)) f.Delete();
            }
            catch (Exception ex)
            {
                Fehler("Tagessicherung", ex);
            }
        }

        /// <summary>
        /// daten.json aus einer anderen Datei übernehmen (z. B. aus dem bisherigen Entwurf). Der bisherige Stand wird vorher
        /// als Backup\daten-vor-Import-….json gesichert. Liefert die Zahl der übernommenen Themen.
        /// </summary>
        public static int Importieren(string datei)
        {
            var neu = Json().Deserialize<DatenDatei>(TextLesen(datei));
            if (neu == null || (neu.anfragen == null && neu.stammdaten == null))
                throw new InvalidDataException("Die Datei enthält keine Themen und keine Stammdaten.");
            neu.Vervollstaendigen();
            Sichern("vor-Import");
            lock (Sperre)
            {
                OrdnerAnlegen();
                AtomarSchreiben(DatenPfad, Json().Serialize(neu));
                _daten = null;
            }
            Protokoll("Daten übernommen aus " + datei + " (" + neu.anfragen.Count + " Themen)");
            return neu.anfragen.Count;
        }

        /// <summary>Aktuellen Stand von daten.json nach Backup\daten-&lt;anlass&gt;-JJJJ-MM-TT-HHmmss.json sichern (z. B. vor einem Import).</summary>
        public static void Sichern(string anlass)
        {
            lock (Sperre)
            {
                if (!File.Exists(DatenPfad)) return;
                OrdnerAnlegen();
                Directory.CreateDirectory(BackupOrdner);
                File.Copy(DatenPfad, Path.Combine(BackupOrdner, "daten-" + anlass + "-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".json"), true);
            }
        }

        // ------------------------------------------------------------------ Einstellungen und Konten

        /// <summary>Kopie der Einstellungen (neuester Stand der Datei).</summary>
        public static Einstellungen EinstellungenLesen()
        {
            lock (Sperre)
            {
                var e = EinstellungenLaden();
                var kopie = Json().Deserialize<Einstellungen>(Json().Serialize(e));
                kopie.Vervollstaendigen();
                return kopie;
            }
        }

        /// <summary>Einstellungen ändern und speichern (unter Sperre, auf dem neuesten Stand der Datei).</summary>
        public static void EinstellungenAendern(Action<Einstellungen> f)
        {
            lock (Sperre)
            {
                var kopie = Json().Deserialize<Einstellungen>(Json().Serialize(EinstellungenLaden()));
                kopie.Vervollstaendigen();
                f(kopie);
                OrdnerAnlegen();
                AtomarSchreiben(EinstellungenPfad, Json().Serialize(kopie));
                _einst = kopie;
                _einstStand = Zeitstempel(EinstellungenPfad);
            }
        }

        private static Einstellungen EinstellungenLaden()
        {
            DateTime stand = Zeitstempel(EinstellungenPfad);
            if (_einst != null && stand == _einstStand) return _einst;
            Einstellungen e;
            if (stand == DateTime.MinValue)
                e = new Einstellungen();
            else
                e = Json().Deserialize<Einstellungen>(TextLesen(EinstellungenPfad)) ?? new Einstellungen();
            e.Vervollstaendigen();
            _einst = e;
            _einstStand = stand;
            return e;
        }

        /// <summary>Kennwort prüfen. Liefert das Konto (mit korrekter Schreibweise des Namens) oder null.</summary>
        public static Konto Pruefen(string name, string kennwort)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(kennwort)) return null;
            var e = EinstellungenLesen();
            var k = e.konten.FirstOrDefault(x => string.Equals(x.name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (k == null) return null;
            try
            {
                string h = Hash(kennwort, Convert.FromBase64String(k.salz), k.runden);
                return GleicheTexte(h, k.hash) ? k : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Neues Konto anlegen bzw. das Kennwort eines vorhandenen Kontos setzen.</summary>
        public static void KennwortSetzen(string name, string kennwort)
        {
            name = (name ?? "").Trim();
            string fehler = KontoFehler(name, kennwort);
            if (fehler != null) throw new ArgumentException(fehler);
            var salz = Zufall(16);
            string hash = Hash(kennwort, salz, HashRunden);
            EinstellungenAendern(e =>
            {
                var k = e.konten.FirstOrDefault(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase));
                if (k == null)
                {
                    k = new Konto { name = name, erstellt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
                    e.konten.Add(k);
                }
                k.salz = Convert.ToBase64String(salz);
                k.runden = HashRunden;
                k.hash = hash;
            });
        }

        /// <summary>Prüfung für Name und Kennwort eines Kontos; null = in Ordnung.</summary>
        public static string KontoFehler(string name, string kennwort)
        {
            if (string.IsNullOrEmpty(name)) return "Bitte einen Namen eingeben.";
            if (name.Length > 50) return "Der Name ist zu lang (höchstens 50 Zeichen).";
            if (kennwort == null || kennwort.Length < 8) return "Das Kennwort muss mindestens 8 Zeichen lang sein.";
            if (kennwort.Length > 200) return "Das Kennwort ist zu lang.";
            return null;
        }

        public static void KontoLoeschen(string name)
        {
            EinstellungenAendern(e => e.konten.RemoveAll(x => string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase)));
        }

        public static string Hash(string kennwort, byte[] salz, int runden)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(kennwort), salz, runden))
                return Convert.ToBase64String(kdf.GetBytes(32));
        }

        /// <summary>SHA-256 eines Textes als Kleinbuchstaben-Hex.</summary>
        public static string Pruefsumme(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] Zufall(int laenge)
        {
            var b = new byte[laenge];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        /// <summary>Vergleich in konstanter Zeit (verrät nicht, ab welchem Zeichen sich die Werte unterscheiden).</summary>
        public static bool GleicheTexte(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ------------------------------------------------------------------ Dateien

        private static DateTime Zeitstempel(string pfad)
        {
            try { return File.Exists(pfad) ? File.GetLastWriteTimeUtc(pfad) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static string TextLesen(string pfad)
        {
            for (int i = 1; ; i++)
            {
                try
                {
                    using (var fs = new FileStream(pfad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8, true))
                        return r.ReadToEnd();
                }
                catch (IOException)
                {
                    if (i >= 5) throw;
                    Thread.Sleep(200); // z. B. kurz vom Virenscanner oder vom anderen Prozess gesperrt
                }
            }
        }

        /// <summary>Erst in eine Temp-Datei schreiben, dann ersetzen: ein Absturz zerstört die Datei nie halb.</summary>
        private static void AtomarSchreiben(string pfad, string inhalt)
        {
            string tmp = pfad + ".tmp";
            File.WriteAllText(tmp, inhalt, new UTF8Encoding(false));
            for (int i = 1; ; i++)
            {
                try
                {
                    if (File.Exists(pfad)) File.Replace(tmp, pfad, null);
                    else File.Move(tmp, pfad);
                    return;
                }
                catch (IOException)
                {
                    if (i >= 5) throw;
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    if (i >= 5) throw;
                    Thread.Sleep(200);
                }
            }
        }

        // ------------------------------------------------------------------ Protokolle

        /// <summary>Änderungen und Anmeldungen (Protokoll.log im Datenordner).</summary>
        public static void Protokoll(string text)
        {
            Schreiben("Protokoll.log", text);
        }

        /// <summary>Fehler mit Einzelheiten (Fehler.log im Datenordner).</summary>
        public static void Fehler(string text, Exception ex)
        {
            Schreiben("Fehler.log", text + (ex == null ? "" : ": " + ex));
        }

        private static readonly object LogSperre = new object();

        private static void Schreiben(string datei, string text)
        {
            try
            {
                lock (LogSperre)
                {
                    Directory.CreateDirectory(Ordner);
                    string pfad = Path.Combine(Ordner, datei);
                    // nicht endlos wachsen lassen: ab 2 MB die alte Datei als .alt.log aufheben
                    var fi = new FileInfo(pfad);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string alt = Path.ChangeExtension(pfad, ".alt.log");
                        if (File.Exists(alt)) File.Delete(alt);
                        File.Move(pfad, alt);
                    }
                    File.AppendAllText(pfad, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}