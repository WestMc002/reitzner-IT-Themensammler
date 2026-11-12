// reitzner IT-Themensammler – Konten aus Active Directory und Microsoft 365 zusammenführen (Anwendungsproxy)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using System.Text;

namespace ITThemensammler
{
    /// <summary>Ein Benutzerkonto im AD, mit den Angaben, über die es einem Microsoft-365-Konto zugeordnet wird.</summary>
    internal sealed class AdKonto
    {
        /// <summary>"DOMÄNE\anmeldename"</summary>
        public string Anmeldung;
        public string Name;
        /// <summary>E-Mail-Adresse und UPN aus dem AD (klein geschrieben, ggf. leer)</summary>
        public string Mail;
        public string Upn;
        public System.Security.Principal.SecurityIdentifier Sid;
        /// <summary>AD-Felder "Büro" (physicalDeliveryOfficeName) und "Abteilung" (department), ggf. leer</summary>
        public string Buero = "";
        public string Abteilung = "";
    }

    /// <summary>
    /// Verbindet Domänenkonto und Microsoft-365-Konto derselben Person. AD und Microsoft 365 sind nicht synchronisiert;
    /// zugeordnet wird über die E-Mail-Adresse bzw. den UPN im AD (= Anmeldename bei Microsoft 365).
    /// Alle AD-Abfragen werden 10 Minuten zwischengespeichert, auch "nicht gefunden".
    /// </summary>
    internal static class Konten
    {
        private sealed class Eintrag<T>
        {
            public T Wert;
            public DateTime Bis;
        }

        private static readonly Dictionary<string, Eintrag<AdKonto>> NachAnmeldung = new Dictionary<string, Eintrag<AdKonto>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Eintrag<AdKonto>> NachM365 = new Dictionary<string, Eintrag<AdKonto>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Eintrag<bool>> InGruppe = new Dictionary<string, Eintrag<bool>>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Dauer = TimeSpan.FromMinutes(10);

        private static bool Holen<T>(Dictionary<string, Eintrag<T>> cache, string schluessel, out T wert)
        {
            lock (cache)
            {
                Eintrag<T> e;
                if (cache.TryGetValue(schluessel, out e) && e.Bis > DateTime.UtcNow)
                {
                    wert = e.Wert;
                    return true;
                }
            }
            wert = default(T);
            return false;
        }

        private static T Merken<T>(Dictionary<string, Eintrag<T>> cache, string schluessel, T wert)
        {
            lock (cache) cache[schluessel] = new Eintrag<T> { Wert = wert, Bis = DateTime.UtcNow + Dauer };
            return wert;
        }

        private static AdKonto Aus(UserPrincipal u)
        {
            if (u == null) return null;
            // "DOMÄNE\anmeldename" so, wie Windows ihn bei der Anmeldung meldet (NetBIOS-Name der Domäne)
            string anmeldung;
            try { anmeldung = ((System.Security.Principal.NTAccount)u.Sid.Translate(typeof(System.Security.Principal.NTAccount))).Value; }
            catch { anmeldung = (u.Context != null ? u.Context.Name : "") + "\\" + u.SamAccountName; }
            string name = !string.IsNullOrEmpty(u.DisplayName) ? u.DisplayName.Trim()
                : ((u.GivenName ?? "") + " " + (u.Surname ?? "")).Trim();
            if (name.Length == 0) name = u.SamAccountName;
            string buero = "", abteilung = "";
            try
            {
                var de = u.GetUnderlyingObject() as System.DirectoryServices.DirectoryEntry;
                if (de != null)
                {
                    buero = Convert.ToString(de.Properties["physicalDeliveryOfficeName"].Value ?? "").Trim();
                    abteilung = Convert.ToString(de.Properties["department"].Value ?? "").Trim();
                }
            }
            catch { /* Felder nicht lesbar: ohne Vorbelegung */ }
            return new AdKonto
            {
                Buero = buero,
                Abteilung = abteilung,
                Anmeldung = anmeldung,
                Name = name,
                Mail = (u.EmailAddress ?? "").Trim().ToLowerInvariant(),
                Upn = (u.UserPrincipalName ?? "").Trim().ToLowerInvariant(),
                Sid = u.Sid
            };
        }

        /// <summary>AD-Konto zu "DOMÄNE\anmeldename" (Anmeldung mit dem Windows-Konto) oder null.</summary>
        public static AdKonto AusAnmeldung(string domaeneBenutzer)
        {
            AdKonto k;
            if (Holen(NachAnmeldung, domaeneBenutzer, out k)) return k;
            int p = domaeneBenutzer.IndexOf('\\');
            string domaene = p > 0 ? domaeneBenutzer.Substring(0, p) : null;
            string sam = p >= 0 ? domaeneBenutzer.Substring(p + 1) : domaeneBenutzer;
            try
            {
                using (var pc = domaene == null ? new PrincipalContext(ContextType.Domain) : new PrincipalContext(ContextType.Domain, domaene))
                using (var u = UserPrincipal.FindByIdentity(pc, IdentityType.SamAccountName, sam))
                    k = Aus(u);
            }
            catch (Exception ex)
            {
                Speicher.Fehler("AD-Konto " + domaeneBenutzer + " nicht lesbar", ex);
                k = null;
            }
            return Merken(NachAnmeldung, domaeneBenutzer, k);
        }

        /// <summary>AD-Konto zum Microsoft-365-Anmeldenamen (UPN), gesucht über UPN und E-Mail-Adresse im AD; sonst null.</summary>
        public static AdKonto AusM365(string upn)
        {
            AdKonto k;
            if (string.IsNullOrEmpty(upn)) return null;
            if (Holen(NachM365, upn, out k)) return k;
            k = null;
            try
            {
                using (var pc = new PrincipalContext(ContextType.Domain))
                {
                    using (var u = UserPrincipal.FindByIdentity(pc, IdentityType.UserPrincipalName, upn))
                        k = Aus(u);
                    if (k == null)
                    {
                        using (var muster = new UserPrincipal(pc) { EmailAddress = upn })
                        using (var suche = new PrincipalSearcher(muster))
                        {
                            var treffer = suche.FindAll().OfType<UserPrincipal>().ToList();
                            try
                            {
                                if (treffer.Count == 1) k = Aus(treffer[0]);
                            }
                            finally
                            {
                                foreach (var t in treffer) t.Dispose();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Speicher.Fehler("AD-Konto zu " + upn + " nicht lesbar", ex);
            }
            return Merken(NachM365, upn, k);
        }

        /// <summary>Ist das AD-Konto (auch über verschachtelte Gruppen) Mitglied der Gruppe?</summary>
        public static bool IstInGruppe(AdKonto k, string gruppe)
        {
            if (k == null || k.Sid == null || string.IsNullOrEmpty(gruppe)) return false;
            string schluessel = k.Sid.Value + "|" + gruppe;
            bool drin;
            if (Holen(InGruppe, schluessel, out drin)) return drin;
            drin = false;
            try
            {
                var sid = AdAbgleich.GruppeSid(gruppe);
                if (sid != null)
                    using (var pc = new PrincipalContext(ContextType.Domain))
                    using (var u = UserPrincipal.FindByIdentity(pc, IdentityType.Sid, k.Sid.Value))
                        if (u != null)
                            foreach (var g in u.GetAuthorizationGroups())
                                using (g)
                                    if (g.Sid != null && g.Sid.Equals(sid)) drin = true;
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Gruppen von " + k.Anmeldung + " nicht lesbar", ex);
            }
            return Merken(InGruppe, schluessel, drin);
        }

        /// <summary>
        /// Alle Kennungen einer Person für "eigene Themen": "ad:domäne\name" und "m365:upn" (UPN bzw. E-Mail aus dem AD),
        /// klein geschrieben. So gehört ein Thema derselben Person, egal ob sie im Haus oder über den Anwendungsproxy eingetragen hat.
        /// </summary>
        public static List<string> Kennungen(AdKonto ad, string m365Upn)
        {
            var l = new List<string>();
            if (!string.IsNullOrEmpty(m365Upn)) l.Add("m365:" + m365Upn.ToLowerInvariant());
            if (ad != null)
            {
                l.Add("ad:" + ad.Anmeldung.ToLowerInvariant());
                if (ad.Upn.Length > 0) l.Add("m365:" + ad.Upn);
                if (ad.Mail.Length > 0) l.Add("m365:" + ad.Mail);
            }
            return l.Distinct().ToList();
        }

        /// <summary>
        /// Wert einer Kopfzeile vom Anwendungsproxy. HttpListener liest Kopfzeilen als Latin-1; kommen sie als UTF-8
        /// (Umlaute in Namen), wird das hier korrigiert.
        /// </summary>
        public static string Kopf(System.Net.HttpListenerRequest req, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string v = (req.Headers[name] ?? "").Trim();
            if (v.Length == 0) return "";
            if (v.Any(c => c >= 0x80 && c <= 0xFF))
            {
                try
                {
                    var bytes = Encoding.GetEncoding(28591).GetBytes(v);
                    string utf8 = new UTF8Encoding(false, true).GetString(bytes);
                    v = utf8;
                }
                catch (DecoderFallbackException) { /* war doch Latin-1 */ }
            }
            return v.Length > 200 ? v.Substring(0, 200) : v;
        }
    }
}