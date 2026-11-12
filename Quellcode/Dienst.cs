// reitzner IT-Themensammler – Windows-Dienst sowie Einrichten, Starten, Beenden und Entfernen des Dienstes
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>Der Windows-Dienst: startet beim Hochfahren den Webserver, auch ohne angemeldeten Benutzer (Konto LocalSystem).</summary>
    internal sealed class Dienst : ServiceBase
    {
        public const string Argument = "--dienst";
        public const string DienstName = "ITThemensammler";

        private readonly WebServer _server = new WebServer();

        public Dienst()
        {
            ServiceName = DienstName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _server.Starten();
                UpdatePruefung.DienstStarten(); // neue Version auf GitHub? (Hinweis in der Weboberfläche)
                Ereignis.Schreiben(Ereignis.Gestartet, EventLogEntryType.Information,
                    "Dienst gestartet. Weboberfläche auf Port " + _server.Port + ", Version " + AppInfo.Version + ".");
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Dienst: Webserver konnte nicht starten", ex);
                Ereignis.Schreiben(Ereignis.Startfehler, EventLogEntryType.Error, "Der Webserver konnte nicht starten: " + ex.Message);
                throw; // Dienststatus "Fehler", Windows startet ihn laut Wiederherstellungsoptionen neu
            }
        }

        protected override void OnStop()
        {
            UpdatePruefung.DienstBeenden();
            _server.Stoppen();
            Ereignis.Schreiben(Ereignis.Beendet, EventLogEntryType.Information, "Dienst beendet.");
        }

        protected override void OnShutdown()
        {
            UpdatePruefung.DienstBeenden();
            _server.Stoppen();
            Ereignis.Schreiben(Ereignis.Beendet, EventLogEntryType.Information, "Dienst beendet (Windows fährt herunter).");
        }
    }

    /// <summary>
    /// Einträge in der Windows-Ereignisanzeige (Protokoll "Anwendung", Quelle "reitzner IT-Themensammler") für die Überwachung,
    /// z. B. durch ein RMM-System. Feste Ereignis-IDs, siehe Liesmich.txt. Fehlermeldungen gleicher ID höchstens alle 10 Minuten.
    /// </summary>
    internal static class Ereignis
    {
        public const string Quelle = AppInfo.Name;
        public const int Gestartet = 1000, Beendet = 1001, Eingerichtet = 1002, Aktualisiert = 1003, Entfernt = 1004;
        public const int Startfehler = 1100, Anfragefehler = 1101, Datenfehler = 1102, AdFehler = 1103;

        private static readonly Dictionary<int, DateTime> Zuletzt = new Dictionary<int, DateTime>();

        /// <summary>Quelle anlegen (braucht Administratorrechte; beim Einrichten des Dienstes).</summary>
        public static void QuelleAnlegen()
        {
            try
            {
                if (!EventLog.SourceExists(Quelle)) EventLog.CreateEventSource(Quelle, "Application");
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Ereignisquelle anlegen", ex);
            }
        }

        public static void Schreiben(int id, EventLogEntryType typ, string text)
        {
            try
            {
                if (typ != EventLogEntryType.Information)
                {
                    lock (Zuletzt)
                    {
                        DateTime t;
                        if (Zuletzt.TryGetValue(id, out t) && t > DateTime.UtcNow.AddMinutes(-10)) return;
                        Zuletzt[id] = DateTime.UtcNow;
                    }
                }
                if (!EventLog.SourceExists(Quelle)) EventLog.CreateEventSource(Quelle, "Application");
                EventLog.WriteEntry(Quelle, text, typ, id);
            }
            catch { } // Ereignisanzeige nicht erreichbar: Fehler.log bleibt die Quelle
        }
    }

    internal enum DienstZustand
    {
        NichtEingerichtet,
        Gestoppt,
        Startet,
        Laeuft,
        Stoppt,
        Unbekannt
    }

    /// <summary>
    /// Dienst einrichten und steuern (aus dem Verwaltungsfenster, mit Administratorrechten). Beim Einrichten wird die EXE nach
    /// "C:\Program Files\reitzner IT-Themensammler" kopiert und von dort als Dienst gestartet. So bleibt der Projekt- bzw.
    /// Download-Ordner frei, und eine neue Version wird über "Dienst aktualisieren" übernommen.
    /// </summary>
    internal static class DienstSteuerung
    {
        public const string FirewallRegel = "reitzner IT-Themensammler";
        public const string FirewallRegelProxy = FirewallRegel + " (Anwendungsproxy)";

        public static string ProgrammOrdner
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name); }
        }

        public static string ProgrammPfad
        {
            get { return Path.Combine(ProgrammOrdner, AppInfo.Name + ".exe"); }
        }

        public static DienstZustand Zustand()
        {
            if (Demo.Active) return DienstZustand.Laeuft; // Screenshots: eingerichteter, laufender Dienst
            try
            {
                var sc = ServiceController.GetServices().FirstOrDefault(s => string.Equals(s.ServiceName, Dienst.DienstName, StringComparison.OrdinalIgnoreCase));
                if (sc == null) return DienstZustand.NichtEingerichtet;
                using (sc)
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return DienstZustand.Laeuft;
                        case ServiceControllerStatus.Stopped: return DienstZustand.Gestoppt;
                        case ServiceControllerStatus.StartPending:
                        case ServiceControllerStatus.ContinuePending: return DienstZustand.Startet;
                        case ServiceControllerStatus.StopPending:
                        case ServiceControllerStatus.PausePending:
                        case ServiceControllerStatus.Paused: return DienstZustand.Stoppt;
                    }
                }
            }
            catch { }
            return DienstZustand.Unbekannt;
        }

        /// <summary>Version der eingerichteten Programmdatei (leer, wenn nicht vorhanden).</summary>
        public static string InstallierteVersion()
        {
            if (Demo.Active) return AppInfo.Version;
            try { return File.Exists(ProgrammPfad) ? FileVersionInfo.GetVersionInfo(ProgrammPfad).FileVersion ?? "" : ""; }
            catch { return ""; }
        }

        /// <summary>Läuft dieses Fenster aus einer anderen Datei als der eingerichtete Dienst und ist die Version neuer?</summary>
        public static bool NeuereVersion()
        {
            string inst = InstallierteVersion();
            if (Demo.Active || inst.Length == 0 || SelbePfade(Application.ExecutablePath, ProgrammPfad)) return false;
            Version a, b;
            return Version.TryParse(AppInfo.Version, out a) && Version.TryParse(inst, out b) && a > b;
        }

        /// <summary>Programm kopieren, Dienst anlegen (automatischer Start, Neustart bei Fehlern), Firewall freigeben und starten.</summary>
        public static void Einrichten(int port)
        {
            if (Zustand() != DienstZustand.NichtEingerichtet) throw new InvalidOperationException("Der Dienst ist bereits eingerichtet.");
            ProgrammKopieren();
            Sc("create " + Dienst.DienstName + " binPath= \"\\\"" + ProgrammPfad + "\\\" " + Dienst.Argument + "\" start= delayed-auto DisplayName= \"" + AppInfo.Name + "\"");
            Sc("description " + Dienst.DienstName + " \"Weboberfläche zum Sammeln von IT-Themen für den Vor-Ort-Termin\"", true);
            Sc("failure " + Dienst.DienstName + " reset= 86400 actions= restart/60000/restart/60000/restart/300000", true);
            FirewallSetzen(port);
            Ereignis.QuelleAnlegen();
            Starten();
            Speicher.Protokoll("Dienst eingerichtet (" + ProgrammPfad + ")");
            Ereignis.Schreiben(Ereignis.Eingerichtet, EventLogEntryType.Information, "Dienst eingerichtet (" + ProgrammPfad + "), Version " + AppInfo.Version + ".");
        }

        /// <summary>Neue Programmversion übernehmen: Dienst beenden, EXE ersetzen, wieder starten.</summary>
        public static void Aktualisieren()
        {
            bool lief = Zustand() == DienstZustand.Laeuft;
            Beenden();
            ProgrammKopieren();
            Ereignis.QuelleAnlegen();
            if (lief) Starten();
            Speicher.Protokoll("Dienst aktualisiert auf Version " + AppInfo.Version);
            Ereignis.Schreiben(Ereignis.Aktualisiert, EventLogEntryType.Information, "Dienst aktualisiert auf Version " + AppInfo.Version + ".");
        }

        /// <summary>Dienst und Firewall-Regel entfernen. Die Daten im Datenordner bleiben erhalten.</summary>
        public static void Entfernen()
        {
            Beenden();
            Sc("delete " + Dienst.DienstName);
            FirewallEntfernen();
            // Programmordner aufräumen (nur, wenn das Verwaltungsfenster nicht selbst von dort läuft)
            try
            {
                if (!SelbePfade(Path.GetDirectoryName(Application.ExecutablePath), ProgrammOrdner) && Directory.Exists(ProgrammOrdner))
                {
                    for (int i = 0; i < 10 && File.Exists(ProgrammPfad); i++)
                    {
                        try { File.Delete(ProgrammPfad); }
                        catch { System.Threading.Thread.Sleep(500); }
                    }
                    Directory.Delete(ProgrammOrdner, true);
                }
            }
            catch { }
            Speicher.Protokoll("Dienst entfernt");
            Ereignis.Schreiben(Ereignis.Entfernt, EventLogEntryType.Information, "Dienst entfernt. Die Daten bleiben erhalten.");
        }

        public static void Starten()
        {
            using (var sc = new ServiceController(Dienst.DienstName))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        public static void Beenden()
        {
            using (var sc = new ServiceController(Dienst.DienstName))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return;
                if (sc.CanStop) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }

        public static void Neustarten()
        {
            Beenden();
            Starten();
        }

        private static void ProgrammKopieren()
        {
            string quelle = Application.ExecutablePath;
            Directory.CreateDirectory(ProgrammOrdner);
            if (!SelbePfade(quelle, ProgrammPfad))
            {
                Exception letzter = null;
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        File.Copy(quelle, ProgrammPfad, true);
                        letzter = null;
                        break;
                    }
                    catch (IOException ex) { letzter = ex; System.Threading.Thread.Sleep(500); } // Dienst gibt die Datei gerade frei
                    catch (UnauthorizedAccessException ex) { letzter = ex; System.Threading.Thread.Sleep(500); }
                }
                if (letzter != null) throw new IOException("Die Programmdatei konnte nicht nach " + ProgrammOrdner + " kopiert werden: " + letzter.Message, letzter);
            }
            // Anleitung mitnehmen (Info-Fenster › Anleitung)
            string liesmich = Path.Combine(Path.GetDirectoryName(quelle), "Liesmich.txt");
            try
            {
                if (File.Exists(liesmich) && !SelbePfade(liesmich, Path.Combine(ProgrammOrdner, "Liesmich.txt")))
                    File.Copy(liesmich, Path.Combine(ProgrammOrdner, "Liesmich.txt"), true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Firewall

        /// <summary>Eingehende Verbindungen auf den Port erlauben (Profile Domäne und Privat), vorhandene Regel ersetzen.</summary>
        public static void FirewallSetzen(int port)
        {
            FirewallEntfernen();
            Ausfuehren("netsh", "advfirewall firewall add rule name=\"" + FirewallRegel + "\" dir=in action=allow protocol=TCP localport=" +
                port.ToString(CultureInfo.InvariantCulture) + " profile=domain,private", false);
            // Zugriff von außen: Port für den Anwendungsproxy nur für die Connector-Server
            var e = Speicher.EinstellungenLesen();
            var ips = e.proxyIps.Where(x => { System.Net.IPAddress a; return System.Net.IPAddress.TryParse(x, out a); }).ToList();
            if (e.proxyAn == true && ips.Count > 0 && e.proxyPort != port)
                Ausfuehren("netsh", "advfirewall firewall add rule name=\"" + FirewallRegelProxy + "\" dir=in action=allow protocol=TCP localport=" +
                    e.proxyPort.ToString(CultureInfo.InvariantCulture) + " remoteip=" + string.Join(",", ips.ToArray()) + " profile=domain,private", false);
        }

        public static void FirewallEntfernen()
        {
            Ausfuehren("netsh", "advfirewall firewall delete rule name=\"" + FirewallRegel + "\"", true);
            Ausfuehren("netsh", "advfirewall firewall delete rule name=\"" + FirewallRegelProxy + "\"", true);
        }

        // ------------------------------------------------------------------ Hilfen

        private static void Sc(string argumente, bool fehlerEgal = false)
        {
            Ausfuehren(Path.Combine(Environment.SystemDirectory, "sc.exe"), argumente, fehlerEgal);
        }

        private static void Ausfuehren(string programm, string argumente, bool fehlerEgal)
        {
            var psi = new ProcessStartInfo(programm, argumente)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var p = Process.Start(psi))
            {
                string aus = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                if (!fehlerEgal && p.ExitCode != 0)
                    throw new InvalidOperationException(Path.GetFileName(programm) + " " + argumente + "\r\n\r\n" + aus.Trim());
            }
        }

        private static bool SelbePfade(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}