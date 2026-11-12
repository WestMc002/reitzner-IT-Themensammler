/*
 * reitzner IT-Themensammler - Weboberfläche
 * Themen eintragen und ansehen: alle. Eigene offene Themen bearbeiten und löschen: Ersteller (angemeldetes Domänenkonto
 * oder Besitzer-Schlüssel im Browser).
 * Alle Themen bearbeiten, erledigen, löschen und Verwaltung: angemeldeter Admin.
 */
"use strict";

/* Nur für die Screenshots (README): "?farbe=hell" bzw. "?farbe=dunkel" legt Hell/Dunkel fest.
 * Ohne den Zusatz folgt die Seite wie immer der Windows-Einstellung. */
(function () {
  var f = (/[?&]farbe=(hell|dunkel)(&|$)/.exec(location.search) || [])[1];
  if (!f) return;
  var dunkel = f === "dunkel", dunkelMedia = /prefers-color-scheme:\s*dark/;
  try {
    Array.prototype.forEach.call(document.styleSheets, function (ss) {
      for (var i = ss.cssRules.length - 1; i >= 0; i--) {
        var r = ss.cssRules[i];
        if (!r.media || !dunkelMedia.test(r.media.mediaText)) continue;
        var innen = Array.prototype.map.call(r.cssRules, function (x) { return x.cssText; });
        ss.deleteRule(i);
        if (dunkel) innen.forEach(function (t, k) { ss.insertRule(t, i + k); });
      }
    });
  } catch (e) { /* Stylesheet nicht lesbar: dann eben wie Windows */ }
  document.querySelectorAll("source[media]").forEach(function (q) {
    if (dunkelMedia.test(q.media)) q.media = dunkel ? "all" : "not all";
  });
})();

var TICKET = "ticket";
var TICKET_TEXT = "Schnellstmöglich/Ticket";
var TAGE = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];

function $(id) { return document.getElementById(id); }

var stand = {
  daten: { stammdaten: { standorte: [], benutzer: [], termine: [] }, anfragen: [] },
  aussehen: {}, admin: null, programm: {}
};
var filterStatus = "offen";
var aktuelleId = null;
var loeschenBereit = false;
var seite = "themen";
var STATUS_TEXT = { arbeit: "In Arbeit", rueckfrage: "Rückfrage", beantwortet: "Rückfrage beantwortet" };

/* ---------- Server ---------- */

async function api(pfad, methode, body, typ, schluessel) {
  var opt = { method: methode || "GET", headers: {}, credentials: "same-origin", cache: "no-store" };
  if (opt.method !== "GET") opt.headers["X-Themensammler"] = "1";
  if (schluessel) opt.headers["X-Themen-Schluessel"] = schluessel;
  if (body !== undefined) {
    if (typ) { opt.headers["Content-Type"] = typ; opt.body = body; }
    else { opt.headers["Content-Type"] = "application/json"; opt.body = JSON.stringify(body); }
  }
  var a;
  try {
    a = await fetch(pfad, opt);
  } catch (e) {
    warnung("Keine Verbindung zum Server. Änderungen werden gerade nicht gespeichert.");
    throw new Error("Keine Verbindung zum Server. Bitte später erneut versuchen.");
  }
  warnung("");
  var json = null;
  try { json = await a.json(); } catch (e) { json = null; }
  if (a.status === 401) {
    stand.admin = null;
    anzeigen();
    throw new Error((json && json.fehler) || "Bitte als Admin anmelden.");
  }
  if (!a.ok) {
    var f = new Error((json && json.fehler) || "Speichern fehlgeschlagen.");
    f.status = a.status;
    throw f;
  }
  return json;
}

// Der Server antwortet mit dem neuen Gesamtstand: übernehmen und neu zeichnen.
async function senden(pfad, methode, body, typ, schluessel) {
  stand = await api(pfad, methode, body, typ, schluessel);
  eigeneAufraeumen();
  anzeigen();
}

/* ---------- Eigene Themen ----------
 * Beim Eintragen schickt der Server einmalig einen Besitzer-Schlüssel. Er bleibt nur in diesem Browser
 * und erlaubt, das eigene Thema zu bearbeiten oder zu löschen, solange es offen ist. */

function eigene() {
  try { return JSON.parse(localStorage.getItem("themen.eigene") || "{}") || {}; } catch (e) { return {}; }
}
function eigeneSpeichern(o) { try { localStorage.setItem("themen.eigene", JSON.stringify(o)); } catch (e) { } }
function schluesselVon(id) { return eigene()[id] || ""; }
function istEigen(a) { return !a.erledigtAm && (a.eigen || !!schluesselVon(a.id)); }
function eigeneAufraeumen() {
  // Schlüssel zu gelöschten oder erledigten Themen werden nicht mehr gebraucht
  var o = eigene(), offen = {}, geaendert = false;
  daten().anfragen.forEach(function (a) { if (!a.erledigtAm) offen[a.id] = true; });
  Object.keys(o).forEach(function (id) { if (!offen[id]) { delete o[id]; geaendert = true; } });
  if (geaendert) eigeneSpeichern(o);
}

/* ---------- Anmeldung mit dem Domänenkonto ----------
 * Der Browser meldet sich unter /api/domaene per Windows-Anmeldung (Kerberos/NTLM) an, auf Arbeitsplätzen in der Domäne
 * ohne Kennwortabfrage. Klappt das nicht (Rechner ohne Domäne), wird es bei "wenn möglich" erst nach 12 Stunden erneut versucht. */

var domaeneVersuch = 0;

var domaeneFehler = "";      // Grund der letzten gescheiterten Domänen-Anmeldung
var domaeneAbgemeldet = false; // nach "Abmelden" bis zum Neuladen nicht still wieder anmelden

async function domaeneAnmelden() {
  var a;
  domaeneFehler = "";
  try {
    a = await fetch("/api/domaene", { method: "POST", headers: { "X-Themensammler": "1" }, credentials: "same-origin", cache: "no-store" });
  } catch (e) {
    domaeneFehler = "Der Server ist nicht erreichbar.";
    return false;
  }
  var json = null;
  try { json = await a.json(); } catch (e) { json = null; }
  if (!a.ok || !json || !json.daten) {
    domaeneFehler = (json && json.fehler) || "Die Anmeldung mit dem Windows-Konto hat nicht geklappt.";
    return false;
  }
  domaeneAbgemeldet = false;
  stand = json;
  eigeneAufraeumen();
  if (stand.mitarbeiter && !trimmen($("n-benutzer").value)) $("n-benutzer").value = stand.mitarbeiter.name;
  anzeigen();
  return true;
}

async function domaeneAutomatisch() {
  var art = (stand.aussehen || {}).mitarbeiterAnmeldung || "aus";
  if (art === "aus" || stand.mitarbeiter || stand.admin || domaeneAbgemeldet) return;
  if (Date.now() - domaeneVersuch < 10 * 60000) return; // höchstens alle 10 Minuten
  if (art === "domaene" && Date.now() - Number(gemerkt("domaeneFehl") || 0) < 12 * 3600000) return;
  domaeneVersuch = Date.now();
  if (await domaeneAnmelden()) merken("domaeneFehl", "");
  else if (art === "domaene") merken("domaeneFehl", String(Date.now()));
}

async function neuLaden() {
  try { await senden("/api/daten"); } catch (e) { /* Warnung ist schon gesetzt */ }
}

function merken(k, v) { try { localStorage.setItem("themen." + k, v); } catch (e) { } }
function gemerkt(k) { try { return localStorage.getItem("themen." + k) || ""; } catch (e) { return ""; } }

function warnung(text) {
  $("verbindung").textContent = text;
  $("verbindung").hidden = !text;
}

/* ---------- Hilfen ---------- */

function zweistellig(n) { return (n < 10 ? "0" : "") + n; }
function isoDatum(d) { return d.getFullYear() + "-" + zweistellig(d.getMonth() + 1) + "-" + zweistellig(d.getDate()); }
function heute() { return isoDatum(new Date()); }
function datumLang(iso) {
  var p = iso.split("-");
  var d = new Date(+p[0], +p[1] - 1, +p[2]);
  return TAGE[d.getDay()] + ", " + p[2] + "." + p[1] + "." + p[0];
}
function datumKurz(iso) {
  if (!iso) return "";
  var p = iso.substring(0, 10).split("-");
  return p[2] + "." + p[1] + "." + p[0];
}
function terminText(w) { return w === TICKET ? TICKET_TEXT : /^\d{4}-\d{2}-\d{2}$/.test(w || "") ? datumLang(w) : (w || ""); }
function datumEingabe(text) {
  var m = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(text.trim());
  if (!m) return "";
  var d = new Date(+m[3], +m[2] - 1, +m[1]);
  if (d.getMonth() !== +m[2] - 1 || d.getDate() !== +m[1]) return "";
  return isoDatum(d);
}
function enthaelt(liste, w) { return liste.indexOf(w) !== -1; }
function eindeutig(liste) { return liste.filter(function (w, i) { return liste.indexOf(w) === i; }); }
function nachName(a, b) { return a.localeCompare(b, "de"); }

/* Reihenfolge der Standorte innerhalb einer Gruppe: alphabetisch (nach dem angezeigten Namen) oder – nach Drag and Drop
 * in der Verwaltung – wie in der Liste der Stammdaten. anzeige(s) = angezeigter Name. */
function standortReihe(liste, anzeige) {
  var sd = daten().stammdaten;
  if (sd.eigeneReihenfolge) return liste.slice().sort(function (a, b) { return sd.standorte.indexOf(a) - sd.standorte.indexOf(b); });
  return liste.slice().sort(function (a, b) { return anzeige(a).localeCompare(anzeige(b), "de"); });
}
function sortiereTermin(a, b) {
  if (a === TICKET) return b === TICKET ? 0 : -1;
  if (b === TICKET) return 1;
  return a < b ? -1 : a > b ? 1 : 0;
}
function leeren(el) { while (el.firstChild) el.removeChild(el.firstChild); }
function neuesEl(tag, klasse, text) {
  var e = document.createElement(tag);
  if (klasse) e.className = klasse;
  if (text !== undefined) e.textContent = text;
  return e;
}
function trimmen(s) { return (s || "").replace(/^\s+|\s+$/g, ""); }
function pruefeEintrag(e) {
  e.benutzer = trimmen(e.benutzer);
  e.problem = trimmen(e.problem);
  if (!e.standort) throw new Error("Bitte einen Standort wählen.");
  if (!e.benutzer) throw new Error("Bitte den Benutzer angeben.");
  if (!e.wunschtermin) throw new Error("Bitte einen Wunschtermin wählen.");
  if (!e.problem) throw new Error("Bitte das Problem beschreiben.");
  return e;
}
function setzeMeldung(el, text, fehler) {
  el.textContent = text;
  el.classList.toggle("fehler", !!fehler);
}
function daten() { return stand.daten; }

/* ---------- Anzeige ---------- */

function optionen(select, werte, wert) {
  leeren(select);
  werte.forEach(function (w) {
    var o = document.createElement("option");
    o.value = w[0];
    o.textContent = w[1];
    select.appendChild(o);
  });
  if (wert !== undefined && wert !== "" && werte.some(function (w) { return w[0] === wert; })) select.value = wert;
}
function terminOptionen(aktuell) {
  var h = heute(), liste = [], t = daten().stammdaten.termine;
  t.forEach(function (x) { if (x >= h || x === aktuell) liste.push([x, datumLang(x)]); });
  if (aktuell && aktuell !== TICKET && !enthaelt(t, aktuell)) liste.unshift([aktuell, datumLang(aktuell)]);
  liste.push([TICKET, TICKET_TEXT]);
  return liste;
}
/* ---------- Standorte gruppieren (nach Ort oder Art, je Kunde einstellbar) ---------- */

var STANDARD_ARTEN = ["Verwaltung", "Bauhöfe", "Feuerwehren", "Schulen und KiTas", "Kläranlagen und Wasser", "Gemeinschaftshäuser"];

// Art aus dem Namen ableiten (Vorschlag; gilt nur, wenn es die Gruppe gibt)
function artVorschlag(name) {
  var n = name.toLowerCase();
  if (/feuerwehr|ffw/.test(n)) return "Feuerwehren";
  if (/bauhof|wertstoff|recycling/.test(n)) return "Bauhöfe";
  if (/kl(ä|ae)ranlage|wasser|pumpwerk|abwasser/.test(n)) return "Kläranlagen und Wasser";
  if (/schule|kita|kinderg|krippe|hort|mittagsbetreuung/.test(n)) return "Schulen und KiTas";
  if (/rathaus|verwaltung|amt\b|ewo|kasse|k(ä|ae)mmerei|standesamt|b(ü|ue)rgerb(ü|ue)ro|rente|bauamt|vg\b/.test(n)) return "Verwaltung";
  if (/gemeindezentrum|gemeinschaftshaus|b(ü|ue)rgerhaus|halle|b(ü|ue)cherei|vereinsheim|dorfgemeinschaft/.test(n)) return "Gemeinschaftshäuser";
  return "";
}

// Orte automatisch erkennen (nur solange keine Ort-Gruppen festgelegt sind):
// was nach "Freiwillige Feuerwehr" steht, und Endungen, die bei mehreren Standorten vorkommen
function erkannteOrte() {
  var zaehler = {}, orte = [];
  daten().stammdaten.standorte.forEach(function (s) {
    var m = /^(freiwillige\s+)?feuerwehr\s+(.+)$/i.exec(s.trim());
    if (m) orte.push(m[2].trim());
    var w = s.trim().split(/\s+/);
    if (w.length > 1) { var l = w[w.length - 1]; zaehler[l] = (zaehler[l] || 0) + 1; }
  });
  Object.keys(zaehler).forEach(function (k) { if (zaehler[k] > 1 && /^[A-ZÄÖÜ]/.test(k)) orte.push(k); });
  return eindeutig(orte).sort(nachName);
}

// Aktuelle Gruppen: festgelegte (Verwaltung › Gruppen) oder, solange keine festgelegt sind, die erkannten bzw. Standardliste
function gruppen() {
  var sd = daten().stammdaten;
  var g = {
    sammelOrt: sd.sammelOrt || "Allgemein",
    sammelArt: sd.sammelArt || "Sonstige",
    orte: (sd.ortGruppen || []).length ? sd.ortGruppen.slice() : erkannteOrte(),
    arten: (sd.artGruppen || []).length ? sd.artGruppen.slice() : STANDARD_ARTEN.slice(),
    orteFest: !!(sd.ortGruppen || []).length,
    artenFest: !!(sd.artGruppen || []).length
  };
  g.orte = g.orte.filter(function (o) { return o !== g.sammelOrt; });
  g.arten = g.arten.filter(function (o) { return o !== g.sammelArt; });
  return g;
}

function ortVorschlag(name, g) {
  var liste = g.orte.slice().sort(function (a, b) { return b.length - a.length; }); // längere zuerst ("Ober-Beispielhausen" vor "Beispielhausen")
  for (var i = 0; i < liste.length; i++) {
    var k = liste[i].replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    if (new RegExp("(^|[\\s/(-])" + k + "($|[\\s/)-])", "i").test(name)) return liste[i];
  }
  return g.sammelOrt;
}
function artVorschlagIn(name, g) {
  var v = artVorschlag(name);
  return enthaelt(g.arten, v) ? v : g.sammelArt;
}
function ortVon(name, g) {
  g = g || gruppen();
  var fest = (daten().stammdaten.orte || {})[name];
  return fest && (fest === g.sammelOrt || enthaelt(g.orte, fest)) ? fest : ortVorschlag(name, g);
}
function artVon(name, g) {
  g = g || gruppen();
  var fest = (daten().stammdaten.arten || {})[name];
  return fest && (fest === g.sammelArt || enthaelt(g.arten, fest)) ? fest : artVorschlagIn(name, g);
}

// Anzeigename in der Ort-Gruppe ohne den Ort ("Bauhof Musterstadt" unter "Musterstadt" → "Bauhof")
function kurzName(name, ort, g) {
  if (ort === g.sammelOrt) return name;
  var k = ort.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  var kurz = name.replace(new RegExp("(^|\\s)" + k + "(?=$|\\s)", "i"), " ").replace(/\s+/g, " ").trim();
  return kurz.length ? kurz : name;
}

// Häufigste Standorte (über alle Themen)
function haeufigsteStandorte(anzahl) {
  var z = {};
  daten().anfragen.forEach(function (a) { if (a.standort) z[a.standort] = (z[a.standort] || 0) + 1; });
  return Object.keys(z).sort(function (a, b) { return z[b] - z[a] || a.localeCompare(b, "de"); }).slice(0, anzahl);
}

// Auswahl "Standort" füllen: oben "Zuletzt verwendet", darunter nach Ort/Art gruppiert (Einstellung im Admin-Bereich)
function standortAuswahl(select, wert, mitZuletzt) {
  var liste = daten().stammdaten.standorte.slice();
  if (wert && !enthaelt(liste, wert)) liste.push(wert);
  var art = (stand.aussehen || {}).gruppierung || "ort";
  leeren(select);
  var option = function (ziel, value, text) {
    var o = document.createElement("option");
    o.value = value;
    o.textContent = text;
    if (text !== value) o.title = value;
    ziel.appendChild(o);
  };
  if (mitZuletzt) {
    var oben = [];
    var letzter = letzterStandort($("n-benutzer").value);
    if (letzter && enthaelt(liste, letzter)) oben.push(letzter);
    haeufigsteStandorte(3).forEach(function (s) { if (enthaelt(liste, s) && !enthaelt(oben, s)) oben.push(s); });
    if (oben.length && liste.length > 8) {
      var g0 = document.createElement("optgroup");
      g0.label = "Zuletzt verwendet";
      oben.forEach(function (s) { option(g0, s, s); });
      select.appendChild(g0);
    }
  }
  if (art === "keine") {
    standortReihe(liste, function (s) { return s; }).forEach(function (s) { option(select, s, s); });
  } else {
    var gs = gruppen(), inhalt = {};
    liste.forEach(function (s) {
      var g = art === "art" ? artVon(s, gs) : ortVon(s, gs);
      (inhalt[g] = inhalt[g] || []).push(s);
    });
    // Reihenfolge wie unter Verwaltung › Gruppen: Sammelgruppe "Allgemein" oben, "Sonstige" unten
    var reihe = art === "art" ? gs.arten.concat([gs.sammelArt]) : [gs.sammelOrt].concat(gs.orte);
    reihe.filter(function (g) { return inhalt[g]; }).forEach(function (g) {
      var og = document.createElement("optgroup");
      og.label = g;
      var name = function (s) { return art === "ort" ? kurzName(s, g, gs) : s; };
      standortReihe(inhalt[g], name).forEach(function (s) { option(og, s, name(s)); });
      select.appendChild(og);
    });
  }
  if (wert && enthaelt(liste, wert)) select.value = wert;
}

function standortOptionen(aktuell) {
  var l = daten().stammdaten.standorte.slice();
  if (aktuell && !enthaelt(l, aktuell)) l.unshift(aktuell);
  return l.map(function (s) { return [s, s]; });
}
function feldListe(name) { return daten().anfragen.map(function (a) { return a[name]; }); }

function kopfZeichnen() {
  var a = stand.aussehen || {}, p = stand.programm || {};
  $("titel").textContent = a.titel || "IT-Themensammlung";
  $("untertitel").textContent = a.untertitel || "";
  document.title = (a.titel || "IT-Themensammlung") + (a.kunde ? " · " + a.kunde : "");
  var v = encodeURIComponent(a.logoStand || "");
  var mitLogo = !!(a.logoHell || a.logoDunkel);
  $("kunde").hidden = !mitLogo && !a.kunde;
  $("kunde-bild").hidden = !mitLogo;
  $("kunde-name").textContent = a.kunde || "";
  $("kunde").classList.toggle("nur-name", !mitLogo);
  if (mitLogo) {
    var hell = "/logo/kunde-hell.png?v=" + v, dunkel = "/logo/kunde-dunkel.png?v=" + v;
    if ($("kunde-hell").getAttribute("src") !== hell) $("kunde-hell").setAttribute("src", hell);
    if ($("kunde-dunkel").getAttribute("srcset") !== dunkel) $("kunde-dunkel").setAttribute("srcset", dunkel);
    $("kunde-hell").alt = a.kunde || "Kundenlogo";
  }
  var admin = !!stand.admin, ma = stand.mitarbeiter;
  $("ich").hidden = !ma || admin;
  $("ich").textContent = ma ? ma.name : "";
  $("ich").title = ma ? (ma.quelle === "m365" ? "Angemeldet mit Microsoft 365: " : "Angemeldet als ") + ma.konto : "";
  if (ma && !trimmen($("n-benutzer").value)) $("n-benutzer").value = ma.name;
  $("seite-themen").classList.toggle("gesperrt", !!stand.anmeldungNoetig);
  $("anmeldung-noetig").hidden = !stand.anmeldungNoetig;
  $("anmelden").hidden = admin;
  // Admin über den Anwendungsproxy (Microsoft 365 + AD-Gruppe): keine eigene Sitzung, daher kein "Abmelden"
  $("abmelden").hidden = !admin || !!stand.adminAutomatisch;
  $("an-windows-zeile").hidden = !(stand.aussehen || {}).adAdmins;
  $("k-form").hidden = !!stand.adminAd;
  $("k-ad").hidden = !stand.adminAd;
  $("abmelden").textContent = admin ? "Abmelden (" + stand.admin + ")" : "";
  $("reiter-verwaltung").hidden = !admin;
  $("reiter-bericht").hidden = !admin;
  if (!admin && seite !== "themen") reiter("themen");
  $("version-knopf").textContent = "v" + (p.version || "");
  $("info-version").textContent = "Version " + (p.version || "");
  $("info-firma").textContent = p.firma || "";
  document.querySelectorAll(".prog-name").forEach(function (e) { e.textContent = p.name || "reitzner IT-Themensammler"; });
}

// Standort, den dieser Benutzer zuletzt angegeben hat (aus seinem neuesten Thema), sonst leer
function letzterStandort(name) {
  name = trimmen(name).toLowerCase();
  if (!name) return "";
  var liste = daten().anfragen.filter(function (a) { return a.benutzer.toLowerCase() === name && a.standort; });
  if (!liste.length) return "";
  // neuestes zuerst: nach Erfassungszeit, ohne Zeit (z. B. aus der Excel-Liste) nach Reihenfolge der Eingabe
  var neuestes = liste[liste.length - 1];
  liste.forEach(function (a) { if (a.erfasst && (!neuestes.erfasst || a.erfasst >= neuestes.erfasst)) neuestes = a; });
  return neuestes.standort;
}
var standortVonHand = false;

// Standort aus dem Active Directory (Büro bzw. Abteilung), wenn der angemeldete Mitarbeiter für sich selbst einträgt
function adStandort(name) {
  var ma = stand.mitarbeiter;
  if (!ma || !ma.standort) return "";
  return trimmen(name).toLowerCase() === (ma.name || "").toLowerCase() ? ma.standort : "";
}

function standortVorschlagen() {
  var sel = $("n-standort"), n = $("n-benutzer").value;
  var s = standortVonHand ? sel.value : (letzterStandort(n) || adStandort(n) || sel.value);
  standortAuswahl(sel, s, true); // "Zuletzt verwendet" für diesen Benutzer neu aufbauen
}

function auswahlZeichnen() {
  standortAuswahl($("n-standort"), $("n-standort").value || letzterStandort($("n-benutzer").value) || adStandort($("n-benutzer").value) || gemerkt("standort"), true);
  optionen($("n-termin"), terminOptionen(), $("n-termin").value);
  ticketHinweis();

  var dl = $("benutzer-liste");
  leeren(dl);
  eindeutig(daten().stammdaten.benutzer).sort(nachName).forEach(function (n) {
    var o = document.createElement("option");
    o.value = n;
    dl.appendChild(o);
  });

  var t = eindeutig(feldListe("wunschtermin")).sort(sortiereTermin).map(function (w) { return [w, terminText(w)]; });
  optionen($("f-termin"), [["", "Alle Termine"]].concat(t), $("f-termin").value);
  var o = eindeutig(feldListe("standort")).sort(nachName).map(function (s) { return [s, s]; });
  optionen($("f-standort"), [["", "Alle Standorte"]].concat(o), $("f-standort").value);
}

function ticketHinweis() {
  var ticket = $("n-termin").value === TICKET;
  var a = stand.aussehen || {};
  // Hinweis immer sichtbar; bei "Schnellstmöglich/Ticket" hervorgehoben und Eintragen gesperrt
  $("n-ticket").hidden = !a.ticketMail && !a.hotline;
  $("n-ticket").classList.toggle("aktiv", ticket);
  $("n-absenden").disabled = ticket;
  var el = $("n-ticket-text");
  leeren(el);
  el.appendChild(document.createTextNode("Bitte direkt"));
  if (a.ticketMail) {
    el.appendChild(document.createTextNode(" per E-Mail an "));
    var link = neuesEl("a", "", a.ticketMail);
    link.href = "mailto:" + a.ticketMail;
    el.appendChild(link);
  }
  if (a.hotline) el.appendChild(document.createTextNode((a.ticketMail ? " oder" : "") + " über die Telefon-Hotline " + a.hotline));
  el.appendChild(document.createTextNode(" melden."));
}

function tabelleZeichnen() {
  var suche = trimmen($("f-suche").value).toLowerCase();
  var fT = $("f-termin").value, fS = $("f-standort").value, h = heute();
  var admin = !!stand.admin;
  // Darf der Mitarbeiter erledigte Themen sehen? (Verwaltung › Aussehen; der Server liefert sie dann gar nicht erst)
  var mitErledigten = admin || (stand.aussehen || {}).erledigteSichtbar !== false;
  $("segment").hidden = !mitErledigten;
  if (!mitErledigten && filterStatus !== "offen") {
    filterStatus = "offen";
    document.querySelectorAll("#segment button").forEach(function (x) { x.classList.toggle("aktiv", x.getAttribute("data-status") === "offen"); });
  }
  var treffer = daten().anfragen.filter(function (a) {
    if (filterStatus === "offen" && a.erledigtAm) return false;
    if (filterStatus === "erledigt" && !a.erledigtAm) return false;
    if (fT && a.wunschtermin !== fT) return false;
    if (fS && a.standort !== fS) return false;
    return !suche || (a.standort + " " + a.benutzer + " " + a.problem + " " + (a.notiz || "") + " " + (a.hinweis || "") + " " + (STATUS_TEXT[a.status] || "")).toLowerCase().indexOf(suche) !== -1;
  }).sort(function (a, b) {
    if (!!a.erledigtAm !== !!b.erledigtAm) return a.erledigtAm ? 1 : -1;
    if (a.erledigtAm) return a.erledigtAm < b.erledigtAm ? 1 : a.erledigtAm > b.erledigtAm ? -1 : 0;
    return sortiereTermin(a.wunschtermin, b.wunschtermin) || a.standort.localeCompare(b.standort, "de");
  });

  var tbody = $("zeilen");
  leeren(tbody);
  treffer.forEach(function (a) {
    var z = tbody.insertRow(-1);
    if (a.erledigtAm) z.className = "fertig";
    z.insertCell(-1).textContent = a.standort;
    var bz = z.insertCell(-1);
    bz.textContent = a.benutzer;
    if (a.ersteller) bz.title = "Eingetragen von " + a.ersteller.replace(/^ad:/, "");
    var t = z.insertCell(-1);
    if (a.wunschtermin === TICKET) t.appendChild(neuesEl("span", "marke-klein ticket", TICKET_TEXT));
    else t.textContent = terminText(a.wunschtermin);
    if (!a.erledigtAm && a.wunschtermin !== TICKET && a.wunschtermin < h) {
      t.appendChild(document.createElement("br"));
      t.appendChild(neuesEl("span", "marke-klein spaet", "Termin vorbei"));
    }
    var pz = z.insertCell(-1);
    pz.appendChild(neuesEl("div", "text", a.problem));
    standZeichnen(pz, a);
    if (a.notiz) pz.appendChild(neuesEl("div", "text loesung", a.notiz));
    if (a.bild) {
      var bb = neuesEl("button", "bild-knopf", "Bild ansehen");
      bb.type = "button";
      bb.onclick = function () { bildOeffnen(a); };
      pz.appendChild(bb);
    }

    var cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = !!a.erledigtAm;
    cb.disabled = !admin;
    cb.title = admin ? (a.erledigtAm ? "Wieder öffnen" : "Als erledigt markieren") : (a.erledigtAm ? "Erledigt" : "Offen");
    cb.setAttribute("aria-label", cb.title);
    cb.onclick = function () {
      if (cb.checked) {
        cb.checked = false; // Haken erst nach dem Dialog setzen
        erledigenOeffnen(a);
      } else {
        zuruecknehmen(a.id, cb);
      }
    };
    var kasten = neuesEl("label", "check");
    kasten.appendChild(cb);
    kasten.appendChild(document.createElement("span"));
    var hz = z.insertCell(-1);
    hz.className = "haken";
    hz.appendChild(kasten);

    var dz = z.insertCell(-1);
    dz.className = "datum";
    dz.textContent = datumKurz(a.erledigtAm);
    if (a.erledigtAm && a.erledigtVon) dz.appendChild(neuesEl("span", "von", a.erledigtVon));

    var mz = z.insertCell(-1);
    mz.className = "mehr";
    if (admin || istEigen(a)) {
      var b = neuesEl("button", "", "Bearbeiten");
      b.type = "button";
      b.onclick = function () { bearbeiten(a.id); };
      mz.appendChild(b);
      if (!admin) mz.appendChild(neuesEl("span", "eigen-marke", "Ihr Thema"));
    }
  });
  if (!treffer.length) {
    var td = tbody.insertRow(-1).insertCell(-1);
    td.colSpan = 7;
    td.className = "leer";
    td.textContent = filterStatus === "offen" && !suche && !fT && !fS ? "Keine offenen Themen." : "Keine passenden Einträge.";
  }
  var offen = daten().anfragen.filter(function (a) { return !a.erledigtAm; }).length;
  $("anzahl").textContent = offen + " offen";
}

// Stand eines offenen Themas (In Arbeit, Rückfrage …) mit dem Hinweis des Technikers
function standZeichnen(zelle, a) {
  if (a.erledigtAm || (!a.status && !a.hinweis)) return;
  var d = neuesEl("div", "stand-zeile");
  if (STATUS_TEXT[a.status]) d.appendChild(neuesEl("span", "marke-klein stand-" + a.status, STATUS_TEXT[a.status]));
  if (a.hinweis) d.appendChild(neuesEl("span", "text stand-hinweis", a.hinweis));
  zelle.appendChild(d);
}

function bildAdresse(a) { return "/api/anfragen/" + a.id + "/bild?v=" + encodeURIComponent(a.bild || ""); }
function bildOeffnen(a) {
  $("bild-titel").textContent = "Bild – " + a.benutzer + ", " + a.standort;
  $("bild-gross").setAttribute("src", bildAdresse(a));
  $("bild-neu").setAttribute("href", bildAdresse(a));
  dialogOeffnen("bild-dialog");
}
// Bild prüfen, bevor es hochgeladen wird; liefert eine Fehlermeldung oder ""
function bildFehler(datei) {
  if (!datei) return "";
  if (!/^image\/(png|jpeg|gif|bmp)$/.test(datei.type || "")) return "Bitte ein Bild wählen (PNG, JPG, GIF oder BMP).";
  if (datei.size > 10 * 1024 * 1024) return "Das Bild ist zu groß (höchstens 10 MB).";
  return "";
}
function bildSenden(id, datei) {
  return senden("/api/anfragen/" + id + "/bild", "PUT", datei, datei.type, stand.admin ? "" : schluesselVon(id));
}

function listeZeichnen(name) {
  var ul = $("l-" + name), werte = daten().stammdaten[name], h = heute();
  leeren(ul);
  var sd = daten().stammdaten;
  werte.forEach(function (w) {
    var vorbei = name === "termine" && w < h;
    var li = neuesEl("li", vorbei ? "vorbei" : "");
    var text = neuesEl("span", "eintrag", (name === "termine" ? datumLang(w) : w) + (vorbei ? " (vorbei)" : ""));
    var knoepfe = neuesEl("span", "knoepfe");
    if (name === "benutzer" && enthaelt(sd.adBenutzer || [], w)) text.appendChild(neuesEl("span", "zuordnung", "aus dem Active Directory"));
    if (name === "termine") {
      var tb = neuesEl("button", "", "Bearbeiten");
      tb.type = "button";
      tb.onclick = function () { terminOeffnen(w); };
      knoepfe.appendChild(tb);
    }
    if (name === "benutzer") {
      var nb = neuesEl("button", "", "Bearbeiten");
      nb.type = "button";
      nb.onclick = function () { benutzerOeffnen(w); };
      knoepfe.appendChild(nb);
    }
    // Benutzer aus dem AD werden ausgeblendet statt gelöscht (der Abgleich würde sie sonst wieder eintragen)
    var ad = name === "benutzer" && enthaelt(sd.adBenutzer || [], w);
    var b = neuesEl("button", "", ad ? "Ausblenden" : "Entfernen");
    b.type = "button";
    if (ad) b.title = "Nicht mehr in der Auswahl anzeigen; der AD-Abgleich trägt den Benutzer nicht wieder ein";
    b.onclick = function () {
      listeAendern(name, function (l) { return l.filter(function (x) { return x !== w; }); });
    };
    knoepfe.appendChild(b);
    li.appendChild(text);
    li.appendChild(knoepfe);
    ul.appendChild(li);
  });
  if (name === "termine") ul.appendChild(neuesEl("li", "fest", TICKET_TEXT + " (Hinweis auf E-Mail und Hotline)"));
  else if (!werte.length) ul.appendChild(neuesEl("li", "fest", "Noch keine Einträge."));
  if (name === "benutzer") ausgeblendeteZeichnen();
}

// Ausgeblendete AD-Benutzer: zusammengeklappt unter der Liste, mit "Einblenden"
function ausgeblendeteZeichnen() {
  var sd = daten().stammdaten, aus = (sd.adAusgeschlossen || []).slice().sort(nachName), ul = $("l-ausgeblendet");
  $("b-ausgeblendet").hidden = !aus.length;
  $("b-ausgeblendet-titel").textContent = "Ausgeblendet (" + aus.length + ")";
  leeren(ul);
  aus.forEach(function (w) {
    var li = neuesEl("li");
    var text = neuesEl("span", "eintrag", w);
    text.appendChild(neuesEl("span", "zuordnung", "aus dem Active Directory"));
    var knoepfe = neuesEl("span", "knoepfe");
    var b = neuesEl("button", "", "Einblenden");
    b.type = "button";
    b.onclick = function () {
      listeAendern("benutzer", function (l) { if (!enthaelt(l, w)) l.push(w); return l.sort(nachName); });
    };
    knoepfe.appendChild(b);
    li.appendChild(text);
    li.appendChild(knoepfe);
    ul.appendChild(li);
  });
}

function aussehenZeichnen(erzwingen) {
  var a = stand.aussehen || {};
  // Eingaben nicht überschreiben, solange der Admin gerade etwas ändert
  if (erzwingen || !aussehenGeaendert) {
    $("a-titel").value = a.titel || "";
    $("a-untertitel").value = a.untertitel || "";
    $("a-kunde").value = a.kunde || "";
    $("a-mail").value = a.ticketMail || "";
    $("a-hotline").value = a.hotline || "";
    $("a-gruppierung").value = a.gruppierung || "ort";
    $("a-erledigte").checked = a.erledigteSichtbar !== false;
    $("a-anmeldung").value = a.mitarbeiterAnmeldung || "aus";
    $("a-adstandort").value = a.adStandortFeld || "";
    var lm = String(a.loeschMonate || 0), ls = $("a-loeschfrist");
    if (!Array.prototype.some.call(ls.options, function (o) { return o.value === lm; })) {
      var lo = document.createElement("option"); // von Hand in der einstellungen.json gesetzter Wert
      lo.value = lm; lo.textContent = lm + " Monate nach dem Erledigen";
      ls.appendChild(lo);
    }
    ls.value = lm;
    $("a-archiv").checked = a.loeschArchiv !== false;
    $("a-archiv").disabled = lm === "0";
    if (document.activeElement !== $("a-admingruppe")) $("a-admingruppe").value = a.adAdminGruppe || "";
    aussehenGeaendert = false;
  }
  var v = encodeURIComponent(a.logoStand || "");
  logoVorschau("hell", a.logoHell, "/logo/kunde-hell.png?v=" + v, "Kein Logo");
  logoVorschau("dunkel", a.logoDunkel, "/logo/kunde-dunkel.png?v=" + v, a.logoHell ? "Wie hell" : "Kein Logo");
}
function logoVorschau(art, vorhanden, url, leer) {
  var img = $("lv-" + art);
  img.hidden = !vorhanden;
  if (vorhanden && img.getAttribute("src") !== url) img.setAttribute("src", url);
  $("lv-" + art + "-leer").hidden = vorhanden;
  $("lv-" + art + "-leer").textContent = leer;
  $("ll-" + art).disabled = !vorhanden;
}

function anzeigen() {
  kopfZeichnen();
  auswahlZeichnen();
  tabelleZeichnen();
  if (stand.admin && seite === "bericht") berichtZeichnen();
  if (stand.admin) {
    tafelZeichnen();
    adZeichnen();
    updateZeichnen();
    listeZeichnen("benutzer");
    listeZeichnen("termine");
    aussehenZeichnen(false);
  }
}

/* ---------- Anmelden ---------- */

function dialogOeffnen(id) {
  var d = $(id);
  if (!d.open) d.showModal();
}
function dialogSchliessen(id) {
  var d = $(id);
  if (d.open) d.close();
}
function dialogOffen() {
  return Array.prototype.some.call(document.querySelectorAll("dialog"), function (d) { return d.open; });
}
document.querySelectorAll("[data-schliessen]").forEach(function (b) {
  b.onclick = function () { b.closest("dialog").close(); };
});

$("anmelden").onclick = function () {
  setzeMeldung($("an-meldung"), "", true);
  $("an-kennwort").value = "";
  if (!$("an-name").value) $("an-name").value = gemerkt("admin");
  dialogOeffnen("anmelde-dialog");
  ($("an-name").value ? $("an-kennwort") : $("an-name")).focus();
};
$("anmelde-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var name = trimmen($("an-name").value), k = $("an-kennwort").value;
  if (!name || !k) { setzeMeldung($("an-meldung"), "Bitte Name und Kennwort eingeben.", true); return; }
  try {
    await senden("/api/anmelden", "POST", { name: name, kennwort: k });
    merken("admin", name);
    $("an-kennwort").value = "";
    dialogSchliessen("anmelde-dialog");
  } catch (e) {
    $("an-kennwort").value = "";
    setzeMeldung($("an-meldung"), e.message, true);
    $("an-kennwort").focus();
  }
};
$("abmelden").onclick = async function () {
  domaeneAbgemeldet = true;
  try { await senden("/api/abmelden", "POST", {}); } catch (e) { warnung(e.message); }
};
// Admins der AD-Gruppe: mit dem Windows-Konto anmelden
$("an-windows").onclick = async function () {
  setzeMeldung($("an-meldung"), "", true);
  if (await domaeneAnmelden() && stand.admin) dialogSchliessen("anmelde-dialog");
  else setzeMeldung($("an-meldung"), stand.admin ? "" : (domaeneFehler || "Ihr Windows-Konto gehört nicht zur Gruppe der Admins."), true);
};

/* ---------- Stammdaten ---------- */

// Stammdaten mit allen Feldern kopieren (für PUT /api/stammdaten)
function stammKopie() {
  var s = daten().stammdaten;
  return {
    standorte: s.standorte.slice(), benutzer: s.benutzer.slice(), termine: s.termine.slice(),
    orte: Object.assign({}, s.orte || {}), arten: Object.assign({}, s.arten || {}),
    eigeneReihenfolge: !!s.eigeneReihenfolge
  };
}

var zuordnungName = "";
var standortLoeschenBereit = false;
// Standort entfernen (zweiter Klick bestätigt); die Themen behalten ihren Standort-Text
$("z-loeschen").onclick = async function () {
  if (!standortLoeschenBereit) {
    standortLoeschenBereit = true;
    this.textContent = "Wirklich entfernen?";
    return;
  }
  var name = zuordnungName;
  if (await listeAendern("standorte", function (l) { return l.filter(function (x) { return x !== name; }); })) {
    dialogSchliessen("z-dialog");
    setzeMeldung($("g-meldung"), "„" + name + "“ entfernt.", false);
  }
};
var zusammenfuehren = false;
function zuordnenOeffnen(name) {
  zuordnungName = name;
  zusammenfuehren = false;
  standortLoeschenBereit = false;
  $("z-loeschen").textContent = "Entfernen";
  $("z-speichern").textContent = "Speichern";
  var sd = daten().stammdaten, gs = gruppen();
  $("z-neu").value = name;
  var n = daten().anfragen.filter(function (a) { return a.standort === name; }).length;
  $("z-hinweis").hidden = !n;
  $("z-hinweis").textContent = n === 1 ? "Ein Thema hat diesen Standort; es wird beim Umbenennen mit geändert."
    : n + " Themen haben diesen Standort; sie werden beim Umbenennen mit geändert.";
  var fuellen = function (sel, liste, fest, vorschlag) {
    leeren(sel);
    var o = document.createElement("option");
    o.value = "";
    o.textContent = "Automatisch (" + vorschlag + ")";
    sel.appendChild(o);
    liste.forEach(function (x) { var e = document.createElement("option"); e.value = x; e.textContent = x; sel.appendChild(e); });
    sel.value = fest && enthaelt(liste, fest) ? fest : "";
  };
  fuellen($("z-ort"), [gs.sammelOrt].concat(gs.orte), (sd.orte || {})[name], ortVorschlag(name, gs));
  fuellen($("z-art"), gs.arten.concat([gs.sammelArt]), (sd.arten || {})[name], artVorschlagIn(name, gs));
  setzeMeldung($("z-meldung"), "", true);
  dialogOeffnen("z-dialog");
  $("z-neu").focus();
}
$("z-neu").addEventListener("input", function () {
  // anderer Name: Zusammenführen erneut bestätigen
  if (zusammenfuehren) { zusammenfuehren = false; $("z-speichern").textContent = "Speichern"; setzeMeldung($("z-meldung"), "", true); }
});
$("z-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var ort = $("z-ort").value, art = $("z-art").value, alt = zuordnungName, neu = trimmen($("z-neu").value);
  if (!neu) { setzeMeldung($("z-meldung"), "Bitte einen Namen eingeben.", true); return; }
  try {
    await senden("/api/standort", "POST", { alt: alt, neu: neu, ort: ort, art: art, zusammenfuehren: zusammenfuehren });
    dialogSchliessen("z-dialog");
    var r = stand.neu || {};
    // Standort im Formular "Neues Thema" mitziehen
    if (gemerkt("standort") === alt) merken("standort", r.standort || neu);
    setzeMeldung($("g-meldung"), r.standort && r.standort !== alt
      ? "Standort umbenannt in „" + r.standort + "“" + (r.themen ? " (" + r.themen + (r.themen === 1 ? " Thema" : " Themen") + " angepasst)." : ".")
      : "Standort gespeichert.", false);
  } catch (e) {
    if (e.status === 409) {
      zusammenfuehren = true;
      $("z-speichern").textContent = "Zusammenführen";
    }
    setzeMeldung($("z-meldung"), e.message, true);
  }
};

/* ---------- Standorte und Gruppen: Tafel mit Drag and Drop ----------
 * Je Gruppe eine Spalte. Standort in eine Spalte ziehen = fest zuordnen; Spaltenkopf ziehen = Reihenfolge.
 * Klick auf einen Standort öffnet "Standort bearbeiten" (dort auch Ort/Art wählen – so geht es auch auf dem Tablet). */

var tafelAnsicht = null;   // "ort" | "art"; anfangs wie unter Aussehen eingestellt
var gezogen = null;        // { art: "standort" | "gruppe", name }

function tafelArt() {
  if (!tafelAnsicht) tafelAnsicht = (stand.aussehen || {}).gruppierung === "art" ? "art" : "ort";
  return tafelAnsicht;
}

function tafelZeichnen() {
  var art = tafelArt(), gs = gruppen(), sd = daten().stammdaten;
  document.querySelectorAll("#t-ansicht button").forEach(function (x) { x.classList.toggle("aktiv", x.getAttribute("data-ansicht") === art); });
  var sammel = art === "ort" ? gs.sammelOrt : gs.sammelArt;
  var spalten = art === "ort" ? [gs.sammelOrt].concat(gs.orte) : gs.arten.concat([gs.sammelArt]);
  var fest = art === "ort" ? gs.orteFest : gs.artenFest;
  $("t-az").hidden = !sd.eigeneReihenfolge;
  $("t-hinweis").hidden = fest;
  $("t-hinweis").textContent = art === "ort"
    ? "Die Orte sind noch nicht festgelegt, sie sind aus den Standortnamen erkannt. Mit der ersten Änderung werden sie übernommen."
    : "Die Arten sind noch nicht festgelegt (Standardliste). Mit der ersten Änderung werden sie übernommen.";

  var inhalt = {};
  spalten.forEach(function (g) { inhalt[g] = []; });
  sd.standorte.forEach(function (s) {
    var g = art === "ort" ? ortVon(s, gs) : artVon(s, gs);
    (inhalt[g] = inhalt[g] || []).push(s);
  });

  var t = $("tafel");
  leeren(t);
  spalten.forEach(function (g) {
    var istSammel = g === sammel;
    var sp = neuesEl("div", "spalte" + (istSammel ? " sammel" : ""));
    var kopf = neuesEl("div", "spalte-kopf");
    var titel = neuesEl("span", "spalte-titel", g);
    titel.title = istSammel ? (art === "ort" ? "Standorte ohne Ort – steht in der Auswahl oben" : "Standorte ohne Art – steht in der Auswahl unten") : "Ziehen, um die Reihenfolge zu ändern";
    kopf.appendChild(titel);
    kopf.appendChild(neuesEl("span", "spalte-zahl", String(inhalt[g].length)));
    var ub = neuesEl("button", "spalte-knopf", "✎");
    ub.type = "button";
    ub.title = "Gruppe umbenennen";
    ub.onclick = function () { umbenennenOeffnen(art, g); };
    kopf.appendChild(ub);
    if (!istSammel) {
      var wb = neuesEl("button", "spalte-knopf", "×");
      wb.type = "button";
      wb.title = "Gruppe entfernen – die Standorte kommen nach „" + sammel + "“";
      wb.onclick = function () { gruppenAendern(art, "weg", g); };
      kopf.appendChild(wb);
      kopf.draggable = true;
      kopf.ondragstart = function (e) {
        gezogen = { art: "gruppe", name: g };
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", g);
        sp.classList.add("gezogen");
      };
      kopf.ondragend = function () { sp.classList.remove("gezogen"); gezogen = null; };
    }
    sp.appendChild(kopf);

    var anzeige = function (s) { return art === "ort" ? kurzName(s, g, gs) : s; };
    standortReihe(inhalt[g], anzeige).forEach(function (s) {
      var fix = art === "ort" ? (sd.orte || {})[s] === g : (sd.arten || {})[s] === g;
      var k = neuesEl("div", "kaertchen" + (fix ? "" : " auto"));
      k.draggable = true;
      k.tabIndex = 0;
      k.title = s + (fix ? "" : " (automatisch erkannt)") + " – klicken zum Bearbeiten";
      k.appendChild(neuesEl("span", "k-name", art === "ort" ? kurzName(s, g, gs) : s));
      k.appendChild(neuesEl("span", "k-info", art === "ort" ? artVon(s, gs) : ortVon(s, gs)));
      k.onclick = function () { zuordnenOeffnen(s); };
      k.onkeydown = function (e) { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); zuordnenOeffnen(s); } };
      k.ondragstart = function (e) {
        gezogen = { art: "standort", name: s };
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", s);
        setTimeout(function () { k.classList.add("gezogen"); }, 0);
      };
      k.ondragend = function () { k.classList.remove("gezogen"); gezogen = null; };
      // auf ein anderes Kärtchen ziehen: davor bzw. dahinter einsortieren (obere bzw. untere Hälfte)
      k.ondragover = function (e) {
        if (!gezogen || gezogen.art !== "standort" || gezogen.name === s) return;
        e.preventDefault();
        e.stopPropagation();
        e.dataTransfer.dropEffect = "move";
        var r = k.getBoundingClientRect(), nach = e.clientY > r.top + r.height / 2;
        k.classList.toggle("einfuegen-vor", !nach);
        k.classList.toggle("einfuegen-nach", nach);
        sp.classList.add("ueber");
      };
      k.ondragleave = function () { k.classList.remove("einfuegen-vor", "einfuegen-nach"); };
      k.ondrop = function (e) {
        e.preventDefault();
        e.stopPropagation();
        var nach = k.classList.contains("einfuegen-nach");
        k.classList.remove("einfuegen-vor", "einfuegen-nach");
        sp.classList.remove("ueber");
        var z = gezogen;
        gezogen = null;
        if (z && z.art === "standort" && z.name !== s) verschieben(z.name, art, g, s, nach);
      };
      sp.appendChild(k);
    });
    if (!inhalt[g].length) sp.appendChild(neuesEl("div", "spalte-leer", "Standorte hierher ziehen"));

    sp.ondragover = function (e) {
      if (!gezogen) return;
      if (gezogen.art === "gruppe" && (istSammel || gezogen.name === g)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      sp.classList.add("ueber");
    };
    sp.ondragleave = function (e) { if (!sp.contains(e.relatedTarget)) sp.classList.remove("ueber"); };
    sp.ondrop = function (e) {
      e.preventDefault();
      sp.classList.remove("ueber");
      var z = gezogen;
      gezogen = null;
      if (!z) return;
      if (z.art === "standort") verschieben(z.name, art, g, null, true);
      else gruppenAendern(art, "vor", z.name, g);
    };
    t.appendChild(sp);
  });

  // neue Gruppe
  var neu = neuesEl("form", "spalte neu-spalte");
  var feld = document.createElement("input");
  feld.type = "text";
  feld.maxLength = 50;
  feld.placeholder = art === "ort" ? "Neuer Ort" : "Neue Art";
  feld.title = feld.placeholder;
  var nb = neuesEl("button", "knopf neben klein", "Gruppe hinzufügen");
  neu.appendChild(feld);
  neu.appendChild(nb);
  neu.onsubmit = async function (e) {
    e.preventDefault();
    var w = trimmen(feld.value);
    if (w) await gruppenAendern(art, "neu", null, w);
  };
  t.appendChild(neu);
}

document.querySelectorAll("#t-ansicht button").forEach(function (b) {
  b.onclick = function () {
    tafelAnsicht = b.getAttribute("data-ansicht");
    tafelZeichnen();
  };
});

/*
 * Standort per Drag and Drop verschieben: fest der Gruppe zuordnen und – auf ein Kärtchen gezogen – davor bzw. dahinter
 * einsortieren (ziel = Standort, nach = dahinter). In eine freie Stelle der Spalte gezogen: ans Ende der Gruppe.
 * Die Reihenfolge gilt dann für die Auswahl beim Eintragen; "A–Z" stellt wieder alphabetisch.
 */
async function verschieben(name, art, gruppe, ziel, nach) {
  var m = $("g-meldung");
  try {
    await senden("/api/daten");
    var gs = gruppen(), neu = stammKopie();
    if (!enthaelt(neu.standorte, name)) throw new Error("Diesen Standort gibt es nicht mehr.");
    var vorher = art === "ort" ? ortVon(name, gs) : artVon(name, gs);
    (art === "ort" ? neu.orte : neu.arten)[name] = gruppe;
    neu.ortGruppen = gs.orte;
    neu.artGruppen = gs.arten;
    neu.sammelOrt = gs.sammelOrt;
    neu.sammelArt = gs.sammelArt;
    if (ziel || neu.eigeneReihenfolge) {
      var gruppeVon = function (x) { return art === "ort" ? ortVon(x, gs) : artVon(x, gs); };
      var reihe;
      if (neu.eigeneReihenfolge) {
        reihe = neu.standorte.filter(function (x) { return x !== name; });
      } else {
        // erste eigene Reihenfolge: Ausgangspunkt ist die bisherige alphabetische Anzeige aller Gruppen
        var alleGruppen = art === "ort" ? [gs.sammelOrt].concat(gs.orte) : gs.arten.concat([gs.sammelArt]);
        reihe = [];
        alleGruppen.forEach(function (gr) {
          var inGr = neu.standorte.filter(function (x) { return x !== name && gruppeVon(x) === gr; });
          reihe = reihe.concat(standortReihe(inGr, function (x) { return art === "ort" ? kurzName(x, gr, gs) : x; }));
        });
        neu.standorte.forEach(function (x) { if (x !== name && !enthaelt(reihe, x)) reihe.push(x); });
      }
      var mitglieder = reihe.filter(function (x) { return gruppeVon(x) === gruppe; });
      var bezug = ziel || mitglieder[mitglieder.length - 1];
      var i = bezug ? reihe.indexOf(bezug) : -1;
      if (i < 0) reihe.push(name);
      else reihe.splice(ziel && !nach ? i : i + 1, 0, name);
      neu.standorte = reihe;
      neu.eigeneReihenfolge = true;
    }
    await senden("/api/stammdaten", "PUT", neu);
    setzeMeldung(m, vorher !== gruppe ? "„" + name + "“ ist jetzt in „" + gruppe + "“." : "Reihenfolge gespeichert.", false);
  } catch (e) {
    setzeMeldung(m, e.message, true);
  }
}

// Wieder alphabetisch sortieren
async function alphabetisch() {
  try {
    await senden("/api/daten");
    var neu = stammKopie();
    neu.standorte.sort(nachName);
    neu.eigeneReihenfolge = false;
    await senden("/api/stammdaten", "PUT", neu);
    setzeMeldung($("g-meldung"), "Standorte wieder alphabetisch sortiert.", false);
  } catch (e) {
    setzeMeldung($("g-meldung"), e.message, true);
  }
}
$("t-az").onclick = alphabetisch;

/*
 * Gruppen ändern (neu, verschieben, umbenennen, entfernen). Die Gruppenlisten werden dabei festgeschrieben.
 * Beim Umbenennen bleiben die Standorte der Gruppe darin (sie werden fest zugeordnet); beim Entfernen fallen
 * feste Zuordnungen zu dieser Gruppe weg, die Standorte landen dann automatisch in der passenden bzw. der Sammelgruppe.
 */
async function gruppenAendern(art, aktion, name, neuerName) {
  var m = $("g-meldung");
  try {
    await senden("/api/daten");
    var gs = gruppen(), neu = stammKopie();
    var liste = (art === "ort" ? gs.orte : gs.arten).slice();
    var sammel = art === "ort" ? gs.sammelOrt : gs.sammelArt;
    var zuordnung = art === "ort" ? neu.orte : neu.arten;
    var i = liste.indexOf(name);
    if (aktion === "neu") {
      if (!neuerName) return;
      if (enthaelt(liste, neuerName) || neuerName === sammel) throw new Error("Die Gruppe „" + neuerName + "“ gibt es schon.");
      liste.push(neuerName);
    } else if (aktion === "hoch" && i > 0) {
      liste.splice(i - 1, 0, liste.splice(i, 1)[0]);
    } else if (aktion === "runter" && i >= 0 && i < liste.length - 1) {
      liste.splice(i + 1, 0, liste.splice(i, 1)[0]);
    } else if (aktion === "vor" && i >= 0) {
      // vor die Zielgruppe schieben; Ziel = Sammelgruppe: ganz nach vorn (Ort) bzw. ans Ende (Art)
      liste.splice(i, 1);
      var z = liste.indexOf(neuerName);
      if (z < 0) { if (art === "ort") liste.unshift(name); else liste.push(name); }
      else liste.splice(z, 0, name);
    } else if (aktion === "weg") {
      liste.splice(i, 1);
      Object.keys(zuordnung).forEach(function (s) { if (zuordnung[s] === name) delete zuordnung[s]; });
    } else if (aktion === "umbenennen") {
      if (!neuerName || neuerName === name) return;
      if (enthaelt(liste, neuerName) || neuerName === sammel) throw new Error("Die Gruppe „" + neuerName + "“ gibt es schon.");
      if (i >= 0) liste[i] = neuerName;
      // Standorte der Gruppe mitnehmen (feste Zuordnungen zur Sammelgruppe zeigen auf den neuen Namen)
      daten().stammdaten.standorte.forEach(function (s) {
        if (name === sammel ? zuordnung[s] === name : (art === "ort" ? ortVon(s, gs) : artVon(s, gs)) === name) zuordnung[s] = neuerName;
      });
      if (name === sammel) {
        if (art === "ort") neu.sammelOrt = neuerName; else neu.sammelArt = neuerName;
      }
    }
    neu.ortGruppen = art === "ort" ? liste : gs.orte;
    neu.artGruppen = art === "art" ? liste : gs.arten;
    if (!neu.sammelOrt) neu.sammelOrt = gs.sammelOrt;
    if (!neu.sammelArt) neu.sammelArt = gs.sammelArt;
    await senden("/api/stammdaten", "PUT", neu);
    setzeMeldung(m, "Gruppen gespeichert.", false);
  } catch (e) {
    setzeMeldung(m, e.message, true);
  }
}

var umbenennen = null;
function umbenennenOeffnen(art, name) {
  umbenennen = { art: art, name: name };
  $("u-alt").textContent = name;
  $("u-neu").value = name;
  setzeMeldung($("u-meldung"), "", true);
  dialogOeffnen("u-dialog");
  $("u-neu").select();
}
$("u-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var neu = trimmen($("u-neu").value);
  if (!neu) { setzeMeldung($("u-meldung"), "Bitte einen Namen eingeben.", true); return; }
  dialogSchliessen("u-dialog");
  await gruppenAendern(umbenennen.art, "umbenennen", umbenennen.name, neu);
};

async function listeAendern(name, fn) {
  var m = $(name === "standorte" ? "g-meldung" : "d-meldung");
  try {
    await senden("/api/daten"); // erst den neuesten Stand holen
    var neu = stammKopie();
    neu[name] = fn(neu[name]);
    await senden("/api/stammdaten", "PUT", neu);
    setzeMeldung(m, "Gespeichert.", false);
    return true;
  } catch (e) {
    setzeMeldung(m, e.message, true);
    return false;
  }
}

document.querySelectorAll("form.neu").forEach(function (f) {
  f.onsubmit = async function (ev) {
    ev.preventDefault();
    var eingabe = f.querySelector("input");
    var name = f.getAttribute("data-liste");
    var wert = trimmen(eingabe.value);
    if (!wert) return;
    if (name === "termine") {
      wert = datumEingabe(wert);
      if (!wert) { setzeMeldung($("d-meldung"), "Bitte das Datum als TT.MM.JJJJ eingeben.", true); return; }
    }
    await listeAendern(name, function (l) { if (!enthaelt(l, wert)) l.push(wert); return l; });
    eingabe.value = "";
    eingabe.focus();
  };
});

// Benutzer umbenennen: der Name wird auch in allen Themen geändert; gibt es den neuen Namen schon, auf Nachfrage zusammenführen
var benutzerAlt = "", benutzerZusammen = false;
function benutzerOeffnen(name) {
  benutzerAlt = name;
  benutzerZusammen = false;
  $("bn-neu").value = name;
  $("bn-speichern").textContent = "Speichern";
  var n = daten().anfragen.filter(function (a) { return a.benutzer === name; }).length;
  var ad = enthaelt(daten().stammdaten.adBenutzer || [], name);
  $("bn-hinweis").textContent = (n ? "Der Name wird auch in " + n + (n === 1 ? " Thema" : " Themen") + " geändert." : "Zu diesem Benutzer gibt es noch keine Themen.")
    + (ad ? " Der Name stammt aus dem Active Directory: Der alte Name wird ausgeblendet, damit der Abgleich ihn nicht wieder einträgt." : "");
  setzeMeldung($("bn-meldung"), "", true);
  dialogOeffnen("bn-dialog");
  $("bn-neu").select();
}
$("bn-neu").addEventListener("input", function () {
  benutzerZusammen = false;
  $("bn-speichern").textContent = "Speichern";
  setzeMeldung($("bn-meldung"), "", true);
});
$("bn-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var neu = trimmen($("bn-neu").value);
  if (!neu) { setzeMeldung($("bn-meldung"), "Bitte einen Namen eingeben.", true); return; }
  if (neu === benutzerAlt) { dialogSchliessen("bn-dialog"); return; }
  try {
    await senden("/api/benutzer", "POST", { alt: benutzerAlt, neu: neu, zusammenfuehren: benutzerZusammen });
    var e = stand.neu || {};
    if (gemerkt("benutzer") === benutzerAlt) merken("benutzer", e.benutzer || neu);
    dialogSchliessen("bn-dialog");
    setzeMeldung($("d-meldung"), "Gespeichert" + (e.themen ? " – " + e.themen + (e.themen === 1 ? " Thema" : " Themen") + " angepasst." : "."), false);
  } catch (x) {
    if (x.status === 409) {
      benutzerZusammen = true;
      $("bn-speichern").textContent = "Zusammenführen";
    }
    setzeMeldung($("bn-meldung"), x.message, true);
  }
};

// Termin verschieben (offene Themen mit diesem Wunschtermin ziehen mit)
var terminAlt = "";
function terminOeffnen(iso) {
  terminAlt = iso;
  var p = iso.split("-");
  $("td-alt").textContent = datumLang(iso);
  $("td-neu").value = p[2] + "." + p[1] + "." + p[0];
  var n = daten().anfragen.filter(function (a) { return a.wunschtermin === iso && !a.erledigtAm; }).length;
  $("td-hinweis").textContent = n === 0 ? "Kein offenes Thema hat diesen Wunschtermin."
    : (n === 1 ? "Ein offenes Thema hat" : n + " offene Themen haben") + " diesen Wunschtermin und " + (n === 1 ? "wird" : "werden") + " mit verschoben.";
  setzeMeldung($("td-meldung"), "", true);
  dialogOeffnen("td-dialog");
  $("td-neu").select();
}
$("td-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var neu = datumEingabe($("td-neu").value);
  if (!neu) { setzeMeldung($("td-meldung"), "Bitte das Datum als TT.MM.JJJJ eingeben.", true); return; }
  try {
    await senden("/api/termin", "POST", { alt: terminAlt, neu: neu });
    dialogSchliessen("td-dialog");
    var r = stand.neu || {};
    setzeMeldung($("d-meldung"), neu === terminAlt ? "Termin unverändert."
      : "Termin auf " + datumLang(neu) + " verschoben" + (r.themen ? " (" + r.themen + (r.themen === 1 ? " Thema" : " Themen") + " angepasst)." : "."), false);
  } catch (e) {
    setzeMeldung($("td-meldung"), e.message, true);
  }
};

// Serie: jeden n-ten (bzw. letzten) Wochentag im Monat ab heute bis zum Enddatum
$("serie-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var bis = datumEingabe($("serie-bis").value);
  if (!bis) { setzeMeldung($("d-meldung"), "Bitte das Enddatum der Serie als TT.MM.JJJJ eingeben.", true); return; }
  var nr = +$("serie-nr").value, tag = +$("serie-tag").value, h = heute(), termine = [];
  var d = new Date();
  for (var m = 0; m < 60; m++) {
    var jahr = d.getFullYear(), monat = d.getMonth() + m;
    var kandidaten = [];
    for (var t = 1; t <= 31; t++) {
      var x = new Date(jahr, monat, t);
      if (x.getMonth() !== ((monat % 12) + 12) % 12) break;
      if (x.getDay() === tag) kandidaten.push(x);
    }
    var gewaehlt = nr === 5 ? kandidaten[kandidaten.length - 1] : kandidaten[nr - 1];
    if (!gewaehlt) continue;
    var iso = isoDatum(gewaehlt);
    if (iso > bis) break;
    if (iso >= h) termine.push(iso);
  }
  if (!termine.length) { setzeMeldung($("d-meldung"), "Im gewählten Zeitraum liegt kein passender Termin.", true); return; }
  await listeAendern("termine", function (l) {
    termine.forEach(function (x) { if (!enthaelt(l, x)) l.push(x); });
    return l;
  });
  $("serie-bis").value = "";
  setzeMeldung($("d-meldung"), termine.length + " Termine hinzugefügt.", false);
};

/* ---------- Admins aus einer AD-Gruppe ---------- */

$("ag-speichern").onclick = async function () {
  var g = trimmen($("a-admingruppe").value);
  try {
    await senden("/api/admingruppe", "PUT", { gruppe: g });
    setzeMeldung($("ag-meldung"), g ? "Gespeichert – Mitglieder von „" + g + "“ sind Admins." : "Gespeichert – keine Admins aus dem AD.", false);
  } catch (e) {
    setzeMeldung($("ag-meldung"), e.message, true);
  }
};

/* ---------- Benutzer aus dem Active Directory (OUs, täglich) ---------- */

var adOffenGesetzt = false;

function zeitText(iso) {
  var m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(iso || "");
  return m ? m[3] + "." + m[2] + "." + m[1] + ", " + m[4] + ":" + m[5] + " Uhr" : "";
}

function updateZeichnen() {
  var u = stand.admin ? stand.update : null;
  $("update-hinweis").hidden = !u;
  if (!u) return;
  $("update-text").textContent = "Neue Version " + u.version + " verfügbar – installiert ist " + ((stand.programm || {}).version || "?") + ".";
  $("update-seite").href = /^https:\/\/github\.com\//.test(u.seite || "") ? u.seite : "https://github.com/WestMc002/reitzner-IT-Themensammler/releases";
}

function adZeichnen() {
  var ad = stand.ad;
  if (!ad) return;
  if (!adOffenGesetzt) { $("ad-bereich").open = ad.ous.length > 0; adOffenGesetzt = true; }
  $("ad-abgleich").checked = ad.abgleich;
  $("ad-ohnenamen").checked = !!ad.ohneNamen;
  if (document.activeElement !== $("ad-ausschluss")) $("ad-ausschluss").value = (ad.ausschluss || []).join(", ");
  var ul = $("ad-ous");
  leeren(ul);
  ad.ous.forEach(function (o) {
    var li = neuesEl("li");
    var t = neuesEl("span", "eintrag", o.pfad);
    t.title = o.dn;
    var b = neuesEl("button", "", "Entfernen");
    b.type = "button";
    b.onclick = function () { adSpeichern(ad.ous.map(function (x) { return x.dn; }).filter(function (x) { return x !== o.dn; }), ad.abgleich); };
    li.appendChild(t);
    li.appendChild(b);
    ul.appendChild(li);
  });
  if (!ad.ous.length) ul.appendChild(neuesEl("li", "fest", "Noch keine OU ausgewählt."));
  var fehler = /^Fehler/.test(ad.ergebnis || "");
  $("ad-stand").textContent = ad.letzter
    ? "Letzter Abgleich: " + zeitText(ad.letzter) + (fehler ? "" : " – " + ad.ergebnis)
    : (fehler ? "" : "Noch nicht abgeglichen.");
  if (fehler) setzeMeldung($("ad-meldung"), ad.ergebnis, true);
}

async function adSpeichern(ous, abgleich) {
  try {
    await senden("/api/ad", "PUT", { ous: ous, abgleich: abgleich });
    setzeMeldung($("ad-meldung"), "Gespeichert.", false);
    return true;
  } catch (e) {
    setzeMeldung($("ad-meldung"), e.message, true);
    return false;
  }
}

// Regeln für den Abgleich: Ausschluss nach Namen, Konten ohne Anzeigenamen
async function adRegelnSpeichern() {
  var teile = eindeutig($("ad-ausschluss").value.split(/[,;\n]/).map(trimmen).filter(function (x) { return !!x; }));
  if (teile.length > 50) { setzeMeldung($("ad-meldung"), "Bitte höchstens 50 Ausschlüsse eintragen.", true); return; }
  try {
    await senden("/api/ad", "PUT", {
      ous: stand.ad.ous.map(function (x) { return x.dn; }), abgleich: stand.ad.abgleich,
      ausschluss: teile, ohneNamen: $("ad-ohnenamen").checked
    });
    $("ad-ausschluss").value = (stand.ad.ausschluss || []).join(", ");
    setzeMeldung($("ad-meldung"), "Gespeichert. Die Regeln gelten ab dem nächsten Abgleich.", false);
  } catch (e) {
    setzeMeldung($("ad-meldung"), e.message, true);
  }
}
$("ad-regeln-speichern").onclick = adRegelnSpeichern;
$("ad-ohnenamen").onchange = adRegelnSpeichern;
$("ad-ausschluss").addEventListener("keydown", function (ev) {
  if (ev.key === "Enter") { ev.preventDefault(); adRegelnSpeichern(); }
});

$("ad-abgleich").onchange = function () {
  if (this.checked && !stand.ad.ous.length) {
    this.checked = false;
    setzeMeldung($("ad-meldung"), "Bitte zuerst mindestens eine OU auswählen.", true);
    return;
  }
  adSpeichern(stand.ad.ous.map(function (x) { return x.dn; }), this.checked);
};

$("ad-jetzt").onclick = async function () {
  var b = this;
  if (!stand.ad.ous.length) { setzeMeldung($("ad-meldung"), "Bitte zuerst mindestens eine OU auswählen.", true); return; }
  b.disabled = true;
  setzeMeldung($("ad-meldung"), "Abgleich läuft …", false);
  try {
    await senden("/api/ad/abgleichen", "POST");
    setzeMeldung($("ad-meldung"), "Abgleich fertig: " + ((stand.neu || {}).ad || ""), false);
  } catch (e) {
    setzeMeldung($("ad-meldung"), e.message, true);
  } finally {
    b.disabled = false;
  }
};

$("ad-waehlen").onclick = async function () {
  var baum = $("ad-baum");
  leeren(baum);
  baum.appendChild(neuesEl("li", "fest", "OUs werden geladen …"));
  setzeMeldung($("ad-dmeldung"), "", true);
  $("ad-suche").value = "";
  dialogOeffnen("ad-dialog");
  var gewaehlt = stand.ad.ous.map(function (x) { return x.dn; });
  try {
    var r = await api("/api/ad/ous");
    leeren(baum);
    r.ous.forEach(function (o) {
      var teile = o.pfad.split(" › ");
      var li = neuesEl("li");
      li.style.setProperty("--tiefe", String(Math.max(0, teile.length - 2)));
      li.setAttribute("data-suche", o.pfad.toLowerCase());
      var l = document.createElement("label");
      l.className = "ad-ou";
      l.title = o.dn;
      var c = document.createElement("input");
      c.type = "checkbox";
      c.value = o.dn;
      c.checked = enthaelt(gewaehlt, o.dn);
      l.appendChild(c);
      l.appendChild(document.createTextNode(" " + teile[teile.length - 1]));
      li.appendChild(l);
      baum.appendChild(li);
    });
    if (!r.ous.length) baum.appendChild(neuesEl("li", "fest", "Keine OUs gefunden."));
    $("ad-suche").focus();
  } catch (e) {
    leeren(baum);
    setzeMeldung($("ad-dmeldung"), e.message, true);
  }
};

$("ad-suche").oninput = function () {
  var q = trimmen(this.value).toLowerCase();
  document.querySelectorAll("#ad-baum li[data-suche]").forEach(function (li) {
    li.hidden = !!q && li.getAttribute("data-suche").indexOf(q) < 0;
  });
};

$("ad-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var ous = [];
  document.querySelectorAll("#ad-baum input[type=checkbox]").forEach(function (c) { if (c.checked) ous.push(c.value); });
  if (await adSpeichern(ous, stand.ad.abgleich && ous.length > 0)) dialogSchliessen("ad-dialog");
};

/* ---------- Aussehen und Kundenlogo ---------- */

var aussehenGeaendert = false;
$("a-loeschfrist").addEventListener("change", function () { $("a-archiv").disabled = this.value === "0"; });
["a-titel", "a-untertitel", "a-kunde", "a-mail", "a-hotline", "a-gruppierung", "a-erledigte", "a-anmeldung", "a-adstandort", "a-loeschfrist", "a-archiv"].forEach(function (id) {
  $(id).addEventListener("change", function () { aussehenGeaendert = true; setzeMeldung($("a-meldung"), "", false); });
  $(id).addEventListener("input", function () { aussehenGeaendert = true; setzeMeldung($("a-meldung"), "", false); });
});
$("a-form").onsubmit = async function (ev) {
  ev.preventDefault();
  try {
    await senden("/api/aussehen", "PUT", {
      titel: trimmen($("a-titel").value), untertitel: trimmen($("a-untertitel").value), kunde: trimmen($("a-kunde").value),
      ticketMail: trimmen($("a-mail").value), hotline: trimmen($("a-hotline").value),
      gruppierung: $("a-gruppierung").value, erledigteSichtbar: $("a-erledigte").checked,
      mitarbeiterAnmeldung: $("a-anmeldung").value, adStandortFeld: $("a-adstandort").value,
      loeschMonate: parseInt($("a-loeschfrist").value, 10) || 0, loeschArchiv: $("a-archiv").checked
    });
    aussehenZeichnen(true);
    setzeMeldung($("a-meldung"), "Gespeichert.", false);
  } catch (e) {
    setzeMeldung($("a-meldung"), e.message, true);
  }
};
$("a-abbrechen").onclick = function () {
  aussehenZeichnen(true);
  setzeMeldung($("a-meldung"), "", false);
};

["hell", "dunkel"].forEach(function (art) {
  $("lu-" + art).onchange = async function () {
    var datei = this.files[0];
    this.value = "";
    if (!datei) return;
    if (datei.size > 3 * 1024 * 1024) { setzeMeldung($("l-meldung"), "Das Bild ist zu groß (höchstens 3 MB).", true); return; }
    try {
      await senden("/api/logo/" + art, "PUT", datei, datei.type || "application/octet-stream");
      setzeMeldung($("l-meldung"), "Logo gespeichert.", false);
    } catch (e) {
      setzeMeldung($("l-meldung"), e.message, true);
    }
  };
  $("ll-" + art).onclick = async function () {
    try {
      await senden("/api/logo/" + art, "DELETE");
      setzeMeldung($("l-meldung"), "Logo entfernt.", false);
    } catch (e) {
      setzeMeldung($("l-meldung"), e.message, true);
    }
  };
});

/* ---------- Kennwort ---------- */

$("k-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var m = $("k-meldung"), neu = $("k-neu").value;
  if (neu.length < 8) { setzeMeldung(m, "Bitte mindestens 8 Zeichen verwenden.", true); return; }
  if (neu !== $("k-wdh").value) { setzeMeldung(m, "Die Kennwörter stimmen nicht überein.", true); return; }
  try {
    await senden("/api/kennwort", "POST", { alt: $("k-alt").value, neu: neu });
    ["k-alt", "k-neu", "k-wdh"].forEach(function (id) { $(id).value = ""; });
    setzeMeldung(m, "Kennwort geändert.", false);
  } catch (e) {
    setzeMeldung(m, e.message, true);
  }
};

/* ---------- Bearbeiten und Erledigen ---------- */

function erledigenOeffnen(a) {
  aktuelleId = a.id;
  $("e-problem").textContent = a.problem;
  $("e-notiz").value = a.notiz || "";
  setzeMeldung($("e-meldung"), "", true);
  dialogOeffnen("e-dialog");
  $("e-notiz").focus();
}

async function zuruecknehmen(id, cb) {
  try {
    await senden("/api/anfragen/" + id + "/erledigt", "POST", { erledigt: false });
  } catch (e) {
    cb.checked = true;
    warnung(e.message);
  }
}

$("e-form").onsubmit = async function (ev) {
  ev.preventDefault();
  try {
    await senden("/api/anfragen/" + aktuelleId + "/erledigt", "POST", { erledigt: true, notiz: trimmen($("e-notiz").value) });
    dialogSchliessen("e-dialog");
  } catch (x) {
    setzeMeldung($("e-meldung"), x.message, true);
  }
};

function bearbeiten(id) {
  var a = daten().anfragen.filter(function (x) { return x.id === id; })[0];
  if (!a) return;
  aktuelleId = id;
  loeschenBereit = false;
  $("b-loeschen").textContent = "Löschen";
  setzeMeldung($("b-meldung"), "", true);
  standortAuswahl($("b-standort"), a.standort, false);
  $("b-benutzer").value = a.benutzer;
  optionen($("b-termin"), terminOptionen(a.wunschtermin), a.wunschtermin);
  $("b-problem").value = a.problem;
  $("b-notiz").value = a.notiz || "";
  $("b-notiz-feld").hidden = !a.erledigtAm || !stand.admin;
  $("b-stand-felder").hidden = !!a.erledigtAm || !stand.admin;
  $("b-status").value = a.status || "offen";
  // "Rückfrage beantwortet" setzt nur der Mitarbeiter (durch Bearbeiten seines Themas)
  $("b-status").querySelector("option[value=beantwortet]").hidden = a.status !== "beantwortet";
  $("b-hinweis").value = a.hinweis || "";
  bildFeldZeichnen(a);
  dialogOeffnen("b-dialog");
  $("b-problem").focus();
}

$("b-form").onsubmit = async function (ev) {
  ev.preventDefault();
  try {
    var e = pruefeEintrag({
      standort: $("b-standort").value, benutzer: $("b-benutzer").value,
      wunschtermin: $("b-termin").value, problem: $("b-problem").value
    });
    if (!$("b-notiz-feld").hidden) e.notiz = trimmen($("b-notiz").value);
    if (!$("b-stand-felder").hidden) {
      e.status = $("b-status").value;
      e.hinweis = trimmen($("b-hinweis").value);
      if (e.status === "rueckfrage" && !e.hinweis) throw new Error("Bitte die Rückfrage als Hinweis eintragen.");
    }
    await senden("/api/anfragen/" + aktuelleId, "PUT", e, undefined, stand.admin ? "" : schluesselVon(aktuelleId));
    dialogSchliessen("b-dialog");
  } catch (x) {
    setzeMeldung($("b-meldung"), x.message, true);
  }
};
$("b-loeschen").onclick = async function () {
  if (!loeschenBereit) {
    loeschenBereit = true;
    $("b-loeschen").textContent = "Wirklich löschen?";
    return;
  }
  try {
    await senden("/api/anfragen/" + aktuelleId, "DELETE", undefined, undefined, stand.admin ? "" : schluesselVon(aktuelleId));
    dialogSchliessen("b-dialog");
  } catch (x) {
    setzeMeldung($("b-meldung"), x.message, true);
  }
};

// Bild im Bearbeiten-Dialog: ansehen, anhängen bzw. ersetzen, entfernen (wirkt sofort, ohne "Speichern")
function bildFeldZeichnen(a) {
  $("b-bild-ansehen").hidden = !a.bild;
  $("b-bild-weg").hidden = !a.bild;
  $("b-bild-text").textContent = a.bild ? "Ersetzen …" : "Anhängen …";
  setzeMeldung($("b-bild-meldung"), "", false);
}
function aktuellesThema() { return daten().anfragen.filter(function (x) { return x.id === aktuelleId; })[0]; }
$("b-bild-ansehen").onclick = function () { var a = aktuellesThema(); if (a) bildOeffnen(a); };
$("b-bild").onchange = async function () {
  var datei = this.files[0], m = $("b-bild-meldung");
  this.value = "";
  if (!datei) return;
  var f = bildFehler(datei);
  if (f) { setzeMeldung(m, f, true); return; }
  setzeMeldung(m, "Bild wird gespeichert …", false);
  try {
    await bildSenden(aktuelleId, datei);
    var a = aktuellesThema();
    if (a) bildFeldZeichnen(a);
    setzeMeldung(m, "Bild gespeichert.", false);
  } catch (x) {
    setzeMeldung(m, x.message, true);
  }
};
$("b-bild-weg").onclick = async function () {
  var m = $("b-bild-meldung");
  try {
    await senden("/api/anfragen/" + aktuelleId + "/bild", "DELETE", undefined, undefined, stand.admin ? "" : schluesselVon(aktuelleId));
    var a = aktuellesThema();
    if (a) bildFeldZeichnen(a);
    setzeMeldung(m, "Bild entfernt.", false);
  } catch (x) {
    setzeMeldung(m, x.message, true);
  }
};

/* ---------- Neues Thema ---------- */

// Bild zum neuen Thema: gewählt oder mit Strg+V ins Feld "Problem" eingefügt; hochgeladen wird es nach dem Eintragen
var neuesBild = null;
function neuesBildSetzen(datei) {
  var f = bildFehler(datei);
  if (f) { setzeMeldung($("n-meldung"), f, true); return; }
  neuesBild = datei || null;
  $("n-bild-info").hidden = !neuesBild;
  $("n-bild-name").textContent = neuesBild ? (neuesBild.name || "Bild aus der Zwischenablage") : "";
}
$("n-bild").onchange = function () { var d = this.files[0]; this.value = ""; if (d) neuesBildSetzen(d); };
$("n-bild-weg").onclick = function () { neuesBildSetzen(null); };
$("n-problem").addEventListener("paste", function (ev) {
  var liste = (ev.clipboardData || {}).items || [];
  for (var i = 0; i < liste.length; i++) {
    if (liste[i].kind === "file" && /^image\//.test(liste[i].type)) {
      var d = liste[i].getAsFile();
      if (d) { ev.preventDefault(); neuesBildSetzen(d); setzeMeldung($("n-meldung"), "Bild aus der Zwischenablage angehängt.", false); }
      return;
    }
  }
});

$("neu-form").onsubmit = async function (ev) {
  ev.preventDefault();
  var m = $("n-meldung");
  try {
    var e = pruefeEintrag({
      standort: $("n-standort").value, benutzer: $("n-benutzer").value,
      wunschtermin: $("n-termin").value, problem: $("n-problem").value
    });
    if (e.wunschtermin === TICKET) throw new Error("Bitte per E-Mail oder Hotline melden.");
    stand = await api("/api/anfragen", "POST", e);
    if (stand.neu && stand.neu.id && stand.neu.schluessel) {
      var o = eigene();
      o[stand.neu.id] = stand.neu.schluessel;
      eigeneSpeichern(o);
    }
    var neueId = (stand.neu || {}).id || "", bild = neuesBild;
    eigeneAufraeumen();
    anzeigen();
    merken("benutzer", e.benutzer);
    merken("standort", e.standort);
    standortVonHand = false;
    $("n-problem").value = "";
    neuesBildSetzen(null);
    setzeMeldung(m, "Gespeichert.", false);
    $("n-problem").focus();
    if (bild && neueId) {
      setzeMeldung(m, "Gespeichert. Bild wird hochgeladen …", false);
      try {
        await bildSenden(neueId, bild);
        setzeMeldung(m, "Gespeichert, mit Bild.", false);
      } catch (y) {
        setzeMeldung(m, "Das Thema ist gespeichert, das Bild aber nicht: " + y.message, true);
      }
    }
  } catch (x) {
    setzeMeldung(m, x.message, true);
  }
};

/* ---------- Filter, Reiter, Info ---------- */

document.querySelectorAll("#segment button").forEach(function (b, i, alle) {
  b.onclick = function () {
    filterStatus = b.getAttribute("data-status");
    alle.forEach(function (x) { x.classList.toggle("aktiv", x === b); });
    tabelleZeichnen();
  };
});
$("n-termin").onchange = ticketHinweis;
// Benutzer gewählt: Standort vom letzten Mal vorschlagen (bis der Standort von Hand geändert wird)
$("n-benutzer").addEventListener("input", standortVorschlagen);
$("n-benutzer").addEventListener("change", standortVorschlagen);
$("n-standort").addEventListener("change", function () { standortVonHand = true; });
["n-standort", "n-benutzer", "n-termin", "n-problem"].forEach(function (id) {
  ["input", "change"].forEach(function (art) { $(id).addEventListener(art, function () { setzeMeldung($("n-meldung"), "", false); }); });
});
$("f-termin").onchange = tabelleZeichnen;
$("f-standort").onchange = tabelleZeichnen;
$("f-suche").oninput = tabelleZeichnen;
$("drucken").onclick = function () { window.print(); };

function reiter(name) {
  seite = name;
  ["themen", "bericht", "verwaltung"].forEach(function (n) {
    $("reiter-" + n).classList.toggle("aktiv", n === name);
    $("seite-" + n).hidden = n !== name;
  });
  document.body.classList.toggle("druck-bericht", name === "bericht");
  if (name === "verwaltung") aussehenZeichnen(true);
  if (name === "bericht") {
    if (!$("r-datum").value) $("r-datum").value = heute();
    berichtZeichnen();
  }
}
$("reiter-themen").onclick = function () { reiter("themen"); };
$("reiter-bericht").onclick = function () { reiter("bericht"); };
$("reiter-verwaltung").onclick = function () { reiter("verwaltung"); };

/* ---------- Tagesbericht ---------- */

// Was wurde am gewählten Tag erledigt, was ist noch offen (Grundlage für Anzeige, CSV und Text)
function berichtDaten() {
  var tag = $("r-datum").value || heute(), h = heute();
  var nachStandort = function (a, b) { return a.standort.localeCompare(b.standort, "de") || a.benutzer.localeCompare(b.benutzer, "de"); };
  var erledigt = daten().anfragen.filter(function (a) { return a.erledigtAm === tag; }).sort(nachStandort);
  var offen = daten().anfragen.filter(function (a) { return !a.erledigtAm; }).sort(function (a, b) {
    return sortiereTermin(a.wunschtermin, b.wunschtermin) || nachStandort(a, b);
  });
  var neu = daten().anfragen.filter(function (a) { return (a.erfasst || "").substring(0, 10) === tag; }).length;
  var vorbei = offen.filter(function (a) { return a.wunschtermin !== TICKET && a.wunschtermin < h; }).length;
  var a = stand.aussehen || {};
  return {
    tag: tag, erledigt: erledigt, offen: offen, neu: neu, vorbei: vorbei,
    titel: "Tagesbericht " + (a.titel || "IT-Themensammlung"),
    kunde: a.kunde || "",
    erstellt: new Date().toLocaleString("de-DE", { dateStyle: "short", timeStyle: "short" }) + (stand.admin ? " von " + stand.admin : "")
  };
}

function berichtZeichnen() {
  var b = berichtDaten();
  $("b-titel").textContent = b.titel;
  $("b-unter").textContent = (b.kunde ? b.kunde + " · " : "") + datumLang(b.tag);
  var z = $("b-zahlen");
  leeren(z);
  [[b.erledigt.length, "erledigt an diesem Tag"], [b.offen.length, "noch offen"], [b.vorbei, "offen, Termin vorbei"], [b.neu, "neu eingetragen"]].forEach(function (x) {
    var d = document.createElement("div");
    d.appendChild(neuesEl("strong", "", String(x[0])));
    d.appendChild(neuesEl("span", "", x[1]));
    z.appendChild(d);
  });

  var tb = $("b-erledigt");
  leeren(tb);
  b.erledigt.forEach(function (a) {
    var r = tb.insertRow(-1);
    r.insertCell(-1).textContent = a.standort;
    r.insertCell(-1).textContent = a.benutzer;
    r.insertCell(-1).appendChild(neuesEl("div", "text", a.problem));
    r.insertCell(-1).appendChild(neuesEl("div", "text", a.notiz || "–"));
    r.insertCell(-1).textContent = a.erledigtVon || "";
  });
  if (!b.erledigt.length) {
    var c = tb.insertRow(-1).insertCell(-1);
    c.colSpan = 5; c.className = "leer"; c.textContent = "An diesem Tag wurde nichts als erledigt markiert.";
  }

  var to = $("b-offen");
  leeren(to);
  b.offen.forEach(function (a) {
    var r = to.insertRow(-1);
    r.insertCell(-1).textContent = a.standort;
    r.insertCell(-1).textContent = a.benutzer;
    var t = r.insertCell(-1);
    t.textContent = terminText(a.wunschtermin);
    if (a.wunschtermin !== TICKET && a.wunschtermin < heute()) {
      t.appendChild(document.createElement("br"));
      t.appendChild(neuesEl("span", "marke-klein spaet", "Termin vorbei"));
    }
    var pz = r.insertCell(-1);
    pz.appendChild(neuesEl("div", "text", a.problem));
    standZeichnen(pz, a);
  });
  if (!b.offen.length) {
    var c2 = to.insertRow(-1).insertCell(-1);
    c2.colSpan = 4; c2.className = "leer"; c2.textContent = "Keine offenen Themen.";
  }
  $("b-fuss").textContent = "Erstellt " + b.erstellt + " · " + ((stand.programm || {}).name || "reitzner IT-Themensammler");
}

function csvFeld(v) {
  v = String(v == null ? "" : v);
  return /[";\r\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v;
}

function herunterladen(name, inhalt, typ) {
  var url = URL.createObjectURL(new Blob([inhalt], { type: typ }));
  var link = document.createElement("a");
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
}

$("r-datum").onchange = berichtZeichnen;
$("r-heute").onclick = function () { $("r-datum").value = heute(); berichtZeichnen(); };
$("r-drucken").onclick = function () { berichtZeichnen(); window.print(); };

// CSV für Excel: Semikolon, UTF-8 mit BOM (Umlaute), eine Zeile pro Thema
$("r-csv").onclick = function () {
  var b = berichtDaten(), zeilen = [["Bereich", "Standort", "Benutzer", "Wunschtermin", "Problem", "Was wurde gemacht", "Erledigt am", "Erledigt von", "Stand", "Hinweis"]];
  b.erledigt.forEach(function (a) { zeilen.push(["Erledigt", a.standort, a.benutzer, terminText(a.wunschtermin), a.problem, a.notiz, datumKurz(a.erledigtAm), a.erledigtVon, "", ""]); });
  b.offen.forEach(function (a) { zeilen.push(["Offen", a.standort, a.benutzer, terminText(a.wunschtermin), a.problem, "", "", "", STATUS_TEXT[a.status] || "", a.hinweis || ""]); });
  var text = "\ufeff" + zeilen.map(function (z) { return z.map(csvFeld).join(";"); }).join("\r\n") + "\r\n";
  var kd = b.kunde.replace(/[\\/:*?"<>|\s]+/g, "-").replace(/^-+|-+$/g, "");
  herunterladen("Tagesbericht-" + (kd ? kd + "-" : "") + b.tag + ".csv", text, "text/csv;charset=utf-8");
  setzeMeldung($("r-meldung"), "CSV-Datei heruntergeladen.", false);
};

// Als Text kopieren (z. B. in eine E-Mail an den Kunden). Über HTTP gibt es keine Zwischenablage-Schnittstelle,
// deshalb der Weg über ein unsichtbares Textfeld.
$("r-kopieren").onclick = function () {
  var b = berichtDaten(), z = [];
  z.push(b.titel + (b.kunde ? " – " + b.kunde : ""));
  z.push(datumLang(b.tag));
  z.push("");
  z.push("Erledigt (" + b.erledigt.length + "):");
  if (!b.erledigt.length) z.push("- nichts");
  b.erledigt.forEach(function (a) {
    z.push("- " + a.standort + ", " + a.benutzer + ": " + a.problem.replace(/\s*\n\s*/g, " ") + (a.notiz ? "\n  → " + a.notiz.replace(/\s*\n\s*/g, " ") : ""));
  });
  z.push("");
  z.push("Noch offen (" + b.offen.length + "):");
  if (!b.offen.length) z.push("- nichts");
  b.offen.forEach(function (a) {
    z.push("- " + a.standort + ", " + a.benutzer + " (" + terminText(a.wunschtermin) + "): " + a.problem.replace(/\s*\n\s*/g, " ")
      + (STATUS_TEXT[a.status] || a.hinweis ? "\n  [" + (STATUS_TEXT[a.status] || "Hinweis") + (a.hinweis ? ": " + a.hinweis.replace(/\s*\n\s*/g, " ") : "") + "]" : ""));
  });
  var feld = document.createElement("textarea");
  feld.value = z.join("\n");
  feld.setAttribute("readonly", "");
  feld.className = "kopierfeld";
  document.body.appendChild(feld);
  feld.select();
  var ok = false;
  try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
  document.body.removeChild(feld);
  setzeMeldung($("r-meldung"), ok ? "Text kopiert – z. B. in eine E-Mail einfügen." : "Kopieren nicht möglich.", !ok);
};

$("version-knopf").onclick = function () {
  $("info-schema").textContent = (matchMedia("(prefers-color-scheme: dark)").matches ? "dunkel" : "hell") + " (folgt Windows)";
  dialogOeffnen("info");
};
$("info").addEventListener("click", function (ev) { if (ev.target === this) this.close(); });

/* ---------- Start ---------- */

var jahr = String(new Date().getFullYear());
$("jahr").textContent = jahr;
$("info-jahr").textContent = jahr;
$("n-benutzer").value = gemerkt("benutzer");
$("domaene-knopf").onclick = async function () {
  setzeMeldung($("domaene-meldung"), "", false);
  domaeneVersuch = Date.now();
  if (!(await domaeneAnmelden())) setzeMeldung($("domaene-meldung"), "Die Anmeldung mit dem Windows-Konto hat nicht geklappt.", true);
};
// Startseite per Adresse wählbar (z. B. …/#verwaltung); Bericht und Verwaltung nur für Admins
neuLaden().then(function () {
  var start = location.hash.replace("#", "");
  if (stand.admin && (start === "bericht" || start === "verwaltung")) reiter(start);
  return domaeneAutomatisch();
});
setInterval(function () { if (!dialogOffen() && !document.hidden) neuLaden().then(domaeneAutomatisch); }, 30000);
document.addEventListener("visibilitychange", function () {
  if (!document.hidden && !dialogOffen()) neuLaden();
});