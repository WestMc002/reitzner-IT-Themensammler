// reitzner IT-Themensammler – Benutzerliste mit dem Active Directory abgleichen (OUs, täglich automatisch)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ITThemensammler
{
    /// <summary>
    /// Liest aktive Benutzer aus den gewählten OUs (samt Unter-OUs) und gleicht damit die Benutzerliste ab:
    /// neue kommen dazu, Benutzer aus einem früheren Abgleich, die im AD fehlen oder deaktiviert sind, fallen weg.
    /// Von Hand eingetragene Benutzer bleiben. Der Dienst (LocalSystem auf einem Domänenmitglied) liest das AD
    /// mit dem Computerkonto, ein eigenes Dienstkonto ist nicht nötig.
    /// </summary>
    internal static class AdAbgleich
    {
        /// <summary>Aktive Benutzerkonten (keine deaktivierten, keine Computer).</summary>
        private const string BenutzerFilter =
            "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

        private static Timer _timer;
        private static int _laeuft;

        /// <summary>Stündlich prüfen, ob der tägliche Abgleich fällig ist (erster Blick 2 Minuten nach dem Start).</summary>
        public static void Starten()
        {
            Beenden();
            _timer = new Timer(Pruefen, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        }

        public static void Beenden()
        {
            var t = _timer;
            _timer = null;
            if (t != null) t.Dispose();
        }

        private static void Pruefen(object zustand)
        {
            try
            {
                var e = Speicher.EinstellungenLesen();
                if (e.adAbgleich != true || e.adOus.Count == 0) return;
                DateTime letzter;
                if (DateTime.TryParse(e.adLetzter, CultureInfo.InvariantCulture, DateTimeStyles.None, out letzter)
                    && DateTime.Now - letzter < TimeSpan.FromHours(23)) return;
                Abgleichen("automatisch");
            }
            catch
            {
                // steht bereits in Fehler.log und in der Ereignisanzeige; nächster Versuch in einer Stunde
            }
        }

        /// <summary>Abgleich ausführen. Gibt das Ergebnis als Text zurück; Fehler werden gespeichert und weitergereicht.</summary>
        public static string Abgleichen(string anlass)
        {
            if (Interlocked.Exchange(ref _laeuft, 1) == 1) throw new InvalidOperationException("Der Abgleich läuft gerade.");
            try
            {
                var e = Speicher.EinstellungenLesen();
                if (e.adOus.Count == 0) throw new InvalidOperationException("Bitte zuerst mindestens eine OU auswählen.");
                List<string> namen;
                try
                {
                    namen = BenutzerLesen(e);
                    // Schutz: eine leere Antwort (OU umbenannt, AD nicht erreichbar …) leert die Liste nicht
                    if (namen.Count == 0) throw new InvalidOperationException("In den gewählten OUs wurden keine aktiven Benutzer gefunden. Die Liste bleibt unverändert.");
                }
                catch (Exception ex)
                {
                    string fehler = "Fehler: " + ex.Message;
                    Speicher.EinstellungenAendern(x => x.adErgebnis = fehler);
                    Speicher.Fehler("AD-Abgleich (" + anlass + ")", ex);
                    Ereignis.Schreiben(Ereignis.AdFehler, EventLogEntryType.Warning, "Der Abgleich der Benutzer mit dem Active Directory ist fehlgeschlagen: " + ex.Message);
                    throw;
                }

                var vergleich = StringComparer.Create(new CultureInfo("de-DE"), true);
                int neu = 0, weg = 0;
                Speicher.Aendern(d =>
                {
                    var sd = d.stammdaten;
                    var imAd = new HashSet<string>(namen);
                    foreach (string alt in sd.adBenutzer.Where(x => !imAd.Contains(x)).ToList())
                        if (sd.benutzer.Remove(alt)) weg++;
                    foreach (string n in namen)
                    {
                        if (sd.benutzer.Contains(n) || sd.adAusgeschlossen.Contains(n)) continue;
                        sd.benutzer.Add(n);
                        neu++;
                    }
                    sd.adAusgeschlossen.RemoveAll(x => !imAd.Contains(x));
                    sd.adBenutzer = namen.ToList();
                    sd.benutzer.Sort(vergleich);
                    return 0;
                });
                string ergebnis = neu + " neu, " + weg + " entfernt (" + namen.Count + " aktive Benutzer im AD)";
                Speicher.EinstellungenAendern(x =>
                {
                    x.adLetzter = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
                    x.adErgebnis = ergebnis;
                });
                Speicher.Protokoll("AD-Abgleich (" + anlass + "): " + ergebnis);
                return ergebnis;
            }
            finally
            {
                Interlocked.Exchange(ref _laeuft, 0);
            }
        }

        /// <summary>
        /// Anzeigenamen der aktiven Benutzer in den OUs (mit Unter-OUs), sortiert, ohne Doppelte. Konten ohne Namen
        /// (meist Dienst- oder Funktionskonten) nur, wenn eingeschaltet – dann mit dem Anmeldenamen. Konten, deren Name
        /// oder Anmeldename einen der Ausschluss-Texte enthält, werden übersprungen.
        /// </summary>
        private static List<string> BenutzerLesen(Einstellungen e)
        {
            var namen = new HashSet<string>();
            var ausschluss = e.adAusschluss.Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (string dn in e.adOus)
            {
                using (var wurzel = new DirectoryEntry("LDAP://" + dn.Replace("/", "\\/")))
                using (var suche = new DirectorySearcher(wurzel, BenutzerFilter, new[] { "displayName", "givenName", "sn", "sAMAccountName" }, SearchScope.Subtree))
                {
                    suche.PageSize = 500;
                    using (var treffer = suche.FindAll())
                    {
                        foreach (SearchResult r in treffer)
                        {
                            string sam = Wert(r, "sAMAccountName");
                            string name = Wert(r, "displayName");
                            if (name.Length == 0) name = (Wert(r, "givenName") + " " + Wert(r, "sn")).Trim();
                            if (name.Length == 0 && e.adOhneNamen == true) name = sam;
                            if (name.Length == 0 || name.Length > 100) continue;
                            if (ausschluss.Any(x => name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || sam.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            namen.Add(name);
                        }
                    }
                }
            }
            var liste = namen.ToList();
            liste.Sort(StringComparer.Create(new CultureInfo("de-DE"), true));
            return liste;
        }

        private static string Wert(SearchResult r, string feld)
        {
            var p = r.Properties[feld];
            return p != null && p.Count > 0 && p[0] != null ? Convert.ToString(p[0], CultureInfo.InvariantCulture).Trim() : "";
        }

        /// <summary>Alle OUs der Domäne (und den Standard-Container "Users") mit lesbarem Pfad, für die Auswahl in der Verwaltung.</summary>
        public static List<object> OuListe()
        {
            string basis;
            using (var rootDse = new DirectoryEntry("LDAP://RootDSE"))
                basis = Convert.ToString(rootDse.Properties["defaultNamingContext"].Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(basis)) throw new InvalidOperationException("Keine Domäne gefunden. Ist der Server Mitglied der Domäne?");

            var dns = new List<string>();
            using (var wurzel = new DirectoryEntry("LDAP://" + basis))
            using (var suche = new DirectorySearcher(wurzel, "(objectCategory=organizationalUnit)", new[] { "distinguishedName" }, SearchScope.Subtree))
            {
                suche.PageSize = 500;
                using (var treffer = suche.FindAll())
                    foreach (SearchResult r in treffer)
                    {
                        string dn = Wert(r, "distinguishedName");
                        if (dn.Length > 0) dns.Add(dn);
                    }
            }
            dns.Add("CN=Users," + basis);

            return dns
                .Select(dn => new { dn = dn, pfad = Pfad(dn) })
                .OrderBy(x => x.pfad, StringComparer.Create(new CultureInfo("de-DE"), true))
                .Cast<object>()
                .ToList();
        }

        /// <summary>"OU=Mitarbeiter,OU=Rathaus,DC=gemeinde,DC=local" → "gemeinde.local › Rathaus › Mitarbeiter"</summary>
        public static string Pfad(string dn)
        {
            var teile = Regex.Split(dn, @"(?<!\\),");
            var domaene = new List<string>();
            var ous = new List<string>();
            foreach (string t in teile)
            {
                int p = t.IndexOf('=');
                if (p < 0) continue;
                string art = t.Substring(0, p).Trim().ToUpperInvariant();
                string wert = Regex.Replace(t.Substring(p + 1).Trim(), @"\\(.)", "$1");
                if (art == "DC") domaene.Add(wert);
                else ous.Add(wert);
            }
            ous.Reverse();
            ous.Insert(0, string.Join(".", domaene.ToArray()));
            return string.Join(" › ", ous.ToArray());
        }

        /// <summary>SID einer AD-Gruppe (Name wie "IT-Themensammler-Admins" oder "DOMÄNE\Gruppe"); null, wenn es sie nicht gibt.</summary>
        public static System.Security.Principal.SecurityIdentifier GruppeSid(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                // ohne Domäne davor: im AD suchen; mit Domäne: Windows auflösen lassen
                if (name.IndexOf('\\') < 0)
                {
                    using (var pc = new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Domain))
                    using (var g = System.DirectoryServices.AccountManagement.GroupPrincipal.FindByIdentity(pc, name))
                        return g != null ? g.Sid : null;
                }
                return (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(name).Translate(typeof(System.Security.Principal.SecurityIdentifier));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Plausibler Distinguished Name einer OU bzw. eines Containers?</summary>
        public static bool IstDn(string dn)
        {
            return dn != null && dn.Length <= 500 && Regex.IsMatch(dn, @"^(OU|CN)=.+,DC=[^,]+(,DC=[^,]+)*$", RegexOptions.IgnoreCase);
        }
    }
}