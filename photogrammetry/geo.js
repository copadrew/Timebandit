/*
 * DJI GCP Tagger — core geometry + metadata helpers.
 * Works in the browser (window.DJIGeo) and in Node (module.exports) so it can be unit-tested.
 */
(function (root) {
  'use strict';

  var DEG = Math.PI / 180;

  // ---------------------------------------------------------------- units
  var UNIT_TO_M = { m: 1, usft: 1200 / 3937, ft: 0.3048 };

  // ---------------------------------------------------------------- UTM
  var ELLIPSOIDS = {
    WGS84: { a: 6378137, f: 1 / 298.257223563 },
    NAD83: { a: 6378137, f: 1 / 298.257222101 } // GRS80
  };

  function utmZoneFromLon(lon) {
    return Math.min(60, Math.max(1, Math.floor((lon + 180) / 6) + 1));
  }

  // Snyder, "Map Projections — A Working Manual" (USGS PP 1395), eqs. 8-9..8-15.
  function latLonToUTM(lat, lon, zone, south, datum) {
    var el = ELLIPSOIDS[datum] || ELLIPSOIDS.WGS84;
    var a = el.a, f = el.f, k0 = 0.9996;
    var e2 = f * (2 - f), e4 = e2 * e2, e6 = e4 * e2, ep2 = e2 / (1 - e2);
    var phi = lat * DEG;
    var lam0 = ((zone - 1) * 6 - 180 + 3) * DEG;
    var sin = Math.sin(phi), cos = Math.cos(phi), tan = Math.tan(phi);
    var N = a / Math.sqrt(1 - e2 * sin * sin);
    var T = tan * tan, C = ep2 * cos * cos, A = cos * (lon * DEG - lam0);
    var M = a * ((1 - e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
      - (3 * e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.sin(2 * phi)
      + (15 * e4 / 256 + 45 * e6 / 1024) * Math.sin(4 * phi)
      - (35 * e6 / 3072) * Math.sin(6 * phi));
    var A2 = A * A, A3 = A2 * A, A4 = A3 * A, A5 = A4 * A, A6 = A5 * A;
    var easting = k0 * N * (A + (1 - T + C) * A3 / 6
      + (5 - 18 * T + T * T + 72 * C - 58 * ep2) * A5 / 120) + 500000;
    var northing = k0 * (M + N * tan * (A2 / 2 + (5 - T + 9 * C + 4 * C * C) * A4 / 24
      + (61 - 58 * T + T * T + 600 * C - 330 * ep2) * A6 / 720));
    if (south) northing += 10000000;
    return { e: easting, n: northing };
  }

  function utmEpsg(zone, south, datum) {
    if (datum === 'NAD83') return (zone >= 1 && zone <= 23 && !south) ? 'EPSG:' + (26900 + zone) : null;
    return 'EPSG:' + ((south ? 32700 : 32600) + zone);
  }

  function utmProj4(zone, south, datum) {
    return '+proj=utm +zone=' + zone + (south ? ' +south' : '') +
      ' +datum=' + (datum === 'NAD83' ? 'NAD83' : 'WGS84') + ' +units=m +no_defs';
  }

  /*
   * settings: { crsMode: 'utm'|'custom', utmZone, utmSouth, datum, customProj4, units }
   * Returns { toGrid(lat, lon) -> {e, n}, header, units } where e/n are in `units`.
   */
  function makeProjector(settings, proj4lib) {
    if (settings.crsMode === 'custom') {
      if (!proj4lib) throw new Error('proj4 library not loaded — needed for a custom coordinate system.');
      var def = (settings.customProj4 || '').trim();
      if (!def) throw new Error('Enter a proj4 string or EPSG code for the custom coordinate system.');
      var conv = proj4lib('EPSG:4326', def);
      return {
        toGrid: function (lat, lon) { var p = conv.forward([lon, lat]); return { e: p[0], n: p[1] }; },
        header: def,
        units: settings.units || 'm'
      };
    }
    var zone = parseInt(settings.utmZone, 10);
    if (!(zone >= 1 && zone <= 60)) throw new Error('UTM zone must be 1–60.');
    var south = !!settings.utmSouth, datum = settings.datum || 'WGS84';
    return {
      toGrid: function (lat, lon) { return latLonToUTM(lat, lon, zone, south, datum); },
      header: utmEpsg(zone, south, datum) || utmProj4(zone, south, datum),
      units: 'm'
    };
  }

  // ---------------------------------------------------------------- JPEG / EXIF / XMP
  function readAscii(dv, off, len) {
    var s = '';
    for (var i = 0; i < len; i++) {
      var c = dv.getUint8(off + i);
      if (c === 0) break;
      s += String.fromCharCode(c);
    }
    return s.trim();
  }

  var TYPE_SIZE = { 1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 7: 1, 9: 4, 10: 8 };

  function readIFD(dv, tiff, ifdOff, le) {
    var out = {};
    if (ifdOff <= 0 || tiff + ifdOff + 2 > dv.byteLength) return out;
    var count = dv.getUint16(tiff + ifdOff, le);
    for (var i = 0; i < count; i++) {
      var ent = tiff + ifdOff + 2 + i * 12;
      if (ent + 12 > dv.byteLength) break;
      var tag = dv.getUint16(ent, le), type = dv.getUint16(ent + 2, le), n = dv.getUint32(ent + 4, le);
      var size = (TYPE_SIZE[type] || 1) * n;
      var valOff = size <= 4 ? ent + 8 : tiff + dv.getUint32(ent + 8, le);
      if (valOff + size > dv.byteLength) continue;
      var v;
      if (type === 2) v = readAscii(dv, valOff, n);
      else {
        var arr = [];
        for (var k = 0; k < n; k++) {
          if (type === 1 || type === 7) arr.push(dv.getUint8(valOff + k));
          else if (type === 3) arr.push(dv.getUint16(valOff + 2 * k, le));
          else if (type === 4) arr.push(dv.getUint32(valOff + 4 * k, le));
          else if (type === 9) arr.push(dv.getInt32(valOff + 4 * k, le));
          else if (type === 5) arr.push(dv.getUint32(valOff + 8 * k, le) / (dv.getUint32(valOff + 8 * k + 4, le) || 1));
          else if (type === 10) arr.push(dv.getInt32(valOff + 8 * k, le) / (dv.getInt32(valOff + 8 * k + 4, le) || 1));
        }
        v = n === 1 ? arr[0] : arr;
      }
      out[tag] = v;
    }
    return out;
  }

  function parseExif(dv, tiff) {
    var le = dv.getUint16(tiff) === 0x4949;
    var ifd0 = readIFD(dv, tiff, dv.getUint32(tiff + 4, le), le);
    var exif = ifd0[0x8769] ? readIFD(dv, tiff, ifd0[0x8769], le) : {};
    var gps = ifd0[0x8825] ? readIFD(dv, tiff, ifd0[0x8825], le) : {};
    var r = { make: ifd0[0x010F], model: ifd0[0x0110] };
    r.focalLength = exif[0x920A];
    r.focal35 = exif[0xA405];
    r.exifWidth = exif[0xA002];
    r.exifHeight = exif[0xA003];
    r.dateTime = exif[0x9003] || ifd0[0x0132];
    function dms(x) { return Array.isArray(x) ? x[0] + (x[1] || 0) / 60 + (x[2] || 0) / 3600 : x; }
    if (gps[2] != null && gps[4] != null) {
      r.lat = dms(gps[2]) * (gps[1] === 'S' ? -1 : 1);
      r.lon = dms(gps[4]) * (gps[3] === 'W' ? -1 : 1);
    }
    if (gps[6] != null) r.gpsAlt = gps[6] * (gps[5] === 1 ? -1 : 1);
    return r;
  }

  var DJI_KEYS = ['AbsoluteAltitude', 'RelativeAltitude', 'GimbalRollDegree', 'GimbalYawDegree',
    'GimbalPitchDegree', 'FlightRollDegree', 'FlightYawDegree', 'FlightPitchDegree',
    'CalibratedFocalLength', 'CalibratedOpticalCenterX', 'CalibratedOpticalCenterY',
    'GpsLatitude', 'GpsLongitude', 'GpsLongtitude', 'RtkFlag', 'RtkStdLon', 'RtkStdLat', 'RtkStdHgt'];

  function parseDjiXmp(text) {
    var out = {};
    DJI_KEYS.forEach(function (k) {
      var re = new RegExp('drone-dji:' + k + '\\s*=\\s*"([^"]*)"|<drone-dji:' + k + '>([^<]*)<');
      var m = re.exec(text);
      if (m) {
        var v = parseFloat(m[1] != null ? m[1] : m[2]);
        if (!isNaN(v)) out[k] = v;
      }
    });
    if (out.GpsLongitude == null && out.GpsLongtitude != null) out.GpsLongitude = out.GpsLongtitude; // DJI typo on some models
    delete out.GpsLongtitude;
    return out;
  }

  /* Parse the head of a JPEG (first ~1 MB is plenty for DJI files). */
  function parseJpegMeta(buffer) {
    var dv = new DataView(buffer);
    if (dv.byteLength < 4 || dv.getUint16(0) !== 0xFFD8) throw new Error('Not a JPEG file');
    var meta = {}, xmpText = '', off = 2;
    while (off + 4 <= dv.byteLength) {
      if (dv.getUint8(off) !== 0xFF) { off++; continue; }
      var marker = dv.getUint8(off + 1);
      if (marker === 0xFF) { off++; continue; }
      if (marker === 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker === 0x01) { off += 2; continue; }
      if (marker === 0xD9 || marker === 0xDA) break;
      var len = dv.getUint16(off + 2), seg = off + 4;
      if (marker === 0xE1 && seg + 6 <= dv.byteLength) {
        var hdr = readAscii(dv, seg, 6);
        if (hdr === 'Exif' && !meta._exif) {
          Object.assign(meta, parseExif(dv, seg + 6));
          meta._exif = true;
        } else {
          var end = Math.min(dv.byteLength, seg + len - 2), s = '';
          for (var i = seg; i < end; i += 4096) {
            s += String.fromCharCode.apply(null, new Uint8Array(buffer, i, Math.min(4096, end - i)));
          }
          if (s.indexOf('http://ns.adobe.com/xap/1.0/') === 0 || s.indexOf('<x:xmpmeta') >= 0) xmpText += s;
        }
      } else if (marker >= 0xC0 && marker <= 0xCF && marker !== 0xC4 && marker !== 0xC8 && marker !== 0xCC) {
        if (seg + 5 <= dv.byteLength) { meta.height = dv.getUint16(seg + 1); meta.width = dv.getUint16(seg + 3); }
      }
      off = seg + len - 2;
    }
    delete meta._exif;
    if (!meta.width) { meta.width = meta.exifWidth; meta.height = meta.exifHeight; }
    meta.dji = parseDjiXmp(xmpText);
    if (meta.lat == null && meta.dji.GpsLatitude != null) {
      meta.lat = meta.dji.GpsLatitude; meta.lon = meta.dji.GpsLongitude;
    }
    return meta;
  }

  // ---------------------------------------------------------------- camera model
  /*
   * Returns the focal length in pixels. Order of preference:
   *  1. DJI CalibratedFocalLength (already in px, Enterprise/Mavic 3E etc.)
   *  2. 35 mm-equivalent focal length scaled by the image diagonal
   *  3. 24 mm-equivalent fallback (most DJI mapping cameras)
   */
  function focalPixels(meta) {
    var w = meta.width, h = meta.height;
    if (meta.dji && meta.dji.CalibratedFocalLength > 0) return { fpx: meta.dji.CalibratedFocalLength, source: 'DJI calibrated' };
    var diag = Math.sqrt(w * w + h * h), diag35 = Math.sqrt(36 * 36 + 24 * 24);
    if (meta.focal35 > 0) return { fpx: meta.focal35 * diag / diag35, source: '35mm equivalent' };
    return { fpx: 24 * diag / diag35, source: 'assumed 24mm equiv.' };
  }

  /*
   * altSettings: { altMode: 'absolute'|'relative', takeoffElev, altOffset } — takeoffElev/altOffset in `units`.
   */
  function buildCamera(meta, projector, altSettings) {
    if (meta.lat == null || meta.lon == null) throw new Error('Photo has no GPS position');
    if (!meta.width || !meta.height) throw new Error('Could not read image size');
    var units = projector.units || 'm', mToU = 1 / UNIT_TO_M[units];
    var dji = meta.dji || {};
    var p = projector.toGrid(meta.lat, meta.lon);
    // Grid convergence: azimuth of true north in the grid, so DJI (true-north) yaw can be turned into grid yaw.
    var pn = projector.toGrid(meta.lat + 0.001, meta.lon);
    var gamma = Math.atan2(pn.e - p.e, pn.n - p.n) / DEG;

    var z, zSource;
    var mode = (altSettings && altSettings.altMode) || 'absolute';
    if (mode === 'relative' && dji.RelativeAltitude != null) {
      z = (+altSettings.takeoffElev || 0) + dji.RelativeAltitude * mToU;
      zSource = 'takeoff elev + relative alt';
    } else {
      var absM = dji.AbsoluteAltitude != null ? dji.AbsoluteAltitude : meta.gpsAlt;
      if (absM == null) throw new Error('Photo has no altitude');
      z = absM * mToU;
      zSource = dji.AbsoluteAltitude != null ? 'DJI absolute alt' : 'EXIF GPS alt';
    }
    z += +(altSettings && altSettings.altOffset) || 0;

    var yaw = dji.GimbalYawDegree != null ? dji.GimbalYawDegree : (dji.FlightYawDegree || 0);
    var pitch = dji.GimbalPitchDegree != null ? dji.GimbalPitchDegree : -90;
    var roll = dji.GimbalRollDegree || 0;
    var f = focalPixels(meta);
    return {
      e: p.e, n: p.n, z: z, zSource: zSource,
      yawTrue: yaw, yaw: yaw + gamma, pitch: pitch, roll: roll, convergence: gamma,
      fpx: f.fpx, focalSource: f.source,
      cx: dji.CalibratedOpticalCenterX > 0 ? dji.CalibratedOpticalCenterX : meta.width / 2,
      cy: dji.CalibratedOpticalCenterY > 0 ? dji.CalibratedOpticalCenterY : meta.height / 2,
      width: meta.width, height: meta.height, units: units
    };
  }

  function cameraAxes(cam) {
    var y = cam.yaw * DEG, p = cam.pitch * DEG, r = cam.roll * DEG;
    // World frame: x = East, y = North, z = Up.  Yaw clockwise from (grid) north, pitch negative = looking down.
    var fwd = [Math.sin(y) * Math.cos(p), Math.cos(y) * Math.cos(p), Math.sin(p)];
    var right = [Math.cos(y), -Math.sin(y), 0];
    var down = [ // fwd × right
      fwd[1] * right[2] - fwd[2] * right[1],
      fwd[2] * right[0] - fwd[0] * right[2],
      fwd[0] * right[1] - fwd[1] * right[0]
    ];
    if (r) {
      var c = Math.cos(r), s = Math.sin(r), r2 = [], d2 = [];
      for (var i = 0; i < 3; i++) { r2[i] = right[i] * c + down[i] * s; d2[i] = down[i] * c - right[i] * s; }
      right = r2; down = d2;
    }
    return { fwd: fwd, right: right, down: down };
  }

  /* Project a ground point {e, n, z} (grid units) into pixel coordinates of a camera. Ignores lens distortion. */
  function projectPoint(cam, pt) {
    var ax = cameraAxes(cam);
    var v = [pt.e - cam.e, pt.n - cam.n, pt.z - cam.z];
    var dot = function (a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; };
    var depth = dot(v, ax.fwd);
    if (depth <= 0) return { x: NaN, y: NaN, depth: depth, inFrame: false };
    var x = cam.cx + cam.fpx * dot(v, ax.right) / depth;
    var y = cam.cy + cam.fpx * dot(v, ax.down) / depth;
    return { x: x, y: y, depth: depth, inFrame: x >= 0 && y >= 0 && x < cam.width && y < cam.height };
  }

  /* Ground sample distance in grid units per pixel at the given depth. */
  function gsd(cam, depth) { return depth / cam.fpx; }

  // ---------------------------------------------------------------- GCP I/O
  function parseNumber(s) {
    if (s == null) return NaN;
    var t = String(s).trim().replace(/[\s']/g, '');
    if (/^-?\d{1,3}(,\d{3})+(\.\d+)?$/.test(t)) t = t.replace(/,/g, ''); // 1,234,567.89
    return t === '' ? NaN : Number(t);
  }

  /*
   * Parse GCP text. Each non-empty line: name, northing, easting, elevation  (comma / tab / semicolon / space separated).
   * `order` may be 'NEZ' (default) or 'ENZ'. A header row is skipped automatically.
   */
  function parseGcpText(text, order) {
    order = order || 'NEZ';
    var gcps = [], errors = [];
    text.split(/\r?\n/).forEach(function (line, i) {
      var t = line.trim();
      if (!t || t[0] === '#') return;
      var parts = t.split(/\s*[,;\t]\s*|\s+/).filter(function (x) { return x !== ''; });
      if (parts.length < 4) { errors.push('Line ' + (i + 1) + ': need name, northing, easting, elevation'); return; }
      var a = parseNumber(parts[1]), b = parseNumber(parts[2]), z = parseNumber(parts[3]);
      if ([a, b, z].some(isNaN)) {
        if (gcps.length === 0 && errors.length === 0) return; // header row
        errors.push('Line ' + (i + 1) + ': coordinates are not numbers');
        return;
      }
      gcps.push(order === 'ENZ' ? { name: parts[0], e: a, n: b, z: z } : { name: parts[0], n: a, e: b, z: z });
    });
    return { gcps: gcps, errors: errors };
  }

  function fmt(x, d) { return Number(x).toFixed(d); }

  /*
   * OpenDroneMap / WebODM gcp_list.txt
   * Line 1: coordinate system. Then: geo_x geo_y geo_z im_x im_y image_name gcp_name   (geo_x = EASTING)
   */
  function buildOdmGcpList(header, gcps, marks) {
    var byName = {};
    gcps.forEach(function (g) { byName[g.name] = g; });
    var lines = [header];
    marks.forEach(function (m) {
      var g = byName[m.gcp];
      if (!g) return;
      lines.push([fmt(g.e, 4), fmt(g.n, 4), fmt(g.z, 4), fmt(m.x, 2), fmt(m.y, 2), m.image, g.name].join('\t'));
    });
    return lines.join('\n') + '\n';
  }

  /* Plain GCP coordinate list (Pix4D / Metashape / DJI Terra import): label, easting(X), northing(Y), elevation(Z) */
  function buildGcpCsv(gcps) {
    return ['label,easting_x,northing_y,elevation_z'].concat(gcps.map(function (g) {
      return [g.name, fmt(g.e, 4), fmt(g.n, 4), fmt(g.z, 4)].join(',');
    })).join('\n') + '\n';
  }

  /* Image marks: image, gcp, pixel x, pixel y (Pix4D "image marks" style) */
  function buildMarksCsv(marks) {
    return ['image,gcp,x_px,y_px'].concat(marks.map(function (m) {
      return [m.image, m.gcp, fmt(m.x, 2), fmt(m.y, 2)].join(',');
    })).join('\n') + '\n';
  }

  var api = {
    UNIT_TO_M: UNIT_TO_M, utmZoneFromLon: utmZoneFromLon, latLonToUTM: latLonToUTM, utmEpsg: utmEpsg,
    utmProj4: utmProj4, makeProjector: makeProjector, parseJpegMeta: parseJpegMeta, parseDjiXmp: parseDjiXmp,
    focalPixels: focalPixels, buildCamera: buildCamera, cameraAxes: cameraAxes, projectPoint: projectPoint, gsd: gsd,
    parseNumber: parseNumber, parseGcpText: parseGcpText, buildOdmGcpList: buildOdmGcpList,
    buildGcpCsv: buildGcpCsv, buildMarksCsv: buildMarksCsv
  };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.DJIGeo = api;
})(typeof self !== 'undefined' ? self : this);
