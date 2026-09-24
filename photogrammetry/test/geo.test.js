// Run with:  node --test photogrammetry/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const G = require('../geo.js');

let proj4 = null;
try { proj4 = require('proj4'); } catch (e) { /* optional: only for cross-checks */ }

const close = (a, b, tol, msg) => assert.ok(Math.abs(a - b) <= tol, `${msg || ''} ${a} vs ${b} (tol ${tol})`);

test('UTM zone from longitude', () => {
  assert.strictEqual(G.utmZoneFromLon(-80.5), 17);
  assert.strictEqual(G.utmZoneFromLon(3), 31);
  assert.strictEqual(G.utmZoneFromLon(179.9), 60);
});

test('UTM forward matches known value (WGS84)', () => {
  // Statue of Liberty; reference values from PROJ (EPSG:32618)
  const p = G.latLonToUTM(40.689247, -74.044502, 18, false, 'WGS84');
  if (proj4) {
    const [e, n] = proj4('EPSG:4326', G.utmProj4(18, false, 'WGS84'), [-74.044502, 40.689247]);
    close(p.e, e, 0.01, 'easting'); close(p.n, n, 0.01, 'northing');
  }
  close(p.e, 580735.645, 0.01, 'easting'); close(p.n, 4504700.381, 0.01, 'northing');
});

test('UTM forward matches proj4 across zone, both hemispheres and NAD83', { skip: !proj4 }, () => {
  const cases = [
    [-33.8568, 151.2153, 56, true, 'WGS84'],
    [51.5, -0.12, 30, false, 'WGS84'],
    [64.1, -21.9, 27, false, 'WGS84'],
    [29.95, -90.07, 15, false, 'NAD83'],
    [35.0, -117.0, 11, false, 'NAD83'],
    [-1.0, 36.8, 37, true, 'WGS84']
  ];
  for (const [lat, lon, zone, south, datum] of cases) {
    const p = G.latLonToUTM(lat, lon, zone, south, datum);
    const [e, n] = proj4('EPSG:4326', G.utmProj4(zone, south, datum), [lon, lat]);
    close(p.e, e, 0.02, `E ${lat},${lon}`); close(p.n, n, 0.02, `N ${lat},${lon}`);
  }
});

test('EPSG codes', () => {
  assert.strictEqual(G.utmEpsg(17, false, 'WGS84'), 'EPSG:32617');
  assert.strictEqual(G.utmEpsg(56, true, 'WGS84'), 'EPSG:32756');
  assert.strictEqual(G.utmEpsg(15, false, 'NAD83'), 'EPSG:26915');
  assert.strictEqual(G.utmEpsg(40, false, 'NAD83'), null);
});

test('custom projector through proj4 (US survey feet state plane)', { skip: !proj4 }, () => {
  // NAD83 / Texas South Central (ftUS) — EPSG:2278
  const def = '+proj=lcc +lat_0=27.8333333333333 +lon_0=-99 +lat_1=30.2833333333333 +lat_2=28.3833333333333 +x_0=600000 +y_0=4000000 +datum=NAD83 +units=us-ft +no_defs';
  const pr = G.makeProjector({ crsMode: 'custom', customProj4: def, units: 'usft' }, proj4);
  const p = pr.toGrid(29.76, -95.37);
  assert.ok(p.e > 3e6 && p.e < 4e6, 'easting in feet');
  assert.ok(p.n > 1.3e7 && p.n < 1.4e7, 'northing in feet');
  assert.strictEqual(pr.header, def);
});

const nadir = (extra) => Object.assign({
  e: 500000, n: 4000000, z: 200, yaw: 0, pitch: -90, roll: 0,
  fpx: 3000, cx: 2000, cy: 1500, width: 4000, height: 3000
}, extra);

test('nadir camera, yaw 0: north is up, east is right', () => {
  const cam = nadir();
  const c = G.projectPoint(cam, { e: 500000, n: 4000000, z: 100 });
  close(c.x, 2000, 1e-6); close(c.y, 1500, 1e-6); close(c.depth, 100, 1e-9);
  const north = G.projectPoint(cam, { e: 500000, n: 4000010, z: 100 });
  close(north.x, 2000, 1e-6); close(north.y, 1500 - 300, 1e-6); // 10 m at 100 m depth, f=3000 → 300 px
  const east = G.projectPoint(cam, { e: 500010, n: 4000000, z: 100 });
  close(east.x, 2300, 1e-6); close(east.y, 1500, 1e-6);
  assert.ok(east.inFrame);
});

test('nadir camera, yaw 90 (top of image faces east)', () => {
  const cam = nadir({ yaw: 90 });
  const east = G.projectPoint(cam, { e: 500010, n: 4000000, z: 100 });
  close(east.x, 2000, 1e-6); close(east.y, 1200, 1e-6);
  const south = G.projectPoint(cam, { e: 500000, n: 3999990, z: 100 });
  close(south.x, 2300, 1e-6); close(south.y, 1500, 1e-6);
});

test('oblique camera: point straight along the view axis projects to principal point', () => {
  const cam = nadir({ pitch: -45, yaw: 30 });
  const y = 30 * Math.PI / 180;
  const pt = { e: cam.e + 100 * Math.sin(y), n: cam.n + 100 * Math.cos(y), z: cam.z - 100 };
  const p = G.projectPoint(cam, pt);
  close(p.x, cam.cx, 1e-6); close(p.y, cam.cy, 1e-6); close(p.depth, 100 * Math.SQRT2, 1e-6);
});

test('points behind the camera are not in frame', () => {
  const p = G.projectPoint(nadir(), { e: 500000, n: 4000000, z: 300 });
  assert.strictEqual(p.inFrame, false);
});

test('GCP text parsing: N,E,Z default, header skipped, mixed separators', () => {
  const r2 = G.parseGcpText('Name,Northing,Easting,Elev\nGCP1,4504692.9,580741.6,12.5\nGCP2 4504700 580750 13\n');
  assert.deepStrictEqual(r2.errors, []);
  assert.deepStrictEqual(r2.gcps[0], { name: 'GCP1', n: 4504692.9, e: 580741.6, z: 12.5 });
  assert.deepStrictEqual(r2.gcps[1], { name: 'GCP2', n: 4504700, e: 580750, z: 13 });
  const r3 = G.parseGcpText('A;100;200;3', 'ENZ');
  assert.deepStrictEqual(r3.gcps[0], { name: 'A', e: 100, n: 200, z: 3 });
});

test('ODM gcp_list.txt puts EASTING first', () => {
  const txt = G.buildOdmGcpList('EPSG:32618', [{ name: 'G1', n: 4504692.9, e: 580741.6, z: 12.5 }],
    [{ gcp: 'G1', image: 'DJI_0001.JPG', x: 1234.5, y: 987.25 }, { gcp: 'missing', image: 'x.jpg', x: 1, y: 1 }]);
  assert.strictEqual(txt, 'EPSG:32618\n580741.6000\t4504692.9000\t12.5000\t1234.50\t987.25\tDJI_0001.JPG\tG1\n');
});

// ---- synthetic DJI JPEG ----------------------------------------------------
function buildJpeg() {
  const le = true;
  // TIFF: header(8) + IFD0 (2 entries) + ExifIFD(3 entries) + GPS IFD(6 entries) + data
  const buf = Buffer.alloc(400);
  let o = 0;
  buf.write('II', 0); buf.writeUInt16LE(42, 2); buf.writeUInt32LE(8, 4);
  const ifd0 = 8, exifIfd = 8 + 2 + 2 * 12 + 4, gpsIfd = exifIfd + 2 + 3 * 12 + 4, data = gpsIfd + 2 + 6 * 12 + 4;
  let d = data;
  const entry = (at, i, tag, type, count, value) => {
    const p = at + 2 + i * 12;
    buf.writeUInt16LE(tag, p); buf.writeUInt16LE(type, p + 2); buf.writeUInt32LE(count, p + 4);
    if (typeof value === 'function') value(p + 8); else buf.writeUInt32LE(value, p + 8);
  };
  const rationals = (vals) => { const at = d; vals.forEach(([n, den]) => { buf.writeUInt32LE(n, d); buf.writeUInt32LE(den, d + 4); d += 8; }); return at; };
  buf.writeUInt16LE(2, ifd0);
  entry(ifd0, 0, 0x8769, 4, 1, exifIfd);
  entry(ifd0, 1, 0x8825, 4, 1, gpsIfd);
  buf.writeUInt16LE(3, exifIfd);
  entry(exifIfd, 0, 0x920A, 5, 1, rationals([[1229, 100]]));
  entry(exifIfd, 1, 0xA405, 3, 1, (p) => buf.writeUInt16LE(24, p));
  entry(exifIfd, 2, 0xA002, 4, 1, 5280);
  buf.writeUInt16LE(6, gpsIfd);
  entry(gpsIfd, 0, 1, 2, 2, (p) => buf.write('N\0', p));
  entry(gpsIfd, 1, 2, 5, 3, rationals([[40, 1], [41, 1], [2129, 100]]));
  entry(gpsIfd, 2, 3, 2, 2, (p) => buf.write('W\0', p));
  entry(gpsIfd, 3, 4, 5, 3, rationals([[74, 1], [2, 1], [4021, 100]]));
  entry(gpsIfd, 4, 5, 1, 1, 0);
  entry(gpsIfd, 5, 6, 5, 1, rationals([[125500, 1000]]));
  const tiff = buf.subarray(0, d);
  const app1 = Buffer.concat([Buffer.from('Exif\0\0', 'binary'), tiff]);
  const xmp = Buffer.from('http://ns.adobe.com/xap/1.0/\0<x:xmpmeta><rdf:Description ' +
    'drone-dji:AbsoluteAltitude="+125.50" drone-dji:RelativeAltitude="+100.20" ' +
    'drone-dji:GimbalRollDegree="+0.00" drone-dji:GimbalYawDegree="-12.30" drone-dji:GimbalPitchDegree="-89.90" ' +
    'drone-dji:FlightYawDegree="-11.9"/><drone-dji:CalibratedFocalLength>3666.666</drone-dji:CalibratedFocalLength></x:xmpmeta>', 'binary');
  const seg = (m, body) => { const h = Buffer.alloc(4); h[0] = 0xFF; h[1] = m; h.writeUInt16BE(body.length + 2, 2); return Buffer.concat([h, body]); };
  const sof = Buffer.from([8, 0x0E, 0xD6, 0x14, 0xA0, 3]); // 3798 x 5280
  return Buffer.concat([Buffer.from([0xFF, 0xD8]), seg(0xE1, app1), seg(0xE1, xmp), seg(0xC0, sof), Buffer.from([0xFF, 0xDA, 0, 2])]);
}

test('parses EXIF GPS, SOF size and DJI XMP from a JPEG', () => {
  const b = buildJpeg();
  const meta = G.parseJpegMeta(b.buffer.slice(b.byteOffset, b.byteOffset + b.length));
  close(meta.lat, 40 + 41 / 60 + 21.29 / 3600, 1e-9);
  close(meta.lon, -(74 + 2 / 60 + 40.21 / 3600), 1e-9);
  close(meta.gpsAlt, 125.5, 1e-9);
  close(meta.focalLength, 12.29, 1e-9);
  assert.strictEqual(meta.focal35, 24);
  assert.strictEqual(meta.width, 5280);
  assert.strictEqual(meta.height, 3798);
  assert.deepStrictEqual(meta.dji, {
    AbsoluteAltitude: 125.5, RelativeAltitude: 100.2, GimbalRollDegree: 0, GimbalYawDegree: -12.3,
    GimbalPitchDegree: -89.9, FlightYawDegree: -11.9, CalibratedFocalLength: 3666.666
  });
});

test('buildCamera: altitude modes, units and focal length', () => {
  const b = buildJpeg();
  const meta = G.parseJpegMeta(b.buffer.slice(b.byteOffset, b.byteOffset + b.length));
  const pr = G.makeProjector({ crsMode: 'utm', utmZone: 18, utmSouth: false, datum: 'WGS84' });
  const camAbs = G.buildCamera(meta, pr, { altMode: 'absolute', altOffset: -30 });
  close(camAbs.z, 95.5, 1e-9);
  const camRel = G.buildCamera(meta, pr, { altMode: 'relative', takeoffElev: 3.1 });
  close(camRel.z, 103.3, 1e-9);
  close(camRel.fpx, 3666.666, 1e-9);
  assert.strictEqual(camRel.cx, 2640);
  // convergence in zone 18 at -74.04 is small but non-zero
  assert.ok(Math.abs(camRel.convergence) < 3 && camRel.convergence !== 0);

  // Photo taken directly above a GCP projects that GCP to the principal point
  const g = { e: camRel.e, n: camRel.n, z: 3.1 };
  const p = G.projectPoint(camRel, g);
  assert.ok(p.inFrame);
  close(p.x, camRel.cx, 7); close(p.y, camRel.cy, 7); // 0.1° pitch tilt → a few px
});

test('focal length fallback from 35mm equivalent', () => {
  const f = G.focalPixels({ width: 5472, height: 3648, focal35: 24, dji: {} });
  close(f.fpx, 24 * Math.hypot(5472, 3648) / Math.hypot(36, 24), 1e-9);
});
