# Timebandit
Employee time keeper
Confirmed — copadrew exists. Building the poster now.

Step 4 is clipping into the footer. Quick fix.

That’s clean. Delivering both files now.

Two files above:

	•	timebandit_qr_poster.pdf — letter-size, print-ready, pointed at https://copadrew.github.io/timebandit/. High error-correction QR so it still scans through dust and scuffs on the trailer door.
	•	index.html — the app itself.

Three-step deploy from your phone (no terminal needed):

	1.	On github.com, tap + top-right → New repository. Name it exactly timebandit, set Public, check “Add a README”, Create.
	2.	In the new repo, tap Add file → Upload files, upload index.html, scroll down, tap Commit changes.
	3.	Repo → Settings → Pages (left menu). Under “Source,” pick Deploy from a branch, Branch = main, Folder = / (root), Save.

Give it about a minute, then the poster’s QR will scan to a live app. If it 404s the first time, refresh — Pages can take 30–90 seconds on first build.

One honest caveat: the QR is locked to that exact URL. If you ever rename the repo or the username, the printed poster goes dead and we’d need to reprint. So get it deployed and scan-test it from your own phone before printing copies for the crew.