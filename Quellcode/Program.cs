// reitzner IT-Themensammler – Programmstart: Verwaltungsfenster bzw. Windows-Dienst
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(ITThemensammler.AppInfo.Name)]
[assembly: AssemblyProduct(ITThemensammler.AppInfo.Name)]
// Version (Haupt.Neben.Jahr.MonatTag) erzeugt Erstellen.bat in VersionInfo.cs (AppInfo.Version)
[assembly: AssemblyDescription("Themen der Mitarbeiter für den nächsten Vor-Ort-Termin sammeln")]
[assembly: AssemblyCopyright("© 2026 Günther Schoder")]

namespace ITThemensammler
{
    public static partial class AppInfo
    {
        public const string Name = "reitzner IT-Themensammler";
        /// <summary>Angabe "Entwickler" im Info-Fenster und in der Weboberfläche (reitzner-Projekt: ohne Name und Website des Entwicklers).</summary>
        public const string Company = "reitzner AG";
    }

    internal static class Program
    {
        private static Mutex _instance;

        [STAThread]
        private static int Main(string[] args)
        {
            // Als Windows-Dienst gestartet (vom Dienststeuerungs-Manager, eingerichtet über das Verwaltungsfenster)
            if (args.Length > 0 && string.Equals(args[0], Dienst.Argument, StringComparison.OrdinalIgnoreCase))
            {
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Speicher.Fehler("Unerwarteter Fehler im Dienst", e.ExceptionObject as Exception);
                ServiceBase.Run(new Dienst());
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Screenshots für GitHub aus Beispieldaten (Erstellen.bat fragt danach); echte Daten und Dienst bleiben unberührt
            if (args.Length > 0 && string.Equals(args[0], "--screenshots", StringComparison.OrdinalIgnoreCase))
                return Demo.RunScreenshots(args.Length > 1 ? args[1] : Path.Combine(Application.StartupPath, "Screenshots")) ? 0 : 1;

            // Neustart durch das Update-Skript: wie ein normaler Start, das Fenster meldet das Ergebnis
            if (args.Length > 0 && string.Equals(args[0], "--nach-update", StringComparison.OrdinalIgnoreCase)) MainForm.AfterUpdate = true;

            // Nur ein Verwaltungsfenster
            bool first;
            _instance = new Mutex(true, "Local\\ITThemensammler.Fenster", out first);
            if (!first && MainForm.AfterUpdate)
            {
                // Neustart nach dem Update: kurz warten, bis die alte Instanz ganz beendet ist
                try { first = _instance.WaitOne(15000); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                MessageBox.Show("Das Verwaltungsfenster ist bereits geöffnet.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            try
            {
                Speicher.OrdnerAnlegen();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Der Datenordner konnte nicht angelegt werden:\r\n" + Speicher.Ordner + "\r\n\r\n" + ex.Message +
                    "\r\n\r\nBitte das Programm als Administrator starten.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Application.Run(new MainForm());
            GC.KeepAlive(_instance);
            return 0;
        }

        /// <summary>Unerwarteter Fehler: Details nach Fehler.log im Datenordner schreiben und kurz melden.</summary>
        private static void ShowCrash(Exception ex)
        {
            Speicher.Fehler("Unerwarteter Fehler im Verwaltungsfenster", ex);
            try
            {
                MessageBox.Show("Unerwarteter Fehler: " + (ex == null ? "unbekannt" : ex.Message) + "\r\n\r\nEinzelheiten stehen in\r\n" +
                    Path.Combine(Speicher.Ordner, "Fehler.log"), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}