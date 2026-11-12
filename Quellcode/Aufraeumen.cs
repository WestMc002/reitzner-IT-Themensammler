// reitzner IT-Themensammler – Löschfrist für erledigte Themen und Aufräumen der angehängten Bilder
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ITThemensammler
{
    /// <summary>
    /// Löscht erledigte Themen nach der eingestellten Frist (Verwaltung › Aussehen), auf Wunsch vorher ins Archiv
    /// (Archiv\Themen-JJJJ.csv im Datenordner), und entfernt Bilder, zu denen es kein Thema mehr gibt.
    /// Läuft im Dienst 3 Minuten nach dem Start und danach alle 6 Stunden.
    /// </summary>
    internal static class Aufraeumen
    {
        private static Timer _timer;
        private static int _laeuft;

        public static void Starten()
        {
            Beenden();
            _timer = new Timer(o =>
            {
                try { Ausfuehren(); }
                catch (Exception ex) { Speicher.Fehler("Aufräumen", ex); }
            }, null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(6));
        }

        public static void Beenden()
        {
            var t = _timer;
            _timer = null;
            if (t != null) t.Dispose();
        }

        /// <summary>Frist anwenden und verwaiste Bilder löschen. Liefert die Zahl der gelöschten Themen.</summary>
        public static int Ausfuehren()
        {
            if (Interlocked.Exchange(ref _laeuft, 1) == 1) return 0;
            try
            {
                var e = Speicher.EinstellungenLesen();
                int geloescht = 0;
                if (e.loeschMonate > 0)
                {
                    string grenze = DateTime.Today.AddMonths(-e.loeschMonate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    bool faellig = Speicher.Lesen(d => d.anfragen.Any(a => Abgelaufen(a, grenze)));
                    if (faellig)
                    {
                        bool archiv = e.loeschArchiv != false;
                        var weg = Speicher.Aendern(d =>
                        {
                            var liste = d.anfragen.Where(a => Abgelaufen(a, grenze)).ToList();
                            // erst ins Archiv schreiben; scheitert das, wird nichts gelöscht
                            if (archiv && liste.Count > 0) Archivieren(liste);
                            d.anfragen.RemoveAll(a => Abgelaufen(a, grenze));
                            return liste;
                        });
                        foreach (var a in weg) Speicher.AnhangLoeschen(a.id);
                        geloescht = weg.Count;
                        if (geloescht > 0)
                            Speicher.Protokoll("Löschfrist (" + e.loeschMonate + " Monate): " + geloescht + " erledigte Themen " +
                                (archiv ? "ins Archiv verschoben" : "gelöscht"));
                    }
                }
                VerwaisteBilder();
                return geloescht;
            }
            finally
            {
                Interlocked.Exchange(ref _laeuft, 0);
            }
        }

        private static bool Abgelaufen(Anfrage a, string grenze)
        {
            return a.erledigtAm.Length == 10 && string.CompareOrdinal(a.erledigtAm, grenze) < 0;
        }

        /// <summary>Themen an Archiv\Themen-JJJJ.csv anhängen (Jahr der Erledigung; Semikolon, UTF-8 mit BOM – öffnet Excel direkt).</summary>
        private static void Archivieren(List<Anfrage> liste)
        {
            Directory.CreateDirectory(Speicher.ArchivOrdner);
            foreach (var jahr in liste.GroupBy(a => a.erledigtAm.Substring(0, 4)))
            {
                string datei = Path.Combine(Speicher.ArchivOrdner, "Themen-" + jahr.Key + ".csv");
                var sb = new StringBuilder();
                if (!File.Exists(datei))
                    sb.Append("Erledigt am;Erledigt von;Standort;Benutzer;Wunschtermin;Erfasst;Problem;Was wurde gemacht\r\n");
                foreach (var a in jahr.OrderBy(x => x.erledigtAm, StringComparer.Ordinal))
                    sb.Append(string.Join(";", new[] { a.erledigtAm, a.erledigtVon, a.standort, a.benutzer, a.wunschtermin, a.erfasst, a.problem, a.notiz }
                        .Select(x => Feld(x)).ToArray())).Append("\r\n");
                bool neu = !File.Exists(datei);
                using (var w = new StreamWriter(datei, true, new UTF8Encoding(neu)))
                    w.Write(sb.ToString());
            }
        }

        private static string Feld(string v)
        {
            v = v ?? "";
            // Zeichen am Anfang, die Excel als Formel ausführen würde, entschärfen
            if (v.Length > 0 && "=+-@\t\r".IndexOf(v[0]) >= 0) v = "'" + v;
            return v.IndexOfAny(new[] { '"', ';', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        /// <summary>Bilder ohne Thema löschen (Thema gelöscht, Daten neu eingespielt …); frische Dateien bleiben (Upload läuft evtl. noch).</summary>
        private static void VerwaisteBilder()
        {
            try
            {
                if (!Directory.Exists(Speicher.AnhangOrdner)) return;
                var ids = Speicher.Lesen(d => new HashSet<string>(d.anfragen.Where(a => a.bild.Length > 0).Select(a => a.id)));
                foreach (var f in new DirectoryInfo(Speicher.AnhangOrdner).GetFiles())
                {
                    if (f.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
                    if (f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && ids.Contains(Path.GetFileNameWithoutExtension(f.Name))) continue;
                    f.Delete();
                }
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Verwaiste Bilder löschen", ex);
            }
        }
    }
}