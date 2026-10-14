# Rule 310 Dust Log

A phone web app for the daily dust-control records required by **Maricopa County Air Quality Rule 310**
(Fugitive Dust from Dust-Generating Operations). It has no server: logs stay on the phone.

Open `dust/index.html`, or `https://copadrew.github.io/timebandit/dust/` once GitHub Pages is on.

## What it does

1. **Site setup** (once per job): permit #, expiry, project, permittee, Dust Control Coordinator, and the
   control-measure checklist. **Edit the checklist to match your approved Dust Control Plan.**
2. **Daily log**: work done, weather and wind, timed visual inspections (each item Y / N / NA),
   water applied (time, truck, loads, gallons), suppressant, and problems with their corrective actions.
3. **Checks before signing**: blank items, an N with no comment or fix, water marked but not logged,
   expired permit, high wind.
4. **Sign and lock**: finger signature. After signing, changes can only be added as dated amendments.
5. **Hard copy**: print one log, print a date range as a binder, or print a blank paper form with
   the site's checklist filled in. Also: PDF download, CSV export and JSON backup / restore.

## Notes

* Logs live in the phone browser only. Clearing browser data deletes them. Use **Back up** often.
* The page needs internet when it loads (fonts and the PDF library come from a CDN).
* The default checklist is a starting point, not the rule. Check the current Rule 310 and your permit.

Tests: `node --test dust/test/*.test.js`
