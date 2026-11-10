# Luma User Journeys

Baseline: v0.6.0
Date: 2026-11-11
Audience: QAE and UXD engineers for test coverage, UI states, and scenario design.

---

## Journey 1: First Launch and Onboarding
- Context: User runs Luma for the first time on a PC.
- Flow:
  1. User starts `Luma.App.exe`.
  2. `%LOCALAPPDATA%\Luma` catalog and thumbnail directories are initialized automatically.
  3. App opens into an empty state explaining: "Your files stay where they are."
  4. Suggested locations (Pictures, Videos, detected removable drives) and a primary call to action: "Add folder or drive".
  5. User can immediately start browsing as soon as files are discovered while background indexing and date reading continue.

---

## Journey 2: Adding a Media Source
- Context: User adds a local folder or external drive.
- Flow:
  1. User clicks "Add folder or drive" or drops a folder onto Luma.
  2. Windows volume resolver captures volume GUID, serial, and relative path.
  3. App begins Step 1 of 2: Finding media (enumerating files, saving checkpoints every 100 files).
  4. Gallery populates immediately with basic file records.
  5. App proceeds to Step 2 of 2: Reading dates (extracting EXIF/metadata in batches of 100).
  6. User can pause, browse, or exit safely at any point.

---

## Journey 3: Chronological Gallery Browsing
- Context: User browses thousands of photos and videos.
- Flow:
  1. Gallery groups media by month and sorts by capture date (falling back to modification date).
  2. Continuous virtualized scrolling loads cursor batches of 120 items without page buttons.
  3. Decoded bitmaps are recycled as items scroll off screen to keep memory bounded.
  4. User adjusts thumbnail size via slider or Ctrl+mouse wheel.
  5. User navigates months directly via Month Browser with item counts.

---

## Journey 4: Discovery, Filtering, and Search
- Context: User searches for specific events, media types, or dates.
- Flow:
  1. User types in search box (filename prefix or tag search).
  2. User selects media type filters (Photos, Videos, Everyday formats, or All formats).
  3. Active filter chips appear in filter bar with counts.
  4. User clicks "Reset" to clear filters and return to full library.

---

## Journey 5: Organization (Tags, Favorites, Albums)
- Context: User curates media into albums, tags, and favorites.
- Flow:
  1. User selects single or multiple media cards using Ctrl+click, Shift+click, or Select mode.
  2. User toggles Favorite star (instant catalog update, visible badge).
  3. User creates virtual album in sidebar and drags or adds selected files.
  4. User edits tags in Details inspector; confirms before replacing tags on multi-selection.
  5. Originals on disk remain untouched.

---

## Journey 6: Offline Disconnection
- Context: User unplugs an external drive while browsing.
- Flow:
  1. Next periodic check (every 30s) or window activation detects drive is offline.
  2. Drive status changes to offline indicator.
  3. Cached thumbnails and metadata remain browsable in the gallery.
  4. File operations (open, copy, rename, delete) are disabled with clear feedback: "Drive is offline".
  5. Catalog records are never deleted on disconnect.

---

## Journey 7: Reconnection and Rehydration
- Context: User plugs the external drive back into the PC (potentially under a different drive letter).
- Flow:
  1. Luma resolves the drive by volume GUID and serial, discovering its new mount letter.
  2. Source indicator updates to online in place.
  3. Loaded cards regain online status without reloading the gallery or losing scroll position.
  4. Missing thumbnails retry generation.
  5. No silent automatic full scan is executed without explicit user Refresh.

---

## Journey 8: Explicit Refresh and Resumable Indexing
- Context: User modified files externally and triggers a library refresh.
- Flow:
  1. User clicks Refresh → "Refresh this source" or "Refresh all connected sources".
  2. Traversal checks existing checkpoints; resumes from interrupted folders if paused.
  3. New and modified files are added; deleted files are reconciled safely after an error-free scan.
  4. Traversal publishes atomically; gallery remains browsable throughout.

---

## Journey 9: Library Exclusions
- Context: User wants to hide build folders, private photos, or temporary files.
- Flow:
  1. User right-clicks a card or opens Settings → "Manage exclusions".
  2. Adds a folder exclusion, source exclusion, or global file-type extension rule.
  3. Excluded items disappear immediately from gallery, search, counts, and future scans.
  4. User reviews active rules in Manage Exclusions dialog and clicks "Restore".
  5. Media reappears instantly with favorites, tags, and album memberships intact.

---

## Journey 10: Storage Maintenance and Cache Cleanup
- Context: User manages local thumbnail disk space.
- Flow:
  1. User opens "Storage & previews".
  2. Views total cache size, thumbnail count, and coverage (e.g. 260 / 260).
  3. Clicks "Prepare previews for offline browsing" to pre-render thumbnails.
  4. If disk space is needed, clicks "Clean unused previews" or "Clear thumbnail cache".
  5. Originals and catalog metadata remain untouched.

---

## Journey 11: Native Windows File Operations
- Context: User renames, copies, or deletes an original file.
- Flow:
  1. User selects item and presses F2 to rename; file is renamed on disk and catalog record updated.
  2. User presses Ctrl+C or chooses "Copy to folder"; file streams safely to temporary sibling and renames.
  3. User presses Delete; confirmation dialog routes through native Windows Recycle Bin.
  4. Double-click launches default Windows handler for the media type.

---

## Journey 12: Pick Up Where You Left Off
- Context: User closes and reopens Luma.
- Flow:
  1. On close, Luma saves the last active source, folder, album, search, month, and first visible item anchor.
  2. On reopen, Luma restores that exact view from the local catalog without scanning the drive.
  3. Displays a subtle notice: "Welcome back. Your last view is restored."
  4. Clicking "Earlier items" loads preceding items without jumping the viewport.
  5. Can be disabled in Settings → "Remember where I left off".

