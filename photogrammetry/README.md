# DJI GCP Tagger

A browser app for adding **ground control points (GCPs)** to DJI drone photogrammetry.
You type in each GCP's **Northing, Easting and Elevation**, tag where it appears in your DJI photos, and
export a `gcp_list.txt` for **WebODM / OpenDroneMap** (plus CSVs for Pix4D, Metashape and DJI Terra).

Open `photogrammetry/index.html` (or `https://<user>.github.io/<repo>/photogrammetry/` once GitHub Pages is on).
Your photos never leave the device. Nothing is uploaded.

## Workflow

1. **Setup**: pick the coordinate system your GCPs were surveyed in: UTM (zone, hemisphere, WGS84/NAD83),
   or any other system as a proj4 string (state plane, feet and so on; copy it from epsg.io).
   Enter the ground elevation at the take-off point so the app can predict where each GCP shows up in each photo.
2. **GCPs**: enter name, Northing, Easting, Elevation (one at a time, or paste a list).
3. **Photos**: load the DJI JPGs. The app reads the GPS, altitude, gimbal angles and focal length from the
   EXIF and DJI XMP metadata.
4. **Tag**: for each GCP the app lists the photos that should contain it and shows a blue ring at the predicted spot.
   Zoom in and tap the centre of the target. Tag each GCP in at least 3 photos (5–8 is better).
5. **Export**: download `gcp_list.txt` and upload it to WebODM together with the photos.

The app does the GCP entry and image tagging. The 3D reconstruction (orthophoto, DSM, point cloud) is done
by WebODM / ODM (or Pix4D / Metashape / DJI Terra) using the exported files.

## Notes

* `gcp_list.txt` writes coordinates as `easting northing elevation`, which is what ODM expects. You only ever type N, E, Z.
* The predicted position ignores lens distortion and uses the drone's own GPS, so it is usually a few metres off.
  It is only there to help you find the target. Your tap is what gets exported.
* If no photos are listed for a GCP, the app warns about the usual causes: wrong UTM zone, or Northing/Easting swapped.
* UTM works offline. A custom proj4 system needs the proj4 library, which loads from the jsDelivr CDN.

## Tests

```
node --test photogrammetry/test/*.test.js
```
Tests that cross-check the UTM math against PROJ run when the `proj4` npm package is installed
(`npm i proj4`). Otherwise they are skipped.
