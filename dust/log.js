/*
 * Rule 310 Dust Log — core data + compliance checks.
 * Works in the browser (window.DustLog) and in Node (module.exports) so it can be unit-tested.
 *
 * Built around Maricopa County Air Quality Rule 310 (Fugitive Dust from Dust-Generating
 * Operations), Section 502 daily recordkeeping: a dated log of the control measures actually
 * carried out under the approved Dust Control Plan (DCP), with timed visual checks, each item
 * marked Y / N / NA, and comments / corrective actions.
 * The default checklist is a starting point; edit it to match the site's approved DCP.
 */
(function (root) {
  'use strict';

  // Default control-measure checklist. Each site can edit this to match its approved DCP.
  var DEFAULT_ITEMS = [
    { id: 'prewet',    label: 'Soils pre-watered before disturbance / excavation' },
    { id: 'water',     label: 'Water applied to active disturbed areas during work' },
    { id: 'haulroads', label: 'Unpaved haul / access roads stabilized (wet, gravel or suppressant)' },
    { id: 'staging',   label: 'Unpaved parking / staging areas stabilized' },
    { id: 'inactive',  label: 'Inactive disturbed areas stabilized (crusted, wet or covered)' },
    { id: 'piles',     label: 'Open storage piles stabilized or covered' },
    { id: 'hauling',   label: 'Bulk material loads wet / covered / freeboard kept' },
    { id: 'trackdev',  label: 'Trackout control device in place and working (grizzly, gravel pad, wash)' },
    { id: 'trackout',  label: 'Paved roads checked: trackout cleaned up (none left at end of day)' },
    { id: 'speed',     label: 'Speed limit posted and obeyed on unpaved roads' },
    { id: 'opacity',   label: 'No visible dust plumes over the opacity limit / none leaving site' },
    { id: 'wind',      label: 'High-wind contingency followed if needed (extra water / stop work)' },
    { id: 'permit',    label: 'Dust control permit + approved DCP on site' },
    { id: 'sign',      label: 'Project dust-control sign posted and readable (if required)' }
  ];

  var STATUSES = ['Y', 'N', 'NA'];

  function uid() {
    return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  }

  function newSite(over) {
    var s = {
      id: uid(),
      permitNo: '', permitExpires: '', projectName: '', address: '', acres: '',
      permittee: '', dccName: '', dccCert: '', phone: '',
      items: DEFAULT_ITEMS.map(function (i) { return { id: i.id, label: i.label }; })
    };
    return Object.assign(s, over || {});
  }

  function newLog(site, date, over) {
    var l = {
      id: uid(),
      siteId: site ? site.id : '',
      date: date || '',
      noWork: false,               // true = no dust-generating work today
      preparedBy: '', trainingNo: '',
      activities: '', workStart: '', workEnd: '',
      weather: '', windMph: '', gustMph: '', tempF: '',
      checks: [],                  // timed visual inspections
      water: [],                   // water applications
      suppressant: [],             // dust suppressant applications
      actions: [],                 // problems found + corrective actions
      notes: '',
      signedBy: '', signature: '', signedAt: null,
      amendments: [],
      createdAt: new Date().toISOString()
    };
    return Object.assign(l, over || {});
  }

  function newCheck(site, time, inspector) {
    var st = {};
    (site ? site.items : DEFAULT_ITEMS).forEach(function (i) { st[i.id] = ''; });
    return { id: uid(), time: time || '', inspector: inspector || '', status: st, comments: {} };
  }

  function num(v) {
    var n = parseFloat(v);
    return isFinite(n) ? n : 0;
  }

  function totalWater(log) {
    return (log.water || []).reduce(function (a, w) { return a + num(w.gallons); }, 0);
  }

  function itemLabel(site, id) {
    var it = (site && site.items || DEFAULT_ITEMS).filter(function (i) { return i.id === id; })[0];
    return it ? it.label : id;
  }

  /*
   * Compliance check. Returns { errors: [...], warnings: [...] }.
   * Errors block signing; warnings are shown but do not block.
   */
  function validate(site, log) {
    var errors = [], warnings = [];
    if (!site || !site.permitNo) errors.push('Site has no dust control permit number.');
    if (!log.date) errors.push('Log date is missing.');
    if (!log.preparedBy) errors.push('Who prepared the log is missing.');
    if (site && site.permitExpires && log.date && log.date > site.permitExpires) {
      errors.push('Permit expired on ' + site.permitExpires + '. Renew it before working.');
    }

    if (log.noWork) {
      if (!log.notes) warnings.push('No-work day: add a note (e.g. "rain day, site stable").');
      return { errors: errors, warnings: warnings };
    }

    if (!log.activities) errors.push('Describe the dust-generating work done today.');
    var checks = log.checks || [];
    if (!checks.length) errors.push('No visual inspections logged. Log at least one timed check.');
    if (checks.length === 1) warnings.push('Only one inspection logged. Most DCPs expect checks through the day.');

    var items = site ? site.items : DEFAULT_ITEMS;
    var actions = log.actions || [];
    checks.forEach(function (c, ci) {
      var tag = 'Check ' + (ci + 1) + (c.time ? ' (' + c.time + ')' : '');
      if (!c.time) errors.push(tag + ': time is missing.');
      var blank = items.filter(function (i) { return !c.status[i.id]; });
      if (blank.length) errors.push(tag + ': ' + blank.length + ' item(s) not marked Y / N / NA.');
      items.forEach(function (i) {
        if (c.status[i.id] === 'N') {
          var hasComment = c.comments && c.comments[i.id] && c.comments[i.id].trim();
          var hasAction = actions.some(function (a) { return a.itemId === i.id; });
          if (!hasComment && !hasAction) {
            errors.push(tag + ': "' + i.label + '" marked N. Add a comment or corrective action.');
          }
        }
      });
    });

    actions.forEach(function (a, ai) {
      if (!a.issue) errors.push('Corrective action ' + (ai + 1) + ': describe the problem.');
      if (!a.action) errors.push('Corrective action ' + (ai + 1) + ': describe what was done.');
      if (!a.fixedAt) warnings.push('Corrective action ' + (ai + 1) + ': not marked fixed yet.');
    });

    var waterMarked = checks.some(function (c) { return c.status.water === 'Y' || c.status.prewet === 'Y'; });
    if (waterMarked && !(log.water || []).length) {
      errors.push('Water was marked as applied, but no water application is logged (time + amount).');
    }
    (log.water || []).forEach(function (w, wi) {
      if (!w.time) errors.push('Water entry ' + (wi + 1) + ': time is missing.');
      if (!num(w.gallons)) errors.push('Water entry ' + (wi + 1) + ': gallons is missing.');
    });

    if (log.windMph === '' && log.gustMph === '') warnings.push('Wind speed not recorded.');
    if (num(log.windMph) >= 25 || num(log.gustMph) >= 25) {
      warnings.push('High wind recorded. Note the contingency measures from your DCP in the log.');
    }
    return { errors: errors, warnings: warnings };
  }

  function canSign(site, log) {
    return validate(site, log).errors.length === 0;
  }

  // Lock a log. After signing, changes are only allowed as timestamped amendments.
  function sign(site, log, name, signatureDataUrl, when) {
    if (log.signedAt) throw new Error('Log already signed.');
    if (!name) throw new Error('Signer name required.');
    var v = validate(site, log);
    if (v.errors.length) throw new Error('Log has errors: ' + v.errors[0]);
    log.signedBy = name;
    log.signature = signatureDataUrl || '';
    log.signedAt = (when || new Date()).toISOString();
    return log;
  }

  function amend(log, by, note, when) {
    if (!log.signedAt) throw new Error('Only signed logs need amendments. Edit it directly.');
    if (!by || !note) throw new Error('Amendment needs a name and a note.');
    log.amendments.push({ at: (when || new Date()).toISOString(), by: by, note: note });
    return log;
  }

  // ------------------------------------------------------------- CSV
  function csvCell(x) {
    var s = x == null ? '' : String(x);
    // Guard against spreadsheet formula injection.
    if (/^[=+\-@]/.test(s)) s = "'" + s;
    return '"' + s.replace(/"/g, '""') + '"';
  }

  // One row per (log, check, item): what an inspector would ask for.
  function toCSV(site, logs) {
    var head = ['Permit', 'Project', 'Date', 'No work', 'Prepared by', 'Check time', 'Inspector',
      'Item', 'Status', 'Comment', 'Water total (gal)', 'Wind mph', 'Signed by', 'Signed at'];
    var rows = [head];
    logs.slice().sort(function (a, b) { return a.date < b.date ? -1 : a.date > b.date ? 1 : 0; })
      .forEach(function (l) {
        var base = [site.permitNo, site.projectName, l.date, l.noWork ? 'Y' : '', l.preparedBy];
        var tail = [totalWater(l), l.windMph, l.signedBy, l.signedAt || ''];
        if (l.noWork || !(l.checks || []).length) {
          rows.push(base.concat(['', '', '', '', l.notes || '']).concat(tail));
          return;
        }
        l.checks.forEach(function (c) {
          site.items.forEach(function (i) {
            rows.push(base.concat([c.time, c.inspector, i.label, c.status[i.id] || '',
              (c.comments && c.comments[i.id]) || '']).concat(tail));
          });
        });
      });
    return rows.map(function (r) { return r.map(csvCell).join(','); }).join('\r\n');
  }

  // ------------------------------------------------------------- store
  function emptyStore() { return { version: 1, sites: [], logs: [], currentSiteId: null, lastBackup: null }; }

  function parseBackup(text) {
    var d = JSON.parse(text);
    if (!d || !Array.isArray(d.sites) || !Array.isArray(d.logs)) throw new Error('Not a Rule 310 log backup file.');
    return d;
  }

  // Merge a backup into the store. Never drops existing records; on id clash keep the signed / newer one.
  function mergeStore(store, backup) {
    var out = JSON.parse(JSON.stringify(store));
    function mergeList(key, prefer) {
      var byId = {};
      out[key].forEach(function (x) { byId[x.id] = x; });
      backup[key].forEach(function (x) {
        if (!byId[x.id]) { out[key].push(x); byId[x.id] = x; return; }
        if (prefer(x, byId[x.id])) {
          var idx = out[key].indexOf(byId[x.id]);
          out[key][idx] = x; byId[x.id] = x;
        }
      });
    }
    mergeList('sites', function () { return false; });
    mergeList('logs', function (incoming, existing) {
      if (incoming.signedAt && !existing.signedAt) return true;
      if (incoming.signedAt && existing.signedAt) return (incoming.amendments || []).length > (existing.amendments || []).length;
      return false;
    });
    return out;
  }

  var api = {
    DEFAULT_ITEMS: DEFAULT_ITEMS, STATUSES: STATUSES,
    uid: uid, newSite: newSite, newLog: newLog, newCheck: newCheck,
    totalWater: totalWater, itemLabel: itemLabel,
    validate: validate, canSign: canSign, sign: sign, amend: amend,
    toCSV: toCSV, emptyStore: emptyStore, parseBackup: parseBackup, mergeStore: mergeStore
  };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.DustLog = api;
})(this);
