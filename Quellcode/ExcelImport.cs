// reitzner IT-Themensammler – Übernahme der bisherigen Excel-Liste (reitzner IT-Support.xlsm: Blätter "Anfragen" und "Daten")
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ITThemensammler
{
    /// <summary>
    /// Liest die bisherige Excel-Liste (.xlsx/.xlsm) direkt aus der Datei, ohne Excel:
    /// Blatt "Anfragen" ab Zeile 3 (Standort, Benutzer, Wunschtermin, Problem, Erledigt, Datum) und
    /// Blatt "Daten" ab Zeile 2 (Standorte, Benutzer, Wunschtermine). Die Themen werden zu den vorhandenen hinzugefügt;
    /// was schon vorhanden ist (gleicher Standort, Benutzer, Wunschtermin und Problem), wird übersprungen.
    /// </summary>
    internal static class ExcelImport
    {
        public sealed class Ergebnis
        {
            public int Neu, Vorhanden, Erledigt, DatumGeschaetzt, Standorte, Benutzer, Termine;
        }

        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>Eine Zelle: Text (bzw. Zahl als Text), Zahl, ob Wahrheitswert und ob Formel.</summary>
        private sealed class Zelle
        {
            public string Text = "";
            public double? Zahl;
            public bool? Wahr;
            public bool Formel;
        }

        public static Ergebnis Importieren(string datei)
        {
            Dictionary<int, Dictionary<string, Zelle>> anfragen, daten;
            using (var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var texte = GemeinsameTexte(zip);
                var blaetter = Blaetter(zip);
                string pfadAnfragen, pfadDaten;
                if (!blaetter.TryGetValue("Anfragen", out pfadAnfragen))
                    throw new InvalidDataException("Das Blatt „Anfragen“ wurde in der Excel-Datei nicht gefunden.");
                blaetter.TryGetValue("Daten", out pfadDaten);
                anfragen = Blatt(zip, pfadAnfragen, texte);
                daten = pfadDaten == null ? new Dictionary<int, Dictionary<string, Zelle>>() : Blatt(zip, pfadDaten, texte);
            }

            var erg = new Ergebnis();
            var neu = new List<Anfrage>();
            foreach (var zeile in anfragen.Where(z => z.Key >= 3).OrderBy(z => z.Key))
            {
                string standort = Einzeilig(Wert(zeile.Value, "A"));
                string benutzer = Einzeilig(Wert(zeile.Value, "B"));
                string termin = Termin(Zellwert(zeile.Value, "C"));
                string problem = Mehrzeilig(Wert(zeile.Value, "D"));
                if (standort.Length == 0 && benutzer.Length == 0 && termin.Length == 0 && problem.Length == 0) continue; // leere Vorlagezeile

                var e = Zellwert(zeile.Value, "E");
                bool erledigt = e != null && (e.Wahr == true || e.Text.Equals("WAHR", StringComparison.OrdinalIgnoreCase) || e.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase));
                string erledigtAm = "";
                if (erledigt)
                {
                    erg.Erledigt++;
                    // Fest eingetragenes Datum übernehmen. Die Formel =WENN(E=WAHR;HEUTE();"") zeigt nur den Tag des letzten Öffnens,
                    // nicht den Erledigt-Tag – dann ersatzweise den Wunschtermin verwenden.
                    var f = Zellwert(zeile.Value, "F");
                    if (f != null && !f.Formel && f.Zahl.HasValue) erledigtAm = AusZahl(f.Zahl.Value);
                    if (erledigtAm.Length == 0)
                    {
                        erg.DatumGeschaetzt++;
                        erledigtAm = termin.Length == 10 ? termin : (f != null && f.Zahl.HasValue ? AusZahl(f.Zahl.Value) : DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    }
                }
                if (termin.Length == 0) termin = erledigtAm.Length == 10 ? erledigtAm : WebServer.Ticket;

                neu.Add(new Anfrage
                {
                    id = Guid.NewGuid().ToString("N"),
                    standort = standort.Length > 0 ? standort : "(ohne Standort)",
                    benutzer = benutzer.Length > 0 ? benutzer : "(unbekannt)",
                    wunschtermin = termin,
                    problem = problem.Length > 0 ? problem : "(ohne Beschreibung)",
                    erfasst = "",
                    erledigtAm = erledigtAm,
                    erledigtVon = "",
                    notiz = ""
                });
            }

            var standorte = new List<string>();
            var benutzerListe = new List<string>();
            var termine = new List<string>();
            foreach (var zeile in daten.Where(z => z.Key >= 2).OrderBy(z => z.Key))
            {
                Hinzu(standorte, Einzeilig(Wert(zeile.Value, "A")));
                Hinzu(benutzerListe, Einzeilig(Wert(zeile.Value, "B")));
                string t = Termin(Zellwert(zeile.Value, "C"));
                if (t.Length == 10) Hinzu(termine, t);
            }
            // Standorte und Benutzer aus den Themen ergänzen, die im Blatt "Daten" fehlen
            foreach (var a in neu)
            {
                if (!a.standort.StartsWith("(", StringComparison.Ordinal)) Hinzu(standorte, a.standort);
                if (!a.benutzer.StartsWith("(", StringComparison.Ordinal)) Hinzu(benutzerListe, a.benutzer);
            }

            Speicher.Sichern("vor-Excel-Import");
            Speicher.Aendern(d =>
            {
                foreach (var a in neu)
                {
                    bool schon = d.anfragen.Any(x => Gleich(x.standort, a.standort) && Gleich(x.benutzer, a.benutzer) &&
                        x.wunschtermin == a.wunschtermin && Gleich(x.problem, a.problem));
                    if (schon) { erg.Vorhanden++; continue; }
                    d.anfragen.Add(a);
                    erg.Neu++;
                }
                foreach (string s in standorte) if (Hinzu(d.stammdaten.standorte, s)) erg.Standorte++;
                foreach (string s in benutzerListe) if (Hinzu(d.stammdaten.benutzer, s)) erg.Benutzer++;
                foreach (string s in termine) if (Hinzu(d.stammdaten.termine, s)) erg.Termine++;
                d.stammdaten.termine.Sort(StringComparer.Ordinal);
                return 0;
            });
            Speicher.Protokoll("Excel übernommen aus " + datei + ": " + erg.Neu + " neue Themen, " + erg.Vorhanden + " schon vorhanden");
            return erg;
        }

        // ------------------------------------------------------------------ Excel-Datei lesen

        private static List<string> GemeinsameTexte(ZipArchive zip)
        {
            var liste = new List<string>();
            var e = zip.GetEntry("xl/sharedStrings.xml");
            if (e == null) return liste;
            XDocument doc;
            using (var s = e.Open()) doc = XDocument.Load(s);
            foreach (var si in doc.Root.Elements(Main + "si"))
                liste.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
            return liste;
        }

        /// <summary>Blattname → Pfad im Archiv (z. B. "Anfragen" → "xl/worksheets/sheet1.xml").</summary>
        private static Dictionary<string, string> Blaetter(ZipArchive zip)
        {
            var ziele = new Dictionary<string, string>();
            var relEintrag = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relEintrag != null)
            {
                XDocument rels;
                using (var s = relEintrag.Open()) rels = XDocument.Load(s);
                foreach (var r in rels.Root.Elements(PackRel + "Relationship"))
                {
                    string ziel = (string)r.Attribute("Target") ?? "";
                    ziel = ziel.StartsWith("/", StringComparison.Ordinal) ? ziel.Substring(1) : "xl/" + ziel;
                    ziele[(string)r.Attribute("Id") ?? ""] = ziel;
                }
            }
            var blaetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wb = zip.GetEntry("xl/workbook.xml");
            if (wb == null) throw new InvalidDataException("Die Datei ist keine Excel-Arbeitsmappe (.xlsx oder .xlsm).");
            XDocument doc;
            using (var s = wb.Open()) doc = XDocument.Load(s);
            foreach (var sh in doc.Root.Descendants(Main + "sheet"))
            {
                string id = (string)sh.Attribute(Rel + "id") ?? "";
                string ziel;
                if (ziele.TryGetValue(id, out ziel)) blaetter[(string)sh.Attribute("name") ?? ""] = ziel;
            }
            return blaetter;
        }

        /// <summary>Zeilen eines Blatts: Zeilennummer → (Spaltenbuchstabe → Zelle).</summary>
        private static Dictionary<int, Dictionary<string, Zelle>> Blatt(ZipArchive zip, string pfad, List<string> texte)
        {
            var e = zip.GetEntry(pfad);
            if (e == null) throw new InvalidDataException("Blatt nicht gefunden: " + pfad);
            XDocument doc;
            using (var s = e.Open()) doc = XDocument.Load(s);
            var zeilen = new Dictionary<int, Dictionary<string, Zelle>>();
            foreach (var c in doc.Root.Descendants(Main + "c"))
            {
                var m = Regex.Match((string)c.Attribute("r") ?? "", "^([A-Z]+)([0-9]+)$");
                if (!m.Success) continue;
                int nr = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string typ = (string)c.Attribute("t") ?? "";
                var v = c.Element(Main + "v");
                string roh = v == null ? "" : v.Value;
                var z = new Zelle { Formel = c.Element(Main + "f") != null };
                if (typ == "s")
                {
                    int i;
                    if (int.TryParse(roh, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0 && i < texte.Count) z.Text = texte[i];
                }
                else if (typ == "inlineStr")
                {
                    var istr = c.Element(Main + "is");
                    z.Text = istr == null ? "" : string.Concat(istr.Descendants(Main + "t").Select(t => t.Value));
                }
                else if (typ == "b")
                {
                    z.Wahr = roh == "1";
                    z.Text = z.Wahr == true ? "WAHR" : "FALSCH";
                }
                else if (typ == "str" || typ == "e")
                {
                    z.Text = roh;
                }
                else
                {
                    z.Text = roh;
                    double d;
                    if (roh.Length > 0 && double.TryParse(roh, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) z.Zahl = d;
                }
                Dictionary<string, Zelle> zeile;
                if (!zeilen.TryGetValue(nr, out zeile)) zeilen[nr] = zeile = new Dictionary<string, Zelle>();
                zeile[m.Groups[1].Value] = z;
            }
            return zeilen;
        }

        // ------------------------------------------------------------------ Werte aufbereiten

        private static Zelle Zellwert(Dictionary<string, Zelle> zeile, string spalte)
        {
            Zelle z;
            return zeile.TryGetValue(spalte, out z) ? z : null;
        }

        private static string Wert(Dictionary<string, Zelle> zeile, string spalte)
        {
            var z = Zellwert(zeile, spalte);
            return z == null ? "" : z.Text ?? "";
        }

        /// <summary>Wunschtermin: "Dienstag, 10.03.2026", Excel-Datum oder "Schnellstmöglich/Ticket" → JJJJ-MM-TT bzw. "ticket"; sonst leer.</summary>
        private static string Termin(Zelle z)
        {
            if (z == null) return "";
            if (z.Zahl.HasValue && z.Zahl.Value > 1000 && z.Zahl.Value < 100000) return AusZahl(z.Zahl.Value);
            string t = (z.Text ?? "").Trim();
            if (t.Length == 0) return "";
            var m = Regex.Match(t, @"(\d{1,2})\.(\d{1,2})\.(\d{4})");
            if (m.Success)
            {
                DateTime d;
                string text = m.Groups[1].Value.PadLeft(2, '0') + "." + m.Groups[2].Value.PadLeft(2, '0') + "." + m.Groups[3].Value;
                if (DateTime.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (t.IndexOf("ticket", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("schnellst", StringComparison.OrdinalIgnoreCase) >= 0)
                return WebServer.Ticket;
            return "";
        }

        private static string AusZahl(double d)
        {
            try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        private static string Einzeilig(string s)
        {
            return Regex.Replace(s ?? "", @"\s+", " ").Trim();
        }

        private static string Mehrzeilig(string s)
        {
            s = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @" ?\n ?", "\n");
            return s.Trim();
        }

        private static bool Gleich(string a, string b)
        {
            return string.Equals(Einzeilig(a), Einzeilig(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Wert ergänzen, wenn noch nicht vorhanden (Groß-/Kleinschreibung egal). Liefert true, wenn ergänzt.</summary>
        private static bool Hinzu(List<string> liste, string wert)
        {
            if (string.IsNullOrEmpty(wert) || wert.Length > 100) return false;
            if (liste.Any(x => string.Equals(x, wert, StringComparison.OrdinalIgnoreCase))) return false;
            liste.Add(wert);
            return true;
        }
    }
}